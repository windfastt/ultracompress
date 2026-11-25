using UltraCompress.Desktop.Presentation.Models;

namespace UltraCompress.Desktop.Presentation.Services;

public static class ConsentGateValidator
{
    public static IReadOnlyList<OptimizationTargetOption> Catalog { get; } =
    [
        new OptimizationTargetOption
        {
            Id = "lossy-photo-webp",
            SubMode = OptimizationSubMode.LossyOptimization,
            OperationKind = OperationKind.LossyOptimization,
            DisplayName = "Perceptual Image Optimization (Q82 High-Visual-Fidelity)",
            TargetExtension = ".optimized",
            EstimatedOutputRatio = 0.42,
            FidelitySummary = "Irreversible perceptual quantization (~0.985 SSIM); cryptographic SHA-256 will intentionally differ from original.",
            ItemizedActionTemplate = "Re-quantize high-frequency chroma/luminance blocks (~58% smaller, non-bit-exact)",
        },
        new OptimizationTargetOption
        {
            Id = "lossy-pdf-downsample",
            SubMode = OptimizationSubMode.LossyOptimization,
            OperationKind = OperationKind.LossyOptimization,
            DisplayName = "Document Raster Downsample (150 DPI Sharing Copy)",
            TargetExtension = ".compact.pdf",
            EstimatedOutputRatio = 0.35,
            FidelitySummary = "Downsamples embedded high-DPI scan rasters to 150 DPI while keeping vector text intact; irreversible.",
            ItemizedActionTemplate = "Downsample embedded scan images above 150 DPI (irreversible pixel reduction)",
        },
        new OptimizationTargetOption
        {
            Id = "convert-bmp-png",
            SubMode = OptimizationSubMode.FormatConversion,
            OperationKind = OperationKind.FormatConversion,
            DisplayName = "Raster Container Transcode (BMP/TIFF → PNG Container)",
            TargetExtension = ".converted.png",
            EstimatedOutputRatio = 0.55,
            FidelitySummary = "Transcodes container format and header layout; pixel values preserved, original container bytes not identical.",
            ItemizedActionTemplate = "Transcode container format and rewrite image chunk headers",
        },
        new OptimizationTargetOption
        {
            Id = "convert-jsonl-ndjson-archive",
            SubMode = OptimizationSubMode.FormatConversion,
            OperationKind = OperationKind.FormatConversion,
            DisplayName = "Normalized UTF-8 Canonical Newline Transcode",
            TargetExtension = ".normalized.txt",
            EstimatedOutputRatio = 0.94,
            FidelitySummary = "Normalizes CRLF → LF line endings and strips UTF-8 BOM; byte stream changes intentionally.",
            ItemizedActionTemplate = "Normalize line endings (CRLF → LF) and canonicalize UTF-8 header",
        },
        new OptimizationTargetOption
        {
            Id = "hygiene-strip-metadata",
            SubMode = OptimizationSubMode.DataRemoval,
            OperationKind = OperationKind.DataRemoval,
            DisplayName = "Strip EXIF / GPS / Camera & Author Metadata",
            TargetExtension = ".sanitized",
            EstimatedOutputRatio = 0.92,
            FidelitySummary = "Permanently removes EXIF, GPS coordinates, XMP sidecar tags, and embedded preview thumbnails from output.",
            ItemizedActionTemplate = "Strip [EXIF, GPS, XMP, Author, Embedded Thumbnail] metadata segments",
        },
        new OptimizationTargetOption
        {
            Id = "hygiene-strip-debug-padding",
            SubMode = OptimizationSubMode.DataRemoval,
            OperationKind = OperationKind.DataRemoval,
            DisplayName = "Strip Trailing Null Padding & Diagnostic Trailer Markers",
            TargetExtension = ".cleaned",
            EstimatedOutputRatio = 0.88,
            FidelitySummary = "Removes trailing diagnostic trailers and null padding blocks; resulting file is not bit-identical to original.",
            ItemizedActionTemplate = "Remove trailing null-padding and diagnostic metadata blocks",
        },
    ];

    public static IReadOnlyList<OptimizationTargetOption> GetOptionsForSubMode(OptimizationSubMode subMode) =>
        Catalog.Where(o => o.SubMode == subMode).ToList();

    public static OperationKind MapSubModeToOperationKind(OptimizationSubMode subMode) => subMode switch
    {
        OptimizationSubMode.LossyOptimization => OperationKind.LossyOptimization,
        OptimizationSubMode.FormatConversion => OperationKind.FormatConversion,
        OptimizationSubMode.DataRemoval => OperationKind.DataRemoval,
        _ => throw new ArgumentOutOfRangeException(nameof(subMode)),
    };

    public static MutatingConsentGateResult Validate(MutatingConsentState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var missing = new List<string>();

        if (state.OperationKind == OperationKind.LosslessCompression)
        {
            missing.Add("Optimization Studio cannot run LosslessCompression; mutating operations must specify LossyOptimization, FormatConversion, or DataRemoval.");
        }

        if (!state.HasStagedFiles)
        {
            missing.Add("Stage at least one file to inspect before enabling consent.");
        }

        if (!state.HasGeneratedPreview)
        {
            missing.Add("Generate and review the itemized Impact & Preview summary.");
        }

        if (!state.ConsentUnderstandNonBitExact)
        {
            missing.Add("Confirm you understand this operation is NOT bit-exact lossless compression.");
        }

        if (!state.ConsentApproveItemizedPreview)
        {
            missing.Add("Confirm you reviewed the itemized impact summary and output target.");
        }

        if (!state.PreserveOriginalFile && !state.ConsentAllowReplaceOriginalWhenUnpreserved)
        {
            missing.Add("Original-file preservation is turned OFF: explicitly confirm destructive in-place replacement (or re-enable 'Preserve Original File').");
        }

        if (missing.Count == 0)
        {
            return new MutatingConsentGateResult
            {
                IsAllowed = true,
                MissingRequirements = Array.Empty<string>(),
                GateStatusMessage = state.PreserveOriginalFile
                    ? "Consent verified · Original files locked SAFE (untouched); transformed copies will be written separately."
                    : "Consent verified (destructive override active) · Original files will be replaced.",
            };
        }

        return new MutatingConsentGateResult
        {
            IsAllowed = false,
            MissingRequirements = missing,
            GateStatusMessage = $"Execution locked — {missing.Count} safety requirement(s) pending.",
        };
    }
}
