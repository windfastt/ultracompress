using UltraCompress.Core;

namespace UltraCompress.Core.Jobs;

public sealed class CompressionJobRecord
{
    public Guid JobId { get; init; }

    public string InputPath { get; init; } = string.Empty;

    public string? OutputPath { get; set; }

    public CompressionProfile Profile { get; init; }

    public JobStatus Status { get; set; } = JobStatus.Queued;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? ErrorMessage { get; set; }

    public long? OriginalBytes { get; set; }

    public long? CompressedBytes { get; set; }

    public string? WorkerExitDetail { get; set; }
}