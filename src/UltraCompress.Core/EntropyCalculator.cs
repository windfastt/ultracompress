namespace UltraCompress.Core;

/// <summary>
/// Shannon entropy in bits per byte (0–8).
/// </summary>
public static class EntropyCalculator
{
    public static double ShannonBitsPerByte(byte[] data, int offset, int count) =>
        ShannonBitsPerByte(data.AsSpan(offset, count));

    public static double ShannonBitsPerByte(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        Span<int> counts = stackalloc int[256];
        foreach (var b in data)
        {
            counts[b]++;
        }

        var length = (double)data.Length;
        double entropy = 0;
        foreach (var count in counts)
        {
            if (count == 0)
            {
                continue;
            }

            var p = count / length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    public static IReadOnlyList<BlockEntropySample> BlockedEntropy(
        byte[] data,
        int offset,
        int count,
        int blockSizeBytes) =>
        BlockedEntropy(data.AsSpan(offset, count), blockSizeBytes);

    public static IReadOnlyList<BlockEntropySample> BlockedEntropy(
        ReadOnlySpan<byte> data,
        int blockSizeBytes)
    {
        if (data.IsEmpty || blockSizeBytes <= 0)
        {
            return Array.Empty<BlockEntropySample>();
        }

        var blocks = new List<BlockEntropySample>((data.Length + blockSizeBytes - 1) / blockSizeBytes);
        for (var offset = 0; offset < data.Length; offset += blockSizeBytes)
        {
            var length = Math.Min(blockSizeBytes, data.Length - offset);
            blocks.Add(new BlockEntropySample
            {
                Offset = offset,
                Length = length,
                EntropyBitsPerByte = ShannonBitsPerByte(data.Slice(offset, length)),
            });
        }

        return blocks;
    }
}