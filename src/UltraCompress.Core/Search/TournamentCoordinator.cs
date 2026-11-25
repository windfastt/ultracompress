using System.Diagnostics;

namespace UltraCompress.Core.Search;

public delegate Task<ArmTrialResult> ArmTrialExecutor(PipelineArm arm, CancellationToken cancellationToken);

/// <summary>
/// Orchestrates Tiny-profile pipeline tournaments with UCB1 arm selection and early stopping.
/// </summary>
public sealed class TournamentCoordinator
{
    public async Task<TournamentResult> RunAsync(
        FileFingerprint fingerprint,
        CompressionProfile profile,
        long originalBytes,
        ArmTrialExecutor executor,
        TournamentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TournamentOptions();
        var arms = PipelineArmCatalog.SelectArms(fingerprint, profile, options);
        if (arms.Count == 0)
        {
            return new TournamentResult(null, Array.Empty<ArmTrialResult>(), TournamentStopReason.SkippedByAdvisor);
        }

        if (arms.Count == 1 && ShouldUseSingleFastArm(fingerprint))
        {
            var only = arms[0];
            var single = await executor(only, cancellationToken);
            return new TournamentResult(single, new[] { single }, TournamentStopReason.SkippedByAdvisor);
        }

        var bandit = new Ucb1Selector(options.ExplorationConstant);
        bandit.RegisterArms(arms.Select(a => a.ArmId));

        var armById = arms.ToDictionary(a => a.ArmId, StringComparer.OrdinalIgnoreCase);
        var trials = new List<ArmTrialResult>();
        var stoppedArms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prunedArms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ArmTrialResult? best = null;
        var recentImprovementFractions = new Queue<double>();
        var stopwatch = Stopwatch.StartNew();
        var maxTrials = options.MaxTrials ?? Math.Max(arms.Count * 4, 32);

        while (stopwatch.Elapsed < options.TimeBudget && trials.Count < maxTrials)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var eligible = armById.Keys
                .Where(id => !stoppedArms.Contains(id) && !prunedArms.Contains(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (eligible.Count == 0)
            {
                break;
            }

            if (ShouldStopByUcb(bandit, eligible, best, options))
            {
                return BuildResult(best, trials, TournamentStopReason.UcbEarlyStop);
            }

            var nextId = bandit.SelectNext(eligible);
            if (nextId is null)
            {
                break;
            }

            var arm = armById[nextId];
            var trial = await executor(arm, cancellationToken);

            if (!trial.Verified)
            {
                stoppedArms.Add(nextId);
                continue;
            }

            trials.Add(trial);
            bandit.RecordPull(nextId, trial.Reward);

            if (best is null || trial.CompressedBytes < best.CompressedBytes
                || (trial.CompressedBytes == best.CompressedBytes && trial.Reward > best.Reward))
            {
                if (best is not null)
                {
                    var improvement = (best.CompressedBytes - trial.CompressedBytes) / (double)Math.Max(1, best.CompressedBytes);
                    recentImprovementFractions.Enqueue(improvement);
                    while (recentImprovementFractions.Count > options.DiminishingReturnsWindow)
                    {
                        recentImprovementFractions.Dequeue();
                    }
                }

                best = trial;
            }

            if (best is not null && trial.CompressedBytes > best.CompressedBytes * options.ArmOvershootStopRatio)
            {
                stoppedArms.Add(nextId);
            }

            PruneArms(bandit, eligible, best, prunedArms, options);

            if (recentImprovementFractions.Count >= options.DiminishingReturnsWindow
                && recentImprovementFractions.All(f => f < options.DiminishingReturnsMinImprovementFraction))
            {
                return BuildResult(best, trials, TournamentStopReason.DiminishingReturns);
            }
        }

        var reason = stopwatch.Elapsed >= options.TimeBudget
            ? TournamentStopReason.TimeBudgetExceeded
            : TournamentStopReason.WinnerFound;

        return BuildResult(best, trials, reason);
    }

    private static bool ShouldUseSingleFastArm(FileFingerprint fingerprint) =>
        fingerprint.EntropyBitsPerByte is >= 7.9;

    private static bool ShouldStopByUcb(
        Ucb1Selector bandit,
        IReadOnlySet<string> eligible,
        ArmTrialResult? best,
        TournamentOptions options)
    {
        if (best is null)
        {
            return false;
        }

        foreach (var id in eligible)
        {
            if (bandit.GetPullCount(id) < options.MinPullsBeforeUcbPrune)
            {
                return false;
            }

            if (bandit.UcbUpperBound(id) >= best.Reward)
            {
                return false;
            }
        }

        return eligible.Count > 0;
    }

    private static void PruneArms(
        Ucb1Selector bandit,
        IReadOnlySet<string> eligible,
        ArmTrialResult? best,
        HashSet<string> prunedArms,
        TournamentOptions options)
    {
        if (best is null)
        {
            return;
        }

        foreach (var id in eligible)
        {
            if (bandit.GetPullCount(id) < options.MinPullsBeforeUcbPrune)
            {
                continue;
            }

            if (bandit.UcbUpperBound(id) < best.Reward)
            {
                prunedArms.Add(id);
            }
        }
    }

    private static TournamentResult BuildResult(
        ArmTrialResult? best,
        IReadOnlyList<ArmTrialResult> trials,
        TournamentStopReason reason) =>
        new(best, trials, reason);
}

public sealed record TournamentResult(
    ArmTrialResult? Winner,
    IReadOnlyList<ArmTrialResult> Trials,
    TournamentStopReason StopReason);