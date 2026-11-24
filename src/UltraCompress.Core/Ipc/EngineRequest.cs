using System.Text.Json.Serialization;

namespace UltraCompress.Core.Ipc;

public sealed class EngineRequest
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }
}