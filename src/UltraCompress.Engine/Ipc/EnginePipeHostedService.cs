using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UltraCompress.Core;
using UltraCompress.Core.Archive;
using UltraCompress.Core.Destination;
using UltraCompress.Core.Errors;
using UltraCompress.Core.Ipc;
using UltraCompress.Core.Jobs;
using UltraCompress.Engine.Jobs;

namespace UltraCompress.Engine.Ipc;

/// <summary>Hosts the tray &amp; CLI ↔ engine newline-JSON protocol on a named pipe with concurrent session handling.</summary>
public sealed class EnginePipeHostedService : BackgroundService
{
    private readonly ILogger<EnginePipeHostedService> _logger;
    private readonly EngineRuntimeState _state;
    private readonly ResilientArchiveEngine _archiveEngine;
    private readonly string _pipeName;

    public EnginePipeHostedService(
        ILogger<EnginePipeHostedService> logger,
        EngineRuntimeState state,
        ResilientArchiveEngine archiveEngine,
        string? pipeName = null)
    {
        _logger = logger;
        _state = state;
        _archiveEngine = archiveEngine;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? EnginePipeNames.PipeName : pipeName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Engine pipe listening on \\\\.\\pipe\\{PipeName}", _pipeName);
        var activeSessions = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                var sessionTask = RunClientSessionAsync(server, stoppingToken);
                activeSessions.RemoveAll(t => t.IsCompleted);
                activeSessions.Add(sessionTask);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                _logger.LogError(ex, "Named pipe accept failed");
                await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken).ConfigureAwait(false);
            }
        }

        try
        {
            await Task.WhenAll(activeSessions).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task RunClientSessionAsync(NamedPipeServerStream server, CancellationToken stoppingToken)
    {
        await using (server.ConfigureAwait(false))
        {
            try
            {
                await HandleClientAsync(server, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Named pipe client session faulted");
            }
        }
    }

    internal async Task HandleClientAsync(Stream server, CancellationToken cancellationToken)
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
                Success = false,
                Message = "Invalid request JSON",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Type))
        {
            await WriteResponseAsync(server, new EngineResponse
            {
                Type = EngineMessageTypes.Error,
                Success = false,
                Message = "Missing request type",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        EngineResponse response;
        try
        {
            response = await DispatchRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var mapped = UltraCompressErrors.MapException(ex, request.Path ?? request.OutputPath);
            response = new EngineResponse
            {
                Type = EngineMessageTypes.Error,
                Id = request.Id,
                Success = false,
                Message = mapped.ToDisplayString(),
                Error = mapped,
            };
        }

        await WriteResponseAsync(server, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EngineResponse> DispatchRequestAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        return request.Type switch
        {
            EngineMessageTypes.Ping => new EngineResponse
            {
                Type = EngineMessageTypes.Pong,
                Id = request.Id,
                Success = true,
                EngineVersion = _state.EngineVersion,
            },
            EngineMessageTypes.GetStatus => BuildStatusResponse(request.Id),
            EngineMessageTypes.PauseAll => PauseAll(request.Id),
            EngineMessageTypes.ResumeAll => ResumeAll(request.Id),
            EngineMessageTypes.ListJobs => ListJobs(request.Id),
            EngineMessageTypes.EnqueueJob => EnqueueLegacyOrArchiveJob(request),
            EngineMessageTypes.CancelJob => await CancelJobAsync(request, cancellationToken).ConfigureAwait(false),
            EngineMessageTypes.Pack => await HandlePackAsync(request, cancellationToken).ConfigureAwait(false),
            EngineMessageTypes.ListArchive => await HandleListArchiveAsync(request, cancellationToken).ConfigureAwait(false),
            EngineMessageTypes.Extract => await HandleExtractAsync(request, cancellationToken).ConfigureAwait(false),
            EngineMessageTypes.Verify => await HandleVerifyAsync(request, cancellationToken).ConfigureAwait(false),
            EngineMessageTypes.History => HandleHistory(request),
            EngineMessageTypes.GetArchiveJob => HandleGetArchiveJob(request),
            EngineMessageTypes.PauseJob => HandlePauseJob(request),
            EngineMessageTypes.ResumeJob => HandleResumeJob(request),
            EngineMessageTypes.RollbackJob => await HandleRollbackJobAsync(request, cancellationToken).ConfigureAwait(false),
            EngineMessageTypes.ListInterruptedJobs => new EngineResponse
            {
                Type = EngineMessageTypes.InterruptedJobsResult,
                Id = request.Id,
                Success = true,
                InterruptedJobs = _archiveEngine.ListInterruptedJobs(),
            },
            _ => new EngineResponse
            {
                Type = EngineMessageTypes.Error,
                Id = request.Id,
                Success = false,
                Message = $"Unknown request type: {request.Type}",
            },
        };
    }

    private EngineResponse BuildStatusResponse(string? id)
    {
        var archiveJobs = _archiveEngine.ListJobs(200);
        int archiveActive = archiveJobs.Count(j => j.Status == ArchiveJobLifecycleState.Running);
        int archiveQueued = archiveJobs.Count(j => j.Status is ArchiveJobLifecycleState.Queued or ArchiveJobLifecycleState.Paused);

        return new EngineResponse
        {
            Type = EngineMessageTypes.Status,
            Id = id,
            Success = true,
            EngineVersion = _state.EngineVersion,
            ServiceState = _state.ServiceState,
            ActiveJobs = _state.ActiveJobs + archiveActive,
            QueuedJobs = _state.QueuedJobs + archiveQueued,
            Paused = _state.Paused || _archiveEngine.IsQueuePaused,
            InterruptedJobs = _archiveEngine.ListInterruptedJobs(),
        };
    }

    private EngineResponse PauseAll(string? id)
    {
        _state.SetPaused(true);
        _archiveEngine.SetQueuePaused(true);
        _logger.LogInformation("PauseAll requested over named pipe");
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
        _archiveEngine.SetQueuePaused(false);
        _logger.LogInformation("ResumeAll requested over named pipe");
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
        var archiveJobs = _archiveEngine.ListJobs(200);
        var combinedSummaries = _state.Queue.List().ToList();
        foreach (var aj in archiveJobs)
        {
            combinedSummaries.Add(new EngineJobSummary
            {
                JobId = aj.JobId,
                Path = aj.InputPaths.FirstOrDefault() ?? aj.ResolvedOutputPath,
                Profile = aj.Profile.ToString(),
                State = aj.Status.ToString(),
            });
        }

        return new EngineResponse
        {
            Type = EngineMessageTypes.JobList,
            Id = id,
            Success = true,
            Jobs = combinedSummaries,
            ArchiveJobs = archiveJobs,
            InterruptedJobs = _archiveEngine.ListInterruptedJobs(),
            ActiveJobs = _state.ActiveJobs + archiveJobs.Count(j => j.Status == ArchiveJobLifecycleState.Running),
            QueuedJobs = _state.QueuedJobs + archiveJobs.Count(j => j.Status is ArchiveJobLifecycleState.Queued or ArchiveJobLifecycleState.Paused),
            Paused = _state.Paused || _archiveEngine.IsQueuePaused,
        };
    }

    private EngineResponse EnqueueLegacyOrArchiveJob(EngineRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw UltraCompressErrors.InvalidArguments("path is required");
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

    private async Task<EngineResponse> CancelJobAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            throw UltraCompressErrors.InvalidArguments("jobId is required");
        }

        var okArchive = await _archiveEngine.CancelJobAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        var okLegacy = !okArchive && _state.Queue.Cancel(request.JobId);
        var ok = okArchive || okLegacy;

        return new EngineResponse
        {
            Type = EngineMessageTypes.Ack,
            Id = request.Id,
            Success = ok,
            JobId = request.JobId,
            ArchiveJob = okArchive ? _archiveEngine.GetJob(request.JobId) : null,
            Message = ok ? null : "Job not found or already completed",
        };
    }

    private async Task<EngineResponse> HandlePackAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        var inputs = request.InputPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList()
            ?? (string.IsNullOrWhiteSpace(request.Path) ? new List<string>() : [request.Path]);

        if (inputs.Count == 0)
        {
            throw UltraCompressErrors.EmptyInputSet();
        }

        var profile = CompressionProfileParser.ParseOrDefault(request.Profile, CompressionProfile.Balanced);

        var destMode = request.DestinationMode ?? (string.IsNullOrWhiteSpace(request.OutputPath)
            ? DestinationMode.SideBySide
            : DestinationMode.CustomDirectory);

        var snap = _archiveEngine.EnqueuePack(new ArchivePackJobRequest
        {
            InputPaths = inputs,
            OutputPathOrDirectory = request.OutputPath,
            Profile = profile,
            DestinationMode = destMode,
            ConflictPolicy = request.ConflictPolicy ?? DestinationConflictPolicy.Fail,
            PreferredCodecOverride = request.CodecOverride,
            PreserveRootFolderName = request.PreserveRootFolder ?? false,
        });

        if (request.WaitForCompletion == true && !snap.IsTerminal)
        {
            snap = await _archiveEngine.WaitForJobAsync(snap.JobId, cancellationToken).ConfigureAwait(false);
        }

        bool succeeded = snap.Status != ArchiveJobLifecycleState.Failed;
        return new EngineResponse
        {
            Type = succeeded ? EngineMessageTypes.ArchiveJobResult : EngineMessageTypes.Error,
            Id = request.Id,
            Success = succeeded,
            JobId = snap.JobId,
            ArchiveJob = snap,
            Error = snap.Error,
            Message = snap.Error?.ToDisplayString(),
        };
    }

    private async Task<EngineResponse> HandleListArchiveAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw UltraCompressErrors.InvalidArguments("Archive path is required for 'list'.");
        }

        var inspection = await _archiveEngine.InspectArchiveAsync(request.Path, cancellationToken).ConfigureAwait(false);
        var entries = request.SelectPatterns is { Count: > 0 }
            ? inspection.Entries.Where(e => UcxPathSecurity.MatchesSelectPatterns(e.RelativePath, request.SelectPatterns)).ToList()
            : inspection.Entries;

        return new EngineResponse
        {
            Type = EngineMessageTypes.ArchiveInspectionResult,
            Id = request.Id,
            Success = true,
            ArchiveInspection = inspection,
            ArchiveEntries = entries,
        };
    }

    private async Task<EngineResponse> HandleExtractAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw UltraCompressErrors.InvalidArguments("Archive path is required for 'extract'.");
        }

        var destMode = request.DestinationMode ?? (string.IsNullOrWhiteSpace(request.OutputPath)
            ? DestinationMode.ArchiveSubfolder
            : DestinationMode.CustomDirectory);

        var snap = _archiveEngine.EnqueueExtract(new ArchiveExtractJobRequest
        {
            ArchivePath = request.Path,
            OutputDirectory = request.OutputPath,
            SelectPatterns = request.SelectPatterns,
            DestinationMode = destMode,
            ConflictPolicy = request.ConflictPolicy ?? DestinationConflictPolicy.Fail,
        });

        if (request.WaitForCompletion == true && !snap.IsTerminal)
        {
            snap = await _archiveEngine.WaitForJobAsync(snap.JobId, cancellationToken).ConfigureAwait(false);
        }

        bool succeeded = snap.Status != ArchiveJobLifecycleState.Failed;
        return new EngineResponse
        {
            Type = succeeded ? EngineMessageTypes.ArchiveJobResult : EngineMessageTypes.Error,
            Id = request.Id,
            Success = succeeded,
            JobId = snap.JobId,
            ArchiveJob = snap,
            Error = snap.Error,
            Message = snap.Error?.ToDisplayString(),
        };
    }

    private async Task<EngineResponse> HandleVerifyAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw UltraCompressErrors.InvalidArguments("Archive path is required for 'verify'.");
        }

        var verify = await _archiveEngine.VerifyArchiveImmediateAsync(request.Path, cancellationToken).ConfigureAwait(false);
        return new EngineResponse
        {
            Type = EngineMessageTypes.VerifyResult,
            Id = request.Id,
            Success = verify.IsValid,
            VerifyResult = verify,
            Message = verify.Message,
        };
    }

    private EngineResponse HandleHistory(EngineRequest request)
    {
        int limit = request.Limit ?? 50;
        var entries = _archiveEngine.History.ListRecent(limit, request.OperationFilter);
        var summary = _archiveEngine.History.GetSummary();

        return new EngineResponse
        {
            Type = EngineMessageTypes.HistoryResult,
            Id = request.Id,
            Success = true,
            HistoryEntries = entries,
            HistorySummary = summary,
        };
    }

    private EngineResponse HandleGetArchiveJob(EngineRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            throw UltraCompressErrors.InvalidArguments("jobId is required.");
        }

        var job = _archiveEngine.GetJob(request.JobId)
            ?? throw UltraCompressErrors.InputNotFound(request.JobId);

        return new EngineResponse
        {
            Type = EngineMessageTypes.ArchiveJobResult,
            Id = request.Id,
            Success = true,
            JobId = job.JobId,
            ArchiveJob = job,
            Error = job.Error,
        };
    }

    private EngineResponse HandlePauseJob(EngineRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            throw UltraCompressErrors.InvalidArguments("jobId is required.");
        }

        var ok = _archiveEngine.PauseJob(request.JobId);
        return new EngineResponse
        {
            Type = EngineMessageTypes.Ack,
            Id = request.Id,
            Success = ok,
            JobId = request.JobId,
            ArchiveJob = _archiveEngine.GetJob(request.JobId),
            Message = ok ? "Job paused." : "Job could not be paused.",
        };
    }

    private EngineResponse HandleResumeJob(EngineRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            throw UltraCompressErrors.InvalidArguments("jobId is required.");
        }

        var resumed = _archiveEngine.ResumeJob(request.JobId);
        return new EngineResponse
        {
            Type = EngineMessageTypes.ArchiveJobResult,
            Id = request.Id,
            Success = true,
            JobId = resumed.JobId,
            ArchiveJob = resumed,
            Message = "Job resumed.",
        };
    }

    private async Task<EngineResponse> HandleRollbackJobAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            throw UltraCompressErrors.InvalidArguments("jobId is required.");
        }

        var rolledBack = await _archiveEngine.RollbackJobAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        return new EngineResponse
        {
            Type = EngineMessageTypes.ArchiveJobResult,
            Id = request.Id,
            Success = true,
            JobId = rolledBack.JobId,
            ArchiveJob = rolledBack,
            Message = "Job rolled back and staging artifacts deleted.",
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
