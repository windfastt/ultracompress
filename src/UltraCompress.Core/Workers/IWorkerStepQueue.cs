namespace UltraCompress.Core.Workers;

public interface IWorkerStepQueue
{
    CompressionStepRecord EnqueueForJob(CompressionStepRecord step);

    Task<LeasedStep?> TryLeaseStepAsync(string workerId, TimeSpan waitTimeout, CancellationToken cancellationToken);

    bool TryCompleteStep(string workerId, Guid stepId, StepCompletionResult result, out Guid jobId);

    void ReleaseExpiredLeases();

    int PendingCount { get; }

    int LeasedCount { get; }

    Task<StepCompletionResult> WaitForStepAsync(Guid stepId, CancellationToken cancellationToken);
}

public sealed record LeasedStep(CompressionStepRecord Step, DateTimeOffset LeaseExpiresAt);