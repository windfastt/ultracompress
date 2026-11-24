using System.Collections.Concurrent;
using UltraCompress.Core;
using UltraCompress.Core.Ipc;

namespace UltraCompress.Engine;

public sealed class JobQueueService
{
    private readonly ConcurrentDictionary<string, QueuedJob> _jobs = new();
    private volatile bool _paused;

    public bool Paused => _paused;

    public IReadOnlyList<EngineJobSummary> List()
    {
        return _jobs.Values
            .OrderBy(j => j.EnqueuedAt)
            .Select(j => new EngineJobSummary
            {
                JobId = j.JobId,
                Path = j.Path,
                Profile = j.Profile,
                State = j.State,
            })
            .ToList();
    }

    public (int active, int queued) Counts()
    {
        var list = _jobs.Values;
        var active = list.Count(j => j.State is "Running");
        var queued = list.Count(j => j.State is "Queued" or "Paused");
        return (active, queued);
    }

    public string Enqueue(string path, CompressionProfile profile)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var job = new QueuedJob(jobId, path, profile.ToString(), _paused ? "Paused" : "Queued");
        _jobs[jobId] = job;
        return jobId;
    }

    public bool Cancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        if (job.State == "Running")
        {
            return false;
        }

        _jobs.TryRemove(jobId, out _);
        return true;
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        foreach (var job in _jobs.Values.Where(j => j.State is "Queued" or "Paused"))
        {
            job.State = paused ? "Paused" : "Queued";
        }
    }

    private sealed class QueuedJob
    {
        public QueuedJob(string jobId, string path, string profile, string state)
        {
            JobId = jobId;
            Path = path;
            Profile = profile;
            State = state;
            EnqueuedAt = DateTimeOffset.UtcNow;
        }

        public string JobId { get; }
        public string Path { get; }
        public string Profile { get; }
        public string State { get; set; }
        public DateTimeOffset EnqueuedAt { get; }
    }
}