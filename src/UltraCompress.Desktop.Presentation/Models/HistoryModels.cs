using System.Globalization;
using UltraCompress.Desktop.Presentation.Primitives;

namespace UltraCompress.Desktop.Presentation.Models;

public sealed class CompressionHistoryRecord
{
    public string RecordId { get; init; } = string.Empty;

    public DateTimeOffset CompletedAtUtc { get; init; }

    public string ArchiveName { get; init; } = string.Empty;

    public string SourceSummary { get; init; } = string.Empty;

    public string OutputPath { get; init; } = string.Empty;

    public string OutputFolder { get; init; } = string.Empty;

    public OperationKind OperationKind { get; init; } = OperationKind.LosslessCompression;

    public string ProfileName { get; init; } = string.Empty;

    public string StrategySummary { get; init; } = string.Empty;

    public long OriginalBytes { get; init; }

    public long OutputBytes { get; init; }

    public double DurationSeconds { get; init; }

    public double AverageSpeedMbPerSec { get; init; }

    public bool VerifiedSha256 { get; init; } = true;

    public long SavedBytes => Math.Max(0, OriginalBytes - OutputBytes);

    public double SavingsPercent =>
        OriginalBytes <= 0
            ? 0
            : Math.Clamp((1.0 - ((double)OutputBytes / OriginalBytes)) * 100.0, -99.9, 100.0);

    public double AfterProportionPercent =>
        OriginalBytes <= 0
            ? 100.0
            : Math.Clamp(((double)OutputBytes / OriginalBytes) * 100.0, 2.0, 100.0);

    public string CompletedAtFormatted =>
        CompletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string OriginalSizeFormatted => ByteSizeFormatter.FormatBytes(OriginalBytes);

    public string OutputSizeFormatted => ByteSizeFormatter.FormatBytes(OutputBytes);

    public string SavedBytesFormatted => ByteSizeFormatter.FormatBytes(SavedBytes);

    public string SavingsPercentFormatted => ByteSizeFormatter.FormatPercent(SavingsPercent);

    public string BeforeAfterComparisonText =>
        $"{OriginalSizeFormatted} → {OutputSizeFormatted} (−{SavedBytesFormatted}, {SavingsPercentFormatted})";

    public string VerificationBadge =>
        VerifiedSha256 ? "SHA-256 Bit-Exact" : "Mutated Output";
}

public sealed class HistoryKpiSummary
{
    public int TotalCompletedJobs { get; init; }

    public long CumulativeOriginalBytes { get; init; }

    public long CumulativeOutputBytes { get; init; }

    public long CumulativeBytesSaved => Math.Max(0, CumulativeOriginalBytes - CumulativeOutputBytes);

    public double AverageSavingsPercent =>
        CumulativeOriginalBytes <= 0
            ? 0
            : Math.Clamp((1.0 - ((double)CumulativeOutputBytes / CumulativeOriginalBytes)) * 100.0, 0.0, 100.0);

    public int BitExactVerifiedJobsCount { get; init; }

    public string CumulativeSavedFormatted => ByteSizeFormatter.FormatBytes(CumulativeBytesSaved);

    public string CumulativeOriginalFormatted => ByteSizeFormatter.FormatBytes(CumulativeOriginalBytes);

    public string CumulativeOutputFormatted => ByteSizeFormatter.FormatBytes(CumulativeOutputBytes);

    public string AverageSavingsFormatted => ByteSizeFormatter.FormatPercent(AverageSavingsPercent);

    public static HistoryKpiSummary Empty { get; } = new();
}
