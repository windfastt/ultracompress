using Serilog;
using UltraCompress.Core;
using UltraCompress.Core.Ipc;
using UltraCompress.Core.Jobs;
using UltraCompress.Core.Workers;
using UltraCompress.Engine;
using UltraCompress.Engine.Grpc;
using UltraCompress.Engine.Ipc;
using UltraCompress.Engine.Services;

var logRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "UltraCompress",
    "logs");
Directory.CreateDirectory(logRoot);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.File(
        Path.Combine(logRoot, "engine-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) =>
    {
        var logDir = EnginePaths.GetLogDirectory();
        Directory.CreateDirectory(logDir);
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(logDir, "engine-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14);
    });

    builder.Host.UseWindowsService(options => options.ServiceName = EngineConstants.ServiceName);

    builder.Services.Configure<HostOptions>(options =>
    {
        options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
    });

    var distributedSection = builder.Configuration.GetSection("Engine:DistributedWorkers");
    var distributedSettings = distributedSection.Get<DistributedWorkerSettings>() ?? new DistributedWorkerSettings();
    builder.Services.Configure<DistributedWorkerSettings>(distributedSection);

    builder.Services.AddSingleton<IJobQueue, InMemoryJobQueue>();
    builder.Services.AddSingleton<JobQueueService>();
    builder.Services.AddSingleton<EngineRuntimeState>();
    builder.Services.AddSingleton(sp =>
    {
        var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DistributedWorkerSettings>>().Value;
        return new WorkerFleetRegistry(TimeSpan.FromSeconds(settings.HeartbeatIntervalSeconds * 2.5));
    });
    builder.Services.AddSingleton<IWorkerStepQueue>(sp =>
    {
        var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DistributedWorkerSettings>>().Value;
        return new InMemoryWorkerStepQueue(TimeSpan.FromSeconds(settings.LeaseTtlSeconds));
    });
    builder.Services.AddSingleton<DistributedWorkerRuntime>();
    builder.Services.AddSingleton<WorkerProcessRunner>();
    builder.Services.AddHostedService<EnginePipeHostedService>();
    builder.Services.AddHostedService<EngineStartupHostedService>();
    builder.Services.AddHostedService<JobSchedulerHostedService>();
    builder.Services.AddHostedService<WorkerLeaseReaperHostedService>();
    builder.Services.AddGrpc();

    var clientGrpcPort = builder.Configuration.GetValue("Engine:GrpcPort", EnginePaths.DefaultGrpcPort);
    var workerGrpcPort = builder.Configuration.GetValue(
        "Engine:DistributedWorkers:WorkerGrpcPort",
        DistributedWorkerSettings.DefaultWorkerGrpcPort);
    var workerListenHost = builder.Configuration.GetValue(
        "Engine:DistributedWorkers:WorkerListenHost",
        distributedSettings.WorkerListenHost) ?? "0.0.0.0";

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenLocalhost(clientGrpcPort, listenOptions =>
            listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);

        if (distributedSettings.Enabled)
        {
            options.Listen(System.Net.IPAddress.Parse(workerListenHost), workerGrpcPort, listenOptions =>
                listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
        }
    });

    var app = builder.Build();
    app.MapGrpcService<EngineGrpcService>();

    if (distributedSettings.Enabled)
    {
        app.MapGrpcService<WorkerCoordinationGrpcService>();
    }

    app.MapGet("/health", () => Results.Ok(new { status = "ok", service = EngineConstants.ServiceName }));

    Log.Information(
        "UltraCompress.Engine client gRPC localhost:{ClientPort}; named pipe \\\\.\\pipe\\{Pipe}; worker gRPC {WorkerState} on {WorkerHost}:{WorkerPort}",
        clientGrpcPort,
        EnginePipeNames.PipeName,
        distributedSettings.Enabled ? "enabled" : "disabled",
        workerListenHost,
        workerGrpcPort);
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Engine host terminated unexpectedly");
    Environment.Exit(1);
}
finally
{
    Log.CloseAndFlush();
}