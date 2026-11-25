using UltraCompress.Core.Ipc;
using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.Services;
using UltraCompress.Desktop.Presentation.ViewModels;

namespace UltraCompress.Desktop.Tests;

public sealed class QueueAndCrashRecoveryTests : IDisposable
{
    private readonly string _tempDir;

    public QueueAndCrashRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"uc-desktop-queue-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task Queue_PauseResumeCancelAndPriorityReorder_WorkCooperatively()
    {
        var stateDir = Path.Combine(_tempDir, "state");
        var journal = new DesktopJournalStore(stateDir);
        var analyzer = new BatchContentAnalyzer();

        var f1 = Path.Combine(_tempDir, "part1.txt");
        var f2 = Path.Combine(_tempDir, "part2.txt");
        await File.WriteAllTextAsync(f1, new string('X', 8192));
        await File.WriteAllTextAsync(f2, new string('Y', 8192));

        var (staged, _) = await analyzer.AnalyzePathsAsync([f1, f2], DesktopLosslessProfile.Balanced);

        var queueVm = new QueueRecoveryViewModel(journal, loadJournalOnInit: false)
        {
            AutoExecuteEnqueuedJobs = false,
        };

        var jobLow = queueVm.EnqueueBatchJob("Low.ucx", staged, Path.Combine(_tempDir, "Low.ucx"), DesktopLosslessProfile.Balanced, DestinationConflictMode.Rename, initialPriority: 1);
        var jobHigh = queueVm.EnqueueBatchJob("High.ucx", staged, Path.Combine(_tempDir, "High.ucx"), DesktopLosslessProfile.Balanced, DestinationConflictMode.Rename, initialPriority: 1);

        queueVm.MovePriorityUp(jobHigh.JobId);
        Assert.Equal(jobHigh.JobId, queueVm.Jobs[0].JobId);
        Assert.True(jobHigh.Priority > jobLow.Priority);

        var runTask = queueVm.ExecuteJobByIdAsync(jobHigh.JobId, perEntryDelay: TimeSpan.FromMilliseconds(60));
        await Task.Delay(30);
        await queueVm.PauseJobAsync(jobHigh.JobId);
        Assert.Equal(DesktopJobState.Paused, jobHigh.State);

        await Task.Delay(50);
        Assert.False(runTask.IsCompleted);

        await queueVm.ResumeJobAsync(jobHigh.JobId);
        await runTask;

        Assert.Equal(DesktopJobState.Completed, jobHigh.State);
        Assert.Equal(100.0, jobHigh.ProgressPercent);
        Assert.True(File.Exists(jobHigh.OutputPath));

        await queueVm.CancelJobAsync(jobLow.JobId);
        Assert.Equal(DesktopJobState.Cancelled, jobLow.State);
        Assert.False(File.Exists(jobLow.TempStagingPath));
    }

    [Fact]
    public async Task CrashRecovery_InterruptedJournalEntry_SupportsResumeAndDiscardTemp()
    {
        var stateDir = Path.Combine(_tempDir, "crash-state");
        var journal = new DesktopJournalStore(stateDir);

        var sourceFile = Path.Combine(_tempDir, "important.csv");
        await File.WriteAllTextAsync(sourceFile, string.Concat(Enumerable.Repeat("col1,col2,col3\n1,2,3\n", 100)));

        var targetUcx1 = Path.Combine(_tempDir, "recovered.ucx");
        var targetUcx2 = Path.Combine(_tempDir, "discarded.ucx");
        var partPath1 = journal.GetStagingPartPath(targetUcx1, "job-resume");
        var partPath2 = journal.GetStagingPartPath(targetUcx2, "job-discard");
        await File.WriteAllBytesAsync(partPath1, new byte[2048]);
        await File.WriteAllBytesAsync(partPath2, new byte[4096]);

        journal.SaveActiveJournalEntries(
        [
            new PersistedQueueJournalEntry
            {
                JobId = "job-resume",
                Title = "recovered.ucx",
                SourcePaths = [sourceFile],
                OutputPath = targetUcx1,
                TempPartFilePath = partPath1,
                ProfileName = "Archive",
                TotalBytes = new FileInfo(sourceFile).Length,
                LastKnownState = DesktopJobState.Running,
                ProgressPercent = 47.5,
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
            },
            new PersistedQueueJournalEntry
            {
                JobId = "job-discard",
                Title = "discarded.ucx",
                SourcePaths = [sourceFile],
                OutputPath = targetUcx2,
                TempPartFilePath = partPath2,
                ProfileName = "Balanced",
                TotalBytes = new FileInfo(sourceFile).Length,
                LastKnownState = DesktopJobState.Paused,
                ProgressPercent = 15.0,
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            },
        ]);

        var restartedVm = new QueueRecoveryViewModel(journal, loadJournalOnInit: true)
        {
            AutoExecuteEnqueuedJobs = false,
        };

        Assert.True(restartedVm.HasCrashRecoveryItems);
        Assert.Equal(2, restartedVm.InterruptedJobs.Count);

        restartedVm.DiscardInterruptedJobTemp("job-discard");
        Assert.False(File.Exists(partPath2));
        Assert.Single(restartedVm.InterruptedJobs);

        var resumedJob = await restartedVm.ResumeInterruptedJobAsync("job-resume");
        Assert.NotNull(resumedJob);
        Assert.False(restartedVm.HasCrashRecoveryItems);

        await restartedVm.ProcessQueueAsync();
        Assert.Equal(DesktopJobState.Completed, resumedJob!.State);
        Assert.True(File.Exists(targetUcx1));
    }

    [Fact]
    public async Task EnginePipeIntegration_OnlineAndOffline_UpdatesQueueStatusCleanly()
    {
        var fakePipe = new FakeEngineBridge { Online = true };
        var journal = new DesktopJournalStore(Path.Combine(_tempDir, "pipe-state"));
        var vm = new QueueRecoveryViewModel(journal, engineBridge: fakePipe, loadJournalOnInit: false);

        await vm.RefreshEngineConnectionAsync();
        Assert.True(vm.IsEngineConnected);
        Assert.Contains("Connected to Engine", vm.EngineStatusSummary);

        await vm.PauseAllAsync();
        Assert.True(fakePipe.PauseAllCalled);

        await vm.ResumeAllAsync();
        Assert.True(fakePipe.ResumeAllCalled);

        fakePipe.Online = false;
        await vm.RefreshEngineConnectionAsync();
        Assert.False(vm.IsEngineConnected);
        Assert.Contains("offline", vm.EngineStatusSummary, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }

    private sealed class FakeEngineBridge : IDesktopEngineBridge
    {
        public bool Online { get; set; }

        public bool PauseAllCalled { get; private set; }

        public bool ResumeAllCalled { get; private set; }

        public Task<EngineResponse?> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<EngineResponse?>(Online
                ? new EngineResponse
                {
                    Type = EngineMessageTypes.Status,
                    EngineVersion = "1.0.0",
                    ActiveJobs = 1,
                    QueuedJobs = 2,
                    Paused = false,
                }
                : null);

        public Task<bool> PauseAllAsync(CancellationToken cancellationToken = default)
        {
            PauseAllCalled = true;
            return Task.FromResult(Online);
        }

        public Task<bool> ResumeAllAsync(CancellationToken cancellationToken = default)
        {
            ResumeAllCalled = true;
            return Task.FromResult(Online);
        }

        public Task<EngineResponse?> EnqueueJobAsync(string path, string profile, CancellationToken cancellationToken = default) =>
            Task.FromResult<EngineResponse?>(new EngineResponse { Type = EngineMessageTypes.Ack, Success = true, JobId = "pipe-1" });

        public Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
