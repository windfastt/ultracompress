using UltraCompress.Core.Intelligence;
using UltraCompress.Core.Safety;

namespace UltraCompress.Core.Advisor;

/// <summary>
/// Content-intelligent lossless pipeline advisor backed by <see cref="ContentInspector"/>, <see cref="StrategySelector"/>,
/// <see cref="ProfileContract"/>, and <see cref="SafetyPolicy"/>.
/// Never emits lossy, format-converting, or data-removing preprocessors on any compression profile.
/// </summary>
public static class PipelineAdvisor
{
    public const double HighEntropyGate = 7.9;
    public const double MediaEntropySkipReencode = 7.5;

    public static AdvisorPlan Plan(FileFingerprint fingerprint, CompressionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var contract = ProfileContract.Get(profile);
        contract.ValidateContractInvariants();

        var inspection = ResolveInspection(fingerprint);
        var report = StrategySelector.SelectForInspection(inspection, profile);
        var decisions = BuildAuditDecisions(fingerprint, inspection, report, profile);

        var primaryPreprocessStep = report.SelectedPipeline.ExecutableRecipe.Steps.OfType<PreprocessStep>().FirstOrDefault()
            ?? new PreprocessStep { Name = "raw" };
        var primaryCompressStep = report.SelectedPipeline.ExecutableRecipe.Steps.OfType<CompressStep>().Last();

        var primaryCodec = new CodecProfileSuggestion
        {
            Codec = primaryCompressStep.Codec,
            Level = primaryCompressStep.Level,
            DictSizeKb = primaryCompressStep.DictSizeKb,
            MatchedRuleId = decisions.FirstOrDefault()?.RuleId ?? AdvisorRuleIds.ProfileDefault,
        };

        int? stride = primaryPreprocessStep.Params.TryGetValue("stride", out var rawStride) && rawStride is int s
            ? s
            : null;

        var primaryRecipe = BuildValidatedRecipe(
            fingerprint,
            profile,
            primaryPreprocessStep.Name,
            stride,
            primaryCodec,
            primaryCompressStep.Method,
            primaryCompressStep.Solid);

        var alternates = BuildAlternateCodecs(inspection, primaryCodec, profile, contract, primaryPreprocessStep.Name);
        var candidates = BuildTournamentCandidates(fingerprint, profile, primaryPreprocessStep.Name, stride, alternates, decisions);

        var finalizedReport = report with
        {
            SelectedPipeline = report.SelectedPipeline with
            {
                ExecutableRecipe = primaryRecipe,
            },
        };

        return new AdvisorPlan
        {
            PrimaryRecipe = primaryRecipe,
            PrimaryCodec = primaryCodec,
            AlternateCodecs = alternates,
            TournamentCandidates = candidates,
            Decisions = decisions,
            DecisionReport = finalizedReport,
        };
    }

    public static StrategyDecisionReport ExplainPath(
        string targetPath,
        CompressionProfile profile = CompressionProfile.Balanced,
        ContentInspectorOptions? options = null) =>
        StrategySelector.AnalyzePath(targetPath, profile, options);

    public static Task<StrategyDecisionReport> ExplainPathAsync(
        string targetPath,
        CompressionProfile profile = CompressionProfile.Balanced,
        ContentInspectorOptions? options = null,
        CancellationToken cancellationToken = default) =>
        StrategySelector.AnalyzePathAsync(targetPath, profile, options, cancellationToken);

    public static PipelineRecipe SuggestPrimaryRecipe(FileFingerprint fingerprint, CompressionProfile profile) =>
        Plan(fingerprint, profile).PrimaryRecipe;

    private static ContentInspectionResult ResolveInspection(FileFingerprint fingerprint)
    {
        if (fingerprint.Inspection is not null)
        {
            return fingerprint.Inspection;
        }

        if (!string.IsNullOrWhiteSpace(fingerprint.Path) && File.Exists(fingerprint.Path))
        {
            return ContentInspector.InspectFile(fingerprint.Path);
        }

        return SynthesizeInspectionFromFingerprint(fingerprint);
    }

    private static ContentInspectionResult SynthesizeInspectionFromFingerprint(FileFingerprint fp)
    {
        var ext = !string.IsNullOrWhiteSpace(fp.Extension)
            ? fp.Extension.ToLowerInvariant()
            : Path.GetExtension(fp.Path).ToLowerInvariant();
        var mime = fp.DetectedType?.ToLowerInvariant() ?? string.Empty;
        var entropy = fp.EntropyBitsPerByte ?? 5.0;

        var (formatId, displayName, resolvedMime, contentClass, stride) = ResolveSyntheticFormat(ext, mime, entropy);
        var probeRatio = EstimateMicroProbeRatioFromEntropy(entropy, contentClass);
        var strideRed = contentClass is ContentClass.DatabaseAndTabular or ContentClass.UncompressedRasterImage or ContentClass.UncompressedAudio
            && entropy < 7.7
            ? 0.65
            : 0.0;

        return new ContentInspectionResult
        {
            Path = fp.Path,
            SizeBytes = fp.SizeBytes,
            SampledBytes = Math.Min(fp.SizeBytes, 320 * 1024),
            PeakBufferBytes = (int)Math.Min(fp.SizeBytes, 320 * 1024),
            InspectionDurationMs = 0.05,
            Signature = new SignatureMatch
            {
                FormatId = formatId,
                DisplayName = displayName,
                MimeType = resolvedMime,
                ContentClass = contentClass,
                Confidence = 0.90,
                MatchedEvidence = $"Synthesized from FileFingerprint ({resolvedMime}, {entropy:F2} bpb)",
                StructuralStrideBytes = stride,
                HasConsentGatedOptimizationCandidate = contentClass is ContentClass.CompressedVideo or ContentClass.CompressedImage or ContentClass.ExecutableBinary,
            },
            Windows = fp.BlockEntropies.Select((b, idx) => new InspectionWindowSample
            {
                Position = idx switch
                {
                    0 => WindowPosition.Head,
                    1 => WindowPosition.Quarter25,
                    2 => WindowPosition.Midpoint50,
                    3 => WindowPosition.Quarter75,
                    _ => WindowPosition.Tail,
                },
                Offset = b.Offset,
                Length = b.Length,
                EntropyBitsPerByte = b.EntropyBitsPerByte,
                Stride4DeltaEntropy = Math.Max(0, b.EntropyBitsPerByte - strideRed),
                Stride8DeltaEntropy = Math.Max(0, b.EntropyBitsPerByte - strideRed),
            }).ToList(),
            Signals = new ContentInspectionSignals
            {
                MeanEntropyBitsPerByte = entropy,
                InteriorEntropyBitsPerByte = entropy,
                MinWindowEntropy = entropy,
                MaxWindowEntropy = entropy,
                BigramConditionalEntropy = Math.Clamp(entropy * 0.85, 0.0, 8.0),
                BigramPredictability = Math.Clamp(1.0 - (entropy / 8.0), 0.0, 1.0),
                Stride4EntropyReduction = stride == 4 ? strideRed : strideRed * 0.5,
                Stride8EntropyReduction = stride == 8 ? strideRed : strideRed * 0.5,
                BestStrideBytes = strideRed > 0 ? (stride ?? 4) : 0,
                BestStrideEntropyReduction = strideRed,
                MicroProbeInputBytes = (int)Math.Min(fp.SizeBytes, 32 * 1024),
                MicroProbeCompressedBytes = (int)Math.Round(Math.Min(fp.SizeBytes, 32 * 1024) * probeRatio),
                MicroProbeRatio = probeRatio,
                PreprocessedMicroProbeRatio = strideRed > 0 ? Math.Max(0.10, probeRatio * 0.72) : null,
            },
            SampleFingerprintSha256 = fp.ContentHashSha256 ?? new string('0', 64),
            IsFullContentHash = fp.ContentHashSha256 is not null,
        };
    }

    private static (string FormatId, string DisplayName, string Mime, ContentClass Class, int? Stride) ResolveSyntheticFormat(
        string ext,
        string mime,
        double entropy)
    {
        if (mime.Contains("pdf") || ext == ".pdf")
            return ("PDF", "Portable Document Format", "application/pdf", ContentClass.ContainerDocument, null);
        if (mime.Contains("sqlite") || ext is ".sqlite" or ".db" or ".sqlite3")
            return ("SQLite", "SQLite 3 Database", "application/x-sqlite3", ContentClass.DatabaseAndTabular, 4096);
        if (mime.Contains("mp4") || ext == ".mp4")
            return ("MP4", "MPEG-4 Video", "video/mp4", ContentClass.CompressedVideo, null);
        if (mime.Contains("matroska") || ext == ".mkv")
            return ("MKV", "Matroska Video", "video/x-matroska", ContentClass.CompressedVideo, null);
        if (mime.Contains("quicktime") || ext == ".mov")
            return ("MOV", "QuickTime Video", "video/quicktime", ContentClass.CompressedVideo, null);
        if (mime.Contains("jpeg") || ext is ".jpg" or ".jpeg")
            return ("JPEG", "JPEG Still Image", "image/jpeg", ContentClass.CompressedImage, null);
        if (mime.Contains("heic") || ext is ".heic" or ".heif")
            return ("HEIC", "HEIC Still Image", "image/heic", ContentClass.CompressedImage, null);
        if (mime.Contains("png") || ext == ".png")
            return ("PNG", "Portable Network Graphics", "image/png", ContentClass.CompressedImage, null);
        if (mime.Contains("bmp") || ext == ".bmp")
            return ("BMP", "Windows Bitmap", "image/bmp", ContentClass.UncompressedRasterImage, 4);
        if (mime.Contains("dng") || ext == ".dng")
            return ("DNG", "Adobe Digital Negative", "image/x-adobe-dng", ContentClass.UncompressedRasterImage, 2);
        if (mime.Contains("tiff") || ext is ".tif" or ".tiff")
            return ("TIFF", "TIFF Raster Image", "image/tiff", entropy >= 7.85 ? ContentClass.CompressedImage : ContentClass.UncompressedRasterImage, 4);
        if (ext is ".raw" or ".cr2" or ".cr3" or ".nef" or ".arw")
            return ("RAW", "Camera RAW Image", "image/x-raw", ContentClass.UncompressedRasterImage, 2);
        if (mime.Contains("msdownload") || mime.Contains("x-dosexec") || mime.Contains("x-executable") || ext is ".exe" or ".dll")
            return ("PE", "Windows PE/COFF Binary", "application/x-msdownload", ContentClass.ExecutableBinary, null);
        if (mime.Contains("elf") || ext == ".so")
            return ("ELF", "ELF Binary", "application/x-elf", ContentClass.ExecutableBinary, null);
        if (ext == ".sql")
            return ("SQL", "SQL Dump", "application/sql", ContentClass.DatabaseAndTabular, null);
        if (ext == ".csv")
            return ("CSV", "CSV Tabular Data", "text/csv", ContentClass.DatabaseAndTabular, 4);
        if (ext == ".tsv")
            return ("TSV", "TSV Tabular Data", "text/tab-separated-values", ContentClass.DatabaseAndTabular, 4);
        if (ext is ".ndjson" or ".jsonl")
            return ("NDJSON", "NDJSON Stream", "application/x-ndjson", ContentClass.DatabaseAndTabular, null);
        if (mime.Contains("json") || ext == ".json")
            return ("JSON", "JSON Document", "application/json", ContentClass.DatabaseAndTabular, null);
        if (ext == ".parquet")
            return ("Parquet", "Apache Parquet", "application/vnd.apache.parquet", ContentClass.DatabaseAndTabular, 8);
        if (ext is ".cs" or ".ts" or ".js" or ".py" or ".rs" or ".go" or ".cpp" or ".c" or ".h" or ".java")
            return ("SourceCode", "Source Code", "text/x-source-code", ContentClass.SourceAndText, null);
        if (ext is ".txt" or ".md" or ".log" or ".xml" or ".html" or ".htm")
            return ("UTF-8", "UTF-8 Text", "text/plain", ContentClass.SourceAndText, null);
        if (ext is ".zip" or ".7z" or ".gz" or ".zst" or ".xz")
            return ("ZIP", "Compressed Archive", "application/zip", ContentClass.CompressedArchive, null);

        return ("Binary", "Generic Binary", string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime, ContentClass.GenericBinary, null);
    }

    private static double EstimateMicroProbeRatioFromEntropy(double entropy, ContentClass contentClass)
    {
        if (entropy >= 7.85)
        {
            return 0.999;
        }

        if (contentClass is ContentClass.CompressedVideo or ContentClass.CompressedImage or ContentClass.CompressedAudio or ContentClass.CompressedArchive
            && entropy >= 7.50)
        {
            return 0.996;
        }

        return Math.Clamp(entropy / 8.0, 0.10, 0.999);
    }

    private static List<AdvisorDecision> BuildAuditDecisions(
        FileFingerprint fingerprint,
        ContentInspectionResult inspection,
        StrategyDecisionReport report,
        CompressionProfile profile)
    {
        var decisions = new List<AdvisorDecision>();

        if (report.SelectedPipeline.StrategyId == "tiny-file-fast-zstd")
        {
            decisions.Add(new AdvisorDecision(
                AdvisorRuleIds.TinyFileFast,
                report.SelectedPipeline.RationaleSummary));
            return decisions;
        }

        if (fingerprint.EntropyBitsPerByte is >= MediaEntropySkipReencode
            && inspection.Signature.ContentClass is ContentClass.CompressedImage or ContentClass.CompressedAudio)
        {
            decisions.Add(new AdvisorDecision(
                AdvisorRuleIds.EntropySkipReencode,
                $"Entropy {fingerprint.EntropyBitsPerByte:F2} >= {MediaEntropySkipReencode}; skip structural preprocessors and route to lossless bypass."));
        }

        if (report.SelectedPipeline.PrimaryCodec == "store")
        {
            if (inspection.Signature.ContentClass == ContentClass.CompressedVideo)
            {
                decisions.Add(new AdvisorDecision(
                    AdvisorRuleIds.MediaStreamBypass,
                    report.SelectedPipeline.RationaleSummary));
            }

            decisions.Add(new AdvisorDecision(
                AdvisorRuleIds.EntropyGate,
                report.SelectedPipeline.RationaleSummary));
            return decisions;
        }

        switch (inspection.Signature.ContentClass)
        {
            case ContentClass.DatabaseAndTabular:
                if (inspection.Signature.FormatId == "SQLite")
                {
                    decisions.Add(new AdvisorDecision(AdvisorRuleIds.ExtensionSqlite, report.SelectedPipeline.RationaleSummary));
                }
                else if (inspection.Signature.FormatId is "CSV" or "TSV")
                {
                    decisions.Add(new AdvisorDecision(AdvisorRuleIds.ExtensionLogCsv, report.SelectedPipeline.RationaleSummary));
                }

                decisions.Add(new AdvisorDecision(AdvisorRuleIds.StrideByteShuffleDelta, report.SelectedPipeline.RationaleSummary));
                break;

            case ContentClass.ExecutableBinary:
                decisions.Add(new AdvisorDecision(AdvisorRuleIds.ExtensionPe, report.SelectedPipeline.RationaleSummary));
                decisions.Add(new AdvisorDecision(AdvisorRuleIds.BinaryBcjFilter, report.SelectedPipeline.RationaleSummary));
                break;

            case ContentClass.UncompressedRasterImage:
            case ContentClass.UncompressedAudio:
                decisions.Add(new AdvisorDecision(AdvisorRuleIds.ExtensionPng, report.SelectedPipeline.RationaleSummary));
                break;

            case ContentClass.SourceAndText:
                decisions.Add(new AdvisorDecision(AdvisorRuleIds.SolidSourceDictionary, report.SelectedPipeline.RationaleSummary));
                break;
        }

        decisions.Add(new AdvisorDecision(
            AdvisorRuleIds.ProfileDefault,
            $"Profile {profile} -> {report.SelectedPipeline.PrimaryCodec} level {report.SelectedPipeline.CodecLevel}."));

        return decisions;
    }

    private static IReadOnlyList<CodecProfileSuggestion> BuildAlternateCodecs(
        ContentInspectionResult inspection,
        CodecProfileSuggestion primary,
        CompressionProfile profile,
        ProfileContract contract,
        string preprocess)
    {
        if (profile is CompressionProfile.Instant or CompressionProfile.Balanced
            || primary.Codec == "store"
            || inspection.SizeBytes < 4 * 1024)
        {
            return Array.Empty<CodecProfileSuggestion>();
        }

        if (inspection.Signals.InteriorEntropyBitsPerByte >= contract.IncompressibleEntropyBypassBitsPerByte)
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
            var method = alt.Codec == "7z" ? "LZMA2" : null;
            var solid = alt.Codec == "7z" ? false : (bool?)null;
            candidates.Add(BuildValidatedRecipe(fingerprint, profile, primaryPreprocess, stride, alt, method, solid, $"candidate-{candidates.Count + 1}"));
        }

        return candidates;
    }

    private static PipelineRecipe BuildValidatedRecipe(
        FileFingerprint fingerprint,
        CompressionProfile profile,
        string preprocess,
        int? stride,
        CodecProfileSuggestion codec,
        string? method = null,
        bool? solid = null,
        string? recipeSuffix = null)
    {
        var ext = Path.GetExtension(fingerprint.Path).ToLowerInvariant().TrimStart('.');
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
            Method = codec.Codec == "7z" ? (method ?? "LZMA2") : method,
            Solid = codec.Codec == "7z" ? (solid ?? false) : solid,
            Threads = codec.Codec == "7z" ? 0 : null,
        };

        var recipe = new PipelineRecipe
        {
            Id = id.Length <= 128 ? id : id[..128],
            Profile = profile,
            Verify = new VerifyOptions { DecompressRoundtrip = true, HashOriginal = true },
            Steps = [preprocessStep, compressStep],
        };

        SafetyPolicy.ValidateCompressionProfileRecipe(profile, recipe);
        return recipe;
    }
}
