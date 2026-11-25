using System.Text.Json.Serialization;
using UltraCompress.Core.Safety;

namespace UltraCompress.Core.Intelligence;

/// <summary>
/// High-level content classification driving per-stream routing and preprocessing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContentClass
{
    Empty,
    CompressedVideo,
    CompressedAudio,
    UncompressedAudio,
    CompressedImage,
    UncompressedRasterImage,
    DatabaseAndTabular,
    SourceAndText,
    ExecutableBinary,
    CompressedArchive,
    ContainerDocument,
    GenericBinary,
    HeterogeneousWorkload,
}

/// <summary>
/// Canonical workload archetype identified for a single file or directory job.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkloadArchetype
{
    CompressedVideoStream,
    SourceRepository,
    DatabaseDump,
    HeterogeneousPhotoCollection,
    CompressedMediaOrArchive,
    UncompressedMediaOrRaster,
    ExecutableBinaries,
    MixedDirectory,
    GeneralFile,
}

/// <summary>
/// Expected execution throughput tier for a selected lossless pipeline.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpeedTier
{
    DiskSpeedBypass,
    UltraFast,
    Balanced,
    DeepArchival,
    HeavyMaxRatio,
}

/// <summary>
/// Logical position of a fixed-size inspection window within a stream.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WindowPosition
{
    Head,
    Quarter25,
    Midpoint50,
    Quarter75,
    Tail,
}

/// <summary>
/// Metadata entry describing a supported container or magic-byte signature in <see cref="MagicSignatureCatalog"/>.
/// </summary>
public sealed record FormatSignatureDefinition(
    string FormatId,
    string Category,
    string DisplayName,
    string MimeType,
    ContentClass DefaultClass);

/// <summary>
/// Concrete format identification result from header/tail magic bytes and structural container parsing.
/// </summary>
public sealed record SignatureMatch
{
    public required string FormatId { get; init; }

    public required string DisplayName { get; init; }

    public required string MimeType { get; init; }

    public required ContentClass ContentClass { get; init; }

    public required double Confidence { get; init; }

    public required string MatchedEvidence { get; init; }

    /// <summary>
    /// True when container markers (e.g. JPEG APP1 Exif, TIFF IFD metadata, MP4 udta/meta) indicate
    /// optional metadata or media optimization candidates outside <see cref="OperationKind.LosslessCompression"/>.
    /// </summary>
    public bool HasConsentGatedOptimizationCandidate { get; init; }

    /// <summary>
    /// Structural byte stride or page size parsed from container headers (e.g. 4096 for SQLite pages,
    /// 3/4 for RGB/RGBA BMP/TIFF, 4 for 16-bit stereo PCM WAV, 2 for UTF-16).
    /// </summary>
    public int? StructuralStrideBytes { get; init; }
}

/// <summary>
/// Per-window statistical sample captured during multi-window O(1)-memory stream inspection.
/// </summary>
public sealed record InspectionWindowSample
{
    public required WindowPosition Position { get; init; }

    public required long Offset { get; init; }

    public required int Length { get; init; }

    public required double EntropyBitsPerByte { get; init; }

    public required double Stride4DeltaEntropy { get; init; }

    public required double Stride8DeltaEntropy { get; init; }
}

/// <summary>
/// Aggregated statistical and micro-compression probe measurements across sampled windows.
/// </summary>
public sealed record ContentInspectionSignals
{
    public double MeanEntropyBitsPerByte { get; init; }

    public double InteriorEntropyBitsPerByte { get; init; }

    public double EntropyVariance { get; init; }

    public double EntropyStdDev { get; init; }

    public double MinWindowEntropy { get; init; }

    public double MaxWindowEntropy { get; init; }

    public double BigramConditionalEntropy { get; init; }

    public double BigramPredictability { get; init; }

    public double RunLengthScore { get; init; }

    public double Stride1EntropyReduction { get; init; }

    public double Stride2EntropyReduction { get; init; }

    public double Stride3EntropyReduction { get; init; }

    public double Stride4EntropyReduction { get; init; }

    public double Stride8EntropyReduction { get; init; }

    public int BestStrideBytes { get; init; }

    public double BestStrideEntropyReduction { get; init; }

    public int MicroProbeInputBytes { get; init; }

    public int MicroProbeCompressedBytes { get; init; }

    /// <summary>
    /// Compressed-to-original byte ratio on the 32 KiB stratified fast probe (e.g. 1.00 = incompressible, 0.22 = 78% savings).
    /// </summary>
    public double MicroProbeRatio { get; init; }

    /// <summary>
    /// Compressed-to-original ratio on the 32 KiB probe after lossless stride-delta / byte-shuffle preprocessing, when applicable.
    /// </summary>
    public double? PreprocessedMicroProbeRatio { get; init; }

    public double AsciiPrintableRatio { get; init; }

    public double ZeroByteRatio { get; init; }

    [JsonIgnore]
    public double MicroProbeSavingsFraction => Math.Max(0.0, 1.0 - MicroProbeRatio);
}

/// <summary>
/// Complete O(1)-memory multi-window inspection output for a single file or stream.
/// </summary>
public sealed record ContentInspectionResult
{
    public string? Path { get; init; }

    public required long SizeBytes { get; init; }

    public required long SampledBytes { get; init; }

    public required int PeakBufferBytes { get; init; }

    public required double InspectionDurationMs { get; init; }

    public required SignatureMatch Signature { get; init; }

    public required IReadOnlyList<InspectionWindowSample> Windows { get; init; }

    public required ContentInspectionSignals Signals { get; init; }

    /// <summary>
    /// Deterministic SHA-256 hex digest over stream size + sampled windows (or exact content hash for files fully covered by head window).
    /// </summary>
    public required string SampleFingerprintSha256 { get; init; }

    /// <summary>
    /// True when the entire file fit within the sampled windows so <see cref="SampleFingerprintSha256"/> covers 100% of file bytes.
    /// </summary>
    public bool IsFullContentHash { get; init; }
}
