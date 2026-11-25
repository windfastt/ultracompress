namespace UltraCompress.Core;

/// <summary>
/// Request from Explorer (or dev stub) to enqueue paths on the engine.
/// </summary>
public sealed record ShellEnqueueRequest(
    IReadOnlyList<string> Paths,
    CompressionProfile Profile,
    DateTimeOffset RequestedAtUtc);