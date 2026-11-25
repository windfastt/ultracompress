using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UltraCompress.Core;
using UltraCompress.Core.Ipc;

namespace UltraCompress.Engine.Ipc;

/// <summary>Hosts the tray ↔ engine newline-JSON protocol on a named pipe.</summary>
public sealed class EnginePipeHostedService : BackgroundService
{
    private readonly ILogger<EnginePipeHostedService> _logger;
    private readonly EngineRuntimeState _state;

    public EnginePipeHostedService(ILogger<EnginePipeHostedService> logger, EngineRuntimeState state)
    {
        _logger = logger;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Engine pipe listening on \\\\.\\pipe\\{PipeName}", EnginePipeNames.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                EnginePipeNames.PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 4,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await HandleClientAsync(server, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Named pipe session failed");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        EngineRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<EngineRequest>(line, EngineJsonProtocol.SerializerOptions);
        }
        catch (JsonException)
        {
            await WriteResponseAsync(server, new EngineResponse
            {
                Type = EngineMessageTypes.Error,
                Message = "Invalid request JSON",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Type))
        {
            await WriteResponseAsync(server, new EngineResponse
            {
                Type = EngineMessageTypes.Error,
                Message = "Missing request type",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var response = request.Type switch
        {
            EngineMessageTypes.Ping => new EngineResponse
            {
                Type = EngineMessageTypes.Pong,
                Id = request.Id,
                EngineVersion = _state.EngineVersion,
            },
            EngineMessageTypes.GetStatus => new EngineResponse
            {
                Type = EngineMessageTypes.Status,
                Id = request.Id,
                EngineVersion = _state.EngineVersion,
                ServiceState = _state.ServiceState,
                ActiveJobs = _state.ActiveJobs,
                QueuedJobs = _state.QueuedJobs,
                Paused = _state.Paused,
            },
            EngineMessageTypes.PauseAll => PauseAll(request.Id),
            EngineMessageTypes.ResumeAll => ResumeAll(request.Id),
            EngineMessageTypes.ListJobs => ListJobs(request.Id),
            EngineMessageTypes.EnqueueJob => EnqueueJob(request),
            EngineMessageTypes.CancelJob => CancelJob(request),
            _ => new EngineResponse
            {
                Type = EngineMessageTypes.Error,
                Id = request.Id,
                Message = $"Unknown request type: {request.Type}",
            },
        };

        await WriteResponseAsync(server, response, cancellationToken).ConfigureAwait(false);
    }

    private EngineResponse PauseAll(string? id)
    {
        _state.SetPaused(true);
        _logger.LogInformation("PauseAll requested from tray");
        return new EngineResponse
        {
            Type = EngineMessageTypes.Ack,
            Id = id,
            Success = true,
            Paused = true,
        };
    }

    private EngineResponse ResumeAll(string? id)
    {
        _state.SetPaused(false);
        _logger.LogInformation("ResumeAll requested from tray");
        return new EngineResponse
        {
            Type = EngineMessageTypes.Ack,
            Id = id,
            Success = true,
            Paused = false,
        };
    }

    private EngineResponse ListJobs(string? id)
    {
        return new EngineResponse
        {
            Type = EngineMessageTypes.JobList,
            Id = id,
            Jobs = _state.Queue.List(),
            ActiveJobs = _state.ActiveJobs,
            QueuedJobs = _state.QueuedJobs,
            Paused = _state.Paused,
        };
    }

    private EngineResponse EnqueueJob(EngineRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return new EngineResponse
            {
                Type = EngineMessageTypes.Error,
                Id = request.Id,
                Message = "path is required",
            };
        }

        var profile = CompressionProfileParser.ParseOrDefault(request.Profile, CompressionProfile.Balanced);

        var jobId = _state.Queue.Enqueue(request.Path.Trim(), profile);
        _logger.LogInformation("Enqueued job {JobId} {Path} profile {Profile}", jobId, request.Path, profile);
        return new EngineResponse
        {
            Type = EngineMessageTypes.Ack,
            Id = request.Id,
            Success = true,
            JobId = jobId,
            ActiveJobs = _state.ActiveJobs,
            QueuedJobs = _state.QueuedJobs,
        };
    }

    private EngineResponse CancelJob(EngineRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            return new EngineResponse
            {
                Type = EngineMessageTypes.Error,
                Id = request.Id,
                Message = "jobId is required",
            };
        }

        var ok = _state.Queue.Cancel(request.JobId);
        return new EngineResponse
        {
            Type = EngineMessageTypes.Ack,
            Id = request.Id,
            Success = ok,
            Message = ok ? null : "Job not found or running",
            ActiveJobs = _state.ActiveJobs,
            QueuedJobs = _state.QueuedJobs,
        };
    }

    private static async Task WriteResponseAsync(Stream stream, EngineResponse response, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(response, EngineJsonProtocol.SerializerOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}