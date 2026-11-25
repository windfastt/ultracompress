using System.Text.Json;
using UltraCompress.Desktop.Presentation.Models;

namespace UltraCompress.Desktop.Presentation.Services;

public sealed class PersistedQueueJournalEntry
{
    public required string JobId { get; init; }

    public required string Title { get; init; }

    public required IReadOnlyList<string> SourcePaths { get; init; }

    public required string OutputPath { get; init; }

    public required string TempPartFilePath { get; init; }

    public required string ProfileName { get; init; }

    public required long TotalBytes { get; init; }

    public required DesktopJobState LastKnownState { get; init; }

    public required double ProgressPercent { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed class DesktopJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly string _stateDirectory;
    private readonly string _journalFilePath;
    private readonly string _historyFilePath;

    public DesktopJournalStore(string? stateDirectory = null)
    {
        _stateDirectory = string.IsNullOrWhiteSpace(stateDirectory)
            ? GetDefaultStateDirectory()
            : Path.GetFullPath(stateDirectory);

        Directory.CreateDirectory(_stateDirectory);
        _journalFilePath = Path.Combine(_stateDirectory, "queue-journal.json");
        _historyFilePath = Path.Combine(_stateDirectory, "compression-history.json");
    }

    public string StateDirectory => _stateDirectory;

    public string GetStagingPartPath(string outputPath, string jobId)
    {
        var stagingFolder = Path.Combine(_stateDirectory, "staging");
        Directory.CreateDirectory(stagingFolder);
        var safeFileName = Path.GetFileName(outputPath);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            safeFileName = "archive.ucx";
        }

        return Path.Combine(stagingFolder, $"{safeFileName}.{jobId}.part");
    }

    public void SaveActiveJournalEntries(IEnumerable<PersistedQueueJournalEntry> entries)
    {
        lock (_sync)
        {
            var list = entries.ToList();
            WriteJsonAtomically(_journalFilePath, list);
        }
    }

    public IReadOnlyList<InterruptedJobRecoveryItem> LoadCrashRecoveryItems()
    {
        lock (_sync)
        {
            if (!File.Exists(_journalFilePath))
            {
                return Array.Empty<InterruptedJobRecoveryItem>();
            }

            try
            {
                var json = File.ReadAllText(_journalFilePath);
                var persisted = JsonSerializer.Deserialize<List<PersistedQueueJournalEntry>>(json, JsonOptions)
                                ?? [];

                var recoverable = new List<InterruptedJobRecoveryItem>();
                foreach (var entry in persisted)
                {
                    if (entry.LastKnownState is not (DesktopJobState.Running or DesktopJobState.Paused or DesktopJobState.Queued or DesktopJobState.InterruptedCrashRecovery))
                    {
                        continue;
                    }

                    var tempBytes = File.Exists(entry.TempPartFilePath)
                        ? new FileInfo(entry.TempPartFilePath).Length
                        : 0L;

                    recoverable.Add(new InterruptedJobRecoveryItem
                    {
                        JobId = entry.JobId,
                        Title = entry.Title,
                        SourcePaths = entry.SourcePaths,
                        OutputPath = entry.OutputPath,
                        TempPartFilePath = entry.TempPartFilePath,
                        TempBytesOnDisk = tempBytes,
                        TotalBytes = entry.TotalBytes,
                        ProfileName = entry.ProfileName,
                        CheckpointPercent = entry.ProgressPercent,
                        InterruptedAtUtc = entry.UpdatedAtUtc,
                    });
                }

                return recoverable;
            }
            catch
            {
                return Array.Empty<InterruptedJobRecoveryItem>();
            }
        }
    }

    public void RemoveJournalEntryAndDeleteTemp(string jobId, string? tempPartPath)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(tempPartPath) && File.Exists(tempPartPath))
            {
                try
                {
                    File.Delete(tempPartPath);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }

            if (!File.Exists(_journalFilePath))
            {
                return;
            }

            try
            {
                var existing = JsonSerializer.Deserialize<List<PersistedQueueJournalEntry>>(
                    File.ReadAllText(_journalFilePath),
                    JsonOptions) ?? [];
                var remaining = existing
                    .Where(e => !string.Equals(e.JobId, jobId, StringComparison.Ordinal))
                    .ToList();
                WriteJsonAtomically(_journalFilePath, remaining);
            }
            catch
            {
                // Ignore malformed state file during cleanup.
            }
        }
    }

    public IReadOnlyList<CompressionHistoryRecord> LoadHistory()
    {
        lock (_sync)
        {
            if (!File.Exists(_historyFilePath))
            {
                return Array.Empty<CompressionHistoryRecord>();
            }

            try
            {
                var json = File.ReadAllText(_historyFilePath);
                var list = JsonSerializer.Deserialize<List<CompressionHistoryRecord>>(json, JsonOptions) ?? [];
                return list
                    .OrderByDescending(r => r.CompletedAtUtc)
                    .ToList();
            }
            catch
            {
                return Array.Empty<CompressionHistoryRecord>();
            }
        }
    }

    public void AppendHistoryRecord(CompressionHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_sync)
        {
            var current = LoadHistory().ToList();
            current.RemoveAll(r => string.Equals(r.RecordId, record.RecordId, StringComparison.Ordinal));
            current.Insert(0, record);
            WriteJsonAtomically(_historyFilePath, current);
        }
    }

    private static void WriteJsonAtomically<T>(string filePath, T payload)
    {
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, filePath, overwrite: true);
    }

    private static string GetDefaultStateDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "UltraCompress", "desktop-state");
        }

        return Path.Combine(Path.GetTempPath(), "ultracompress", "desktop-state");
    }
}
