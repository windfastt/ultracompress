using UltraCompress.Core;
using UltraCompress.Core.Jobs;

namespace UltraCompress.Core.Tests;

public class InMemoryJobQueueTests
{
    [Fact]
    public void Enqueue_ThenDequeue_MarksRunning()
    {
        var queue = new InMemoryJobQueue();
        var job = queue.Enqueue(Path.GetTempFileName(), CompressionProfile.Small);

        Assert.True(queue.TryDequeue(out var dequeued));
        Assert.NotNull(dequeued);
        Assert.Equal(job.JobId, dequeued!.JobId);
        Assert.Equal(JobStatus.Running, dequeued.Status);
        Assert.NotNull(dequeued.StartedAt);
    }

    [Fact]
    public void Pause_PreventsDequeue()
    {
        var queue = new InMemoryJobQueue();
        queue.Enqueue(Path.GetTempFileName(), CompressionProfile.Balanced);
        queue.SetPaused(true);

        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void CancelJob_OnQueued_Succeeds()
    {
        var queue = new InMemoryJobQueue();
        var job = queue.Enqueue(Path.GetTempFileName(), CompressionProfile.Balanced);

        Assert.True(queue.CancelJob(job.JobId));
        Assert.Equal(JobStatus.Cancelled, queue.GetJob(job.JobId)!.Status);
    }

    [Fact]
    public void RecoverOrphanedRunningJobs_Requeues()
    {
        var queue = new InMemoryJobQueue();
        var job = queue.Enqueue(Path.GetTempFileName(), CompressionProfile.Balanced);
        queue.TryDequeue(out _);

        var recovered = queue.RecoverOrphanedRunningJobs();
        Assert.Equal(1, recovered);
        Assert.Equal(JobStatus.Queued, queue.GetJob(job.JobId)!.Status);
        Assert.True(queue.TryDequeue(out var again));
        Assert.Equal(job.JobId, again!.JobId);
    }
}