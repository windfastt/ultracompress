using System.Buffers;
using System.IO.Compression;

namespace UltraCompress.Core.Intelligence;

/// <summary>
/// Computes O(1)-memory statistical and micro-compression signals across sampled byte windows.
/// </summary>
public static class SignalAnalyzer
{
    private const int BigramTableLength = 256 * 256;

    public static ContentInspectionSignals AnalyzeWindows(
        IReadOnlyList<(WindowPosition Position, long Offset, byte[] Buffer, int Length)> windows,
        int microProbeSizeBytes,
        int? structuralStrideHint,
        out IReadOnlyList<InspectionWindowSample> windowSamples)
    {
        if (windows.Count == 0)
        {
            windowSamples = Array.Empty<InspectionWindowSample>();
            return new ContentInspectionSignals
            {
                MicroProbeRatio = 1.0,
            };
        }

        var samples = new List<InspectionWindowSample>(windows.Count);
        var bigramCounts = ArrayPool<int>.Shared.Rent(BigramTableLength);
        Array.Clear(bigramCounts, 0, BigramTableLength);

        Span<long> unigramCounts = stackalloc long[256];
        Span<long> bigramRowTotals = stackalloc long[256];
        Span<long> delta1Counts = stackalloc long[256];
        Span<long> delta2Counts = stackalloc long[256];
        Span<long> delta3Counts = stackalloc long[256];
        Span<long> delta4Counts = stackalloc long[256];
        Span<long> delta8Counts = stackalloc long[256];

        long totalBytes = 0;
        long totalTransitions = 0;
        long equalAdjacentCount = 0;
        long asciiPrintableCount = 0;
        long zeroByteCount = 0;
        long d1Total = 0, d2Total = 0, d3Total = 0, d4Total = 0, d8Total = 0;

        try
        {
            foreach (var w in windows)
            {
                var span = w.Buffer.AsSpan(0, w.Length);
                if (span.IsEmpty)
                {
                    continue;
                }

                var winEntropy = EntropyCalculator.ShannonBitsPerByte(span);
                var winStride4 = ComputeWindowStrideDeltaEntropy(span, 4);
                var winStride8 = ComputeWindowStrideDeltaEntropy(span, 8);

                samples.Add(new InspectionWindowSample
                {
                    Position = w.Position,
                    Offset = w.Offset,
                    Length = w.Length,
                    EntropyBitsPerByte = winEntropy,
                    Stride4DeltaEntropy = winStride4,
                    Stride8DeltaEntropy = winStride8,
                });

                totalBytes += span.Length;
                for (var i = 0; i < span.Length; i++)
                {
                    var b = span[i];
                    unigramCounts[b]++;
                    if (b == 0) zeroByteCount++;
                    if ((b >= 0x20 && b <= 0x7E) || b is (byte)'\n' or (byte)'\r' or (byte)'\t')
                    {
                        asciiPrintableCount++;
                    }

                    if (i >= 1)
                    {
                        var prev = span[i - 1];
                        if (b == prev) equalAdjacentCount++;
                        bigramCounts[(prev << 8) | b]++;
                        bigramRowTotals[prev]++;
                        totalTransitions++;

                        delta1Counts[unchecked((byte)(b - prev))]++;
                        d1Total++;
                    }

                    if (i >= 2)
                    {
                        delta2Counts[unchecked((byte)(b - span[i - 2]))]++;
                        d2Total++;
                    }

                    if (i >= 3)
                    {
                        delta3Counts[unchecked((byte)(b - span[i - 3]))]++;
                        d3Total++;
                    }

                    if (i >= 4)
                    {
                        delta4Counts[unchecked((byte)(b - span[i - 4]))]++;
                        d4Total++;
                    }

                    if (i >= 8)
                    {
                        delta8Counts[unchecked((byte)(b - span[i - 8]))]++;
                        d8Total++;
                    }
                }
            }

            windowSamples = samples;
            if (totalBytes == 0 || samples.Count == 0)
            {
                return new ContentInspectionSignals { MicroProbeRatio = 1.0 };
            }

            // Weighted mean, interior mean, min, max, and variance across windows
            double weightedSum = 0;
            double interiorSum = 0;
            long interiorBytes = 0;
            double minWin = double.MaxValue;
            double maxWin = double.MinValue;

            foreach (var s in samples)
            {
                weightedSum += s.EntropyBitsPerByte * s.Length;
                minWin = Math.Min(minWin, s.EntropyBitsPerByte);
                maxWin = Math.Max(maxWin, s.EntropyBitsPerByte);
                if (samples.Count == 1 || s.Position != WindowPosition.Head)
                {
                    interiorSum += s.EntropyBitsPerByte * s.Length;
                    interiorBytes += s.Length;
                }
            }

            var meanEntropy = weightedSum / totalBytes;
            var interiorEntropy = interiorBytes > 0 ? interiorSum / interiorBytes : meanEntropy;

            double varianceSum = 0;
            foreach (var s in samples)
            {
                var diff = s.EntropyBitsPerByte - meanEntropy;
                varianceSum += diff * diff * s.Length;
            }

            var variance = varianceSum / totalBytes;
            var stdDev = Math.Sqrt(variance);

            // Aggregate unigram entropy for exact stride reduction comparison
            var aggregateEntropy = ComputeEntropyFromHistogram(unigramCounts, totalBytes);
            var s1Red = d1Total > 0 ? Math.Max(0.0, aggregateEntropy - ComputeEntropyFromHistogram(delta1Counts, d1Total)) : 0.0;
            var s2Red = d2Total > 0 ? Math.Max(0.0, aggregateEntropy - ComputeEntropyFromHistogram(delta2Counts, d2Total)) : 0.0;
            var s3Red = d3Total > 0 ? Math.Max(0.0, aggregateEntropy - ComputeEntropyFromHistogram(delta3Counts, d3Total)) : 0.0;
            var s4Red = d4Total > 0 ? Math.Max(0.0, aggregateEntropy - ComputeEntropyFromHistogram(delta4Counts, d4Total)) : 0.0;
            var s8Red = d8Total > 0 ? Math.Max(0.0, aggregateEntropy - ComputeEntropyFromHistogram(delta8Counts, d8Total)) : 0.0;

            var (bestStride, bestReduction) = PickBestStride(s1Red, s2Red, s3Red, s4Red, s8Red, structuralStrideHint);

            var (bigramCondEntropy, bigramPred) = ComputeUnbiasedBigramPredictability(
                bigramCounts.AsSpan(0, BigramTableLength),
                bigramRowTotals,
                totalTransitions);

            var runLengthScore = totalTransitions > 0
                ? (double)equalAdjacentCount / totalTransitions
                : 0.0;

            var (probeIn, probeOut, probeRatio, preProbeRatio) = RunStratifiedMicroProbe(
                windows,
                microProbeSizeBytes,
                bestStride,
                bestReduction);

            return new ContentInspectionSignals
            {
                MeanEntropyBitsPerByte = meanEntropy,
                InteriorEntropyBitsPerByte = interiorEntropy,
                EntropyVariance = variance,
                EntropyStdDev = stdDev,
                MinWindowEntropy = minWin,
                MaxWindowEntropy = maxWin,
                BigramConditionalEntropy = bigramCondEntropy,
                BigramPredictability = bigramPred,
                RunLengthScore = runLengthScore,
                Stride1EntropyReduction = s1Red,
                Stride2EntropyReduction = s2Red,
                Stride3EntropyReduction = s3Red,
                Stride4EntropyReduction = s4Red,
                Stride8EntropyReduction = s8Red,
                BestStrideBytes = bestStride,
                BestStrideEntropyReduction = bestReduction,
                MicroProbeInputBytes = probeIn,
                MicroProbeCompressedBytes = probeOut,
                MicroProbeRatio = probeRatio,
                PreprocessedMicroProbeRatio = preProbeRatio,
                AsciiPrintableRatio = (double)asciiPrintableCount / totalBytes,
                ZeroByteRatio = (double)zeroByteCount / totalBytes,
            };
        }
        finally
        {
            ArrayPool<int>.Shared.Return(bigramCounts);
        }
    }

    private static (int BestStride, double BestReduction) PickBestStride(
        double s1,
        double s2,
        double s3,
        double s4,
        double s8,
        int? structuralHint)
    {
        Span<(int Stride, double Reduction)> candidates =
        [
            (1, s1),
            (2, s2),
            (3, s3),
            (4, s4),
            (8, s8),
        ];

        int bestStride = 0;
        double bestReduction = 0.0;

        foreach (var c in candidates)
        {
            var bonus = structuralHint == c.Stride ? 0.05 : 0.0;
            if (c.Reduction + bonus > bestReduction + (structuralHint == bestStride ? 0.05 : 0.0))
            {
                bestStride = c.Stride;
                bestReduction = c.Reduction;
            }
        }

        if (bestReduction < 0.05)
        {
            return (0, bestReduction);
        }

        return (bestStride, bestReduction);
    }

    /// <summary>
    /// Uses an unbiased within-row Rényi collision estimator so finite samples (e.g. 4 KiB vs 320 KiB)
    /// never overestimate predictability on uniform random / encrypted / video streams.
    /// </summary>
    private static (double ConditionalEntropy, double Predictability) ComputeUnbiasedBigramPredictability(
        ReadOnlySpan<int> bigramCounts,
        ReadOnlySpan<long> rowTotals,
        long totalTransitions)
    {
        if (totalTransitions < 16)
        {
            return (8.0, 0.0);
        }

        double weightedCollisionProb = 0.0;
        long eligibleWeight = 0;

        for (var a = 0; a < 256; a++)
        {
            var rowTotal = rowTotals[a];
            if (rowTotal < 2)
            {
                continue;
            }

            long pairRepeats = 0;
            var rowOffset = a << 8;
            for (var b = 0; b < 256; b++)
            {
                long c = bigramCounts[rowOffset | b];
                if (c >= 2)
                {
                    pairRepeats += c * (c - 1);
                }
            }

            var pCollisionGivenA = (double)pairRepeats / (rowTotal * (rowTotal - 1));
            weightedCollisionProb += rowTotal * pCollisionProbClamp(pCollisionGivenA);
            eligibleWeight += rowTotal;
        }

        if (eligibleWeight == 0)
        {
            return (8.0, 0.0);
        }

        var avgCollision = Math.Clamp(weightedCollisionProb / eligibleWeight, 1.0 / 256.0, 1.0);
        var conditionalRenyiEntropy = Math.Clamp(-Math.Log2(avgCollision), 0.0, 8.0);
        var predictability = Math.Clamp(1.0 - (conditionalRenyiEntropy / 8.0), 0.0, 1.0);
        return (conditionalRenyiEntropy, predictability);

        static double pCollisionProbClamp(double v) => Math.Clamp(v, 0.0, 1.0);
    }

    private static (int InputBytes, int CompressedBytes, double RawRatio, double? PreprocessedRatio) RunStratifiedMicroProbe(
        IReadOnlyList<(WindowPosition Position, long Offset, byte[] Buffer, int Length)> windows,
        int targetProbeBytes,
        int bestStride,
        double bestStrideReduction)
    {
        targetProbeBytes = Math.Clamp(targetProbeBytes, 1024, 64 * 1024);

        // Prefer interior windows when multiple windows exist so a small container header does not skew 10 GB media streams
        var sourceWindows = windows.Count >= 3
            ? windows.Where(w => w.Position != WindowPosition.Head && w.Length > 0).ToList()
            : windows.Where(w => w.Length > 0).ToList();

        if (sourceWindows.Count == 0)
        {
            sourceWindows = windows.Where(w => w.Length > 0).ToList();
        }

        var probeInput = ArrayPool<byte>.Shared.Rent(targetProbeBytes);
        var maxCompressedLen = BrotliEncoder.GetMaxCompressedLength(targetProbeBytes) + 64;
        var probeDest = ArrayPool<byte>.Shared.Rent(maxCompressedLen);

        try
        {
            int filled = 0;
            int perWindowQuota = Math.Max(1, targetProbeBytes / Math.Max(1, sourceWindows.Count));

            for (var idx = 0; idx < sourceWindows.Count && filled < targetProbeBytes; idx++)
            {
                var w = sourceWindows[idx];
                var remaining = targetProbeBytes - filled;
                var take = idx == sourceWindows.Count - 1
                    ? Math.Min(w.Length, remaining)
                    : Math.Min(w.Length, Math.Min(perWindowQuota, remaining));

                w.Buffer.AsSpan(0, take).CopyTo(probeInput.AsSpan(filled, take));
                filled += take;
            }

            if (filled == 0)
            {
                return (0, 0, 1.0, null);
            }

            var inputSpan = probeInput.AsSpan(0, filled);
            int compressedBytes = FastCompressProbe(inputSpan, probeDest);
            var rawRatio = Math.Min(1.05, (double)compressedBytes / filled);

            double? preprocessedRatio = null;
            if (bestStride is >= 1 and <= 8 && (bestStrideReduction >= 0.15 || rawRatio > 0.35))
            {
                var shuffled = ArrayPool<byte>.Shared.Rent(filled);
                try
                {
                    // Test both stride-delta and byte-lane transpose (ByteShuffle) on the 32 KiB probe
                    ApplyStrideDelta(inputSpan, shuffled.AsSpan(0, filled), bestStride);
                    var deltaCompressed = FastCompressProbe(shuffled.AsSpan(0, filled), probeDest);
                    var bestPreCompressed = deltaCompressed;

                    if (bestStride is 2 or 4 or 8 && filled >= bestStride * 4)
                    {
                        ApplyByteShuffle(inputSpan, shuffled.AsSpan(0, filled), bestStride);
                        var shuffleCompressed = FastCompressProbe(shuffled.AsSpan(0, filled), probeDest);
                        bestPreCompressed = Math.Min(bestPreCompressed, shuffleCompressed);
                    }

                    preprocessedRatio = Math.Min(1.05, (double)bestPreCompressed / filled);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(shuffled);
                }
            }

            return (filled, compressedBytes, rawRatio, preprocessedRatio);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(probeInput);
            ArrayPool<byte>.Shared.Return(probeDest);
        }
    }

    private static int FastCompressProbe(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (BrotliEncoder.TryCompress(source, destination, out var bytesWritten, quality: 1, window: 16))
        {
            return bytesWritten;
        }

        return source.Length;
    }

    private static void ApplyStrideDelta(ReadOnlySpan<byte> source, Span<byte> destination, int stride)
    {
        for (var i = 0; i < source.Length; i++)
        {
            destination[i] = i < stride
                ? source[i]
                : unchecked((byte)(source[i] - source[i - stride]));
        }
    }

    private static void ApplyByteShuffle(ReadOnlySpan<byte> source, Span<byte> destination, int elemBytes)
    {
        var fullElements = source.Length / elemBytes;
        var alignedBytes = fullElements * elemBytes;
        int outIdx = 0;

        for (var lane = 0; lane < elemBytes; lane++)
        {
            for (var elem = 0; elem < fullElements; elem++)
            {
                destination[outIdx++] = source[(elem * elemBytes) + lane];
            }
        }

        for (var rem = alignedBytes; rem < source.Length; rem++)
        {
            destination[outIdx++] = source[rem];
        }
    }

    private static double ComputeWindowStrideDeltaEntropy(ReadOnlySpan<byte> span, int stride)
    {
        if (span.Length <= stride)
        {
            return EntropyCalculator.ShannonBitsPerByte(span);
        }

        Span<long> counts = stackalloc long[256];
        var total = span.Length - stride;
        for (var i = stride; i < span.Length; i++)
        {
            counts[unchecked((byte)(span[i] - span[i - stride]))]++;
        }

        return ComputeEntropyFromHistogram(counts, total);
    }

    private static double ComputeEntropyFromHistogram(ReadOnlySpan<long> counts, long total)
    {
        if (total <= 0)
        {
            return 0.0;
        }

        var invTotal = 1.0 / total;
        double entropy = 0.0;
        for (var i = 0; i < counts.Length; i++)
        {
            var c = counts[i];
            if (c == 0)
            {
                continue;
            }

            var p = c * invTotal;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }
}
