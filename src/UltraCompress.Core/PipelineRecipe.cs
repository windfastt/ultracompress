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
    [JsonIgnore]
    public abstract string Op { get; }
}

public sealed class PreprocessStep : PipelineStep
{
    [JsonIgnore]
    public override string Op => "preprocess";

    public string Name { get; set; } = "noop";

    public Dictionary<string, object?> Params { get; set; } = new();
}

public sealed class CompressStep : PipelineStep
{
    [JsonIgnore]
    public override string Op => "compress";

    public string Codec { get; set; } = "zstd";

    public int Level { get; set; } = 3;

    public int? DictSizeKb { get; set; }
}

public sealed class VerifyOptions
{
    public bool DecompressRoundtrip { get; set; } = true;

    public bool HashOriginal { get; set; } = true;
}