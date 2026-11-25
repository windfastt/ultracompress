using Grpc.Core;
using Grpc.Net.Client;
using UltraCompress.Core;
using UltraCompress.Core.Workers;
using UltraCompress.Engine.Grpc;
using UltraCompress.Worker;

var engineHost = GetArg("--engine", "127.0.0.1");
var enginePort = int.Parse(GetArg("--port", "17833"), System.Globalization.CultureInfo.InvariantCulture);
var workerId = GetArg("--worker-id", Environment.MachineName);
var tokenPath = GetArg("--token-file", EnginePaths.GetDefaultWorkerTokenPath());
var maxParallel = int.Parse(GetArg("--max-parallel", "1"), System.Globalization.CultureInfo.InvariantCulture);

var token = WorkerTokenValidator.LoadTokenFromFile(tokenPath);
if (string.IsNullOrEmpty(token))
{
    Console.Error.WriteLine($"Worker token file is missing or empty: {tokenPath}");
    return 1;
}

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var address = $"http://{engineHost}:{enginePort}";
using var channel = GrpcChannel.ForAddress(address);
var client = new WorkerService.WorkerServiceClient(channel);
var runner = new CompressionJobRunner();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var heartbeatTask = RunHeartbeatLoopAsync(client, workerId, maxParallel, token, cts.Token);
var leaseTask = RunLeaseLoopAsync(client, runner, workerId, token, cts.Token);

await Task.WhenAll(heartbeatTask, leaseTask);
return 0;

static string GetArg(string name, string fallback)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return fallback;
}

static Metadata AuthHeaders(string token) =>
    new() { { "x-uc-worker-token", token } };

static async Task RunHeartbeatLoopAsync(
    WorkerService.WorkerServiceClient client,
    string workerId,
    int maxParallel,
    string token,
    CancellationToken cancellationToken)
{
    var heartbeatIntervalMs = 30_000;
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            var response = await client.HeartbeatAsync(
                new HeartbeatRequest
                {
                    WorkerId = workerId,
                    MachineName = Environment.MachineName,
                    MaxParallelSteps = maxParallel,
                },
                headers: AuthHeaders(token),
                cancellationToken: cancellationToken);

            if (response.Accepted)
            {
                heartbeatIntervalMs = response.HeartbeatIntervalMs;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Heartbeat failed: {ex.Message}");
        }

        await Task.Delay(heartbeatIntervalMs, cancellationToken);
    }
}

static async Task RunLeaseLoopAsync(
    WorkerService.WorkerServiceClient client,
    CompressionJobRunner runner,
    string workerId,
    string token,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        LeaseStepResponse lease;
        try
        {
            lease = await client.LeaseStepAsync(
                new LeaseStepRequest { WorkerId = workerId, WaitTimeoutMs = 15_000 },
                headers: AuthHeaders(token),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"LeaseStep failed: {ex.Message}");
            await Task.Delay(2_000, cancellationToken);
            continue;
        }

        if (!lease.Leased || lease.Step is null)
        {
            continue;
        }

        var step = lease.Step;
        var profile = Enum.TryParse<CompressionProfile>(step.Profile, true, out var p)
            ? p
            : CompressionProfile.Small;

        ReportStepResultRequest report;
        try
        {
            var result = await runner.RunAsync(step.InputPath, profile, step.OutputPath, cancellationToken);
            report = new ReportStepResultRequest
            {
                WorkerId = workerId,
                StepId = step.StepId,
                Succeeded = true,
                OutputPath = result.OutputPath,
                OriginalBytes = result.OriginalBytes,
                CompressedBytes = result.CompressedBytes,
            };
        }
        catch (Exception ex)
        {
            report = new ReportStepResultRequest
            {
                WorkerId = workerId,
                StepId = step.StepId,
                Succeeded = false,
                ErrorMessage = ex.Message,
            };
        }

        try
        {
            await client.ReportStepResultAsync(report, headers: AuthHeaders(token), cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"ReportStepResult failed: {ex.Message}");
        }
    }
}