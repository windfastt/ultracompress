using System.Buffers.Binary;
using System.Text;

namespace UltraCompress.Intelligence.Tests;

/// <summary>
/// Seekable, zero-allocation virtual stream synthesizing realistic multi-GiB (10 GiB+) containers
/// on demand while recording exact seek and read byte counts to prove O(1)-memory inspection.
/// </summary>
internal delegate void InteriorSpanFiller(long streamOffset, Span<byte> destination);

internal sealed class VirtualLargeWorkloadStream : Stream
{
    public const long TenGiB = 10L * 1024 * 1024 * 1024;

    private readonly long _length;
    private readonly byte[] _header;
    private readonly byte[] _footer;
    private readonly InteriorSpanFiller _fillInterior;
    private long _position;

    public VirtualLargeWorkloadStream(
        long length,
        byte[] header,
        InteriorSpanFiller fillInterior,
        byte[]? footer = null)
    {
        _length = length;
        _header = header;
        _fillInterior = fillInterior;
        _footer = footer ?? Array.Empty<byte>();
    }

    public long TotalBytesRead { get; private set; }

    public int SeekCount { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length || buffer.IsEmpty)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, _length - _position);
        var slice = buffer[..toRead];
        _fillInterior(_position, slice);

        // Overlay header bytes when window intersects [0, _header.Length)
        if (_position < _header.Length)
        {
            var headStart = (int)_position;
            var headCopy = Math.Min(_header.Length - headStart, toRead);
            _header.AsSpan(headStart, headCopy).CopyTo(slice[..headCopy]);
        }

        // Overlay footer bytes when window intersects [_length - _footer.Length, _length)
        var footerStartOffset = _length - _footer.Length;
        if (_footer.Length > 0 && _position + toRead > footerStartOffset)
        {
            var destStart = (int)Math.Max(0, footerStartOffset - _position);
            var footerSrcStart = (int)Math.Max(0, _position - footerStartOffset);
            var copyLen = Math.Min(_footer.Length - footerSrcStart, toRead - destStart);
            if (copyLen > 0)
            {
                _footer.AsSpan(footerSrcStart, copyLen).CopyTo(slice.Slice(destStart, copyLen));
            }
        }

        _position += toRead;
        TotalBytesRead += toRead;
        return toRead;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        SeekCount++;
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        _position = Math.Clamp(_position, 0, _length);
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Deterministic SplitMix64 high-entropy byte generator (~7.998 bpb, incompressible like H.264/HEVC/JPEG/encrypted payloads).
    /// </summary>
    public static void FillHighEntropyPseudoRandom(long streamOffset, Span<byte> destination)
    {
        int i = 0;
        long blockIdx = streamOffset >> 3;
        int subOffset = (int)(streamOffset & 7);

        if (subOffset != 0)
        {
            ulong v = SplitMix64((ulong)blockIdx++);
            Span<byte> tmp = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(tmp, v);
            var take = Math.Min(8 - subOffset, destination.Length);
            tmp.Slice(subOffset, take).CopyTo(destination[..take]);
            i += take;
        }

        while (i + 8 <= destination.Length)
        {
            ulong v = SplitMix64((ulong)blockIdx++);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(i, 8), v);
            i += 8;
        }

        if (i < destination.Length)
        {
            ulong v = SplitMix64((ulong)blockIdx);
            Span<byte> tmp = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(tmp, v);
            tmp[..(destination.Length - i)].CopyTo(destination[i..]);
        }
    }

    /// <summary>
    /// Synthesizes 8-byte binary database records (monotonic int64 key + correlated timestamp/metric)
    /// exhibiting strong 4/8-byte stride delta entropy reduction and B-tree page structure.
    /// </summary>
    public static void FillStructuredDatabasePages(long streamOffset, Span<byte> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            var pos = streamOffset + i;
            var pageOffset = pos & 4095;
            if (pageOffset < 16)
            {
                // B-tree leaf table page header
                destination[i] = pageOffset switch
                {
                    0 => 0x0D,
                    3 => 0x40,
                    _ => 0x00,
                };
                continue;
            }

            long recordIndex = pos >> 3;
            int lane = (int)(pos & 7);
            // Smoothly incrementing 64-bit record ID / epoch timestamp so upper 6 bytes repeat across 8-byte stride
            ulong recordWord = ((ulong)(1_700_000_000_000L + (recordIndex * 3))) ^ ((ulong)(recordIndex & 0x03) << 8);
            destination[i] = (byte)((recordWord >> (lane * 8)) & 0xFF);
        }
    }

    /// <summary>
    /// Synthesizes uncompressed 24-bit RGB / 16-bit Bayer smooth camera sensor gradients with strong pixel stride correlation.
    /// </summary>
    public static void FillUncompressedSensorGradient(long streamOffset, Span<byte> destination, int pixelStride = 4)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            var pos = streamOffset + i;
            var pixelIdx = pos / pixelStride;
            var channel = (int)(pos % pixelStride);
            var baseVal = (int)((pixelIdx / 2) & 0xFF);
            var noise = (int)(((pixelIdx * 17) + channel) & 0x03);
            destination[i] = unchecked((byte)(baseVal + (channel * 24) + noise));
        }
    }

    /// <summary>
    /// Synthesizes realistic C# / TypeScript / SQL source/dump lines repeating deterministically across any stream size.
    /// </summary>
    public static InteriorSpanFiller CreateRepeatingTextFiller(string templateBlock)
    {
        var pattern = Encoding.UTF8.GetBytes(templateBlock);
        return (offset, dest) =>
        {
            var pLen = pattern.Length;
            var srcIdx = (int)(offset % pLen);
            var written = 0;
            while (written < dest.Length)
            {
                var copy = Math.Min(pLen - srcIdx, dest.Length - written);
                pattern.AsSpan(srcIdx, copy).CopyTo(dest.Slice(written, copy));
                written += copy;
                srcIdx = 0;
            }
        };
    }

    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
