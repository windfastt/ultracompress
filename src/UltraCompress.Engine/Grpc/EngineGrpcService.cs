using Grpc.Core;
using UltraCompress.Core.Jobs;
using CoreJobStatus = UltraCompress.Core.Jobs.JobStatus;

namespace UltraCompress.Engine.Grpc;

public sealed class EngineGrpcService : EngineService.EngineServiceBase
{
    private readonly IJobQueue _queue;
    private readonly ILogger<EngineGrpcService> _logger;

    public EngineGrpcService(IJobQueue queue, ILogger<EngineGrpcService> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    public override Task<EnqueueResponse> Enqueue(EnqueueRequest request, ServerCallContext context)
    {
        var profile = JobMapper.ParseProfile(request.Profile);
        var response = new EnqueueResponse();

        foreach (var path in request.InputPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var job = _queue.Enqueue(path, profile, string.IsNullOrWhiteSpace(request.OutputPath) ? null : request.OutputPath);
            response.JobIds.Add(job.JobId.ToString("D"));
            _logger.LogInformation("Enqueued job {JobId} path={Path} profile={Profile}", job.JobId, job.InputPath, profile);
        }

        return Task.FromResult(response);
    }

    public override Task<Grpc.JobSnapshot> GetJob(GetJobRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.JobId, out var jobId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "job_id must be a GUID."));
        }

        var job = _queue.GetJob(jobId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Job {jobId} was not found."));

        return Task.FromResult(JobMapper.ToSnapshot(job));
    }

    public override Task<ListJobsResponse> ListJobs(ListJobsRequest request, ServerCallContext context)
    {
        var limit = request.Limit <= 0 ? 100 : request.Limit;
        var response = new ListJobsResponse();
        foreach (var job in _queue.ListJobs(limit))
        {
            response.Jobs.Add(JobMapper.ToSnapshot(job));
        }

        return Task.FromResult(response);
    }

    public override Task<CancelJobResponse> CancelJob(CancelJobRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.JobId, out var jobId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "job_id must be a GUID."));
        }

        var cancelled = _queue.CancelJob(jobId);
        return Task.FromResult(new CancelJobResponse { Cancelled = cancelled });
    }

    public override Task<SetQueuePausedResponse> SetQueuePaused(SetQueuePausedRequest request, ServerCallContext context)
    {
        _queue.SetPaused(request.Paused);
        return Task.FromResult(new SetQueuePausedResponse { Paused = _queue.IsPaused });
    }

    public override Task<QueueState> GetQueueState(GetQueueStateRequest request, ServerCallContext context)
    {
        var jobs = _queue.ListJobs(500);
        return Task.FromResult(new QueueState
        {
            Paused = _queue.IsPaused,
            QueuedCount = jobs.Count(j => j.Status == CoreJobStatus.Queued),
            RunningCount = jobs.Count(j => j.Status == CoreJobStatus.Running),
        });
    }

    public override async Task WatchJobs(WatchJobsRequest request, IServerStreamWriter<JobEvent> responseStream, ServerCallContext context)
    {
        var interval = request.PollIntervalMs <= 0 ? 1000 : Math.Clamp(request.PollIntervalMs, 100, 30_000);
        var lastFingerprint = new Dictionary<Guid, string>();

        while (!context.CancellationToken.IsCancellationRequested)
        {
            foreach (var job in _queue.ListJobs(500))
            {
                var fingerprint = $"{job.Status}:{job.CompletedAt?.ToUnixTimeMilliseconds()}:{job.ErrorMessage}";
                if (lastFingerprint.TryGetValue(job.JobId, out var prior) && prior == fingerprint)
                {
                    continue;
                }

                lastFingerprint[job.JobId] = fingerprint;
                await responseStream.WriteAsync(new JobEvent { Job = JobMapper.ToSnapshot(job) }, context.CancellationToken);
            }

            await Task.Delay(interval, context.CancellationToken);
        }
    }
}