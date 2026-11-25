using System.Collections.ObjectModel;
using System.Globalization;
using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.Primitives;
using UltraCompress.Desktop.Presentation.Services;

namespace UltraCompress.Desktop.Presentation.ViewModels;

public sealed class CompressWorkspaceViewModel : ObservableObject
{
    private const double MaxComparisonBarWidth = 320.0;

    private readonly BatchContentAnalyzer _analyzer;
    private readonly DestinationManagerService _destinationManager;
    private readonly List<string> _rawRootInputs = [];
    private int _foldersScanned;

    private LosslessProfileOption _selectedProfileOption;
    private DestinationConflictMode _selectedConflictMode = DestinationConflictMode.Rename;
    private string _destinationFolder;
    private string _archiveFileName = "Archive.ucx";
    private bool _archiveFileNameCustomized;
    private StrategyJustificationReport _strategyReport = StrategyJustificationReport.Empty;
    private DiskSpacePreCheckResult _diskPreCheck;
    private ActionableErrorCard? _lastErrorCard;
    private string _workspaceStatusMessage = "Drop files or folders into the staging zone to inspect entropy, duplicates, and strategy routing.";

    public CompressWorkspaceViewModel(
        BatchContentAnalyzer? analyzer = null,
        DestinationManagerService? destinationManager = null,
        string? defaultDestinationFolder = null)
    {
        _analyzer = analyzer ?? new BatchContentAnalyzer();
        _destinationManager = destinationManager ?? new DestinationManagerService();
        _selectedProfileOption = LosslessProfileCatalog.Get(DesktopLosslessProfile.Balanced);
        _destinationFolder = string.IsNullOrWhiteSpace(defaultDestinationFolder)
            ? GetInitialDestinationFolder()
            : Path.GetFullPath(defaultDestinationFolder);

        _diskPreCheck = _destinationManager.EvaluatePreCheck(
            _destinationFolder,
            _archiveFileName,
            0,
            _selectedConflictMode);
    }

    public ObservableCollection<StagedFileItem> StagedFiles { get; } = [];

    public ObservableCollection<CategoryBreakdownItem> CategoryBreakdowns { get; } = [];

    public IReadOnlyList<LosslessProfileOption> Profiles => LosslessProfileCatalog.All;

    public IReadOnlyList<DestinationConflictMode> ConflictModes { get; } =
    [
        DestinationConflictMode.Rename,
        DestinationConflictMode.Overwrite,
        DestinationConflictMode.Skip,
    ];

    public OperationKind OperationKind => OperationKind.LosslessCompression;

    public string SlaGuaranteeBadge => LosslessProfileOption.CanonicalLosslessSlaBadge;

    public LosslessProfileOption SelectedProfileOption
    {
        get => _selectedProfileOption;
        set
        {
            if (value is not null && SetProperty(ref _selectedProfileOption, value))
            {
                RaisePropertyChanged(nameof(SelectedProfile));
                RaisePropertyChanged(nameof(ProfileSlaSummary));
                RecalculateForSelectedProfile();
            }
        }
    }

    public DesktopLosslessProfile SelectedProfile
    {
        get => _selectedProfileOption.Profile;
        set => SelectedProfileOption = LosslessProfileCatalog.Get(value);
    }

    public string ProfileSlaSummary => _selectedProfileOption.SlaMetricsLine;

    public DestinationConflictMode SelectedConflictMode
    {
        get => _selectedConflictMode;
        set
        {
            if (SetProperty(ref _selectedConflictMode, value))
            {
                RefreshDestinationPreCheck();
            }
        }
    }

    public string DestinationFolder
    {
        get => _destinationFolder;
        set
        {
            if (SetProperty(ref _destinationFolder, value ?? string.Empty))
            {
                RefreshDestinationPreCheck();
            }
        }
    }

    public string ArchiveFileName
    {
        get => _archiveFileName;
        set
        {
            if (SetProperty(ref _archiveFileName, value ?? string.Empty))
            {
                _archiveFileNameCustomized = !string.IsNullOrWhiteSpace(value);
                RefreshDestinationPreCheck();
            }
        }
    }

    public StrategyJustificationReport StrategyReport
    {
        get => _strategyReport;
        private set
        {
            if (SetProperty(ref _strategyReport, value))
            {
                RaiseComparisonProperties();
            }
        }
    }

    public DiskSpacePreCheckResult DiskPreCheck
    {
        get => _diskPreCheck;
        private set
        {
            if (SetProperty(ref _diskPreCheck, value))
            {
                RaisePropertyChanged(nameof(CanCompressBatch));
                RaisePropertyChanged(nameof(DiskPreCheckSummary));
                RaisePropertyChanged(nameof(HasSufficientDiskSpace));
            }
        }
    }

    public ActionableErrorCard? LastErrorCard
    {
        get => _lastErrorCard;
        private set
        {
            if (SetProperty(ref _lastErrorCard, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => LastErrorCard is not null;

    public string WorkspaceStatusMessage
    {
        get => _workspaceStatusMessage;
        private set => SetProperty(ref _workspaceStatusMessage, value);
    }

    public bool HasStagedFiles => StagedFiles.Count > 0;

    public bool CanCompressBatch => HasStagedFiles && DiskPreCheck.CanProceed;

    public bool HasSufficientDiskSpace => DiskPreCheck.HasSufficientSpace;

    public string DiskPreCheckSummary => DiskPreCheck.StatusMessage;

    public string OriginalSizeFormatted => ByteSizeFormatter.FormatBytes(StrategyReport.TotalOriginalBytes);

    public string EstimatedAfterSizeFormatted => ByteSizeFormatter.FormatBytes(StrategyReport.EstimatedArchiveBytes);

    public string EstimatedSavingsFormatted =>
        StrategyReport.TotalOriginalBytes <= 0
            ? "0 B (0.0%)"
            : $"{ByteSizeFormatter.FormatBytes(StrategyReport.EstimatedSavedBytes)} ({ByteSizeFormatter.FormatPercent(StrategyReport.EstimatedSavingsPercent)} saved)";

    public string BeforeAfterComparisonHeadline =>
        StrategyReport.TotalOriginalBytes <= 0
            ? "Stage files to preview Before vs. Estimated After archive footprint."
            : $"Before: {OriginalSizeFormatted}  →  Estimated After ({SelectedProfileOption.DisplayName}): {EstimatedAfterSizeFormatted}  ({ByteSizeFormatter.FormatPercent(StrategyReport.EstimatedSavingsPercent)} reduction)";

    public double BeforeBarWidth => StrategyReport.TotalOriginalBytes > 0 ? MaxComparisonBarWidth : 0.0;

    public double EstimatedAfterBarWidth
    {
        get
        {
            if (StrategyReport.TotalOriginalBytes <= 0)
            {
                return 0.0;
            }

            var ratio = (double)StrategyReport.EstimatedArchiveBytes / StrategyReport.TotalOriginalBytes;
            return Math.Clamp(ratio * MaxComparisonBarWidth, 12.0, MaxComparisonBarWidth);
        }
    }

    public string DuplicateBadgeText => StrategyReport.DuplicateSummaryBadge;

    public async Task StageDroppedPathsAsync(
        IEnumerable<string> droppedPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(droppedPaths);
        LastErrorCard = null;

        var incoming = droppedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList();

        if (incoming.Count == 0)
        {
            return;
        }

        foreach (var path in incoming)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                LastErrorCard = ActionableErrorPresenter.FromCode(ActionableErrorCode.SourceFileNotFound, path);
                WorkspaceStatusMessage = LastErrorCard.Title;
                return;
            }
        }

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var path in incoming)
        {
            var full = Path.GetFullPath(path);
            if (!_rawRootInputs.Contains(full, pathComparer))
            {
                _rawRootInputs.Add(full);
            }
        }

        if (!_archiveFileNameCustomized && _rawRootInputs.Count > 0)
        {
            var firstRoot = _rawRootInputs[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var baseName = Directory.Exists(firstRoot)
                ? Path.GetFileName(firstRoot)
                : Path.GetFileNameWithoutExtension(firstRoot);
            if (!string.IsNullOrWhiteSpace(baseName))
            {
                _archiveFileName = $"{baseName}.ucx";
                RaisePropertyChanged(nameof(ArchiveFileName));
            }

            var parentDir = Path.GetDirectoryName(firstRoot);
            if (!string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir) && string.IsNullOrWhiteSpace(_destinationFolder))
            {
                _destinationFolder = parentDir;
                RaisePropertyChanged(nameof(DestinationFolder));
            }
        }

        await RebuildBatchAnalysisAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveStagedFileAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _rawRootInputs.RemoveAll(p => string.Equals(p, fullPath, comparison));

        var remaining = StagedFiles
            .Where(f => !string.Equals(f.FullPath, fullPath, comparison))
            .ToList();

        ApplyAnalysisResult(remaining, _foldersScanned, _analyzer.RecalculateForProfile(remaining, _foldersScanned, SelectedProfile).Report);
        await Task.CompletedTask;
    }

    public void ClearBatch()
    {
        _rawRootInputs.Clear();
        _foldersScanned = 0;
        _archiveFileNameCustomized = false;
        StagedFiles.Clear();
        CategoryBreakdowns.Clear();
        LastErrorCard = null;
        StrategyReport = StrategyJustificationReport.Empty;
        WorkspaceStatusMessage = "Batch cleared. Ready for new files or folders.";
        RaisePropertyChanged(nameof(HasStagedFiles));
        RefreshDestinationPreCheck();
    }

    public bool TryValidateReadyForCompression(out DiskSpacePreCheckResult preCheck, out ActionableErrorCard? errorCard)
    {
        RefreshDestinationPreCheck();
        preCheck = DiskPreCheck;
        errorCard = null;

        if (!HasStagedFiles)
        {
            errorCard = ActionableErrorPresenter.FromCode(ActionableErrorCode.SourceFileNotFound, null, "No staged files in batch.");
            LastErrorCard = errorCard;
            return false;
        }

        if (!preCheck.IsDestinationWritable)
        {
            errorCard = ActionableErrorPresenter.FromCode(ActionableErrorCode.AccessDeniedPermission, DestinationFolder);
            LastErrorCard = errorCard;
            return false;
        }

        if (!preCheck.HasSufficientSpace)
        {
            errorCard = ActionableErrorPresenter.ForInsufficientDiskSpace(
                preCheck.DestinationFolder,
                preCheck.AvailableFreeBytes,
                preCheck.RequiredBytesWithHeadroom);
            LastErrorCard = errorCard;
            return false;
        }

        LastErrorCard = null;
        return true;
    }

    public void DismissError() => LastErrorCard = null;

    private async Task RebuildBatchAnalysisAsync(CancellationToken cancellationToken)
    {
        _foldersScanned = _rawRootInputs.Count(Directory.Exists);
        var (items, report) = await _analyzer.AnalyzePathsAsync(_rawRootInputs, SelectedProfile, cancellationToken).ConfigureAwait(false);
        ApplyAnalysisResult(items, _foldersScanned, report);
    }

    private void RecalculateForSelectedProfile()
    {
        if (StagedFiles.Count == 0)
        {
            RefreshDestinationPreCheck();
            return;
        }

        var (updatedItems, report) = _analyzer.RecalculateForProfile(StagedFiles.ToList(), _foldersScanned, SelectedProfile);
        ApplyAnalysisResult(updatedItems, _foldersScanned, report);
    }

    private void ApplyAnalysisResult(
        IReadOnlyList<StagedFileItem> items,
        int foldersScanned,
        StrategyJustificationReport report)
    {
        StagedFiles.Clear();
        foreach (var item in items)
        {
            StagedFiles.Add(item);
        }

        CategoryBreakdowns.Clear();
        foreach (var category in report.Categories)
        {
            CategoryBreakdowns.Add(category);
        }

        _foldersScanned = foldersScanned;
        StrategyReport = report;
        WorkspaceStatusMessage = items.Count == 0
            ? "Selected folders did not contain any files."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Staged {items.Count} file(s) across {Math.Max(1, report.Categories.Count)} content class(es) · {report.DuplicateSummaryBadge}");

        RaisePropertyChanged(nameof(HasStagedFiles));
        RefreshDestinationPreCheck();
    }

    public void RefreshDestinationPreCheck()
    {
        DiskPreCheck = _destinationManager.EvaluatePreCheck(
            _destinationFolder,
            _archiveFileName,
            StrategyReport.EstimatedArchiveBytes,
            _selectedConflictMode);
    }

    private void RaiseComparisonProperties()
    {
        RaisePropertyChanged(nameof(OriginalSizeFormatted));
        RaisePropertyChanged(nameof(EstimatedAfterSizeFormatted));
        RaisePropertyChanged(nameof(EstimatedSavingsFormatted));
        RaisePropertyChanged(nameof(BeforeAfterComparisonHeadline));
        RaisePropertyChanged(nameof(BeforeBarWidth));
        RaisePropertyChanged(nameof(EstimatedAfterBarWidth));
        RaisePropertyChanged(nameof(DuplicateBadgeText));
    }

    private static string GetInitialDestinationFolder()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(docs) ? Path.GetTempPath() : docs;
    }
}
