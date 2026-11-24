using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltraCompress.Core;

/// <summary>JSON (de)serialization settings for pipeline recipes.</summary>
public static class PipelineRecipeJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string Serialize(PipelineRecipe recipe) =>
        JsonSerializer.Serialize(recipe, Options);

    public static PipelineRecipe? Deserialize(string json) =>
        JsonSerializer.Deserialize<PipelineRecipe>(json, Options);

    public static bool TryDeserialize(string json, out PipelineRecipe? recipe, out string? error)
    {
        try
        {
            recipe = Deserialize(json);
            if (recipe is null)
            {
                error = "Document deserialized to null.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            recipe = null;
            error = ex.Message;
            return false;
        }
    }
}

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

    /// <summary>Backend-specific method (e.g. LZMA2 for 7z).</summary>
    public string? Method { get; set; }

    public bool? Solid { get; set; }

    /// <summary>Worker thread count; 0 means auto.</summary>
    public int? Threads { get; set; }
}

public sealed class VerifyOptions
{
    public bool DecompressRoundtrip { get; set; } = true;

    public bool HashOriginal { get; set; } = true;
}