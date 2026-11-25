using UltraCompress.Core.Ipc;
using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.Primitives;
using UltraCompress.Desktop.Presentation.Services;

namespace UltraCompress.Desktop.Presentation.ViewModels;

public sealed class MainShellViewModel : ObservableObject
{
    private DesktopWorkspaceTab _activeTab = DesktopWorkspaceTab.CompressBatch;

    public MainShellViewModel(
        IEnginePipeClient? enginePipeClient = null,
        string? stateDirectory = null,
        IDiskSpaceProvider? diskSpaceProvider = null)
    {
        var journalStore = new DesktopJournalStore(stateDirectory);
        var destinationManager = new DestinationManagerService(diskSpaceProvider);
        var archiveService = new UcxArchiveService(destinationManager);
        var batchAnalyzer = new BatchContentAnalyzer();
        var bridge = enginePipeClient is null ? null : new EnginePipeClientBridge(enginePipeClient);

        CompressWorkspace = new CompressWorkspaceViewModel(batchAnalyzer, destinationManager);
        ArchiveBrowser = new ArchiveBrowserViewModel(archiveService);
        QueueRecovery = new QueueRecoveryViewModel(journalStore, archiveService, batchAnalyzer, bridge);
        HistoryWorkspace = new HistoryWorkspaceViewModel(journalStore);
        OptimizationStudio = new OptimizationStudioViewModel();

        QueueRecovery.JobCompletedHistoryProduced += record => HistoryWorkspace.RecordCompletedJob(record);
        OptimizationStudio.OptimizationHistoryProduced += record => HistoryWorkspace.RecordCompletedJob(record);
        HistoryWorkspace.ExtractArchiveRequested += archivePath => _ = OpenArchiveInBrowserAsync(archivePath);
    }

    public CompressWorkspaceViewModel CompressWorkspace { get; }

    public ArchiveBrowserViewModel ArchiveBrowser { get; }

    public QueueRecoveryViewModel QueueRecovery { get; }

    public HistoryWorkspaceViewModel HistoryWorkspace { get; }

    public OptimizationStudioViewModel OptimizationStudio { get; }

    public DesktopWorkspaceTab ActiveTab
    {
        get => _activeTab;
        set => SetProperty(ref _activeTab, value);
    }

    public string TrayTooltipText
    {
        get
        {
            var active = QueueRecovery.ActiveJobsCount;
            var queued = QueueRecovery.QueuedJobsCount;
            var recovery = QueueRecovery.InterruptedJobs.Count;
            var paused = QueueRecovery.IsQueuePaused ? " (Paused)" : string.Empty;
            if (recovery > 0)
            {
                return $"UltraCompress Desktop — {recovery} crash-recovery job(s) pending · active {active}, queued {queued}{paused}";
            }

            return $"UltraCompress Desktop — active {active}, queued {queued}{paused}";
        }
    }

    public QueueJobItem? SubmitCurrentBatchToQueue(bool switchToQueueTab = true)
    {
        if (!CompressWorkspace.TryValidateReadyForCompression(out var preCheck, out _))
        {
            return null;
        }

        var jobTitle = Path.GetFileName(preCheck.ResolvedOutputPath);
        var job = QueueRecovery.EnqueueBatchJob(
            jobTitle,
            CompressWorkspace.StagedFiles.ToList(),
            preCheck.ResolvedOutputPath,
            CompressWorkspace.SelectedProfile,
            CompressWorkspace.SelectedConflictMode);

        if (switchToQueueTab)
        {
            ActiveTab = DesktopWorkspaceTab.QueueAndRecovery;
        }

        RaisePropertyChanged(nameof(TrayTooltipText));
        return job;
    }

    public async Task<bool> OpenArchiveInBrowserAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        ActiveTab = DesktopWorkspaceTab.ArchiveBrowser;
        return await ArchiveBrowser.OpenArchiveAsync(archivePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshEngineAndTrayStateAsync(CancellationToken cancellationToken = default)
    {
        await QueueRecovery.RefreshEngineConnectionAsync(cancellationToken).ConfigureAwait(false);
        RaisePropertyChanged(nameof(TrayTooltipText));
    }
}
