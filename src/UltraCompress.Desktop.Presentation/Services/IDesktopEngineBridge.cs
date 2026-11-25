using UltraCompress.Core.Ipc;

namespace UltraCompress.Desktop.Presentation.Services;

public interface IDesktopEngineBridge
{
    Task<EngineResponse?> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<bool> PauseAllAsync(CancellationToken cancellationToken = default);

    Task<bool> ResumeAllAsync(CancellationToken cancellationToken = default);

    Task<EngineResponse?> EnqueueJobAsync(string path, string profile, CancellationToken cancellationToken = default);

    Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default);
}

public sealed class EnginePipeClientBridge(IEnginePipeClient client) : IDesktopEngineBridge
{
    public Task<EngineResponse?> GetStatusAsync(CancellationToken cancellationToken = default) =>
        client.GetStatusAsync(cancellationToken);

    public Task<bool> PauseAllAsync(CancellationToken cancellationToken = default) =>
        client.PauseAllAsync(cancellationToken);

    public Task<bool> ResumeAllAsync(CancellationToken cancellationToken = default) =>
        client.ResumeAllAsync(cancellationToken);

    public Task<EngineResponse?> EnqueueJobAsync(string path, string profile, CancellationToken cancellationToken = default) =>
        client.EnqueueJobAsync(path, profile, cancellationToken);

    public Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        client.CancelJobAsync(jobId, cancellationToken);
}
