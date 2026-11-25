using UltraCompress.Core;
using UltraCompress.Core.Archive;
using UltraCompress.Core.Errors;
using UltraCompress.Core.Jobs;
using UltraCompress.Engine.Jobs;

namespace UltraCompress.ArchiveEngine.Tests;

public sealed class ResilientQueuePauseAndCrashRecoveryTests
{
    [Fact]
    public async Task CooperativePauseAndResume_PausesAtBlockBoundary_EmitsLiveProgressAndCompletesCleanly()
    {
        using var ws = new TempTestWorkspace();
        for (int i = 0; i < 6; i++)
        {
            ws.WriteTextFile($"src/file_{i}.cs", string.Concat(Enumerable.Repeat($"public class Module{i} {{ int Value = {i}; }}\n", 900)));
        }

        var dbPath = ws.SubPath("state", "jobs.db");
        var observedSnapshots = new List<ArchiveJobSnapshot>();
        var pausedAtBlockOne = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var engine = new ResilientArchiveEngine(new SqliteJobJournal(dbPath));
        engine.JobProgressChanged += snap =>
        {
            lock (observedSnapshots)
            {
                observedSnapshots.Add(snap);
            }
        };

        engine.OnAfterBlockCheckpointedAsync = async (jobId, cp, _) =>
        {
            if (cp.BlockIndex == 0)
            {
                engine.PauseJob(jobId);
                pausedAtBlockOne.TrySetResult(true);
            }

            await Task.CompletedTask;
        };

        engine.StartBackgroundWorker();

        var archivePath = ws.SubPath("out", "paused-resume.ucx");
        var enqueued = engine.EnqueuePack(new ArchivePackJobRequest
        {
            InputPaths = [ws.SubPath("src")],
            OutputPathOrDirectory = archivePath,
            MaxSolidBlockBytes = 32 * 1024,
        });

        await pausedAtBlockOne.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(80);

        var pausedSnap = engine.GetJob(enqueued.JobId)!;
        Assert.Equal(ArchiveJobLifecycleState.Paused, pausedSnap.Status);
        Assert.Equal("Paused", pausedSnap.CurrentStage);
        Assert.True(pausedSnap.CompletedBlocks >= 1);

        // Resume and wait for completion
        engine.ResumeJob(enqueued.JobId);
        var finalSnap = await engine.WaitForJobAsync(enqueued.JobId).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(ArchiveJobLifecycleState.Completed, finalSnap.Status);
        Assert.Equal(100.0, finalSnap.PercentComplete);
        Assert.Equal(6, finalSnap.ProcessedFiles);
        Assert.True(finalSnap.ThroughputMbPerSec >= 0);
        Assert.Equal(0, finalSnap.EstimatedRemainingSeconds);
        Assert.True(File.Exists(archivePath));
        Assert.False(File.Exists(archivePath + ".part"));

        lock (observedSnapshots)
        {
            Assert.Contains(observedSnapshots, s => s.CurrentStage is "Scanning" or "CompressingBlocks");
        }

        Assert.True((await new UcxArchiveReader().VerifyArchiveAsync(archivePath)).IsValid);
    }

    [Fact]
    public async Task CancelWhilePaused_WakesImmediately_DeletesPartStagingAndMarksCancelled()
    {
        using var ws = new TempTestWorkspace();
        for (int i = 0; i < 5; i++)
        {
            ws.WriteTextFile($"batch/item_{i}.json", string.Concat(Enumerable.Repeat($"{{\"index\":{i},\"text\":\"payload\"}}\n", 1000)));
        }

        var dbPath = ws.SubPath("state", "jobs.db");
        var blockZeroDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var engine = new ResilientArchiveEngine(new SqliteJobJournal(dbPath));
        engine.OnAfterBlockCheckpointedAsync = (jobId, cp, _) =>
        {
            if (cp.BlockIndex == 0)
            {
                engine.PauseJob(jobId);
                blockZeroDone.TrySetResult(true);
            }

            return ValueTask.CompletedTask;
        };

        engine.StartBackgroundWorker();
        var archivePath = ws.SubPath("cancel-target.ucx");
        var job = engine.EnqueuePack(new ArchivePackJobRequest
        {
            InputPaths = [ws.SubPath("batch")],
            OutputPathOrDirectory = archivePath,
            MaxSolidBlockBytes = 24 * 1024,
        });

        await blockZeroDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(archivePath + ".part"));

        var cancelled = await engine.CancelJobAsync(job.JobId);
        Assert.True(cancelled);

        var final = engine.GetJob(job.JobId)!;
        Assert.Equal(ArchiveJobLifecycleState.Cancelled, final.Status);
        Assert.Equal(UltraCompressErrorCode.JobCancelled, final.Error?.Code);
        Assert.False(File.Exists(archivePath + ".part"));
        Assert.False(File.Exists(archivePath));
    }

    [Fact]
    public async Task SimulatedCrashRecovery_DetectsInterruptedJob_TruncatesTornPartTail_AndResumesRemainingBlocksOnly()
    {
        using var ws = new TempTestWorkspace();
        // Create 6 distinct files of ~25 KiB each with MaxSolidBlockBytes = 30 KiB -> 6 sub-blocks!
        var expectedContents = new Dictionary<string, string>();
        for (int i = 0; i < 6; i++)
        {
            var content = string.Concat(Enumerable.Repeat($"File #{i} line with deterministic entropy seed {i * 17}.\n", 500));
            expectedContents[$"f{i}.txt"] = content;
            ws.WriteTextFile($"crash-src/f{i}.txt", content);
        }

        var dbPath = ws.SubPath("journal", "jobs.db");
        var archivePath = ws.SubPath("out", "crash-recovered.ucx");
        var partPath = archivePath + ".part";
        string crashedJobId;

        // First engine instance: commit 2 blocks to jobs.db + .ucx.part, then simulate abrupt crash!
        {
            var twoBlocksCommitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holdUntilCrash = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var engine1 = new ResilientArchiveEngine(new SqliteJobJournal(dbPath));
            engine1.OnAfterBlockCheckpointedAsync = async (_, cp, ct) =>
            {
                if (cp.BlockIndex == 1)
                {
                    twoBlocksCommitted.TrySetResult(true);
                    await holdUntilCrash.Task.WaitAsync(ct);
                }
            };

            engine1.StartBackgroundWorker();
            var job = engine1.EnqueuePack(new ArchivePackJobRequest
            {
                InputPaths = [ws.SubPath("crash-src")],
                OutputPathOrDirectory = archivePath,
                MaxSolidBlockBytes = 30 * 1024,
            });
            crashedJobId = job.JobId;

            await twoBlocksCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await engine1.SimulateAbruptCrashForTestingAsync();
        }

        Assert.False(File.Exists(archivePath));
        Assert.True(File.Exists(partPath));

        // Append simulated torn write garbage to the end of .ucx.part (simulating power-loss mid-block #2!)
        await using (var fs = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            await fs.WriteAsync(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE });
        }

        // Second engine instance (restart): RecoverOnStartup detects Interrupted job with 2 completed blocks!
        await using var engine2 = new ResilientArchiveEngine(new SqliteJobJournal(dbPath));
        var interrupted = engine2.RecoverOnStartup();

        var summary = Assert.Single(interrupted);
        Assert.Equal(crashedJobId, summary.JobId);
        Assert.Equal(2, summary.CompletedBlocks);
        Assert.True(summary.HasStagingFileOnDisk);
        Assert.Equal(ArchiveJobLifecycleState.Interrupted, engine2.GetJob(crashedJobId)!.Status);

        // One-click Resume!
        engine2.StartBackgroundWorker();
        engine2.ResumeJob(crashedJobId);

        var completed = await engine2.WaitForJobAsync(crashedJobId).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(ArchiveJobLifecycleState.Completed, completed.Status);
        Assert.Equal(6, completed.TotalBlocks);
        Assert.Equal(2, completed.ResumedBlocks); // Proves blocks 0 and 1 were reused from .ucx.part checkpoint!
        Assert.True(File.Exists(archivePath));
        Assert.False(File.Exists(partPath));
        Assert.Empty(engine2.Journal.GetBlockCheckpoints(crashedJobId));

        // Verify bit-exact integrity of all 6 files!
        var reader = new UcxArchiveReader();
        var verify = await reader.VerifyArchiveAsync(archivePath);
        Assert.True(verify.IsValid);
        Assert.Equal(6, verify.VerifiedFiles);

        var extractDir = ws.SubPath("crash-restored");
        await reader.ExtractEntriesAsync(archivePath, extractDir);
        foreach (var (name, expectedText) in expectedContents)
        {
            Assert.Equal(expectedText, await File.ReadAllTextAsync(Path.Combine(extractDir, name)));
        }
    }

    [Fact]
    public async Task SimulatedCrashRecovery_OneClickCleanRollback_DeletesStagingPartAndClearsCheckpoints()
    {
        using var ws = new TempTestWorkspace();
        for (int i = 0; i < 4; i++)
        {
            ws.WriteTextFile($"rollback-src/doc_{i}.md", string.Concat(Enumerable.Repeat($"# Section {i}\nBody text paragraph.\n", 800)));
        }

        var dbPath = ws.SubPath("journal", "jobs-rb.db");
        var archivePath = ws.SubPath("out", "rollback-target.ucx");
        var partPath = archivePath + ".part";
        string jobId;

        {
            var firstBlockDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holdUntilCrash = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var engine1 = new ResilientArchiveEngine(new SqliteJobJournal(dbPath));
            engine1.OnAfterBlockCheckpointedAsync = async (_, cp, ct) =>
            {
                if (cp.BlockIndex == 0)
                {
                    firstBlockDone.TrySetResult(true);
                    await holdUntilCrash.Task.WaitAsync(ct);
                }
            };

            engine1.StartBackgroundWorker();
            var job = engine1.EnqueuePack(new ArchivePackJobRequest
            {
                InputPaths = [ws.SubPath("rollback-src")],
                OutputPathOrDirectory = archivePath,
                MaxSolidBlockBytes = 16 * 1024,
            });
            jobId = job.JobId;
            await firstBlockDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await engine1.SimulateAbruptCrashForTestingAsync();
        }

        Assert.True(File.Exists(partPath));

        await using var engine2 = new ResilientArchiveEngine(new SqliteJobJournal(dbPath));
        var interrupted = engine2.RecoverOnStartup();
        Assert.Single(interrupted);

        var rolledBack = await engine2.RollbackJobAsync(jobId);
        Assert.Equal(ArchiveJobLifecycleState.RolledBack, rolledBack.Status);
        Assert.False(File.Exists(partPath));
        Assert.False(File.Exists(archivePath));
        Assert.Empty(engine2.Journal.GetBlockCheckpoints(jobId));
    }
}
