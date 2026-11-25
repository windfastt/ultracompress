using System.Text.Json.Serialization;
using UltraCompress.Core.Intelligence;

namespace UltraCompress.Core;

/// <summary>
/// Static probe of a file used for advisor, caching, and recipe lookup.
/// </summary>
public sealed record FileFingerprint
{
    public required string Path { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTimeOffset LastModifiedUtc { get; init; }

    /// <summary>SHA-256 of file contents (hex, lowercase).</summary>
    public string? ContentHashSha256 { get; init; }

    /// <summary>Detected MIME or magic label (e.g. application/pdf, video/mp4).</summary>
    public string? DetectedType { get; init; }

    /// <summary>Shannon entropy in bits per byte (0–8), when computed.</summary>
    public double? EntropyBitsPerByte { get; init; }

    /// <summary>File extension including the dot, when present.</summary>
    public string? Extension { get; init; }

    /// <summary>Per-block entropy samples (full scan for small files, spaced samples for large).</summary>
    public IReadOnlyList<BlockEntropySample> BlockEntropies { get; init; } = Array.Empty<BlockEntropySample>();

    /// <summary>
    /// Rich O(1) multi-window content inspection attached at runtime for explainable strategy selection.
    /// Excluded from persistence JSON so persisted fingerprints stay compliant with file-fingerprint.schema.json.
    /// </summary>
    [JsonIgnore]
    public ContentInspectionResult? Inspection { get; init; }

    /// <summary>True when global entropy suggests the payload is already compressed or encrypted.</summary>
    public bool LikelyAlreadyCompressed => EntropyBitsPerByte is >= 7.5;

    public string SizeClass => SizeBytes switch
    {
        < 1024 => "tiny",
        < 1024 * 1024 => "small",
        < 100 * 1024 * 1024 => "medium",
        _ => "large",
    };
}
