namespace UltraCompress.Core.Advisor;

/// <summary>
/// Rules-based pipeline and codec profile suggestions (v1: extension + entropy gates).
/// </summary>
public static class PipelineAdvisor
{
    public const double HighEntropyGate = 7.9;
    public const double MediaEntropySkipReencode = 7.5;

    public static AdvisorPlan Plan(FileFingerprint fingerprint, CompressionProfile profile)
    {
        var decisions = new List<AdvisorDecision>();

        if (TryEntropyFastPath(fingerprint, profile, decisions, out var fastPlan))
        {
            return fastPlan;
        }

        if (TryTinyFileFastPath(fingerprint, profile, decisions, out var tinyPlan))
        {
            return tinyPlan;
        }

        var preprocess = PickPreprocess(fingerprint, decisions);
        var codec = SuggestPrimaryCodec(fingerprint, profile, decisions);
        var recipe = BuildRecipe(fingerprint, profile, preprocess, codec, decisions);

        var alternates = BuildAlternateCodecs(fingerprint, profile, preprocess, decisions);
        var candidates = BuildTournamentCandidates(fingerprint, profile, preprocess, alternates, decisions);

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
        List<AdvisorDecision> decisions,
        out AdvisorPlan plan)
    {
        plan = null!;
        if (fingerprint.EntropyBitsPerByte is not >= HighEntropyGate)
        {
            return false;
        }

        decisions.Add(new AdvisorDecision(
            AdvisorRuleIds.EntropyGate,
            $"Blocked entropy {fingerprint.EntropyBitsPerByte:F2} ≥ {HighEntropyGate}; skip expensive preprocess and use fast zstd."));

        var codec = new CodecProfileSuggestion
        {
            Codec = "zstd",
            Level = 3,
            MatchedRuleId = AdvisorRuleIds.EntropyGate,
        };
        var recipe = BuildRecipe(fingerprint, profile, "noop", codec, decisions);
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
            "File under 4 KiB; dictionary and preprocess gains are negligible."));

        var codec = new CodecProfileSuggestion
        {
            Codec = "zstd",
            Level = 3,
            MatchedRuleId = AdvisorRuleIds.TinyFileFast,
        };
        var recipe = BuildRecipe(fingerprint, profile, "noop", codec, decisions);
        plan = new AdvisorPlan
        {
            PrimaryRecipe = recipe,
            PrimaryCodec = codec,
            Decisions = decisions,
        };
        return true;
    }

    private static string PickPreprocess(FileFingerprint fingerprint, List<AdvisorDecision> decisions)
    {
        if (fingerprint.EntropyBitsPerByte is >= MediaEntropySkipReencode)
        {
            decisions.Add(new AdvisorDecision(
                AdvisorRuleIds.EntropySkipReencode,
                $"Entropy {fingerprint.EntropyBitsPerByte:F2} ≥ {MediaEntropySkipReencode}; skip type-specific re-encode preprocessors."));
            return "noop";
        }

        var ext = NormalizeExtension(fingerprint);
        var rule = ext switch
        {
            ".pdf" => (AdvisorRuleIds.ExtensionPdf, "pdf_object_stream"),
            ".sqlite" or ".db" => (AdvisorRuleIds.ExtensionSqlite, "sqlite_vacuum_pages"),
            ".png" => (AdvisorRuleIds.ExtensionPng, "png_palette_quant"),
            ".exe" or ".dll" => (AdvisorRuleIds.ExtensionPe, "pe_strip_debug"),
            ".log" or ".csv" => (AdvisorRuleIds.ExtensionLogCsv, "bytesort_block"),
            ".txt" or ".json" or ".xml" or ".html" or ".md" => (AdvisorRuleIds.ExtensionTextFlate, "flate_optimize"),
            _ => (AdvisorRuleIds.ProfileDefault, "noop"),
        };

        if (rule.Item1 != AdvisorRuleIds.ProfileDefault)
        {
            decisions.Add(new AdvisorDecision(rule.Item1, $"Extension {ext} → preprocess `{rule.Item2}`."));
        }

        return rule.Item2;
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
                "Large file with high zstd level; cap dictionary to limit RAM (Phase 3 heuristic)."));
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
        string preprocess,
        List<AdvisorDecision> decisions)
    {
        if (profile is CompressionProfile.Balanced)
        {
            return Array.Empty<CodecProfileSuggestion>();
        }

        if (fingerprint.EntropyBitsPerByte is >= HighEntropyGate)
        {
            return Array.Empty<CodecProfileSuggestion>();
        }

        var list = new List<CodecProfileSuggestion>
        {
            new() { Codec = "zstd", Level = profile == CompressionProfile.Small ? 9 : 19, MatchedRuleId = AdvisorRuleIds.SmallTournamentArm },
            new() { Codec = "zstd", Level = 6, MatchedRuleId = AdvisorRuleIds.SmallTournamentArm },
        };

        if (!string.Equals(preprocess, "noop", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new CodecProfileSuggestion
            {
                Codec = "zstd",
                Level = 15,
                MatchedRuleId = AdvisorRuleIds.SmallTournamentArm,
            });
        }

        if (profile is CompressionProfile.Tiny or CompressionProfile.Laboratory or CompressionProfile.Experimental)
        {
            list.Add(new CodecProfileSuggestion { Codec = "brotli", Level = 9, MatchedRuleId = AdvisorRuleIds.SmallTournamentArm });
            list.Add(new CodecProfileSuggestion { Codec = "7z", Level = 9, MatchedRuleId = AdvisorRuleIds.SmallTournamentArm });
        }

        var max = profile switch
        {
            CompressionProfile.Small => 3,
            CompressionProfile.Tiny => 8,
            _ => 4,
        };

        return list.Take(max).ToList();
    }

    private static IReadOnlyList<PipelineRecipe> BuildTournamentCandidates(
        FileFingerprint fingerprint,
        CompressionProfile profile,
        string primaryPreprocess,
        IReadOnlyList<CodecProfileSuggestion> codecs,
        List<AdvisorDecision> decisions)
    {
        if (profile is CompressionProfile.Balanced)
        {
            return Array.Empty<PipelineRecipe>();
        }

        if (codecs.Count == 0)
        {
            return Array.Empty<PipelineRecipe>();
        }

        decisions.Add(new AdvisorDecision(
            AdvisorRuleIds.SmallTournamentArm,
            $"Profile {profile} tournament: {codecs.Count + 1} candidate pipeline(s) including primary."));

        var candidates = new List<PipelineRecipe>();
        foreach (var alt in codecs)
        {
            var preprocess = alt.Codec == "brotli" ? "noop" : primaryPreprocess;
            candidates.Add(BuildRecipe(fingerprint, profile, preprocess, alt, decisions, $"candidate-{candidates.Count + 1}"));
        }

        return candidates;
    }

    private static PipelineRecipe BuildRecipe(
        FileFingerprint fingerprint,
        CompressionProfile profile,
        string preprocess,
        CodecProfileSuggestion codec,
        List<AdvisorDecision> decisions,
        string? recipeSuffix = null)
    {
        var ext = NormalizeExtension(fingerprint);
        var id = recipeSuffix is null
            ? $"advisor-{profile.ToString().ToLowerInvariant()}-{ext.TrimStart('.')}"
            : $"advisor-{recipeSuffix}";

        var recipe = new PipelineRecipe
        {
            Id = id,
            Profile = profile,
            Verify = new VerifyOptions { DecompressRoundtrip = true, HashOriginal = true },
        };

        recipe.Steps.Add(new PreprocessStep { Name = preprocess });
        recipe.Steps.Add(new CompressStep
        {
            Codec = codec.Codec,
            Level = codec.Level,
            DictSizeKb = codec.DictSizeKb,
        });

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
        }

        return Path.GetExtension(fingerprint.Path).ToLowerInvariant();
    }
}