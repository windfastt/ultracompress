using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using UltraCompress.Core.Ipc;
using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.Primitives;
using UltraCompress.Desktop.Presentation.Services;

namespace UltraCompress.Desktop.Presentation.ViewModels;

public sealed class QueueRecoveryViewModel : ObservableObject
{
    private readonly DesktopJournalStore _journalStore;
    private readonly IDesktopArchiveService _archiveService;
    private readonly BatchContentAnalyzer _analyzer;
    private readonly IDesktopEngineBridge? _engineBridge;
    private readonly ConcurrentDictionary<string, JobExecutionControl> _controls = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _schedulerGate = new(1, 1);

    private bool _isQueuePaused;
    private bool _isEngineConnected;
    private string _engineStatusSummary = "Local Desktop Engine Ready";
    private bool _autoExecuteEnqueuedJobs = true;

    public QueueRecoveryViewModel(
        DesktopJournalStore? journalStore = null,
        IDesktopArchiveService? archiveService = null,
        BatchContentAnalyzer? analyzer = null,
        IDesktopEngineBridge? engineBridge = null,
        bool loadJournalOnInit = true)
    {
        _journalStore = journalStore ?? new DesktopJournalStore();
        _archiveService = archiveService ?? new UcxArchiveService();
        _analyzer = analyzer ?? new BatchContentAnalyzer();
        _engineBridge = engineBridge;

        if (loadJournalOnInit)
        {
            LoadCrashRecoveryFromJournal();
        }
    }

    public event Action<CompressionHistoryRecord>? JobCompletedHistoryProduced;

    public ObservableCollection<QueueJobItem> Jobs { get; } = [];

    public ObservableCollection<InterruptedJobRecoveryItem> InterruptedJobs { get; } = [];

    public bool AutoExecuteEnqueuedJobs
    {
        get => _autoExecuteEnqueuedJobs;
        set => SetProperty(ref _autoExecuteEnqueuedJobs, value);
    }

    public bool IsQueuePaused
    {
        get => _isQueuePaused;
        private set
        {
            if (SetProperty(ref _isQueuePaused, value))
            {
                RaiseQueueCounters();
            }
        }
    }

    public bool IsEngineConnected
    {
        get => _isEngineConnected;
        private set => SetProperty(ref _isEngineConnected, value);
    }

    public string EngineStatusSummary
    {
        get => _engineStatusSummary;
        private set => SetProperty(ref _engineStatusSummary, value);
    }

    public bool HasCrashRecoveryItems => InterruptedJobs.Count > 0;

    public string CrashRecoveryBannerTitle =>
        InterruptedJobs.Count == 0
            ? string.Empty
            : $"{InterruptedJobs.Count} interrupted compression job(s) recovered from journal";

    public string CrashRecoveryBannerMessage =>
        InterruptedJobs.Count == 0
            ? string.Empty
            : $"Total `.part` staging data on disk: {ByteSizeFormatter.FormatBytes(InterruptedJobs.Sum(i => i.TempBytesOnDisk))}. Resume to finish safely or Discard Temp to clean up.";

    public int ActiveJobsCount => Jobs.Count(j => j.State == DesktopJobState.Running);

    public int QueuedJobsCount => Jobs.Count(j => j.State is DesktopJobState.Queued or DesktopJobState.Paused);

    public string QueueOverviewBadge =>
        $"Active: {ActiveJobsCount} · Queued/Paused: {QueuedJobsCount}" + (IsQueuePaused ? " · Global Pause" : string.Empty);

    public void LoadCrashRecoveryFromJournal()
    {
        InterruptedJobs.Clear();
        foreach (var item in _journalStore.LoadCrashRecoveryItems())
        {
            InterruptedJobs.Add(item);
        }

        RaiseCrashRecoveryProperties();
    }

    public QueueJobItem EnqueueBatchJob(
        string title,
        IReadOnlyList<StagedFileItem> stagedItems,
        string resolvedOutputPath,
        DesktopLosslessProfile profile,
        DestinationConflictMode conflictMode,
        int initialPriority = 1)
    {
        ArgumentNullException.ThrowIfNull(stagedItems);
        var jobId = Guid.NewGuid().ToString("N")[..12];
        var tempPartPath = _journalStore.GetStagingPartPath(resolvedOutputPath, jobId);
        var totalBytes = stagedItems.Sum(i => i.SizeBytes);

        var jobItem = new QueueJobItem
        {
            JobId = jobId,
            Title = title,
            InputSummary = $"{stagedItems.Count} file(s) → {Path.GetFileName(resolvedOutputPath)}",
            SourcePaths = stagedItems.Select(s => s.FullPath).ToList(),
            OutputPath = resolvedOutputPath,
            OperationKind = OperationKind.LosslessCompression,
            ProfileName = profile.ToString(),
            ConflictMode = conflictMode,
            TotalBytes = totalBytes,
            TempStagingPath = tempPartPath,
            Priority = initialPriority,
            State = _isQueuePaused ? DesktopJobState.Paused : DesktopJobState.Queued,
        };

        var control = new JobExecutionControl(stagedItems, profile);
        if (_isQueuePaused)
        {
            control.Pause();
        }

        _controls[jobId] = control;
        InsertJobByPriority(jobItem);
        PersistActiveJournal();
        RaiseQueueCounters();

        if (_engineBridge is not null && stagedItems.Count > 0)
        {
            _ = TryNotifyEngineEnqueueAsync(stagedItems[0].FullPath, profile);
        }

        if (AutoExecuteEnqueuedJobs && !_isQueuePaused)
        {
            _ = ProcessQueueAsync();
        }

        return jobItem;
    }

    public async Task ProcessQueueAsync(TimeSpan? perEntrySimulatedDelay = null)
    {
        if (!await _schedulerGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            while (!_isQueuePaused)
            {
                var nextJob = Jobs
                    .Where(j => j.State == DesktopJobState.Queued)
                    .OrderByDescending(j => j.Priority)
                    .ThenBy(j => j.CreatedAtUtc)
                    .FirstOrDefault();

                if (nextJob is null)
                {
                    break;
                }

                await ExecuteSingleJobAsync(nextJob, perEntrySimulatedDelay).ConfigureAwait(false);
            }
        }
        finally
        {
            _schedulerGate.Release();
            RaiseQueueCounters();
        }
    }

    public async Task ExecuteJobByIdAsync(string jobId, TimeSpan? perEntryDelay = null)
    {
        var job = Jobs.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
        if (job is null)
        {
            return;
        }

        await ExecuteSingleJobAsync(job, perEntryDelay).ConfigureAwait(false);
    }

    public async Task PauseJobAsync(string jobId)
    {
        var job = Jobs.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
        if (job is null || !job.CanPause)
        {
            return;
        }

        if (_controls.TryGetValue(jobId, out var control))
        {
            control.Pause();
        }

        job.State = DesktopJobState.Paused;
        job.SpeedMbPerSec = 0;
        job.Eta = null;
        PersistActiveJournal();
        RaiseQueueCounters();
        await Task.CompletedTask;
    }

    public async Task ResumeJobAsync(string jobId)
    {
        var job = Jobs.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
        if (job is null || !job.CanResume)
        {
            return;
        }

        var wasQueuedNeverStarted = job.ProcessedBytes == 0 && !_controls.TryGetValue(jobId, out var existingCtrl);
        if (_controls.TryGetValue(jobId, out var control))
        {
            var wasRunning = control.IsExecuting;
            control.Resume();
            job.State = wasRunning ? DesktopJobState.Running : DesktopJobState.Queued;
        }
        else
        {
            job.State = DesktopJobState.Queued;
        }

        PersistActiveJournal();
        RaiseQueueCounters();

        if (AutoExecuteEnqueuedJobs && !_isQueuePaused && (wasQueuedNeverStarted || job.State == DesktopJobState.Queued))
        {
            _ = ProcessQueueAsync();
        }

        await Task.CompletedTask;
    }

    public async Task CancelJobAsync(string jobId)
    {
        var job = Jobs.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
        if (job is null || !job.CanCancel)
        {
            return;
        }

        if (_controls.TryGetValue(jobId, out var control))
        {
            control.Cancel();
        }

        job.State = DesktopJobState.Cancelled;
        job.SpeedMbPerSec = 0;
        job.Eta = null;
        _journalStore.RemoveJournalEntryAndDeleteTemp(jobId, job.TempStagingPath);
        RaiseQueueCounters();

        if (_engineBridge is not null)
        {
            try
            {
                await _engineBridge.CancelJobAsync(jobId).ConfigureAwait(false);
            }
            catch
            {
                // Local cancel already succeeded.
            }
        }
    }

    public async Task PauseAllAsync()
    {
        IsQueuePaused = true;
        foreach (var job in Jobs.Where(j => j.CanPause).ToList())
        {
            await PauseJobAsync(job.JobId).ConfigureAwait(false);
        }

        if (_engineBridge is not null)
        {
            try
            {
                await _engineBridge.PauseAllAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore offline pipe on local pause.
            }
        }
    }

    public async Task ResumeAllAsync()
    {
        IsQueuePaused = false;
        foreach (var job in Jobs.Where(j => j.State == DesktopJobState.Paused).ToList())
        {
            if (_controls.TryGetValue(job.JobId, out var ctrl))
            {
                ctrl.Resume();
                job.State = ctrl.IsExecuting ? DesktopJobState.Running : DesktopJobState.Queued;
            }
            else
            {
                job.State = DesktopJobState.Queued;
            }
        }

        PersistActiveJournal();
        RaiseQueueCounters();

        if (_engineBridge is not null)
        {
            try
            {
                await _engineBridge.ResumeAllAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore offline pipe.
            }
        }

        if (AutoExecuteEnqueuedJobs)
        {
            _ = ProcessQueueAsync();
        }
    }

    public void MovePriorityUp(string jobId)
    {
        var index = FindJobIndex(jobId);
        if (index < 0)
        {
            return;
        }

        var job = Jobs[index];
        job.Priority++;
        if (index > 0 && Jobs[index - 1].State != DesktopJobState.Running)
        {
            Jobs.Move(index, index - 1);
        }
    }

    public void MovePriorityDown(string jobId)
    {
        var index = FindJobIndex(jobId);
        if (index < 0)
        {
            return;
        }

        var job = Jobs[index];
        job.Priority = Math.Max(0, job.Priority - 1);
        if (index < Jobs.Count - 1)
        {
            Jobs.Move(index, index + 1);
        }
    }

    public async Task<QueueJobItem?> ResumeInterruptedJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var interrupted = InterruptedJobs.FirstOrDefault(i => string.Equals(i.JobId, jobId, StringComparison.Ordinal));
        if (interrupted is null)
        {
            return null;
        }

        InterruptedJobs.Remove(interrupted);
        _journalStore.RemoveJournalEntryAndDeleteTemp(interrupted.JobId, interrupted.TempPartFilePath);
        RaiseCrashRecoveryProperties();

        var missingPath = interrupted.SourcePaths.FirstOrDefault(p => !File.Exists(p) && !Directory.Exists(p));
        if (missingPath is not null)
        {
            var failedItem = new QueueJobItem
            {
                JobId = interrupted.JobId,
                Title = interrupted.Title,
                InputSummary = $"{interrupted.SourcePaths.Count} source path(s) (missing input)",
                SourcePaths = interrupted.SourcePaths,
                OutputPath = interrupted.OutputPath,
                ProfileName = interrupted.ProfileName,
                TotalBytes = interrupted.TotalBytes,
                State = DesktopJobState.Failed,
                ErrorCard = ActionableErrorPresenter.FromCode(ActionableErrorCode.SourceFileNotFound, missingPath),
            };
            Jobs.Insert(0, failedItem);
            RaiseQueueCounters();
            return failedItem;
        }

        var profile = LosslessProfileCatalog.ParseFromEngine(interrupted.ProfileName);
        var (reanalyzedItems, _) = await _analyzer.AnalyzePathsAsync(interrupted.SourcePaths, profile, cancellationToken).ConfigureAwait(false);

        return EnqueueBatchJob(
            interrupted.Title,
            reanalyzedItems,
            interrupted.OutputPath,
            profile,
            DestinationConflictMode.Overwrite,
            initialPriority: 2);
    }

    public void DiscardInterruptedJobTemp(string jobId)
    {
        var interrupted = InterruptedJobs.FirstOrDefault(i => string.Equals(i.JobId, jobId, StringComparison.Ordinal));
        if (interrupted is null)
        {
            return;
        }

        _journalStore.RemoveJournalEntryAndDeleteTemp(interrupted.JobId, interrupted.TempPartFilePath);
        InterruptedJobs.Remove(interrupted);
        RaiseCrashRecoveryProperties();
    }

    public async Task ResumeAllInterruptedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var item in InterruptedJobs.ToList())
        {
            await ResumeInterruptedJobAsync(item.JobId, cancellationToken).ConfigureAwait(false);
        }
    }

    public void DiscardAllInterruptedTemp()
    {
        foreach (var item in InterruptedJobs.ToList())
        {
            DiscardInterruptedJobTemp(item.JobId);
        }
    }

    public void DismissJobError(string jobId)
    {
        var job = Jobs.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
        if (job is not null)
        {
            job.ErrorCard = null;
        }
    }

    public async Task RefreshEngineConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_engineBridge is null)
        {
            IsEngineConnected = false;
            EngineStatusSummary = "Local Desktop Engine Active (Standalone)";
            return;
        }

        try
        {
            var status = await _engineBridge.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status is null || status.Type == EngineMessageTypes.Error)
            {
                IsEngineConnected = false;
                EngineStatusSummary = status?.Message is { Length: > 0 } msg
                    ? $"Engine offline ({msg}) · Local desktop queue ready"
                    : "Engine service offline · Local desktop queue ready";
                return;
            }

            IsEngineConnected = true;
            var pausedSuffix = status.Paused == true ? " · Paused" : string.Empty;
            EngineStatusSummary =
                $"Connected to Engine v{status.EngineVersion ?? "0.3"} · Service active {status.ActiveJobs ?? 0}, queued {status.QueuedJobs ?? 0}{pausedSuffix}";
        }
        catch
        {
            IsEngineConnected = false;
            EngineStatusSummary = "Engine service offline · Local desktop queue ready";
        }
    }

    private async Task ExecuteSingleJobAsync(QueueJobItem job, TimeSpan? perEntryDelay)
    {
        if (!_controls.TryGetValue(job.JobId, out var control))
        {
            return;
        }

        control.IsExecuting = true;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await control.WaitIfPausedAsync().ConfigureAwait(false);
            control.CancellationToken.ThrowIfCancellationRequested();

            if (job.ConflictMode == DestinationConflictMode.Skip && File.Exists(job.OutputPath))
            {
                job.ProgressPercent = 100.0;
                job.ProcessedBytes = job.TotalBytes;
                job.State = DesktopJobState.Completed;
                _journalStore.RemoveJournalEntryAndDeleteTemp(job.JobId, job.TempStagingPath);
                RaiseQueueCounters();
                return;
            }

            job.State = DesktopJobState.Running;
            job.ErrorCard = null;
            PersistActiveJournal();
            RaiseQueueCounters();

            var tempPartPath = job.TempStagingPath ?? _journalStore.GetStagingPartPath(job.OutputPath, job.JobId);
            job.TempStagingPath = tempPartPath;

            var finalArchiveBytes = await _archiveService.CreateUcxArchiveAsync(
                control.StagedItems,
                tempPartPath,
                control.Profile,
                async (processed, total) =>
                {
                    if (perEntryDelay.HasValue && perEntryDelay.Value > TimeSpan.Zero)
                    {
                        await Task.Delay(perEntryDelay.Value, control.CancellationToken).ConfigureAwait(false);
                    }

                    await control.WaitIfPausedAsync().ConfigureAwait(false);
                    control.CancellationToken.ThrowIfCancellationRequested();

                    var elapsedSeconds = Math.Max(0.005, stopwatch.Elapsed.TotalSeconds);
                    var speedMb = (processed / (1024.0 * 1024.0)) / elapsedSeconds;
                    var remainingBytes = Math.Max(0L, total - processed);
                    var etaSeconds = speedMb > 0.01
                        ? (remainingBytes / (1024.0 * 1024.0)) / speedMb
                        : 0;

                    job.ProcessedBytes = processed;
                    job.ProgressPercent = total > 0 ? (processed * 100.0) / total : 100.0;
                    job.SpeedMbPerSec = speedMb;
                    job.Eta = remainingBytes == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(etaSeconds);
                    PersistActiveJournal();
                },
                control.CancellationToken).ConfigureAwait(false);

            var outDir = Path.GetDirectoryName(job.OutputPath);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            File.Move(tempPartPath, job.OutputPath, overwrite: true);

            stopwatch.Stop();
            var totalSec = Math.Max(0.005, stopwatch.Elapsed.TotalSeconds);
            var avgSpeedMb = (job.TotalBytes / (1024.0 * 1024.0)) / totalSec;

            job.ProcessedBytes = job.TotalBytes;
            job.ProgressPercent = 100.0;
            job.SpeedMbPerSec = avgSpeedMb;
            job.Eta = TimeSpan.Zero;
            job.State = DesktopJobState.Completed;

            _journalStore.RemoveJournalEntryAndDeleteTemp(job.JobId, tempPartPath);
            RaiseQueueCounters();

            var dominantStrategies = control.StagedItems
                .Select(i => i.SelectedStrategy)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2);

            var historyRecord = new CompressionHistoryRecord
            {
                RecordId = job.JobId,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ArchiveName = Path.GetFileName(job.OutputPath),
                SourceSummary = $"{control.StagedItems.Count} file(s)",
                OutputPath = job.OutputPath,
                OutputFolder = Path.GetDirectoryName(job.OutputPath) ?? string.Empty,
                OperationKind = OperationKind.LosslessCompression,
                ProfileName = control.Profile.ToString(),
                StrategySummary = string.Join(" + ", dominantStrategies),
                OriginalBytes = job.TotalBytes,
                OutputBytes = finalArchiveBytes,
                DurationSeconds = stopwatch.Elapsed.TotalSeconds,
                AverageSpeedMbPerSec = avgSpeedMb,
                VerifiedSha256 = true,
            };

            JobCompletedHistoryProduced?.Invoke(historyRecord);
        }
        catch (OperationCanceledException)
        {
            job.State = DesktopJobState.Cancelled;
            job.SpeedMbPerSec = 0;
            job.Eta = null;
            _journalStore.RemoveJournalEntryAndDeleteTemp(job.JobId, job.TempStagingPath);
            RaiseQueueCounters();
        }
        catch (Exception ex)
        {
            job.State = DesktopJobState.Failed;
            job.SpeedMbPerSec = 0;
            job.Eta = null;
            job.ErrorCard = ActionableErrorPresenter.FromException(ex, job.OutputPath);
            _journalStore.RemoveJournalEntryAndDeleteTemp(job.JobId, job.TempStagingPath);
            RaiseQueueCounters();
        }
        finally
        {
            control.IsExecuting = false;
        }
    }

    private void PersistActiveJournal()
    {
        var activeEntries = Jobs
            .Where(j => j.State is DesktopJobState.Running or DesktopJobState.Paused or DesktopJobState.Queued)
            .Select(j => new PersistedQueueJournalEntry
            {
                JobId = j.JobId,
                Title = j.Title,
                SourcePaths = j.SourcePaths,
                OutputPath = j.OutputPath,
                TempPartFilePath = j.TempStagingPath ?? _journalStore.GetStagingPartPath(j.OutputPath, j.JobId),
                ProfileName = j.ProfileName,
                TotalBytes = j.TotalBytes,
                LastKnownState = j.State,
                ProgressPercent = j.ProgressPercent,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            })
            .ToList();

        _journalStore.SaveActiveJournalEntries(activeEntries);
    }

    private void InsertJobByPriority(QueueJobItem item)
    {
        for (var i = 0; i < Jobs.Count; i++)
        {
            if (Jobs[i].State != DesktopJobState.Running && item.Priority > Jobs[i].Priority)
            {
                Jobs.Insert(i, item);
                return;
            }
        }

        Jobs.Add(item);
    }

    private int FindJobIndex(string jobId)
    {
        for (var i = 0; i < Jobs.Count; i++)
        {
            if (string.Equals(Jobs[i].JobId, jobId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private async Task TryNotifyEngineEnqueueAsync(string samplePath, DesktopLosslessProfile profile)
    {
        if (_engineBridge is null)
        {
            return;
        }

        try
        {
            await _engineBridge.EnqueueJobAsync(samplePath, LosslessProfileCatalog.ToEngineWireName(profile)).ConfigureAwait(false);
        }
        catch
        {
            // Engine service optional when running standalone desktop archive manager.
        }
    }

    private void RaiseCrashRecoveryProperties()
    {
        RaisePropertyChanged(nameof(HasCrashRecoveryItems));
        RaisePropertyChanged(nameof(CrashRecoveryBannerTitle));
        RaisePropertyChanged(nameof(CrashRecoveryBannerMessage));
    }

    private void RaiseQueueCounters()
    {
        RaisePropertyChanged(nameof(ActiveJobsCount));
        RaisePropertyChanged(nameof(QueuedJobsCount));
        RaisePropertyChanged(nameof(QueueOverviewBadge));
    }

    private sealed class JobExecutionControl
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly object _pauseLock = new();
        private TaskCompletionSource<bool>? _pauseTcs;

        public JobExecutionControl(IReadOnlyList<StagedFileItem> stagedItems, DesktopLosslessProfile profile)
        {
            StagedItems = stagedItems;
            Profile = profile;
        }

        public IReadOnlyList<StagedFileItem> StagedItems { get; }

        public DesktopLosslessProfile Profile { get; }

        public bool IsExecuting { get; set; }

        public CancellationToken CancellationToken => _cts.Token;

        public void Pause()
        {
            lock (_pauseLock)
            {
                _pauseTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Resume()
        {
            TaskCompletionSource<bool>? toRelease;
            lock (_pauseLock)
            {
                toRelease = _pauseTcs;
                _pauseTcs = null;
            }

            toRelease?.TrySetResult(true);
        }

        public void Cancel()
        {
            _cts.Cancel();
            Resume();
        }

        public Task WaitIfPausedAsync()
        {
            TaskCompletionSource<bool>? current;
            lock (_pauseLock)
            {
                current = _pauseTcs;
            }

            return current is null
                ? Task.CompletedTask
                : current.Task.WaitAsync(_cts.Token);
        }
    }
}
