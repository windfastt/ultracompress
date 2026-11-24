using UltraCompress.Core;

namespace UltraCompress.Core.Tests;

public class ProfileLaboratoryTests
{
    [Fact]
    public void CreateDefaultRecipe_Laboratory_UsesSevenZipLzma2()
    {
        var recipe = ProfileDefaults.CreateDefaultRecipe(CompressionProfile.Laboratory);
        var step = Assert.Single(recipe.Steps, s => s is CompressStep) as CompressStep;
        Assert.NotNull(step);
        Assert.Equal("7z", step.Codec);
        Assert.Equal(9, step.Level);
        Assert.Equal("LZMA2", step.Method);
        Assert.False(step.Solid);
        Assert.Equal(0, step.Threads);
    }
}