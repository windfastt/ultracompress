using System.Collections.Concurrent;
using UltraCompress.Core;
using UltraCompress.Core.Archive;
using UltraCompress.Core.Destination;
using UltraCompress.Core.Errors;
using UltraCompress.Core.History;
using UltraCompress.Core.Jobs;

namespace UltraCompress.Engine.Jobs;

/// <summary>
/// Production resilient archive queue engine combining SQLite job journaling (<c>jobs.db</c>),
/// atomic <c>.ucx.part</c> staging, startup crash detection with one-click Resume / Clean Rollback,
/// cooperative block/chunk-level Pause/Resume/Cancel, pre-flight destination &amp; free-space checks,
/// structured <see cref="UltraCompressError"/> diagnostics, and persistent compression history.
/// </summary>
public sealed class ResilientArchiveEngine : IAsyncDisposable, IDisposable
{
    private readonly SqliteJobJournal _journal;
    private readonly ICompressionHistoryStore _history;
    private readonly DestinationResolver _destinationResolver;
    private readonly UcxArchiveWriter _writer = new();
    private readonly UcxArchiveReader _reader = new();

    private readonly CooperativePauseController _globalPause = new();
    private readonly ConcurrentDictionary<string, ActiveJobRuntime> _activeRuntimes = new();
    private readonly ConcurrentDictionary<string, ArchiveJobSnapshot> _liveSnapshots = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ArchiveJobSnapshot>> _completionWaiters = new();
    private readonly SemaphoreSlim _wakeSignal = new(0);

    private CancellationTokenSource? _workerLoopCts;
    private Task? _workerLoopTask;
    private volatile bool _crashSimulated;

    public ResilientArchiveEngine(
        SqliteJobJournal? journal = null,
        ICompressionHistoryStore? history = null,
        DestinationResolver? destinationResolver = null)
    {
        _journal = journal ?? new SqliteJobJournal();
        _journal.EnsureInitialized();

        _history = history ?? new SqliteCompressionHistoryStore(_journal.DatabasePath);
        _destinationResolver = destinationResolver ?? new DestinationResolver();
    }

    public event Action<ArchiveJobSnapshot>? JobProgressChanged;

    /// <summary>Optional hook invoked right after each block checkpoint commits (useful for deterministic crash/pause tests).</summary>
    internal Func<string, UcxBlockCheckpoint, CancellationToken, ValueTask>? OnAfterBlockCheckpointedAsync { get; set; }

    public SqliteJobJournal Journal => _journal;

    public ICompressionHistoryStore History => _history;

    public DestinationResolver Destination => _destinationResolver;

    public bool IsQueuePaused => _globalPause.IsPaused;

    public IReadOnlyList<InterruptedJobSummary> RecoverOnStartup()
    {
        var interrupted = _journal.DetectAndMarkInterruptedJobs();
        foreach (var summary in interrupted)
        {
            var snap = _journal.GetJob(summary.JobId);
            if (snap is not null)
            {
                _liveSnapshots[summary.JobId] = snap;
            }
        }

        return interrupted;
    }

    public void StartBackgroundWorker()
    {
        if (_workerLoopTask is not null && !_workerLoopTask.IsCompleted)
        {
            return;
        }

        _crashSimulated = false;
        _workerLoopCts = new CancellationTokenSource();
        _workerLoopTask = Task.Run(() => RunWorkerLoopAsync(_workerLoopCts.Token));
        SignalWorker();
    }

    public async Task StopBackgroundWorkerAsync()
    {
        if (_workerLoopCts is null || _workerLoopTask is null)
        {
            return;
        }

        await _workerLoopCts.CancelAsync().ConfigureAwait(false);
        SignalWorker();
        try
        {
            await _workerLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Enqueues a multi-file/folder <c>.ucx</c> pack job after running pre-flight destination resolution
    /// and free-disk-space validation.
    /// </summary>
    public ArchiveJobSnapshot EnqueuePack(ArchivePackJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = _destinationResolver.ResolveAndValidatePack(
            request.InputPaths,
            request.DestinationMode,
            request.ConflictPolicy,
            request.OutputPathOrDirectory,
            request.ArchiveSubfolderName);

        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.NewGuid().ToString("N");

        if (resolved.ShouldSkipExisting)
        {
            var skippedSnap = new ArchiveJobSnapshot
            {
                JobId = jobId,
                Operation = ArchiveJobOperation.Pack,
                InputPaths = request.InputPaths.Select(Path.GetFullPath).ToList(),
                ResolvedOutputPath = resolved.FinalArchivePath,
                StagingPartPath = null,
                Profile = request.Profile,
                DestinationMode = request.DestinationMode,
                ConflictPolicy = request.ConflictPolicy,
                MaxSolidBlockBytes = request.MaxSolidBlockBytes,
                PreferredCodecOverride = request.PreferredCodecOverride,
                PreserveRootFolderName = request.PreserveRootFolderName,
                Status = ArchiveJobLifecycleState.Completed,
                CurrentStage = "SkippedExisting",
                SkippedExisting = true,
                CreatedUtc = now,
                StartedUtc = now,
                UpdatedUtc = now,
                CompletedUtc = now,
            };

            PersistAndPublish(skippedSnap);
            CompleteWaiters(skippedSnap);
            return skippedSnap;
        }

        var snapshot = new ArchiveJobSnapshot
        {
            JobId = jobId,
            Operation = ArchiveJobOperation.Pack,
            InputPaths = request.InputPaths.Select(Path.GetFullPath).ToList(),
            ResolvedOutputPath = resolved.FinalArchivePath,
            StagingPartPath = resolved.StagingPartPath,
            Profile = request.Profile,
            DestinationMode = request.DestinationMode,
            ConflictPolicy = request.ConflictPolicy,
            MaxSolidBlockBytes = request.MaxSolidBlockBytes,
            PreferredCodecOverride = request.PreferredCodecOverride,
            PreserveRootFolderName = request.PreserveRootFolderName,
            Status = ArchiveJobLifecycleState.Queued,
            CurrentStage = "Queued",
            TotalBytes = Math.Max(0, resolved.EstimatedRequiredBytes - 64 * 1024),
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        PersistAndPublish(snapshot);
        SignalWorker();
        return snapshot;
    }

    /// <summary>
    /// Enqueues a selective or full <c>.ucx</c> extraction job after O(1) manifest inspection and
    /// pre-flight destination free-space validation.
    /// </summary>
    public ArchiveJobSnapshot EnqueueExtract(ArchiveExtractJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var inspection = _reader.InspectArchive(request.ArchivePath);
        var matchingEntries = inspection.Entries
            .Where(e => !e.IsDirectory && UcxPathSecurity.MatchesSelectPatterns(e.RelativePath, request.SelectPatterns))
            .ToList();

        long requiredBytes = matchingEntries.Sum(e => e.OriginalSize);
        var resolved = _destinationResolver.ResolveAndValidateExtract(
            inspection.ArchivePath ?? request.ArchivePath,
            requiredBytes,
            request.DestinationMode,
            request.OutputDirectory,
            request.SubfolderNameOverride);

        var now = DateTimeOffset.UtcNow;
        var snapshot = new ArchiveJobSnapshot
        {
            JobId = Guid.NewGuid().ToString("N"),
            Operation = ArchiveJobOperation.Extract,
            InputPaths = [Path.GetFullPath(request.ArchivePath)],
            ResolvedOutputPath = resolved.DestinationDirectory,
            Profile = inspection.Profile,
            DestinationMode = request.DestinationMode,
            ConflictPolicy = request.ConflictPolicy,
            SelectPatterns = request.SelectPatterns?.ToList(),
            Status = ArchiveJobLifecycleState.Queued,
            CurrentStage = "Queued",
            TotalBytes = requiredBytes,
            TotalFiles = matchingEntries.Count,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        PersistAndPublish(snapshot);
        SignalWorker();
        return snapshot;
    }

    public ArchiveJobSnapshot EnqueueVerify(string archivePath)
    {
        var inspection = _reader.InspectArchive(archivePath);
        var now = DateTimeOffset.UtcNow;

        var snapshot = new ArchiveJobSnapshot
        {
            JobId = Guid.NewGuid().ToString("N"),
            Operation = ArchiveJobOperation.Verify,
            InputPaths = [Path.GetFullPath(archivePath)],
            ResolvedOutputPath = Path.GetFullPath(archivePath),
            Profile = inspection.Profile,
            Status = ArchiveJobLifecycleState.Queued,
            CurrentStage = "Queued",
            TotalBytes = inspection.Summary.OriginalBytes,
            TotalFiles = inspection.Summary.FileCount,
            TotalBlocks = inspection.Summary.BlockCount,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        PersistAndPublish(snapshot);
        SignalWorker();
        return snapshot;
    }

    public Task<UcxArchiveInspection> InspectArchiveAsync(string archivePath, CancellationToken cancellationToken = default) =>
        _reader.InspectArchiveAsync(archivePath, cancellationToken);

    public Task<UcxArchiveVerifyResult> VerifyArchiveImmediateAsync(string archivePath, CancellationToken cancellationToken = default) =>
        _reader.VerifyArchiveAsync(archivePath, cancellationToken: cancellationToken);

    public ArchiveJobSnapshot? GetJob(string jobId)
    {
        if (_liveSnapshots.TryGetValue(jobId, out var live))
        {
            return live;
        }

        return _journal.GetJob(jobId);
    }

    public IReadOnlyList<ArchiveJobSnapshot> ListJobs(int limit = 100, ArchiveJobLifecycleState? stateFilter = null)
    {
        var persisted = _journal.ListJobs(limit, stateFilter);
        return persisted
            .Select(j => _liveSnapshots.TryGetValue(j.JobId, out var live) ? live : j)
            .Where(j => stateFilter is null || j.Status == stateFilter.Value)
            .ToList();
    }

    public IReadOnlyList<InterruptedJobSummary> ListInterruptedJobs() =>
        _journal.ListInterruptedJobs();

    public Task<ArchiveJobSnapshot> WaitForJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var current = GetJob(jobId)
            ?? throw UltraCompressErrors.InputNotFound(jobId);

        if (current.IsTerminal || current.Status == ArchiveJobLifecycleState.Interrupted)
        {
            return Task.FromResult(current);
        }

        var waiter = _completionWaiters.GetOrAdd(
            jobId,
            _ => new TaskCompletionSource<ArchiveJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously));

        // Double-check race after registering waiter.
        var recheck = GetJob(jobId);
        if (recheck is not null && (recheck.IsTerminal || recheck.Status == ArchiveJobLifecycleState.Interrupted))
        {
            waiter.TrySetResult(recheck);
        }

        return waiter.Task.WaitAsync(cancellationToken);
    }

    public void SetQueuePaused(bool paused)
    {
        if (paused)
        {
            _globalPause.Pause();
            foreach (var runtime in _activeRuntimes.Values)
            {
                if (_liveSnapshots.TryGetValue(runtime.JobId, out var snap) && snap.Status == ArchiveJobLifecycleState.Running)
                {
                    PersistAndPublish(snap with
                    {
                        Status = ArchiveJobLifecycleState.Paused,
                        CurrentStage = "Paused",
                        UpdatedUtc = DateTimeOffset.UtcNow,
                    });
                }
            }
        }
        else
        {
            _globalPause.Resume();
            foreach (var runtime in _activeRuntimes.Values)
            {
                if (!runtime.PauseController.IsPaused
                    && _liveSnapshots.TryGetValue(runtime.JobId, out var snap)
                    && snap.Status == ArchiveJobLifecycleState.Paused)
                {
                    PersistAndPublish(snap with
                    {
                        Status = ArchiveJobLifecycleState.Running,
                        CurrentStage = "Resuming",
                        UpdatedUtc = DateTimeOffset.UtcNow,
                    });
                }
            }

            SignalWorker();
        }
    }

    public bool PauseJob(string jobId)
    {
        if (_activeRuntimes.TryGetValue(jobId, out var runtime))
        {
            runtime.PauseController.Pause();
            var current = GetJob(jobId);
            if (current is not null && !current.IsTerminal)
            {
                PersistAndPublish(current with
                {
                    Status = ArchiveJobLifecycleState.Paused,
                    CurrentStage = "Paused",
                    ThroughputMbPerSec = 0,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                });
            }

            return true;
        }

        var queued = _journal.GetJob(jobId);
        if (queued is not null && queued.Status == ArchiveJobLifecycleState.Queued)
        {
            PersistAndPublish(queued with
            {
                Status = ArchiveJobLifecycleState.Paused,
                CurrentStage = "Paused",
                UpdatedUtc = DateTimeOffset.UtcNow,
            });
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resumes either an in-flight <c>Paused</c> job or an <c>Interrupted</c> job recovered after a crash,
    /// reusing committed SQLite block checkpoints and <c>.ucx.part</c> staging data without re-compressing completed blocks.
    /// </summary>
    public ArchiveJobSnapshot ResumeJob(string jobId)
    {
        if (_activeRuntimes.TryGetValue(jobId, out var runtime))
        {
            runtime.PauseController.Resume();
            if (_globalPause.IsPaused)
            {
                _globalPause.Resume();
            }

            var live = GetJob(jobId)!;
            var resumedLive = live with
            {
                Status = ArchiveJobLifecycleState.Running,
                CurrentStage = "Resuming",
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
            PersistAndPublish(resumedLive);
            return resumedLive;
        }

        var persisted = _journal.GetJob(jobId)
            ?? throw UltraCompressErrors.InputNotFound(jobId);

        if (persisted.Status is not (ArchiveJobLifecycleState.Interrupted or ArchiveJobLifecycleState.Paused))
        {
            throw UltraCompressErrors.JobNotResumable(jobId, persisted.Status.ToString());
        }

        // Reset completion waiter so callers can await the resumed execution.
        _completionWaiters.TryRemove(jobId, out _);

        var requeued = persisted with
        {
            Status = ArchiveJobLifecycleState.Queued,
            CurrentStage = "QueuedForResume",
            Error = null,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        PersistAndPublish(requeued);
        if (_globalPause.IsPaused)
        {
            _globalPause.Resume();
        }

        SignalWorker();
        return requeued;
    }

    /// <summary>
    /// Cleanly rolls back an <c>Interrupted</c>, <c>Paused</c>, <c>Queued</c>, or <c>Failed</c> job:
    /// cancels any active worker, deletes <c>.ucx.part</c> staging files, clears SQLite checkpoints,
    /// and transitions the job to <see cref="ArchiveJobLifecycleState.RolledBack"/>.
    /// </summary>
    public async Task<ArchiveJobSnapshot> RollbackJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var current = GetJob(jobId)
            ?? throw UltraCompressErrors.InputNotFound(jobId);

        if (current.Status == ArchiveJobLifecycleState.Completed)
        {
            throw UltraCompressErrors.JobNotResumable(jobId, current.Status.ToString());
        }

        if (_activeRuntimes.TryGetValue(jobId, out var runtime))
        {
            await runtime.CancelCts.CancelAsync().ConfigureAwait(false);
            runtime.PauseController.Resume();
            try
            {
                await runtime.ExecutionTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        CleanupStagingArtifacts(current.StagingPartPath);
        _journal.ClearBlockCheckpoints(jobId);

        var now = DateTimeOffset.UtcNow;
        var rolledBack = current with
        {
            Status = ArchiveJobLifecycleState.RolledBack,
            CurrentStage = "RolledBack",
            ThroughputMbPerSec = 0,
            EstimatedRemainingSeconds = null,
            UpdatedUtc = now,
            CompletedUtc = now,
        };

        PersistAndPublish(rolledBack);
        CompleteWaiters(rolledBack);
        return rolledBack;
    }

    public async Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var current = GetJob(jobId);
        if (current is null || current.IsTerminal)
        {
            return false;
        }

        if (_activeRuntimes.TryGetValue(jobId, out var runtime))
        {
            await runtime.CancelCts.CancelAsync().ConfigureAwait(false);
            runtime.PauseController.Resume();
            try
            {
                await runtime.ExecutionTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        var latest = GetJob(jobId) ?? current;
        if (latest.Status == ArchiveJobLifecycleState.Cancelled)
        {
            return true;
        }

        CleanupStagingArtifacts(latest.StagingPartPath);
        _journal.ClearBlockCheckpoints(jobId);

        var now = DateTimeOffset.UtcNow;
        var cancelled = latest with
        {
            Status = ArchiveJobLifecycleState.Cancelled,
            CurrentStage = "Cancelled",
            Error = UltraCompressErrors.JobCancelled(jobId).Error,
            ThroughputMbPerSec = 0,
            EstimatedRemainingSeconds = null,
            UpdatedUtc = now,
            CompletedUtc = now,
        };

        PersistAndPublish(cancelled);
        CompleteWaiters(cancelled);
        return true;
    }

    /// <summary>
    /// Internal test hook simulating an abrupt process crash (<c>SIGKILL</c> / power loss):
    /// aborts the in-flight worker loop immediately while leaving <c>Running</c> status in <c>jobs.db</c>
    /// and <c>.ucx.part</c> on disk untouched.
    /// </summary>
    internal async Task SimulateAbruptCrashForTestingAsync()
    {
        _crashSimulated = true;
        foreach (var rt in _activeRuntimes.Values)
        {
            rt.CancelCts.Cancel();
            rt.PauseController.Resume();
        }

        _workerLoopCts?.Cancel();
        SignalWorker();

        if (_workerLoopTask is not null && Task.CurrentId != _workerLoopTask.Id)
        {
            try
            {
                await _workerLoopTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _activeRuntimes.Clear();
    }

    private async Task RunWorkerLoopAsync(CancellationToken loopToken)
    {
        while (!loopToken.IsCancellationRequested && !_crashSimulated)
        {
            try
            {
                if (_globalPause.IsPaused)
                {
                    await _globalPause.WaitWhilePausedAsync(loopToken).ConfigureAwait(false);
                }

                var claimed = _journal.TryClaimNextQueuedJob();
                if (claimed is null)
                {
                    await _wakeSignal.WaitAsync(TimeSpan.FromMilliseconds(250), loopToken).ConfigureAwait(false);
                    continue;
                }

                var pauseController = new CooperativePauseController(startPaused: false);
                using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
                var compositePause = new CompositePauseToken(_globalPause, pauseController);

                var execTask = ExecuteClaimedJobAsync(claimed, pauseController, compositePause, jobCts.Token);
                var runtime = new ActiveJobRuntime(claimed.JobId, pauseController, jobCts, execTask);
                _activeRuntimes[claimed.JobId] = runtime;

                try
                {
                    await execTask.ConfigureAwait(false);
                }
                finally
                {
                    _activeRuntimes.TryRemove(claimed.JobId, out _);
                }
            }
            catch (OperationCanceledException) when (loopToken.IsCancellationRequested || _crashSimulated)
            {
                break;
            }
            catch
            {
                if (loopToken.IsCancellationRequested || _crashSimulated)
                {
                    break;
                }
            }
        }
    }

    private async Task ExecuteClaimedJobAsync(
        ArchiveJobSnapshot job,
        CooperativePauseController jobPauseController,
        IPauseToken effectivePauseToken,
        CancellationToken jobToken)
    {
        PersistAndPublish(job);

        try
        {
            switch (job.Operation)
            {
                case ArchiveJobOperation.Pack:
                    await ExecutePackJobAsync(job, jobPauseController, effectivePauseToken, jobToken).ConfigureAwait(false);
                    break;

                case ArchiveJobOperation.Extract:
                    await ExecuteExtractJobAsync(job, jobPauseController, effectivePauseToken, jobToken).ConfigureAwait(false);
                    break;

                case ArchiveJobOperation.Verify:
                    await ExecuteVerifyJobAsync(job, jobPauseController, effectivePauseToken, jobToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (_crashSimulated)
        {
            // Simulated abrupt crash: intentionally leave job in Running/Paused state and preserve .ucx.part!
        }
        catch (OperationCanceledException)
        {
            CleanupStagingArtifacts(job.StagingPartPath);
            _journal.ClearBlockCheckpoints(job.JobId);

            var now = DateTimeOffset.UtcNow;
            var cancelled = (_liveSnapshots.GetValueOrDefault(job.JobId) ?? job) with
            {
                Status = ArchiveJobLifecycleState.Cancelled,
                CurrentStage = "Cancelled",
                Error = UltraCompressErrors.JobCancelled(job.JobId).Error,
                ThroughputMbPerSec = 0,
                EstimatedRemainingSeconds = null,
                UpdatedUtc = now,
                CompletedUtc = now,
            };

            PersistAndPublish(cancelled);
            CompleteWaiters(cancelled);
        }
        catch (Exception ex)
        {
            if (_crashSimulated)
            {
                return;
            }

            // On failure, remove unreferenced staging .part unless checkpoints exist for potential diagnosis;
            // clean up non-checkpointed empty staging files.
            var mappedError = UltraCompressErrors.MapException(ex, job.ResolvedOutputPath);
            if (_journal.GetBlockCheckpoints(job.JobId).Count == 0)
            {
                CleanupStagingArtifacts(job.StagingPartPath);
            }

            var now = DateTimeOffset.UtcNow;
            var failed = (_liveSnapshots.GetValueOrDefault(job.JobId) ?? job) with
            {
                Status = ArchiveJobLifecycleState.Failed,
                CurrentStage = "Failed",
                Error = mappedError,
                ThroughputMbPerSec = 0,
                EstimatedRemainingSeconds = null,
                UpdatedUtc = now,
                CompletedUtc = now,
            };

            PersistAndPublish(failed);
            CompleteWaiters(failed);
        }
    }

    private async Task ExecutePackJobAsync(
        ArchiveJobSnapshot job,
        CooperativePauseController jobPause,
        IPauseToken effectivePause,
        CancellationToken jobToken)
    {
        var existingCheckpoints = _journal.GetBlockCheckpoints(job.JobId);
        int plannedTotalBlocks = Math.Max(job.TotalBlocks, existingCheckpoints.Count);

        var progress = new SyncProgress<ArchiveProgressSnapshot>(snap =>
        {
            if (_crashSimulated)
            {
                return;
            }

            var current = _liveSnapshots.GetValueOrDefault(job.JobId) ?? job;
            bool isPaused = jobPause.IsPaused || _globalPause.IsPaused;
            var updated = current with
            {
                Status = isPaused ? ArchiveJobLifecycleState.Paused : ArchiveJobLifecycleState.Running,
                CurrentStage = isPaused ? "Paused" : snap.Stage,
                ProcessedBytes = snap.ProcessedBytes,
                TotalBytes = snap.TotalBytes > 0 ? snap.TotalBytes : current.TotalBytes,
                ProcessedFiles = snap.ProcessedFiles,
                TotalFiles = snap.TotalFiles > 0 ? snap.TotalFiles : current.TotalFiles,
                CompletedBlocks = snap.CompletedBlocks,
                TotalBlocks = snap.TotalBlocks > 0 ? snap.TotalBlocks : plannedTotalBlocks,
                ThroughputMbPerSec = isPaused ? 0 : snap.ThroughputMbPerSec,
                EstimatedRemainingSeconds = isPaused ? null : snap.EstimatedRemainingSeconds,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
            _liveSnapshots[job.JobId] = updated;
            JobProgressChanged?.Invoke(updated);
        });

        var packOptions = new UcxPackOptions
        {
            Profile = job.Profile,
            MaxSolidBlockBytes = job.MaxSolidBlockBytes,
            PreferredCodecOverride = job.PreferredCodecOverride,
            OverwriteDestination = job.ConflictPolicy == DestinationConflictPolicy.Overwrite || !File.Exists(job.ResolvedOutputPath),
            PreserveRootFolderName = job.PreserveRootFolderName,
            StagingPartPath = job.StagingPartPath ?? (job.ResolvedOutputPath + ".part"),
            ExistingCheckpoints = existingCheckpoints,
            PauseToken = effectivePause,
            OnTotalBlocksPlanned = total => plannedTotalBlocks = total,
            OnBlockCommittedAsync = async (cp, ct) =>
            {
                _journal.CommitBlockCheckpoint(job.JobId, cp, plannedTotalBlocks);
                if (OnAfterBlockCheckpointedAsync is not null)
                {
                    await OnAfterBlockCheckpointedAsync(job.JobId, cp, ct).ConfigureAwait(false);
                }
            },
        };

        var result = await _writer.CreateAsync(
            job.ResolvedOutputPath,
            job.InputPaths,
            packOptions,
            progress,
            jobToken).ConfigureAwait(false);

        _journal.ClearBlockCheckpoints(job.JobId);

        var completedNow = DateTimeOffset.UtcNow;
        double durationSec = Math.Max(result.Elapsed.TotalSeconds, 0.001);
        double mbps = result.Summary.OriginalBytes > 0
            ? (result.Summary.OriginalBytes / (1024.0 * 1024.0)) / durationSec
            : 0.0;

        var completedJob = (_liveSnapshots.GetValueOrDefault(job.JobId) ?? job) with
        {
            Status = ArchiveJobLifecycleState.Completed,
            CurrentStage = "Completed",
            ProcessedBytes = result.Summary.OriginalBytes,
            TotalBytes = result.Summary.OriginalBytes,
            CompressedBytes = result.Summary.ArchiveBytes,
            ProcessedFiles = result.Summary.FileCount,
            TotalFiles = result.Summary.FileCount,
            CompletedBlocks = result.Summary.BlockCount,
            TotalBlocks = result.Summary.BlockCount,
            ResumedBlocks = result.ResumedBlocksFromCheckpoint,
            DuplicateCount = result.Summary.DuplicateCount,
            DuplicateBytesSaved = result.Summary.DuplicateBytesSaved,
            ThroughputMbPerSec = Math.Round(mbps, 2),
            EstimatedRemainingSeconds = 0,
            UpdatedUtc = completedNow,
            CompletedUtc = completedNow,
        };

        _history.Record(new CompressionHistoryRecord
        {
            JobId = job.JobId,
            Operation = "Pack",
            ArchivePath = result.ArchivePath,
            SourceSummary = FormatSourceSummary(job.InputPaths),
            DestinationPath = result.ArchivePath,
            Profile = job.Profile.ToString(),
            FileCount = result.Summary.FileCount,
            OriginalBytes = result.Summary.OriginalBytes,
            CompressedBytes = result.Summary.ArchiveBytes,
            SavedBytes = Math.Max(0, result.Summary.OriginalBytes - result.Summary.ArchiveBytes),
            CompressionRatio = result.Summary.OriginalBytes == 0
                ? 1.0
                : (double)result.Summary.ArchiveBytes / result.Summary.OriginalBytes,
            DuplicateCount = result.Summary.DuplicateCount,
            DuplicateBytesSaved = result.Summary.DuplicateBytesSaved,
            DurationMs = Math.Max(1, (long)result.Elapsed.TotalMilliseconds),
            ThroughputMbPerSec = Math.Round(mbps, 2),
            CompletedUtc = completedNow,
        });

        PersistAndPublish(completedJob);
        CompleteWaiters(completedJob);
    }

    private async Task ExecuteExtractJobAsync(
        ArchiveJobSnapshot job,
        CooperativePauseController jobPause,
        IPauseToken effectivePause,
        CancellationToken jobToken)
    {
        var archivePath = job.InputPaths[0];
        var progress = new SyncProgress<ArchiveProgressSnapshot>(snap =>
        {
            if (_crashSimulated)
            {
                return;
            }

            var current = _liveSnapshots.GetValueOrDefault(job.JobId) ?? job;
            bool isPaused = jobPause.IsPaused || _globalPause.IsPaused;
            var updated = current with
            {
                Status = isPaused ? ArchiveJobLifecycleState.Paused : ArchiveJobLifecycleState.Running,
                CurrentStage = isPaused ? "Paused" : snap.Stage,
                ProcessedBytes = snap.ProcessedBytes,
                TotalBytes = snap.TotalBytes > 0 ? snap.TotalBytes : current.TotalBytes,
                ProcessedFiles = snap.ProcessedFiles,
                TotalFiles = snap.TotalFiles > 0 ? snap.TotalFiles : current.TotalFiles,
                CompletedBlocks = snap.CompletedBlocks,
                TotalBlocks = snap.TotalBlocks,
                ThroughputMbPerSec = isPaused ? 0 : snap.ThroughputMbPerSec,
                EstimatedRemainingSeconds = isPaused ? null : snap.EstimatedRemainingSeconds,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
            _liveSnapshots[job.JobId] = updated;
            JobProgressChanged?.Invoke(updated);
        });

        var extractOptions = new UcxExtractOptions
        {
            SelectPatterns = job.SelectPatterns,
            ConflictPolicy = job.ConflictPolicy,
            PauseToken = effectivePause,
        };

        var result = await _reader.ExtractEntriesAsync(
            archivePath,
            job.ResolvedOutputPath,
            extractOptions,
            progress,
            jobToken).ConfigureAwait(false);

        var archiveSize = File.Exists(archivePath) ? new FileInfo(archivePath).Length : 0;
        var completedNow = DateTimeOffset.UtcNow;
        double durationSec = Math.Max(result.Elapsed.TotalSeconds, 0.001);
        double mbps = result.ExtractedBytes > 0
            ? (result.ExtractedBytes / (1024.0 * 1024.0)) / durationSec
            : 0.0;

        var completedJob = (_liveSnapshots.GetValueOrDefault(job.JobId) ?? job) with
        {
            Status = ArchiveJobLifecycleState.Completed,
            CurrentStage = "Completed",
            ProcessedBytes = result.ExtractedBytes,
            TotalBytes = result.ExtractedBytes,
            CompressedBytes = archiveSize,
            ProcessedFiles = result.ExtractedFiles,
            TotalFiles = result.ExtractedFiles + result.SkippedExistingFiles,
            CompletedBlocks = result.DecompressedBlockCount,
            TotalBlocks = result.DecompressedBlockCount,
            SkippedExisting = result.SkippedExistingFiles > 0,
            ThroughputMbPerSec = Math.Round(mbps, 2),
            EstimatedRemainingSeconds = 0,
            UpdatedUtc = completedNow,
            CompletedUtc = completedNow,
        };

        _history.Record(new CompressionHistoryRecord
        {
            JobId = job.JobId,
            Operation = "Extract",
            ArchivePath = archivePath,
            SourceSummary = archivePath,
            DestinationPath = result.DestinationDirectory,
            Profile = job.Profile.ToString(),
            FileCount = result.ExtractedFiles,
            OriginalBytes = result.ExtractedBytes,
            CompressedBytes = archiveSize,
            SavedBytes = 0,
            CompressionRatio = result.ExtractedBytes == 0 ? 1.0 : (double)archiveSize / result.ExtractedBytes,
            DurationMs = Math.Max(1, (long)result.Elapsed.TotalMilliseconds),
            ThroughputMbPerSec = Math.Round(mbps, 2),
            CompletedUtc = completedNow,
        });

        PersistAndPublish(completedJob);
        CompleteWaiters(completedJob);
    }

    private async Task ExecuteVerifyJobAsync(
        ArchiveJobSnapshot job,
        CooperativePauseController jobPause,
        IPauseToken effectivePause,
        CancellationToken jobToken)
    {
        var archivePath = job.InputPaths[0];
        var progress = new SyncProgress<ArchiveProgressSnapshot>(snap =>
        {
            if (_crashSimulated)
            {
                return;
            }

            var current = _liveSnapshots.GetValueOrDefault(job.JobId) ?? job;
            bool isPaused = jobPause.IsPaused || _globalPause.IsPaused;
            var updated = current with
            {
                Status = isPaused ? ArchiveJobLifecycleState.Paused : ArchiveJobLifecycleState.Running,
                CurrentStage = isPaused ? "Paused" : snap.Stage,
                ProcessedBytes = snap.ProcessedBytes,
                TotalBytes = snap.TotalBytes,
                ProcessedFiles = snap.ProcessedFiles,
                TotalFiles = snap.TotalFiles,
                CompletedBlocks = snap.CompletedBlocks,
                TotalBlocks = snap.TotalBlocks,
                ThroughputMbPerSec = isPaused ? 0 : snap.ThroughputMbPerSec,
                EstimatedRemainingSeconds = isPaused ? null : snap.EstimatedRemainingSeconds,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
            _liveSnapshots[job.JobId] = updated;
            JobProgressChanged?.Invoke(updated);
        });

        var result = await _reader.VerifyArchiveAsync(
            archivePath,
            progress,
            effectivePause,
            cancellationToken: jobToken).ConfigureAwait(false);

        var completedNow = DateTimeOffset.UtcNow;
        double durationSec = Math.Max(result.Elapsed.TotalSeconds, 0.001);
        double mbps = result.VerifiedOriginalBytes > 0
            ? (result.VerifiedOriginalBytes / (1024.0 * 1024.0)) / durationSec
            : 0.0;

        var completedJob = (_liveSnapshots.GetValueOrDefault(job.JobId) ?? job) with
        {
            Status = ArchiveJobLifecycleState.Completed,
            CurrentStage = "Completed",
            ProcessedBytes = result.VerifiedOriginalBytes,
            TotalBytes = result.VerifiedOriginalBytes,
            ProcessedFiles = result.VerifiedFiles,
            TotalFiles = result.VerifiedFiles,
            CompletedBlocks = result.VerifiedBlocks,
            TotalBlocks = result.VerifiedBlocks,
            ThroughputMbPerSec = Math.Round(mbps, 2),
            EstimatedRemainingSeconds = 0,
            UpdatedUtc = completedNow,
            CompletedUtc = completedNow,
        };

        PersistAndPublish(completedJob);
        CompleteWaiters(completedJob);
    }

    private void PersistAndPublish(ArchiveJobSnapshot snapshot)
    {
        _liveSnapshots[snapshot.JobId] = snapshot;
        _journal.SaveJob(snapshot);
        JobProgressChanged?.Invoke(snapshot);
    }

    private void CompleteWaiters(ArchiveJobSnapshot finalSnapshot)
    {
        if (_completionWaiters.TryRemove(finalSnapshot.JobId, out var tcs))
        {
            tcs.TrySetResult(finalSnapshot);
        }
    }

    private void SignalWorker()
    {
        try
        {
            if (_wakeSignal.CurrentCount == 0)
            {
                _wakeSignal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void CleanupStagingArtifacts(string? stagingPartPath)
    {
        if (string.IsNullOrWhiteSpace(stagingPartPath))
        {
            return;
        }

        try
        {
            if (File.Exists(stagingPartPath))
            {
                File.Delete(stagingPartPath);
            }
        }
        catch
        {
        }
    }

    private static string FormatSourceSummary(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0)
        {
            return string.Empty;
        }

        if (inputs.Count == 1)
        {
            return inputs[0];
        }

        return $"{inputs[0]} (+{inputs.Count - 1} more)";
    }

    public async ValueTask DisposeAsync()
    {
        await StopBackgroundWorkerAsync().ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        _workerLoopCts?.Cancel();
        _workerLoopCts?.Dispose();
        _wakeSignal.Dispose();
        _history.Dispose();
        _journal.Dispose();
    }

    private sealed record ActiveJobRuntime(
        string JobId,
        CooperativePauseController PauseController,
        CancellationTokenSource CancelCts,
        Task ExecutionTask);

    /// <summary>Synchronous IProgress implementation so progress snapshots update immediately without ThreadPool lag.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
