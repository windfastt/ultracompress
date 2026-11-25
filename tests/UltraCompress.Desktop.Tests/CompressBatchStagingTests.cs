using System.Security.Cryptography;
using System.Text;
using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.ViewModels;

namespace UltraCompress.Desktop.Tests;

public sealed class CompressBatchStagingTests : IDisposable
{
    private readonly string _tempDir;

    public CompressBatchStagingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"uc-desktop-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task StageDroppedPaths_FilesAndNestedFolders_DetectsCategoriesEntropyAndDuplicates()
    {
        var repoDir = Path.Combine(_tempDir, "sample-repo");
        var subDir = Path.Combine(repoDir, "src");
        Directory.CreateDirectory(subDir);

        var sourceCode = string.Concat(Enumerable.Repeat("public sealed class EngineService { public int Execute() => 42; }\n", 120));
        var sourceFileA = Path.Combine(subDir, "EngineService.cs");
        var duplicateSourceB = Path.Combine(repoDir, "EngineService.Copy.cs");
        await File.WriteAllTextAsync(sourceFileA, sourceCode, Encoding.UTF8);
        await File.WriteAllTextAsync(duplicateSourceB, sourceCode, Encoding.UTF8);

        var mp4Path = Path.Combine(_tempDir, "clip.mp4");
        var randomMedia = new byte[16 * 1024];
        new Random(1337).NextBytes(randomMedia);
        randomMedia[0] = 0x00;
        randomMedia[1] = 0x00;
        randomMedia[2] = 0x00;
        randomMedia[3] = 0x18;
        randomMedia[4] = (byte)'f';
        randomMedia[5] = (byte)'t';
        randomMedia[6] = (byte)'y';
        randomMedia[7] = (byte)'p';
        await File.WriteAllBytesAsync(mp4Path, randomMedia);

        var sqlPath = Path.Combine(_tempDir, "dump.sql");
        var sqlContent = string.Concat(Enumerable.Repeat("INSERT INTO events(id, status, tenant_id) VALUES (1001, 'ACTIVE', 7);\n", 150));
        await File.WriteAllTextAsync(sqlPath, sqlContent, Encoding.UTF8);

        var destDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(destDir);

        var vm = new CompressWorkspaceViewModel(defaultDestinationFolder: destDir);
        await vm.StageDroppedPathsAsync([repoDir, mp4Path, sqlPath]);

        Assert.Equal(4, vm.StagedFiles.Count);
        Assert.Equal(1, vm.StrategyReport.DuplicateFilesCount);
        Assert.True(vm.StrategyReport.DuplicateReclaimableBytes > 0);
        Assert.Contains("1 exact duplicate(s) collapsed", vm.DuplicateBadgeText);

        var mediaItem = Assert.Single(vm.StagedFiles, f => f.FileName == "clip.mp4");
        Assert.Equal(FileContentCategory.AlreadyCompressedMedia, mediaItem.Category);
        Assert.True(mediaItem.ShannonEntropyBitsPerByte > 7.5);
        Assert.StartsWith("Store / FastStreamBypass", mediaItem.SelectedStrategy);

        var duplicateItem = Assert.Single(vm.StagedFiles, f => f.IsDuplicate);
        Assert.Contains("SHA-256 Dedup Reference", duplicateItem.SelectedStrategy);

        var sqlItem = Assert.Single(vm.StagedFiles, f => f.FileName == "dump.sql");
        Assert.Equal(FileContentCategory.DatabaseAndTabular, sqlItem.Category);
        Assert.Contains("ByteShuffle", sqlItem.SelectedStrategy);

        Assert.True(vm.CategoryBreakdowns.Count >= 3);
        Assert.True(vm.BeforeBarWidth > 0);
        Assert.True(vm.EstimatedAfterBarWidth > 0 && vm.EstimatedAfterBarWidth <= vm.BeforeBarWidth);
    }

    [Fact]
    public async Task LosslessProfileSelector_AllFiveProfiles_EnforceBitExactSlaAndUpdateEstimates()
    {
        var inputPath = Path.Combine(_tempDir, "notes.txt");
        await File.WriteAllTextAsync(
            inputPath,
            string.Concat(Enumerable.Repeat("Lossless content-aware archival test line with repeated tokens.\n", 300)));

        var vm = new CompressWorkspaceViewModel(defaultDestinationFolder: _tempDir);
        Assert.Equal(5, vm.Profiles.Count);
        Assert.Equal(
            [
                DesktopLosslessProfile.Instant,
                DesktopLosslessProfile.Balanced,
                DesktopLosslessProfile.Archive,
                DesktopLosslessProfile.Tiny,
                DesktopLosslessProfile.Laboratory,
            ],
            vm.Profiles.Select(p => p.Profile).ToArray());

        Assert.All(vm.Profiles, profile =>
        {
            Assert.True(profile.IsBitExactLossless);
            Assert.Equal("100% Bit-Exact Lossless", profile.SlaBadge);
            Assert.True(profile.ThroughputFloorMbPerSec > 0);
            Assert.True(profile.MemoryCapMb > 0);
        });

        await vm.StageDroppedPathsAsync([inputPath]);

        vm.SelectedProfile = DesktopLosslessProfile.Instant;
        var instantEstimated = vm.StrategyReport.EstimatedArchiveBytes;

        vm.SelectedProfile = DesktopLosslessProfile.Laboratory;
        var labEstimated = vm.StrategyReport.EstimatedArchiveBytes;

        Assert.True(labEstimated <= instantEstimated);
        Assert.Equal(OperationKind.LosslessCompression, vm.OperationKind);
    }

    [Fact]
    public async Task StageDroppedPaths_NonExistentPath_PopulatesActionableErrorCard()
    {
        var vm = new CompressWorkspaceViewModel(defaultDestinationFolder: _tempDir);
        var missingPath = Path.Combine(_tempDir, "does-not-exist.dat");

        await vm.StageDroppedPathsAsync([missingPath]);

        Assert.True(vm.HasError);
        Assert.NotNull(vm.LastErrorCard);
        Assert.Equal(ActionableErrorCode.SourceFileNotFound, vm.LastErrorCard!.Code);
        Assert.NotEmpty(vm.LastErrorCard.RemediationSteps);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup failures.
        }
    }
}
