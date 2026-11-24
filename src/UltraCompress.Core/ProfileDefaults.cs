namespace UltraCompress.Core;

public static class ProfileDefaults
{
    public static PipelineRecipe CreateDefaultRecipe(CompressionProfile profile)
    {
        var recipe = new PipelineRecipe
        {
            Id = $"default-{profile.ToString().ToLowerInvariant()}",
            Profile = profile,
            Verify = new VerifyOptions { DecompressRoundtrip = true, HashOriginal = true },
        };

        var level = profile switch
        {
            CompressionProfile.Balanced => 3,
            CompressionProfile.Small => 10,
            CompressionProfile.Tiny => 19,
            CompressionProfile.Laboratory => 22,
            CompressionProfile.Experimental => 22,
            _ => 3,
        };

        if (profile is CompressionProfile.Laboratory)
        {
            recipe.Steps.Add(new CompressStep
            {
                Codec = "7z",
                Level = 9,
                Method = "LZMA2",
                Solid = false,
                Threads = 0,
            });
        }
        else
        {
            recipe.Steps.Add(new CompressStep { Codec = "zstd", Level = level });
        }

        if (profile is CompressionProfile.Small or CompressionProfile.Tiny or CompressionProfile.Laboratory)
        {
            recipe.Steps.Insert(0, new PreprocessStep { Name = "noop" });
        }

        return recipe;
    }
}