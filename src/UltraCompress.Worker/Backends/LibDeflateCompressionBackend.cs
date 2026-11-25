using System.Buffers;
using System.IO.Compression;
using LibDeflate;
using UltraCompress.Core;

namespace UltraCompress.Worker.Backends;

/// <summary>
/// Raw DEFLATE via <see href="https://www.nuget.org/packages/LibDeflate.NET">LibDeflate.NET</see> (bundled native libdeflate).
/// </summary>
public sealed class LibDeflateCompressionBackend : ICompressionBackend
{
    private const int MaxDecompressCapacityBytes = 256 * 1024 * 1024;

    public string CodecName => "libdeflate";

    public Task<byte[]> CompressAsync(
        ReadOnlyMemory<byte> input,
        CompressStep step,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var level = Math.Clamp(step.Level, 0, 12);
        using var compressor = new DeflateCompressor(level);
        using IMemoryOwner<byte> compressedOwner = compressor.Compress(input.Span, true)
            ?? throw new InvalidOperationException("libdeflate compression failed.");
        return Task.FromResult(compressedOwner.Memory.ToArray());
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
        using var decompressor = new DeflateDecompressor();
        var capacity = Math.Max(compressed.Length * 4, 256);
        while (capacity <= MaxDecompressCapacityBytes)
        {
            var buffer = new byte[capacity];
            var status = decompressor.Decompress(compressed, buffer, out var bytesWritten, out _);
            if (status == OperationStatus.Done)
            {
                return buffer[..bytesWritten].ToArray();
            }

            if (status is OperationStatus.InvalidData)
            {
                throw new InvalidDataException("libdeflate decompression failed: invalid DEFLATE stream.");
            }

            capacity *= 2;
        }

        throw new InvalidDataException("libdeflate decompression failed or exceeded size cap.");
    }
}