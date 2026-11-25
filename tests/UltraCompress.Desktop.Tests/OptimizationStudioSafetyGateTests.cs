using System.Security.Cryptography;
using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.ViewModels;

namespace UltraCompress.Desktop.Tests;

public sealed class OptimizationStudioSafetyGateTests : IDisposable
{
    private readonly string _tempDir;

    public OptimizationStudioSafetyGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"uc-desktop-studio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task OptimizationStudio_EnforcesConsentGatesDefaultPreservationAndResetsOnModeChange()
    {
        var outDir = Path.Combine(_tempDir, "studio-out");
        var studio = new OptimizationStudioViewModel(outDir);

        Assert.True(studio.PreserveOriginalFile);
        Assert.NotEqual(OperationKind.LosslessCompression, studio.OperationKind);
        Assert.False(studio.CanExecuteOptimization);

        var blockedEmpty = await studio.ExecuteOptimizationAsync();
        Assert.False(blockedEmpty.Success);
        Assert.Equal(ActionableErrorCode.MutatingConsentMissing, studio.LastErrorCard?.Code);

        var photoPath = Path.Combine(_tempDir, "photo-with-exif.jpg");
        var fakeJpegWithApp1Exif = new byte[]
        {
            0xFF, 0xD8,
            0xFF, 0xE1, 0x00, 0x08, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
            0xFF, 0xDB, 0x11, 0x22, 0x33, 0x44, 0xFF, 0xD9,
        };
        await File.WriteAllBytesAsync(photoPath, fakeJpegWithApp1Exif);
        var originalHash = SHA256.HashData(fakeJpegWithApp1Exif);

        studio.StageFiles([photoPath]);
        Assert.True(studio.HasImpactPreview);
        Assert.NotEmpty(studio.ItemizedPreviewLines);
        Assert.False(studio.CanExecuteOptimization);

        studio.ConsentUnderstandNonBitExact = true;
        Assert.False(studio.CanExecuteOptimization);

        studio.ConsentApproveItemizedPreview = true;
        Assert.True(studio.CanExecuteOptimization);

        studio.SelectedSubMode = OptimizationSubMode.DataRemoval;
        Assert.Equal(OperationKind.DataRemoval, studio.OperationKind);
        Assert.False(studio.ConsentUnderstandNonBitExact);
        Assert.False(studio.ConsentApproveItemizedPreview);
        Assert.False(studio.CanExecuteOptimization);

        studio.PreserveOriginalFile = false;
        studio.ConsentUnderstandNonBitExact = true;
        studio.ConsentApproveItemizedPreview = true;
        Assert.True(studio.RequiresDestructiveReplaceConsent);
        Assert.False(studio.CanExecuteOptimization);

        var blockedDestructive = await studio.ExecuteOptimizationAsync();
        Assert.False(blockedDestructive.Success);

        studio.PreserveOriginalFile = true;
        studio.ConsentUnderstandNonBitExact = true;
        studio.ConsentApproveItemizedPreview = true;
        Assert.True(studio.CanExecuteOptimization);

        var executed = await studio.ExecuteOptimizationAsync();
        Assert.True(executed.Success);
        var sanitizedPath = Assert.Single(executed.WrittenOutputPaths);
        Assert.True(File.Exists(sanitizedPath));
        Assert.NotEqual(Path.GetFullPath(photoPath), Path.GetFullPath(sanitizedPath));

        var afterSourceBytes = await File.ReadAllBytesAsync(photoPath);
        Assert.Equal(originalHash, SHA256.HashData(afterSourceBytes));

        var sanitizedBytes = await File.ReadAllBytesAsync(sanitizedPath);
        Assert.True(sanitizedBytes.Length < fakeJpegWithApp1Exif.Length);
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
}
