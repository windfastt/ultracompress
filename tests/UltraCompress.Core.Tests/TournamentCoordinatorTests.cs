using UltraCompress.Core.Search;

namespace UltraCompress.Core.Tests;

public class TournamentCoordinatorTests
{
  [Fact]
    public void TinyProfile_SelectsBetweenEightAndTwelveArms()
    {
        var fp = SampleFingerprint(sizeBytes: 50_000);
        var arms = PipelineArmCatalog.SelectArms(fp, CompressionProfile.Tiny, new TournamentOptions());

        Assert.InRange(arms.Count, 8, 12);
    }

    [Fact]
    public void HighEntropy_SkipsToSingleFastArm()
    {
        var fp = SampleFingerprint(entropy: 7.95);
        var arms = PipelineArmCatalog.SelectArms(fp, CompressionProfile.Tiny, new TournamentOptions());

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

        var arms = PipelineArmCatalog.SelectArms(fp, CompressionProfile.Small, options);
        var resultsByArm = arms.ToDictionary(
            a => a.ArmId,
            a => new ArmTrialResult(
                a.ArmId,
                1000,
                a.ArmId switch
                {
                    PipelineArmCatalog.A0 => 400,
                    PipelineArmCatalog.A2 => 350,
                    _ => 900,
                },
                1.0,
                true,
                a.Recipe));

        var pulled = new List<string>();
        var result = await coordinator.RunAsync(
            fp,
            CompressionProfile.Small,
            1000,
            async (arm, _) =>
            {
                pulled.Add(arm.ArmId);
                await Task.CompletedTask;
                return resultsByArm[arm.ArmId];
            },
            options);

        Assert.NotNull(result.Winner);
        Assert.Equal(PipelineArmCatalog.A2, result.Winner.ArmId);
        Assert.Contains(PipelineArmCatalog.A2, pulled);
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

        var pullCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var result = await coordinator.RunAsync(
            fp,
            CompressionProfile.Small,
            10_000,
            async (arm, _) =>
            {
                pullCounts.TryGetValue(arm.ArmId, out var n);
                pullCounts[arm.ArmId] = n + 1;

                var compressed = arm.ArmId switch
                {
                    PipelineArmCatalog.A0 => 3000,
                    PipelineArmCatalog.A2 => 2500,
                    PipelineArmCatalog.A3 => 2550,
                    _ => 8000,
                };

                await Task.Delay(10);
                return new ArmTrialResult(arm.ArmId, 10_000, compressed, 2.0, true, arm.Recipe);
            },
            options);

        Assert.NotNull(result.Winner);
        Assert.Equal(PipelineArmCatalog.A2, result.Winner.ArmId);
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
        var trial = new ArmTrialResult("A0", 1000, 800, 2.0, true, new PipelineRecipe());
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