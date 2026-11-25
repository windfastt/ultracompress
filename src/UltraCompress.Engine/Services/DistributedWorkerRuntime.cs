using Microsoft.Extensions.Options;
using UltraCompress.Core;
using UltraCompress.Core.Workers;

namespace UltraCompress.Engine.Services;

public sealed class DistributedWorkerRuntime
{
    public DistributedWorkerRuntime(
        IOptions<DistributedWorkerSettings> settings,
        WorkerFleetRegistry fleet,
        IWorkerStepQueue stepQueue,
        ILogger<DistributedWorkerRuntime> logger)
    {
        Settings = settings.Value;
        Fleet = fleet;
        StepQueue = stepQueue;
        Logger = logger;
        ExpectedToken = ResolveExpectedToken(Settings);
    }

    public DistributedWorkerSettings Settings { get; }

    public WorkerFleetRegistry Fleet { get; }

    public IWorkerStepQueue StepQueue { get; }

    public ILogger<DistributedWorkerRuntime> Logger { get; }

    public string ExpectedToken { get; }

    public bool IsEnabled => Settings.Enabled && !string.IsNullOrEmpty(ExpectedToken);

    public bool ValidateToken(string? token) =>
        WorkerTokenValidator.TokensMatch(ExpectedToken, token);

    private static string ResolveExpectedToken(DistributedWorkerSettings settings)
    {
        var fromEnv = Environment.GetEnvironmentVariable("ULTRACOMPRESS_WORKER_TOKEN");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        var path = WorkerTokenFileLifecycle.ResolveReadPath(settings.WorkerTokenFile);
        return WorkerTokenFileLifecycle.ReadTokenOrEmpty(path);
    }
}