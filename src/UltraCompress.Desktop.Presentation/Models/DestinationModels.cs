using UltraCompress.Desktop.Presentation.Primitives;

namespace UltraCompress.Desktop.Presentation.Models;

public sealed class DiskSpacePreCheckResult
{
    public string DestinationFolder { get; init; } = string.Empty;

    public string ResolvedOutputPath { get; init; } = string.Empty;

    public long AvailableFreeBytes { get; init; }

    public long EstimatedOutputBytes { get; init; }

    public long RequiredBytesWithHeadroom { get; init; }

    public bool HasSufficientSpace { get; init; }

    public bool IsDestinationWritable { get; init; }

    public bool ExistingTargetConflictDetected { get; init; }

    public DestinationConflictMode ConflictMode { get; init; }

    public bool WillSkipOperation { get; init; }

    public PreCheckSeverity Severity { get; init; }

    public string StatusMessage { get; init; } = string.Empty;

    public string AvailableSpaceFormatted => ByteSizeFormatter.FormatBytes(AvailableFreeBytes);

    public string RequiredSpaceFormatted => ByteSizeFormatter.FormatBytes(RequiredBytesWithHeadroom);

    public bool CanProceed => HasSufficientSpace && IsDestinationWritable && Severity != PreCheckSeverity.Blocked;
}
