using System.Text.Json.Serialization;
using UltraCompress.Core.Archive;
using UltraCompress.Core.Errors;
using UltraCompress.Core.History;
using UltraCompress.Core.Jobs;

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

    [JsonPropertyName("error")]
    public UltraCompressError? Error { get; init; }

    [JsonPropertyName("archiveJob")]
    public ArchiveJobSnapshot? ArchiveJob { get; init; }

    [JsonPropertyName("archiveJobs")]
    public IReadOnlyList<ArchiveJobSnapshot>? ArchiveJobs { get; init; }

    [JsonPropertyName("interruptedJobs")]
    public IReadOnlyList<InterruptedJobSummary>? InterruptedJobs { get; init; }

    [JsonPropertyName("archiveInspection")]
    public UcxArchiveInspection? ArchiveInspection { get; init; }

    [JsonPropertyName("archiveEntries")]
    public IReadOnlyList<UcxArchiveEntryView>? ArchiveEntries { get; init; }

    [JsonPropertyName("verifyResult")]
    public UcxArchiveVerifyResult? VerifyResult { get; init; }

    [JsonPropertyName("historyEntries")]
    public IReadOnlyList<CompressionHistoryRecord>? HistoryEntries { get; init; }

    [JsonPropertyName("historySummary")]
    public CompressionHistorySummary? HistorySummary { get; init; }
}
