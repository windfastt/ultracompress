using System.Text.Json.Serialization;
using UltraCompress.Core.Destination;
using UltraCompress.Core.Errors;

namespace UltraCompress.Core.Jobs;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArchiveJobOperation
{
    Pack = 0,
    Extract = 1,
    Verify = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArchiveJobLifecycleState
{
    Queued = 0,
    Running = 1,
    Paused = 2,
    Interrupted = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6,
    RolledBack = 7,
}

public sealed class ArchivePackJobRequest
{
    public required IReadOnlyList<string> InputPaths { get; init; }

    public string? OutputPathOrDirectory { get; init; }

    public CompressionProfile Profile { get; init; } = CompressionProfile.Balanced;

    public DestinationMode DestinationMode { get; init; } = DestinationMode.SideBySide;

    public DestinationConflictPolicy ConflictPolicy { get; init; } = DestinationConflictPolicy.Fail;

    public string? ArchiveSubfolderName { get; init; }

    public long MaxSolidBlockBytes { get; init; } = 16 * 1024 * 1024;

    public string? PreferredCodecOverride { get; init; }

    public bool PreserveRootFolderName { get; init; }
}

public sealed class ArchiveExtractJobRequest
{
    public required string ArchivePath { get; init; }

    public string? OutputDirectory { get; init; }

    public IReadOnlyList<string>? SelectPatterns { get; init; }

    public DestinationMode DestinationMode { get; init; } = DestinationMode.CustomDirectory;

    public DestinationConflictPolicy ConflictPolicy { get; init; } = DestinationConflictPolicy.Fail;

    public string? SubfolderNameOverride { get; init; }
}

public sealed record ArchiveJobSnapshot
{
    public required string JobId { get; init; }

    public required ArchiveJobOperation Operation { get; init; }

    public required IReadOnlyList<string> InputPaths { get; init; }

    public required string ResolvedOutputPath { get; init; }

    public string? StagingPartPath { get; init; }

    public CompressionProfile Profile { get; init; } = CompressionProfile.Balanced;

    public DestinationMode DestinationMode { get; init; } = DestinationMode.SideBySide;

    public DestinationConflictPolicy ConflictPolicy { get; init; } = DestinationConflictPolicy.Fail;

    public IReadOnlyList<string>? SelectPatterns { get; init; }

    public long MaxSolidBlockBytes { get; init; } = 16 * 1024 * 1024;

    public string? PreferredCodecOverride { get; init; }

    public bool PreserveRootFolderName { get; init; }

    public ArchiveJobLifecycleState Status { get; init; } = ArchiveJobLifecycleState.Queued;

    public string CurrentStage { get; init; } = "Queued";

    public long ProcessedBytes { get; init; }

    public long TotalBytes { get; init; }

    public long CompressedBytes { get; init; }

    public int ProcessedFiles { get; init; }

    public int TotalFiles { get; init; }

    public int CompletedBlocks { get; init; }

    public int TotalBlocks { get; init; }

    public int ResumedBlocks { get; init; }

    public int DuplicateCount { get; init; }

    public long DuplicateBytesSaved { get; init; }

    public double ThroughputMbPerSec { get; init; }

    public double? EstimatedRemainingSeconds { get; init; }

    public bool SkippedExisting { get; init; }

    public UltraCompressError? Error { get; init; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedUtc { get; init; }

    public double PercentComplete =>
        Status == ArchiveJobLifecycleState.Completed
            ? 100.0
            : TotalBytes > 0
                ? Math.Clamp((double)ProcessedBytes / TotalBytes * 100.0, 0.0, 100.0)
                : TotalBlocks > 0
                    ? Math.Clamp((double)CompletedBlocks / TotalBlocks * 100.0, 0.0, 100.0)
                    : 0.0;

    public bool IsTerminal =>
        Status is ArchiveJobLifecycleState.Completed
            or ArchiveJobLifecycleState.Failed
            or ArchiveJobLifecycleState.Cancelled
            or ArchiveJobLifecycleState.RolledBack;
}

public sealed record InterruptedJobSummary(
    string JobId,
    ArchiveJobOperation Operation,
    IReadOnlyList<string> InputPaths,
    string ResolvedOutputPath,
    string? StagingPartPath,
    bool HasStagingFileOnDisk,
    long StagingFileBytes,
    int CompletedBlocks,
    int TotalBlocks,
    DateTimeOffset LastUpdatedUtc);
