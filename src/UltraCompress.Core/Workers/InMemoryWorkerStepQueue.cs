using System.Collections.Concurrent;

namespace UltraCompress.Core.Workers;

public sealed class InMemoryWorkerStepQueue : IWorkerStepQueue
{
    private readonly ConcurrentQueue<Guid> _pending = new();
    private readonly ConcurrentDictionary<Guid, CompressionStepRecord> _steps = new();
    private readonly ConcurrentDictionary<Guid, LeasedStepState> _leases = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<StepCompletionResult>> _waiters = new();
    private readonly TimeSpan _leaseTtl;

    public InMemoryWorkerStepQueue(TimeSpan leaseTtl) => _leaseTtl = leaseTtl;

    public int PendingCount => _pending.Count;

    public int LeasedCount => _leases.Count;

    public CompressionStepRecord EnqueueForJob(CompressionStepRecord step)
    {
        _steps[step.StepId] = step;
        _pending.Enqueue(step.StepId);
        _waiters[step.StepId] = new TaskCompletionSource<StepCompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        return step;
    }

    public async Task<LeasedStep?> TryLeaseStepAsync(
        string workerId,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + waitTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pending.TryDequeue(out var stepId) && _steps.TryGetValue(stepId, out var step))
            {
                if (_leases.ContainsKey(stepId))
                {
                    await Task.Delay(50, cancellationToken);
                    continue;
                }

                var expires = DateTimeOffset.UtcNow + _leaseTtl;
                _leases[stepId] = new LeasedStepState(workerId, expires);
                return new LeasedStep(step, expires);
            }

            await Task.Delay(50, cancellationToken);
        }

        return null;
    }

    public bool TryCompleteStep(string workerId, Guid stepId, StepCompletionResult result, out Guid jobId)
    {
        jobId = Guid.Empty;
        if (!_steps.TryGetValue(stepId, out var step))
        {
            return false;
        }

        if (!_leases.TryGetValue(stepId, out var lease) || !string.Equals(lease.WorkerId, workerId, StringComparison.Ordinal))
        {
            return false;
        }

        _leases.TryRemove(stepId, out _);
        _steps.TryRemove(stepId, out _);
        jobId = step.JobId;

        if (_waiters.TryRemove(stepId, out var tcs))
        {
            tcs.TrySetResult(result);
        }

        return true;
    }

    public void ReleaseExpiredLeases()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (stepId, lease) in _leases)
        {
            if (lease.ExpiresAt > now)
            {
                continue;
            }

            if (_leases.TryRemove(stepId, out _))
            {
                _pending.Enqueue(stepId);
            }
        }
    }

    public Task<StepCompletionResult> WaitForStepAsync(Guid stepId, CancellationToken cancellationToken)
    {
        if (!_waiters.TryGetValue(stepId, out var tcs))
        {
            throw new InvalidOperationException($"Step {stepId} is not registered.");
        }

        return tcs.Task.WaitAsync(cancellationToken);
    }

    private sealed record LeasedStepState(string WorkerId, DateTimeOffset ExpiresAt);
}