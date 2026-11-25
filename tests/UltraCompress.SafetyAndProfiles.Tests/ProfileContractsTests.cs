using UltraCompress.Core;
using UltraCompress.Core.Safety;

namespace UltraCompress.SafetyAndProfiles.Tests;

public sealed class ProfileContractsTests
{
    [Fact]
    public void ProfileContract_DefinesConcreteInspectableContractsForAllFiveProfiles()
    {
        var profiles = Enum.GetValues<CompressionProfile>();
        Assert.Equal(5, ProfileContract.All.Count);

        foreach (var profile in profiles)
        {
            var contract = ProfileContract.Get(profile);
            contract.ValidateContractInvariants();

            Assert.Equal(profile, contract.Profile);
            Assert.Equal(OperationKind.LosslessCompression, contract.OperationKind);
            Assert.True(contract.MandatoryBitExactVerify);
            Assert.True(contract.VerifySha256);
            Assert.True(contract.VerifyXxHash64);
            Assert.Equal(0.995, contract.IncompressibleRatioThreshold, precision: 6);
            Assert.InRange(contract.IncompressibleEntropyBypassBitsPerByte, 7.0, 8.0);
            Assert.True(contract.MaxMemoryBudgetBytes >= 64L * 1024 * 1024);
            Assert.True(contract.CandidatePipelineBudget >= 1);
            Assert.True(contract.TargetThroughputFloorMiBPerSec > 0);
        }
    }

    [Fact]
    public void ProfileContracts_FollowMonotonicResourceAndSearchBudgets()
    {
        var instant = ProfileContract.Get(CompressionProfile.Instant);
        var balanced = ProfileContract.Get(CompressionProfile.Balanced);
        var archive = ProfileContract.Get(CompressionProfile.Archive);
        var tiny = ProfileContract.Get(CompressionProfile.Tiny);
        var lab = ProfileContract.Get(CompressionProfile.Laboratory);

        Assert.True(instant.IncompressibleEntropyBypassBitsPerByte < balanced.IncompressibleEntropyBypassBitsPerByte);
        Assert.True(balanced.IncompressibleEntropyBypassBitsPerByte < archive.IncompressibleEntropyBypassBitsPerByte);
        Assert.True(archive.IncompressibleEntropyBypassBitsPerByte < tiny.IncompressibleEntropyBypassBitsPerByte);
        Assert.True(tiny.IncompressibleEntropyBypassBitsPerByte < lab.IncompressibleEntropyBypassBitsPerByte);

        Assert.True(instant.MaxMemoryBudgetBytes < balanced.MaxMemoryBudgetBytes);
        Assert.True(balanced.MaxMemoryBudgetBytes < archive.MaxMemoryBudgetBytes);
        Assert.True(archive.MaxMemoryBudgetBytes < tiny.MaxMemoryBudgetBytes);
        Assert.True(tiny.MaxMemoryBudgetBytes < lab.MaxMemoryBudgetBytes);

        Assert.True(archive.CandidatePipelineBudget > balanced.CandidatePipelineBudget);
        Assert.True(tiny.CandidatePipelineBudget > archive.CandidatePipelineBudget);
        Assert.True(lab.CandidatePipelineBudget > tiny.CandidatePipelineBudget);

        Assert.True(tiny.PilotSampleBytes > archive.PilotSampleBytes);
        Assert.True(lab.PilotSampleBytes > tiny.PilotSampleBytes);

        Assert.False(instant.UsesMultiCandidateTournament);
        Assert.False(balanced.UsesMultiCandidateTournament);
        Assert.False(archive.UsesMultiCandidateTournament);
        Assert.True(tiny.UsesMultiCandidateTournament);
        Assert.True(lab.UsesMultiCandidateTournament);
    }

    [Fact]
    public void ProfileDefaults_AndStoreRecipes_ValidateAgainstJsonSchemaAndSafetyPolicy()
    {
        foreach (var profile in Enum.GetValues<CompressionProfile>())
        {
            var defaultRecipe = ProfileDefaults.CreateDefaultRecipe(profile);
            var storeRecipe = ProfileDefaults.CreateStoreRecipe(profile);

            Assert.True(RecipeSchemaValidation.ValidatePipelineRecipe(defaultRecipe).IsValid);
            Assert.True(RecipeSchemaValidation.ValidatePipelineRecipe(storeRecipe).IsValid);

            SafetyPolicy.ValidateCompressionProfileRecipe(profile, defaultRecipe);
            SafetyPolicy.ValidateCompressionProfileRecipe(profile, storeRecipe);
        }
    }
}
