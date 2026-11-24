using UltraCompress.Core;
using UltraCompress.Engine;

namespace UltraCompress.Core.Tests;

public sealed class JobQueueServiceTests
{
    [Fact]
    public void Enqueue_and_cancel_updates_depth()
    {
        var queue = new JobQueueService();
        var state = new EngineRuntimeState(queue);

        var id = queue.Enqueue(@"C:\data\file.bin", CompressionProfile.Small);
        Assert.Equal(1, state.QueuedJobs);
        Assert.True(queue.Cancel(id));
        Assert.Equal(0, state.QueuedJobs);
    }

    [Fact]
    public void Pause_marks_queued_jobs_paused()
    {
        var queue = new JobQueueService();
        queue.Enqueue(@"C:\a", CompressionProfile.Balanced);
        queue.SetPaused(true);
        var job = queue.List().Single();
        Assert.Equal("Paused", job.State);
    }
}