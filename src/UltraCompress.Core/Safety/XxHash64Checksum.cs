using System.Buffers.Binary;
using System.Numerics;

namespace UltraCompress.Core.Safety;

/// <summary>
/// Exact, zero-allocation Cyan4973 XXH64 implementation used alongside SHA-256 for fast bit-exact stream verification.
/// </summary>
public static class XxHash64Checksum
{
    private const ulong Prime64_1 = 0x9E3779B185EBCA87UL;
    private const ulong Prime64_2 = 0xC2B2AE3D27D4EB4FUL;
    private const ulong Prime64_3 = 0x165667B19E3779F9UL;
    private const ulong Prime64_4 = 0x85EBCA77C2B2AE63UL;
    private const ulong Prime64_5 = 0x27D4EB2F165667C5UL;

    public static ulong HashToUInt64(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        var offset = 0;
        ulong h64;

        if (data.Length >= 32)
        {
            var v1 = unchecked(seed + Prime64_1 + Prime64_2);
            var v2 = unchecked(seed + Prime64_2);
            var v3 = seed;
            var v4 = unchecked(seed - Prime64_1);

            while (offset <= data.Length - 32)
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8)));
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 8, 8)));
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 16, 8)));
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 24, 8)));
                offset += 32;
            }

            h64 = unchecked(
                BitOperations.RotateLeft(v1, 1)
                + BitOperations.RotateLeft(v2, 7)
                + BitOperations.RotateLeft(v3, 12)
                + BitOperations.RotateLeft(v4, 18));

            h64 = MergeAccumulator(h64, v1);
            h64 = MergeAccumulator(h64, v2);
            h64 = MergeAccumulator(h64, v3);
            h64 = MergeAccumulator(h64, v4);
        }
        else
        {
            h64 = unchecked(seed + Prime64_5);
        }

        h64 = unchecked(h64 + (ulong)data.Length);

        while (offset <= data.Length - 8)
        {
            var k1 = Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8)));
            h64 ^= k1;
            h64 = unchecked(BitOperations.RotateLeft(h64, 27) * Prime64_1 + Prime64_4);
            offset += 8;
        }

        if (offset <= data.Length - 4)
        {
            var k1 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
            h64 ^= unchecked(k1 * Prime64_1);
            h64 = unchecked(BitOperations.RotateLeft(h64, 23) * Prime64_2 + Prime64_3);
            offset += 4;
        }

        while (offset < data.Length)
        {
            h64 ^= unchecked(data[offset] * Prime64_5);
            h64 = unchecked(BitOperations.RotateLeft(h64, 11) * Prime64_1);
            offset++;
        }

        h64 ^= h64 >> 33;
        h64 = unchecked(h64 * Prime64_2);
        h64 ^= h64 >> 29;
        h64 = unchecked(h64 * Prime64_3);
        h64 ^= h64 >> 32;

        return h64;
    }

    public static string HashToHexString(ReadOnlySpan<byte> data, ulong seed = 0) =>
        HashToUInt64(data, seed).ToString("x16");

    private static ulong Round(ulong acc, ulong input)
    {
        acc = unchecked(acc + input * Prime64_2);
        acc = BitOperations.RotateLeft(acc, 31);
        acc = unchecked(acc * Prime64_1);
        return acc;
    }

    private static ulong MergeAccumulator(ulong acc, ulong value)
    {
        value = Round(0, value);
        acc ^= value;
        acc = unchecked(acc * Prime64_1 + Prime64_4);
        return acc;
    }
}
