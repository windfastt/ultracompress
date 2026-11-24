using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltraCompress.Core.Recipes;

internal static class RecipeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(PipelineRecipe recipe) =>
        JsonSerializer.Serialize(recipe, Options);

    public static PipelineRecipe Deserialize(string json) =>
        JsonSerializer.Deserialize<PipelineRecipe>(json, Options)
        ?? throw new InvalidOperationException("Recipe JSON deserialized to null.");
}