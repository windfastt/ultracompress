namespace UltraCompress.Engine;

/// <summary>Volatile engine state surfaced to the tray via named pipes.</summary>
public sealed class EngineRuntimeState
{
    private readonly JobQueueService _queue;

    public EngineRuntimeState(JobQueueService queue)
    {
        _queue = queue;
    }

    public string ServiceState { get; } = "running";

    public string EngineVersion { get; } = "0.3.0-queue";

    public int ActiveJobs => _queue.Counts().active;

    public int QueuedJobs => _queue.Counts().queued;

    public bool Paused => _queue.Paused;

    public JobQueueService Queue => _queue;

    public void SetPaused(bool paused) => _queue.SetPaused(paused);
}