using UltraCompress.Engine.Jobs;

namespace UltraCompress.Engine.Services;

/// <summary>
/// Hosted lifecycle wrapper for <see cref="ResilientArchiveEngine"/>: detects interrupted jobs
/// in <c>jobs.db</c> on startup and manages the resilient background worker loop.
/// </summary>
public sealed class ResilientArchiveHostedService : IHostedService
{
    private readonly ResilientArchiveEngine _archiveEngine;
    private readonly ILogger<ResilientArchiveHostedService> _logger;

    public ResilientArchiveHostedService(
        ResilientArchiveEngine archiveEngine,
        ILogger<ResilientArchiveHostedService> logger)
    {
        _archiveEngine = archiveEngine;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var interrupted = _archiveEngine.RecoverOnStartup();
        if (interrupted.Count > 0)
        {
            _logger.LogWarning(
                "Detected {Count} interrupted archive job(s) in jobs.db awaiting Resume or Clean Rollback.",
                interrupted.Count);
        }

        _archiveEngine.StartBackgroundWorker();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _archiveEngine.StopBackgroundWorkerAsync().ConfigureAwait(false);
    }
}
