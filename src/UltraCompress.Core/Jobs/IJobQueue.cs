namespace UltraCompress.Core.Jobs;

public interface IJobQueue
{
    bool IsPaused { get; }

    CompressionJobRecord Enqueue(string inputPath, CompressionProfile profile, string? outputPath = null);

    bool TryDequeue(out CompressionJobRecord? job);

    CompressionJobRecord? GetJob(Guid jobId);

    IReadOnlyList<CompressionJobRecord> ListJobs(int limit = 100);

    bool CancelJob(Guid jobId);

    void SetPaused(bool paused);

    int RecoverOrphanedRunningJobs();

    void PublishJobUpdate(CompressionJobRecord job);

    event Action<CompressionJobRecord>? JobChanged;
}