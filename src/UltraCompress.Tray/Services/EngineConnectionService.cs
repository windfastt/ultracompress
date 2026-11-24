using UltraCompress.Core.Ipc;

namespace UltraCompress.Tray.Services;

public sealed class EngineConnectionService : IDisposable
{
    private readonly NamedPipeEngineClient _client = new();

    public Task<bool> PingAsync(CancellationToken cancellationToken = default) =>
        _client.PingAsync(cancellationToken);

    public Task<EngineResponse?> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _client.GetStatusAsync(cancellationToken);

    public Task<bool> PauseAllAsync(CancellationToken cancellationToken = default) =>
        _client.PauseAllAsync(cancellationToken);

    public Task<bool> ResumeAllAsync(CancellationToken cancellationToken = default) =>
        _client.ResumeAllAsync(cancellationToken);

    public Task<EngineResponse?> ListJobsAsync(CancellationToken cancellationToken = default) =>
        _client.ListJobsAsync(cancellationToken);

    public Task<EngineResponse?> EnqueueJobAsync(string path, string profile, CancellationToken cancellationToken = default) =>
        _client.EnqueueJobAsync(path, profile, cancellationToken);

    public Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        _client.CancelJobAsync(jobId, cancellationToken);

    public void Dispose() => _client.Dispose();
}