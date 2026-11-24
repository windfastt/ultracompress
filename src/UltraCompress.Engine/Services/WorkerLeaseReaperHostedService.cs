using Microsoft.Extensions.Options;
using UltraCompress.Core.Workers;

namespace UltraCompress.Engine.Services;

/// <summary>
/// Re-queues leased steps after TTL when a LAN worker disappears (see improvisation playbook §4).
/// </summary>
public sealed class WorkerLeaseReaperHostedService : BackgroundService
{
    private readonly DistributedWorkerRuntime _runtime;
    private readonly ILogger<WorkerLeaseReaperHostedService> _logger;

    public WorkerLeaseReaperHostedService(
        DistributedWorkerRuntime runtime,
        ILogger<WorkerLeaseReaperHostedService> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_runtime.IsEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _runtime.Settings.LeaseTtlSeconds / 4));
        _logger.LogInformation("Worker lease reaper started (interval {Interval}s).", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var before = _runtime.StepQueue.LeasedCount;
                _runtime.StepQueue.ReleaseExpiredLeases();
                var after = _runtime.StepQueue.LeasedCount;
                if (before > after)
                {
                    _logger.LogWarning("Re-queued {Count} expired worker step lease(s).", before - after);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lease reaper fault.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}