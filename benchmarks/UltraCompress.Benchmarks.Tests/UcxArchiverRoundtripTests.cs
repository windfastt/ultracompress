using UltraCompress.Benchmarks.Corpus;
using UltraCompress.Benchmarks.Engine;
using UltraCompress.Benchmarks.Models;

namespace UltraCompress.Benchmarks.Tests;

public sealed class UcxArchiverRoundtripTests
{
    [Theory]
    [InlineData(UltraCompressBenchmarkProfile.Instant)]
    [InlineData(UltraCompressBenchmarkProfile.Balanced)]
    [InlineData(UltraCompressBenchmarkProfile.Archive)]
    [InlineData(UltraCompressBenchmarkProfile.Tiny)]
    public async Task AllProfiles_RoundtripMixedWorkspaceBitExact(UltraCompressBenchmarkProfile profile)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"uc_ucx_rt_{Guid.NewGuid():N}");
        try
        {
            var manifest = CorpusBuilder.BuildCategory(tempRoot, CorpusBuilder.MixedWorkspaceCategory, BenchmarkScale.Smoke);
            string archivePath = Path.Combine(tempRoot, $"out_{profile}.ucx");
            string extractDir = Path.Combine(tempRoot, $"extract_{profile}");

            var archiver = new UcxBenchmarkArchiver();
            string summary = await archiver.CompressDirectoryAsync(manifest.DirectoryPath, archivePath, profile);
            Assert.NotEmpty(summary);
            Assert.True(File.Exists(archivePath));

            await archiver.ExtractDirectoryAsync(archivePath, extractDir);

            var (restoredSha, restoredBytes, restoredFiles) = DeterministicTarArchive.ComputeDirectoryManifest(extractDir);
            Assert.Equal(manifest.FileCount, restoredFiles);
            Assert.Equal(manifest.OriginalBytes, restoredBytes);
            Assert.Equal(manifest.ManifestSha256, restoredSha);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
