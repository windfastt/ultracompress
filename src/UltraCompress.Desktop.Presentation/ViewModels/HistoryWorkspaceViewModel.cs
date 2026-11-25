using System.Collections.ObjectModel;
using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.Primitives;
using UltraCompress.Desktop.Presentation.Services;

namespace UltraCompress.Desktop.Presentation.ViewModels;

public sealed class HistoryWorkspaceViewModel : ObservableObject
{
    private readonly DesktopJournalStore _journalStore;
    private List<CompressionHistoryRecord> _allRecords = [];
    private string _searchQuery = string.Empty;
    private string _selectedProfileFilter = "All";
    private CompressionHistoryRecord? _selectedRecord;
    private HistoryKpiSummary _kpiSummary = HistoryKpiSummary.Empty;

    public HistoryWorkspaceViewModel(DesktopJournalStore? journalStore = null)
    {
        _journalStore = journalStore ?? new DesktopJournalStore();
        Reload();
    }

    public event Action<string>? OpenFolderRequested;

    public event Action<string>? ExtractArchiveRequested;

    public ObservableCollection<CompressionHistoryRecord> FilteredRecords { get; } = [];

    public IReadOnlyList<string> ProfileFilters { get; } =
    [
        "All",
        "Instant",
        "Balanced",
        "Archive",
        "Tiny",
        "Laboratory",
        "Optimization Studio",
    ];

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value ?? string.Empty))
            {
                ApplyFilterAndKpis();
            }
        }
    }

    public string SelectedProfileFilter
    {
        get => _selectedProfileFilter;
        set
        {
            if (SetProperty(ref _selectedProfileFilter, string.IsNullOrWhiteSpace(value) ? "All" : value))
            {
                ApplyFilterAndKpis();
            }
        }
    }

    public CompressionHistoryRecord? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (SetProperty(ref _selectedRecord, value))
            {
                RaisePropertyChanged(nameof(HasSelectedRecord));
            }
        }
    }

    public bool HasSelectedRecord => SelectedRecord is not null;

    public HistoryKpiSummary KpiSummary
    {
        get => _kpiSummary;
        private set
        {
            if (SetProperty(ref _kpiSummary, value))
            {
                RaisePropertyChanged(nameof(CumulativeSpaceSavedHeadline));
                RaisePropertyChanged(nameof(TotalJobsHeadline));
                RaisePropertyChanged(nameof(AverageSavingsHeadline));
                RaisePropertyChanged(nameof(VerifiedCountHeadline));
            }
        }
    }

    public string CumulativeSpaceSavedHeadline => KpiSummary.CumulativeSavedFormatted;

    public string TotalJobsHeadline => $"{KpiSummary.TotalCompletedJobs} job(s)";

    public string AverageSavingsHeadline => KpiSummary.AverageSavingsFormatted;

    public string VerifiedCountHeadline => $"{KpiSummary.BitExactVerifiedJobsCount} bit-exact SHA-256";

    public void Reload()
    {
        _allRecords = _journalStore.LoadHistory().ToList();
        ApplyFilterAndKpis();
    }

    public void RecordCompletedJob(CompressionHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _journalStore.AppendHistoryRecord(record);
        _allRecords.RemoveAll(r => string.Equals(r.RecordId, record.RecordId, StringComparison.Ordinal));
        _allRecords.Insert(0, record);
        ApplyFilterAndKpis();
        SelectedRecord ??= record;
    }

    public void OpenFolderForRecord(CompressionHistoryRecord? record)
    {
        var target = record ?? SelectedRecord;
        if (target is null)
        {
            return;
        }

        OpenFolderRequested?.Invoke(target.OutputPath);
    }

    public void ExtractOrBrowseRecord(CompressionHistoryRecord? record)
    {
        var target = record ?? SelectedRecord;
        if (target is null)
        {
            return;
        }

        ExtractArchiveRequested?.Invoke(target.OutputPath);
    }

    private void ApplyFilterAndKpis()
    {
        KpiSummary = new HistoryKpiSummary
        {
            TotalCompletedJobs = _allRecords.Count,
            CumulativeOriginalBytes = _allRecords.Sum(r => r.OriginalBytes),
            CumulativeOutputBytes = _allRecords.Sum(r => r.OutputBytes),
            BitExactVerifiedJobsCount = _allRecords.Count(r => r.VerifiedSha256),
        };

        var query = _searchQuery.Trim();
        var profileFilter = _selectedProfileFilter;

        FilteredRecords.Clear();
        foreach (var record in _allRecords)
        {
            if (!string.Equals(profileFilter, "All", StringComparison.OrdinalIgnoreCase))
            {
                var isStudio = record.OperationKind != OperationKind.LosslessCompression;
                if (string.Equals(profileFilter, "Optimization Studio", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isStudio)
                    {
                        continue;
                    }
                }
                else if (!string.Equals(record.ProfileName, profileFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (!string.IsNullOrEmpty(query))
            {
                var hit = record.ArchiveName.Contains(query, StringComparison.OrdinalIgnoreCase)
                          || record.SourceSummary.Contains(query, StringComparison.OrdinalIgnoreCase)
                          || record.ProfileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                          || record.StrategySummary.Contains(query, StringComparison.OrdinalIgnoreCase)
                          || record.OutputPath.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (!hit)
                {
                    continue;
                }
            }

            FilteredRecords.Add(record);
        }

        if (SelectedRecord is null && FilteredRecords.Count > 0)
        {
            SelectedRecord = FilteredRecords[0];
        }
    }
}
