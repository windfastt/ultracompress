using System.Collections.Concurrent;
using UltraCompress.Core;

namespace UltraCompress.Core.Jobs;

public sealed class InMemoryJobQueue : IJobQueue
{
    private readonly ConcurrentDictionary<Guid, CompressionJobRecord> _jobs = new();
    private readonly ConcurrentQueue<Guid> _pending = new();
    private readonly object _pauseLock = new();
    private bool _paused;

    public bool IsPaused
    {
        get
        {
            lock (_pauseLock)
            {
                return _paused;
            }
        }
    }

    public event Action<CompressionJobRecord>? JobChanged;

    public CompressionJobRecord Enqueue(string inputPath, CompressionProfile profile, string? outputPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        var job = new CompressionJobRecord
        {
            JobId = Guid.NewGuid(),
            InputPath = Path.GetFullPath(inputPath),
            OutputPath = outputPath is null ? null : Path.GetFullPath(outputPath),
            Profile = profile,
            Status = JobStatus.Queued,
        };

        _jobs[job.JobId] = job;
        _pending.Enqueue(job.JobId);
        Notify(job);
        return job;
    }

    public bool TryDequeue(out CompressionJobRecord? job)
    {
        job = null;
        if (IsPaused)
        {
            return false;
        }

        while (_pending.TryDequeue(out var id))
        {
            if (!_jobs.TryGetValue(id, out var record))
            {
                continue;
            }

            if (record.Status != JobStatus.Queued)
            {
                continue;
            }

            record.Status = JobStatus.Running;
            record.StartedAt = DateTimeOffset.UtcNow;
            job = record;
            Notify(record);
            return true;
        }

        return false;
    }

    public CompressionJobRecord? GetJob(Guid jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job : null;

    public IReadOnlyList<CompressionJobRecord> ListJobs(int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);
        return _jobs.Values
            .OrderByDescending(j => j.CreatedAt)
            .Take(limit)
            .ToList();
    }

    public bool CancelJob(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
        {
            return false;
        }

        job.Status = JobStatus.Cancelled;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.ErrorMessage ??= "Cancelled by client.";
        Notify(job);
        return true;
    }

    public void SetPaused(bool paused)
    {
        lock (_pauseLock)
        {
            _paused = paused;
        }
    }

    public int RecoverOrphanedRunningJobs()
    {
        var count = 0;
        foreach (var job in _jobs.Values.Where(j => j.Status == JobStatus.Running))
        {
            job.Status = JobStatus.Queued;
            job.StartedAt = null;
            job.ErrorMessage = "Recovered after service restart.";
            _pending.Enqueue(job.JobId);
            Notify(job);
            count++;
        }

        return count;
    }

    public void PublishJobUpdate(CompressionJobRecord job) => Notify(job);

    private void Notify(CompressionJobRecord job) => JobChanged?.Invoke(job);
}