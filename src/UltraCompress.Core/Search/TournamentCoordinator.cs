using System.Diagnostics;
using UltraCompress.Core.Recipes;
using UltraCompress.Core.Safety;
using UltraCompress.Core.Transforms;

namespace UltraCompress.Core.Search;

public delegate Task<ArmTrialResult> ArmTrialExecutor(PipelineArm arm, CancellationToken cancellationToken);

/// <summary>
/// Full-stream tournament execution outcome including pilot trials, selected recipe,
/// compressed payload, dual SHA-256/XXH64 verification, and store fallback metadata.
/// </summary>
public sealed record TournamentExecutionResult(
    PipelineRecipe WinningRecipe,
    byte[] CompressedBytes,
    BitExactVerificationResult Verification,
    ArmTrialResult? PilotWinner,
    IReadOnlyList<ArmTrialResult> Trials,
    TournamentStopReason StopReason,
    bool UsedStoreFallback,
    int PilotSampleBytesEvaluated,
    int RungsExecuted);

/// <summary>
/// Production multi-candidate tournament evaluator combining UCB1 bandit selection with successive halving
/// over representative pilot chunks across <c>(Raw, Delta, ByteShuffle, X86Bcj) x (Zstd, Brotli, LibDeflate, SevenZip)</c>.
/// </summary>
public sealed class TournamentCoordinator
{
    /// <summary>
    /// Runs UCB1 arm selection over caller-supplied <see cref="ArmTrialExecutor"/> trials.
    /// </summary>
    public async Task<TournamentResult> RunAsync(
        FileFingerprint fingerprint,
        CompressionProfile profile,
        long originalBytes,
        ArmTrialExecutor executor,
        TournamentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var contract = ProfileContract.Get(profile);
        contract.ValidateContractInvariants();
        MutatingOperationGuard.EnforceProfileBoundary(profile, OperationKind.LosslessCompression);

        options ??= TournamentOptions.ForProfile(profile);
        var arms = PipelineArmCatalog.SelectArms(fingerprint, profile, options);
        if (arms.Count == 0)
        {
            return new TournamentResult(null, Array.Empty<ArmTrialResult>(), TournamentStopReason.SkippedByAdvisor);
        }

        if (arms.Count == 1 && fingerprint.EntropyBitsPerByte >= contract.IncompressibleEntropyBypassBitsPerByte)
        {
            var only = arms[0];
            var single = await executor(only, cancellationToken);
            return new TournamentResult(single, [single], TournamentStopReason.IncompressibleBypass);
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
                return new TournamentResult(best, trials, TournamentStopReason.UcbEarlyStop);
            }

            var nextId = bandit.SelectNext(eligible);
            if (nextId is null)
            {
                break;
            }

            var arm = armById[nextId];
            SafetyPolicy.ValidateCompressionProfileRecipe(profile, arm.Recipe);
            var trial = await executor(arm, cancellationToken);

            if (!trial.Verified)
            {
                stoppedArms.Add(nextId);
                continue;
            }

            trials.Add(trial);
            bandit.RecordPull(nextId, trial.Reward);

            if (best is null
                || trial.CompressedBytes < best.CompressedBytes
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
                return new TournamentResult(best, trials, TournamentStopReason.DiminishingReturns);
            }
        }

        var reason = stopwatch.Elapsed >= options.TimeBudget
            ? TournamentStopReason.TimeBudgetExceeded
            : TournamentStopReason.WinnerFound;

        return new TournamentResult(best, trials, reason);
    }

    /// <summary>
    /// Executes the full production multi-candidate tournament on <paramref name="fullStreamBytes"/>:
    /// 1) Samples aligned multi-window pilot chunks per <see cref="ProfileContract"/>.
    /// 2) Evaluates <c>(Raw, Delta, ByteShuffle, X86Bcj) x (Zstd, Brotli, LibDeflate, SevenZip)</c> with UCB1 + successive halving.
    /// 3) Selects the smallest roundtrip-verified pipeline, or falls back to <c>Store</c> if incompressible (&gt;= 0.995 ratio or entropy gate).
    /// 4) Executes full-stream compression and verifies bit-exact SHA-256 + XXH64 roundtrip.
    /// 5) Records the winning recipe in <see cref="IRecipeStore"/> (<see cref="SqliteRecipeStore"/>).
    /// </summary>
    public async Task<TournamentExecutionResult> ExecuteProductionTournamentAsync(
        FileFingerprint fingerprint,
        ReadOnlyMemory<byte> fullStreamBytes,
        CompressionProfile profile,
        IReadOnlyDictionary<string, ICompressionBackend> backends,
        IRecipeStore? recipeStore = null,
        TournamentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(backends);

        var contract = ProfileContract.Get(profile);
        contract.ValidateContractInvariants();
        MutatingOperationGuard.EnforceProfileBoundary(profile, OperationKind.LosslessCompression);
        options ??= TournamentOptions.ForProfile(profile);

        if (fullStreamBytes.IsEmpty)
        {
            var emptyStoreRecipe = ProfileDefaults.CreateStoreRecipe(profile, $"tournament-store-{profile.ToString().ToLowerInvariant()}");
            var emptyVerification = BitExactVerifier.VerifyOrThrow(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);
            PersistWinnerIfConfigured(recipeStore, fingerprint, 0, emptyVerification.OriginalSha256, emptyStoreRecipe);
            return new TournamentExecutionResult(
                WinningRecipe: emptyStoreRecipe,
                CompressedBytes: Array.Empty<byte>(),
                Verification: emptyVerification,
                PilotWinner: null,
                Trials: Array.Empty<ArmTrialResult>(),
                StopReason: TournamentStopReason.IncompressibleBypass,
                UsedStoreFallback: true,
                PilotSampleBytesEvaluated: 0,
                RungsExecuted: 0);
        }

        var pilotSample = PilotChunkSampler.ExtractCompositeSample(fullStreamBytes.Span, contract);
        var rungs = PilotChunkSampler.BuildSuccessiveHalvingRungs(pilotSample);
        var rawEntropy = EntropyCalculator.ShannonBitsPerByte(pilotSample, 0, pilotSample.Length);
        var (_, postDeltaEntropy) = DeltaTransform.SelectBestStride(pilotSample);

        // Fast entropy bypass when neither raw bytes nor delta-coded strides show structure.
        if (rawEntropy >= contract.IncompressibleEntropyBypassBitsPerByte
            && postDeltaEntropy >= contract.IncompressibleEntropyBypassBitsPerByte
            && await ConfirmPilotIncompressibleAsync(rungs[0], backends, contract.IncompressibleRatioThreshold, cancellationToken))
        {
            return await FinalizeWithStoreAsync(
                fingerprint,
                fullStreamBytes,
                profile,
                recipeStore,
                Array.Empty<ArmTrialResult>(),
                TournamentStopReason.IncompressibleBypass,
                pilotSampleBytes: rungs[0].Length,
                rungsExecuted: 1,
                cancellationToken);
        }

        var availableCodecNames = backends.Keys
            .Where(k => !k.Equals("store", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidateArms = PipelineArmCatalog.SelectArms(
            fingerprint,
            profile,
            options,
            pilotSample,
            availableCodecNames);

        if (candidateArms.Count == 0)
        {
            return await FinalizeWithStoreAsync(
                fingerprint,
                fullStreamBytes,
                profile,
                recipeStore,
                Array.Empty<ArmTrialResult>(),
                TournamentStopReason.AllArmsExhausted,
                pilotSampleBytes: 0,
                rungsExecuted: 0,
                cancellationToken);
        }

        var armMap = candidateArms.ToDictionary(a => a.ArmId, StringComparer.OrdinalIgnoreCase);
        var bandit = new Ucb1Selector(options.ExplorationConstant);
        bandit.RegisterArms(armMap.Keys);

        var activeIds = candidateArms.Select(a => a.ArmId).ToList();
        var allTrials = new List<ArmTrialResult>(candidateArms.Count * 2);
        var latestVerifiedByArm = new Dictionary<string, ArmTrialResult>(StringComparer.OrdinalIgnoreCase);
        var stopwatch = Stopwatch.StartNew();
        var rungsExecuted = 0;
        var stopReason = TournamentStopReason.SuccessiveHalvingComplete;

        for (var rungIdx = 0; rungIdx < rungs.Count && activeIds.Count > 0; rungIdx++)
        {
            if (stopwatch.Elapsed >= options.TimeBudget)
            {
                stopReason = TournamentStopReason.TimeBudgetExceeded;
                break;
            }

            var rungSample = rungs[rungIdx];
            rungsExecuted++;

            var remainingInRung = new HashSet<string>(activeIds, StringComparer.OrdinalIgnoreCase);
            var rungResults = new List<ArmTrialResult>(activeIds.Count);

            while (remainingInRung.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.Elapsed >= options.TimeBudget && rungResults.Count > 0)
                {
                    stopReason = TournamentStopReason.TimeBudgetExceeded;
                    break;
                }

                var nextArmId = bandit.SelectNext(remainingInRung) ?? remainingInRung.First();
                remainingInRung.Remove(nextArmId);

                var arm = armMap[nextArmId];
                SafetyPolicy.ValidateCompressionProfileRecipe(profile, arm.Recipe);

                var trial = await EvaluatePilotArmAsync(arm, rungSample, rungIdx, backends, cancellationToken);
                allTrials.Add(trial);

                if (!trial.Verified)
                {
                    continue;
                }

                rungResults.Add(trial);
                latestVerifiedByArm[nextArmId] = trial;

                // Normalized UCB1 reward: compression ratio gain in [0, 1] + throughput tie-breaker.
                var savingsFraction = Math.Max(0.0, 1.0 - trial.Ratio);
                var speedBonus = 0.01 / (1.0 + trial.WallSeconds * 10.0);
                bandit.RecordPull(nextArmId, savingsFraction + speedBonus);
            }

            if (rungResults.Count == 0)
            {
                activeIds.Clear();
                stopReason = TournamentStopReason.AllArmsExhausted;
                break;
            }

            // Successive halving between rungs: keep top ceil(N/2) by compressed size plus near-ties / top UCB bound.
            if (rungIdx < rungs.Count - 1 && rungResults.Count > 2)
            {
                var ordered = rungResults
                    .OrderBy(t => t.CompressedBytes)
                    .ThenBy(t => armMap[t.ArmId].CostTier)
                    .ThenBy(t => t.WallSeconds)
                    .ToList();

                var keepCount = Math.Max(2, (ordered.Count + 1) / 2);
                var bestBytes = ordered[0].CompressedBytes;
                var nearTieCeiling = (long)Math.Ceiling(bestBytes * 1.005);

                var survivors = ordered
                    .Take(keepCount)
                    .Select(t => t.ArmId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var contender in ordered.Skip(keepCount))
                {
                    if (contender.CompressedBytes <= nearTieCeiling)
                    {
                        survivors.Add(contender.ArmId);
                    }
                }

                var topUcbCandidate = ordered
                    .Select(t => t.ArmId)
                    .OrderByDescending(id => bandit.UcbUpperBound(id))
                    .FirstOrDefault();
                if (topUcbCandidate is not null)
                {
                    survivors.Add(topUcbCandidate);
                }

                activeIds = activeIds
                    .Where(survivors.Contains)
                    .ToList();
            }
            else
            {
                activeIds = rungResults
                    .OrderBy(t => t.CompressedBytes)
                    .ThenBy(t => armMap[t.ArmId].CostTier)
                    .ThenBy(t => t.WallSeconds)
                    .Select(t => t.ArmId)
                    .ToList();
            }
        }

        // Choose smallest verified pipeline on the highest completed rung.
        ArmTrialResult? pilotWinner = null;
        if (latestVerifiedByArm.Count > 0)
        {
            var maxRung = latestVerifiedByArm.Values.Max(t => t.RungIndex);
            pilotWinner = latestVerifiedByArm.Values
                .Where(t => t.RungIndex == maxRung && t.Verified)
                .OrderBy(t => t.CompressedBytes)
                .ThenBy(t => armMap[t.ArmId].CostTier)
                .ThenBy(t => t.WallSeconds)
                .FirstOrDefault();
        }

        // Fallback to Store if no verified winner or pilot compression ratio >= 0.995.
        if (pilotWinner is null || pilotWinner.Ratio >= contract.IncompressibleRatioThreshold)
        {
            return await FinalizeWithStoreAsync(
                fingerprint,
                fullStreamBytes,
                profile,
                recipeStore,
                allTrials,
                TournamentStopReason.IncompressibleBypass,
                pilotSampleBytes: pilotSample.Length,
                rungsExecuted: rungsExecuted,
                cancellationToken);
        }

        // Execute full-stream compression with the winning recipe.
        var winningRecipe = pilotWinner.Recipe;
        SafetyPolicy.ValidateCompressionProfileRecipe(profile, winningRecipe);

        var winningCompressStep = winningRecipe.Steps.OfType<CompressStep>().Last();
        var winningBackend = backends[winningCompressStep.Codec];

        var fullTransformed = TransformPipeline.Encode(fullStreamBytes.Span, winningRecipe.Steps);
        var fullCompressed = await winningBackend.CompressAsync(fullTransformed, winningCompressStep, cancellationToken);

        // Enforce >= 0.995 incompressible Store threshold on the full stream as well.
        var fullStreamRatio = (double)fullCompressed.Length / fullStreamBytes.Length;
        if (fullStreamRatio >= contract.IncompressibleRatioThreshold)
        {
            return await FinalizeWithStoreAsync(
                fingerprint,
                fullStreamBytes,
                profile,
                recipeStore,
                allTrials,
                TournamentStopReason.IncompressibleBypass,
                pilotSampleBytes: pilotSample.Length,
                rungsExecuted: rungsExecuted,
                cancellationToken);
        }

        // Mandatory full-stream bit-exact roundtrip verification (SHA-256 + XXH64 + byte equality).
        var fullDecompressed = await winningBackend.DecompressAsync(fullCompressed, winningCompressStep, cancellationToken);
        var fullRestored = TransformPipeline.Decode(fullDecompressed, winningRecipe.Steps);
        var verification = BitExactVerifier.VerifyOrThrow(fullStreamBytes.Span, fullRestored);

        PersistWinnerIfConfigured(recipeStore, fingerprint, fullStreamBytes.Length, verification.OriginalSha256, winningRecipe);

        return new TournamentExecutionResult(
            WinningRecipe: winningRecipe,
            CompressedBytes: fullCompressed,
            Verification: verification,
            PilotWinner: pilotWinner,
            Trials: allTrials,
            StopReason: stopReason,
            UsedStoreFallback: false,
            PilotSampleBytesEvaluated: pilotSample.Length,
            RungsExecuted: rungsExecuted);
    }

    private static async Task<ArmTrialResult> EvaluatePilotArmAsync(
        PipelineArm arm,
        byte[] rungSample,
        int rungIndex,
        IReadOnlyDictionary<string, ICompressionBackend> backends,
        CancellationToken cancellationToken)
    {
        var compressStep = arm.Recipe.Steps.OfType<CompressStep>().Last();
        if (!backends.TryGetValue(compressStep.Codec, out var backend))
        {
            return new ArmTrialResult(arm.ArmId, rungSample.Length, rungSample.Length, 0, Verified: false, arm.Recipe, rungIndex);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var transformed = TransformPipeline.Encode(rungSample, arm.Recipe.Steps);
            var compressed = await backend.CompressAsync(transformed, compressStep, cancellationToken);
            var decompressed = await backend.DecompressAsync(compressed, compressStep, cancellationToken);
            var restored = TransformPipeline.Decode(decompressed, arm.Recipe.Steps);
            sw.Stop();

            var verified = restored.AsSpan().SequenceEqual(rungSample);
            return new ArmTrialResult(
                ArmId: arm.ArmId,
                OriginalBytes: rungSample.Length,
                CompressedBytes: compressed.Length,
                WallSeconds: Math.Max(1e-6, sw.Elapsed.TotalSeconds),
                Verified: verified,
                Recipe: arm.Recipe,
                RungIndex: rungIndex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            sw.Stop();
            return new ArmTrialResult(arm.ArmId, rungSample.Length, rungSample.Length, sw.Elapsed.TotalSeconds, Verified: false, arm.Recipe, rungIndex);
        }
    }

    private static async Task<bool> ConfirmPilotIncompressibleAsync(
        byte[] probeSlice,
        IReadOnlyDictionary<string, ICompressionBackend> backends,
        double incompressibleRatioThreshold,
        CancellationToken cancellationToken)
    {
        if (probeSlice.Length == 0)
        {
            return true;
        }

        if (!backends.TryGetValue("zstd", out var zstdBackend))
        {
            return true;
        }

        var fastStep = new CompressStep { Codec = "zstd", Level = 1 };
        var compressed = await zstdBackend.CompressAsync(probeSlice, fastStep, cancellationToken);
        return (double)compressed.Length / probeSlice.Length >= incompressibleRatioThreshold;
    }

    private static async Task<TournamentExecutionResult> FinalizeWithStoreAsync(
        FileFingerprint fingerprint,
        ReadOnlyMemory<byte> fullStreamBytes,
        CompressionProfile profile,
        IRecipeStore? recipeStore,
        IReadOnlyList<ArmTrialResult> trials,
        TournamentStopReason stopReason,
        int pilotSampleBytes,
        int rungsExecuted,
        CancellationToken cancellationToken)
    {
        var storeRecipe = ProfileDefaults.CreateStoreRecipe(profile, $"tournament-store-{profile.ToString().ToLowerInvariant()}");
        var storeStep = storeRecipe.Steps.OfType<CompressStep>().Last();
        var storeBackend = StoreCompressionBackend.Instance;

        var compressed = await storeBackend.CompressAsync(fullStreamBytes, storeStep, cancellationToken);
        var restored = await storeBackend.DecompressAsync(compressed, storeStep, cancellationToken);
        var verification = BitExactVerifier.VerifyOrThrow(fullStreamBytes.Span, restored);

        PersistWinnerIfConfigured(recipeStore, fingerprint, fullStreamBytes.Length, verification.OriginalSha256, storeRecipe);

        return new TournamentExecutionResult(
            WinningRecipe: storeRecipe,
            CompressedBytes: compressed,
            Verification: verification,
            PilotWinner: null,
            Trials: trials,
            StopReason: stopReason,
            UsedStoreFallback: true,
            PilotSampleBytesEvaluated: pilotSampleBytes,
            RungsExecuted: rungsExecuted);
    }

    private static void PersistWinnerIfConfigured(
        IRecipeStore? recipeStore,
        FileFingerprint fingerprint,
        long streamLength,
        string sha256Hex,
        PipelineRecipe winningRecipe)
    {
        if (recipeStore is null)
        {
            return;
        }

        var effectiveFingerprint = string.IsNullOrWhiteSpace(fingerprint.ContentHashSha256) || fingerprint.SizeBytes != streamLength
            ? fingerprint with
            {
                ContentHashSha256 = string.IsNullOrWhiteSpace(fingerprint.ContentHashSha256) ? sha256Hex : fingerprint.ContentHashSha256,
                SizeBytes = streamLength,
            }
            : fingerprint;

        recipeStore.EnsureInitialized();
        recipeStore.SaveWinningCache(effectiveFingerprint, winningRecipe);
    }

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
}

public sealed record TournamentResult(
    ArmTrialResult? Winner,
    IReadOnlyList<ArmTrialResult> Trials,
    TournamentStopReason StopReason);
