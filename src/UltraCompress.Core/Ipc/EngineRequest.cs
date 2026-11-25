using System.Text.Json.Serialization;
using UltraCompress.Core.Destination;

namespace UltraCompress.Core.Ipc;

public sealed class EngineRequest
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("inputPaths")]
    public IReadOnlyList<string>? InputPaths { get; init; }

    [JsonPropertyName("outputPath")]
    public string? OutputPath { get; init; }

    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("selectPatterns")]
    public IReadOnlyList<string>? SelectPatterns { get; init; }

    [JsonPropertyName("destinationMode")]
    public DestinationMode? DestinationMode { get; init; }

    [JsonPropertyName("conflictPolicy")]
    public DestinationConflictPolicy? ConflictPolicy { get; init; }

    [JsonPropertyName("codecOverride")]
    public string? CodecOverride { get; init; }

    [JsonPropertyName("preserveRootFolder")]
    public bool? PreserveRootFolder { get; init; }

    [JsonPropertyName("waitForCompletion")]
    public bool? WaitForCompletion { get; init; }

    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    [JsonPropertyName("operationFilter")]
    public string? OperationFilter { get; init; }
}
