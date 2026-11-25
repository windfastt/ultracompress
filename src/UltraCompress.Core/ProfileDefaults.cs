using UltraCompress.Core.Safety;

namespace UltraCompress.Core;

public static class ProfileDefaults
{
    public static PipelineRecipe CreateDefaultRecipe(CompressionProfile profile)
    {
        var contract = ProfileContract.Get(profile);
        contract.ValidateContractInvariants();

        var recipe = new PipelineRecipe
        {
            Id = $"default-{profile.ToString().ToLowerInvariant()}",
            Profile = profile,
            Verify = new VerifyOptions { DecompressRoundtrip = true, HashOriginal = true },
        };

        var level = profile switch
        {
            CompressionProfile.Instant => 1,
            CompressionProfile.Balanced => 3,
            CompressionProfile.Archive => 12,
            CompressionProfile.Tiny => 19,
            CompressionProfile.Laboratory => 22,
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Undefined compression profile."),
        };

        if (profile is CompressionProfile.Archive or CompressionProfile.Tiny or CompressionProfile.Laboratory)
        {
            recipe.Steps.Add(new PreprocessStep { Name = "raw" });
        }

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

        SafetyPolicy.ValidateCompressionProfileRecipe(profile, recipe);
        return recipe;
    }

    public static PipelineRecipe CreateStoreRecipe(CompressionProfile profile, string? id = null)
    {
        var recipe = new PipelineRecipe
        {
            Id = id ?? $"store-{profile.ToString().ToLowerInvariant()}",
            Profile = profile,
            Verify = new VerifyOptions { DecompressRoundtrip = true, HashOriginal = true },
            Steps =
            [
                new PreprocessStep { Name = "raw" },
                new CompressStep { Codec = "store", Level = 0 },
            ],
        };

        SafetyPolicy.ValidateCompressionProfileRecipe(profile, recipe);
        return recipe;
    }
}
