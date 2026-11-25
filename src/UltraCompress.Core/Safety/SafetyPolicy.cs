namespace UltraCompress.Core.Safety;

/// <summary>
/// Enforces that all five <see cref="CompressionProfile"/> levels execute exclusively
/// <see cref="OperationKind.LosslessCompression"/> with mandatory bit-exact verification
/// and zero lossy, format-converting, or data-removing transforms.
/// </summary>
public static class SafetyPolicy
{
    private static readonly HashSet<string> AllowedLosslessPreprocessorsSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "raw",
        "noop",
        "delta",
        "delta1",
        "delta2",
        "delta4",
        "delta8",
        "byteshuffle",
        "byteshuffle4",
        "byteshuffle8",
        "x86_bcj",
        "bcj_x86",
    };

    private static readonly HashSet<string> AllowedLosslessCodecsSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "store",
        "zstd",
        "brotli",
        "libdeflate",
        "7z",
        "xz",
        "gzip",
        "deflate",
        "lzma",
    };

    private static readonly Dictionary<string, OperationKind> KnownMutatingTransforms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png_palette_quant"] = OperationKind.LossyOptimization,
        ["jpeg_recompress"] = OperationKind.LossyOptimization,
        ["webp_lossy"] = OperationKind.LossyOptimization,
        ["avif_lossy"] = OperationKind.LossyOptimization,
        ["pdf_recompress_images"] = OperationKind.LossyOptimization,
        ["audio_reencode_lossy"] = OperationKind.LossyOptimization,
        ["lossy_quantize"] = OperationKind.LossyOptimization,

        ["pdf_object_stream"] = OperationKind.FormatConversion,
        ["sqlite_vacuum_pages"] = OperationKind.FormatConversion,
        ["image_transcode"] = OperationKind.FormatConversion,
        ["format_convert"] = OperationKind.FormatConversion,

        ["pe_strip_debug"] = OperationKind.DataRemoval,
        ["exif_strip"] = OperationKind.DataRemoval,
        ["strip_metadata"] = OperationKind.DataRemoval,
        ["remove_debug_symbols"] = OperationKind.DataRemoval,
    };

    public static IReadOnlySet<string> AllowedLosslessPreprocessors => AllowedLosslessPreprocessorsSet;

    public static IReadOnlySet<string> AllowedLosslessCodecs => AllowedLosslessCodecsSet;

    /// <summary>
    /// Every <see cref="CompressionProfile"/> tier maps strictly to <see cref="OperationKind.LosslessCompression"/>.
    /// </summary>
    public static OperationKind GetOperationKind(CompressionProfile profile)
    {
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "Undefined compression profile.");
        }

        return OperationKind.LosslessCompression;
    }

    public static bool IsMutating(OperationKind operationKind) =>
        operationKind != OperationKind.LosslessCompression;

    public static bool IsAllowedLosslessPreprocessor(string? name) =>
        !string.IsNullOrWhiteSpace(name) && AllowedLosslessPreprocessorsSet.Contains(name.Trim());

    public static bool IsAllowedLosslessCodec(string? codec) =>
        !string.IsNullOrWhiteSpace(codec) && AllowedLosslessCodecsSet.Contains(codec.Trim());

    public static OperationKind? TryClassifyTransform(string? preprocessName)
    {
        if (string.IsNullOrWhiteSpace(preprocessName))
        {
            return null;
        }

        var trimmed = preprocessName.Trim();
        if (AllowedLosslessPreprocessorsSet.Contains(trimmed))
        {
            return OperationKind.LosslessCompression;
        }

        if (KnownMutatingTransforms.TryGetValue(trimmed, out var mutatingKind))
        {
            return mutatingKind;
        }

        if (trimmed.Contains("lossy", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("quant", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("recompress", StringComparison.OrdinalIgnoreCase))
        {
            return OperationKind.LossyOptimization;
        }

        if (trimmed.Contains("transcode", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("convert", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("vacuum", StringComparison.OrdinalIgnoreCase))
        {
            return OperationKind.FormatConversion;
        }

        if (trimmed.Contains("strip", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("remove", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("prune", StringComparison.OrdinalIgnoreCase))
        {
            return OperationKind.DataRemoval;
        }

        return null;
    }

    /// <summary>
    /// Validates that a <see cref="PipelineRecipe"/> executed under any <see cref="CompressionProfile"/>
    /// contains strictly reversible lossless steps and has mandatory bit-exact verification enabled.
    /// </summary>
    public static void ValidateCompressionProfileRecipe(
        CompressionProfile profile,
        PipelineRecipe recipe,
        OperationKind requestedKind = OperationKind.LosslessCompression)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var profileKind = GetOperationKind(profile);

        if (requestedKind != profileKind)
        {
            throw new SafetyPolicyViolationException(
                "PROFILE_REQUIRES_LOSSLESS",
                $"Compression profile '{profile}' only permits '{OperationKind.LosslessCompression}', not '{requestedKind}'.");
        }

        if (recipe.Verify is null || !recipe.Verify.DecompressRoundtrip || !recipe.Verify.HashOriginal)
        {
            throw new SafetyPolicyViolationException(
                "MANDATORY_VERIFY_DISABLED",
                $"Compression profile '{profile}' requires mandatory bit-exact roundtrip and original hash verification.");
        }

        if (recipe.Steps is null || recipe.Steps.Count == 0)
        {
            throw new SafetyPolicyViolationException(
                "EMPTY_RECIPE_STEPS",
                $"Recipe '{recipe.Id}' has no pipeline steps.");
        }

        var hasCompressStep = false;
        foreach (var step in recipe.Steps)
        {
            switch (step)
            {
                case PreprocessStep preprocess:
                {
                    var classified = TryClassifyTransform(preprocess.Name);
                    if (classified is null)
                    {
                        throw new SafetyPolicyViolationException(
                            "UNVERIFIED_PREPROCESSOR_BANNED",
                            $"Preprocessor '{preprocess.Name}' is not a verified reversible lossless transform and cannot run in profile '{profile}'.");
                    }

                    if (classified != OperationKind.LosslessCompression)
                    {
                        throw new SafetyPolicyViolationException(
                            "MUTATING_PREPROCESSOR_BANNED",
                            $"Preprocessor '{preprocess.Name}' has mode '{classified}' and is strictly banned from compression profile '{profile}'.");
                    }

                    break;
                }

                case CompressStep compress:
                {
                    hasCompressStep = true;
                    if (!IsAllowedLosslessCodec(compress.Codec))
                    {
                        throw new SafetyPolicyViolationException(
                            "UNVERIFIED_CODEC_BANNED",
                            $"Codec '{compress.Codec}' is not an approved bit-exact lossless codec for profile '{profile}'.");
                    }

                    break;
                }
            }
        }

        if (!hasCompressStep)
        {
            throw new SafetyPolicyViolationException(
                "MISSING_COMPRESS_STEP",
                $"Recipe '{recipe.Id}' must include at least one lossless compress step.");
        }
    }
}
