using System.Diagnostics;
using System.Security.Cryptography;
using UltraCompress.Core.Safety;

namespace UltraCompress.Core.Intelligence;

/// <summary>
/// Explainable content-aware lossless strategy selector for single files, streams, and heterogeneous directories.
/// Every generated pipeline upholds <see cref="OperationKind.LosslessCompression"/> and passes <see cref="SafetyPolicy"/>.
/// </summary>
public static class StrategySelector
{
    public const double IncompressibleEntropyFloor = 7.85;
    public const double IncompressibleProbeRatioFloor = 0.985;
    public const double StrideCorrelationMinReduction = 0.18;

    public static StrategyDecisionReport AnalyzePath(
        string targetPath,
        CompressionProfile profile = CompressionProfile.Balanced,
        ContentInspectorOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var fullPath = Path.GetFullPath(targetPath);

        if (Directory.Exists(fullPath))
        {
            return AnalyzeDirectory(fullPath, profile, options);
        }

        if (File.Exists(fullPath))
        {
            return AnalyzeFile(fullPath, profile, options);
        }

        throw new FileNotFoundException($"Target path '{fullPath}' does not exist as a file or directory.", fullPath);
    }

    public static async Task<StrategyDecisionReport> AnalyzePathAsync(
        string targetPath,
        CompressionProfile profile = CompressionProfile.Balanced,
        ContentInspectorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var fullPath = Path.GetFullPath(targetPath);

        if (Directory.Exists(fullPath))
        {
            return await AnalyzeDirectoryAsync(fullPath, profile, options, cancellationToken);
        }

        if (File.Exists(fullPath))
        {
            return await AnalyzeFileAsync(fullPath, profile, options, cancellationToken);
        }

        throw new FileNotFoundException($"Target path '{fullPath}' does not exist as a file or directory.", fullPath);
    }

    public static StrategyDecisionReport AnalyzeFile(
        string filePath,
        CompressionProfile profile = CompressionProfile.Balanced,
        ContentInspectorOptions? options = null)
    {
        var inspection = ContentInspector.InspectFile(filePath, options);
        return SelectForInspection(inspection, profile);
    }

    public static async Task<StrategyDecisionReport> AnalyzeFileAsync(
        string filePath,
        CompressionProfile profile = CompressionProfile.Balanced,
        ContentInspectorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inspection = await ContentInspector.InspectFileAsync(filePath, options, cancellationToken);
        return SelectForInspection(inspection, profile);
    }

    public static StrategyDecisionReport AnalyzeStream(
        Stream stream,
        string? pathHint = null,
        CompressionProfile profile = CompressionProfile.Balanced,
        ContentInspectorOptions? options = null)
    {
        var inspection = ContentInspector.Inspect(stream, pathHint, options);
        return SelectForInspection(inspection, profile);
    }

    public static async Task<StrategyDecisionReport> AnalyzeStreamAsync(
        Stream stream,
        string? pathHint = null,
        CompressionProfile profile = CompressionProfile.Balanced,
        ContentInspectorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inspection = await ContentInspector.InspectAsync(stream, pathHint, options, cancellationToken);
        return SelectForInspection(inspection, profile);
    }

    public static StrategyDecisionReport AnalyzeDirectory(
        string directoryPath,
        CompressionProfile profile = CompressionProfile.Balanced,
        ContentInspectorOptions? options = null)
    {
        var fullDir = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullDir))
        {
            throw new DirectoryNotFoundException($"Target directory not found: '{fullDir}'.");
        }

        var sw = Stopwatch.StartNew();
        var files = Directory.EnumerateFiles(fullDir, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var inspections = new List<ContentInspectionResult>(files.Count);
        foreach (var file in files)
        {
            inspections.Add(ContentInspector.InspectFile(file, options));
        }

        var confirmedHashes = ResolveExactHashesForDuplicateCandidates(inspections);
        sw.Stop();

        return SelectForWorkload(fullDir, inspections, profile, confirmedHashes, sw.Elapsed.TotalMilliseconds);
    }

    public static async Task<StrategyDecisionReport> AnalyzeDirectoryAsync(
        string directoryPath,
        CompressionProfile profile = CompressionProfile.Balanced,
        ContentInspectorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var fullDir = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullDir))
        {
            throw new DirectoryNotFoundException($"Target directory not found: '{fullDir}'.");
        }

        var sw = Stopwatch.StartNew();
        var files = Directory.EnumerateFiles(fullDir, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var inspections = new List<ContentInspectionResult>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inspections.Add(await ContentInspector.InspectFileAsync(file, options, cancellationToken));
        }

        var confirmedHashes = await ResolveExactHashesForDuplicateCandidatesAsync(inspections, cancellationToken);
        sw.Stop();

        return SelectForWorkload(fullDir, inspections, profile, confirmedHashes, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Selects an explainable lossless strategy for a single inspected stream/file.
    /// </summary>
    public static StrategyDecisionReport SelectForInspection(
        ContentInspectionResult inspection,
        CompressionProfile profile = CompressionProfile.Balanced)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var contract = ProfileContract.Get(profile);
        contract.ValidateContractInvariants();

        var targetPath = string.IsNullOrWhiteSpace(inspection.Path) ? "<stream>" : inspection.Path;
        var sig = inspection.Signature;
        var sigClass = sig.ContentClass;
        var s = inspection.Signals;

        if (inspection.SizeBytes == 0 || sigClass == ContentClass.Empty)
        {
            return BuildEmptyReport(targetPath, isDirectory: false, profile, inspection.InspectionDurationMs);
        }

        // Tiny file fast path (< 4 KiB) when below incompressible gate
        var entropyGate = Math.Min(IncompressibleEntropyFloor, contract.IncompressibleEntropyBypassBitsPerByte);
        if (inspection.SizeBytes < 4 * 1024 && s.MeanEntropyBitsPerByte < entropyGate
            && sigClass is not (ContentClass.CompressedVideo or ContentClass.CompressedImage or ContentClass.CompressedAudio or ContentClass.CompressedArchive))
        {
            return BuildTinyFileReport(inspection, targetPath, profile);
        }

        // 1. Video stream or high-entropy already-compressed media/archive -> Store / FastStreamBypass
        if (sigClass == ContentClass.CompressedVideo && IsEffectivelyIncompressibleMedia(s, sigClass))
        {
            return BuildVideoBypassReport(inspection, targetPath, profile);
        }

        if (sigClass is ContentClass.CompressedImage or ContentClass.CompressedAudio or ContentClass.CompressedArchive
            && IsEffectivelyIncompressibleMedia(s, sigClass))
        {
            return BuildCompressedMediaOrArchiveBypassReport(inspection, targetPath, profile);
        }

        // Generic high-entropy stream (encrypted / random / unknown compressed blob)
        if (s.InteriorEntropyBitsPerByte >= entropyGate
            && s.MicroProbeRatio >= IncompressibleProbeRatioFloor
            && (s.PreprocessedMicroProbeRatio ?? 1.0) >= 0.975)
        {
            return BuildHighEntropyBypassReport(inspection, targetPath, profile);
        }

        // 2. Database & structured tabular (SQLite, SQL dump, CSV, TSV, JSON, NDJSON, Parquet, Arrow, structured binary)
        if (sigClass == ContentClass.DatabaseAndTabular)
        {
            return BuildDatabaseSingleFileReport(inspection, targetPath, profile);
        }

        // 3. Uncompressed raster images & camera RAW (BMP, TIFF, DNG, RAW) or uncompressed PCM audio (WAV-PCM, AIFF)
        if (sigClass is ContentClass.UncompressedRasterImage or ContentClass.UncompressedAudio)
        {
            return BuildUncompressedRasterOrAudioReport(inspection, targetPath, profile);
        }

        // 4. Executable binaries (PE/COFF, .NET Assembly/Metadata, ELF, Mach-O, Wasm)
        if (sigClass == ContentClass.ExecutableBinary)
        {
            return BuildExecutableBinaryReport(inspection, targetPath, profile);
        }

        // 5. Source code & UTF-8 / UTF-16 text
        if (sigClass == ContentClass.SourceAndText)
        {
            return BuildSourceOrTextSingleFileReport(inspection, targetPath, profile);
        }

        // 6. Container documents (PDF, TAR) or compressible binary/PNG/etc.
        return BuildGeneralCompressibleReport(inspection, targetPath, profile);
    }

    /// <summary>
    /// Selects an explainable multi-file / heterogeneous directory strategy from per-file inspections.
    /// </summary>
    public static StrategyDecisionReport SelectForWorkload(
        string workloadPath,
        IReadOnlyList<ContentInspectionResult> items,
        CompressionProfile profile = CompressionProfile.Balanced,
        IReadOnlyDictionary<int, string>? exactContentHashes = null,
        double? wallClockDurationMs = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ProfileContract.Get(profile).ValidateContractInvariants();

        if (items.Count == 0)
        {
            return BuildEmptyReport(workloadPath, isDirectory: true, profile, wallClockDurationMs ?? 0.0);
        }

        long totalBytes = 0;
        long sampledBytes = 0;
        int peakBufferBytes = 0;
        double sumDurationMs = 0;

        foreach (var item in items)
        {
            totalBytes += item.SizeBytes;
            sampledBytes += item.SampledBytes;
            peakBufferBytes = Math.Max(peakBufferBytes, item.PeakBufferBytes);
            sumDurationMs += item.InspectionDurationMs;
        }

        var durationMs = wallClockDurationMs ?? sumDurationMs;

        var resolvedKeys = BuildResolvedIdentityKeys(items, exactContentHashes);
        var (duplicateFiles, duplicateBytes) = ComputeDuplicateSavingsFromKeys(items, Enumerable.Range(0, items.Count), resolvedKeys);

        var blendedSignals = BlendWorkloadSignals(items, totalBytes);
        var representativeWindows = SelectRepresentativeWindows(items);

        var bytesByClass = new Dictionary<ContentClass, long>();
        var countByClass = new Dictionary<ContentClass, int>();
        int consentCandidateFiles = 0;

        foreach (var item in items)
        {
            var c = item.Signature.ContentClass;
            bytesByClass[c] = bytesByClass.GetValueOrDefault(c) + item.SizeBytes;
            countByClass[c] = countByClass.GetValueOrDefault(c) + 1;
            if (item.Signature.HasConsentGatedOptimizationCandidate)
            {
                consentCandidateFiles++;
            }
        }

        var compressedImageBytes = bytesByClass.GetValueOrDefault(ContentClass.CompressedImage);
        var rawRasterBytes = bytesByClass.GetValueOrDefault(ContentClass.UncompressedRasterImage);
        var sourceBytes = bytesByClass.GetValueOrDefault(ContentClass.SourceAndText);
        var dbBytes = bytesByClass.GetValueOrDefault(ContentClass.DatabaseAndTabular);
        var videoBytes = bytesByClass.GetValueOrDefault(ContentClass.CompressedVideo);

        var photoBytes = compressedImageBytes + rawRasterBytes;

        // Canonical Workload 4: Heterogeneous Photo Collection (mix of compressed JPEG/HEIC + uncompressed BMP/TIFF/DNG/RAW)
        if (compressedImageBytes > 0 && rawRasterBytes > 0 && photoBytes >= totalBytes * 0.55)
        {
            return BuildHeterogeneousPhotoCollectionReport(
                workloadPath,
                items,
                resolvedKeys,
                profile,
                totalBytes,
                sampledBytes,
                peakBufferBytes,
                durationMs,
                blendedSignals,
                representativeWindows,
                duplicateFiles,
                duplicateBytes,
                consentCandidateFiles);
        }

        // Canonical Workload 2: Source Repository (source/text + config JSON/XML/YAML files)
        var repoTextAndConfigBytes = sourceBytes
            + items.Where(i => i.Signature.FormatId is "JSON" or "NDJSON" && i.SizeBytes < 16 * 1024 * 1024).Sum(i => i.SizeBytes);
        if (countByClass.GetValueOrDefault(ContentClass.SourceAndText) >= 1
            && repoTextAndConfigBytes >= totalBytes * 0.60)
        {
            return BuildSourceRepositoryReport(
                workloadPath,
                items,
                resolvedKeys,
                profile,
                totalBytes,
                sampledBytes,
                peakBufferBytes,
                durationMs,
                blendedSignals,
                representativeWindows,
                duplicateFiles,
                duplicateBytes);
        }

        // Canonical Workload 3 (directory of database dumps / shards / CSV partitions)
        if (dbBytes >= totalBytes * 0.70)
        {
            return BuildDatabaseDirectoryReport(
                workloadPath,
                items,
                profile,
                totalBytes,
                sampledBytes,
                peakBufferBytes,
                durationMs,
                blendedSignals,
                representativeWindows,
                duplicateFiles,
                duplicateBytes);
        }

        // Canonical Workload 1 (directory of compressed video streams)
        if (videoBytes >= totalBytes * 0.85)
        {
            return BuildVideoDirectoryBypassReport(
                workloadPath,
                items,
                resolvedKeys,
                profile,
                totalBytes,
                sampledBytes,
                peakBufferBytes,
                durationMs,
                blendedSignals,
                representativeWindows,
                duplicateFiles,
                duplicateBytes);
        }

        // General Heterogeneous Multi-Class Directory Split
        return BuildGeneralHeterogeneousDirectoryReport(
            workloadPath,
            items,
            resolvedKeys,
            profile,
            totalBytes,
            sampledBytes,
            peakBufferBytes,
            durationMs,
            blendedSignals,
            representativeWindows,
            duplicateFiles,
            duplicateBytes,
            consentCandidateFiles);
    }

    private static StrategyDecisionReport BuildVideoBypassReport(
        ContentInspectionResult inspection,
        string targetPath,
        CompressionProfile profile)
    {
        var s = inspection.Signals;
        var sig = inspection.Signature;
        var predictedRatio = Math.Clamp(Math.Min(1.0, s.MicroProbeRatio), 0.9985, 1.0);
        var predictedBytes = (long)Math.Round(inspection.SizeBytes * predictedRatio);
        var expectedGainPct = (1.0 - predictedRatio) * 100.0;

        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-video-store",
            profile,
            preprocess: "raw",
            codec: "store",
            level: 0);

        var rationale =
            $"Detected {sig.DisplayName} ({sig.FormatId}) with high interior entropy ({s.InteriorEntropyBitsPerByte:F2} bpb) " +
            $"and 32 KiB micro-probe ratio {s.MicroProbeRatio:F4} (<0.2% expected gain: {expectedGainPct:F2}%). " +
            "Selected Store / FastStreamBypass with streaming integrity verification instead of burning massive LZMA2/Zstd CPU.";

        var route = new WorkloadRoutePlan
        {
            RouteId = "video-stream-bypass",
            RouteName = $"Compressed Video ({sig.FormatId})",
            ContentClass = ContentClass.CompressedVideo,
            Formats = new[] { sig.FormatId },
            FileCount = 1,
            InputBytes = inspection.SizeBytes,
            ShareOfBytesPercent = 100.0,
            Preprocessors = new[] { "xxh64_stream_hash" },
            Codec = "store",
            CodecLevel = 0,
            PipelineSummary = "Store / FastStreamBypass + XXH64/SHA-256 Integrity",
            PredictedRatio = predictedRatio,
            PredictedCompressedBytes = predictedBytes,
            SpeedTier = SpeedTier.DiskSpeedBypass,
            Rationale = rationale,
        };

        return new StrategyDecisionReport
        {
            TargetPath = targetPath,
            IsDirectory = false,
            Profile = profile,
            DetectedFormat = sig.FormatId,
            DetectedMimeType = sig.MimeType,
            ContentClass = ContentClass.CompressedVideo,
            WorkloadArchetype = WorkloadArchetype.CompressedVideoStream,
            TotalFiles = 1,
            TotalBytes = inspection.SizeBytes,
            SampledBytes = inspection.SampledBytes,
            PeakInspectionBufferBytes = inspection.PeakBufferBytes,
            InspectionDurationMs = inspection.InspectionDurationMs,
            Signals = s,
            WindowSamples = inspection.Windows,
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "store-fast-stream-bypass",
                StrategyName = "Store / FastStreamBypass",
                Preprocessors = new[] { "xxh64_stream_hash" },
                PrimaryCodec = "store",
                CodecLevel = 0,
                SolidBlockGrouping = false,
                DeduplicateExactFiles = false,
                RationaleSummary = rationale,
                ExecutableRecipe = recipe,
            },
            Routes = new[] { route },
            RejectedAlternatives = new[]
            {
                new RejectedStrategyAlternative
                {
                    StrategyName = "LZMA2-9 (7z Ultra)",
                    ReasonCode = "INCOMPRESSIBLE_VIDEO_CPU_WASTE",
                    RejectionReason = $"Entropy-coded video packets ({s.InteriorEntropyBitsPerByte:F2} bpb) yield <0.2% lossless reduction while stalling on arithmetic range-coder mispredictions.",
                    EstimatedPenalty = "<0.2% expected gain vs 40x–120x higher CPU time and 64–768 MiB dictionary RAM",
                },
                new RejectedStrategyAlternative
                {
                    StrategyName = "Zstd-19 (Long-Window)",
                    ReasonCode = "SUB_0_2_PERCENT_GAIN",
                    RejectionReason = $"32 KiB micro-probe measured {s.MicroProbeRatio:F4} ratio; long-window match search finds virtually zero cross-GOP byte matches.",
                    EstimatedPenalty = "<0.2% expected gain vs 15x–35x slower throughput than Store bypass",
                },
                new RejectedStrategyAlternative
                {
                    StrategyName = "ByteShuffle + StrideDelta",
                    ReasonCode = "NO_STRIDE_CORRELATION",
                    RejectionReason = $"4-byte/8-byte stride delta entropy reduction is {Math.Max(s.Stride4EntropyReduction, s.Stride8EntropyReduction):F3} bpb (none).",
                    EstimatedPenalty = "Extra memory-bandwidth pass for 0.0% size improvement",
                },
            },
            PredictedRatio = predictedRatio,
            PredictedSavingsPercent = (1.0 - predictedRatio) * 100.0,
            PredictedCompressedBytes = predictedBytes,
            PredictedSpeedTier = SpeedTier.DiskSpeedBypass,
            EstimatedThroughputRange = "1,500–4,500 MB/s (NVMe/disk I/O bound)",
            ConsentGatedAdvisoryNote =
                "Non-executed advisory: Bit-exact LosslessCompression preserves the original video bitstream untouched. " +
                "Meaningful size reduction requires separate user-consented OperationKind.LossyOptimization (AV1/HEVC transcode) or " +
                "OperationKind.DataRemoval (container metadata strip) in Optimization Studio, with original preservation enabled by default.",
        };
    }

    private static StrategyDecisionReport BuildCompressedMediaOrArchiveBypassReport(
        ContentInspectionResult inspection,
        string targetPath,
        CompressionProfile profile)
    {
        var s = inspection.Signals;
        var sig = inspection.Signature;
        var predictedRatio = Math.Clamp(Math.Min(1.0, s.MicroProbeRatio), 0.996, 1.0);
        var predictedBytes = (long)Math.Round(inspection.SizeBytes * predictedRatio);

        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-media-store",
            profile,
            preprocess: "raw",
            codec: "store",
            level: 0);

        var rationale =
            $"Detected already-compressed {sig.DisplayName} ({sig.FormatId}) with {s.InteriorEntropyBitsPerByte:F2} bpb entropy " +
            $"and micro-probe ratio {s.MicroProbeRatio:F4}. Routing to Store / FastStreamBypass to preserve bit-exact fidelity at disk speed.";

        string? advisory = sig.HasConsentGatedOptimizationCandidate
            ? $"Non-executed advisory: {sig.FormatId} contains metadata or compressed media streams. Optional EXIF/metadata hygiene (OperationKind.DataRemoval) or lossy re-encoding (OperationKind.LossyOptimization) is never applied during LosslessCompression and requires explicit consent."
            : null;

        return new StrategyDecisionReport
        {
            TargetPath = targetPath,
            IsDirectory = false,
            Profile = profile,
            DetectedFormat = sig.FormatId,
            DetectedMimeType = sig.MimeType,
            ContentClass = sig.ContentClass,
            WorkloadArchetype = WorkloadArchetype.CompressedMediaOrArchive,
            TotalFiles = 1,
            TotalBytes = inspection.SizeBytes,
            SampledBytes = inspection.SampledBytes,
            PeakInspectionBufferBytes = inspection.PeakBufferBytes,
            InspectionDurationMs = inspection.InspectionDurationMs,
            Signals = s,
            WindowSamples = inspection.Windows,
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "store-fast-stream-bypass",
                StrategyName = "Store / FastStreamBypass",
                Preprocessors = new[] { "xxh64_stream_hash" },
                PrimaryCodec = "store",
                CodecLevel = 0,
                RationaleSummary = rationale,
                ExecutableRecipe = recipe,
            },
            Routes = new[]
            {
                new WorkloadRoutePlan
                {
                    RouteId = "compressed-bypass",
                    RouteName = $"Pre-Compressed {sig.FormatId}",
                    ContentClass = sig.ContentClass,
                    Formats = new[] { sig.FormatId },
                    FileCount = 1,
                    InputBytes = inspection.SizeBytes,
                    ShareOfBytesPercent = 100.0,
                    Preprocessors = new[] { "xxh64_stream_hash" },
                    Codec = "store",
                    CodecLevel = 0,
                    PipelineSummary = "Store / FastStreamBypass",
                    PredictedRatio = predictedRatio,
                    PredictedCompressedBytes = predictedBytes,
                    SpeedTier = SpeedTier.DiskSpeedBypass,
                    Rationale = rationale,
                },
            },
            RejectedAlternatives = new[]
            {
                new RejectedStrategyAlternative
                {
                    StrategyName = "LZMA2-9 / Zstd-19 Re-Compression",
                    ReasonCode = "ALREADY_COMPRESSED_PAYLOAD",
                    RejectionReason = $"{sig.FormatId} already applies domain-specific entropy coding ({s.InteriorEntropyBitsPerByte:F2} bpb); re-compressing yields <0.4% savings.",
                    EstimatedPenalty = "20x–90x CPU overhead for negligible (<0.4%) gain",
                },
                new RejectedStrategyAlternative
                {
                    StrategyName = "Silent Lossy Re-Encode / Metadata Strip",
                    ReasonCode = "LOSSLESS_BOUNDARY_ENFORCED",
                    RejectionReason = "Violates OperationKind.LosslessCompression bit-exact guarantee.",
                    EstimatedPenalty = "Irreversible fidelity or metadata mutation without explicit user consent",
                },
            },
            PredictedRatio = predictedRatio,
            PredictedSavingsPercent = (1.0 - predictedRatio) * 100.0,
            PredictedCompressedBytes = predictedBytes,
            PredictedSpeedTier = SpeedTier.DiskSpeedBypass,
            EstimatedThroughputRange = "1,500–4,500 MB/s (disk I/O bound)",
            ConsentGatedAdvisoryNote = advisory,
        };
    }

    private static StrategyDecisionReport BuildHighEntropyBypassReport(
        ContentInspectionResult inspection,
        string targetPath,
        CompressionProfile profile)
    {
        var s = inspection.Signals;
        var sig = inspection.Signature;
        var predictedRatio = 1.0;

        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-entropy-bypass",
            profile,
            preprocess: "raw",
            codec: "store",
            level: 0);

        var rationale =
            $"Multi-window entropy {s.InteriorEntropyBitsPerByte:F2} bpb (variance {s.EntropyVariance:F4}) and 32 KiB micro-probe ratio {s.MicroProbeRatio:F4} " +
            "confirm incompressible/encrypted data; selected Store / FastStreamBypass.";

        return new StrategyDecisionReport
        {
            TargetPath = targetPath,
            IsDirectory = false,
            Profile = profile,
            DetectedFormat = sig.FormatId,
            DetectedMimeType = sig.MimeType,
            ContentClass = sig.ContentClass,
            WorkloadArchetype = WorkloadArchetype.CompressedMediaOrArchive,
            TotalFiles = 1,
            TotalBytes = inspection.SizeBytes,
            SampledBytes = inspection.SampledBytes,
            PeakInspectionBufferBytes = inspection.PeakBufferBytes,
            InspectionDurationMs = inspection.InspectionDurationMs,
            Signals = s,
            WindowSamples = inspection.Windows,
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "store-fast-stream-bypass",
                StrategyName = "Store / FastStreamBypass",
                Preprocessors = new[] { "xxh64_stream_hash" },
                PrimaryCodec = "store",
                CodecLevel = 0,
                RationaleSummary = rationale,
                ExecutableRecipe = recipe,
            },
            Routes = new[]
            {
                new WorkloadRoutePlan
                {
                    RouteId = "high-entropy-bypass",
                    RouteName = "High-Entropy Stream",
                    ContentClass = sig.ContentClass,
                    Formats = new[] { sig.FormatId },
                    FileCount = 1,
                    InputBytes = inspection.SizeBytes,
                    ShareOfBytesPercent = 100.0,
                    Preprocessors = new[] { "xxh64_stream_hash" },
                    Codec = "store",
                    CodecLevel = 0,
                    PipelineSummary = "Store / FastStreamBypass",
                    PredictedRatio = predictedRatio,
                    PredictedCompressedBytes = inspection.SizeBytes,
                    SpeedTier = SpeedTier.DiskSpeedBypass,
                    Rationale = rationale,
                },
            },
            RejectedAlternatives = new[]
            {
                new RejectedStrategyAlternative
                {
                    StrategyName = "Zstd-19 / LZMA2-9",
                    ReasonCode = "HIGH_ENTROPY_INCOMPRESSIBLE",
                    RejectionReason = $"Near-uniform byte distribution ({s.InteriorEntropyBitsPerByte:F2} bpb) with zero stride reduction ({s.BestStrideEntropyReduction:F3} bpb).",
                    EstimatedPenalty = "0% compression gain + frame header expansion + high CPU cost",
                },
            },
            PredictedRatio = predictedRatio,
            PredictedSavingsPercent = 0.0,
            PredictedCompressedBytes = inspection.SizeBytes,
            PredictedSpeedTier = SpeedTier.DiskSpeedBypass,
            EstimatedThroughputRange = "1,500–4,500 MB/s (disk I/O bound)",
        };
    }

    private static StrategyDecisionReport BuildDatabaseSingleFileReport(
        ContentInspectionResult inspection,
        string targetPath,
        CompressionProfile profile)
    {
        var s = inspection.Signals;
        var sig = inspection.Signature;

        // Already Zstd/Snappy-compressed Parquet/columnar payload -> Store bypass
        if (s.InteriorEntropyBitsPerByte >= 7.92
            && s.MicroProbeRatio >= 0.992
            && (s.PreprocessedMicroProbeRatio ?? 1.0) >= 0.985)
        {
            return BuildCompressedMediaOrArchiveBypassReport(inspection, targetPath, profile);
        }

        var (codec, level, method, dictKb, speedTier, throughput) = PickLongWindowBackend(profile, inspection.SizeBytes);

        var shuffleStride = s.BestStrideBytes == 8 || sig.FormatId is "SQLite" or "Parquet" or "Arrow" ? 8 : 4;
        var preprocessors = new[] { $"byteshuffle({shuffleStride}B)", $"delta({shuffleStride}B)" };

        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-db-{sig.FormatId.ToLowerInvariant()}",
            profile,
            preprocess: "byteshuffle",
            codec: codec,
            level: level,
            dictSizeKb: dictKb,
            method: method,
            solid: codec == "7z" ? false : null,
            strideParam: shuffleStride);

        var effectiveProbe = Math.Min(s.MicroProbeRatio, s.PreprocessedMicroProbeRatio ?? s.MicroProbeRatio);
        var predictedRatio = Math.Clamp(effectiveProbe * 0.78, 0.05, 0.85);
        var predictedBytes = (long)Math.Round(inspection.SizeBytes * predictedRatio);

        var codecDisplay = codec == "7z" ? $"LZMA2-{level} ({dictKb / 1024} MiB dict)" : $"Zstd-{level} ({dictKb / 1024} MiB long-window)";
        var strategyName = $"ByteShuffle/Delta + {codecDisplay}";

        var strideDetail = Math.Max(s.Stride4EntropyReduction, s.Stride8EntropyReduction) >= 0.05
            ? $"stride-4/8 delta entropy reduction (-{Math.Max(s.Stride4EntropyReduction, s.Stride8EntropyReduction):F2} bpb)"
            : $"recurring {sig.FormatId} schema/page structure ({sig.StructuralStrideBytes ?? 4096}B alignment)";

        var rationale =
            $"Detected {sig.DisplayName} ({sig.FormatId}) with {strideDetail}, entropy {s.MeanEntropyBitsPerByte:F2} bpb, " +
            $"and preprocessed 32 KiB probe ratio {effectiveProbe:F3}. Selected reversible ByteShuffle/Delta preprocessing + {codecDisplay} " +
            "to align numeric keys/page pointers and capture cross-page table redundancy.";

        return new StrategyDecisionReport
        {
            TargetPath = targetPath,
            IsDirectory = false,
            Profile = profile,
            DetectedFormat = sig.FormatId,
            DetectedMimeType = sig.MimeType,
            ContentClass = ContentClass.DatabaseAndTabular,
            WorkloadArchetype = WorkloadArchetype.DatabaseDump,
            TotalFiles = 1,
            TotalBytes = inspection.SizeBytes,
            SampledBytes = inspection.SampledBytes,
            PeakInspectionBufferBytes = inspection.PeakBufferBytes,
            InspectionDurationMs = inspection.InspectionDurationMs,
            Signals = s,
            WindowSamples = inspection.Windows,
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "structured-byteshuffle-delta-longwindow",
                StrategyName = strategyName,
                Preprocessors = preprocessors,
                PrimaryCodec = codec,
                CodecLevel = level,
                Method = method,
                DictionaryOrWindowSizeKb = dictKb,
                SolidBlockGrouping = false,
                DeduplicateExactFiles = false,
                RationaleSummary = rationale,
                ExecutableRecipe = recipe,
            },
            Routes = new[]
            {
                new WorkloadRoutePlan
                {
                    RouteId = "database-structured-delta",
                    RouteName = $"Structured Database / Tabular ({sig.FormatId})",
                    ContentClass = ContentClass.DatabaseAndTabular,
                    Formats = new[] { sig.FormatId },
                    FileCount = 1,
                    InputBytes = inspection.SizeBytes,
                    ShareOfBytesPercent = 100.0,
                    Preprocessors = preprocessors,
                    Codec = codec,
                    CodecLevel = level,
                    PipelineSummary = strategyName,
                    PredictedRatio = predictedRatio,
                    PredictedCompressedBytes = predictedBytes,
                    SpeedTier = speedTier,
                    Rationale = rationale,
                },
            },
            RejectedAlternatives = new[]
            {
                new RejectedStrategyAlternative
                {
                    StrategyName = "GZip / Deflate (32 KiB window, no preprocessor)",
                    ReasonCode = "WINDOW_TOO_SMALL_FOR_DB_PAGES",
                    RejectionReason = "32 KiB sliding window cannot reach across 4 KiB–64 KiB B-tree pages or distant row batches, and raw byte order scatters multi-byte keys.",
                    EstimatedPenalty = "25%–45% larger output than ByteShuffle/Delta + long-window coding",
                },
                new RejectedStrategyAlternative
                {
                    StrategyName = "Plain Zstd Without ByteShuffle/Delta",
                    ReasonCode = "UNALIGNED_STRIDE_LANES",
                    RejectionReason = $"Omits lossless stride/lane conditioning ({strideDetail}), leaving multi-byte integers and pointers interleaved.",
                    EstimatedPenalty = "12%–30% worse compression ratio on tabular/page records",
                },
                new RejectedStrategyAlternative
                {
                    StrategyName = "Store / FastStreamBypass",
                    ReasonCode = "HIGH_STRUCTURAL_REDUNDANCY",
                    RejectionReason = $"{sig.FormatId} exhibits {(1.0 - predictedRatio) * 100.0:F0}% predicted lossless savings.",
                    EstimatedPenalty = $"Leaves ~{FormatBytes(inspection.SizeBytes - predictedBytes)} of lossless space savings unrealized",
                },
            },
            PredictedRatio = predictedRatio,
            PredictedSavingsPercent = (1.0 - predictedRatio) * 100.0,
            PredictedCompressedBytes = predictedBytes,
            PredictedSpeedTier = speedTier,
            EstimatedThroughputRange = throughput,
        };
    }

    private static StrategyDecisionReport BuildDatabaseDirectoryReport(
        string workloadPath,
        IReadOnlyList<ContentInspectionResult> items,
        CompressionProfile profile,
        long totalBytes,
        long sampledBytes,
        int peakBufferBytes,
        double durationMs,
        ContentInspectionSignals signals,
        IReadOnlyList<InspectionWindowSample> windows,
        int duplicateFiles,
        long duplicateBytes)
    {
        var formats = items.Select(i => i.Signature.FormatId).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f).ToList();
        var (codec, level, method, dictKb, speedTier, throughput) = PickLongWindowBackend(profile, totalBytes);
        var codecDisplay = codec == "7z" ? $"LZMA2-{level}" : $"Zstd-{level} ({dictKb / 1024} MiB window)";
        var strategyName = $"ByteShuffle/Delta + {codecDisplay} + SHA256Dedup";
        var preprocessors = new[] { "sha256_exact_dedup", "byteshuffle(8B)", "delta(8B)" };

        var uniqueBytes = Math.Max(0L, totalBytes - duplicateBytes);
        var effectiveProbe = Math.Min(signals.MicroProbeRatio, signals.PreprocessedMicroProbeRatio ?? signals.MicroProbeRatio);
        var uniqueRatio = Math.Clamp(effectiveProbe * 0.76, 0.05, 0.85);
        var predictedBytes = (long)Math.Round(uniqueBytes * uniqueRatio);
        var predictedRatio = totalBytes > 0 ? (double)predictedBytes / totalBytes : 1.0;

        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-db-workload",
            profile,
            preprocess: "byteshuffle",
            codec: codec,
            level: level,
            dictSizeKb: dictKb,
            method: method,
            solid: true,
            strideParam: 8);

        var rationale =
            $"Database/tabular workload ({items.Count} files, formats: {string.Join("/", formats)}) with stride correlation " +
            $"(-{signals.BestStrideEntropyReduction:F2} bpb) and {signals.MeanEntropyBitsPerByte:F2} bpb entropy. " +
            $"Selected {strategyName} to exploit page/columnar deltas across partitions.";

        return new StrategyDecisionReport
        {
            TargetPath = workloadPath,
            IsDirectory = true,
            Profile = profile,
            DetectedFormat = string.Join("+", formats),
            DetectedMimeType = "multipart/x-database-workload",
            ContentClass = ContentClass.DatabaseAndTabular,
            WorkloadArchetype = WorkloadArchetype.DatabaseDump,
            TotalFiles = items.Count,
            TotalBytes = totalBytes,
            SampledBytes = sampledBytes,
            PeakInspectionBufferBytes = peakBufferBytes,
            InspectionDurationMs = durationMs,
            Signals = signals,
            WindowSamples = windows,
            DuplicateFileCount = duplicateFiles,
            DuplicateReclaimableBytes = duplicateBytes,
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "structured-byteshuffle-delta-longwindow",
                StrategyName = strategyName,
                Preprocessors = preprocessors,
                PrimaryCodec = codec,
                CodecLevel = level,
                Method = method,
                DictionaryOrWindowSizeKb = dictKb,
                SolidBlockGrouping = true,
                DeduplicateExactFiles = true,
                RationaleSummary = rationale,
                ExecutableRecipe = recipe,
            },
            Routes = new[]
            {
                new WorkloadRoutePlan
                {
                    RouteId = "db-partitions-delta",
                    RouteName = "Database & Tabular Partitions",
                    ContentClass = ContentClass.DatabaseAndTabular,
                    Formats = formats,
                    FileCount = items.Count,
                    InputBytes = totalBytes,
                    ShareOfBytesPercent = 100.0,
                    Preprocessors = preprocessors,
                    Codec = codec,
                    CodecLevel = level,
                    PipelineSummary = strategyName,
                    PredictedRatio = predictedRatio,
                    PredictedCompressedBytes = predictedBytes,
                    SpeedTier = speedTier,
                    Rationale = rationale,
                },
            },
            RejectedAlternatives = new[]
            {
                new RejectedStrategyAlternative
                {
                    StrategyName = "Per-File GZip / Deflate (32 KiB window)",
                    ReasonCode = "NO_STRIDE_OR_CROSS_PAGE_WINDOW",
                    RejectionReason = "Short 32 KiB window and unshuffled numeric lanes miss cross-page and cross-shard schema repetition.",
                    EstimatedPenalty = "30%–50% larger archive",
                },
                new RejectedStrategyAlternative
                {
                    StrategyName = "Store / FastStreamBypass",
                    ReasonCode = "HIGH_TABULAR_COMPRESSIBILITY",
                    RejectionReason = "Structured database shards compress strongly with lossless ByteShuffle/Delta.",
                    EstimatedPenalty = $"Forfeits {(1.0 - predictedRatio) * 100.0:F0}% lossless size reduction",
                },
            },
            PredictedRatio = predictedRatio,
            PredictedSavingsPercent = (1.0 - predictedRatio) * 100.0,
            PredictedCompressedBytes = predictedBytes,
            PredictedSpeedTier = speedTier,
            EstimatedThroughputRange = throughput,
        };
    }

    private static StrategyDecisionReport BuildUncompressedRasterOrAudioReport(
        ContentInspectionResult inspection,
        string targetPath,
        CompressionProfile profile)
    {
        var s = inspection.Signals;
        var sig = inspection.Signature;

        var rawStride = sig.StructuralStrideBytes is >= 1 and <= 8
            ? sig.StructuralStrideBytes.Value
            : (s.BestStrideBytes > 0 ? s.BestStrideBytes : 4);
        var supportedDeltaStride = NormalizeSupportedDeltaStride(rawStride);

        var (codec, level, method, dictKb, speedTier, throughput) = PickBalancedOrArchivalBackend(profile, inspection.SizeBytes);
        var codecLabel = codec == "7z" ? $"LZMA2-{level}" : $"Zstd-{level}";
        var strategyName = $"Delta({rawStride}B) + {codecLabel}";
        var preprocessors = new[] { $"delta({supportedDeltaStride}B)" };

        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-raw-{sig.FormatId.ToLowerInvariant()}",
            profile,
            preprocess: "delta",
            codec: codec,
            level: level,
            dictSizeKb: dictKb,
            method: method,
            solid: codec == "7z" ? false : null,
            strideParam: supportedDeltaStride);

        var effectiveProbe = Math.Min(s.MicroProbeRatio, s.PreprocessedMicroProbeRatio ?? s.MicroProbeRatio);
        var predictedRatio = Math.Clamp(effectiveProbe * 0.88, 0.12, 0.78);
        var predictedBytes = (long)Math.Round(inspection.SizeBytes * predictedRatio);

        var rationale =
            $"Detected uncompressed {sig.DisplayName} ({sig.FormatId}) with {rawStride}-byte channel/sample stride " +
            $"(-{s.BestStrideEntropyReduction:F2} bpb delta entropy reduction, probe {s.MicroProbeRatio:F2} -> {effectiveProbe:F2}). " +
            $"Selected lossless {strategyName} for bit-exact pixel/sample restoration.";

        return new StrategyDecisionReport
        {
            TargetPath = targetPath,
            IsDirectory = false,
            Profile = profile,
            DetectedFormat = sig.FormatId,
            DetectedMimeType = sig.MimeType,
            ContentClass = sig.ContentClass,
            WorkloadArchetype = WorkloadArchetype.UncompressedMediaOrRaster,
            TotalFiles = 1,
            TotalBytes = inspection.SizeBytes,
            SampledBytes = inspection.SampledBytes,
            PeakInspectionBufferBytes = inspection.PeakBufferBytes,
            InspectionDurationMs = inspection.InspectionDurationMs,
            Signals = s,
            WindowSamples = inspection.Windows,
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "uncompressed-delta-zstd",
                StrategyName = strategyName,
                Preprocessors = preprocessors,
                PrimaryCodec = codec,
                CodecLevel = level,
                Method = method,
                DictionaryOrWindowSizeKb = dictKb,
                RationaleSummary = rationale,
                ExecutableRecipe = recipe,
            },
            Routes = new[]
            {
                new WorkloadRoutePlan
                {
                    RouteId = "uncompressed-raster-delta",
                    RouteName = $"Uncompressed {sig.FormatId}",
                    ContentClass = sig.ContentClass,
                    Formats = new[] { sig.FormatId },
                    FileCount = 1,
                    InputBytes = inspection.SizeBytes,
                    ShareOfBytesPercent = 100.0,
                    Preprocessors = preprocessors,
                    Codec = codec,
                    CodecLevel = level,
                    PipelineSummary = strategyName,
                    PredictedRatio = predictedRatio,
                    PredictedCompressedBytes = predictedBytes,
                    SpeedTier = speedTier,
                    Rationale = rationale,
                },
            },
            RejectedAlternatives = new[]
            {
                new RejectedStrategyAlternative
                {
                    StrategyName = "Store / FastStreamBypass",
                    ReasonCode = "UNCOMPRESSED_SPATIAL_REDUNDANCY",
                    RejectionReason = $"Uncompressed {sig.FormatId} samples compress by ~{(1.0 - predictedRatio) * 100.0:F0}% losslessly once {rawStride}-byte channel deltas are applied.",
                    EstimatedPenalty = $"Wastes ~{FormatBytes(inspection.SizeBytes - predictedBytes)} of disk space",
                },
                new RejectedStrategyAlternative
                {
                    StrategyName = "Raw Zstd Without Stride Delta",
                    ReasonCode = "ADJACENT_PIXEL_INTERLEAVING",
                    RejectionReason = $"Adjacent RGB/RGBA/Bayer/PCM samples correlate at {rawStride}-byte stride; raw LZ77 misses smooth spatial gradients.",
                    EstimatedPenalty = "15%–35% larger compressed size",
                },
                new RejectedStrategyAlternative
                {
                    StrategyName = "Silent Lossy JPEG/WebP/AAC Conversion",
                    ReasonCode = "LOSSLESS_GUARANTEE_STRICT",
                    RejectionReason = "Forbidden in OperationKind.LosslessCompression; master sensor/PCM bit-depth must restore bit-for-bit.",
                    EstimatedPenalty = "Permanent sensor/audio dynamic-range loss without consent",
                },
            },
            PredictedRatio = predictedRatio,
            PredictedSavingsPercent = (1.0 - predictedRatio) * 100.0,
            PredictedCompressedBytes = predictedBytes,
            PredictedSpeedTier = speedTier,
            EstimatedThroughputRange = throughput,
            ConsentGatedAdvisoryNote = sig.HasConsentGatedOptimizationCandidate
                ? $"Non-executed advisory: {sig.FormatId} is compressed 100% losslessly via {strategyName}. Optional format conversion (e.g. lossless PNG/FLAC or lossy AVIF/WebP) belongs strictly to consent-gated OperationKind.FormatConversion / OperationKind.LossyOptimization."
                : null,
        };
    }

    private static StrategyDecisionReport BuildExecutableBinaryReport(
        ContentInspectionResult inspection,
        string targetPath,
        CompressionProfile profile)
    {
        var s = inspection.Signals;
        var sig = inspection.Signature;
        var (codec, level, method, dictKb, speedTier, throughput) = PickBalancedOrArchivalBackend(profile, inspection.SizeBytes);

        var preprocessors = new[] { "x86_bcj" };
        var codecLabel = codec == "7z" ? $"LZMA2-{level}" : $"Zstd-{level}";
        var strategyName = $"X86Bcj + {codecLabel}";

        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-bin-{sig.FormatId.ToLowerInvariant()}",
            profile,
            preprocess: "x86_bcj",
            codec: codec,
            level: level,
            dictSizeKb: dictKb,
            method: method,
            solid: codec == "7z" ? false : null);

        var predictedRatio = Math.Clamp(s.MicroProbeRatio * 0.82, 0.18, 0.78);
        var predictedBytes = (long)Math.Round(inspection.SizeBytes * predictedRatio);

        var rationale =
            $"Detected {sig.DisplayName} ({sig.FormatId}, entropy {s.MeanEntropyBitsPerByte:F2} bpb). " +
            $"Selected reversible X86Bcj relative-jump target filter + {codecLabel} without touching debug symbols or signatures.";

        return new StrategyDecisionReport
        {
            TargetPath = targetPath,
            IsDirectory = false,
            Profile = profile,
            DetectedFormat = sig.FormatId,
            DetectedMimeType = sig.MimeType,
            ContentClass = ContentClass.ExecutableBinary,
            WorkloadArchetype = WorkloadArchetype.ExecutableBinaries,
            TotalFiles = 1,
            TotalBytes = inspection.SizeBytes,
            SampledBytes = inspection.SampledBytes,
            PeakInspectionBufferBytes = inspection.PeakBufferBytes,
            InspectionDurationMs = inspection.InspectionDurationMs,
            Signals = s,
            WindowSamples = inspection.Windows,
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "binary-bcj-longwindow",
                StrategyName = strategyName,
                Preprocessors = preprocessors,
                PrimaryCodec = codec,
                CodecLevel = level,
                Method = method,
                DictionaryOrWindowSizeKb = dictKb,
                RationaleSummary = rationale,
                ExecutableRecipe = recipe,
            },
            Routes = new[]
            {
                new WorkloadRoutePlan
                {
                    RouteId = "executable-bcj",
                    RouteName = $"Executable Binary ({sig.FormatId})",
                    ContentClass = ContentClass.ExecutableBinary,
                    Formats = new[] { sig.FormatId },
                    FileCount = 1,
                    InputBytes = inspection.SizeBytes,
                    ShareOfBytesPercent = 100.0,
                    Preprocessors = preprocessors,
                    Codec = codec,
                    CodecLevel = level,
                    PipelineSummary = strategyName,
                    PredictedRatio = predictedRatio,
                    PredictedCompressedBytes = predictedBytes,
                    SpeedTier = speedTier,
                    Rationale = rationale,
                },
            },
            RejectedAlternatives = new[]
            {
                new RejectedStrategyAlternative
                {
                    StrategyName = "Debug Symbol / Section Stripping",
                    ReasonCode = "DESTRUCTIVE_MUTATION_BANNED",
                    RejectionReason = "Stripping PE/ELF debug directories invalidates Authenticode signatures and SHA-256 bit-exactness.",
                    EstimatedPenalty = "Requires explicit OperationKind.DataRemoval consent",
                },
                new RejectedStrategyAlternative
                {
                    StrategyName = "Raw Deflate Without BCJ Filter",
                    ReasonCode = "UNFILTERED_REL32_OFFSETS",
                    RejectionReason = "Relative E8/E9 CALL/JMP offsets drift with instruction address, hiding identical call targets from LZ77.",
                    EstimatedPenalty = "8%–18% larger binary archive",
                },
            },
            PredictedRatio = predictedRatio,
            PredictedSavingsPercent = (1.0 - predictedRatio) * 100.0,
            PredictedCompressedBytes = predictedBytes,
            PredictedSpeedTier = speedTier,
            EstimatedThroughputRange = throughput,
            ConsentGatedAdvisoryNote =
                "Non-executed advisory: Binary sections and Authenticode/debug tables are preserved bit-for-bit. " +
                "Symbol stripping is isolated to consent-gated OperationKind.DataRemoval.",
        };
    }

    private static StrategyDecisionReport BuildSourceOrTextSingleFileReport(
        ContentInspectionResult inspection,
        string targetPath,
        CompressionProfile profile)
    {
        var s = inspection.Signals;
        var sig = inspection.Signature;
        var (codec, level, method, dictKb, speedTier, throughput) = PickLongWindowBackend(profile, inspection.SizeBytes);

        var isUtf16 = sig.FormatId is "UTF-16LE" or "UTF-16BE";
        var preprocessors = isUtf16
            ? new[] { "delta(2B)", "long_window_token_match" }
            : new[] { "long_window_token_match" };

        var codecLabel = codec == "7z" ? $"LZMA2-{level}" : $"Zstd-{level} ({dictKb / 1024} MiB window)";
        var strategyName = isUtf16 ? $"Delta(2B) + {codecLabel}" : $"LongWindow {codecLabel}";

        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-text-{sig.FormatId.ToLowerInvariant()}",
            profile,
            preprocess: isUtf16 ? "delta" : "raw",
            codec: codec,
            level: level,
            dictSizeKb: dictKb,
            method: method,
            solid: codec == "7z" ? false : null,
            strideParam: isUtf16 ? 2 : null);

        var effectiveProbe = Math.Min(s.MicroProbeRatio, s.PreprocessedMicroProbeRatio ?? s.MicroProbeRatio);
        var predictedRatio = Math.Clamp(effectiveProbe * 0.80, 0.06, 0.65);
        var predictedBytes = (long)Math.Round(inspection.SizeBytes * predictedRatio);

        var rationale =
            $"Detected {sig.DisplayName} ({sig.FormatId}) with low entropy ({s.MeanEntropyBitsPerByte:F2} bpb), " +
            $"high bigram predictability ({s.BigramPredictability:P0}), and 32 KiB probe ratio {effectiveProbe:F3}. " +
            $"Selected {strategyName} for high lossless lexical compression.";

        return new StrategyDecisionReport
        {
            TargetPath = targetPath,
            IsDirectory = false,
            Profile = profile,
            DetectedFormat = sig.FormatId,
            DetectedMimeType = sig.MimeType,
            ContentClass = ContentClass.SourceAndText,
            WorkloadArchetype = sig.FormatId == "SourceCode" ? WorkloadArchetype.SourceRepository : WorkloadArchetype.GeneralFile,
            TotalFiles = 1,
            TotalBytes = inspection.SizeBytes,
            SampledBytes = inspection.SampledBytes,
            PeakInspectionBufferBytes = inspection.PeakBufferBytes,
            InspectionDurationMs = inspection.InspectionDurationMs,
            Signals = s,
            WindowSamples = inspection.Windows,
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "text-longwindow-lossless",
                StrategyName = strategyName,
                Preprocessors = preprocessors,
                PrimaryCodec = codec,
                CodecLevel = level,
                Method = method,
                DictionaryOrWindowSizeKb = dictKb,
                RationaleSummary = rationale,
                ExecutableRecipe = recipe,
            },
            Routes = new[]
            {
                new WorkloadRoutePlan
                {
                    RouteId = "source-text-longwindow",
                    RouteName = $"Source / Text ({sig.FormatId})",
                    ContentClass = ContentClass.SourceAndText,
                    Formats = new[] { sig.FormatId },
                    FileCount = 1,
                    InputBytes = inspection.SizeBytes,
                    ShareOfBytesPercent = 100.0,
                    Preprocessors = preprocessors,
                    Codec = codec,
                    CodecLevel = level,
                    PipelineSummary = strategyName,
                    PredictedRatio = predictedRatio,
                    PredictedCompressedBytes = predictedBytes,
                    SpeedTier = speedTier,
                    Rationale = rationale,
                },
            },
            RejectedAlternatives = new[]
            {
                new RejectedStrategyAlternative
                {
                    StrategyName = "Store / FastStreamBypass",
                    ReasonCode = "HIGHLY_COMPRESSIBLE_TEXT",
                    RejectionReason = $"Low entropy ({s.MeanEntropyBitsPerByte:F2} bpb) text achieves ~{(1.0 - predictedRatio) * 100.0:F0}% lossless reduction.",
                    EstimatedPenalty = $"Forfeits ~{FormatBytes(inspection.SizeBytes - predictedBytes)} of savings",
                },
                new RejectedStrategyAlternative
                {
                    StrategyName = "Deflate (32 KiB window)",
                    ReasonCode = "SHORT_HISTORY_WINDOW",
                    RejectionReason = "32 KiB window forgets recurring symbols, headers, and boilerplate beyond a few hundred lines.",
                    EstimatedPenalty = "18%–35% larger output than long-window Zstd/LZMA2",
                },
            },
            PredictedRatio = predictedRatio,
            PredictedSavingsPercent = (1.0 - predictedRatio) * 100.0,
            PredictedCompressedBytes = predictedBytes,
            PredictedSpeedTier = speedTier,
            EstimatedThroughputRange = throughput,
        };
    }

    private static StrategyDecisionReport BuildSourceRepositoryReport(
        string workloadPath,
        IReadOnlyList<ContentInspectionResult> items,
        IReadOnlyList<(long Size, string Hash)> resolvedKeys,
        CompressionProfile profile,
        long totalBytes,
        long sampledBytes,
        int peakBufferBytes,
        double durationMs,
        ContentInspectionSignals signals,
        IReadOnlyList<InspectionWindowSample> windows,
        int duplicateFiles,
        long duplicateBytes)
    {
        var (codec, level, method, dictKb, speedTier, throughput) = PickLongWindowBackend(profile, totalBytes);
        var codecLabel = codec == "7z"
            ? $"LongWindowLZMA2-{level} ({dictKb / 1024} MiB solid dict)"
            : $"LongWindowZstd-{level} ({dictKb / 1024} MiB solid dict)";
        var strategyName = $"TypeGroupedSolid + {codecLabel} + SHA256Dedup";
        var preprocessors = new[] { "sha256_exact_dedup", "type_grouped_solid_order", "trained_token_dictionary" };

        var routes = BuildMultiClassRoutes(items, resolvedKeys, totalBytes, profile, forceSolidSource: true);
        var predictedBytes = routes.Sum(r => r.PredictedCompressedBytes);
        var predictedRatio = totalBytes > 0 ? Math.Clamp((double)predictedBytes / totalBytes, 0.04, 1.0) : 1.0;

        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-source-repo-solid",
            profile,
            preprocess: "raw",
            codec: codec,
            level: level,
            dictSizeKb: dictKb,
            method: method,
            solid: true);

        var dedupNote = duplicateFiles > 0
            ? $" plus {duplicateFiles} exact duplicate file(s) ({FormatBytes(duplicateBytes)} instant SHA-256 dedup)"
            : " plus content-addressed SHA-256 duplicate elimination";

        var rationale =
            $"Detected Source Repository ({items.Count} files, {signals.MeanEntropyBitsPerByte:F2} bpb mean entropy, " +
            $"{signals.BigramPredictability:P0} bigram predictability{dedupNote}). " +
            $"Selected {strategyName} to sort files by extension/syntax into shared solid dictionary blocks while bypassing any embedded binary/media blobs.";

        return new StrategyDecisionReport
        {
            TargetPath = workloadPath,
            IsDirectory = true,
            Profile = profile,
            DetectedFormat = "SourceRepository",
            DetectedMimeType = "multipart/x-source-repository",
            ContentClass = ContentClass.SourceAndText,
            WorkloadArchetype = WorkloadArchetype.SourceRepository,
            TotalFiles = items.Count,
            TotalBytes = totalBytes,
            SampledBytes = sampledBytes,
            PeakInspectionBufferBytes = peakBufferBytes,
            InspectionDurationMs = durationMs,
            Signals = signals,
            WindowSamples = windows,
            DuplicateFileCount = duplicateFiles,
            DuplicateReclaimableBytes = duplicateBytes,
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "type-grouped-solid-longwindow-dedup",
                StrategyName = strategyName,
                Preprocessors = preprocessors,
                PrimaryCodec = codec,
                CodecLevel = level,
                Method = method,
                DictionaryOrWindowSizeKb = dictKb,
                SolidBlockGrouping = true,
                DeduplicateExactFiles = true,
                RationaleSummary = rationale,
                ExecutableRecipe = recipe,
            },
            Routes = routes,
            RejectedAlternatives = new[]
            {
                new RejectedStrategyAlternative
                {
                    StrategyName = "Per-File Independent ZIP / Deflate",
                    ReasonCode = "LOST_CROSS_FILE_DICTIONARY_AND_DEDUP",
                    RejectionReason = "Resets the 32 KiB LZ77 dictionary at every file boundary, losing shared imports, AST patterns, license headers, and vendor/lockfile duplicates.",
                    EstimatedPenalty = "35%–60% larger archive on multi-file source trees",
                },
                new RejectedStrategyAlternative
                {
                    StrategyName = "Unsorted Monolithic Solid LZMA2",
                    ReasonCode = "POOR_LOCALITY_AND_SLOW_SELECTIVE_EXTRACT",
                    RejectionReason = "Interleaving unrelated binary assets between source files pollutes dictionary state and forces full-archive decompression to extract one file.",
                    EstimatedPenalty = "10%–20% worse ratio + slow random file extraction",
                },
                new RejectedStrategyAlternative
                {
                    StrategyName = "Store / FastStreamBypass",
                    ReasonCode = "HIGH_LEXICAL_REDUNDANCY",
                    RejectionReason = $"Source repository achieves {(1.0 - predictedRatio) * 100.0:F0}% bit-exact lossless compression.",
                    EstimatedPenalty = $"Wastes ~{FormatBytes(totalBytes - predictedBytes)} of storage",
                },
            },
            PredictedRatio = predictedRatio,
            PredictedSavingsPercent = (1.0 - predictedRatio) * 100.0,
            PredictedCompressedBytes = predictedBytes,
            PredictedSpeedTier = speedTier,
            EstimatedThroughputRange = throughput,
        };
    }

    private static StrategyDecisionReport BuildHeterogeneousPhotoCollectionReport(
        string workloadPath,
        IReadOnlyList<ContentInspectionResult> items,
        IReadOnlyList<(long Size, string Hash)> resolvedKeys,
        CompressionProfile profile,
        long totalBytes,
        long sampledBytes,
        int peakBufferBytes,
        double durationMs,
        ContentInspectionSignals signals,
        IReadOnlyList<InspectionWindowSample> windows,
        int duplicateFiles,
        long duplicateBytes,
        int consentCandidateFiles)
    {
        var (rawCodec, rawLevel, rawMethod, rawDictKb, rawSpeedTier, _) = PickBalancedOrArchivalBackend(profile, totalBytes);
        var rawCodecLabel = rawCodec == "7z" ? $"LZMA2-{rawLevel}" : $"Zstd-{rawLevel}";

        var routes = BuildMultiClassRoutes(items, resolvedKeys, totalBytes, profile, forceSolidSource: false);
        var predictedBytes = routes.Sum(r => r.PredictedCompressedBytes);
        var predictedRatio = totalBytes > 0 ? Math.Clamp((double)predictedBytes / totalBytes, 0.05, 1.0) : 1.0;

        var compressedPhotoCount = items.Count(i => i.Signature.ContentClass == ContentClass.CompressedImage);
        var rawPhotoCount = items.Count(i => i.Signature.ContentClass == ContentClass.UncompressedRasterImage);

        var strategyName = $"HeterogeneousSplit(JPEG/HEIC->Store, BMP/TIFF/RAW->Delta+{rawCodecLabel}, SHA256Dedup)";
        var preprocessors = new[] { "per_file_signature_router", "sha256_exact_dedup", "delta(4B)" };

        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-photo-heterogeneous-split",
            profile,
            preprocess: "delta",
            codec: rawCodec,
            level: rawLevel,
            dictSizeKb: rawDictKb,
            method: rawMethod,
            solid: false,
            strideParam: 4);

        var rationale =
            $"Detected heterogeneous Photo Collection ({items.Count} files): split routing sends {compressedPhotoCount} already-compressed " +
            $"JPEG/HEIC photo(s) (~7.95 bpb) to Store / FastStreamBypass at disk speed while routing {rawPhotoCount} uncompressed " +
            $"BMP/TIFF/DNG/RAW raster(s) through lossless Delta + {rawCodecLabel}, plus SHA-256 exact duplicate dedup ({duplicateFiles} duplicate(s)). " +
            "Zero silent lossy recompression or EXIF stripping is ever applied.";

        var blendedTier = rawSpeedTier == SpeedTier.HeavyMaxRatio ? SpeedTier.DeepArchival : SpeedTier.Balanced;

        return new StrategyDecisionReport
        {
            TargetPath = workloadPath,
            IsDirectory = true,
            Profile = profile,
            DetectedFormat = "PhotoCollection(JPEG/HEIC+RAW/TIFF/BMP)",
            DetectedMimeType = "multipart/x-photo-collection",
            ContentClass = ContentClass.HeterogeneousWorkload,
            WorkloadArchetype = WorkloadArchetype.HeterogeneousPhotoCollection,
            TotalFiles = items.Count,
            TotalBytes = totalBytes,
            SampledBytes = sampledBytes,
            PeakInspectionBufferBytes = peakBufferBytes,
            InspectionDurationMs = durationMs,
            Signals = signals,
            WindowSamples = windows,
            DuplicateFileCount = duplicateFiles,
            DuplicateReclaimableBytes = duplicateBytes,
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "heterogeneous-photo-split",
                StrategyName = strategyName,
                Preprocessors = preprocessors,
                PrimaryCodec = rawCodec,
                CodecLevel = rawLevel,
                Method = rawMethod,
                DictionaryOrWindowSizeKb = rawDictKb,
                SolidBlockGrouping = false,
                DeduplicateExactFiles = true,
                RationaleSummary = rationale,
                ExecutableRecipe = recipe,
            },
            Routes = routes,
            RejectedAlternatives = new[]
            {
                new RejectedStrategyAlternative
                {
                    StrategyName = "Uniform LZMA2-9 / Zstd-19 Across Whole Folder",
                    ReasonCode = "WASTED_CPU_ON_JPEG_HEIC",
                    RejectionReason = "Grinds heavy dictionary compression over high-entropy JPEG/HEIC bitstreams (<0.3% gain) instead of bypassing them at disk speed.",
                    EstimatedPenalty = "10x–50x slower total job time for <0.3% gain on JPEG/HEIC portion",
                },
                new RejectedStrategyAlternative
                {
                    StrategyName = "Uniform Store Across Whole Folder",
                    ReasonCode = "MISSED_RAW_TIFF_BMP_SAVINGS",
                    RejectionReason = "Leaves uncompressed BMP, TIFF, DNG, and Camera RAW rasters and burst duplicates completely uncompressed.",
                    EstimatedPenalty = $"Forfeits ~{FormatBytes(totalBytes - predictedBytes)} of bit-exact lossless savings on RAW/TIFF/BMP",
                },
                new RejectedStrategyAlternative
                {
                    StrategyName = "Silent JPEG->AVIF/WebP Re-Encode or EXIF Strip",
                    ReasonCode = "SILENT_LOSSY_AND_DATA_REMOVAL_FORBIDDEN",
                    RejectionReason = "Violates OperationKind.LosslessCompression; re-encoding alters pixel values and stripping EXIF deletes camera/GPS provenance.",
                    EstimatedPenalty = "Irreversible generational quality loss and metadata destruction without user consent",
                },
            },
            PredictedRatio = predictedRatio,
            PredictedSavingsPercent = (1.0 - predictedRatio) * 100.0,
            PredictedCompressedBytes = predictedBytes,
            PredictedSpeedTier = blendedTier,
            EstimatedThroughputRange = "450–1,800 MB/s blended (instant Store on JPEG/HEIC + parallel Delta+Zstd on RAW/TIFF/BMP)",
            ConsentGatedAdvisoryNote =
                $"Non-executed advisory: {Math.Max(1, consentCandidateFiles)} photo file(s) contain camera EXIF/IPTC metadata or lossy transcode candidates. " +
                "LosslessCompression preserves 100% of original bytes and EXIF tags untouched. Optional EXIF removal (OperationKind.DataRemoval) or " +
                "AVIF/WebP lossy optimization (OperationKind.LossyOptimization) requires explicit user consent in Optimization Studio with original preservation.",
        };
    }

    private static StrategyDecisionReport BuildVideoDirectoryBypassReport(
        string workloadPath,
        IReadOnlyList<ContentInspectionResult> items,
        IReadOnlyList<(long Size, string Hash)> resolvedKeys,
        CompressionProfile profile,
        long totalBytes,
        long sampledBytes,
        int peakBufferBytes,
        double durationMs,
        ContentInspectionSignals signals,
        IReadOnlyList<InspectionWindowSample> windows,
        int duplicateFiles,
        long duplicateBytes)
    {
        var routes = BuildMultiClassRoutes(items, resolvedKeys, totalBytes, profile, forceSolidSource: false);
        var predictedBytes = routes.Sum(r => r.PredictedCompressedBytes);
        var predictedRatio = totalBytes > 0 ? (double)predictedBytes / totalBytes : 1.0;

        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-video-dir-store",
            profile,
            preprocess: "raw",
            codec: "store",
            level: 0);

        var rationale =
            $"Video media folder ({items.Count} files, {signals.InteriorEntropyBitsPerByte:F2} bpb interior entropy). " +
            "Selected Store / FastStreamBypass + SHA-256 duplicate dedup (<0.2% codec gain on compressed video streams).";

        return new StrategyDecisionReport
        {
            TargetPath = workloadPath,
            IsDirectory = true,
            Profile = profile,
            DetectedFormat = "VideoCollection",
            DetectedMimeType = "multipart/x-video-collection",
            ContentClass = ContentClass.CompressedVideo,
            WorkloadArchetype = WorkloadArchetype.CompressedVideoStream,
            TotalFiles = items.Count,
            TotalBytes = totalBytes,
            SampledBytes = sampledBytes,
            PeakInspectionBufferBytes = peakBufferBytes,
            InspectionDurationMs = durationMs,
            Signals = signals,
            WindowSamples = windows,
            DuplicateFileCount = duplicateFiles,
            DuplicateReclaimableBytes = duplicateBytes,
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "store-fast-stream-bypass",
                StrategyName = "Store / FastStreamBypass + SHA256Dedup",
                Preprocessors = new[] { "sha256_exact_dedup", "xxh64_stream_hash" },
                PrimaryCodec = "store",
                CodecLevel = 0,
                DeduplicateExactFiles = true,
                RationaleSummary = rationale,
                ExecutableRecipe = recipe,
            },
            Routes = routes,
            RejectedAlternatives = new[]
            {
                new RejectedStrategyAlternative
                {
                    StrategyName = "Uniform LZMA2-9 / Zstd-19",
                    ReasonCode = "INCOMPRESSIBLE_VIDEO_CPU_WASTE",
                    RejectionReason = "<0.2% expected gain on entropy-coded video containers at massive CPU cost.",
                    EstimatedPenalty = "25x–100x slower archival with <0.2% size change",
                },
            },
            PredictedRatio = predictedRatio,
            PredictedSavingsPercent = (1.0 - predictedRatio) * 100.0,
            PredictedCompressedBytes = predictedBytes,
            PredictedSpeedTier = SpeedTier.DiskSpeedBypass,
            EstimatedThroughputRange = "1,500–4,500 MB/s (disk I/O bound)",
            ConsentGatedAdvisoryNote =
                "Non-executed advisory: Video bitstreams are archived bit-for-bit untouched. Transcoding requires explicit OperationKind.LossyOptimization consent.",
        };
    }

    private static StrategyDecisionReport BuildGeneralHeterogeneousDirectoryReport(
        string workloadPath,
        IReadOnlyList<ContentInspectionResult> items,
        IReadOnlyList<(long Size, string Hash)> resolvedKeys,
        CompressionProfile profile,
        long totalBytes,
        long sampledBytes,
        int peakBufferBytes,
        double durationMs,
        ContentInspectionSignals signals,
        IReadOnlyList<InspectionWindowSample> windows,
        int duplicateFiles,
        long duplicateBytes,
        int consentCandidateFiles)
    {
        var routes = BuildMultiClassRoutes(items, resolvedKeys, totalBytes, profile, forceSolidSource: true);
        var predictedBytes = routes.Sum(r => r.PredictedCompressedBytes);
        var predictedRatio = totalBytes > 0 ? Math.Clamp((double)predictedBytes / totalBytes, 0.05, 1.0) : 1.0;

        var (codec, level, method, dictKb, speedTier, throughput) = PickBalancedOrArchivalBackend(profile, totalBytes);
        var strategyName = $"HeterogeneousClassRouter({routes.Count} routes) + SHA256Dedup";

        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-mixed-router",
            profile,
            preprocess: "raw",
            codec: codec,
            level: level,
            dictSizeKb: dictKb,
            method: method,
            solid: true);

        var rationale =
            $"Mixed directory ({items.Count} files across {routes.Count} content route(s)): routes compressed media/archives to Store bypass " +
            "while applying class-matched solid dictionary, ByteShuffle/Delta, and X86Bcj lossless pipelines to compressible streams.";

        return new StrategyDecisionReport
        {
            TargetPath = workloadPath,
            IsDirectory = true,
            Profile = profile,
            DetectedFormat = "HeterogeneousDirectory",
            DetectedMimeType = "multipart/mixed",
            ContentClass = ContentClass.HeterogeneousWorkload,
            WorkloadArchetype = WorkloadArchetype.MixedDirectory,
            TotalFiles = items.Count,
            TotalBytes = totalBytes,
            SampledBytes = sampledBytes,
            PeakInspectionBufferBytes = peakBufferBytes,
            InspectionDurationMs = durationMs,
            Signals = signals,
            WindowSamples = windows,
            DuplicateFileCount = duplicateFiles,
            DuplicateReclaimableBytes = duplicateBytes,
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "heterogeneous-multi-route",
                StrategyName = strategyName,
                Preprocessors = new[] { "per_file_signature_router", "sha256_exact_dedup" },
                PrimaryCodec = codec,
                CodecLevel = level,
                Method = method,
                DictionaryOrWindowSizeKb = dictKb,
                SolidBlockGrouping = true,
                DeduplicateExactFiles = true,
                RationaleSummary = rationale,
                ExecutableRecipe = recipe,
            },
            Routes = routes,
            RejectedAlternatives = new[]
            {
                new RejectedStrategyAlternative
                {
                    StrategyName = "Single Uniform Codec Across Mixed Folder",
                    ReasonCode = "SUBOPTIMAL_ON_MIXED_CLASSES",
                    RejectionReason = "Either wastes CPU grinding heavy codecs on embedded media/archives or under-compresses structured dumps and source trees.",
                    EstimatedPenalty = "2x–15x worse CPU efficiency per GiB saved",
                },
            },
            PredictedRatio = predictedRatio,
            PredictedSavingsPercent = (1.0 - predictedRatio) * 100.0,
            PredictedCompressedBytes = predictedBytes,
            PredictedSpeedTier = speedTier,
            EstimatedThroughputRange = throughput,
            ConsentGatedAdvisoryNote = consentCandidateFiles > 0
                ? $"Non-executed advisory: {consentCandidateFiles} file(s) carry metadata/media optimization candidates excluded from bit-exact LosslessCompression."
                : null,
        };
    }

    private static StrategyDecisionReport BuildGeneralCompressibleReport(
        ContentInspectionResult inspection,
        string targetPath,
        CompressionProfile profile)
    {
        var s = inspection.Signals;
        var sig = inspection.Signature;

        var (codec, level, method, dictKb, speedTier, throughput) = PickBalancedOrArchivalBackend(profile, inspection.SizeBytes);

        string preprocess = "raw";
        int? strideParam = null;
        IReadOnlyList<string> preList = new[] { "raw" };
        if (s.BestStrideEntropyReduction >= StrideCorrelationMinReduction && s.BestStrideBytes > 0)
        {
            preprocess = "delta";
            strideParam = NormalizeSupportedDeltaStride(s.BestStrideBytes);
            preList = new[] { $"delta({strideParam.Value}B)" };
        }

        var codecLabel = codec == "7z" ? $"LZMA2-{level}" : $"Zstd-{level}";
        var strategyName = preprocess == "raw" ? codecLabel : $"Delta({strideParam}B) + {codecLabel}";

        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-{sig.FormatId.ToLowerInvariant()}",
            profile,
            preprocess: preprocess,
            codec: codec,
            level: level,
            dictSizeKb: dictKb,
            method: method,
            solid: codec == "7z" ? false : null,
            strideParam: strideParam);

        var effectiveProbe = Math.Min(s.MicroProbeRatio, s.PreprocessedMicroProbeRatio ?? s.MicroProbeRatio);
        var predictedRatio = Math.Clamp(effectiveProbe * 0.88, 0.08, 0.96);
        var predictedBytes = (long)Math.Round(inspection.SizeBytes * predictedRatio);

        var rationale =
            $"Detected {sig.DisplayName} ({sig.FormatId}, entropy {s.MeanEntropyBitsPerByte:F2} bpb, probe ratio {effectiveProbe:F3}). " +
            $"Selected lossless {strategyName}.";

        return new StrategyDecisionReport
        {
            TargetPath = targetPath,
            IsDirectory = false,
            Profile = profile,
            DetectedFormat = sig.FormatId,
            DetectedMimeType = sig.MimeType,
            ContentClass = sig.ContentClass,
            WorkloadArchetype = WorkloadArchetype.GeneralFile,
            TotalFiles = 1,
            TotalBytes = inspection.SizeBytes,
            SampledBytes = inspection.SampledBytes,
            PeakInspectionBufferBytes = inspection.PeakBufferBytes,
            InspectionDurationMs = inspection.InspectionDurationMs,
            Signals = s,
            WindowSamples = inspection.Windows,
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "general-lossless-pipeline",
                StrategyName = strategyName,
                Preprocessors = preList,
                PrimaryCodec = codec,
                CodecLevel = level,
                Method = method,
                DictionaryOrWindowSizeKb = dictKb,
                RationaleSummary = rationale,
                ExecutableRecipe = recipe,
            },
            Routes = new[]
            {
                new WorkloadRoutePlan
                {
                    RouteId = "general-compressible",
                    RouteName = sig.DisplayName,
                    ContentClass = sig.ContentClass,
                    Formats = new[] { sig.FormatId },
                    FileCount = 1,
                    InputBytes = inspection.SizeBytes,
                    ShareOfBytesPercent = 100.0,
                    Preprocessors = preList,
                    Codec = codec,
                    CodecLevel = level,
                    PipelineSummary = strategyName,
                    PredictedRatio = predictedRatio,
                    PredictedCompressedBytes = predictedBytes,
                    SpeedTier = speedTier,
                    Rationale = rationale,
                },
            },
            RejectedAlternatives = new[]
            {
                new RejectedStrategyAlternative
                {
                    StrategyName = "Store / FastStreamBypass",
                    ReasonCode = "MEASURED_COMPRESSIBLE_GAIN",
                    RejectionReason = $"32 KiB micro-probe achieved {effectiveProbe:F3} ratio ({(1.0 - predictedRatio) * 100.0:F0}% predicted savings).",
                    EstimatedPenalty = $"Forfeits ~{FormatBytes(inspection.SizeBytes - predictedBytes)} of lossless savings",
                },
            },
            PredictedRatio = predictedRatio,
            PredictedSavingsPercent = (1.0 - predictedRatio) * 100.0,
            PredictedCompressedBytes = predictedBytes,
            PredictedSpeedTier = speedTier,
            EstimatedThroughputRange = throughput,
            ConsentGatedAdvisoryNote = sig.HasConsentGatedOptimizationCandidate
                ? $"Non-executed advisory: {sig.FormatId} is compressed bit-for-bit losslessly. Any image/metadata re-optimization requires explicit consent outside LosslessCompression."
                : null,
        };
    }

    private static StrategyDecisionReport BuildTinyFileReport(
        ContentInspectionResult inspection,
        string targetPath,
        CompressionProfile profile)
    {
        var s = inspection.Signals;
        var sig = inspection.Signature;
        var level = profile == CompressionProfile.Instant ? 1 : 3;
        var predictedRatio = Math.Clamp(s.MicroProbeRatio, 0.15, 0.98);
        var predictedBytes = (long)Math.Round(inspection.SizeBytes * predictedRatio);

        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-tiny-fast",
            profile,
            preprocess: "raw",
            codec: "zstd",
            level: level);

        var rationale =
            $"Tiny payload ({inspection.SizeBytes} B < 4 KiB, {sig.FormatId}); heavy dictionaries and multi-pass preprocessors add framing overhead exceeding gains. Selected fast Zstd-{level}.";

        return new StrategyDecisionReport
        {
            TargetPath = targetPath,
            IsDirectory = false,
            Profile = profile,
            DetectedFormat = sig.FormatId,
            DetectedMimeType = sig.MimeType,
            ContentClass = sig.ContentClass,
            WorkloadArchetype = WorkloadArchetype.GeneralFile,
            TotalFiles = 1,
            TotalBytes = inspection.SizeBytes,
            SampledBytes = inspection.SampledBytes,
            PeakInspectionBufferBytes = inspection.PeakBufferBytes,
            InspectionDurationMs = inspection.InspectionDurationMs,
            Signals = s,
            WindowSamples = inspection.Windows,
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "tiny-file-fast-zstd",
                StrategyName = $"Fast Zstd-{level} (Tiny Payload)",
                Preprocessors = new[] { "raw" },
                PrimaryCodec = "zstd",
                CodecLevel = level,
                RationaleSummary = rationale,
                ExecutableRecipe = recipe,
            },
            Routes = new[]
            {
                new WorkloadRoutePlan
                {
                    RouteId = "tiny-fast",
                    RouteName = $"Tiny File ({sig.FormatId})",
                    ContentClass = sig.ContentClass,
                    Formats = new[] { sig.FormatId },
                    FileCount = 1,
                    InputBytes = inspection.SizeBytes,
                    ShareOfBytesPercent = 100.0,
                    Preprocessors = new[] { "raw" },
                    Codec = "zstd",
                    CodecLevel = level,
                    PipelineSummary = $"Zstd-{level}",
                    PredictedRatio = predictedRatio,
                    PredictedCompressedBytes = predictedBytes,
                    SpeedTier = SpeedTier.UltraFast,
                    Rationale = rationale,
                },
            },
            RejectedAlternatives = new[]
            {
                new RejectedStrategyAlternative
                {
                    StrategyName = "LZMA2-9 / 64 MiB Dictionary",
                    ReasonCode = "TINY_FILE_DICTIONARY_OVERHEAD",
                    RejectionReason = "Allocating multi-megabyte match tables on a <4 KiB single file adds header/setup cost with negligible byte savings.",
                    EstimatedPenalty = "100x higher memory/startup latency for <20 bytes difference",
                },
            },
            PredictedRatio = predictedRatio,
            PredictedSavingsPercent = (1.0 - predictedRatio) * 100.0,
            PredictedCompressedBytes = predictedBytes,
            PredictedSpeedTier = SpeedTier.UltraFast,
            EstimatedThroughputRange = "600–1,500 MB/s",
        };
    }

    private static StrategyDecisionReport BuildEmptyReport(
        string targetPath,
        bool isDirectory,
        CompressionProfile profile,
        double durationMs)
    {
        var recipe = CreateValidatedRecipe(
            $"advisor-{profile.ToString().ToLowerInvariant()}-empty",
            profile,
            preprocess: "raw",
            codec: "store",
            level: 0);

        return new StrategyDecisionReport
        {
            TargetPath = targetPath,
            IsDirectory = isDirectory,
            Profile = profile,
            DetectedFormat = "Empty",
            DetectedMimeType = "application/x-empty",
            ContentClass = ContentClass.Empty,
            WorkloadArchetype = WorkloadArchetype.GeneralFile,
            TotalFiles = 0,
            TotalBytes = 0,
            SampledBytes = 0,
            PeakInspectionBufferBytes = 0,
            InspectionDurationMs = durationMs,
            Signals = new ContentInspectionSignals { MicroProbeRatio = 1.0 },
            WindowSamples = Array.Empty<InspectionWindowSample>(),
            SelectedPipeline = new SelectedStrategyPlan
            {
                StrategyId = "store-empty",
                StrategyName = "Store (Empty Input)",
                Preprocessors = new[] { "raw" },
                PrimaryCodec = "store",
                CodecLevel = 0,
                RationaleSummary = "Zero-byte input; stored directly as empty manifest entry.",
                ExecutableRecipe = recipe,
            },
            Routes = Array.Empty<WorkloadRoutePlan>(),
            RejectedAlternatives = Array.Empty<RejectedStrategyAlternative>(),
            PredictedRatio = 1.0,
            PredictedSavingsPercent = 0.0,
            PredictedCompressedBytes = 0,
            PredictedSpeedTier = SpeedTier.DiskSpeedBypass,
            EstimatedThroughputRange = "Instant (0 bytes)",
        };
    }

    private static IReadOnlyList<WorkloadRoutePlan> BuildMultiClassRoutes(
        IReadOnlyList<ContentInspectionResult> items,
        IReadOnlyList<(long Size, string Hash)> resolvedKeys,
        long totalBytes,
        CompressionProfile profile,
        bool forceSolidSource)
    {
        var routes = new List<WorkloadRoutePlan>();
        var indexedGroups = items
            .Select((item, idx) => (item, idx))
            .GroupBy(x => ClassifyRouteBucket(x.item))
            .OrderByDescending(g => g.Sum(x => x.item.SizeBytes));

        foreach (var group in indexedGroups)
        {
            var groupEntries = group.ToList();
            var groupItems = groupEntries.Select(x => x.item).ToList();
            var groupIndices = groupEntries.Select(x => x.idx);
            var groupBytes = groupItems.Sum(x => x.SizeBytes);
            var sharePct = totalBytes > 0 ? (groupBytes * 100.0) / totalBytes : 0.0;
            var formats = groupItems.Select(x => x.Signature.FormatId).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            var avgProbe = groupBytes > 0
                ? groupItems.Sum(x => Math.Min(x.Signals.MicroProbeRatio, x.Signals.PreprocessedMicroProbeRatio ?? x.Signals.MicroProbeRatio) * x.SizeBytes) / groupBytes
                : 1.0;

            var (_, groupDedupBytes) = ComputeDuplicateSavingsFromKeys(items, groupIndices, resolvedKeys);
            var netBytes = Math.Max(0L, groupBytes - groupDedupBytes);

            switch (group.Key)
            {
                case RouteBucket.CompressedBypass:
                {
                    var unitRatio = Math.Clamp(avgProbe, 0.996, 1.0);
                    var outBytes = (long)Math.Round(netBytes * unitRatio);
                    var effRatio = groupBytes > 0 ? (double)outBytes / groupBytes : 1.0;
                    routes.Add(new WorkloadRoutePlan
                    {
                        RouteId = "route-store-bypass",
                        RouteName = $"Pre-Compressed Media/Archives ({string.Join("/", formats)})",
                        ContentClass = groupItems[0].Signature.ContentClass,
                        Formats = formats,
                        FileCount = groupItems.Count,
                        InputBytes = groupBytes,
                        ShareOfBytesPercent = sharePct,
                        Preprocessors = new[] { "sha256_exact_dedup", "xxh64_stream_hash" },
                        Codec = "store",
                        CodecLevel = 0,
                        PipelineSummary = "Store / FastStreamBypass + SHA256Dedup",
                        PredictedRatio = effRatio,
                        PredictedCompressedBytes = outBytes,
                        SpeedTier = SpeedTier.DiskSpeedBypass,
                        Rationale = $"High-entropy compressed streams ({string.Join(", ", formats)}) bypass heavy codecs (<0.4% codec gain) for instant disk-speed archival.",
                    });
                    break;
                }

                case RouteBucket.UncompressedRasterAndAudio:
                {
                    var (codec, level, _, _, tier, _) = PickBalancedOrArchivalBackend(profile, groupBytes);
                    var codecLabel = codec == "7z" ? $"LZMA2-{level}" : $"Zstd-{level}";
                    var unitRatio = Math.Clamp(avgProbe * 0.86, 0.14, 0.76);
                    var outBytes = (long)Math.Round(netBytes * unitRatio);
                    var effRatio = groupBytes > 0 ? (double)outBytes / groupBytes : 1.0;
                    routes.Add(new WorkloadRoutePlan
                    {
                        RouteId = "route-delta-raster",
                        RouteName = $"Uncompressed Rasters & RAW ({string.Join("/", formats)})",
                        ContentClass = ContentClass.UncompressedRasterImage,
                        Formats = formats,
                        FileCount = groupItems.Count,
                        InputBytes = groupBytes,
                        ShareOfBytesPercent = sharePct,
                        Preprocessors = new[] { "sha256_exact_dedup", "delta", "byteshuffle" },
                        Codec = codec,
                        CodecLevel = level,
                        PipelineSummary = $"Delta + {codecLabel} + SHA256Dedup",
                        PredictedRatio = effRatio,
                        PredictedCompressedBytes = outBytes,
                        SpeedTier = tier,
                        Rationale = $"Uncompressed {string.Join("/", formats)} channels correlate strongly across pixel/sensor strides; Delta + {codecLabel} achieves bit-exact lossless savings.",
                    });
                    break;
                }

                case RouteBucket.SourceAndText:
                {
                    var (codec, level, _, dictKb, tier, _) = PickLongWindowBackend(profile, groupBytes);
                    var codecLabel = codec == "7z" ? $"LZMA2-{level}" : $"Zstd-{level} ({dictKb / 1024} MiB)";
                    var solidFactor = forceSolidSource && groupItems.Count > 1 ? 0.70 : 0.80;
                    var unitRatio = Math.Clamp(avgProbe * solidFactor, 0.05, 0.60);
                    var outBytes = (long)Math.Round(netBytes * unitRatio);
                    var effRatio = groupBytes > 0 ? (double)outBytes / groupBytes : 1.0;
                    routes.Add(new WorkloadRoutePlan
                    {
                        RouteId = "route-solid-source",
                        RouteName = $"Source Code & Text ({string.Join("/", formats)})",
                        ContentClass = ContentClass.SourceAndText,
                        Formats = formats,
                        FileCount = groupItems.Count,
                        InputBytes = groupBytes,
                        ShareOfBytesPercent = sharePct,
                        Preprocessors = new[] { "sha256_exact_dedup", "type_grouped_solid_order", "trained_token_dictionary" },
                        Codec = codec,
                        CodecLevel = level,
                        PipelineSummary = $"TypeGroupedSolid + {codecLabel} + SHA256Dedup",
                        PredictedRatio = effRatio,
                        PredictedCompressedBytes = outBytes,
                        SpeedTier = tier,
                        Rationale = "Type-sorted solid blocks share lexical dictionaries and long-window history across related source/text files.",
                    });
                    break;
                }

                case RouteBucket.DatabaseAndTabular:
                {
                    var (codec, level, _, dictKb, tier, _) = PickLongWindowBackend(profile, groupBytes);
                    var codecLabel = codec == "7z" ? $"LZMA2-{level}" : $"Zstd-{level} ({dictKb / 1024} MiB)";
                    var unitRatio = Math.Clamp(avgProbe * 0.76, 0.05, 0.80);
                    var outBytes = (long)Math.Round(netBytes * unitRatio);
                    var effRatio = groupBytes > 0 ? (double)outBytes / groupBytes : 1.0;
                    routes.Add(new WorkloadRoutePlan
                    {
                        RouteId = "route-shuffle-database",
                        RouteName = $"Structured Database & Tabular ({string.Join("/", formats)})",
                        ContentClass = ContentClass.DatabaseAndTabular,
                        Formats = formats,
                        FileCount = groupItems.Count,
                        InputBytes = groupBytes,
                        ShareOfBytesPercent = sharePct,
                        Preprocessors = new[] { "sha256_exact_dedup", "byteshuffle", "delta" },
                        Codec = codec,
                        CodecLevel = level,
                        PipelineSummary = $"ByteShuffle/Delta + {codecLabel}",
                        PredictedRatio = effRatio,
                        PredictedCompressedBytes = outBytes,
                        SpeedTier = tier,
                        Rationale = "ByteShuffle/Delta aligns fixed-stride columns and B-tree page structures before long-window entropy coding.",
                    });
                    break;
                }

                case RouteBucket.Executables:
                {
                    var (codec, level, _, _, tier, _) = PickBalancedOrArchivalBackend(profile, groupBytes);
                    var codecLabel = codec == "7z" ? $"LZMA2-{level}" : $"Zstd-{level}";
                    var unitRatio = Math.Clamp(avgProbe * 0.82, 0.16, 0.78);
                    var outBytes = (long)Math.Round(netBytes * unitRatio);
                    var effRatio = groupBytes > 0 ? (double)outBytes / groupBytes : 1.0;
                    routes.Add(new WorkloadRoutePlan
                    {
                        RouteId = "route-bcj-binaries",
                        RouteName = $"Executable Binaries ({string.Join("/", formats)})",
                        ContentClass = ContentClass.ExecutableBinary,
                        Formats = formats,
                        FileCount = groupItems.Count,
                        InputBytes = groupBytes,
                        ShareOfBytesPercent = sharePct,
                        Preprocessors = new[] { "sha256_exact_dedup", "x86_bcj" },
                        Codec = codec,
                        CodecLevel = level,
                        PipelineSummary = $"X86Bcj + {codecLabel}",
                        PredictedRatio = effRatio,
                        PredictedCompressedBytes = outBytes,
                        SpeedTier = tier,
                        Rationale = "Reversible x86 BCJ jump filtering improves match density across native and managed binaries.",
                    });
                    break;
                }

                default:
                {
                    var (codec, level, _, _, tier, _) = PickBalancedOrArchivalBackend(profile, groupBytes);
                    var codecLabel = codec == "7z" ? $"LZMA2-{level}" : $"Zstd-{level}";
                    var unitRatio = Math.Clamp(avgProbe * 0.88, 0.10, 0.96);
                    var outBytes = (long)Math.Round(netBytes * unitRatio);
                    var effRatio = groupBytes > 0 ? (double)outBytes / groupBytes : 1.0;
                    routes.Add(new WorkloadRoutePlan
                    {
                        RouteId = "route-general-lossless",
                        RouteName = $"General Data ({string.Join("/", formats)})",
                        ContentClass = groupItems[0].Signature.ContentClass,
                        Formats = formats,
                        FileCount = groupItems.Count,
                        InputBytes = groupBytes,
                        ShareOfBytesPercent = sharePct,
                        Preprocessors = new[] { "sha256_exact_dedup" },
                        Codec = codec,
                        CodecLevel = level,
                        PipelineSummary = codecLabel,
                        PredictedRatio = effRatio,
                        PredictedCompressedBytes = outBytes,
                        SpeedTier = tier,
                        Rationale = $"General lossless {codecLabel} block compression with SHA-256 deduplication.",
                    });
                    break;
                }
            }
        }

        return routes;
    }

    private enum RouteBucket
    {
        CompressedBypass,
        UncompressedRasterAndAudio,
        SourceAndText,
        DatabaseAndTabular,
        Executables,
        GeneralCompressible,
    }

    private static RouteBucket ClassifyRouteBucket(ContentInspectionResult item)
    {
        var c = item.Signature.ContentClass;
        var s = item.Signals;

        if (c is ContentClass.CompressedVideo or ContentClass.CompressedImage or ContentClass.CompressedAudio or ContentClass.CompressedArchive
            && IsEffectivelyIncompressibleMedia(s, c))
        {
            return RouteBucket.CompressedBypass;
        }

        if (s.InteriorEntropyBitsPerByte >= IncompressibleEntropyFloor && s.MicroProbeRatio >= IncompressibleProbeRatioFloor)
        {
            return RouteBucket.CompressedBypass;
        }

        return c switch
        {
            ContentClass.UncompressedRasterImage or ContentClass.UncompressedAudio => RouteBucket.UncompressedRasterAndAudio,
            ContentClass.SourceAndText => RouteBucket.SourceAndText,
            ContentClass.DatabaseAndTabular => RouteBucket.DatabaseAndTabular,
            ContentClass.ExecutableBinary => RouteBucket.Executables,
            _ => RouteBucket.GeneralCompressible,
        };
    }

    private static bool IsEffectivelyIncompressibleMedia(ContentInspectionSignals s, ContentClass contentClass)
    {
        if (contentClass == ContentClass.CompressedVideo)
        {
            return s.InteriorEntropyBitsPerByte >= 7.20 || s.MicroProbeRatio >= 0.95;
        }

        if (contentClass is ContentClass.CompressedImage or ContentClass.CompressedAudio or ContentClass.CompressedArchive)
        {
            return s.InteriorEntropyBitsPerByte >= 7.40 || s.MicroProbeRatio >= 0.95;
        }

        return false;
    }

    private static int NormalizeSupportedDeltaStride(int rawStride) =>
        rawStride switch
        {
            1 => 1,
            2 => 2,
            8 => 8,
            _ => 4,
        };

    private static (string Codec, int Level, string? Method, int DictKb, SpeedTier Tier, string Throughput) PickLongWindowBackend(
        CompressionProfile profile,
        long sizeBytes)
    {
        var largeDictKb = sizeBytes >= 64L * 1024 * 1024 ? 65536 : 16384;
        var mediumDictKb = sizeBytes >= 16L * 1024 * 1024 ? 32768 : 8192;

        return profile switch
        {
            CompressionProfile.Instant => ("zstd", 1, null, 4096, SpeedTier.UltraFast, "550–1,400 MB/s"),
            CompressionProfile.Balanced => ("zstd", 9, null, mediumDictKb, SpeedTier.Balanced, "250–600 MB/s"),
            CompressionProfile.Archive => ("zstd", 15, null, mediumDictKb, SpeedTier.DeepArchival, "90–240 MB/s"),
            CompressionProfile.Tiny => ("zstd", 19, null, largeDictKb, SpeedTier.DeepArchival, "35–110 MB/s"),
            CompressionProfile.Laboratory => ("7z", 9, "LZMA2", largeDictKb, SpeedTier.HeavyMaxRatio, "15–55 MB/s (multi-threaded LZMA2)"),
            _ => ("zstd", 9, null, mediumDictKb, SpeedTier.Balanced, "250–600 MB/s"),
        };
    }

    private static (string Codec, int Level, string? Method, int DictKb, SpeedTier Tier, string Throughput) PickBalancedOrArchivalBackend(
        CompressionProfile profile,
        long sizeBytes)
    {
        var dictKb = sizeBytes >= 32L * 1024 * 1024 ? 32768 : 8192;

        return profile switch
        {
            CompressionProfile.Instant => ("zstd", 1, null, 4096, SpeedTier.UltraFast, "600–1,500 MB/s"),
            CompressionProfile.Balanced => ("zstd", 6, null, dictKb, SpeedTier.Balanced, "350–850 MB/s"),
            CompressionProfile.Archive => ("zstd", 12, null, dictKb, SpeedTier.Balanced, "140–350 MB/s"),
            CompressionProfile.Tiny => ("zstd", 19, null, dictKb * 2, SpeedTier.DeepArchival, "35–110 MB/s"),
            CompressionProfile.Laboratory => ("7z", 9, "LZMA2", dictKb * 2, SpeedTier.HeavyMaxRatio, "15–55 MB/s (multi-threaded LZMA2)"),
            _ => ("zstd", 6, null, dictKb, SpeedTier.Balanced, "350–850 MB/s"),
        };
    }

    private static PipelineRecipe CreateValidatedRecipe(
        string id,
        CompressionProfile profile,
        string preprocess,
        string codec,
        int level,
        int? dictSizeKb = null,
        string? method = null,
        bool? solid = null,
        int? strideParam = null)
    {
        var safeId = id.Length <= 128 ? id : id[..128];
        var preprocessStep = new PreprocessStep { Name = preprocess };
        if (strideParam is int s)
        {
            preprocessStep.Params["stride"] = s;
        }

        var recipe = new PipelineRecipe
        {
            Id = safeId,
            Profile = profile,
            Verify = new VerifyOptions { DecompressRoundtrip = true, HashOriginal = true },
            Steps =
            [
                preprocessStep,
                new CompressStep
                {
                    Codec = codec,
                    Level = level,
                    DictSizeKb = dictSizeKb,
                    Method = method,
                    Solid = solid,
                    Threads = codec == "7z" ? 0 : null,
                },
            ],
        };

        SafetyPolicy.ValidateCompressionProfileRecipe(profile, recipe);
        return recipe;
    }

    private static IReadOnlyList<(long Size, string Hash)> BuildResolvedIdentityKeys(
        IReadOnlyList<ContentInspectionResult> items,
        IReadOnlyDictionary<int, string>? exactHashes)
    {
        var keys = new List<(long Size, string Hash)>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var hash = item.IsFullContentHash
                ? item.SampleFingerprintSha256
                : (exactHashes is not null && exactHashes.TryGetValue(i, out var exact) ? exact : item.SampleFingerprintSha256);
            keys.Add((item.SizeBytes, hash));
        }

        return keys;
    }

    private static (int DuplicateFileCount, long DuplicateReclaimableBytes) ComputeDuplicateSavingsFromKeys(
        IReadOnlyList<ContentInspectionResult> items,
        IEnumerable<int> indices,
        IReadOnlyList<(long Size, string Hash)> resolvedKeys)
    {
        var seen = new HashSet<(long Size, string Hash)>();
        int dupCount = 0;
        long dupBytes = 0;

        foreach (var i in indices)
        {
            var item = items[i];
            if (item.SizeBytes <= 0)
            {
                continue;
            }

            if (!seen.Add(resolvedKeys[i]))
            {
                dupCount++;
                dupBytes += item.SizeBytes;
            }
        }

        return (dupCount, dupBytes);
    }

    private static IReadOnlyDictionary<int, string> ResolveExactHashesForDuplicateCandidates(
        IReadOnlyList<ContentInspectionResult> items)
    {
        var result = new Dictionary<int, string>();
        var candidateGroups = items
            .Select((item, index) => (item, index))
            .Where(x => !x.item.IsFullContentHash && x.item.SizeBytes > 0 && !string.IsNullOrWhiteSpace(x.item.Path) && File.Exists(x.item.Path))
            .GroupBy(x => (x.item.SizeBytes, x.item.SampleFingerprintSha256))
            .Where(g => g.Count() >= 2);

        foreach (var group in candidateGroups)
        {
            foreach (var (item, index) in group)
            {
                using var fs = File.OpenRead(item.Path!);
                var hash = SHA256.HashData(fs);
                result[index] = Convert.ToHexString(hash).ToLowerInvariant();
            }
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<int, string>> ResolveExactHashesForDuplicateCandidatesAsync(
        IReadOnlyList<ContentInspectionResult> items,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();
        var candidateGroups = items
            .Select((item, index) => (item, index))
            .Where(x => !x.item.IsFullContentHash && x.item.SizeBytes > 0 && !string.IsNullOrWhiteSpace(x.item.Path) && File.Exists(x.item.Path))
            .GroupBy(x => (x.item.SizeBytes, x.item.SampleFingerprintSha256))
            .Where(g => g.Count() >= 2);

        foreach (var group in candidateGroups)
        {
            foreach (var (item, index) in group)
            {
                await using var fs = File.OpenRead(item.Path!);
                var hash = await SHA256.HashDataAsync(fs, cancellationToken);
                result[index] = Convert.ToHexString(hash).ToLowerInvariant();
            }
        }

        return result;
    }

    private static ContentInspectionSignals BlendWorkloadSignals(
        IReadOnlyList<ContentInspectionResult> items,
        long totalBytes)
    {
        if (totalBytes <= 0)
        {
            return new ContentInspectionSignals { MicroProbeRatio = 1.0 };
        }

        double meanEnt = 0, intEnt = 0, bigramCond = 0, bigramPred = 0, runLen = 0;
        double s1 = 0, s2 = 0, s3 = 0, s4 = 0, s8 = 0, bestRed = 0;
        double probeRatio = 0, preProbeSum = 0, ascii = 0, zeros = 0;
        long preProbeWeight = 0;
        int probeIn = 0, probeOut = 0;
        double minWin = double.MaxValue, maxWin = double.MinValue;
        int dominantStride = 0;

        foreach (var item in items)
        {
            var w = (double)item.SizeBytes / totalBytes;
            var s = item.Signals;
            meanEnt += s.MeanEntropyBitsPerByte * w;
            intEnt += s.InteriorEntropyBitsPerByte * w;
            bigramCond += s.BigramConditionalEntropy * w;
            bigramPred += s.BigramPredictability * w;
            runLen += s.RunLengthScore * w;
            s1 += s.Stride1EntropyReduction * w;
            s2 += s.Stride2EntropyReduction * w;
            s3 += s.Stride3EntropyReduction * w;
            s4 += s.Stride4EntropyReduction * w;
            s8 += s.Stride8EntropyReduction * w;
            bestRed += s.BestStrideEntropyReduction * w;
            probeRatio += s.MicroProbeRatio * w;
            ascii += s.AsciiPrintableRatio * w;
            zeros += s.ZeroByteRatio * w;
            probeIn += s.MicroProbeInputBytes;
            probeOut += s.MicroProbeCompressedBytes;
            minWin = Math.Min(minWin, s.MinWindowEntropy);
            maxWin = Math.Max(maxWin, s.MaxWindowEntropy);

            if (s.PreprocessedMicroProbeRatio.HasValue)
            {
                preProbeSum += s.PreprocessedMicroProbeRatio.Value * item.SizeBytes;
                preProbeWeight += item.SizeBytes;
            }

            if (s.BestStrideBytes > 0 && dominantStride == 0)
            {
                dominantStride = s.BestStrideBytes;
            }
        }

        double varSum = 0;
        foreach (var item in items)
        {
            var w = (double)item.SizeBytes / totalBytes;
            var diff = item.Signals.MeanEntropyBitsPerByte - meanEnt;
            varSum += w * (item.Signals.EntropyVariance + (diff * diff));
        }

        return new ContentInspectionSignals
        {
            MeanEntropyBitsPerByte = meanEnt,
            InteriorEntropyBitsPerByte = intEnt,
            EntropyVariance = varSum,
            EntropyStdDev = Math.Sqrt(varSum),
            MinWindowEntropy = minWin == double.MaxValue ? 0.0 : minWin,
            MaxWindowEntropy = maxWin == double.MinValue ? 0.0 : maxWin,
            BigramConditionalEntropy = bigramCond,
            BigramPredictability = bigramPred,
            RunLengthScore = runLen,
            Stride1EntropyReduction = s1,
            Stride2EntropyReduction = s2,
            Stride3EntropyReduction = s3,
            Stride4EntropyReduction = s4,
            Stride8EntropyReduction = s8,
            BestStrideBytes = dominantStride,
            BestStrideEntropyReduction = bestRed,
            MicroProbeInputBytes = probeIn,
            MicroProbeCompressedBytes = probeOut,
            MicroProbeRatio = probeRatio,
            PreprocessedMicroProbeRatio = preProbeWeight > 0 ? preProbeSum / preProbeWeight : null,
            AsciiPrintableRatio = ascii,
            ZeroByteRatio = zeros,
        };
    }

    private static IReadOnlyList<InspectionWindowSample> SelectRepresentativeWindows(
        IReadOnlyList<ContentInspectionResult> items)
    {
        var largest = items.OrderByDescending(i => i.SizeBytes).FirstOrDefault();
        return largest?.Windows ?? Array.Empty<InspectionWindowSample>();
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F2} GiB",
            >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F2} MiB",
            >= 1024L => $"{bytes / 1024.0:F1} KiB",
            _ => $"{bytes} B",
        };
    }
}
