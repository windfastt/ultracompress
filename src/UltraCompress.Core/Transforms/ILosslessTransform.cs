namespace UltraCompress.Core.Transforms;

/// <summary>
/// Binary frame identifiers for reversible lossless preprocessors.
/// </summary>
public enum TransformKind : byte
{
    Raw = 0,
    Delta = 1,
    ByteShuffle = 2,
    X86Bcj = 3,
}

/// <summary>
/// Reversible lossless byte-stream preprocessor with self-describing framing header and exact inverse decode.
/// </summary>
public interface ILosslessTransform
{
    TransformKind Kind { get; }

    string Name { get; }

    byte[] Encode(ReadOnlySpan<byte> input);

    byte[] Decode(ReadOnlySpan<byte> encodedFrame);
}
