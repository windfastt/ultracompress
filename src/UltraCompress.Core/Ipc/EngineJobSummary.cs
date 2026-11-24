using System.Text.Json.Serialization;

namespace UltraCompress.Core.Ipc;

public sealed class EngineJobSummary
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("profile")]
    public required string Profile { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }
}