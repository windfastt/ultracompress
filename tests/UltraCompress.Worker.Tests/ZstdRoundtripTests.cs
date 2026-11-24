using UltraCompress.Core;
using UltraCompress.Worker;
using UltraCompress.Worker.Backends;
using UltraCompress.Worker.Verification;

namespace UltraCompress.Worker.Tests;

public class ZstdRoundtripTests
{
    private static readonly byte[] SamplePayload = System.Text.Encoding.UTF8.GetBytes(
        string.Concat(Enumerable.Repeat("UltraCompress roundtrip harness 0123456789 ", 64)));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(19)]
  public async Task ZstdBackend_RoundtripsPayload(int level)
    {
        var backend = new ZstdCompressionBackend();
        var step = new CompressStep { Codec = "zstd", Level = level };

        var compressed = await backend.CompressAsync(SamplePayload, step);
        Assert.NotEmpty(compressed);
        Assert.InRange(compressed.Length, 1, SamplePayload.Length);

        var decompressed = await backend.DecompressAsync(compressed, step);
        Assert.Equal(SamplePayload, decompressed);
    }

    [Fact]
    public async Task CompressionJobRunner_VerifiesRoundtrip()
    {
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "uc-zstd-" + Guid.NewGuid().ToString("N"))).FullName;
        var inputPath = Path.Combine(tempDir, "sample.bin");
        await File.WriteAllBytesAsync(inputPath, SamplePayload);

        try
        {
            var runner = new CompressionJobRunner();
            var result = await runner.RunAsync(inputPath, CompressionProfile.Balanced, null, CancellationToken.None);

            Assert.True(result.Verification.Passed, result.Verification.Message);
            Assert.NotNull(result.Verification.OriginalSha256);
            Assert.True(File.Exists(result.OutputPath));
            Assert.InRange(result.CompressedBytes, 1, result.OriginalBytes);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RoundtripVerifier_FailsWhenBytesDiffer()
    {
        var verifier = new RoundtripVerifier();
        var options = new VerifyOptions { DecompressRoundtrip = true, HashOriginal = true };
        var result = verifier.Verify([1, 2, 3], [1, 2, 4], options);
        Assert.False(result.Passed);
    }
}