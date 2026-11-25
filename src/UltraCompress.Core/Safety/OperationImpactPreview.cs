namespace UltraCompress.Core.Safety;

/// <summary>
/// Single itemized entry in an <see cref="OperationImpactPreview"/> describing non-reversible changes.
/// </summary>
public sealed record ImpactPreviewItem(
    string TargetPath,
    string TransformId,
    string Description,
    long OriginalBytes,
    long? EstimatedOutputBytes = null,
    bool IsReversible = false);

/// <summary>
/// Itemized preview of the fidelity, structural, or metadata impact of a mutating operation.
/// </summary>
public sealed record OperationImpactPreview
{
    public required OperationKind OperationKind { get; init; }

    public required string Summary { get; init; }

    public required IReadOnlyList<ImpactPreviewItem> Items { get; init; }
}
