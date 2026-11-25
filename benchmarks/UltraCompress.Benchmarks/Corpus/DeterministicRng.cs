using System.Buffers.Binary;
using System.Numerics;

namespace UltraCompress.Benchmarks.Corpus;

/// <summary>
/// Cross-platform bit-exact Xoshiro256** PRNG seeded via SplitMix64.
/// Guarantees byte-identical corpus synthesis across Linux, Windows, macOS, and .NET versions.
/// </summary>
public sealed class DeterministicRng
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    public DeterministicRng(ulong seed)
    {
        ulong z = seed;
        _s0 = NextSplitMix64(ref z);
        _s1 = NextSplitMix64(ref z);
        _s2 = NextSplitMix64(ref z);
        _s3 = NextSplitMix64(ref z);
    }

    public ulong NextUInt64()
    {
        ulong result = BitOperations.RotateLeft(_s1 * 5UL, 7) * 9UL;
        ulong t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;

        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);

        return result;
    }

    public uint NextUInt32() => (uint)(NextUInt64() >> 32);

    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            return minInclusive;
        }

        ulong range = (ulong)(maxExclusive - minInclusive);
        return minInclusive + (int)(NextUInt64() % range);
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public T Pick<T>(T[] items) => items[NextInt(0, items.Length)];

    public T Pick<T>(ReadOnlySpan<T> items) => items[NextInt(0, items.Length)];

    public void FillBytes(Span<byte> buffer)
    {
        int i = 0;
        while (i + 8 <= buffer.Length)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(i, 8), NextUInt64());
            i += 8;
        }

        if (i < buffer.Length)
        {
            ulong tail = NextUInt64();
            while (i < buffer.Length)
            {
                buffer[i++] = (byte)tail;
                tail >>= 8;
            }
        }
    }

    private static ulong NextSplitMix64(ref ulong state)
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
