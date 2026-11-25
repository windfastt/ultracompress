namespace UltraCompress.Core.Safety;

/// <summary>
/// Explicit operator consent token required before executing any non-lossless <see cref="OperationKind"/>
/// (<see cref="OperationKind.LossyOptimization"/>, <see cref="OperationKind.FormatConversion"/>, or <see cref="OperationKind.DataRemoval"/>).
/// </summary>
public sealed record OperationConsentToken
{
    public required OperationKind OperationKind { get; init; }

    /// <summary>Must be true to confirm understanding that original bit-identity cannot be reconstructed from output.</summary>
    public required bool AcknowledgeNonReversible { get; init; }

    /// <summary>Explicit target parameters acknowledged by the caller (e.g. quality, format, stripped sections).</summary>
    public required IReadOnlyDictionary<string, string> TargetParameters { get; init; }

    /// <summary>Itemized impact preview shown prior to consent.</summary>
    public required OperationImpactPreview ImpactPreview { get; init; }

    /// <summary>True by default: mutating operations write to a separate output path and leave source untouched.</summary>
    public bool PreserveOriginal { get; init; } = true;

    /// <summary>First confirmation flag required if <see cref="PreserveOriginal"/> is overridden to false.</summary>
    public bool AllowInPlaceOverwrite { get; init; }

    /// <summary>Second independent confirmation flag (double-confirmation) required to permit destructive in-place overwrite.</summary>
    public bool ConfirmDestructiveInPlaceOverwrite { get; init; }

    public DateTimeOffset IssuedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public static OperationConsentToken Create(
        OperationKind operationKind,
        IReadOnlyDictionary<string, string> targetParameters,
        OperationImpactPreview impactPreview,
        bool acknowledgeNonReversible = true,
        bool preserveOriginal = true,
        bool allowInPlaceOverwrite = false,
        bool confirmDestructiveInPlaceOverwrite = false) =>
        new()
        {
            OperationKind = operationKind,
            AcknowledgeNonReversible = acknowledgeNonReversible,
            TargetParameters = targetParameters,
            ImpactPreview = impactPreview,
            PreserveOriginal = preserveOriginal,
            AllowInPlaceOverwrite = allowInPlaceOverwrite,
            ConfirmDestructiveInPlaceOverwrite = confirmDestructiveInPlaceOverwrite,
            IssuedAtUtc = DateTimeOffset.UtcNow,
        };
}
