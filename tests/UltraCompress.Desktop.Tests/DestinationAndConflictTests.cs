using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.Services;
using UltraCompress.Desktop.Presentation.ViewModels;

namespace UltraCompress.Desktop.Tests;

public sealed class DestinationAndConflictTests : IDisposable
{
    private readonly string _tempDir;

    public DestinationAndConflictTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"uc-desktop-dest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ResolveTargetFilePath_RenameOverwriteSkip_FollowsConflictPolicy()
    {
        var existingArchive = Path.Combine(_tempDir, "Project.ucx");
        File.WriteAllBytes(existingArchive, [1, 2, 3]);
        var existingRenamed1 = Path.Combine(_tempDir, "Project (1).ucx");
        File.WriteAllBytes(existingRenamed1, [4, 5, 6]);

        var service = new DestinationManagerService(minimumSafetyHeadroomBytes: 1024);

        var renamed = service.ResolveTargetFilePath(
            _tempDir,
            "Project.ucx",
            DestinationConflictMode.Rename,
            out var renameConflict,
            out var renameSkip);
        Assert.True(renameConflict);
        Assert.False(renameSkip);
        Assert.Equal(Path.Combine(_tempDir, "Project (2).ucx"), renamed);

        var overwrite = service.ResolveTargetFilePath(
            _tempDir,
            "Project.ucx",
            DestinationConflictMode.Overwrite,
            out var overwriteConflict,
            out var overwriteSkip);
        Assert.True(overwriteConflict);
        Assert.False(overwriteSkip);
        Assert.Equal(existingArchive, overwrite);

        var skipped = service.ResolveTargetFilePath(
            _tempDir,
            "Project.ucx",
            DestinationConflictMode.Skip,
            out var skipConflict,
            out var willSkip);
        Assert.True(skipConflict);
        Assert.True(willSkip);
        Assert.Equal(existingArchive, skipped);
    }

    [Fact]
    public async Task CompressWorkspace_LowDiskSpace_BlocksExecutionWithActionableErrorCard()
    {
        var inputFile = Path.Combine(_tempDir, "payload.txt");
        await File.WriteAllTextAsync(inputFile, new string('A', 64 * 1024));

        var lowDiskMock = new FakeDiskSpaceProvider(availableBytes: 512, writable: true);
        var destinationManager = new DestinationManagerService(lowDiskMock, minimumSafetyHeadroomBytes: 4096);
        var vm = new CompressWorkspaceViewModel(destinationManager: destinationManager, defaultDestinationFolder: _tempDir);

        await vm.StageDroppedPathsAsync([inputFile]);

        Assert.False(vm.HasSufficientDiskSpace);
        Assert.False(vm.CanCompressBatch);
        Assert.Equal(PreCheckSeverity.Blocked, vm.DiskPreCheck.Severity);

        var ok = vm.TryValidateReadyForCompression(out _, out var error);
        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal(ActionableErrorCode.InsufficientDiskSpace, error!.Code);
        Assert.True(error.RemediationSteps.Count >= 2);
    }

    [Fact]
    public async Task CompressWorkspace_UnwritableDestination_ProducesPermissionErrorCard()
    {
        var inputFile = Path.Combine(_tempDir, "payload.txt");
        await File.WriteAllTextAsync(inputFile, "hello world");

        var unwritableMock = new FakeDiskSpaceProvider(availableBytes: 100_000_000, writable: false);
        var destinationManager = new DestinationManagerService(unwritableMock, minimumSafetyHeadroomBytes: 1024);
        var vm = new CompressWorkspaceViewModel(destinationManager: destinationManager, defaultDestinationFolder: _tempDir);

        await vm.StageDroppedPathsAsync([inputFile]);

        Assert.False(vm.TryValidateReadyForCompression(out _, out var error));
        Assert.NotNull(error);
        Assert.Equal(ActionableErrorCode.AccessDeniedPermission, error!.Code);
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
            // Ignore cleanup errors.
        }
    }

    private sealed class FakeDiskSpaceProvider(long availableBytes, bool writable) : IDiskSpaceProvider
    {
        public long? TryGetAvailableFreeBytes(string directoryPath) => availableBytes;

        public bool IsDirectoryWritable(string directoryPath) => writable;
    }
}
