using Microsoft.Data.Sqlite;

namespace UltraCompress.Core.Recipes;

/// <summary>SQLite-backed recipe store with schema version migrations.</summary>
public sealed class SqliteRecipeStore : IRecipeStore
{
    private const int SchemaVersion = 1;

    private readonly string _databasePath;
    private SqliteConnection? _connection;
    private bool _initialized;

    public SqliteRecipeStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    public static SqliteRecipeStore OpenDefault() => new(RecipeStorePaths.DefaultRecipeDatabasePath());

    public void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        ApplyMigrations();
        _initialized = true;
    }

    public void PinRecipe(
        RecipeBindingKind kind,
        string bindingKey,
        PipelineRecipe recipe,
        string? notes = null)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingKey);
        ArgumentNullException.ThrowIfNull(recipe);

        var normalizedKey = NormalizeBindingKey(kind, bindingKey);
        var now = DateTimeOffset.UtcNow;
        var json = RecipeJson.Serialize(recipe);

        using var command = _connection!.CreateCommand();
        command.CommandText = """
            INSERT INTO recipe_bindings (
                binding_kind, binding_key, size_bytes, last_modified_utc,
                recipe_json, recipe_id, is_pinned, notes, created_utc, updated_utc)
            VALUES ($kind, $key, NULL, NULL, $json, $recipeId, 1, $notes, $created, $updated)
            ON CONFLICT(binding_kind, binding_key) DO UPDATE SET
                recipe_json = excluded.recipe_json,
                recipe_id = excluded.recipe_id,
                size_bytes = NULL,
                last_modified_utc = NULL,
                is_pinned = 1,
                notes = excluded.notes,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$kind", ToKindString(kind));
        command.Parameters.AddWithValue("$key", normalizedKey);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$recipeId", recipe.Id);
        command.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void SaveWinningCache(FileFingerprint fingerprint, PipelineRecipe recipe)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(recipe);

        if (string.IsNullOrWhiteSpace(fingerprint.ContentHashSha256))
        {
            throw new ArgumentException("Content hash is required for recipe cache.", nameof(fingerprint));
        }

        var hash = fingerprint.ContentHashSha256.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var json = RecipeJson.Serialize(recipe);

        using var command = _connection!.CreateCommand();
        command.CommandText = """
            INSERT INTO recipe_bindings (
                binding_kind, binding_key, size_bytes, last_modified_utc,
                recipe_json, recipe_id, is_pinned, notes, created_utc, updated_utc)
            VALUES ('content_hash', $key, $size, $mtime, $json, $recipeId, 0, NULL, $created, $updated)
            ON CONFLICT(binding_kind, binding_key) DO UPDATE SET
                recipe_json = excluded.recipe_json,
                recipe_id = excluded.recipe_id,
                size_bytes = excluded.size_bytes,
                last_modified_utc = excluded.last_modified_utc,
                is_pinned = 0,
                notes = NULL,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$key", hash);
        command.Parameters.AddWithValue("$size", fingerprint.SizeBytes);
        command.Parameters.AddWithValue("$mtime", fingerprint.LastModifiedUtc.ToString("O"));
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$recipeId", recipe.Id);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public bool TryResolveRecipe(FileFingerprint fingerprint, out PipelineRecipe? recipe)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(fingerprint);
        recipe = null;

        if (!string.IsNullOrWhiteSpace(fingerprint.ContentHashSha256)
            && TryGetByKind(
                RecipeBindingKind.ContentHash,
                fingerprint.ContentHashSha256.Trim().ToLowerInvariant(),
                fingerprint,
                out recipe))
        {
            return true;
        }

        var path = RecipePathNormalization.NormalizePath(fingerprint.Path);
        if (TryGetByKind(RecipeBindingKind.Path, path, fingerprint, out recipe))
        {
            return true;
        }

        if (TryGetBestPathGlob(path, fingerprint, out recipe))
        {
            return true;
        }

        var extension = RecipePathNormalization.NormalizeExtension(fingerprint.Path);
        if (!string.IsNullOrEmpty(extension)
            && TryGetByKind(RecipeBindingKind.Extension, extension, fingerprint, out recipe))
        {
            return true;
        }

        recipe = null;
        return false;
    }

    public bool RemoveBinding(RecipeBindingKind kind, string bindingKey)
    {
        EnsureInitialized();
        var normalizedKey = NormalizeBindingKey(kind, bindingKey);

        using var command = _connection!.CreateCommand();
        command.CommandText = """
            DELETE FROM recipe_bindings
            WHERE binding_kind = $kind AND binding_key = $key;
            """;
        command.Parameters.AddWithValue("$kind", ToKindString(kind));
        command.Parameters.AddWithValue("$key", normalizedKey);
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<RecipeBindingRecord> ListBindings(RecipeBindingKind? kindFilter = null)
    {
        EnsureInitialized();

        using var command = _connection!.CreateCommand();
        command.CommandText = kindFilter is null
            ? """
                SELECT id, binding_kind, binding_key, size_bytes, last_modified_utc,
                       recipe_id, is_pinned, notes, created_utc, updated_utc
                FROM recipe_bindings
                ORDER BY binding_kind, binding_key;
                """
            : """
                SELECT id, binding_kind, binding_key, size_bytes, last_modified_utc,
                       recipe_id, is_pinned, notes, created_utc, updated_utc
                FROM recipe_bindings
                WHERE binding_kind = $kind
                ORDER BY binding_key;
                """;

        if (kindFilter is not null)
        {
            command.Parameters.AddWithValue("$kind", ToKindString(kindFilter.Value));
        }

        var results = new List<RecipeBindingRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadBindingRecord(reader));
        }

        return results;
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
        _initialized = false;
    }

    private bool TryGetByKind(
        RecipeBindingKind kind,
        string bindingKey,
        FileFingerprint fingerprint,
        out PipelineRecipe? recipe)
    {
        recipe = null;

        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT recipe_json, is_pinned, size_bytes, last_modified_utc
            FROM recipe_bindings
            WHERE binding_kind = $kind AND binding_key = $key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$kind", ToKindString(kind));
        command.Parameters.AddWithValue("$key", bindingKey);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        var isPinned = reader.GetInt64(1) != 0;
        if (kind == RecipeBindingKind.ContentHash && !isPinned)
        {
            var storedSize = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
            var storedMtime = reader.IsDBNull(3)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(3));

            if (storedSize != fingerprint.SizeBytes || storedMtime != fingerprint.LastModifiedUtc)
            {
                return false;
            }
        }

        var json = reader.GetString(0);
        recipe = RecipeJson.Deserialize(json);
        return true;
    }

    private bool TryGetBestPathGlob(string normalizedPath, FileFingerprint fingerprint, out PipelineRecipe? recipe)
    {
        recipe = null;

        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT binding_key, recipe_json
            FROM recipe_bindings
            WHERE binding_kind = 'path_glob';
            """;

        string? bestKey = null;
        string? bestJson = null;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var pattern = reader.GetString(0);
            if (!RecipeGlobMatcher.IsMatch(pattern, normalizedPath))
            {
                continue;
            }

            if (bestKey is null
                || pattern.Length > bestKey.Length
                || (pattern.Length == bestKey.Length
                    && string.CompareOrdinal(pattern, bestKey) < 0))
            {
                bestKey = pattern;
                bestJson = reader.GetString(1);
            }
        }

        if (bestJson is null)
        {
            return false;
        }

        recipe = RecipeJson.Deserialize(bestJson);
        return true;
    }

    private static RecipeBindingRecord ReadBindingRecord(SqliteDataReader reader)
    {
        var kind = ParseKind(reader.GetString(1));
        return new RecipeBindingRecord
        {
            Id = reader.GetInt64(0),
            Kind = kind,
            BindingKey = reader.GetString(2),
            SizeBytes = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            LastModifiedUtc = reader.IsDBNull(4)
                ? null
                : DateTimeOffset.Parse(reader.GetString(4)),
            RecipeId = reader.GetString(5),
            IsPinned = reader.GetInt64(6) != 0,
            Notes = reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedUtc = DateTimeOffset.Parse(reader.GetString(8)),
            UpdatedUtc = DateTimeOffset.Parse(reader.GetString(9)),
        };
    }

    private void ApplyMigrations()
    {
        var current = GetSchemaVersion();
        if (current >= SchemaVersion)
        {
            return;
        }

        if (current == 0)
        {
            ExecuteNonQuery("""
                CREATE TABLE IF NOT EXISTS schema_version (
                    version INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS recipe_bindings (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    binding_kind TEXT NOT NULL CHECK (binding_kind IN (
                        'content_hash', 'path', 'path_glob', 'extension')),
                    binding_key TEXT NOT NULL,
                    size_bytes INTEGER,
                    last_modified_utc TEXT,
                    recipe_json TEXT NOT NULL,
                    recipe_id TEXT NOT NULL,
                    is_pinned INTEGER NOT NULL DEFAULT 0,
                    notes TEXT,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    UNIQUE(binding_kind, binding_key)
                );

                CREATE INDEX IF NOT EXISTS ix_recipe_bindings_kind_key
                    ON recipe_bindings(binding_kind, binding_key);
                """);

            ExecuteNonQuery("DELETE FROM schema_version;");
            using var insert = _connection!.CreateCommand();
            insert.CommandText = "INSERT INTO schema_version(version) VALUES ($v);";
            insert.Parameters.AddWithValue("$v", SchemaVersion);
            insert.ExecuteNonQuery();
        }
    }

    private int GetSchemaVersion()
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'schema_version';
            """;
        var exists = command.ExecuteScalar() is not null;
        if (!exists)
        {
            return 0;
        }

        command.CommandText = "SELECT version FROM schema_version LIMIT 1;";
        var result = command.ExecuteScalar();
        if (result is null or DBNull)
        {
            return 0;
        }

        return Convert.ToInt32(result);
    }

    private void ExecuteNonQuery(string sql)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string NormalizeBindingKey(RecipeBindingKind kind, string bindingKey) =>
        kind switch
        {
            RecipeBindingKind.ContentHash => bindingKey.Trim().ToLowerInvariant(),
            RecipeBindingKind.Extension => bindingKey.StartsWith('.')
                ? bindingKey.ToLowerInvariant()
                : "." + bindingKey.ToLowerInvariant(),
            RecipeBindingKind.Path => RecipePathNormalization.NormalizePath(bindingKey),
            RecipeBindingKind.PathGlob => bindingKey.Trim(),
            _ => bindingKey.Trim(),
        };

    private static string ToKindString(RecipeBindingKind kind) =>
        kind switch
        {
            RecipeBindingKind.ContentHash => "content_hash",
            RecipeBindingKind.Path => "path",
            RecipeBindingKind.PathGlob => "path_glob",
            RecipeBindingKind.Extension => "extension",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static RecipeBindingKind ParseKind(string value) =>
        value switch
        {
            "content_hash" => RecipeBindingKind.ContentHash,
            "path" => RecipeBindingKind.Path,
            "path_glob" => RecipeBindingKind.PathGlob,
            "extension" => RecipeBindingKind.Extension,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown binding kind."),
        };
}