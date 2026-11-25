using UltraCompress.Core.Safety;

namespace UltraCompress.Core.Advisor;

/// <summary>
/// Strictly lossless pipeline and codec profile advisor governed by <see cref="ProfileContract"/> and <see cref="SafetyPolicy"/>.
/// Never emits lossy, format-converting, or data-removing preprocessors on any compression profile.
/// </summary>
public static class PipelineAdvisor
{
    public const double HighEntropyGate = 7.9;
    public const double MediaEntropySkipReencode = 7.5;

    public static AdvisorPlan Plan(FileFingerprint fingerprint, CompressionProfile profile)
    {
        var contract = ProfileContract.Get(profile);
        contract.ValidateContractInvariants();
        var decisions = new List<AdvisorDecision>();

        if (TryEntropyFastPath(fingerprint, profile, contract, decisions, out var fastPlan))
        {
            return fastPlan;
        }

        if (TryTinyFileFastPath(fingerprint, profile, decisions, out var tinyPlan))
        {
            return tinyPlan;
        }

        var (preprocess, stride) = PickLosslessPreprocess(fingerprint, decisions);
        var codec = SuggestPrimaryCodec(fingerprint, profile, decisions);
        var recipe = BuildRecipe(fingerprint, profile, preprocess, stride, codec);

        var alternates = BuildAlternateCodecs(fingerprint, profile, contract, preprocess);
        var candidates = BuildTournamentCandidates(fingerprint, profile, preprocess, stride, alternates, decisions);

        return new AdvisorPlan
        {
            PrimaryRecipe = recipe,
            PrimaryCodec = codec,
            AlternateCodecs = alternates,
            TournamentCandidates = candidates,
            Decisions = decisions,
        };
    }

    public static PipelineRecipe SuggestPrimaryRecipe(FileFingerprint fingerprint, CompressionProfile profile) =>
        Plan(fingerprint, profile).PrimaryRecipe;

    private static bool TryEntropyFastPath(
        FileFingerprint fingerprint,
        CompressionProfile profile,
        ProfileContract contract,
        List<AdvisorDecision> decisions,
        out AdvisorPlan plan)
    {
        plan = null!;
        var gate = Math.Min(HighEntropyGate, contract.IncompressibleEntropyBypassBitsPerByte);
        if (fingerprint.EntropyBitsPerByte is not double entropy || entropy < gate)
        {
            return false;
        }

        decisions.Add(new AdvisorDecision(
            AdvisorRuleIds.EntropyGate,
            $"Entropy {entropy:F2} >= {gate:F2} bpb for profile {profile}; bypass heavy preprocessors and use fast lossless store/zstd."));

        var codec = new CodecProfileSuggestion
        {
            Codec = profile == CompressionProfile.Instant ? "store" : "zstd",
            Level = profile == CompressionProfile.Instant ? 0 : 1,
            MatchedRuleId = AdvisorRuleIds.EntropyGate,
        };
        var recipe = BuildRecipe(fingerprint, profile, "raw", stride: null, codec);
        plan = new AdvisorPlan
        {
            PrimaryRecipe = recipe,
            PrimaryCodec = codec,
            Decisions = decisions,
        };
        return true;
    }

    private static bool TryTinyFileFastPath(
        FileFingerprint fingerprint,
        CompressionProfile profile,
        List<AdvisorDecision> decisions,
        out AdvisorPlan plan)
    {
        plan = null!;
        if (fingerprint.SizeBytes >= 4 * 1024)
        {
            return false;
        }

        decisions.Add(new AdvisorDecision(
            AdvisorRuleIds.TinyFileFast,
            "File under 4 KiB; dictionary and multi-candidate tournament overhead skipped in favor of fast lossless zstd."));

        var codec = new CodecProfileSuggestion
        {
            Codec = "zstd",
            Level = profile == CompressionProfile.Instant ? 1 : 3,
            MatchedRuleId = AdvisorRuleIds.TinyFileFast,
        };
        var recipe = BuildRecipe(fingerprint, profile, "raw", stride: null, codec);
        plan = new AdvisorPlan
        {
            PrimaryRecipe = recipe,
            PrimaryCodec = codec,
            Decisions = decisions,
        };
        return true;
    }

    private static (string Name, int? Stride) PickLosslessPreprocess(
        FileFingerprint fingerprint,
        List<AdvisorDecision> decisions)
    {
        if (fingerprint.EntropyBitsPerByte is >= MediaEntropySkipReencode)
        {
            decisions.Add(new AdvisorDecision(
                AdvisorRuleIds.EntropySkipReencode,
                $"Entropy {fingerprint.EntropyBitsPerByte:F2} >= {MediaEntropySkipReencode}; skip structural preprocessors."));
            return ("raw", null);
        }

        var ext = NormalizeExtension(fingerprint);
        var selected = ext switch
        {
            ".exe" or ".dll" or ".so" or ".elf" => (AdvisorRuleIds.ExtensionPe, "x86_bcj", (int?)null),
            ".sqlite" or ".db" or ".parquet" => (AdvisorRuleIds.ExtensionSqlite, "byteshuffle", (int?)8),
            ".csv" or ".tsv" or ".bin" when ext is ".csv" or ".tsv" => (AdvisorRuleIds.ExtensionLogCsv, "byteshuffle", (int?)4),
            ".bmp" or ".tiff" or ".tif" or ".wav" or ".raw" => (AdvisorRuleIds.ExtensionPng, "delta", (int?)4),
            _ => (AdvisorRuleIds.ProfileDefault, "raw", (int?)null),
        };

        if (selected.Item1 != AdvisorRuleIds.ProfileDefault)
        {
            decisions.Add(new AdvisorDecision(selected.Item1, $"Extension {ext} → reversible lossless preprocess `{selected.Item2}`."));
        }

        return (selected.Item2, selected.Item3);
    }

    private static CodecProfileSuggestion SuggestPrimaryCodec(
        FileFingerprint fingerprint,
        CompressionProfile profile,
        List<AdvisorDecision> decisions)
    {
        var baseRecipe = ProfileDefaults.CreateDefaultRecipe(profile);
        var step = baseRecipe.Steps.OfType<CompressStep>().Last();
        int? dict = step.DictSizeKb;

        if (profile is CompressionProfile.Tiny or CompressionProfile.Laboratory
            && fingerprint.SizeClass is "medium" or "large"
            && step.Codec == "zstd")
        {
            dict ??= fingerprint.SizeBytes switch
            {
                < 10 * 1024 * 1024 => 4096,
                _ => 16384,
            };
        }

        if (fingerprint.SizeClass == "large" && step.Level >= 19)
        {
            decisions.Add(new AdvisorDecision(
                AdvisorRuleIds.ProfileDefault,
                "Large file with high zstd level; cap dictionary within profile memory budget."));
            dict = dict is null or > 8192 ? 8192 : dict;
        }

        decisions.Add(new AdvisorDecision(
            AdvisorRuleIds.ProfileDefault,
            $"Profile {profile} → {step.Codec} level {step.Level}."));

        return new CodecProfileSuggestion
        {
            Codec = step.Codec,
            Level = step.Level,
            DictSizeKb = dict,
            MatchedRuleId = AdvisorRuleIds.ProfileDefault,
        };
    }

    private static IReadOnlyList<CodecProfileSuggestion> BuildAlternateCodecs(
        FileFingerprint fingerprint,
        CompressionProfile profile,
        ProfileContract contract,
        string preprocess)
    {
        if (profile is CompressionProfile.Instant or CompressionProfile.Balanced)
        {
            return Array.Empty<CodecProfileSuggestion>();
        }

        if (fingerprint.EntropyBitsPerByte is double e && e >= contract.IncompressibleEntropyBypassBitsPerByte)
        {
            return Array.Empty<CodecProfileSuggestion>();
        }

        var list = new List<CodecProfileSuggestion>
        {
            new() { Codec = "zstd", Level = profile == CompressionProfile.Archive ? 12 : 19, MatchedRuleId = AdvisorRuleIds.SmallTournamentArm },
            new() { Codec = "libdeflate", Level = profile == CompressionProfile.Archive ? 9 : 12, MatchedRuleId = AdvisorRuleIds.SmallTournamentArm },
            new() { Codec = "brotli", Level = profile == CompressionProfile.Archive ? 6 : 9, MatchedRuleId = AdvisorRuleIds.SmallTournamentArm },
        };

        if (!string.Equals(preprocess, "raw", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new CodecProfileSuggestion
            {
                Codec = "zstd",
                Level = 15,
                MatchedRuleId = AdvisorRuleIds.SmallTournamentArm,
            });
        }

        if (profile is CompressionProfile.Tiny or CompressionProfile.Laboratory)
        {
            list.Add(new CodecProfileSuggestion { Codec = "7z", Level = 9, MatchedRuleId = AdvisorRuleIds.SmallTournamentArm });
        }

        var max = profile switch
        {
            CompressionProfile.Archive => 3,
            CompressionProfile.Tiny => 5,
            CompressionProfile.Laboratory => 5,
            _ => 0,
        };

        return list.Take(max).ToList();
    }

    private static IReadOnlyList<PipelineRecipe> BuildTournamentCandidates(
        FileFingerprint fingerprint,
        CompressionProfile profile,
        string primaryPreprocess,
        int? stride,
        IReadOnlyList<CodecProfileSuggestion> codecs,
        List<AdvisorDecision> decisions)
    {
        if (profile is CompressionProfile.Instant or CompressionProfile.Balanced || codecs.Count == 0)
        {
            return Array.Empty<PipelineRecipe>();
        }

        decisions.Add(new AdvisorDecision(
            AdvisorRuleIds.SmallTournamentArm,
            $"Profile {profile} candidate set: {codecs.Count + 1} verified lossless pipeline(s) including primary."));

        var candidates = new List<PipelineRecipe>(codecs.Count);
        foreach (var alt in codecs)
        {
            candidates.Add(BuildRecipe(fingerprint, profile, primaryPreprocess, stride, alt, $"candidate-{candidates.Count + 1}"));
        }

        return candidates;
    }

    private static PipelineRecipe BuildRecipe(
        FileFingerprint fingerprint,
        CompressionProfile profile,
        string preprocess,
        int? stride,
        CodecProfileSuggestion codec,
        string? recipeSuffix = null)
    {
        var ext = NormalizeExtension(fingerprint).TrimStart('.');
        if (string.IsNullOrEmpty(ext))
        {
            ext = "bin";
        }

        var id = recipeSuffix is null
            ? $"advisor-{profile.ToString().ToLowerInvariant()}-{ext}"
            : $"advisor-{recipeSuffix}";

        var preprocessStep = new PreprocessStep { Name = preprocess };
        if (stride is int s)
        {
            preprocessStep.Params["stride"] = s;
        }

        var compressStep = new CompressStep
        {
            Codec = codec.Codec,
            Level = codec.Level,
            DictSizeKb = codec.DictSizeKb,
            Method = codec.Codec == "7z" ? "LZMA2" : null,
            Solid = codec.Codec == "7z" ? false : null,
            Threads = codec.Codec == "7z" ? 0 : null,
        };

        var recipe = new PipelineRecipe
        {
            Id = id,
            Profile = profile,
            Verify = new VerifyOptions { DecompressRoundtrip = true, HashOriginal = true },
            Steps = [preprocessStep, compressStep],
        };

        SafetyPolicy.ValidateCompressionProfileRecipe(profile, recipe);
        return recipe;
    }

    private static string NormalizeExtension(FileFingerprint fingerprint)
    {
        if (!string.IsNullOrWhiteSpace(fingerprint.DetectedType))
        {
            var mime = fingerprint.DetectedType.ToLowerInvariant();
            if (mime.Contains("pdf", StringComparison.Ordinal))
            {
                return ".pdf";
            }

            if (mime.Contains("sqlite", StringComparison.Ordinal))
            {
                return ".sqlite";
            }

            if (mime.Contains("x-dosexec", StringComparison.Ordinal) || mime.Contains("x-executable", StringComparison.Ordinal))
            {
                return ".exe";
            }
        }

        return Path.GetExtension(fingerprint.Path).ToLowerInvariant();
    }
}
