using UltraCompress.Core;
using UltraCompress.Core.Workers;

namespace UltraCompress.Core.Tests;

public sealed class WorkerStepQueueTests
{
    [Fact]
    public async Task Lease_and_complete_roundtrip()
    {
        var queue = new InMemoryWorkerStepQueue(TimeSpan.FromSeconds(30));
        var step = queue.EnqueueForJob(new CompressionStepRecord
        {
            StepId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            InputPath = "/tmp/a.bin",
            OutputPath = "/tmp/a.uc.zst",
            Profile = CompressionProfile.Small,
        });

        var waitTask = queue.WaitForStepAsync(step.StepId, CancellationToken.None);
        var leased = await queue.TryLeaseStepAsync("worker-a", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(leased);

        Assert.True(queue.TryCompleteStep(
            "worker-a",
            step.StepId,
            new StepCompletionResult(StepCompletionStatus.Succeeded, null, "/tmp/a.uc.zst", 10, 4),
            out _));

        var completion = await waitTask;
        Assert.Equal(StepCompletionStatus.Succeeded, completion.Status);
        Assert.Equal(4, completion.CompressedBytes);
    }

    [Fact]
    public void Token_validator_uses_constant_time_compare()
    {
        Assert.True(WorkerTokenValidator.TokensMatch("secret", "secret"));
        Assert.False(WorkerTokenValidator.TokensMatch("secret", "secrex"));
        Assert.False(WorkerTokenValidator.TokensMatch("secret", "sec"));
    }
}