using System.Security.Cryptography;
using UltraCompress.Core.Intelligence;
using UltraCompress.Core.Safety;
using UltraCompress.Worker;

namespace UltraCompress.Intelligence.Tests;

public sealed class WorkerAnalyzeCliTests : IDisposable
{
    private readonly string _tempDir;

    public WorkerAnalyzeCliTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "uc-intel-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeCli_ExplainOnMp4File_OutputsHumanReadableRationaleAndRejectedAlternatives()
    {
        var mp4Path = Path.Combine(_tempDir, "sample.mp4");
        var payload = new byte[128 * 1024];
        VirtualLargeWorkloadStream.FillHighEntropyPseudoRandom(0, payload);
        MagicSignatureDetectionTests.BuildFtyp("isom", "mp42").CopyTo(payload, 0);
        await File.WriteAllBytesAsync(mp4Path, payload);

        var (exitCode, output, stderr) = await WorkerCli.InvokeCapturedAsync(
            "analyze", mp4Path, "--explain", "--profile", "Tiny");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("UltraCompress Intelligent Strategy Decision Report", output);
        Assert.Contains("Store / FastStreamBypass", output);
        Assert.Contains("LosslessCompression", output);
        Assert.Contains("Multi-Window O(1) Samples:", output);
        Assert.Contains("[REJECTED]", output);
        Assert.Contains("<0.2%", output);
    }

    [Fact]
    public async Task AnalyzeCli_JsonOnHeterogeneousPhotoDirectory_RoundtripsStrategyDecisionReport()
    {
        var photoDir = Path.Combine(_tempDir, "photos");
        Directory.CreateDirectory(photoDir);

        var jpegBytes = new byte[64 * 1024];
        VirtualLargeWorkloadStream.FillHighEntropyPseudoRandom(1024, jpegBytes);
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x10, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 }.CopyTo(jpegBytes, 0);
        await File.WriteAllBytesAsync(Path.Combine(photoDir, "IMG_01.jpg"), jpegBytes);
        await File.WriteAllBytesAsync(Path.Combine(photoDir, "IMG_01_dup.jpg"), jpegBytes);

        var bmpBytes = new byte[96 * 1024];
        VirtualLargeWorkloadStream.FillUncompressedSensorGradient(0, bmpBytes, 3);
        MagicSignatureDetectionTests.BuildBmpHeader(160, 200, 24).CopyTo(bmpBytes, 0);
        await File.WriteAllBytesAsync(Path.Combine(photoDir, "MASTER_01.bmp"), bmpBytes);

        var (exitCode, json, stderr) = await WorkerCli.InvokeCapturedAsync(
            "analyze", "--input", photoDir, "--profile", "Archive", "--json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        var report = StrategyReportFormatter.FromJson(json);

        Assert.NotNull(report);
        Assert.True(report!.IsDirectory);
        Assert.Equal(OperationKind.LosslessCompression, report.OperationKind);
        Assert.Equal(WorkloadArchetype.HeterogeneousPhotoCollection, report.WorkloadArchetype);
        Assert.Equal(3, report.TotalFiles);
        Assert.Equal(1, report.DuplicateFileCount);
        Assert.Contains(report.Routes, r => r.Codec == "store");
        Assert.Contains(report.Routes, r => r.Codec == "zstd");
    }

    [Fact]
    public async Task CompressCli_ExplainOnMp4_ExecutesBitExactStoreBypassRoundtrip()
    {
        var mp4Path = Path.Combine(_tempDir, "clip.mp4");
        var outPath = Path.Combine(_tempDir, "clip.mp4.uc.raw");
        var payload = new byte[48 * 1024];
        VirtualLargeWorkloadStream.FillHighEntropyPseudoRandom(42, payload);
        MagicSignatureDetectionTests.BuildFtyp("mp42").CopyTo(payload, 0);
        await File.WriteAllBytesAsync(mp4Path, payload);

        var (exitCode, output, stderr) = await WorkerCli.InvokeCapturedAsync(
            "compress", "--input", mp4Path, "--output", outPath, "--profile", "Archive", "--explain");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.True(File.Exists(outPath));
        var storedBytes = await File.ReadAllBytesAsync(outPath);
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(storedBytes));
        Assert.Contains("Store / FastStreamBypass", output);
    }

    [Fact]
    public async Task AnalyzeCli_MissingTarget_ReturnsNonZeroWithRemediation()
    {
        var missingPath = Path.Combine(_tempDir, "does-not-exist.sqlite");
        var (exitCode, _, stderr) = await WorkerCli.InvokeCapturedAsync("analyze", missingPath);

        Assert.Equal(3, exitCode);
        Assert.Contains("UC-ERR-IO-001", stderr);
        Assert.Contains("Remediation:", stderr);
    }
}
