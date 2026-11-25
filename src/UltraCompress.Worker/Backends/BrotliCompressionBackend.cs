using System.IO.Compression;
using UltraCompress.Core;

namespace UltraCompress.Worker.Backends;

/// <summary>
/// In-process brotli via <see cref="BrotliEncoder"/> / <see cref="BrotliDecoder"/> (.NET runtime).
/// </summary>
public sealed class BrotliCompressionBackend : ICompressionBackend
{
    private const int DefaultWindowBits = 22;
    private const int MaxDecompressCapacityBytes = 256 * 1024 * 1024;

    public string CodecName => "brotli";

    public Task<byte[]> CompressAsync(
        ReadOnlyMemory<byte> input,
        CompressStep step,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var quality = Math.Clamp(step.Level, 0, 11);
        var window = ClampWindow(step.DictSizeKb);

        Span<byte> destination = new byte[BrotliEncoder.GetMaxCompressedLength(input.Length)];
        if (!BrotliEncoder.TryCompress(input.Span, destination, out var written, quality, window))
        {
            throw new InvalidOperationException("Brotli compression failed.");
        }

        return Task.FromResult(destination[..written].ToArray());
    }

    public Task<byte[]> DecompressAsync(
        ReadOnlyMemory<byte> compressed,
        CompressStep step,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Decompress(compressed.Span));
    }

    private static byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        var capacity = Math.Max(compressed.Length * 4, 256);
        while (capacity <= MaxDecompressCapacityBytes)
        {
            var buffer = new byte[capacity];
            if (BrotliDecoder.TryDecompress(compressed, buffer, out var written))
            {
                return buffer[..written];
            }

            capacity *= 2;
        }

        throw new InvalidDataException("Brotli decompression failed or exceeded size cap.");
    }

    private static int ClampWindow(int? dictSizeKb)
    {
        if (dictSizeKb is null or <= 0)
        {
            return DefaultWindowBits;
        }

        // Brotli window is in bits (10–24); treat recipe dict size as an approximate hint.
        var bits = (int)Math.Round(Math.Log2(dictSizeKb.Value * 1024.0));
        return Math.Clamp(bits, 10, 24);
    }
}