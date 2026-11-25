namespace UltraCompress.Core.Search;

/// <summary>
/// Tiny-profile tournament / UCB1 search knobs (see internal compression-research §3).
/// </summary>
public sealed class TournamentOptions
{
    /// <summary>Wall-clock budget for the whole tournament (Tiny default: 10 min).</summary>
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Minimum pipeline arms to register (Tiny: 8).</summary>
    public int MinArms { get; init; } = 8;

    /// <summary>Maximum pipeline arms to register (Tiny: 12).</summary>
    public int MaxArms { get; init; } = 12;

    /// <summary>UCB1 exploration constant <c>c</c> (heterogeneous arms: ~1.0–1.4).</summary>
    public double ExplorationConstant { get; init; } = 1.2;

    /// <summary>Pulls per arm before UCB-based pruning.</summary>
    public int MinPullsBeforeUcbPrune { get; init; } = 2;

    /// <summary>Abandon arm trials above this multiple of the current best size (2% rule).</summary>
    public double ArmOvershootStopRatio { get; init; } = 1.02;

    /// <summary>Consecutive arms that must fail the improvement threshold to halt.</summary>
    public int DiminishingReturnsWindow { get; init; } = 3;

    /// <summary>Minimum fractional size improvement vs best to count as meaningful (0.1%).</summary>
    public double DiminishingReturnsMinImprovementFraction { get; init; } = 0.001;

    /// <summary>Optional arm ids to try first (recipe-cache warm start).</summary>
    public IReadOnlyList<string>? WarmStartArmIds { get; init; }

    /// <summary>Hard cap on executed trials (safety rail; default 4× max arms).</summary>
    public int? MaxTrials { get; init; }
}