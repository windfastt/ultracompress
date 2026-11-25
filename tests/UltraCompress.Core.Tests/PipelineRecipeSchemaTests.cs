using System.Text.Json;
using UltraCompress.Core;

namespace UltraCompress.Core.Tests;

public class PipelineRecipeSchemaTests
{
    public const string BlueprintExample = """
        {
          "id": "pdf-scan-insane-v3",
          "steps": [
            { "op": "preprocess", "name": "pdf_recompress_images", "params": { "dpi_max": 120, "jpeg_quality": null } },
            { "op": "preprocess", "name": "flate_optimize" },
            { "op": "compress", "codec": "zstd", "level": 22, "dictSizeKb": 16384 },
            { "op": "compress", "codec": "7z", "method": "LZMA2", "solid": true, "threads": 0, "level": 9 }
          ],
          "verify": { "decompressRoundtrip": true, "hashOriginal": true },
          "profile": "Tiny"
        }
        """;

    [Fact]
    public void BlueprintExample_ValidatesAgainstSchema()
    {
        var result = RecipeSchemaValidation.ValidatePipelineRecipeJson(BlueprintExample);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void BlueprintExample_DeserializesWithExpectedSteps()
    {
        Assert.True(PipelineRecipeJson.TryDeserialize(BlueprintExample, out var recipe, out var error), error);
        Assert.NotNull(recipe);
        Assert.Equal("pdf-scan-insane-v3", recipe.Id);
        Assert.Equal(CompressionProfile.Tiny, recipe.Profile);
        Assert.Equal(4, recipe.Steps.Count);

        var sevenZip = Assert.IsType<CompressStep>(recipe.Steps[3]);
        Assert.Equal("7z", sevenZip.Codec);
        Assert.Equal("LZMA2", sevenZip.Method);
        Assert.True(sevenZip.Solid);
        Assert.Equal(0, sevenZip.Threads);
    }

    [Fact]
    public void DefaultRecipes_ValidateAfterSerialization()
    {
        foreach (CompressionProfile profile in Enum.GetValues<CompressionProfile>())
        {
            var recipe = ProfileDefaults.CreateDefaultRecipe(profile);
            var result = RecipeSchemaValidation.ValidatePipelineRecipe(recipe);
            Assert.True(result.IsValid, $"{profile}: {string.Join("; ", result.Errors)}");
        }
    }

    [Fact]
    public void MissingSteps_FailsValidation()
    {
        const string json = """{"id": "empty", "steps": []}""";
        var result = RecipeSchemaValidation.ValidatePipelineRecipeJson(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void FileFingerprint_JsonRoundTrip_Validates()
    {
        var fp = new FileFingerprint
        {
            Path = @"C:\data\report.pdf",
            SizeBytes = 2_097_152,
            LastModifiedUtc = DateTimeOffset.Parse("2026-01-15T12:00:00Z"),
            ContentHashSha256 = new string('a', 64),
            DetectedType = "application/pdf",
            EntropyBitsPerByte = 7.2,
        };

        var json = JsonSerializer.Serialize(fp, PipelineRecipeJson.Options);
        var result = RecipeSchemaValidation.ValidateFileFingerprintJson(json);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData(CompressionProfile.Instant)]
    [InlineData(CompressionProfile.Balanced)]
    [InlineData(CompressionProfile.Archive)]
    [InlineData(CompressionProfile.Tiny)]
    [InlineData(CompressionProfile.Laboratory)]
    public void CompressionProfile_IsStringInJson(CompressionProfile profile)
    {
        var json = JsonSerializer.Serialize(profile, PipelineRecipeJson.Options);
        Assert.Equal($"\"{profile}\"", json);
    }
}