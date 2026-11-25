namespace UltraCompress.Core.Search;

/// <summary>
/// Multi-candidate UCB1 + successive-halving tournament options governed by <see cref="ProfileContract"/>.
/// </summary>
public sealed class TournamentOptions
{
    /// <summary>Wall-clock budget for the whole tournament (Tiny default: 10 min).</summary>
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Minimum pipeline arms to register when codecs are available.</summary>
    public int MinArms { get; init; } = 8;

    /// <summary>Maximum pipeline arms to register (Tiny: 16; Laboratory: 24).</summary>
    public int MaxArms { get; init; } = 16;

    /// <summary>UCB1 exploration constant <c>c</c> (heterogeneous arms: ~1.0–1.4).</summary>
    public double ExplorationConstant { get; init; } = 1.2;

    /// <summary>Pulls per arm before UCB-based pruning.</summary>
    public int MinPullsBeforeUcbPrune { get; init; } = 2;

    /// <summary>Abandon arm trials above this multiple of the current best size.</summary>
    public double ArmOvershootStopRatio { get; init; } = 1.05;

    /// <summary>Consecutive arms that must fail the improvement threshold to halt.</summary>
    public int DiminishingReturnsWindow { get; init; } = 3;

    /// <summary>Minimum fractional size improvement vs best to count as meaningful (0.1%).</summary>
    public double DiminishingReturnsMinImprovementFraction { get; init; } = 0.001;

    /// <summary>Optional arm ids to try first (recipe-cache warm start).</summary>
    public IReadOnlyList<string>? WarmStartArmIds { get; init; }

    /// <summary>Hard cap on executed trials (safety rail).</summary>
    public int? MaxTrials { get; init; }

    public static TournamentOptions ForProfile(CompressionProfile profile)
    {
        var contract = ProfileContract.Get(profile);
        return new TournamentOptions
        {
            TimeBudget = contract.SearchTimeBudget,
            MinArms = Math.Min(8, contract.CandidatePipelineBudget),
            MaxArms = contract.CandidatePipelineBudget,
            ExplorationConstant = profile == CompressionProfile.Laboratory ? 1.4 : 1.2,
            MinPullsBeforeUcbPrune = 2,
        };
    }
}
