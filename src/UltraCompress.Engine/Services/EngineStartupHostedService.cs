using UltraCompress.Core.Jobs;

namespace UltraCompress.Engine.Services;

/// <summary>
/// Re-queues jobs left in Running after a prior crash (SCM recovery).
/// </summary>
public sealed class EngineStartupHostedService : IHostedService
{
    private readonly IJobQueue _queue;
    private readonly ILogger<EngineStartupHostedService> _logger;

    public EngineStartupHostedService(IJobQueue queue, ILogger<EngineStartupHostedService> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var recovered = _queue.RecoverOrphanedRunningJobs();
        if (recovered > 0)
        {
            _logger.LogWarning("Recovered {Count} orphaned running job(s) into the queue.", recovered);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}