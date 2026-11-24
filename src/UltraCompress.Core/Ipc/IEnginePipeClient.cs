namespace UltraCompress.Core.Ipc;

public interface IEnginePipeClient
{
    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    Task<EngineResponse?> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<bool> PauseAllAsync(CancellationToken cancellationToken = default);

    Task<bool> ResumeAllAsync(CancellationToken cancellationToken = default);

    Task<EngineResponse?> ListJobsAsync(CancellationToken cancellationToken = default);

    Task<EngineResponse?> EnqueueJobAsync(string path, string profile, CancellationToken cancellationToken = default);

    Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default);
}