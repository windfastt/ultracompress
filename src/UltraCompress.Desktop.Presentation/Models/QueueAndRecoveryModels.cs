using UltraCompress.Desktop.Presentation.Primitives;

namespace UltraCompress.Desktop.Presentation.Models;

public sealed class ActionableErrorCard
{
    public ActionableErrorCode Code { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Explanation { get; init; } = string.Empty;

    public IReadOnlyList<string> RemediationSteps { get; init; } = Array.Empty<string>();

    public string? AffectedPath { get; init; }

    public string? TechnicalDetail { get; init; }

    public string PrimaryActionHint { get; init; } = "Review guidance and retry";

    public string FormattedRemediation =>
        string.Join(Environment.NewLine, RemediationSteps.Select((step, idx) => $"{idx + 1}. {step}"));
}

public sealed class QueueJobItem : ObservableObject
{
    private DesktopJobState _state;
    private double _progressPercent;
    private long _processedBytes;
    private double _speedMbPerSec;
    private TimeSpan? _eta;
    private int _priority;
    private ActionableErrorCard? _errorCard;
    private DateTimeOffset _updatedAtUtc = DateTimeOffset.UtcNow;

    public string JobId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string InputSummary { get; init; } = string.Empty;

    public IReadOnlyList<string> SourcePaths { get; init; } = Array.Empty<string>();

    public string OutputPath { get; init; } = string.Empty;

    public OperationKind OperationKind { get; init; } = OperationKind.LosslessCompression;

    public string ProfileName { get; init; } = string.Empty;

    public DestinationConflictMode ConflictMode { get; init; } = DestinationConflictMode.Rename;

    public long TotalBytes { get; init; }

    public string? TempStagingPath { get; set; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public int Priority
    {
        get => _priority;
        set
        {
            if (SetProperty(ref _priority, value))
            {
                RaisePropertyChanged(nameof(PriorityLabel));
            }
        }
    }

    public DesktopJobState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                _updatedAtUtc = DateTimeOffset.UtcNow;
                RaisePropertyChanged(nameof(StateBadgeText));
                RaisePropertyChanged(nameof(CanPause));
                RaisePropertyChanged(nameof(CanResume));
                RaisePropertyChanged(nameof(CanCancel));
                RaisePropertyChanged(nameof(IsPausedState));
                RaisePropertyChanged(nameof(IsErrorState));
                RaisePropertyChanged(nameof(MetricsSummaryLine));
            }
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 100.0);
            if (SetProperty(ref _progressPercent, clamped))
            {
                RaisePropertyChanged(nameof(ProgressFormatted));
                RaisePropertyChanged(nameof(MetricsSummaryLine));
            }
        }
    }

    public long ProcessedBytes
    {
        get => _processedBytes;
        set
        {
            if (SetProperty(ref _processedBytes, Math.Max(0, value)))
            {
                RaisePropertyChanged(nameof(MetricsSummaryLine));
            }
        }
    }

    public double SpeedMbPerSec
    {
        get => _speedMbPerSec;
        set
        {
            if (SetProperty(ref _speedMbPerSec, Math.Max(0, value)))
            {
                RaisePropertyChanged(nameof(SpeedFormatted));
                RaisePropertyChanged(nameof(MetricsSummaryLine));
            }
        }
    }

    public TimeSpan? Eta
    {
        get => _eta;
        set
        {
            if (SetProperty(ref _eta, value))
            {
                RaisePropertyChanged(nameof(EtaFormatted));
                RaisePropertyChanged(nameof(MetricsSummaryLine));
            }
        }
    }

    public ActionableErrorCard? ErrorCard
    {
        get => _errorCard;
        set
        {
            if (SetProperty(ref _errorCard, value))
            {
                RaisePropertyChanged(nameof(HasError));
                RaisePropertyChanged(nameof(ErrorCardMaxHeight));
                RaisePropertyChanged(nameof(ErrorCardOpacity));
                RaisePropertyChanged(nameof(ErrorTitle));
                RaisePropertyChanged(nameof(ErrorExplanation));
                RaisePropertyChanged(nameof(ErrorRemediation));
            }
        }
    }

    public DateTimeOffset UpdatedAtUtc => _updatedAtUtc;

    public string PriorityLabel => $"P{Priority}";

    public string StateBadgeText => State switch
    {
        DesktopJobState.Queued => "Queued",
        DesktopJobState.Running => "Compressing",
        DesktopJobState.Paused => "Paused",
        DesktopJobState.Completed => "Completed · Verified",
        DesktopJobState.Failed => "Action Needed",
        DesktopJobState.Cancelled => "Cancelled",
        DesktopJobState.InterruptedCrashRecovery => "Interrupted (Crash Recovery)",
        _ => State.ToString(),
    };

    public string ProgressFormatted => ByteSizeFormatter.FormatPercent(ProgressPercent);

    public string SpeedFormatted => ByteSizeFormatter.FormatSpeed(SpeedMbPerSec);

    public string EtaFormatted => ByteSizeFormatter.FormatEta(Eta);

    public string MetricsSummaryLine =>
        $"{ProgressFormatted} · {ByteSizeFormatter.FormatBytes(ProcessedBytes)} / {ByteSizeFormatter.FormatBytes(TotalBytes)} · {SpeedFormatted} · ETA {EtaFormatted}";

    public bool CanPause => State is DesktopJobState.Running or DesktopJobState.Queued;

    public bool CanResume => State is DesktopJobState.Paused or DesktopJobState.InterruptedCrashRecovery;

    public bool CanCancel => State is DesktopJobState.Queued or DesktopJobState.Running or DesktopJobState.Paused;

    public bool IsPausedState => State is DesktopJobState.Paused;

    public bool IsErrorState => State is DesktopJobState.Failed;

    public bool HasError => ErrorCard is not null;

    public double ErrorCardMaxHeight => ErrorCard is not null ? 360.0 : 0.0;

    public double ErrorCardOpacity => ErrorCard is not null ? 1.0 : 0.0;

    public string ErrorTitle => ErrorCard?.Title ?? string.Empty;

    public string ErrorExplanation => ErrorCard?.Explanation ?? string.Empty;

    public string ErrorRemediation => ErrorCard?.FormattedRemediation ?? string.Empty;
}

public sealed class InterruptedJobRecoveryItem
{
    public string JobId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<string> SourcePaths { get; init; } = Array.Empty<string>();

    public string OutputPath { get; init; } = string.Empty;

    public string TempPartFilePath { get; init; } = string.Empty;

    public long TempBytesOnDisk { get; init; }

    public long TotalBytes { get; init; }

    public string ProfileName { get; init; } = string.Empty;

    public double CheckpointPercent { get; init; }

    public DateTimeOffset InterruptedAtUtc { get; init; }

    public string RecoverySummary =>
        $"{Title} ({ProfileName}) interrupted at {ByteSizeFormatter.FormatPercent(CheckpointPercent)} · staging artifact: {ByteSizeFormatter.FormatBytes(TempBytesOnDisk)}";
}
