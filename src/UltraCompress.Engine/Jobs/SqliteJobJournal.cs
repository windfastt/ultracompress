using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using UltraCompress.Core;
using UltraCompress.Core.Archive;
using UltraCompress.Core.Destination;
using UltraCompress.Core.Errors;
using UltraCompress.Core.Jobs;

namespace UltraCompress.Engine.Jobs;

/// <summary>
/// Persistent SQLite job journal (<c>jobs.db</c>) storing queued/running archive jobs,
/// crash-recovery state, and atomic block-level checkpoints.
/// </summary>
public sealed class SqliteJobJournal : IDisposable
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _databasePath;
    private readonly object _sync = new();
    private bool _initialized;

    public SqliteJobJournal(string? databasePath = null)
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
            if (_initialized)
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

                CREATE TABLE IF NOT EXISTS job_journal_schema (
                    version INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS archive_jobs (
                    job_id TEXT PRIMARY KEY,
                    operation TEXT NOT NULL,
                    input_paths_json TEXT NOT NULL,
                    resolved_output_path TEXT NOT NULL,
                    staging_part_path TEXT,
                    profile TEXT NOT NULL,
                    destination_mode TEXT NOT NULL,
                    conflict_policy TEXT NOT NULL,
                    select_patterns_json TEXT,
                    max_solid_block_bytes INTEGER NOT NULL DEFAULT 16777216,
                    preferred_codec_override TEXT,
                    preserve_root_folder INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL,
                    current_stage TEXT NOT NULL,
                    processed_bytes INTEGER NOT NULL DEFAULT 0,
                    total_bytes INTEGER NOT NULL DEFAULT 0,
                    compressed_bytes INTEGER NOT NULL DEFAULT 0,
                    processed_files INTEGER NOT NULL DEFAULT 0,
                    total_files INTEGER NOT NULL DEFAULT 0,
                    completed_blocks INTEGER NOT NULL DEFAULT 0,
                    total_blocks INTEGER NOT NULL DEFAULT 0,
                    resumed_blocks INTEGER NOT NULL DEFAULT 0,
                    duplicate_count INTEGER NOT NULL DEFAULT 0,
                    duplicate_bytes_saved INTEGER NOT NULL DEFAULT 0,
                    throughput_mbps REAL NOT NULL DEFAULT 0,
                    eta_seconds REAL,
                    skipped_existing INTEGER NOT NULL DEFAULT 0,
                    error_json TEXT,
                    created_utc TEXT NOT NULL,
                    started_utc TEXT,
                    updated_utc TEXT NOT NULL,
                    completed_utc TEXT
                );

                CREATE INDEX IF NOT EXISTS ix_archive_jobs_status_created
                    ON archive_jobs(status, created_utc);

                CREATE TABLE IF NOT EXISTS job_block_checkpoints (
                    job_id TEXT NOT NULL,
                    block_index INTEGER NOT NULL,
                    block_plan_hash TEXT NOT NULL,
                    content_class TEXT NOT NULL,
                    codec TEXT NOT NULL,
                    codec_level INTEGER NOT NULL,
                    codec_rationale TEXT NOT NULL,
                    offset_in_archive INTEGER NOT NULL,
                    uncompressed_length INTEGER NOT NULL,
                    compressed_length INTEGER NOT NULL,
                    uncompressed_sha256 TEXT NOT NULL,
                    compressed_sha256 TEXT NOT NULL,
                    entry_count INTEGER NOT NULL,
                    committed_utc TEXT NOT NULL,
                    PRIMARY KEY (job_id, block_index)
                );
                """;
            cmd.ExecuteNonQuery();

            using var checkVer = conn.CreateCommand();
            checkVer.CommandText = "SELECT COUNT(*) FROM job_journal_schema;";
            if (Convert.ToInt32(checkVer.ExecuteScalar()) == 0)
            {
                using var insertVer = conn.CreateCommand();
                insertVer.CommandText = "INSERT INTO job_journal_schema(version) VALUES ($v);";
                insertVer.Parameters.AddWithValue("$v", SchemaVersion);
                insertVer.ExecuteNonQuery();
            }

            _initialized = true;
        }
    }

    public void SaveJob(ArchiveJobSnapshot job)
    {
        ArgumentNullException.ThrowIfNull(job);
        EnsureInitialized();

        lock (_sync)
        {
            using var conn = OpenConnection();
            UpsertJobCore(conn, transaction: null, job);
        }
    }

    public ArchiveJobSnapshot? GetJob(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        EnsureInitialized();
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT job_id, operation, input_paths_json, resolved_output_path, staging_part_path,
                       profile, destination_mode, conflict_policy, select_patterns_json,
                       max_solid_block_bytes, preferred_codec_override, preserve_root_folder,
                       status, current_stage, processed_bytes, total_bytes, compressed_bytes,
                       processed_files, total_files, completed_blocks, total_blocks, resumed_blocks,
                       duplicate_count, duplicate_bytes_saved, throughput_mbps, eta_seconds,
                       skipped_existing, error_json, created_utc, started_utc, updated_utc, completed_utc
                FROM archive_jobs
                WHERE job_id = $id
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$id", jobId);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadJobRow(reader) : null;
        }
    }

    public IReadOnlyList<ArchiveJobSnapshot> ListJobs(int limit = 100, ArchiveJobLifecycleState? stateFilter = null)
    {
        EnsureInitialized();
        limit = Math.Clamp(limit, 1, 1000);

        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = stateFilter is null
                ? """
                    SELECT job_id, operation, input_paths_json, resolved_output_path, staging_part_path,
                           profile, destination_mode, conflict_policy, select_patterns_json,
                           max_solid_block_bytes, preferred_codec_override, preserve_root_folder,
                           status, current_stage, processed_bytes, total_bytes, compressed_bytes,
                           processed_files, total_files, completed_blocks, total_blocks, resumed_blocks,
                           duplicate_count, duplicate_bytes_saved, throughput_mbps, eta_seconds,
                           skipped_existing, error_json, created_utc, started_utc, updated_utc, completed_utc
                    FROM archive_jobs
                    ORDER BY created_utc DESC, rowid DESC
                    LIMIT $limit;
                    """
                : """
                    SELECT job_id, operation, input_paths_json, resolved_output_path, staging_part_path,
                           profile, destination_mode, conflict_policy, select_patterns_json,
                           max_solid_block_bytes, preferred_codec_override, preserve_root_folder,
                           status, current_stage, processed_bytes, total_bytes, compressed_bytes,
                           processed_files, total_files, completed_blocks, total_blocks, resumed_blocks,
                           duplicate_count, duplicate_bytes_saved, throughput_mbps, eta_seconds,
                           skipped_existing, error_json, created_utc, started_utc, updated_utc, completed_utc
                    FROM archive_jobs
                    WHERE status = $status
                    ORDER BY created_utc DESC, rowid DESC
                    LIMIT $limit;
                    """;

            if (stateFilter is not null)
            {
                cmd.Parameters.AddWithValue("$status", stateFilter.Value.ToString());
            }

            cmd.Parameters.AddWithValue("$limit", limit);

            var list = new List<ArchiveJobSnapshot>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadJobRow(reader));
            }

            return list;
        }
    }

    public ArchiveJobSnapshot? TryClaimNextQueuedJob()
    {
        EnsureInitialized();
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            ArchiveJobSnapshot? candidate = null;
            using (var selectCmd = conn.CreateCommand())
            {
                selectCmd.Transaction = tx;
                selectCmd.CommandText = """
                    SELECT job_id, operation, input_paths_json, resolved_output_path, staging_part_path,
                           profile, destination_mode, conflict_policy, select_patterns_json,
                           max_solid_block_bytes, preferred_codec_override, preserve_root_folder,
                           status, current_stage, processed_bytes, total_bytes, compressed_bytes,
                           processed_files, total_files, completed_blocks, total_blocks, resumed_blocks,
                           duplicate_count, duplicate_bytes_saved, throughput_mbps, eta_seconds,
                           skipped_existing, error_json, created_utc, started_utc, updated_utc, completed_utc
                    FROM archive_jobs
                    WHERE status = 'Queued'
                    ORDER BY created_utc ASC, rowid ASC
                    LIMIT 1;
                    """;
                using var reader = selectCmd.ExecuteReader();
                if (reader.Read())
                {
                    candidate = ReadJobRow(reader);
                }
            }

            if (candidate is null)
            {
                tx.Commit();
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var claimed = candidate with
            {
                Status = ArchiveJobLifecycleState.Running,
                CurrentStage = "Starting",
                StartedUtc = candidate.StartedUtc ?? now,
                UpdatedUtc = now,
            };

            UpsertJobCore(conn, tx, claimed);
            tx.Commit();
            return claimed;
        }
    }

    public void CommitBlockCheckpoint(string jobId, UcxBlockCheckpoint checkpoint, int totalBlocks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(checkpoint);
        EnsureInitialized();

        lock (_sync)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            using (var cpCmd = conn.CreateCommand())
            {
                cpCmd.Transaction = tx;
                cpCmd.CommandText = """
                    INSERT OR REPLACE INTO job_block_checkpoints (
                        job_id, block_index, block_plan_hash, content_class, codec, codec_level,
                        codec_rationale, offset_in_archive, uncompressed_length, compressed_length,
                        uncompressed_sha256, compressed_sha256, entry_count, committed_utc
                    ) VALUES (
                        $jobId, $idx, $planHash, $contentClass, $codec, $level,
                        $rationale, $offset, $uncompLen, $compLen,
                        $uncompSha, $compSha, $entryCount, $now
                    );
                    """;
                cpCmd.Parameters.AddWithValue("$jobId", jobId);
                cpCmd.Parameters.AddWithValue("$idx", checkpoint.BlockIndex);
                cpCmd.Parameters.AddWithValue("$planHash", checkpoint.BlockPlanHash);
                cpCmd.Parameters.AddWithValue("$contentClass", checkpoint.ContentClass.ToString());
                cpCmd.Parameters.AddWithValue("$codec", checkpoint.Codec);
                cpCmd.Parameters.AddWithValue("$level", checkpoint.CodecLevel);
                cpCmd.Parameters.AddWithValue("$rationale", checkpoint.CodecRationale);
                cpCmd.Parameters.AddWithValue("$offset", checkpoint.OffsetInArchive);
                cpCmd.Parameters.AddWithValue("$uncompLen", checkpoint.UncompressedLength);
                cpCmd.Parameters.AddWithValue("$compLen", checkpoint.CompressedLength);
                cpCmd.Parameters.AddWithValue("$uncompSha", checkpoint.UncompressedSha256);
                cpCmd.Parameters.AddWithValue("$compSha", checkpoint.CompressedSha256);
                cpCmd.Parameters.AddWithValue("$entryCount", checkpoint.EntryCount);
                cpCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                cpCmd.ExecuteNonQuery();
            }

            using (var updateJobCmd = conn.CreateCommand())
            {
                updateJobCmd.Transaction = tx;
                updateJobCmd.CommandText = """
                    UPDATE archive_jobs
                    SET completed_blocks = (SELECT COUNT(*) FROM job_block_checkpoints WHERE job_id = $jobId),
                        total_blocks = CASE WHEN $totalBlocks > total_blocks THEN $totalBlocks ELSE total_blocks END,
                        compressed_bytes = (SELECT COALESCE(SUM(compressed_length), 0) FROM job_block_checkpoints WHERE job_id = $jobId),
                        processed_bytes = (SELECT COALESCE(SUM(uncompressed_length), 0) FROM job_block_checkpoints WHERE job_id = $jobId),
                        current_stage = 'CompressingBlocks',
                        updated_utc = $now
                    WHERE job_id = $jobId;
                    """;
                updateJobCmd.Parameters.AddWithValue("$jobId", jobId);
                updateJobCmd.Parameters.AddWithValue("$totalBlocks", totalBlocks);
                updateJobCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                updateJobCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public IReadOnlyList<UcxBlockCheckpoint> GetBlockCheckpoints(string jobId)
    {
        EnsureInitialized();
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT block_index, block_plan_hash, content_class, codec, codec_level,
                       codec_rationale, offset_in_archive, uncompressed_length, compressed_length,
                       uncompressed_sha256, compressed_sha256, entry_count
                FROM job_block_checkpoints
                WHERE job_id = $jobId
                ORDER BY block_index ASC;
                """;
            cmd.Parameters.AddWithValue("$jobId", jobId);

            var checkpoints = new List<UcxBlockCheckpoint>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                checkpoints.Add(new UcxBlockCheckpoint(
                    BlockIndex: reader.GetInt32(0),
                    BlockPlanHash: reader.GetString(1),
                    ContentClass: Enum.Parse<UcxContentClass>(reader.GetString(2), ignoreCase: true),
                    Codec: reader.GetString(3),
                    CodecLevel: reader.GetInt32(4),
                    CodecRationale: reader.GetString(5),
                    OffsetInArchive: reader.GetInt64(6),
                    UncompressedLength: reader.GetInt64(7),
                    CompressedLength: reader.GetInt64(8),
                    UncompressedSha256: reader.GetString(9),
                    CompressedSha256: reader.GetString(10),
                    EntryCount: reader.GetInt32(11)));
            }

            return checkpoints;
        }
    }

    public void ClearBlockCheckpoints(string jobId)
    {
        EnsureInitialized();
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM job_block_checkpoints WHERE job_id = $jobId;";
            cmd.Parameters.AddWithValue("$jobId", jobId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Detects jobs left in <c>Running</c> or <c>Paused</c> after an unclean service/process termination
    /// and transitions them cleanly to <see cref="ArchiveJobLifecycleState.Interrupted"/> so the user or
    /// operator can trigger one-click Resume or Clean Rollback.
    /// </summary>
    public IReadOnlyList<InterruptedJobSummary> DetectAndMarkInterruptedJobs()
    {
        EnsureInitialized();
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            var activeOnCrash = new List<ArchiveJobSnapshot>();
            using (var selectCmd = conn.CreateCommand())
            {
                selectCmd.Transaction = tx;
                selectCmd.CommandText = """
                    SELECT job_id, operation, input_paths_json, resolved_output_path, staging_part_path,
                           profile, destination_mode, conflict_policy, select_patterns_json,
                           max_solid_block_bytes, preferred_codec_override, preserve_root_folder,
                           status, current_stage, processed_bytes, total_bytes, compressed_bytes,
                           processed_files, total_files, completed_blocks, total_blocks, resumed_blocks,
                           duplicate_count, duplicate_bytes_saved, throughput_mbps, eta_seconds,
                           skipped_existing, error_json, created_utc, started_utc, updated_utc, completed_utc
                    FROM archive_jobs
                    WHERE status IN ('Running', 'Paused', 'Interrupted')
                    ORDER BY created_utc ASC;
                    """;
                using var reader = selectCmd.ExecuteReader();
                while (reader.Read())
                {
                    activeOnCrash.Add(ReadJobRow(reader));
                }
            }

            var summaries = new List<InterruptedJobSummary>(activeOnCrash.Count);
            var now = DateTimeOffset.UtcNow;

            foreach (var job in activeOnCrash)
            {
                int cpCount;
                using (var cpCmd = conn.CreateCommand())
                {
                    cpCmd.Transaction = tx;
                    cpCmd.CommandText = "SELECT COUNT(*) FROM job_block_checkpoints WHERE job_id = $jobId;";
                    cpCmd.Parameters.AddWithValue("$jobId", job.JobId);
                    cpCount = Convert.ToInt32(cpCmd.ExecuteScalar());
                }

                var interrupted = job with
                {
                    Status = ArchiveJobLifecycleState.Interrupted,
                    CurrentStage = "Interrupted",
                    CompletedBlocks = cpCount,
                    ThroughputMbPerSec = 0,
                    EstimatedRemainingSeconds = null,
                    UpdatedUtc = now,
                };

                UpsertJobCore(conn, tx, interrupted);

                bool hasPart = !string.IsNullOrEmpty(interrupted.StagingPartPath) && File.Exists(interrupted.StagingPartPath);
                long partBytes = hasPart ? new FileInfo(interrupted.StagingPartPath!).Length : 0;

                summaries.Add(new InterruptedJobSummary(
                    interrupted.JobId,
                    interrupted.Operation,
                    interrupted.InputPaths,
                    interrupted.ResolvedOutputPath,
                    interrupted.StagingPartPath,
                    hasPart,
                    partBytes,
                    interrupted.CompletedBlocks,
                    interrupted.TotalBlocks,
                    now));
            }

            tx.Commit();
            return summaries;
        }
    }

    public IReadOnlyList<InterruptedJobSummary> ListInterruptedJobs()
    {
        var jobs = ListJobs(500, ArchiveJobLifecycleState.Interrupted);
        return jobs.Select(j =>
        {
            bool hasPart = !string.IsNullOrEmpty(j.StagingPartPath) && File.Exists(j.StagingPartPath);
            long partBytes = hasPart ? new FileInfo(j.StagingPartPath!).Length : 0;
            return new InterruptedJobSummary(
                j.JobId,
                j.Operation,
                j.InputPaths,
                j.ResolvedOutputPath,
                j.StagingPartPath,
                hasPart,
                partBytes,
                j.CompletedBlocks,
                j.TotalBlocks,
                j.UpdatedUtc);
        }).ToList();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }

    private static void UpsertJobCore(SqliteConnection conn, SqliteTransaction? transaction, ArchiveJobSnapshot job)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR REPLACE INTO archive_jobs (
                job_id, operation, input_paths_json, resolved_output_path, staging_part_path,
                profile, destination_mode, conflict_policy, select_patterns_json,
                max_solid_block_bytes, preferred_codec_override, preserve_root_folder,
                status, current_stage, processed_bytes, total_bytes, compressed_bytes,
                processed_files, total_files, completed_blocks, total_blocks, resumed_blocks,
                duplicate_count, duplicate_bytes_saved, throughput_mbps, eta_seconds,
                skipped_existing, error_json, created_utc, started_utc, updated_utc, completed_utc
            ) VALUES (
                $id, $op, $inputsJson, $outputPath, $stagingPath,
                $profile, $destMode, $conflictPolicy, $selectJson,
                $maxSolidBytes, $codecOverride, $preserveRoot,
                $status, $stage, $procBytes, $totBytes, $compBytes,
                $procFiles, $totFiles, $compBlocks, $totBlocks, $resumedBlocks,
                $dupCount, $dupBytes, $mbps, $eta,
                $skipped, $errorJson, $created, $started, $updated, $completed
            );
            """;

        cmd.Parameters.AddWithValue("$id", job.JobId);
        cmd.Parameters.AddWithValue("$op", job.Operation.ToString());
        cmd.Parameters.AddWithValue("$inputsJson", JsonSerializer.Serialize(job.InputPaths, JsonOptions));
        cmd.Parameters.AddWithValue("$outputPath", job.ResolvedOutputPath);
        cmd.Parameters.AddWithValue("$stagingPath", (object?)job.StagingPartPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$profile", job.Profile.ToString());
        cmd.Parameters.AddWithValue("$destMode", job.DestinationMode.ToString());
        cmd.Parameters.AddWithValue("$conflictPolicy", job.ConflictPolicy.ToString());
        cmd.Parameters.AddWithValue("$selectJson", job.SelectPatterns is null
            ? DBNull.Value
            : JsonSerializer.Serialize(job.SelectPatterns, JsonOptions));
        cmd.Parameters.AddWithValue("$maxSolidBytes", job.MaxSolidBlockBytes);
        cmd.Parameters.AddWithValue("$codecOverride", (object?)job.PreferredCodecOverride ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$preserveRoot", job.PreserveRootFolderName ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", job.Status.ToString());
        cmd.Parameters.AddWithValue("$stage", job.CurrentStage);
        cmd.Parameters.AddWithValue("$procBytes", job.ProcessedBytes);
        cmd.Parameters.AddWithValue("$totBytes", job.TotalBytes);
        cmd.Parameters.AddWithValue("$compBytes", job.CompressedBytes);
        cmd.Parameters.AddWithValue("$procFiles", job.ProcessedFiles);
        cmd.Parameters.AddWithValue("$totFiles", job.TotalFiles);
        cmd.Parameters.AddWithValue("$compBlocks", job.CompletedBlocks);
        cmd.Parameters.AddWithValue("$totBlocks", job.TotalBlocks);
        cmd.Parameters.AddWithValue("$resumedBlocks", job.ResumedBlocks);
        cmd.Parameters.AddWithValue("$dupCount", job.DuplicateCount);
        cmd.Parameters.AddWithValue("$dupBytes", job.DuplicateBytesSaved);
        cmd.Parameters.AddWithValue("$mbps", job.ThroughputMbPerSec);
        cmd.Parameters.AddWithValue("$eta", job.EstimatedRemainingSeconds is null
            ? DBNull.Value
            : job.EstimatedRemainingSeconds.Value);
        cmd.Parameters.AddWithValue("$skipped", job.SkippedExisting ? 1 : 0);
        cmd.Parameters.AddWithValue("$errorJson", job.Error is null
            ? DBNull.Value
            : JsonSerializer.Serialize(job.Error, JsonOptions));
        cmd.Parameters.AddWithValue("$created", job.CreatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$started", job.StartedUtc is null ? DBNull.Value : job.StartedUtc.Value.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", job.UpdatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$completed", job.CompletedUtc is null ? DBNull.Value : job.CompletedUtc.Value.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static ArchiveJobSnapshot ReadJobRow(SqliteDataReader r)
    {
        var inputs = JsonSerializer.Deserialize<List<string>>(r.GetString(2), JsonOptions) ?? new List<string>();
        var selectPatterns = r.IsDBNull(8)
            ? null
            : JsonSerializer.Deserialize<List<string>>(r.GetString(8), JsonOptions);
        var error = r.IsDBNull(27)
            ? null
            : JsonSerializer.Deserialize<UltraCompressError>(r.GetString(27), JsonOptions);

        return new ArchiveJobSnapshot
        {
            JobId = r.GetString(0),
            Operation = Enum.Parse<ArchiveJobOperation>(r.GetString(1), ignoreCase: true),
            InputPaths = inputs,
            ResolvedOutputPath = r.GetString(3),
            StagingPartPath = r.IsDBNull(4) ? null : r.GetString(4),
            Profile = CompressionProfileParser.ParseOrDefault(r.GetString(5)),
            DestinationMode = Enum.Parse<DestinationMode>(r.GetString(6), ignoreCase: true),
            ConflictPolicy = Enum.Parse<DestinationConflictPolicy>(r.GetString(7), ignoreCase: true),
            SelectPatterns = selectPatterns,
            MaxSolidBlockBytes = r.GetInt64(9),
            PreferredCodecOverride = r.IsDBNull(10) ? null : r.GetString(10),
            PreserveRootFolderName = r.GetInt32(11) != 0,
            Status = Enum.Parse<ArchiveJobLifecycleState>(r.GetString(12), ignoreCase: true),
            CurrentStage = r.GetString(13),
            ProcessedBytes = r.GetInt64(14),
            TotalBytes = r.GetInt64(15),
            CompressedBytes = r.GetInt64(16),
            ProcessedFiles = r.GetInt32(17),
            TotalFiles = r.GetInt32(18),
            CompletedBlocks = r.GetInt32(19),
            TotalBlocks = r.GetInt32(20),
            ResumedBlocks = r.GetInt32(21),
            DuplicateCount = r.GetInt32(22),
            DuplicateBytesSaved = r.GetInt64(23),
            ThroughputMbPerSec = r.GetDouble(24),
            EstimatedRemainingSeconds = r.IsDBNull(25) ? null : r.GetDouble(25),
            SkippedExisting = r.GetInt32(26) != 0,
            Error = error,
            CreatedUtc = DateTimeOffset.Parse(r.GetString(28)),
            StartedUtc = r.IsDBNull(29) ? null : DateTimeOffset.Parse(r.GetString(29)),
            UpdatedUtc = DateTimeOffset.Parse(r.GetString(30)),
            CompletedUtc = r.IsDBNull(31) ? null : DateTimeOffset.Parse(r.GetString(31)),
        };
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

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        opts.Converters.Add(new JsonStringEnumConverter());
        return opts;
    }
}
