using System.Buffers.Binary;
using System.Text;
using UltraCompress.Core.Intelligence;

namespace UltraCompress.Intelligence.Tests;

public sealed class MultiWindowSignalTests
{
    [Fact]
    public async Task InspectAsync_TenGiBStream_SamplesExactFiveWindowsInConstantMemory()
    {
        var mp4Header = MagicSignatureDetectionTests.BuildFtyp("isom", "iso2", "mp41");
        await using var stream = new VirtualLargeWorkloadStream(
            VirtualLargeWorkloadStream.TenGiB,
            mp4Header,
            VirtualLargeWorkloadStream.FillHighEntropyPseudoRandom);

        var options = new ContentInspectorOptions
        {
            WindowSizeBytes = 64 * 1024,
            MicroProbeSizeBytes = 32 * 1024,
        };

        var result = await ContentInspector.InspectAsync(stream, "capture_4k.mp4", options);

        Assert.Equal(VirtualLargeWorkloadStream.TenGiB, result.SizeBytes);
        Assert.Equal(5, result.Windows.Count);
        Assert.Equal(5 * 64 * 1024, result.SampledBytes);
        Assert.Equal(5 * 64 * 1024, stream.TotalBytesRead);
        Assert.Equal(5, stream.SeekCount);
        Assert.True(result.PeakBufferBytes < 1024 * 1024, $"Expected O(1) <1 MiB buffer, got {result.PeakBufferBytes}.");
        Assert.False(result.IsFullContentHash);

        // Verify exact window positions: Head (0%), Quarter25, Midpoint50, Quarter75, Tail (10 GiB - 64 KiB)
        Assert.Equal(WindowPosition.Head, result.Windows[0].Position);
        Assert.Equal(0L, result.Windows[0].Offset);
        Assert.Equal(WindowPosition.Quarter25, result.Windows[1].Position);
        Assert.Equal(WindowPosition.Midpoint50, result.Windows[2].Position);
        Assert.Equal(WindowPosition.Quarter75, result.Windows[3].Position);
        Assert.Equal(WindowPosition.Tail, result.Windows[4].Position);
        Assert.Equal(VirtualLargeWorkloadStream.TenGiB - (64 * 1024), result.Windows[4].Offset);
    }

    [Fact]
    public void Inspect_ZeroPaddedHeaderWithHighEntropyBody_CapturesWindowVarianceAndTrueInteriorEntropy()
    {
        // Simulate container with 64 KiB of zero-padded header boxes followed by 10 GiB of encrypted/H.265 payload
        var headerWithPadding = new byte[64 * 1024];
        MagicSignatureDetectionTests.BuildFtyp("mp42").CopyTo(headerWithPadding, 0);

        using var stream = new VirtualLargeWorkloadStream(
            VirtualLargeWorkloadStream.TenGiB,
            headerWithPadding,
            VirtualLargeWorkloadStream.FillHighEntropyPseudoRandom);

        var result = ContentInspector.Inspect(stream, "faststart_padded.mp4");

        Assert.True(result.Windows[0].EntropyBitsPerByte < 0.1, "Head window should reflect zero-padded box.");
        Assert.All(result.Windows.Skip(1), w => Assert.InRange(w.EntropyBitsPerByte, 7.95, 8.00));
        Assert.InRange(result.Signals.InteriorEntropyBitsPerByte, 7.95, 8.00);
        Assert.True(result.Signals.EntropyVariance > 5.0, "Window entropy variance must reflect head vs body divergence.");
        Assert.True(result.Signals.MicroProbeRatio >= 0.995, "Stratified interior 32 KiB probe must not be fooled by zero-padded head.");
    }

    [Fact]
    public void InspectBytes_FourByteAndEightByteStrideStreams_MeasureStrongDeltaEntropyReduction()
    {
        // 8-byte monotonic int64 telemetry records
        var data8 = new byte[128 * 1024];
        for (var rec = 0; rec < data8.Length / 8; rec++)
        {
            ulong ts = 1_735_000_000_000UL + (ulong)(rec * 5);
            BinaryPrimitives.WriteUInt64LittleEndian(data8.AsSpan(rec * 8, 8), ts);
        }

        var res8 = ContentInspector.InspectBytes(data8, "metrics.bin");
        Assert.True(res8.Signals.Stride8EntropyReduction > 1.5,
            $"Expected significant 8-byte stride delta reduction, got {res8.Signals.Stride8EntropyReduction:F3} bpb.");
        Assert.NotNull(res8.Signals.PreprocessedMicroProbeRatio);
        Assert.True(res8.Signals.PreprocessedMicroProbeRatio!.Value < res8.Signals.MicroProbeRatio);

        // 4-byte monotonic int32 sensor array
        var data4 = new byte[128 * 1024];
        for (var elem = 0; elem < data4.Length / 4; elem++)
        {
            uint val = 500_000U + (uint)(elem * 3);
            BinaryPrimitives.WriteUInt32LittleEndian(data4.AsSpan(elem * 4, 4), val);
        }

        var res4 = ContentInspector.InspectBytes(data4, "samples_i32.bin");
        Assert.True(res4.Signals.Stride4EntropyReduction > 1.5,
            $"Expected significant 4-byte stride delta reduction, got {res4.Signals.Stride4EntropyReduction:F3} bpb.");
    }

    [Fact]
    public void InspectBytes_UnbiasedBigramPredictability_DistinguishesRandomNoiseFromSourceCodeAtAnySize()
    {
        var smallNoise = new byte[4096];
        VirtualLargeWorkloadStream.FillHighEntropyPseudoRandom(0, smallNoise);
        var noiseInspection = ContentInspector.InspectBytes(smallNoise, "noise.bin");

        var codeText = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(
            "public sealed class OrderProcessor { public async Task<Result> ExecuteAsync(Order item) => await SaveAsync(item); }\n",
            60)));
        var codeInspection = ContentInspector.InspectBytes(codeText, "OrderProcessor.cs");

        Assert.True(noiseInspection.Signals.BigramPredictability < 0.05,
            $"4 KiB white noise should have near-zero unbiased bigram predictability, got {noiseInspection.Signals.BigramPredictability:F3}.");
        Assert.True(codeInspection.Signals.BigramPredictability > 0.35,
            $"C# source should exhibit strong bigram predictability, got {codeInspection.Signals.BigramPredictability:F3}.");
        Assert.True(codeInspection.Signals.MicroProbeRatio < 0.25);
    }

    [Fact]
    public void InspectBytes_RunLengthHeavyStream_ReportsHighRunLengthScore()
    {
        var rleData = new byte[64 * 1024];
        for (var i = 0; i < rleData.Length; i++)
        {
            rleData[i] = (byte)((i / 64) & 0xFF);
        }

        var inspection = ContentInspector.InspectBytes(rleData, "scanline.bin");
        Assert.InRange(inspection.Signals.RunLengthScore, 0.96, 1.0);
    }
}
