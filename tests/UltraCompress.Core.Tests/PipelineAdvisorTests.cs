using UltraCompress.Core;
using UltraCompress.Core.Advisor;
using UltraCompress.Core.Safety;

namespace UltraCompress.Core.Tests;

public class PipelineAdvisorTests
{
    private static FileFingerprint Fp(
        string path,
        long size = 50_000,
        double? entropy = null,
        string? detectedType = null) =>
        new()
        {
            Path = path,
            SizeBytes = size,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            EntropyBitsPerByte = entropy,
            DetectedType = detectedType,
        };

    [Fact]
    public void Plan_HighEntropy_UsesFastLosslessOnly()
    {
        var plan = PipelineAdvisor.Plan(Fp("data.bin", entropy: 7.96), CompressionProfile.Tiny);

        Assert.Equal("zstd", plan.PrimaryCodec.Codec);
        Assert.Equal(1, plan.PrimaryCodec.Level);
        Assert.Empty(plan.TournamentCandidates);
        Assert.Contains(plan.Decisions, d => d.RuleId == AdvisorRuleIds.EntropyGate);
        var step = Assert.Single(plan.PrimaryRecipe.Steps, s => s is PreprocessStep) as PreprocessStep;
        Assert.Equal("raw", step!.Name);
    }

    [Fact]
    public void Plan_PeExecutable_AddsReversibleX86BcjPreprocess()
    {
        var plan = PipelineAdvisor.Plan(Fp("engine.exe", entropy: 5.0), CompressionProfile.Archive);

        var preprocess = plan.PrimaryRecipe.Steps.OfType<PreprocessStep>().First();
        Assert.Equal("x86_bcj", preprocess.Name);
        Assert.Contains(plan.Decisions, d => d.RuleId == AdvisorRuleIds.ExtensionPe);
        SafetyPolicy.ValidateCompressionProfileRecipe(CompressionProfile.Archive, plan.PrimaryRecipe);
    }

    [Fact]
    public void Plan_HighEntropyMedia_SkipsStructuralPreprocess()
    {
        var plan = PipelineAdvisor.Plan(Fp("clip.png", entropy: 7.6), CompressionProfile.Archive);

        var preprocess = plan.PrimaryRecipe.Steps.OfType<PreprocessStep>().First();
        Assert.Equal("raw", preprocess.Name);
        Assert.Contains(plan.Decisions, d => d.RuleId == AdvisorRuleIds.EntropySkipReencode);
    }

    [Fact]
    public void Plan_DetectedMimeSqlite_UsesByteShufflePreprocess()
    {
        var plan = PipelineAdvisor.Plan(
            Fp("noext", entropy: 4.0, detectedType: "application/x-sqlite3"),
            CompressionProfile.Balanced);

        var preprocess = plan.PrimaryRecipe.Steps.OfType<PreprocessStep>().First();
        Assert.Equal("byteshuffle", preprocess.Name);
    }

    [Theory]
    [InlineData(CompressionProfile.Instant, 0)]
    [InlineData(CompressionProfile.Balanced, 0)]
    [InlineData(CompressionProfile.Archive, 3)]
    [InlineData(CompressionProfile.Tiny, 4)]
    public void Plan_TournamentCandidateCount_MatchesProfile(CompressionProfile profile, int expectedCandidates)
    {
        var plan = PipelineAdvisor.Plan(Fp("bundle.log", entropy: 4.5), profile);

        Assert.Equal(expectedCandidates, plan.TournamentCandidates.Count);
        if (expectedCandidates > 0)
        {
            Assert.True(plan.AlternateCodecs.Count >= expectedCandidates);
        }
    }

    [Fact]
    public void Plan_Csv_UsesByteShufflePreprocess()
    {
        var plan = PipelineAdvisor.Plan(Fp("metrics.csv", entropy: 3.0), CompressionProfile.Archive);
        var preprocess = plan.PrimaryRecipe.Steps.OfType<PreprocessStep>().First();
        Assert.Equal("byteshuffle", preprocess.Name);
    }

    [Fact]
    public void Plan_TinyFile_SkipsHeavyPipeline()
    {
        var plan = PipelineAdvisor.Plan(Fp("x.txt", size: 100), CompressionProfile.Laboratory);

        Assert.Equal(3, plan.PrimaryCodec.Level);
        Assert.Empty(plan.TournamentCandidates);
        Assert.Contains(plan.Decisions, d => d.RuleId == AdvisorRuleIds.TinyFileFast);
    }

    [Fact]
    public void SuggestPrimaryRecipe_MatchesPlanPrimary()
    {
        var fp = Fp("readme.md", entropy: 4.0);
        var direct = PipelineAdvisor.SuggestPrimaryRecipe(fp, CompressionProfile.Archive);
        var plan = PipelineAdvisor.Plan(fp, CompressionProfile.Archive);

        Assert.Equal(plan.PrimaryRecipe.Id, direct.Id);
        Assert.Equal(
            plan.PrimaryRecipe.Steps.OfType<CompressStep>().Last().Level,
            direct.Steps.OfType<CompressStep>().Last().Level);
    }
}
