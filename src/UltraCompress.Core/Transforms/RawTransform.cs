namespace UltraCompress.Core.Transforms;

/// <summary>
/// Identity lossless transform with standard UCTF framing.
/// </summary>
public sealed class RawTransform : ILosslessTransform
{
    public static readonly RawTransform Instance = new();

    public TransformKind Kind => TransformKind.Raw;

    public string Name => "raw";

    public byte[] Encode(ReadOnlySpan<byte> input)
    {
        var output = new byte[TransformFrameHeader.HeaderSizeBytes + input.Length];
        var header = new TransformFrameHeader(TransformKind.Raw, Parameter: 0, OriginalLength: input.Length);
        header.WriteTo(output);
        input.CopyTo(output.AsSpan(TransformFrameHeader.HeaderSizeBytes));
        return output;
    }

    public byte[] Decode(ReadOnlySpan<byte> encodedFrame)
    {
        var header = TransformFrameHeader.ReadAndValidate(encodedFrame, TransformKind.Raw);
        if (header.OriginalLength == 0)
        {
            return Array.Empty<byte>();
        }

        return encodedFrame.Slice(TransformFrameHeader.HeaderSizeBytes, (int)header.OriginalLength).ToArray();
    }
}
