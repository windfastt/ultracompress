using UltraCompress.Core.Safety;
using UltraCompress.Core.Transforms;

namespace UltraCompress.Core.Search;

/// <summary>
/// Builds verified 100% lossless candidate pipeline arms across
/// <c>(Raw, Delta, ByteShuffle, X86Bcj) x (Zstd, Brotli, LibDeflate, SevenZip)</c>.
/// </summary>
public static class PipelineArmCatalog
{
    public const string A0 = "raw-zstd";
    public const string A1 = "raw-brotli";
    public const string A2 = "raw-libdeflate";
    public const string A3 = "raw-7z";
    public const string A4 = "delta-zstd";
    public const string A5 = "delta-brotli";
    public const string A6 = "delta-libdeflate";
    public const string A7 = "delta-7z";
    public const string A8 = "byteshuffle-zstd";
    public const string A9 = "byteshuffle-brotli";
    public const string A10 = "byteshuffle-libdeflate";
    public const string A11 = "byteshuffle-7z";
    public const string A12 = "x86_bcj-zstd";
    public const string A13 = "x86_bcj-brotli";
    public const string A14 = "x86_bcj-libdeflate";
    public const string A15 = "x86_bcj-7z";

    private sealed record CodecSpec(string Codec, int Level, int CostTier, string? Method = null);
    private sealed record PreprocessSpec(string Key, string Name, int? Stride, int CostPenalty);

    public static IReadOnlyList<PipelineArm> SelectArms(
        FileFingerprint fingerprint,
        CompressionProfile profile,
        TournamentOptions? options = null,
        ReadOnlySpan<byte> pilotSample = default,
        IReadOnlySet<string>? availableCodecs = null)
    {
        var contract = ProfileContract.Get(profile);
        options ??= TournamentOptions.ForProfile(profile);

        if (profile is CompressionProfile.Instant or CompressionProfile.Balanced)
        {
            return Array.Empty<PipelineArm>();
        }

        var (deltaStride, deltaEntropy) = pilotSample.IsEmpty
            ? (4, fingerprint.EntropyBitsPerByte ?? 0.0)
            : DeltaTransform.SelectBestStride(pilotSample);
        var shuffleStride = pilotSample.IsEmpty
            ? 4
            : ByteShuffleTransform.SelectBestStride(pilotSample);

        if (ShouldUseSingleFastArm(fingerprint, contract, deltaEntropy))
        {
            var fastCodec = IsCodecAvailable("zstd", availableCodecs) ? "zstd" : PickFallbackCodec(availableCodecs);
            var fastArm = BuildArm(
                A0,
                $"raw → {fastCodec} (entropy gate)",
                CreateVerifiedRecipe(A0, profile, "raw", null, fastCodec, level: 1),
                costTier: 0);
            return [fastArm];
        }

        if (profile == CompressionProfile.Archive)
        {
            var archiveArms = new List<PipelineArm>
            {
                BuildArm(A0, "raw → zstd-12", CreateVerifiedRecipe(A0, profile, "raw", null, "zstd", 12), 1),
                BuildArm(A4, $"delta({deltaStride}) → zstd-12", CreateVerifiedRecipe(A4, profile, "delta", deltaStride, "zstd", 12), 2),
                BuildArm(A8, $"byteshuffle({shuffleStride}) → zstd-12", CreateVerifiedRecipe(A8, profile, "byteshuffle", shuffleStride, "zstd", 12), 2),
                BuildArm(A1, "raw → brotli-6", CreateVerifiedRecipe(A1, profile, "raw", null, "brotli", 6), 3),
            };

            return FilterAndCap(archiveArms, options, contract.CandidatePipelineBudget, availableCodecs);
        }

        var codecs = GetCodecMatrix(profile)
            .Where(c => IsCodecAvailable(c.Codec, availableCodecs))
            .ToList();

        var preprocessors = new List<PreprocessSpec>
        {
            new("raw", "raw", null, 0),
            new("delta", "delta", deltaStride, 1),
            new("byteshuffle", "byteshuffle", shuffleStride, 1),
            new("x86_bcj", "x86_bcj", null, 1),
        };

        var arms = new List<PipelineArm>(24);
        foreach (var pre in preprocessors)
        {
            foreach (var codec in codecs)
            {
                var armId = $"{pre.Key}-{codec.Codec}";
                var label = pre.Stride is int s
                    ? $"{pre.Name}(stride={s}) → {codec.Codec}-{codec.Level}"
                    : $"{pre.Name} → {codec.Codec}-{codec.Level}";
                var recipe = CreateVerifiedRecipe(
                    armId,
                    profile,
                    pre.Name,
                    pre.Stride,
                    codec.Codec,
                    codec.Level,
                    codec.Method);
                arms.Add(BuildArm(armId, label, recipe, codec.CostTier + pre.CostPenalty));
            }
        }

        // Laboratory explores explicit multi-stride Delta (1/2/4/8) and ByteShuffle (4/8) variants up to budget.
        if (profile == CompressionProfile.Laboratory)
        {
            foreach (var altStride in DeltaTransform.SupportedStrides)
            {
                if (altStride == deltaStride)
                {
                    continue;
                }

                if (IsCodecAvailable("zstd", availableCodecs))
                {
                    var id = $"delta{altStride}-zstd";
                    arms.Add(BuildArm(
                        id,
                        $"delta(stride={altStride}) → zstd-22",
                        CreateVerifiedRecipe(id, profile, "delta", altStride, "zstd", 22),
                        costTier: 3));
                }

                if (IsCodecAvailable("7z", availableCodecs))
                {
                    var id = $"delta{altStride}-7z";
                    arms.Add(BuildArm(
                        id,
                        $"delta(stride={altStride}) → 7z-9",
                        CreateVerifiedRecipe(id, profile, "delta", altStride, "7z", 9, "LZMA2"),
                        costTier: 6));
                }
            }

            var altShuffle = shuffleStride == 4 ? 8 : 4;
            if (IsCodecAvailable("zstd", availableCodecs))
            {
                var id = $"byteshuffle{altShuffle}-zstd";
                arms.Add(BuildArm(
                    id,
                    $"byteshuffle(stride={altShuffle}) → zstd-22",
                    CreateVerifiedRecipe(id, profile, "byteshuffle", altShuffle, "zstd", 22),
                    costTier: 3));
            }

            if (IsCodecAvailable("7z", availableCodecs))
            {
                var id = $"byteshuffle{altShuffle}-7z";
                arms.Add(BuildArm(
                    id,
                    $"byteshuffle(stride={altShuffle}) → 7z-9",
                    CreateVerifiedRecipe(id, profile, "byteshuffle", altShuffle, "7z", 9, "LZMA2"),
                    costTier: 6));
            }
        }

        return FilterAndCap(arms, options, contract.CandidatePipelineBudget, availableCodecs);
    }

    private static IReadOnlyList<CodecSpec> GetCodecMatrix(CompressionProfile profile) =>
        profile == CompressionProfile.Laboratory
            ?
            [
                new("zstd", 22, 2),
                new("brotli", 11, 4),
                new("libdeflate", 12, 2),
                new("7z", 9, 5, "LZMA2"),
            ]
            :
            [
                new("zstd", 19, 2),
                new("brotli", 9, 3),
                new("libdeflate", 12, 2),
                new("7z", 7, 5, "LZMA2"),
            ];

    private static IReadOnlyList<PipelineArm> FilterAndCap(
        List<PipelineArm> arms,
        TournamentOptions options,
        int contractCap,
        IReadOnlySet<string>? availableCodecs)
    {
        var filtered = availableCodecs is null
            ? arms
            : arms.Where(a =>
            {
                var codec = a.Recipe.Steps.OfType<CompressStep>().Last().Codec;
                return availableCodecs.Contains(codec);
            }).ToList();

        var max = Math.Min(options.MaxArms, contractCap);
        if (options.WarmStartArmIds is { Count: > 0 } warm)
        {
            filtered = filtered
                .OrderByDescending(a => warm.Contains(a.ArmId, StringComparer.OrdinalIgnoreCase))
                .ThenBy(a => a.CostTier)
                .ToList();
        }
        else
        {
            filtered = filtered
                .OrderBy(a => a.CostTier)
                .ToList();
        }

        return filtered.Take(max).ToList();
    }

    private static bool ShouldUseSingleFastArm(
        FileFingerprint fingerprint,
        ProfileContract contract,
        double postDeltaEntropy) =>
        fingerprint.EntropyBitsPerByte is double rawEntropy
        && rawEntropy >= contract.IncompressibleEntropyBypassBitsPerByte
        && postDeltaEntropy >= contract.IncompressibleEntropyBypassBitsPerByte;

    private static bool IsCodecAvailable(string codec, IReadOnlySet<string>? availableCodecs) =>
        availableCodecs is null || availableCodecs.Contains(codec);

    private static string PickFallbackCodec(IReadOnlySet<string>? availableCodecs)
    {
        if (availableCodecs is null)
        {
            return "zstd";
        }

        foreach (var candidate in new[] { "zstd", "libdeflate", "brotli", "7z", "store" })
        {
            if (availableCodecs.Contains(candidate))
            {
                return candidate;
            }
        }

        return availableCodecs.FirstOrDefault() ?? "store";
    }

    private static PipelineArm BuildArm(string id, string summary, PipelineRecipe recipe, int costTier) =>
        new() { ArmId = id, Summary = summary, Recipe = recipe, CostTier = costTier };

    private static PipelineRecipe CreateVerifiedRecipe(
        string armId,
        CompressionProfile profile,
        string preprocessName,
        int? stride,
        string codec,
        int level,
        string? method = null)
    {
        var preprocessStep = new PreprocessStep { Name = preprocessName };
        if (stride is int s)
        {
            preprocessStep.Params["stride"] = s;
        }

        var compressStep = new CompressStep
        {
            Codec = codec,
            Level = level,
            Method = method,
            Solid = codec == "7z" ? false : null,
            Threads = codec == "7z" ? 0 : null,
        };

        var recipe = new PipelineRecipe
        {
            Id = $"arm-{profile.ToString().ToLowerInvariant()}-{armId}",
            Profile = profile,
            Verify = new VerifyOptions { DecompressRoundtrip = true, HashOriginal = true },
            Steps = [preprocessStep, compressStep],
        };

        SafetyPolicy.ValidateCompressionProfileRecipe(profile, recipe);
        return recipe;
    }
}
