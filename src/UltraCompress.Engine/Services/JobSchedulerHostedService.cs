using UltraCompress.Core.Jobs;

namespace UltraCompress.Engine.Services;

/// <summary>
/// Drains the in-memory queue and runs each job in an isolated worker process.
/// </summary>
public sealed class JobSchedulerHostedService : BackgroundService
{
    private readonly IJobQueue _queue;
    private readonly WorkerProcessRunner _worker;
    private readonly ILogger<JobSchedulerHostedService> _logger;

    public JobSchedulerHostedService(
        IJobQueue queue,
        WorkerProcessRunner worker,
        ILogger<JobSchedulerHostedService> logger)
    {
        _queue = queue;
        _worker = worker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Job scheduler started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_queue.TryDequeue(out var job) || job is null)
                {
                    await Task.Delay(250, stoppingToken);
                    continue;
                }

                await RunJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected scheduler fault.");
                throw;
            }
        }

        _logger.LogInformation("Job scheduler stopping.");
    }

    private async Task RunJobAsync(CompressionJobRecord job, CancellationToken stoppingToken)
    {
        if (job.Status == JobStatus.Cancelled)
        {
            return;
        }

        _logger.LogInformation("Running job {JobId} for {Input}", job.JobId, job.InputPath);

        try
        {
            if (!File.Exists(job.InputPath))
            {
                Fail(job, $"Input file not found: {job.InputPath}");
                _queue.PublishJobUpdate(job);
                return;
            }

            var outputPath = job.OutputPath ?? job.InputPath + ".uc.zst";
            job.OutputPath = outputPath;

            var result = await _worker.RunCompressAsync(job.InputPath, job.Profile, outputPath, stoppingToken);

            if (job.Status == JobStatus.Cancelled)
            {
                return;
            }

            if (!result.Succeeded)
            {
                Fail(job, result.Detail);
                job.WorkerExitDetail = result.Detail;
                _queue.PublishJobUpdate(job);
                return;
            }

            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.OutputPath = result.OutputPath ?? outputPath;
            job.OriginalBytes = result.OriginalBytes;
            job.CompressedBytes = result.CompressedBytes;
            job.WorkerExitDetail = result.Detail;
            _queue.PublishJobUpdate(job);
        }
        catch (Exception ex)
        {
            Fail(job, ex.Message);
            _logger.LogError(ex, "Job {JobId} failed", job.JobId);
            _queue.PublishJobUpdate(job);
        }
    }

    private static void Fail(CompressionJobRecord job, string message)
    {
        job.Status = JobStatus.Failed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.ErrorMessage = message;
    }
}