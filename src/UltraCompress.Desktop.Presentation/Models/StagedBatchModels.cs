using System.Globalization;
using UltraCompress.Desktop.Presentation.Primitives;

namespace UltraCompress.Desktop.Presentation.Models;

public sealed class StagedFileItem
{
    public string FullPath { get; init; } = string.Empty;

    public string RelativePath { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string Extension { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public string ContentHashSha256 { get; init; } = string.Empty;

    public double ShannonEntropyBitsPerByte { get; init; }

    public double StrideCorrelationScore { get; init; }

    public FileContentCategory Category { get; init; }

    public string CategoryDisplayName { get; init; } = string.Empty;

    public string SelectedStrategy { get; init; } = string.Empty;

    public string StrategyJustification { get; init; } = string.Empty;

    public bool IsDuplicate { get; init; }

    public string? DuplicateOfRelativePath { get; init; }

    public long EstimatedCompressedBytes { get; init; }

    public double EstimatedSavingsPercent =>
        SizeBytes <= 0 ? 0 : Math.Clamp((1.0 - ((double)EstimatedCompressedBytes / SizeBytes)) * 100.0, 0.0, 100.0);

    public string SizeFormatted => ByteSizeFormatter.FormatBytes(SizeBytes);

    public string EstimatedAfterFormatted => ByteSizeFormatter.FormatBytes(EstimatedCompressedBytes);

    public string BeforeToAfterFormatted => $"{SizeFormatted} → {EstimatedAfterFormatted}";

    public string EntropyFormatted =>
        string.Create(CultureInfo.InvariantCulture, $"{ShannonEntropyBitsPerByte:F2} bpb");

    public string DuplicateBadgeText =>
        IsDuplicate ? $"Exact Duplicate → {DuplicateOfRelativePath}" : "Unique";

    public StagedFileItem WithUpdatedRouting(string strategy, string justification, long estimatedCompressedBytes) =>
        new()
        {
            FullPath = FullPath,
            RelativePath = RelativePath,
            FileName = FileName,
            Extension = Extension,
            SizeBytes = SizeBytes,
            ContentHashSha256 = ContentHashSha256,
            ShannonEntropyBitsPerByte = ShannonEntropyBitsPerByte,
            StrideCorrelationScore = StrideCorrelationScore,
            Category = Category,
            CategoryDisplayName = CategoryDisplayName,
            SelectedStrategy = strategy,
            StrategyJustification = justification,
            IsDuplicate = IsDuplicate,
            DuplicateOfRelativePath = DuplicateOfRelativePath,
            EstimatedCompressedBytes = estimatedCompressedBytes,
        };
}

public sealed class CategoryBreakdownItem
{
    public FileContentCategory Category { get; init; }

    public string CategoryDisplayName { get; init; } = string.Empty;

    public int FileCount { get; init; }

    public long TotalBytes { get; init; }

    public double AverageEntropyBitsPerByte { get; init; }

    public string ChosenStrategy { get; init; } = string.Empty;

    public string WhySelected { get; init; } = string.Empty;

    public string TotalBytesFormatted => ByteSizeFormatter.FormatBytes(TotalBytes);

    public string SummaryLine =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{CategoryDisplayName} ({FileCount} file(s), {TotalBytesFormatted}, avg {AverageEntropyBitsPerByte:F2} bpb) → {ChosenStrategy}");
}

public sealed class StrategyJustificationReport
{
    public int TotalFiles { get; init; }

    public int TotalFoldersScanned { get; init; }

    public long TotalOriginalBytes { get; init; }

    public long EstimatedArchiveBytes { get; init; }

    public long EstimatedSavedBytes => Math.Max(0, TotalOriginalBytes - EstimatedArchiveBytes);

    public double EstimatedSavingsPercent =>
        TotalOriginalBytes <= 0
            ? 0
            : Math.Clamp((1.0 - ((double)EstimatedArchiveBytes / TotalOriginalBytes)) * 100.0, 0.0, 100.0);

    public int DuplicateFilesCount { get; init; }

    public long DuplicateReclaimableBytes { get; init; }

    public int BypassedMediaCount { get; init; }

    public long BypassedMediaBytes { get; init; }

    public double AverageEntropyBitsPerByte { get; init; }

    public string HeadlineSummary { get; init; } = "Drop files or folders to inspect content categories, entropy, and routing.";

    public string RejectedAlternativesSummary { get; init; } = string.Empty;

    public IReadOnlyList<CategoryBreakdownItem> Categories { get; init; } = Array.Empty<CategoryBreakdownItem>();

    public string DuplicateSummaryBadge =>
        DuplicateFilesCount > 0
            ? $"{DuplicateFilesCount} exact duplicate(s) collapsed ({ByteSizeFormatter.FormatBytes(DuplicateReclaimableBytes)} saved via SHA-256)"
            : "0 exact duplicates detected";

    public static StrategyJustificationReport Empty { get; } = new();
}
