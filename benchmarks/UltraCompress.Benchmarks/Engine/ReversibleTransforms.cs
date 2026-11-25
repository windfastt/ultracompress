using System.Buffers.Binary;
using UltraCompress.Core;

namespace UltraCompress.Benchmarks.Engine;

public enum TransformKind : byte
{
    None = 0,
    Delta = 1,
    ByteShuffle3Delta = 2,
    ByteShuffle4Delta = 3,
    ByteShuffle8Delta = 4,
    X86Bcj = 5,
    ByteShuffle16Delta = 6,
    ByteShuffle32Delta = 7,
}

/// <summary>
/// Strictly bijective (100% lossless on all 2^(8N) inputs of any length) preprocessors:
/// - ByteShuffle(stride) + byte-lane Delta for numeric databases and uncompressed RGB/RGBA rasters
/// - X86Bcj (E8/E9 rel32 <-> abs32 modulo 2^32) for PE/ELF x86-64 machine code
/// - First-order byte Delta
/// </summary>
public static class ReversibleTransforms
{
    public static byte[] ApplyForward(ReadOnlySpan<byte> input, TransformKind kind)
    {
        if (input.IsEmpty || kind == TransformKind.None)
        {
            return input.ToArray();
        }

        return kind switch
        {
            TransformKind.Delta => DeltaEncode(input),
            TransformKind.ByteShuffle3Delta => DeltaEncode(ByteShuffle(input, 3)),
            TransformKind.ByteShuffle4Delta => DeltaEncode(ByteShuffle(input, 4)),
            TransformKind.ByteShuffle8Delta => DeltaEncode(ByteShuffle(input, 8)),
            TransformKind.ByteShuffle16Delta => DeltaEncode(ByteShuffle(input, 16)),
            TransformKind.ByteShuffle32Delta => DeltaEncode(ByteShuffle(input, 32)),
            TransformKind.X86Bcj => X86BcjForward(input),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported transform kind."),
        };
    }

    public static byte[] ApplyInverse(ReadOnlySpan<byte> transformed, TransformKind kind)
    {
        if (transformed.IsEmpty || kind == TransformKind.None)
        {
            return transformed.ToArray();
        }

        return kind switch
        {
            TransformKind.Delta => DeltaDecode(transformed),
            TransformKind.ByteShuffle3Delta => ByteUnshuffle(DeltaDecode(transformed), 3),
            TransformKind.ByteShuffle4Delta => ByteUnshuffle(DeltaDecode(transformed), 4),
            TransformKind.ByteShuffle8Delta => ByteUnshuffle(DeltaDecode(transformed), 8),
            TransformKind.ByteShuffle16Delta => ByteUnshuffle(DeltaDecode(transformed), 16),
            TransformKind.ByteShuffle32Delta => ByteUnshuffle(DeltaDecode(transformed), 32),
            TransformKind.X86Bcj => X86BcjInverse(transformed),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported transform kind."),
        };
    }

    public static byte[] ByteShuffle(ReadOnlySpan<byte> input, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 2);
        if (input.Length < stride * 2)
        {
            return input.ToArray();
        }

        byte[] output = new byte[input.Length];
        int recordCount = input.Length / stride;
        int mainLen = recordCount * stride;

        for (int r = 0; r < recordCount; r++)
        {
            int srcBase = r * stride;
            for (int lane = 0; lane < stride; lane++)
            {
                output[(lane * recordCount) + r] = input[srcBase + lane];
            }
        }

        if (mainLen < input.Length)
        {
            input[mainLen..].CopyTo(output.AsSpan(mainLen));
        }

        return output;
    }

    public static byte[] ByteUnshuffle(ReadOnlySpan<byte> shuffled, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 2);
        if (shuffled.Length < stride * 2)
        {
            return shuffled.ToArray();
        }

        byte[] output = new byte[shuffled.Length];
        int recordCount = shuffled.Length / stride;
        int mainLen = recordCount * stride;

        for (int lane = 0; lane < stride; lane++)
        {
            int srcBase = lane * recordCount;
            for (int r = 0; r < recordCount; r++)
            {
                output[(r * stride) + lane] = shuffled[srcBase + r];
            }
        }

        if (mainLen < shuffled.Length)
        {
            shuffled[mainLen..].CopyTo(output.AsSpan(mainLen));
        }

        return output;
    }

    public static byte[] DeltaEncode(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        byte[] output = new byte[input.Length];
        output[0] = input[0];
        for (int i = 1; i < input.Length; i++)
        {
            output[i] = unchecked((byte)(input[i] - input[i - 1]));
        }

        return output;
    }

    public static byte[] DeltaDecode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        byte[] output = new byte[encoded.Length];
        output[0] = encoded[0];
        for (int i = 1; i < encoded.Length; i++)
        {
            output[i] = unchecked((byte)(output[i - 1] + encoded[i]));
        }

        return output;
    }

    public static byte[] X86BcjForward(ReadOnlySpan<byte> input)
    {
        byte[] output = input.ToArray();
        Span<byte> span = output.AsSpan();
        int limit = span.Length - 5;

        for (int i = 0; i <= limit; i++)
        {
            byte op = span[i];
            if (op is 0xE8 or 0xE9)
            {
                uint rel = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i + 1, 4));
                uint abs = unchecked(rel + (uint)(i + 5));
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(i + 1, 4), abs);
                i += 4;
            }
        }

        return output;
    }

    public static byte[] X86BcjInverse(ReadOnlySpan<byte> transformed)
    {
        byte[] output = transformed.ToArray();
        Span<byte> span = output.AsSpan();
        int limit = span.Length - 5;

        for (int i = 0; i <= limit; i++)
        {
            byte op = span[i];
            if (op is 0xE8 or 0xE9)
            {
                uint abs = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i + 1, 4));
                uint rel = unchecked(abs - (uint)(i + 5));
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(i + 1, 4), rel);
                i += 4;
            }
        }

        return output;
    }

    /// <summary>
    /// O(1)-memory sample probe detecting whether 3-, 4-, or 8-byte lane-delta transpose
    /// meaningfully lowers zero-order entropy relative to raw bytes.
    /// </summary>
    public static TransformKind DetectBestNumericOrRasterTransform(ReadOnlySpan<byte> data)
    {
        if (data.Length < 512)
        {
            return TransformKind.None;
        }

        // Skip common container headers (e.g. 54-byte BMP or 128-byte TIFF/page header) when sampling middle slice.
        int offset = Math.Min(256, data.Length / 8);
        int sampleLen = Math.Min(32768, data.Length - offset);
        ReadOnlySpan<byte> sample = data.Slice(offset, sampleLen);

        double rawEntropy = EntropyCalculator.ShannonBitsPerByte(sample);
        double bestEntropy = rawEntropy - 0.35; // Require meaningful entropy reduction (>0.35 bpb)
        TransformKind best = TransformKind.None;

        ReadOnlySpan<(TransformKind Kind, int Stride)> candidates =
        [
            (TransformKind.ByteShuffle8Delta, 8),
            (TransformKind.ByteShuffle4Delta, 4),
            (TransformKind.ByteShuffle3Delta, 3),
            (TransformKind.ByteShuffle16Delta, 16),
            (TransformKind.ByteShuffle32Delta, 32),
        ];

        foreach (var (kind, stride) in candidates)
        {
            var transformed = ApplyForward(sample, kind);
            double e = EntropyCalculator.ShannonBitsPerByte(transformed);
            if (e < bestEntropy)
            {
                bestEntropy = e;
                best = kind;
            }
        }

        return best;
    }
}
