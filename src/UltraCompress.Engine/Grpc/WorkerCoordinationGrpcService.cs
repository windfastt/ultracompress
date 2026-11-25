using Grpc.Core;
using UltraCompress.Core;
using UltraCompress.Core.Workers;
using UltraCompress.Engine.Services;

namespace UltraCompress.Engine.Grpc;

public sealed class WorkerCoordinationGrpcService : WorkerService.WorkerServiceBase
{
    private readonly DistributedWorkerRuntime _runtime;
    private readonly ILogger<WorkerCoordinationGrpcService> _logger;

    public WorkerCoordinationGrpcService(
        DistributedWorkerRuntime runtime,
        ILogger<WorkerCoordinationGrpcService> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public override Task<HeartbeatResponse> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        EnsureEnabledAndAuthorized(context);

        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId);
        var maxParallel = request.MaxParallelSteps <= 0 ? 1 : request.MaxParallelSteps;
        var machine = string.IsNullOrWhiteSpace(request.MachineName)
            ? Environment.MachineName
            : request.MachineName;

        _runtime.Fleet.RecordHeartbeat(request.WorkerId, machine, maxParallel);
        _logger.LogDebug("Worker heartbeat {WorkerId} from {Machine}", request.WorkerId, machine);

        return Task.FromResult(new HeartbeatResponse
        {
            Accepted = true,
            HeartbeatIntervalMs = _runtime.Settings.HeartbeatIntervalSeconds * 1000,
            LeaseTtlMs = _runtime.Settings.LeaseTtlSeconds * 1000,
        });
    }

    public override async Task<LeaseStepResponse> LeaseStep(LeaseStepRequest request, ServerCallContext context)
    {
        EnsureEnabledAndAuthorized(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId);

        var waitMs = request.WaitTimeoutMs <= 0 ? 5_000 : Math.Clamp(request.WaitTimeoutMs, 500, 120_000);
        var leased = await _runtime.StepQueue.TryLeaseStepAsync(
            request.WorkerId,
            TimeSpan.FromMilliseconds(waitMs),
            context.CancellationToken);

        if (leased is not { } activeLease)
        {
            return new LeaseStepResponse { Leased = false };
        }

        _runtime.Fleet.AdjustActiveLeases(request.WorkerId, 1);
        var step = activeLease.Step;

        return new LeaseStepResponse
        {
            Leased = true,
            Step = new StepAssignment
            {
                StepId = step.StepId.ToString("D"),
                JobId = step.JobId.ToString("D"),
                InputPath = step.InputPath,
                OutputPath = step.OutputPath,
                Profile = step.Profile.ToString(),
                LeaseExpiresUnixMs = activeLease.LeaseExpiresAt.ToUnixTimeMilliseconds(),
            },
        };
    }

    public override Task<ReportStepResultResponse> ReportStepResult(ReportStepResultRequest request, ServerCallContext context)
    {
        EnsureEnabledAndAuthorized(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId);

        if (!Guid.TryParse(request.StepId, out var stepId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "step_id must be a GUID."));
        }

        var result = request.Succeeded
            ? new StepCompletionResult(
                StepCompletionStatus.Succeeded,
                null,
                string.IsNullOrWhiteSpace(request.OutputPath) ? null : request.OutputPath,
                request.HasOriginalBytes ? request.OriginalBytes : null,
                request.HasCompressedBytes ? request.CompressedBytes : null)
            : new StepCompletionResult(
                StepCompletionStatus.Failed,
                string.IsNullOrWhiteSpace(request.ErrorMessage) ? "Remote worker reported failure." : request.ErrorMessage,
                null,
                null,
                null);

        var accepted = _runtime.StepQueue.TryCompleteStep(request.WorkerId, stepId, result, out var jobId);
        if (accepted)
        {
            _runtime.Fleet.AdjustActiveLeases(request.WorkerId, -1);
            _logger.LogInformation(
                "Step {StepId} for job {JobId} completed via worker {WorkerId} (succeeded={Succeeded})",
                stepId,
                jobId,
                request.WorkerId,
                request.Succeeded);
        }

        return Task.FromResult(new ReportStepResultResponse { Accepted = accepted });
    }

    private void EnsureEnabledAndAuthorized(ServerCallContext context)
    {
        if (!_runtime.IsEnabled)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Distributed workers are not enabled on this engine."));
        }

        var token = context.RequestHeaders.GetValue("x-uc-worker-token")
            ?? context.RequestHeaders.GetValue("authorization")?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase)?.Trim();

        if (!_runtime.ValidateToken(token))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid worker token."));
        }
    }
}