using UltraCompress.Core;

namespace UltraCompress.Core.Workers;

public sealed class CompressionStepRecord
{
    public Guid StepId { get; init; }

    public Guid JobId { get; init; }

    public string InputPath { get; init; } = string.Empty;

    public string OutputPath { get; init; } = string.Empty;

    public CompressionProfile Profile { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum StepCompletionStatus
{
    Succeeded,
    Failed,
}

public sealed record StepCompletionResult(
    StepCompletionStatus Status,
    string? ErrorMessage,
    string? OutputPath,
    long? OriginalBytes,
    long? CompressedBytes);