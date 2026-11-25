namespace UltraCompress.Core.Search;

/// <summary>
/// Builds 8–12 pipeline-family arms for Tiny-profile tournaments.
/// </summary>
public static class PipelineArmCatalog
{
    public const string A0 = "A0";
    public const string A1 = "A1";
    public const string A2 = "A2";
    public const string A3 = "A3";
    public const string A4 = "A4";
    public const string A5 = "A5";
    public const string A6 = "A6";
    public const string A7 = "A7";
    public const string A8 = "A8";
    public const string A9 = "A9";
    public const string A10 = "A10";
    public const string A11 = "A11";

    public static IReadOnlyList<PipelineArm> SelectArms(
        FileFingerprint fingerprint,
        CompressionProfile profile,
        TournamentOptions options)
    {
        if (profile is CompressionProfile.Balanced)
        {
            return Array.Empty<PipelineArm>();
        }

        if (profile is CompressionProfile.Small)
        {
            return new[]
            {
                BuildArm(A0, "noop → zstd-9", Recipe(A0, "noop", "zstd", 9), 1),
                BuildArm(A2, "flate_optimize → zstd", Recipe(A2, "flate_optimize", "zstd", 6), 2),
                BuildArm(A3, "bytesort → zstd19", Recipe(A3, "bytesort_block", "zstd", 19), 3),
                BuildArm(A1, "noop → brotli-6", Recipe(A1, "noop", "brotli", 6), 3),
            };
        }

        if (ShouldUseSingleFastArm(fingerprint))
        {
            return new[] { BuildArm(A0, "noop → zstd-3 (entropy gate)", Recipe(A0, "noop", "zstd", 3), costTier: 0) };
        }

        if (fingerprint.SizeBytes < 4 * 1024)
        {
            return new[] { BuildArm(A0, "noop → zstd-3 (tiny file)", Recipe(A0, "noop", "zstd", 3), costTier: 0) };
        }

        var arms = new List<PipelineArm>(options.MaxArms)
        {
            BuildArm(A0, "noop → zstd ladder", Recipe(A0, "noop", "zstd", profile == CompressionProfile.Small ? 9 : 19), 1),
            BuildArm(A1, "noop → brotli", Recipe(A1, "noop", "brotli", 9), 3),
            BuildArm(A2, "flate_optimize → libdeflate → zstd", Recipe(A2, "flate_optimize", "zstd", 6), 2),
            BuildArm(A3, "bytesort → zstd22", Recipe(A3, "bytesort_block", "zstd", 22, dictKb: 16384), 4),
            BuildArm(A4, "type preprocess → zstd19", Recipe(A4, PickTypePreprocess(fingerprint), "zstd", 19), 4),
            BuildArm(A5, "noop → 7z LZMA2 ultra", Recipe(A5, "noop", "7z", 9), 5),
        };

        if (fingerprint.SizeClass is "medium" or "large")
        {
            arms.Add(BuildArm(A6, "folder 7z solid ultra", Recipe(A6, "solid_bundle_advisor", "7z", 9), 5));
        }

        if (profile is CompressionProfile.Laboratory or CompressionProfile.Experimental)
        {
            arms.Add(BuildArm(A7, "PAQ subprocess (experimental)", Recipe(A7, "noop", "paq", 0), 6));
        }
        else if (profile is CompressionProfile.Tiny)
        {
            arms.Add(BuildArm(A8, "noop → zstd-3 warm", Recipe(A8, "noop", "zstd", 3), 1));
            arms.Add(BuildArm(A9, "noop → brotli-6", Recipe(A9, "noop", "brotli", 6), 2));
            arms.Add(BuildArm(A10, "flate_optimize → zstd-19", Recipe(A10, "flate_optimize", "zstd", 19), 3));
            arms.Add(BuildArm(A11, "bytesort → zstd-15", Recipe(A11, "bytesort_block", "zstd", 15), 3));
        }

        var capped = arms.Take(options.MaxArms).ToList();

        if (options.WarmStartArmIds is { Count: > 0 } warm)
        {
            capped = capped
                .OrderByDescending(a => warm.Contains(a.ArmId, StringComparer.OrdinalIgnoreCase))
                .ThenBy(a => a.CostTier)
                .ToList();
        }

        return capped;
    }

    private static bool ShouldUseSingleFastArm(FileFingerprint fingerprint) =>
        fingerprint.EntropyBitsPerByte is >= 7.9;

    private static string PickTypePreprocess(FileFingerprint fingerprint)
    {
        var ext = Path.GetExtension(fingerprint.Path);
        return ext.ToLowerInvariant() switch
        {
            ".pdf" => "pdf_object_stream",
            ".sqlite" or ".db" => "sqlite_vacuum_pages",
            ".png" => "png_palette_quant",
            ".exe" or ".dll" => "pe_strip_debug",
            _ => "noop",
        };
    }

    private static PipelineArm BuildArm(string id, string summary, PipelineRecipe recipe, int costTier) =>
        new() { ArmId = id, Summary = summary, Recipe = recipe, CostTier = costTier };

    private static PipelineRecipe Recipe(
        string armId,
        string preprocess,
        string codec,
        int level,
        int? dictKb = null)
    {
        var recipe = new PipelineRecipe
        {
            Id = $"arm-{armId}",
            Profile = CompressionProfile.Tiny,
            Verify = new VerifyOptions { DecompressRoundtrip = true, HashOriginal = true },
        };

        if (!string.Equals(preprocess, "noop", StringComparison.OrdinalIgnoreCase))
        {
            recipe.Steps.Add(new PreprocessStep { Name = preprocess });
        }
        else
        {
            recipe.Steps.Add(new PreprocessStep { Name = "noop" });
        }

        recipe.Steps.Add(new CompressStep
        {
            Codec = codec,
            Level = level,
            DictSizeKb = dictKb,
        });

        return recipe;
    }
}