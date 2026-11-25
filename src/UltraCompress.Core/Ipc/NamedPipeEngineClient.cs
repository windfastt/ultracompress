using System.IO.Pipes;
using UltraCompress.Core.Destination;

namespace UltraCompress.Core.Ipc;

/// <summary>Tray and CLI named pipe client for communicating with UltraCompress.Engine.</summary>
public sealed class NamedPipeEngineClient : IEnginePipeClient, IDisposable
{
    private readonly TimeSpan _connectTimeout;
    private readonly string _pipeName;

    public NamedPipeEngineClient(TimeSpan? connectTimeout = null, string? pipeName = null)
    {
        _connectTimeout = connectTimeout ?? EnginePipeNames.DefaultConnectTimeout;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? EnginePipeNames.PipeName : pipeName;
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new EngineRequest { Type = EngineMessageTypes.Ping, Id = Guid.NewGuid().ToString("N") },
            cancellationToken).ConfigureAwait(false);
        return response?.Type == EngineMessageTypes.Pong;
    }

    public Task<EngineResponse?> GetStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync(
            new EngineRequest { Type = EngineMessageTypes.GetStatus, Id = Guid.NewGuid().ToString("N") },
            cancellationToken);

    public async Task<bool> PauseAllAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new EngineRequest { Type = EngineMessageTypes.PauseAll, Id = Guid.NewGuid().ToString("N") },
            cancellationToken).ConfigureAwait(false);
        return response?.Type == EngineMessageTypes.Ack && response.Success == true;
    }

    public async Task<bool> ResumeAllAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new EngineRequest { Type = EngineMessageTypes.ResumeAll, Id = Guid.NewGuid().ToString("N") },
            cancellationToken).ConfigureAwait(false);
        return response?.Type == EngineMessageTypes.Ack && response.Success == true;
    }

    public Task<EngineResponse?> ListJobsAsync(CancellationToken cancellationToken = default) =>
        SendAsync(
            new EngineRequest { Type = EngineMessageTypes.ListJobs, Id = Guid.NewGuid().ToString("N") },
            cancellationToken);

    public Task<EngineResponse?> EnqueueJobAsync(string path, string profile, CancellationToken cancellationToken = default) =>
        SendAsync(
            new EngineRequest
            {
                Type = EngineMessageTypes.EnqueueJob,
                Id = Guid.NewGuid().ToString("N"),
                Path = path,
                Profile = profile,
            },
            cancellationToken);

    public async Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new EngineRequest
            {
                Type = EngineMessageTypes.CancelJob,
                Id = Guid.NewGuid().ToString("N"),
                JobId = jobId,
            },
            cancellationToken).ConfigureAwait(false);
        return response?.Type == EngineMessageTypes.Ack && response.Success == true;
    }

    public Task<EngineResponse?> PackArchiveAsync(
        IReadOnlyList<string> inputPaths,
        string? outputPath = null,
        CompressionProfile profile = CompressionProfile.Balanced,
        DestinationMode destinationMode = DestinationMode.SideBySide,
        DestinationConflictPolicy conflictPolicy = DestinationConflictPolicy.Fail,
        string? codecOverride = null,
        bool waitForCompletion = false,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new EngineRequest
            {
                Type = EngineMessageTypes.Pack,
                Id = Guid.NewGuid().ToString("N"),
                InputPaths = inputPaths,
                OutputPath = outputPath,
                Profile = profile.ToString(),
                DestinationMode = destinationMode,
                ConflictPolicy = conflictPolicy,
                CodecOverride = codecOverride,
                WaitForCompletion = waitForCompletion,
            },
            cancellationToken);

    public Task<EngineResponse?> ListArchiveAsync(
        string archivePath,
        IReadOnlyList<string>? selectPatterns = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new EngineRequest
            {
                Type = EngineMessageTypes.ListArchive,
                Id = Guid.NewGuid().ToString("N"),
                Path = archivePath,
                SelectPatterns = selectPatterns,
            },
            cancellationToken);

    public Task<EngineResponse?> ExtractArchiveAsync(
        string archivePath,
        string? outputDirectory = null,
        IReadOnlyList<string>? selectPatterns = null,
        DestinationMode destinationMode = DestinationMode.CustomDirectory,
        DestinationConflictPolicy conflictPolicy = DestinationConflictPolicy.Fail,
        bool waitForCompletion = false,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new EngineRequest
            {
                Type = EngineMessageTypes.Extract,
                Id = Guid.NewGuid().ToString("N"),
                Path = archivePath,
                OutputPath = outputDirectory,
                SelectPatterns = selectPatterns,
                DestinationMode = destinationMode,
                ConflictPolicy = conflictPolicy,
                WaitForCompletion = waitForCompletion,
            },
            cancellationToken);

    public Task<EngineResponse?> VerifyArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new EngineRequest
            {
                Type = EngineMessageTypes.Verify,
                Id = Guid.NewGuid().ToString("N"),
                Path = archivePath,
            },
            cancellationToken);

    public Task<EngineResponse?> GetHistoryAsync(
        int limit = 50,
        string? operationFilter = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new EngineRequest
            {
                Type = EngineMessageTypes.History,
                Id = Guid.NewGuid().ToString("N"),
                Limit = limit,
                OperationFilter = operationFilter,
            },
            cancellationToken);

    public Task<EngineResponse?> GetArchiveJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        SendAsync(
            new EngineRequest
            {
                Type = EngineMessageTypes.GetArchiveJob,
                Id = Guid.NewGuid().ToString("N"),
                JobId = jobId,
            },
            cancellationToken);

    public Task<EngineResponse?> PauseJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        SendAsync(
            new EngineRequest
            {
                Type = EngineMessageTypes.PauseJob,
                Id = Guid.NewGuid().ToString("N"),
                JobId = jobId,
            },
            cancellationToken);

    public Task<EngineResponse?> ResumeJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        SendAsync(
            new EngineRequest
            {
                Type = EngineMessageTypes.ResumeJob,
                Id = Guid.NewGuid().ToString("N"),
                JobId = jobId,
            },
            cancellationToken);

    public Task<EngineResponse?> RollbackJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        SendAsync(
            new EngineRequest
            {
                Type = EngineMessageTypes.RollbackJob,
                Id = Guid.NewGuid().ToString("N"),
                JobId = jobId,
            },
            cancellationToken);

    public Task<EngineResponse?> ListInterruptedJobsAsync(CancellationToken cancellationToken = default) =>
        SendAsync(
            new EngineRequest
            {
                Type = EngineMessageTypes.ListInterruptedJobs,
                Id = Guid.NewGuid().ToString("N"),
            },
            cancellationToken);

    public async Task<EngineResponse?> SendAsync(EngineRequest request, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(_connectTimeout);

        try
        {
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new EngineResponse
            {
                Type = EngineMessageTypes.Error,
                Id = request.Id,
                Message = "Engine pipe connect timed out. Is UltraCompress.Engine running?",
            };
        }
        catch (Exception ex)
        {
            return new EngineResponse
            {
                Type = EngineMessageTypes.Error,
                Id = request.Id,
                Message = ex.Message,
            };
        }

        await EngineJsonProtocol.WriteRequestAsync(pipe, request, cancellationToken).ConfigureAwait(false);
        return await EngineJsonProtocol.ReadResponseAsync(pipe, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }
}
