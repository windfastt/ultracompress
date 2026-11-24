using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using UltraCompress.Engine;
using UltraCompress.Engine.Ipc;

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

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "UltraCompress.Engine";
});

builder.Services.AddSingleton<JobQueueService>();
builder.Services.AddSingleton<EngineRuntimeState>();
builder.Services.AddHostedService<EnginePipeHostedService>();
builder.Services.AddHostedService<Worker>();

builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);