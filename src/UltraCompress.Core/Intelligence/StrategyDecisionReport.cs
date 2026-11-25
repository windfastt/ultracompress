using UltraCompress.Core.Safety;

namespace UltraCompress.Core.Intelligence;

/// <summary>
/// Selected lossless compression pipeline with explicit preprocessing, codec parameters, and executable recipe.
/// </summary>
public sealed record SelectedStrategyPlan
{
    public required string StrategyId { get; init; }

    public required string StrategyName { get; init; }

    public required IReadOnlyList<string> Preprocessors { get; init; }

    public required string PrimaryCodec { get; init; }

    public required int CodecLevel { get; init; }

    public string? Method { get; init; }

    public int? DictionaryOrWindowSizeKb { get; init; }

    public bool SolidBlockGrouping { get; init; }

    public bool DeduplicateExactFiles { get; init; }

    public required string RationaleSummary { get; init; }

    public required PipelineRecipe ExecutableRecipe { get; init; }
}

/// <summary>
/// Per-content-class routing slice within a single-file or heterogeneous multi-file workload.
/// </summary>
public sealed record WorkloadRoutePlan
{
    public required string RouteId { get; init; }

    public required string RouteName { get; init; }

    public required ContentClass ContentClass { get; init; }

    public required IReadOnlyList<string> Formats { get; init; }

    public required int FileCount { get; init; }

    public required long InputBytes { get; init; }

    public required double ShareOfBytesPercent { get; init; }

    public required IReadOnlyList<string> Preprocessors { get; init; }

    public required string Codec { get; init; }

    public required int CodecLevel { get; init; }

    public required string PipelineSummary { get; init; }

    public required double PredictedRatio { get; init; }

    public required long PredictedCompressedBytes { get; init; }

    public required SpeedTier SpeedTier { get; init; }

    public required string Rationale { get; init; }
}

/// <summary>
/// Candidate compression strategy explicitly rejected by <see cref="StrategySelector"/> with quantitative justification.
/// </summary>
public sealed record RejectedStrategyAlternative
{
    public required string StrategyName { get; init; }

    public required string ReasonCode { get; init; }

    public required string RejectionReason { get; init; }

    public required string EstimatedPenalty { get; init; }
}

/// <summary>
/// Complete explainable strategy decision report emitted by <see cref="StrategySelector"/> for files and directories.
/// </summary>
public sealed record StrategyDecisionReport
{
    public required string TargetPath { get; init; }

    public required bool IsDirectory { get; init; }

    public required CompressionProfile Profile { get; init; }

    /// <summary>
    /// Guaranteed <see cref="OperationKind.LosslessCompression"/> for all compression profiles.
    /// </summary>
    public OperationKind OperationKind { get; init; } = OperationKind.LosslessCompression;

    public string LosslessGuarantee { get; init; } =
        "100% bit-exact lossless compression verified by SHA-256/XXH64; zero lossy recompression, format transcoding, or metadata removal.";

    public required string DetectedFormat { get; init; }

    public required string DetectedMimeType { get; init; }

    public required ContentClass ContentClass { get; init; }

    public required WorkloadArchetype WorkloadArchetype { get; init; }

    public required int TotalFiles { get; init; }

    public required long TotalBytes { get; init; }

    public required long SampledBytes { get; init; }

    public required int PeakInspectionBufferBytes { get; init; }

    public required double InspectionDurationMs { get; init; }

    public required ContentInspectionSignals Signals { get; init; }

    public required IReadOnlyList<InspectionWindowSample> WindowSamples { get; init; }

    public int DuplicateFileCount { get; init; }

    public long DuplicateReclaimableBytes { get; init; }

    public required SelectedStrategyPlan SelectedPipeline { get; init; }

    public required IReadOnlyList<WorkloadRoutePlan> Routes { get; init; }

    public required IReadOnlyList<RejectedStrategyAlternative> RejectedAlternatives { get; init; }

    public required double PredictedRatio { get; init; }

    public required double PredictedSavingsPercent { get; init; }

    public required long PredictedCompressedBytes { get; init; }

    public required SpeedTier PredictedSpeedTier { get; init; }

    public required string EstimatedThroughputRange { get; init; }

    /// <summary>
    /// Non-executed informational note shown only when separate consent-gated modes
    /// (<see cref="OperationKind.LossyOptimization"/>, <see cref="OperationKind.FormatConversion"/>,
    /// or <see cref="OperationKind.DataRemoval"/>) could apply in the Optimization Studio.
    /// </summary>
    public string? ConsentGatedAdvisoryNote { get; init; }
}
