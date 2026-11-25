using System.Buffers;

namespace UltraCompress.Core.Transforms;

/// <summary>
/// 100% reversible byte-wise delta preprocessor supporting strides 1, 2, 4, and 8.
/// Effective on PCM audio, uncompressed raster scanlines (BMP/TIFF/RAW), timestamps, and numeric series.
/// </summary>
public sealed class DeltaTransform : ILosslessTransform
{
    public static readonly IReadOnlyList<int> SupportedStrides = [1, 2, 4, 8];

    public DeltaTransform(int stride = 4)
    {
        ValidateStride(stride);
        Stride = stride;
    }

    public int Stride { get; }

    public TransformKind Kind => TransformKind.Delta;

    public string Name => "delta";

    public static bool IsSupportedStride(int stride) =>
        stride is 1 or 2 or 4 or 8;

    public byte[] Encode(ReadOnlySpan<byte> input)
    {
        var output = new byte[TransformFrameHeader.HeaderSizeBytes + input.Length];
        var header = new TransformFrameHeader(TransformKind.Delta, (byte)Stride, input.Length);
        header.WriteTo(output);

        if (input.IsEmpty)
        {
            return output;
        }

        var payload = output.AsSpan(TransformFrameHeader.HeaderSizeBytes, input.Length);
        var stride = Stride;
        var prefix = Math.Min(stride, input.Length);

        input[..prefix].CopyTo(payload[..prefix]);

        for (var i = prefix; i < input.Length; i++)
        {
            payload[i] = unchecked((byte)(input[i] - input[i - stride]));
        }

        return output;
    }

    public byte[] Decode(ReadOnlySpan<byte> encodedFrame)
    {
        var header = TransformFrameHeader.ReadAndValidate(encodedFrame, TransformKind.Delta);
        var stride = (int)header.Parameter;
        ValidateStride(stride, nameof(encodedFrame));

        var length = checked((int)header.OriginalLength);
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        var payload = encodedFrame.Slice(TransformFrameHeader.HeaderSizeBytes, length);
        var output = new byte[length];
        var prefix = Math.Min(stride, length);

        payload[..prefix].CopyTo(output.AsSpan(0, prefix));

        for (var i = prefix; i < length; i++)
        {
            output[i] = unchecked((byte)(payload[i] + output[i - stride]));
        }

        return output;
    }

    /// <summary>
    /// Evaluates strides {1, 2, 4, 8} on a sample slice and returns the stride yielding the lowest post-delta Shannon entropy.
    /// </summary>
    public static (int BestStride, double BestEntropyBitsPerByte) SelectBestStride(ReadOnlySpan<byte> sample)
    {
        if (sample.Length < 16)
        {
            return (4, sample.IsEmpty ? 0 : 8.0);
        }

        var windowLen = Math.Min(sample.Length, 32 * 1024);
        var slice = sample[..windowLen];
        var scratch = ArrayPool<byte>.Shared.Rent(windowLen);

        try
        {
            var bestStride = 4;
            var bestEntropy = double.PositiveInfinity;

            foreach (var stride in SupportedStrides)
            {
                var prefix = Math.Min(stride, windowLen);
                slice[..prefix].CopyTo(scratch.AsSpan(0, prefix));
                for (var i = prefix; i < windowLen; i++)
                {
                    scratch[i] = unchecked((byte)(slice[i] - slice[i - stride]));
                }

                var entropy = EntropyCalculator.ShannonBitsPerByte(scratch, 0, windowLen);
                if (entropy < bestEntropy - 1e-6)
                {
                    bestEntropy = entropy;
                    bestStride = stride;
                }
            }

            return (bestStride, bestEntropy);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static void ValidateStride(int stride, string paramName = "stride")
    {
        if (!IsSupportedStride(stride))
        {
            throw new ArgumentOutOfRangeException(paramName, stride, "DeltaTransform stride must be 1, 2, 4, or 8.");
        }
    }
}
