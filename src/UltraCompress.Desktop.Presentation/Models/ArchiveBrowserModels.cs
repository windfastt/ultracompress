using UltraCompress.Desktop.Presentation.Primitives;

namespace UltraCompress.Desktop.Presentation.Models;

public sealed class ArchiveEntryNode : ObservableObject
{
    private bool _isSelected;
    private Sha256VerificationStatus _verificationStatus;

    public string EntryPath { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string ParentFolder { get; init; } = string.Empty;

    public bool IsDirectory { get; init; }

    public int Depth { get; init; }

    public long OriginalSizeBytes { get; init; }

    public long CompressedSizeBytes { get; init; }

    public string StrategyCodec { get; init; } = string.Empty;

    public string ExpectedSha256 { get; init; } = string.Empty;

    public bool IsDuplicateReference { get; init; }

    public string? DuplicateTargetEntryPath { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                RaisePropertyChanged(nameof(IsSelectedNullable));
            }
        }
    }

    public bool? IsSelectedNullable
    {
        get => _isSelected;
        set => IsSelected = value == true;
    }

    public Sha256VerificationStatus VerificationStatus
    {
        get => _verificationStatus;
        set
        {
            if (SetProperty(ref _verificationStatus, value))
            {
                RaisePropertyChanged(nameof(Sha256StatusDisplay));
            }
        }
    }

    public double SavingsPercent =>
        IsDirectory || OriginalSizeBytes <= 0
            ? 0.0
            : Math.Clamp((1.0 - ((double)CompressedSizeBytes / OriginalSizeBytes)) * 100.0, -99.9, 100.0);

    public string IndentedDisplayName =>
        string.Concat(new string(' ', Depth * 3), IsDirectory ? $"[Dir] {Name}/" : Name);

    public string OriginalSizeFormatted =>
        IsDirectory ? "—" : ByteSizeFormatter.FormatBytes(OriginalSizeBytes);

    public string CompressedSizeFormatted =>
        IsDirectory ? "—" : ByteSizeFormatter.FormatBytes(CompressedSizeBytes);

    public string SavingsFormatted =>
        IsDirectory ? "—" : ByteSizeFormatter.FormatPercent(SavingsPercent);

    public string Sha256StatusDisplay
    {
        get
        {
            if (IsDirectory)
            {
                return "Folder";
            }

            var shortHash = ExpectedSha256.Length >= 10 ? ExpectedSha256[..10] : "crc/stream";
            return VerificationStatus switch
            {
                Sha256VerificationStatus.VerifiedMatch => $"Verified ({shortHash}…)",
                Sha256VerificationStatus.DedupReferenceVerified => $"Dedup Verified ({shortHash}…)",
                Sha256VerificationStatus.MismatchFailed => $"MISMATCH ({shortHash}…)",
                Sha256VerificationStatus.MissingPayload => "MISSING PAYLOAD",
                _ => string.IsNullOrEmpty(ExpectedSha256) ? "Unchecked" : $"Pending ({shortHash}…)",
            };
        }
    }
}

public sealed class ArchiveManifestSummary
{
    public string ArchivePath { get; init; } = string.Empty;

    public string ArchiveFileName { get; init; } = string.Empty;

    public string ContainerFormat { get; init; } = string.Empty;

    public int TotalFileEntries { get; init; }

    public int TotalDirectoryEntries { get; init; }

    public long TotalOriginalBytes { get; init; }

    public long TotalArchiveBytes { get; init; }

    public int DuplicateEntriesCollapsed { get; init; }

    public double OverallSavingsPercent =>
        TotalOriginalBytes <= 0
            ? 0
            : Math.Clamp((1.0 - ((double)TotalArchiveBytes / TotalOriginalBytes)) * 100.0, -99.9, 100.0);

    public string SummaryLine =>
        $"{ArchiveFileName} ({ContainerFormat}) · {TotalFileEntries} file(s) · {ByteSizeFormatter.FormatBytes(TotalOriginalBytes)} → {ByteSizeFormatter.FormatBytes(TotalArchiveBytes)} ({ByteSizeFormatter.FormatPercent(OverallSavingsPercent)} saved)";
}

public sealed class SelectiveExtractionResult
{
    public bool Success { get; init; }

    public int ExtractedFileCount { get; init; }

    public int SkippedFileCount { get; init; }

    public int VerifiedFileCount { get; init; }

    public long TotalExtractedBytes { get; init; }

    public string DestinationFolder { get; init; } = string.Empty;

    public DestinationConflictMode ConflictModeApplied { get; init; }

    public IReadOnlyList<string> ExtractedPaths { get; init; } = Array.Empty<string>();

    public ActionableErrorCard? ErrorCard { get; init; }

    public string SummaryMessage { get; init; } = string.Empty;
}

public sealed class ArchiveVerificationReport
{
    public bool IsAllValid { get; init; }

    public int TotalEntriesChecked { get; init; }

    public int VerifiedCount { get; init; }

    public int FailedCount { get; init; }

    public IReadOnlyList<string> CorruptedEntries { get; init; } = Array.Empty<string>();

    public string SummaryMessage { get; init; } = string.Empty;

    public DateTimeOffset CheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
