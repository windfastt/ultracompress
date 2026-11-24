using UltraCompress.Core;
using UltraCompress.Worker.Backends;

namespace UltraCompress.Worker.Tests;

public class LibDeflateRoundtripTests
{
    private static readonly byte[] SamplePayload = System.Text.Encoding.UTF8.GetBytes(
        string.Concat(Enumerable.Repeat("UltraCompress libdeflate roundtrip harness 0123456789 ", 64)));

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    public async Task LibDeflateBackend_RoundtripsPayload(int level)
    {
        var backend = new LibDeflateCompressionBackend();
        var step = new CompressStep { Codec = "libdeflate", Level = level };

        var compressed = await backend.CompressAsync(SamplePayload, step);
        Assert.NotEmpty(compressed);
        Assert.InRange(compressed.Length, 1, SamplePayload.Length);

        var decompressed = await backend.DecompressAsync(compressed, step);
        Assert.Equal(SamplePayload, decompressed);
    }
}