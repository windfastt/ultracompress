using UltraCompress.Core.Destination;

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

    Task<EngineResponse?> PackArchiveAsync(
        IReadOnlyList<string> inputPaths,
        string? outputPath = null,
        CompressionProfile profile = CompressionProfile.Balanced,
        DestinationMode destinationMode = DestinationMode.SideBySide,
        DestinationConflictPolicy conflictPolicy = DestinationConflictPolicy.Fail,
        string? codecOverride = null,
        bool waitForCompletion = false,
        CancellationToken cancellationToken = default);

    Task<EngineResponse?> ListArchiveAsync(
        string archivePath,
        IReadOnlyList<string>? selectPatterns = null,
        CancellationToken cancellationToken = default);

    Task<EngineResponse?> ExtractArchiveAsync(
        string archivePath,
        string? outputDirectory = null,
        IReadOnlyList<string>? selectPatterns = null,
        DestinationMode destinationMode = DestinationMode.CustomDirectory,
        DestinationConflictPolicy conflictPolicy = DestinationConflictPolicy.Fail,
        bool waitForCompletion = false,
        CancellationToken cancellationToken = default);

    Task<EngineResponse?> VerifyArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken = default);

    Task<EngineResponse?> GetHistoryAsync(
        int limit = 50,
        string? operationFilter = null,
        CancellationToken cancellationToken = default);

    Task<EngineResponse?> GetArchiveJobAsync(string jobId, CancellationToken cancellationToken = default);

    Task<EngineResponse?> PauseJobAsync(string jobId, CancellationToken cancellationToken = default);

    Task<EngineResponse?> ResumeJobAsync(string jobId, CancellationToken cancellationToken = default);

    Task<EngineResponse?> RollbackJobAsync(string jobId, CancellationToken cancellationToken = default);

    Task<EngineResponse?> ListInterruptedJobsAsync(CancellationToken cancellationToken = default);
}
