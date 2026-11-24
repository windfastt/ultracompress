using System.Collections.Concurrent;

namespace UltraCompress.Core.Workers;

public sealed class WorkerFleetRegistry
{
    private readonly ConcurrentDictionary<string, WorkerRegistration> _workers = new();
    private readonly TimeSpan _heartbeatGrace;

    public WorkerFleetRegistry(TimeSpan heartbeatGrace) => _heartbeatGrace = heartbeatGrace;

    public void RecordHeartbeat(string workerId, string machineName, int maxParallelSteps)
    {
        _workers[workerId] = new WorkerRegistration(
            workerId,
            machineName,
            Math.Max(1, maxParallelSteps),
            DateTimeOffset.UtcNow,
            0);
    }

    public void AdjustActiveLeases(string workerId, int delta)
    {
        if (!_workers.TryGetValue(workerId, out var existing))
        {
            return;
        }

        _workers[workerId] = existing with
        {
            ActiveLeases = Math.Max(0, existing.ActiveLeases + delta),
            LastHeartbeat = DateTimeOffset.UtcNow,
        };
    }

    public bool HasAvailableWorkers()
    {
        var now = DateTimeOffset.UtcNow;
        return _workers.Values.Any(w =>
            now - w.LastHeartbeat <= _heartbeatGrace &&
            w.ActiveLeases < w.MaxParallelSteps);
    }

    public int ConnectedCount(DateTimeOffset now) =>
        _workers.Values.Count(w => now - w.LastHeartbeat <= _heartbeatGrace);

    public int DegradedCount(DateTimeOffset now) =>
        _workers.Values.Count(w => now - w.LastHeartbeat > _heartbeatGrace);
}

public sealed record WorkerRegistration(
    string WorkerId,
    string MachineName,
    int MaxParallelSteps,
    DateTimeOffset LastHeartbeat,
    int ActiveLeases);