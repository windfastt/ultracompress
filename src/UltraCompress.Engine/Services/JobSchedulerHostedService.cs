using UltraCompress.Core.Jobs;
using UltraCompress.Core.Workers;

namespace UltraCompress.Engine.Services;

/// <summary>
/// Drains the in-memory queue and runs each job in an isolated worker process.
/// </summary>
public sealed class JobSchedulerHostedService : BackgroundService
{
    private readonly IJobQueue _queue;
    private readonly WorkerProcessRunner _worker;
    private readonly DistributedWorkerRuntime _distributed;
    private readonly ILogger<JobSchedulerHostedService> _logger;

    public JobSchedulerHostedService(
        IJobQueue queue,
        WorkerProcessRunner worker,
        DistributedWorkerRuntime distributed,
        ILogger<JobSchedulerHostedService> logger)
    {
        _queue = queue;
        _worker = worker;
        _distributed = distributed;
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

            var remoteAttempt = await TryScheduleRemoteAsync(job, outputPath, stoppingToken);
            var result = remoteAttempt.UsedRemote
                ? remoteAttempt.Result
                : await _worker.RunCompressAsync(job.InputPath, job.Profile, outputPath, stoppingToken);

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

    private async Task<(bool UsedRemote, WorkerRunResult Result)> TryScheduleRemoteAsync(
        CompressionJobRecord job,
        string outputPath,
        CancellationToken stoppingToken)
    {
        var settings = _distributed.Settings;
        if (!settings.Enabled || !settings.PreferRemoteWorkers || !_distributed.IsEnabled)
        {
            return (false, WorkerRunResult.CreateFailure(-1, "Remote scheduling was not attempted."));
        }

        if (!_distributed.Fleet.HasAvailableWorkers())
        {
            if (settings.FallbackToLocalProcess)
            {
                return (false, WorkerRunResult.CreateFailure(-1, "No workers connected."));
            }

            throw new InvalidOperationException("No distributed workers are connected and local fallback is disabled.");
        }

        var step = new CompressionStepRecord
        {
            StepId = Guid.NewGuid(),
            JobId = job.JobId,
            InputPath = job.InputPath,
            OutputPath = outputPath,
            Profile = job.Profile,
        };

        _distributed.StepQueue.EnqueueForJob(step);
        _logger.LogInformation(
            "Dispatched step {StepId} for job {JobId} to worker fleet (paths must be visible to LAN workers).",
            step.StepId,
            job.JobId);

        StepCompletionResult completion;
        try
        {
            completion = await _distributed.StepQueue.WaitForStepAsync(step.StepId, stoppingToken);
        }
        catch (Exception ex)
        {
            return (true, WorkerRunResult.CreateFailure(-1, $"Distributed step failed: {ex.Message}"));
        }

        if (completion.Status == StepCompletionStatus.Succeeded)
        {
            return (true, WorkerRunResult.CreateSuccess(
                completion.OutputPath,
                completion.OriginalBytes,
                completion.CompressedBytes,
                "Completed on distributed worker."));
        }

        if (settings.FallbackToLocalProcess)
        {
            _logger.LogWarning(
                "Remote step for job {JobId} failed ({Error}); falling back to local process.",
                job.JobId,
                completion.ErrorMessage);
            return (false, WorkerRunResult.CreateFailure(-1, completion.ErrorMessage ?? "Remote worker failed."));
        }

        return (true, WorkerRunResult.CreateFailure(-1, completion.ErrorMessage ?? "Remote worker failed."));
    }

    private static void Fail(CompressionJobRecord job, string message)
    {
        job.Status = JobStatus.Failed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.ErrorMessage = message;
    }
}