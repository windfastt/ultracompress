using Microsoft.Data.Sqlite;

namespace UltraCompress.Core.History;

public sealed record CompressionHistoryRecord
{
    public string HistoryId { get; init; } = Guid.NewGuid().ToString("N");

    public string? JobId { get; init; }

    public required string Operation { get; init; }

    public required string ArchivePath { get; init; }

    public required string SourceSummary { get; init; }

    public required string DestinationPath { get; init; }

    public required string Profile { get; init; }

    public int FileCount { get; init; }

    public long OriginalBytes { get; init; }

    public long CompressedBytes { get; init; }

    public long SavedBytes { get; init; }

    public double CompressionRatio { get; init; }

    public int DuplicateCount { get; init; }

    public long DuplicateBytesSaved { get; init; }

    public long DurationMs { get; init; }

    public double ThroughputMbPerSec { get; init; }

    public DateTimeOffset CompletedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CompressionHistorySummary(
    int TotalOperations,
    int TotalPackOperations,
    int TotalExtractOperations,
    long CumulativeOriginalBytesCompressed,
    long CumulativeArchiveBytesProduced,
    long CumulativeSavedBytes,
    long CumulativeDuplicateBytesSaved);

public interface ICompressionHistoryStore : IDisposable
{
    void Record(CompressionHistoryRecord entry);

    IReadOnlyList<CompressionHistoryRecord> ListRecent(int limit = 50, string? operationFilter = null);

    CompressionHistorySummary GetSummary();
}

/// <summary>
/// Thread-safe SQLite store persisting completed compression and extraction operations
/// in <c>jobs.db</c> (or a caller-supplied database path).
/// </summary>
public sealed class SqliteCompressionHistoryStore : ICompressionHistoryStore
{
    private readonly string _databasePath;
    private readonly object _sync = new();
    private bool _schemaEnsured;

    public SqliteCompressionHistoryStore(string? databasePath = null)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? EnginePaths.GetDefaultJobsDatabasePath()
            : Path.GetFullPath(databasePath);
    }

    public string DatabasePath => _databasePath;

    public void EnsureInitialized()
    {
        lock (_sync)
        {
            if (_schemaEnsured)
            {
                return;
            }

            var dir = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;

                CREATE TABLE IF NOT EXISTS compression_history (
                    history_id TEXT PRIMARY KEY,
                    job_id TEXT,
                    operation TEXT NOT NULL,
                    archive_path TEXT NOT NULL,
                    source_summary TEXT NOT NULL,
                    destination_path TEXT NOT NULL,
                    profile TEXT NOT NULL,
                    file_count INTEGER NOT NULL,
                    original_bytes INTEGER NOT NULL,
                    compressed_bytes INTEGER NOT NULL,
                    saved_bytes INTEGER NOT NULL,
                    compression_ratio REAL NOT NULL,
                    duplicate_count INTEGER NOT NULL DEFAULT 0,
                    duplicate_bytes_saved INTEGER NOT NULL DEFAULT 0,
                    duration_ms INTEGER NOT NULL,
                    throughput_mb_per_sec REAL NOT NULL,
                    completed_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_compression_history_completed
                    ON compression_history(completed_utc DESC);
                """;
            cmd.ExecuteNonQuery();
            _schemaEnsured = true;
        }
    }

    public void Record(CompressionHistoryRecord entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureInitialized();

        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO compression_history (
                    history_id, job_id, operation, archive_path, source_summary, destination_path,
                    profile, file_count, original_bytes, compressed_bytes, saved_bytes,
                    compression_ratio, duplicate_count, duplicate_bytes_saved,
                    duration_ms, throughput_mb_per_sec, completed_utc
                ) VALUES (
                    $id, $jobId, $op, $archivePath, $sourceSummary, $destPath,
                    $profile, $fileCount, $origBytes, $compBytes, $savedBytes,
                    $ratio, $dupCount, $dupBytes,
                    $durationMs, $mbps, $completedUtc
                );
                """;

            cmd.Parameters.AddWithValue("$id", entry.HistoryId);
            cmd.Parameters.AddWithValue("$jobId", (object?)entry.JobId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$op", entry.Operation);
            cmd.Parameters.AddWithValue("$archivePath", entry.ArchivePath);
            cmd.Parameters.AddWithValue("$sourceSummary", entry.SourceSummary);
            cmd.Parameters.AddWithValue("$destPath", entry.DestinationPath);
            cmd.Parameters.AddWithValue("$profile", entry.Profile);
            cmd.Parameters.AddWithValue("$fileCount", entry.FileCount);
            cmd.Parameters.AddWithValue("$origBytes", entry.OriginalBytes);
            cmd.Parameters.AddWithValue("$compBytes", entry.CompressedBytes);
            cmd.Parameters.AddWithValue("$savedBytes", entry.SavedBytes);
            cmd.Parameters.AddWithValue("$ratio", entry.CompressionRatio);
            cmd.Parameters.AddWithValue("$dupCount", entry.DuplicateCount);
            cmd.Parameters.AddWithValue("$dupBytes", entry.DuplicateBytesSaved);
            cmd.Parameters.AddWithValue("$durationMs", entry.DurationMs);
            cmd.Parameters.AddWithValue("$mbps", entry.ThroughputMbPerSec);
            cmd.Parameters.AddWithValue("$completedUtc", entry.CompletedUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<CompressionHistoryRecord> ListRecent(int limit = 50, string? operationFilter = null)
    {
        EnsureInitialized();
        limit = Math.Clamp(limit, 1, 1000);

        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();

            if (string.IsNullOrWhiteSpace(operationFilter))
            {
                cmd.CommandText = """
                    SELECT history_id, job_id, operation, archive_path, source_summary, destination_path,
                           profile, file_count, original_bytes, compressed_bytes, saved_bytes,
                           compression_ratio, duplicate_count, duplicate_bytes_saved,
                           duration_ms, throughput_mb_per_sec, completed_utc
                    FROM compression_history
                    ORDER BY completed_utc DESC, rowid DESC
                    LIMIT $limit;
                    """;
            }
            else
            {
                cmd.CommandText = """
                    SELECT history_id, job_id, operation, archive_path, source_summary, destination_path,
                           profile, file_count, original_bytes, compressed_bytes, saved_bytes,
                           compression_ratio, duplicate_count, duplicate_bytes_saved,
                           duration_ms, throughput_mb_per_sec, completed_utc
                    FROM compression_history
                    WHERE lower(operation) = lower($op)
                    ORDER BY completed_utc DESC, rowid DESC
                    LIMIT $limit;
                    """;
                cmd.Parameters.AddWithValue("$op", operationFilter.Trim());
            }

            cmd.Parameters.AddWithValue("$limit", limit);

            var list = new List<CompressionHistoryRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new CompressionHistoryRecord
                {
                    HistoryId = reader.GetString(0),
                    JobId = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Operation = reader.GetString(2),
                    ArchivePath = reader.GetString(3),
                    SourceSummary = reader.GetString(4),
                    DestinationPath = reader.GetString(5),
                    Profile = reader.GetString(6),
                    FileCount = reader.GetInt32(7),
                    OriginalBytes = reader.GetInt64(8),
                    CompressedBytes = reader.GetInt64(9),
                    SavedBytes = reader.GetInt64(10),
                    CompressionRatio = reader.GetDouble(11),
                    DuplicateCount = reader.GetInt32(12),
                    DuplicateBytesSaved = reader.GetInt64(13),
                    DurationMs = reader.GetInt64(14),
                    ThroughputMbPerSec = reader.GetDouble(15),
                    CompletedUtc = DateTimeOffset.Parse(reader.GetString(16)),
                });
            }

            return list;
        }
    }

    public CompressionHistorySummary GetSummary()
    {
        EnsureInitialized();

        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    COUNT(*),
                     COALESCE(SUM(CASE WHEN lower(operation) IN ('pack', 'compress') THEN 1 ELSE 0 END), 0),
                     COALESCE(SUM(CASE WHEN lower(operation) = 'extract' THEN 1 ELSE 0 END), 0),
                     COALESCE(SUM(CASE WHEN lower(operation) IN ('pack', 'compress') THEN original_bytes ELSE 0 END), 0),
                     COALESCE(SUM(CASE WHEN lower(operation) IN ('pack', 'compress') THEN compressed_bytes ELSE 0 END), 0),
                     COALESCE(SUM(CASE WHEN lower(operation) IN ('pack', 'compress') THEN saved_bytes ELSE 0 END), 0),
                     COALESCE(SUM(CASE WHEN lower(operation) IN ('pack', 'compress') THEN duplicate_bytes_saved ELSE 0 END), 0)
                FROM compression_history;
                """;

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return new CompressionHistorySummary(0, 0, 0, 0, 0, 0, 0);
            }

            return new CompressionHistorySummary(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6));
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }

    private SqliteConnection OpenConnection()
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 10,
        };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        return conn;
    }
}
