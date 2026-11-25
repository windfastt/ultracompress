using System.Text.Json.Serialization;
using UltraCompress.Core.Destination;
using UltraCompress.Core.Jobs;

namespace UltraCompress.Core.Archive;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UcxContentClass
{
    EmptyOrDirectory = 0,
    IncompressibleMedia = 1,
    SourceAndText = 2,
    StructuredData = 3,
    ExecutableAndBinary = 4,
}

public sealed class UcxArchiveEntry
{
    public required string RelativePath { get; init; }

    public bool IsDirectory { get; init; }

    public long OriginalSize { get; init; }

    public required string Sha256 { get; init; }

    public DateTimeOffset LastModifiedUtc { get; init; }

    public DateTimeOffset CreationUtc { get; init; }

    public int FileAttributesRaw { get; init; }

    public UcxContentClass ContentClass { get; init; }

    public int? BlockIndex { get; set; }

    public long OffsetInBlock { get; set; }

    public long LengthInBlock { get; set; }

    public bool IsDuplicate { get; set; }

    public string? CanonicalEntryPath { get; set; }
}

public sealed class UcxArchiveBlock
{
    public required int BlockIndex { get; init; }

    public required UcxContentClass ContentClass { get; init; }

    public required string Codec { get; init; }

    public int CodecLevel { get; init; }

    public required string CodecRationale { get; init; }

    public long OffsetInArchive { get; init; }

    public long UncompressedLength { get; init; }

    public long CompressedLength { get; init; }

    public required string UncompressedSha256 { get; init; }

    public required string CompressedSha256 { get; init; }

    public int EntryCount { get; init; }
}

public sealed class UcxArchiveSummary
{
    public int TotalEntries { get; init; }

    public int FileCount { get; init; }

    public int DirectoryCount { get; init; }

    public int BlockCount { get; init; }

    public long OriginalBytes { get; init; }

    public long UniqueBytes { get; init; }

    public long CompressedPayloadBytes { get; init; }

    public long ArchiveBytes { get; set; }

    public int DuplicateCount { get; init; }

    public long DuplicateBytesSaved { get; init; }

    [JsonIgnore]
    public double CompressionRatio =>
        OriginalBytes == 0 ? 1.0 : (double)CompressedPayloadBytes / OriginalBytes;

    [JsonIgnore]
    public long SpaceSavedBytes =>
        Math.Max(0, OriginalBytes - CompressedPayloadBytes);

    [JsonIgnore]
    public double SpaceSavedPercent =>
        OriginalBytes == 0 ? 0.0 : (double)SpaceSavedBytes / OriginalBytes * 100.0;
}

public sealed class UcxArchiveManifest
{
    public string FormatVersion { get; init; } = "1.0";

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public CompressionProfile Profile { get; init; } = CompressionProfile.Balanced;

    public required UcxArchiveSummary Summary { get; init; }

    public required List<UcxArchiveBlock> Blocks { get; init; }

    public required List<UcxArchiveEntry> Entries { get; init; }
}

public sealed class UcxArchiveEntryView
{
    public required string RelativePath { get; init; }

    public bool IsDirectory { get; init; }

    public long OriginalSize { get; init; }

    public long EffectiveCompressedSize { get; init; }

    public required string Sha256 { get; init; }

    public DateTimeOffset LastModifiedUtc { get; init; }

    public DateTimeOffset CreationUtc { get; init; }

    public FileAttributes Attributes { get; init; }

    public UcxContentClass ContentClass { get; init; }

    public int? BlockIndex { get; init; }

    public required string Codec { get; init; }

    public int CodecLevel { get; init; }

    public bool IsDuplicate { get; init; }

    public string? CanonicalEntryPath { get; init; }

    public required string CodecRationale { get; init; }
}

public sealed class UcxEntryTreeNode
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public bool IsDirectory { get; init; }

    public long OriginalBytes { get; set; }

    public long EffectiveCompressedBytes { get; set; }

    public int FileCount { get; set; }

    public int DuplicateCount { get; set; }

    public UcxArchiveEntryView? Entry { get; init; }

    public List<UcxEntryTreeNode> Children { get; init; } = new();
}

public sealed class UcxArchiveInspection
{
    public string? ArchivePath { get; init; }

    public long ArchiveFileBytes { get; init; }

    public required string FormatVersion { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public CompressionProfile Profile { get; init; }

    public required UcxArchiveSummary Summary { get; init; }

    public required IReadOnlyList<UcxArchiveBlock> Blocks { get; init; }

    public required IReadOnlyList<UcxArchiveEntryView> Entries { get; init; }

    public required UcxEntryTreeNode RootTree { get; init; }
}

public sealed record UcxBlockCheckpoint(
    int BlockIndex,
    string BlockPlanHash,
    UcxContentClass ContentClass,
    string Codec,
    int CodecLevel,
    string CodecRationale,
    long OffsetInArchive,
    long UncompressedLength,
    long CompressedLength,
    string UncompressedSha256,
    string CompressedSha256,
    int EntryCount);

public sealed record ArchiveProgressSnapshot
{
    public required string Stage { get; init; }

    public long ProcessedBytes { get; init; }

    public long TotalBytes { get; init; }

    public int ProcessedFiles { get; init; }

    public int TotalFiles { get; init; }

    public int CompletedBlocks { get; init; }

    public int TotalBlocks { get; init; }

    public double ThroughputMbPerSec { get; init; }

    public double? EstimatedRemainingSeconds { get; init; }

    public string? CurrentItem { get; init; }

    public double PercentComplete =>
        TotalBytes > 0
            ? Math.Clamp((double)ProcessedBytes / TotalBytes * 100.0, 0.0, 100.0)
            : TotalFiles > 0
                ? Math.Clamp((double)ProcessedFiles / TotalFiles * 100.0, 0.0, 100.0)
                : 100.0;
}

public sealed class UcxPackOptions
{
    public CompressionProfile Profile { get; init; } = CompressionProfile.Balanced;

    public long MaxSolidBlockBytes { get; init; } = 16 * 1024 * 1024;

    public string? PreferredCodecOverride { get; init; }

    public bool OverwriteDestination { get; init; } = true;

    public bool PreserveRootFolderName { get; init; }

    public string? StagingPartPath { get; init; }

    public IReadOnlyList<UcxBlockCheckpoint>? ExistingCheckpoints { get; init; }

    public Func<UcxBlockCheckpoint, CancellationToken, ValueTask>? OnBlockCommittedAsync { get; init; }

    public Action<int>? OnTotalBlocksPlanned { get; init; }

    public IPauseToken? PauseToken { get; init; }

    public IReadOnlyDictionary<string, ICompressionBackend>? CustomBackends { get; init; }
}

public sealed class UcxExtractOptions
{
    public IReadOnlyList<string>? SelectPatterns { get; init; }

    public DestinationConflictPolicy ConflictPolicy { get; init; } = DestinationConflictPolicy.Fail;

    public bool PreserveTimestamps { get; init; } = true;

    public bool PreserveAttributes { get; init; } = true;

    public IPauseToken? PauseToken { get; init; }

    public IReadOnlyDictionary<string, ICompressionBackend>? CustomBackends { get; init; }
}

public sealed record UcxArchiveWriteResult(
    string ArchivePath,
    UcxArchiveSummary Summary,
    IReadOnlyList<UcxArchiveBlock> Blocks,
    int ResumedBlocksFromCheckpoint,
    int NewlyCompressedBlocks,
    TimeSpan Elapsed);

public sealed record UcxArchiveExtractResult(
    string ArchivePath,
    string DestinationDirectory,
    int ExtractedFiles,
    int ExtractedDirectories,
    int SkippedExistingFiles,
    int DecompressedBlockCount,
    long ExtractedBytes,
    IReadOnlyList<string> OutputFilePaths,
    TimeSpan Elapsed);

public sealed record UcxArchiveVerifyResult(
    string ArchivePath,
    bool IsValid,
    int VerifiedFiles,
    int VerifiedBlocks,
    long VerifiedOriginalBytes,
    TimeSpan Elapsed,
    string Message);
