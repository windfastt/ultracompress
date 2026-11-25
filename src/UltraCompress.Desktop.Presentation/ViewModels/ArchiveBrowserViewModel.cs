using System.Collections.ObjectModel;
using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.Primitives;
using UltraCompress.Desktop.Presentation.Services;

namespace UltraCompress.Desktop.Presentation.ViewModels;

public sealed class ArchiveBrowserViewModel : ObservableObject
{
    private readonly IDesktopArchiveService _archiveService;
    private List<ArchiveEntryNode> _allEntries = [];

    private string _currentArchivePath = string.Empty;
    private ArchiveManifestSummary? _manifestSummary;
    private string _searchQuery = string.Empty;
    private string _selectedCodecFilter = "All";
    private string _destinationFolder;
    private DestinationConflictMode _selectedConflictMode = DestinationConflictMode.Rename;
    private string _statusMessage = "Open a .ucx or .zip archive to browse contents, verify SHA-256 hashes, or selectively extract.";
    private ArchiveVerificationReport? _lastVerificationReport;
    private SelectiveExtractionResult? _lastExtractionResult;
    private ActionableErrorCard? _lastErrorCard;

    public ArchiveBrowserViewModel(
        IDesktopArchiveService? archiveService = null,
        string? defaultDestinationFolder = null)
    {
        _archiveService = archiveService ?? new UcxArchiveService();
        _destinationFolder = string.IsNullOrWhiteSpace(defaultDestinationFolder)
            ? GetDefaultExtractFolder()
            : Path.GetFullPath(defaultDestinationFolder);
        AvailableCodecFilters.Add("All");
    }

    public ObservableCollection<ArchiveEntryNode> FilteredEntries { get; } = [];

    public ObservableCollection<string> AvailableCodecFilters { get; } = [];

    public IReadOnlyList<DestinationConflictMode> ConflictModes { get; } =
    [
        DestinationConflictMode.Rename,
        DestinationConflictMode.Overwrite,
        DestinationConflictMode.Skip,
    ];

    public IReadOnlyList<ArchiveEntryNode> AllEntries => _allEntries;

    public string CurrentArchivePath
    {
        get => _currentArchivePath;
        set => SetProperty(ref _currentArchivePath, value ?? string.Empty);
    }

    public ArchiveManifestSummary? ManifestSummary
    {
        get => _manifestSummary;
        private set
        {
            if (SetProperty(ref _manifestSummary, value))
            {
                RaisePropertyChanged(nameof(HasLoadedArchive));
                RaisePropertyChanged(nameof(ManifestSummaryLine));
                RaisePropertyChanged(nameof(CanExtractAll));
                RaisePropertyChanged(nameof(CanVerifyArchive));
            }
        }
    }

    public bool HasLoadedArchive => ManifestSummary is not null;

    public string ManifestSummaryLine =>
        ManifestSummary?.SummaryLine ?? "No archive loaded.";

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public string SelectedCodecFilter
    {
        get => _selectedCodecFilter;
        set
        {
            if (SetProperty(ref _selectedCodecFilter, string.IsNullOrWhiteSpace(value) ? "All" : value))
            {
                ApplyFilter();
            }
        }
    }

    public string DestinationFolder
    {
        get => _destinationFolder;
        set => SetProperty(ref _destinationFolder, value ?? string.Empty);
    }

    public DestinationConflictMode SelectedConflictMode
    {
        get => _selectedConflictMode;
        set => SetProperty(ref _selectedConflictMode, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ArchiveVerificationReport? LastVerificationReport
    {
        get => _lastVerificationReport;
        private set => SetProperty(ref _lastVerificationReport, value);
    }

    public SelectiveExtractionResult? LastExtractionResult
    {
        get => _lastExtractionResult;
        private set => SetProperty(ref _lastExtractionResult, value);
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

    public int SelectedFileCount => _allEntries.Count(e => !e.IsDirectory && e.IsSelected);

    public long SelectedOriginalBytes => _allEntries.Where(e => !e.IsDirectory && e.IsSelected).Sum(e => e.OriginalSizeBytes);

    public string SelectionSummaryText =>
        SelectedFileCount == 0
            ? "0 files selected"
            : $"{SelectedFileCount} file(s) selected ({ByteSizeFormatter.FormatBytes(SelectedOriginalBytes)})";

    public bool CanExtractSelected => HasLoadedArchive && SelectedFileCount > 0;

    public bool CanExtractAll => HasLoadedArchive && _allEntries.Any(e => !e.IsDirectory);

    public bool CanVerifyArchive => HasLoadedArchive;

    public async Task<bool> OpenArchiveAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        LastErrorCard = null;
        LastVerificationReport = null;
        LastExtractionResult = null;

        try
        {
            var (summary, nodes) = await _archiveService.InspectArchiveAsync(archivePath, cancellationToken).ConfigureAwait(false);
            CurrentArchivePath = summary.ArchivePath;
            _allEntries = nodes.ToList();
            foreach (var node in _allEntries)
            {
                node.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ArchiveEntryNode.IsSelected))
                    {
                        RaiseSelectionStateChanged();
                    }
                };
            }

            ManifestSummary = summary;
            RebuildCodecFilters();
            ApplyFilter();

            var archiveDir = Path.GetDirectoryName(summary.ArchivePath);
            if (!string.IsNullOrWhiteSpace(archiveDir) && string.IsNullOrWhiteSpace(DestinationFolder))
            {
                DestinationFolder = Path.Combine(archiveDir, Path.GetFileNameWithoutExtension(summary.ArchiveFileName) + "_extracted");
            }

            StatusMessage = $"Loaded {summary.SummaryLine}";
            RaiseSelectionStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            LastErrorCard = ActionableErrorPresenter.FromException(ex, archivePath);
            StatusMessage = LastErrorCard.Title;
            return false;
        }
    }

    public void SetNodeSelection(ArchiveEntryNode node, bool isSelected)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.IsSelected = isSelected;

        if (node.IsDirectory)
        {
            var prefix = node.EntryPath.TrimEnd('/') + "/";
            foreach (var descendant in _allEntries.Where(n => n.EntryPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                descendant.IsSelected = isSelected;
            }
        }

        RaiseSelectionStateChanged();
    }

    public void SelectAllVisible()
    {
        foreach (var node in FilteredEntries)
        {
            node.IsSelected = true;
        }

        RaiseSelectionStateChanged();
    }

    public void ClearSelection()
    {
        foreach (var node in _allEntries)
        {
            node.IsSelected = false;
        }

        RaiseSelectionStateChanged();
    }

    public async Task<ArchiveVerificationReport?> VerifyArchiveAsync(CancellationToken cancellationToken = default)
    {
        if (!HasLoadedArchive || string.IsNullOrWhiteSpace(CurrentArchivePath))
        {
            return null;
        }

        LastErrorCard = null;
        try
        {
            var report = await _archiveService.VerifyArchiveAsync(CurrentArchivePath, _allEntries, cancellationToken).ConfigureAwait(false);
            LastVerificationReport = report;
            StatusMessage = report.SummaryMessage;
            if (!report.IsAllValid)
            {
                LastErrorCard = ActionableErrorPresenter.FromCode(
                    ActionableErrorCode.Sha256IntegrityMismatch,
                    CurrentArchivePath,
                    string.Join(", ", report.CorruptedEntries));
            }

            return report;
        }
        catch (Exception ex)
        {
            LastErrorCard = ActionableErrorPresenter.FromException(ex, CurrentArchivePath);
            StatusMessage = LastErrorCard.Title;
            return null;
        }
    }

    public async Task<SelectiveExtractionResult?> ExtractSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (!HasLoadedArchive)
        {
            return null;
        }

        var selectedPaths = ResolveSelectedFileEntryPaths();
        if (selectedPaths.Count == 0)
        {
            StatusMessage = "Check one or more files or folders before running Extract Selected.";
            return null;
        }

        return await RunExtractionCoreAsync(selectedPaths, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SelectiveExtractionResult?> ExtractAllAsync(CancellationToken cancellationToken = default)
    {
        if (!HasLoadedArchive)
        {
            return null;
        }

        return await RunExtractionCoreAsync(null, cancellationToken).ConfigureAwait(false);
    }

    public HashSet<string> ResolveSelectedFileEntryPaths()
    {
        var selectedDirs = _allEntries
            .Where(n => n.IsDirectory && n.IsSelected)
            .Select(n => n.EntryPath.TrimEnd('/') + "/")
            .ToList();

        var selectedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fileNode in _allEntries.Where(n => !n.IsDirectory))
        {
            if (fileNode.IsSelected
                || selectedDirs.Any(prefix => fileNode.EntryPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                selectedFiles.Add(fileNode.EntryPath);
            }
        }

        return selectedFiles;
    }

    public void DismissError() => LastErrorCard = null;

    private async Task<SelectiveExtractionResult> RunExtractionCoreAsync(
        IReadOnlySet<string>? selectedPaths,
        CancellationToken cancellationToken)
    {
        LastErrorCard = null;
        try
        {
            var result = await _archiveService.ExtractEntriesAsync(
                CurrentArchivePath,
                selectedPaths,
                DestinationFolder,
                SelectedConflictMode,
                cancellationToken).ConfigureAwait(false);

            LastExtractionResult = result;
            StatusMessage = result.SummaryMessage;
            if (!result.Success && result.ErrorCard is not null)
            {
                LastErrorCard = result.ErrorCard;
            }

            return result;
        }
        catch (Exception ex)
        {
            var error = ActionableErrorPresenter.FromException(ex, CurrentArchivePath);
            LastErrorCard = error;
            StatusMessage = error.Title;
            var failed = new SelectiveExtractionResult
            {
                Success = false,
                DestinationFolder = DestinationFolder,
                ConflictModeApplied = SelectedConflictMode,
                ErrorCard = error,
                SummaryMessage = error.Title,
            };
            LastExtractionResult = failed;
            return failed;
        }
    }

    private void RebuildCodecFilters()
    {
        var codecs = _allEntries
            .Where(e => !e.IsDirectory && !string.IsNullOrWhiteSpace(e.StrategyCodec))
            .Select(e => e.StrategyCodec)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AvailableCodecFilters.Clear();
        AvailableCodecFilters.Add("All");
        foreach (var codec in codecs)
        {
            AvailableCodecFilters.Add(codec);
        }

        _selectedCodecFilter = "All";
        RaisePropertyChanged(nameof(SelectedCodecFilter));
    }

    private void ApplyFilter()
    {
        var query = _searchQuery.Trim();
        var filterCodec = _selectedCodecFilter;

        FilteredEntries.Clear();
        foreach (var node in _allEntries)
        {
            if (!string.Equals(filterCodec, "All", StringComparison.OrdinalIgnoreCase))
            {
                if (node.IsDirectory || !string.Equals(node.StrategyCodec, filterCodec, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (!string.IsNullOrEmpty(query))
            {
                var matchesPath = node.EntryPath.Contains(query, StringComparison.OrdinalIgnoreCase);
                var matchesCodec = node.StrategyCodec.Contains(query, StringComparison.OrdinalIgnoreCase);
                var matchesSha = node.Sha256StatusDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
                                 || node.ExpectedSha256.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (!matchesPath && !matchesCodec && !matchesSha)
                {
                    continue;
                }
            }

            FilteredEntries.Add(node);
        }
    }

    private void RaiseSelectionStateChanged()
    {
        RaisePropertyChanged(nameof(SelectedFileCount));
        RaisePropertyChanged(nameof(SelectedOriginalBytes));
        RaisePropertyChanged(nameof(SelectionSummaryText));
        RaisePropertyChanged(nameof(CanExtractSelected));
    }

    private static string GetDefaultExtractFolder()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(docs)
            ? Path.Combine(Path.GetTempPath(), "UltraCompress_Extracted")
            : Path.Combine(docs, "UltraCompress Extracted");
    }
}
