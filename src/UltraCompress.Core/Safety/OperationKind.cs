using System.Text.Json.Serialization;

namespace UltraCompress.Core.Safety;

/// <summary>
/// Strict four-mode data safety classification enforced across Core, CLI, Engine, and UI.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OperationKind
{
    /// <summary>Bit-exact reversible compression; the sole mode allowed across all five CompressionProfile tiers.</summary>
    LosslessCompression = 0,

    /// <summary>Perceptual/lossy re-encoding (e.g. image/PDF/media quantization). Requires explicit consent and preserves originals by default.</summary>
    LossyOptimization = 1,

    /// <summary>Container or format transcoding where byte identity changes. Requires explicit consent and preserves originals by default.</summary>
    FormatConversion = 2,

    /// <summary>Stripping metadata, EXIF, debug symbols, or auxiliary streams. Requires explicit consent and preserves originals by default.</summary>
    DataRemoval = 3,
}
