using System.Text.Json.Serialization;

namespace UltraCompress.Core.Ipc;

public sealed class EngineResponse
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("engineVersion")]
    public string? EngineVersion { get; init; }

    [JsonPropertyName("serviceState")]
    public string? ServiceState { get; init; }

    [JsonPropertyName("activeJobs")]
    public int? ActiveJobs { get; init; }

    [JsonPropertyName("queuedJobs")]
    public int? QueuedJobs { get; init; }

    [JsonPropertyName("paused")]
    public bool? Paused { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("success")]
    public bool? Success { get; init; }

    [JsonPropertyName("jobs")]
    public IReadOnlyList<EngineJobSummary>? Jobs { get; init; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }
}