using System.Security.Cryptography;
using System.Text;
using UltraCompress.Core;

namespace UltraCompress.Core.Tests;

public class FileProbeTests
{
    [Fact]
    public async Task ProbeAsync_PopulatesHashEntropyAndType()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uc-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "sample.pdf");
        try
        {
            var content = Encoding.ASCII.GetBytes("%PDF-1.4\n" + new string('A', 400));
            await File.WriteAllBytesAsync(path, content);

            var fp = await FileProbe.ProbeAsync(path);

            Assert.Equal(path, fp.Path);
            Assert.Equal(content.Length, fp.SizeBytes);
            Assert.Equal(".pdf", fp.Extension);
            Assert.Equal("application/pdf", fp.DetectedType);
            Assert.NotNull(fp.ContentHashSha256);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                fp.ContentHashSha256);
            Assert.NotNull(fp.EntropyBitsPerByte);
            Assert.True(fp.EntropyBitsPerByte < 1.5);
            Assert.NotEmpty(fp.BlockEntropies);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ProbeAsync_HighEntropy_FlagsLikelyCompressed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uc-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "random.bin");
        try
        {
            var content = RandomNumberGenerator.GetBytes(4096);
            await File.WriteAllBytesAsync(path, content);

            var fp = await FileProbe.ProbeAsync(path);

            Assert.NotNull(fp.EntropyBitsPerByte);
            Assert.InRange(fp.EntropyBitsPerByte!.Value, 7.5, 8.01);
            Assert.True(fp.LikelyAlreadyCompressed);
            Assert.Equal("application/octet-stream", fp.DetectedType);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ProbeAsync_LargeFile_UsesSampledBlockEntropy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uc-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "big.bin");
        try
        {
            await using (var fs = File.Create(path))
            {
                var block = new byte[64 * 1024];
                Array.Fill(block, (byte)0xCD);
                for (var i = 0; i < 300; i++)
                {
                    await fs.WriteAsync(block);
                }
            }

            var options = new FileProbeOptions
            {
                FullBlockScanMaxBytes = 1024 * 1024,
                MaxBlockSamples = 8,
                BlockSizeBytes = 64 * 1024,
            };

            var fp = await FileProbe.ProbeAsync(path, options);

            Assert.True(fp.SizeBytes > options.FullBlockScanMaxBytes);
            Assert.InRange(fp.BlockEntropies.Count, 1, options.MaxBlockSamples);
            Assert.NotNull(fp.ContentHashSha256);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}