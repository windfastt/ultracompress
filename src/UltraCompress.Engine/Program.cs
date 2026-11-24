using Serilog;
using UltraCompress.Core;
using UltraCompress.Core.Jobs;
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

    builder.Services.AddSingleton<IJobQueue, InMemoryJobQueue>();
    builder.Services.AddSingleton<JobQueueService>();
    builder.Services.AddSingleton<EngineRuntimeState>();
    builder.Services.AddSingleton<WorkerProcessRunner>();
    builder.Services.AddHostedService<EnginePipeHostedService>();
    builder.Services.AddHostedService<EngineStartupHostedService>();
    builder.Services.AddHostedService<JobSchedulerHostedService>();
    builder.Services.AddGrpc();

    var grpcPort = builder.Configuration.GetValue("Engine:GrpcPort", EnginePaths.DefaultGrpcPort);
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenLocalhost(grpcPort, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
    });

    var app = builder.Build();
    app.MapGrpcService<EngineGrpcService>();
    app.MapGet("/health", () => Results.Ok(new { status = "ok", service = EngineConstants.ServiceName }));

    Log.Information(
        "UltraCompress.Engine gRPC on localhost:{GrpcPort}; named pipe \\\\.\\pipe\\{Pipe}",
        grpcPort,
        EnginePipeNames.PipeName);
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