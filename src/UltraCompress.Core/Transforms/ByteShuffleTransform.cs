namespace UltraCompress.Core.Transforms;

/// <summary>
/// 100% reversible columnar byte transposition filter for 4-byte and 8-byte numeric, scientific, and tabular records.
/// Groups byte lanes across fixed-width records so identical MSBs, exponents, and zero padding form long RLE/LZ77 runs.
/// </summary>
public sealed class ByteShuffleTransform : ILosslessTransform
{
    public static readonly IReadOnlyList<int> SupportedStrides = [4, 8];

    public ByteShuffleTransform(int stride = 4)
    {
        ValidateStride(stride);
        Stride = stride;
    }

    public int Stride { get; }

    public TransformKind Kind => TransformKind.ByteShuffle;

    public string Name => "byteshuffle";

    public static bool IsSupportedStride(int stride) =>
        stride is 4 or 8;

    public byte[] Encode(ReadOnlySpan<byte> input)
    {
        var output = new byte[TransformFrameHeader.HeaderSizeBytes + input.Length];
        var header = new TransformFrameHeader(TransformKind.ByteShuffle, (byte)Stride, input.Length);
        header.WriteTo(output);

        if (input.IsEmpty)
        {
            return output;
        }

        var payload = output.AsSpan(TransformFrameHeader.HeaderSizeBytes, input.Length);
        var stride = Stride;
        var recordCount = input.Length / stride;
        var transposedBytes = recordCount * stride;

        for (var lane = 0; lane < stride; lane++)
        {
            var dstOffset = lane * recordCount;
            var srcIndex = lane;
            for (var record = 0; record < recordCount; record++)
            {
                payload[dstOffset + record] = input[srcIndex];
                srcIndex += stride;
            }
        }

        if (transposedBytes < input.Length)
        {
            input[transposedBytes..].CopyTo(payload[transposedBytes..]);
        }

        return output;
    }

    public byte[] Decode(ReadOnlySpan<byte> encodedFrame)
    {
        var header = TransformFrameHeader.ReadAndValidate(encodedFrame, TransformKind.ByteShuffle);
        var stride = (int)header.Parameter;
        ValidateStride(stride, nameof(encodedFrame));

        var length = checked((int)header.OriginalLength);
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        var payload = encodedFrame.Slice(TransformFrameHeader.HeaderSizeBytes, length);
        var output = new byte[length];
        var recordCount = length / stride;
        var transposedBytes = recordCount * stride;

        for (var lane = 0; lane < stride; lane++)
        {
            var srcOffset = lane * recordCount;
            var dstIndex = lane;
            for (var record = 0; record < recordCount; record++)
            {
                output[dstIndex] = payload[srcOffset + record];
                dstIndex += stride;
            }
        }

        if (transposedBytes < length)
        {
            payload[transposedBytes..].CopyTo(output.AsSpan(transposedBytes));
        }

        return output;
    }

    /// <summary>
    /// Compares lag-4 vs lag-8 byte-lane adjacency correlation to select stride 4 or 8 for numeric/tabular payloads.
    /// </summary>
    public static int SelectBestStride(ReadOnlySpan<byte> sample)
    {
        if (sample.Length < 32)
        {
            return 4;
        }

        var windowLen = Math.Min(sample.Length, 32 * 1024);
        var slice = sample[..windowLen];

        var matches4 = CountEqualAtLag(slice, 4);
        var matches8 = CountEqualAtLag(slice, 8);

        // Prefer 8-byte stride when lag-8 correlation strictly beats lag-4 (e.g. float64/int64 columns).
        return matches8 > matches4 * 1.05 ? 8 : 4;
    }

    private static double CountEqualAtLag(ReadOnlySpan<byte> data, int lag)
    {
        if (data.Length <= lag)
        {
            return 0;
        }

        var equal = 0;
        for (var i = lag; i < data.Length; i++)
        {
            if (data[i] == data[i - lag])
            {
                equal++;
            }
        }

        return (double)equal / (data.Length - lag);
    }

    private static void ValidateStride(int stride, string paramName = "stride")
    {
        if (!IsSupportedStride(stride))
        {
            throw new ArgumentOutOfRangeException(paramName, stride, "ByteShuffleTransform stride must be 4 or 8.");
        }
    }
}
