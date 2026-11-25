using UltraCompress.Core;
using UltraCompress.Worker.Backends;

namespace UltraCompress.Worker.Tests;

public class BrotliRoundtripTests
{
    private static readonly byte[] SamplePayload = System.Text.Encoding.UTF8.GetBytes(
        string.Concat(Enumerable.Repeat("UltraCompress brotli roundtrip harness 0123456789 ", 64)));

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(11)]
    public async Task BrotliBackend_RoundtripsPayload(int quality)
    {
        var backend = new BrotliCompressionBackend();
        var step = new CompressStep { Codec = "brotli", Level = quality };

        var compressed = await backend.CompressAsync(SamplePayload, step);
        Assert.NotEmpty(compressed);
        Assert.InRange(compressed.Length, 1, SamplePayload.Length);

        var decompressed = await backend.DecompressAsync(compressed, step);
        Assert.Equal(SamplePayload, decompressed);
    }
}