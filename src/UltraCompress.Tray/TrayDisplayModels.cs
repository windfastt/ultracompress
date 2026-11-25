namespace UltraCompress.Tray;

public sealed class StagedFileRow
{
    public string FullPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string EntropyFormatted { get; set; } = "";
    public string SelectedStrategy { get; set; } = "";
    public string DuplicateBadgeText { get; set; } = "";
    public string BeforeToAfterFormatted { get; set; } = "";
    public string StrategyJustification { get; set; } = "";
}

public sealed class CategoryBreakdownRow
{
    public string SummaryLine { get; set; } = "";
    public string WhySelected { get; set; } = "";
}

public sealed class ArchiveEntryRow
{
    public string EntryPath { get; set; } = "";
    public string IndentedDisplayName { get; set; } = "";
    public string OriginalSizeFormatted { get; set; } = "";
    public string CompressedSizeFormatted { get; set; } = "";
    public string SavingsFormatted { get; set; } = "";
    public string StrategyCodec { get; set; } = "";
    public string Sha256StatusDisplay { get; set; } = "";
}

public sealed class InterruptedRecoveryRow
{
    public string JobId { get; set; } = "";
    public string RecoverySummary { get; set; } = "";
}

public sealed class QueueJobRow
{
    public string JobId { get; set; } = "";
    public string JobTitle { get; set; } = "";
    public string InputSummary { get; set; } = "";
    public string PriorityLabel { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string StateBadgeText { get; set; } = "";
    public double ProgressPercent { get; set; }
    public string MetricsSummaryLine { get; set; } = "";
    public string ErrorDetailBlock { get; set; } = "";
}

public sealed class HistoryRecordRow
{
    public string RecordId { get; set; } = "";
    public string CompletedAtFormatted { get; set; } = "";
    public string ArchiveName { get; set; } = "";
    public string SourceSummary { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string StrategySummary { get; set; } = "";
    public string BeforeAfterComparisonText { get; set; } = "";
    public string VerificationBadge { get; set; } = "";
}
