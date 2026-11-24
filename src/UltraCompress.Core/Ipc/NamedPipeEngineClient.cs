using System.IO.Pipes;

namespace UltraCompress.Core.Ipc;

/// <summary>Tray-side named pipe client; short timeouts and no native codec dependencies.</summary>
public sealed class NamedPipeEngineClient : IEnginePipeClient, IDisposable
{
    private readonly TimeSpan _connectTimeout;

    public NamedPipeEngineClient(TimeSpan? connectTimeout = null)
    {
        _connectTimeout = connectTimeout ?? EnginePipeNames.DefaultConnectTimeout;
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new EngineRequest { Type = EngineMessageTypes.Ping, Id = Guid.NewGuid().ToString("N") },
            cancellationToken).ConfigureAwait(false);
        return response?.Type == EngineMessageTypes.Pong;
    }

    public Task<EngineResponse?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(
            new EngineRequest { Type = EngineMessageTypes.GetStatus, Id = Guid.NewGuid().ToString("N") },
            cancellationToken);
    }

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

    public Task<EngineResponse?> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(
            new EngineRequest { Type = EngineMessageTypes.ListJobs, Id = Guid.NewGuid().ToString("N") },
            cancellationToken);
    }

    public Task<EngineResponse?> EnqueueJobAsync(string path, string profile, CancellationToken cancellationToken = default)
    {
        return SendAsync(
            new EngineRequest
            {
                Type = EngineMessageTypes.EnqueueJob,
                Id = Guid.NewGuid().ToString("N"),
                Path = path,
                Profile = profile,
            },
            cancellationToken);
    }

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

    private async Task<EngineResponse?> SendAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            EnginePipeNames.PipeName,
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
                Message = "Engine pipe connect timed out. Is UltraCompress.Engine running?",
            };
        }
        catch (Exception ex)
        {
            return new EngineResponse
            {
                Type = EngineMessageTypes.Error,
                Message = ex.Message,
            };
        }

        await EngineJsonProtocol.WriteRequestAsync(pipe, request, cancellationToken).ConfigureAwait(false);
        return await EngineJsonProtocol.ReadResponseAsync(pipe, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Stateless client; nothing to dispose.
    }
}