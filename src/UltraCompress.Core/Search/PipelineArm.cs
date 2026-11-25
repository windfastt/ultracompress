namespace UltraCompress.Core.Search;

/// <summary>
/// One bandit arm: a pipeline family with a representative recipe (not a full numeric grid).
/// </summary>
public sealed class PipelineArm
{
    public required string ArmId { get; init; }

    public required string Summary { get; init; }

    public required PipelineRecipe Recipe { get; init; }

    /// <summary>Relative cost hint for advisor ordering (lower = cheaper).</summary>
    public int CostTier { get; init; }
}