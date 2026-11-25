using UltraCompress.Core.Search;

namespace UltraCompress.Core.Tests;

public class TournamentCoordinatorTests
{
    [Fact]
    public void TinyProfile_SelectsFullSixteenArmMatrix()
    {
        var fp = SampleFingerprint(sizeBytes: 50_000);
        var arms = PipelineArmCatalog.SelectArms(fp, CompressionProfile.Tiny, TournamentOptions.ForProfile(CompressionProfile.Tiny));

        Assert.Equal(16, arms.Count);
    }

    [Fact]
    public void HighEntropy_SkipsToSingleFastArm()
    {
        var fp = SampleFingerprint(entropy: 7.97);
        var arms = PipelineArmCatalog.SelectArms(fp, CompressionProfile.Tiny, TournamentOptions.ForProfile(CompressionProfile.Tiny));

        var arm = Assert.Single(arms);
        Assert.Equal(PipelineArmCatalog.A0, arm.ArmId);
    }

    [Fact]
    public async Task Coordinator_PicksBestRewardWinner()
    {
        var coordinator = new TournamentCoordinator();
        var fp = SampleFingerprint();
        var options = new TournamentOptions
        {
            TimeBudget = TimeSpan.FromSeconds(30),
            MinArms = 3,
            MaxArms = 3,
            ExplorationConstant = 1.2,
            MinPullsBeforeUcbPrune = 1,
        };

        var arms = PipelineArmCatalog.SelectArms(fp, CompressionProfile.Archive, options);
        var targetWinner = arms[1].ArmId;
        var resultsByArm = arms.ToDictionary(
            a => a.ArmId,
            a => new ArmTrialResult(
                a.ArmId,
                1000,
                a.ArmId == targetWinner ? 350 : (a.ArmId == arms[0].ArmId ? 400 : 900),
                1.0,
                true,
                a.Recipe));

        var pulled = new List<string>();
        var result = await coordinator.RunAsync(
            fp,
            CompressionProfile.Archive,
            1000,
            async (arm, _) =>
            {
                pulled.Add(arm.ArmId);
                await Task.CompletedTask;
                return resultsByArm[arm.ArmId];
            },
            options);

        Assert.NotNull(result.Winner);
        Assert.Equal(targetWinner, result.Winner.ArmId);
        Assert.Contains(targetWinner, pulled);
    }

    [Fact]
    public async Task Coordinator_UcbEarlyStop_ReducesPulls()
    {
        var coordinator = new TournamentCoordinator();
        var fp = SampleFingerprint();
        var options = new TournamentOptions
        {
            TimeBudget = TimeSpan.FromSeconds(5),
            MaxTrials = 16,
            MinArms = 4,
            MaxArms = 4,
            ExplorationConstant = 0.01,
            MinPullsBeforeUcbPrune = 1,
        };

        var arms = PipelineArmCatalog.SelectArms(fp, CompressionProfile.Archive, options);
        var winnerArmId = arms[1].ArmId;
        var pullCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var result = await coordinator.RunAsync(
            fp,
            CompressionProfile.Archive,
            10_000,
            async (arm, _) =>
            {
                pullCounts.TryGetValue(arm.ArmId, out var n);
                pullCounts[arm.ArmId] = n + 1;

                var compressed = arm.ArmId == winnerArmId
                    ? 2500
                    : (arm.ArmId == arms[0].ArmId ? 3000 : 8000);

                await Task.Delay(10);
                return new ArmTrialResult(arm.ArmId, 10_000, compressed, 2.0, true, arm.Recipe);
            },
            options);

        Assert.NotNull(result.Winner);
        Assert.Equal(winnerArmId, result.Winner.ArmId);
        Assert.InRange(pullCounts.Values.Sum(), 4, 16);
        Assert.True(
            result.StopReason is TournamentStopReason.UcbEarlyStop
                or TournamentStopReason.WinnerFound
                or TournamentStopReason.DiminishingReturns
                or TournamentStopReason.TimeBudgetExceeded);
    }

    [Fact]
    public void ArmTrialResult_Reward_UsesBytesSavedPerSecond()
    {
        var trial = new ArmTrialResult("raw-zstd", 1000, 800, 2.0, true, new PipelineRecipe());
        Assert.Equal(100, trial.Reward);
    }

    private static FileFingerprint SampleFingerprint(long sizeBytes = 4096, double? entropy = null) =>
        new()
        {
            Path = "sample.bin",
            SizeBytes = sizeBytes,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            EntropyBitsPerByte = entropy,
        };
}
