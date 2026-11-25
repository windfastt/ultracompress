using UltraCompress.Core;

namespace UltraCompress.Core.Tests;

public class ProfileDefaultsTests
{
    [Theory]
    [InlineData(CompressionProfile.Instant)]
    [InlineData(CompressionProfile.Balanced)]
    [InlineData(CompressionProfile.Archive)]
    [InlineData(CompressionProfile.Tiny)]
    public void CreateDefaultRecipe_IncludesZstdCompressStep(CompressionProfile profile)
    {
        var recipe = ProfileDefaults.CreateDefaultRecipe(profile);
        var step = Assert.Single(recipe.Steps, s => s is CompressStep) as CompressStep;
        Assert.NotNull(step);
        Assert.Equal("zstd", step.Codec);
        Assert.Equal(profile, recipe.Profile);
    }

    [Fact]
    public void FileFingerprint_SizeClass_MapsCorrectly()
    {
        var fp = new FileFingerprint
        {
            Path = "x",
            SizeBytes = 512,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };
        Assert.Equal("tiny", fp.SizeClass);
    }
}