using UltraCompress.Desktop.Presentation.Primitives;

namespace UltraCompress.Desktop.Presentation.Models;

public sealed class OptimizationTargetOption
{
    public string Id { get; init; } = string.Empty;

    public OptimizationSubMode SubMode { get; init; }

    public OperationKind OperationKind { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string TargetExtension { get; init; } = string.Empty;

    public double EstimatedOutputRatio { get; init; }

    public string FidelitySummary { get; init; } = string.Empty;

    public string ItemizedActionTemplate { get; init; } = string.Empty;
}

public sealed class OptimizationImpactPreview
{
    public OptimizationSubMode SubMode { get; init; }

    public OperationKind OperationKind { get; init; }

    public int InputFileCount { get; init; }

    public long TotalOriginalBytes { get; init; }

    public long EstimatedOutputBytes { get; init; }

    public long EstimatedSavedBytes => Math.Max(0, TotalOriginalBytes - EstimatedOutputBytes);

    public bool PreserveOriginalFile { get; init; } = true;

    public string FidelityImpactSummary { get; init; } = string.Empty;

    public IReadOnlyList<string> ItemizedChanges { get; init; } = Array.Empty<string>();

    public string? DestructiveOverwriteWarning { get; init; }

    public string BeforeAfterSummary =>
        $"{ByteSizeFormatter.FormatBytes(TotalOriginalBytes)} → ~{ByteSizeFormatter.FormatBytes(EstimatedOutputBytes)} (est. delta −{ByteSizeFormatter.FormatBytes(EstimatedSavedBytes)})";

    public string PreservationPolicyBadge =>
        PreserveOriginalFile
            ? "SAFE DEFAULT: Original files preserved untouched; transformed copies written separately."
            : "DESTRUCTIVE MODE: Original files will be replaced after transform — explicit extra confirmation required.";
}

public sealed class MutatingConsentState
{
    public OperationKind OperationKind { get; init; }

    public bool HasStagedFiles { get; init; }

    public bool HasGeneratedPreview { get; init; }

    public bool PreserveOriginalFile { get; init; } = true;

    public bool ConsentUnderstandNonBitExact { get; init; }

    public bool ConsentApproveItemizedPreview { get; init; }

    public bool ConsentAllowReplaceOriginalWhenUnpreserved { get; init; }
}

public sealed class MutatingConsentGateResult
{
    public bool IsAllowed { get; init; }

    public IReadOnlyList<string> MissingRequirements { get; init; } = Array.Empty<string>();

    public string GateStatusMessage { get; init; } = string.Empty;
}
