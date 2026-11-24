using System.Text.Json.Serialization;

namespace UltraCompress.Core;

/// <summary>
/// Ordered compression pipeline (logical DSL). Serializable to JSON for SQLite storage.
/// </summary>
public sealed class PipelineRecipe
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public List<PipelineStep> Steps { get; set; } = new();

    public VerifyOptions Verify { get; set; } = new();

    public CompressionProfile Profile { get; set; } = CompressionProfile.Balanced;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(PreprocessStep), "preprocess")]
[JsonDerivedType(typeof(CompressStep), "compress")]
public abstract class PipelineStep
{
    public abstract string Op { get; }
}

public sealed class PreprocessStep : PipelineStep
{
    public override string Op => "preprocess";

    public string Name { get; set; } = "noop";

    public Dictionary<string, object?> Params { get; set; } = new();
}

public sealed class CompressStep : PipelineStep
{
    public override string Op => "compress";

    public string Codec { get; set; } = "zstd";

    public int Level { get; set; } = 3;

    public int? DictSizeKb { get; set; }

    /// <summary>7-Zip method flag (e.g. LZMA2, LZMA, PPMd).</summary>
    public string? Method { get; set; }

    /// <summary>7z solid archive; meaningful for multi-file archives.</summary>
    public bool? Solid { get; set; }

    /// <summary>7-Zip -mmt thread count; 0 = use all logical CPUs.</summary>
    public int? Threads { get; set; }
}

public sealed class VerifyOptions
{
    public bool DecompressRoundtrip { get; set; } = true;

    public bool HashOriginal { get; set; } = true;
}