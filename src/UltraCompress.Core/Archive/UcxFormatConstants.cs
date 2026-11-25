using System.Buffers.Binary;
using System.Text;
using UltraCompress.Core.Errors;

namespace UltraCompress.Core.Archive;

public static class UcxFormatConstants
{
    public const int HeaderSizeBytes = 64;
    public const int TrailerSizeBytes = 4;
    public const ushort CurrentMajorVersion = 1;
    public const ushort CurrentMinorVersion = 0;

    public const uint FlagManifestZstdCompressed = 1u << 0;

    public static ReadOnlySpan<byte> MagicUcx1 => "UCX1"u8;

    public static ReadOnlySpan<byte> StagingMagicUcx0 => "UCX0"u8;

    public static ReadOnlySpan<byte> TrailerMagicUcxe => "UCXE"u8;

    public static void WriteIncompleteStagingHeader(Span<byte> buffer)
    {
        if (buffer.Length < HeaderSizeBytes)
        {
            throw new ArgumentException("Header buffer must be at least 64 bytes.", nameof(buffer));
        }

        buffer[..HeaderSizeBytes].Clear();
        StagingMagicUcx0.CopyTo(buffer[..4]);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(4, 2), CurrentMajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(6, 2), CurrentMinorVersion);
    }

    public static void WriteFinalHeader(
        Span<byte> buffer,
        uint flags,
        long manifestOffset,
        long manifestLength,
        int manifestUncompressedLength,
        ReadOnlySpan<byte> manifestSha256)
    {
        if (buffer.Length < HeaderSizeBytes)
        {
            throw new ArgumentException("Header buffer must be at least 64 bytes.", nameof(buffer));
        }

        if (manifestSha256.Length != 32)
        {
            throw new ArgumentException("Manifest SHA-256 must be 32 bytes.", nameof(manifestSha256));
        }

        buffer[..HeaderSizeBytes].Clear();
        MagicUcx1.CopyTo(buffer[..4]);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(4, 2), CurrentMajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(6, 2), CurrentMinorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(8, 4), flags);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(12, 8), manifestOffset);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(20, 8), manifestLength);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(28, 4), manifestUncompressedLength);
        manifestSha256.CopyTo(buffer.Slice(32, 32));
    }

    public static UcxParsedHeader ParseAndValidateHeader(
        ReadOnlySpan<byte> buffer,
        long totalStreamLength,
        string? archivePath = null)
    {
        if (totalStreamLength < HeaderSizeBytes + TrailerSizeBytes || buffer.Length < HeaderSizeBytes)
        {
            throw UltraCompressErrors.CorruptArchive(
                archivePath,
                $"File is too small ({totalStreamLength} bytes) to be a valid UCX1 archive.");
        }

        if (buffer[..4].SequenceEqual(StagingMagicUcx0))
        {
            throw UltraCompressErrors.CorruptArchive(
                archivePath,
                "Container has incomplete staging header 'UCX0' from an interrupted write.");
        }

        if (!buffer[..4].SequenceEqual(MagicUcx1))
        {
            var preview = Encoding.ASCII.GetString(buffer[..4].ToArray());
            throw UltraCompressErrors.CorruptArchive(
                archivePath,
                $"Expected 'UCX1' magic header but found '{ sanitizeAscii(preview) }'.");
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(4, 2));
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(6, 2));
        if (major != CurrentMajorVersion)
        {
            throw UltraCompressErrors.UnsupportedCodecOrVersion(
                archivePath,
                $"Archive format version {major}.{minor} is not supported by this reader (expected {CurrentMajorVersion}.x).");
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4));
        var manifestOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(12, 8));
        var manifestLength = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(20, 8));
        var manifestUncompressedLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(28, 4));
        var manifestSha256 = buffer.Slice(32, 32).ToArray();

        if (manifestOffset < HeaderSizeBytes
            || manifestLength <= 0
            || manifestUncompressedLength <= 0
            || manifestOffset > totalStreamLength - TrailerSizeBytes
            || manifestLength > totalStreamLength - manifestOffset - TrailerSizeBytes)
        {
            throw UltraCompressErrors.CorruptArchive(
                archivePath,
                $"Manifest bounds (offset={manifestOffset}, length={manifestLength}) exceed container size ({totalStreamLength}).");
        }

        return new UcxParsedHeader(
            major,
            minor,
            flags,
            manifestOffset,
            manifestLength,
            manifestUncompressedLength,
            manifestSha256);

        static string sanitizeAscii(string raw)
        {
            var chars = raw.Select(c => c >= 32 && c < 127 ? c : '?').ToArray();
            return new string(chars);
        }
    }
}

public sealed record UcxParsedHeader(
    ushort MajorVersion,
    ushort MinorVersion,
    uint Flags,
    long ManifestOffset,
    long ManifestLength,
    int ManifestUncompressedLength,
    byte[] ManifestSha256);
