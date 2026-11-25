using System.Buffers.Binary;

namespace UltraCompress.Core.Transforms;

/// <summary>
/// 16-byte self-describing framing header prepended to encoded lossless transform streams.
/// Layout: [0..3] ASCII "UCTF", [4] Version (1), [5] TransformKind, [6] Parameter, [7] Flags, [8..15] OriginalLength (LE int64).
/// </summary>
public readonly record struct TransformFrameHeader(
    TransformKind Kind,
    byte Parameter,
    long OriginalLength,
    byte Flags = 0)
{
    public const int HeaderSizeBytes = 16;
    public const byte CurrentVersion = 1;
    public const uint MagicLittleEndian = 0x4654_4355u; // 'U', 'C', 'T', 'F'

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < HeaderSizeBytes)
        {
            throw new ArgumentException($"Destination buffer must be at least {HeaderSizeBytes} bytes.", nameof(destination));
        }

        if (OriginalLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(OriginalLength), "OriginalLength cannot be negative.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], MagicLittleEndian);
        destination[4] = CurrentVersion;
        destination[5] = (byte)Kind;
        destination[6] = Parameter;
        destination[7] = Flags;
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(8, 8), OriginalLength);
    }

    public static TransformFrameHeader ReadAndValidate(
        ReadOnlySpan<byte> encodedFrame,
        TransformKind? expectedKind = null)
    {
        if (!TryRead(encodedFrame, out var header, out var error))
        {
            throw new InvalidDataException(error ?? "Invalid transform frame header.");
        }

        if (expectedKind is not null && header.Kind != expectedKind.Value)
        {
            throw new InvalidDataException(
                $"Transform frame kind mismatch: expected '{expectedKind.Value}', found '{header.Kind}'.");
        }

        var payloadBytes = encodedFrame.Length - HeaderSizeBytes;
        if (payloadBytes != header.OriginalLength)
        {
            throw new InvalidDataException(
                $"Transform frame payload length {payloadBytes} does not match header OriginalLength {header.OriginalLength}.");
        }

        return header;
    }

    public static bool HasMagic(ReadOnlySpan<byte> buffer) =>
        buffer.Length >= HeaderSizeBytes
        && BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]) == MagicLittleEndian;

    public static bool TryRead(
        ReadOnlySpan<byte> buffer,
        out TransformFrameHeader header,
        out string? error)
    {
        header = default;
        if (buffer.Length < HeaderSizeBytes)
        {
            error = $"Transform frame truncated: {buffer.Length} bytes < {HeaderSizeBytes}-byte header.";
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]);
        if (magic != MagicLittleEndian)
        {
            error = "Invalid transform frame magic (expected 'UCTF').";
            return false;
        }

        var version = buffer[4];
        if (version != CurrentVersion)
        {
            error = $"Unsupported transform frame version {version} (expected {CurrentVersion}).";
            return false;
        }

        var kindByte = buffer[5];
        if (!Enum.IsDefined((TransformKind)kindByte))
        {
            error = $"Unknown transform kind identifier {kindByte}.";
            return false;
        }

        var originalLength = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(8, 8));
        if (originalLength < 0)
        {
            error = $"Invalid negative OriginalLength {originalLength} in transform frame.";
            return false;
        }

        header = new TransformFrameHeader(
            Kind: (TransformKind)kindByte,
            Parameter: buffer[6],
            OriginalLength: originalLength,
            Flags: buffer[7]);
        error = null;
        return true;
    }
}
