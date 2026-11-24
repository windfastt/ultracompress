using UltraCompress.Core;
using ZstdNet;

namespace UltraCompress.Worker.Backends;

/// <summary>
/// In-process zstd via <see href="https://www.nuget.org/packages/ZstdNet">ZstdNet</see> (bundled libzstd on Windows).
/// </summary>
public sealed class ZstdCompressionBackend : ICompressionBackend
{
    public string CodecName => "zstd";

    public Task<byte[]> CompressAsync(
        ReadOnlyMemory<byte> input,
        CompressStep step,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Avoid touching ZstdNet level bounds before native lib is loaded (ModuleInitializer).
        var level = Math.Clamp(step.Level, 1, 22);
        using var compressor = new Compressor(new CompressionOptions(level));
        var output = compressor.Wrap(input.Span).ToArray();
        return Task.FromResult(output);
    }

    public Task<byte[]> DecompressAsync(
        ReadOnlyMemory<byte> compressed,
        CompressStep step,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var decompressor = new Decompressor();
        var output = decompressor.Unwrap(compressed.Span).ToArray();
        return Task.FromResult(output);
    }
}