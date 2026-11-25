namespace UltraCompress.Core.Search;

/// <summary>
/// Extracts 8-byte-aligned multi-window representative pilot samples and successive-halving rungs from an input buffer.
/// </summary>
public static class PilotChunkSampler
{
    private const int AlignmentBytes = 8;

    public static byte[] ExtractCompositeSample(ReadOnlySpan<byte> input, ProfileContract contract)
    {
        if (input.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        if (contract.PilotSampleBytes <= 0 || input.Length <= contract.PilotSampleBytes)
        {
            return input.ToArray();
        }

        var windowCount = Math.Max(1, contract.PilotWindowCount);
        var baseWindowSize = Math.Max(AlignmentBytes, AlignDown(contract.PilotSampleBytes / windowCount, AlignmentBytes));
        var targetTotalBytes = AlignDown(contract.PilotSampleBytes, AlignmentBytes);
        if (targetTotalBytes <= 0 || windowCount == 1 || input.Length <= targetTotalBytes)
        {
            return input[..Math.Min(input.Length, contract.PilotSampleBytes)].ToArray();
        }

        var composite = new byte[targetTotalBytes];
        var dstPos = 0;

        for (var w = 0; w < windowCount; w++)
        {
            var currentWindowBytes = (w == windowCount - 1)
                ? targetTotalBytes - dstPos
                : baseWindowSize;
            var maxStartOffset = input.Length - currentWindowBytes;

            int srcOffset;
            if (w == 0)
            {
                srcOffset = 0;
            }
            else if (w == windowCount - 1)
            {
                srcOffset = AlignDown(maxStartOffset, AlignmentBytes);
            }
            else
            {
                var proportional = (long)maxStartOffset * w / (windowCount - 1);
                srcOffset = AlignDown((int)proportional, AlignmentBytes);
            }

            srcOffset = Math.Clamp(srcOffset, 0, maxStartOffset);
            input.Slice(srcOffset, currentWindowBytes).CopyTo(composite.AsSpan(dstPos, currentWindowBytes));
            dstPos += currentWindowBytes;
        }

        return composite;
    }

    public static IReadOnlyList<byte[]> BuildSuccessiveHalvingRungs(byte[] pilotSample)
    {
        if (pilotSample.Length < 1024)
        {
            return [pilotSample];
        }

        if (pilotSample.Length < 16 * 1024)
        {
            var half = Math.Max(512, AlignDown(pilotSample.Length / 2, AlignmentBytes));
            return half < pilotSample.Length
                ? [pilotSample[..half], pilotSample]
                : [pilotSample];
        }

        var quarter = Math.Max(4096, AlignDown(pilotSample.Length / 4, AlignmentBytes));
        var halfLarge = Math.Max(quarter + AlignmentBytes, AlignDown(pilotSample.Length / 2, AlignmentBytes));

        var rungs = new List<byte[]>(3);
        if (quarter < halfLarge)
        {
            rungs.Add(pilotSample[..quarter]);
        }

        if (halfLarge < pilotSample.Length)
        {
            rungs.Add(pilotSample[..halfLarge]);
        }

        rungs.Add(pilotSample);
        return rungs;
    }

    private static int AlignDown(int value, int alignment) =>
        value - (value % alignment);
}
