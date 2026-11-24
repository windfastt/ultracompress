using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace UltraCompress.Core;

/// <summary>Validates recipe and fingerprint JSON against embedded JSON Schema draft 2020-12 documents.</summary>
public static class RecipeSchemaValidation
{
    private static readonly Lazy<JsonSchema> PipelineRecipeSchema = new(LoadPipelineRecipeSchema);
    private static readonly Lazy<JsonSchema> FileFingerprintSchema = new(LoadFileFingerprintSchema);

    public static SchemaValidationResult ValidatePipelineRecipeJson(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return SchemaValidationResult.Invalid(["Invalid JSON: " + ex.Message]);
        }

        if (node is null)
        {
            return SchemaValidationResult.Invalid(["Document is empty."]);
        }

        var evaluation = PipelineRecipeSchema.Value.Evaluate(node, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        return evaluation.IsValid
            ? SchemaValidationResult.Valid()
            : SchemaValidationResult.Invalid(CollectErrors(evaluation));
    }

    public static SchemaValidationResult ValidateFileFingerprintJson(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return SchemaValidationResult.Invalid(["Invalid JSON: " + ex.Message]);
        }

        if (node is null)
        {
            return SchemaValidationResult.Invalid(["Document is empty."]);
        }

        var evaluation = FileFingerprintSchema.Value.Evaluate(node, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        return evaluation.IsValid
            ? SchemaValidationResult.Valid()
            : SchemaValidationResult.Invalid(CollectErrors(evaluation));
    }

    public static SchemaValidationResult ValidatePipelineRecipe(PipelineRecipe recipe)
    {
        var json = PipelineRecipeJson.Serialize(recipe);
        return ValidatePipelineRecipeJson(json);
    }

    private static IEnumerable<string> CollectErrors(EvaluationResults results)
    {
        foreach (var detail in results.Details)
        {
            if (detail.Errors is null)
            {
                continue;
            }

            foreach (var (_, message) in detail.Errors)
            {
                var location = detail.InstanceLocation.ToString();
                yield return string.IsNullOrEmpty(location)
                    ? message
                    : $"{location}: {message}";
            }
        }
    }

    private static JsonSchema LoadPipelineRecipeSchema() =>
        JsonSchema.FromText(ReadEmbeddedSchema("pipeline-recipe.schema.json"));

    private static JsonSchema LoadFileFingerprintSchema() =>
        JsonSchema.FromText(ReadEmbeddedSchema("file-fingerprint.schema.json"));

    private static string ReadEmbeddedSchema(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly
            .GetManifestResourceNames()
            .Single(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded schema resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class SchemaValidationResult
{
    public bool IsValid { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public static SchemaValidationResult Valid() => new() { IsValid = true };

    public static SchemaValidationResult Invalid(IEnumerable<string> errors) =>
        new() { IsValid = false, Errors = errors.ToList() };
}