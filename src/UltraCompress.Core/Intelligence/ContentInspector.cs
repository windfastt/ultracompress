using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace UltraCompress.Core.Intelligence;

/// <summary>
/// Configuration for <see cref="ContentInspector"/> multi-window O(1)-memory sampling.
/// </summary>
public sealed class ContentInspectorOptions
{
    /// <summary>
    /// Size in bytes of each sampled window (Head, 25%, 50%, 75%, Tail). Default 64 KiB (320 KiB max I/O).
    /// </summary>
    public int WindowSizeBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Size in bytes of the stratified micro-compression probe. Default 32 KiB.
    /// </summary>
    public int MicroProbeSizeBytes { get; init; } = 32 * 1024;
}

/// <summary>
/// Multi-window O(1)-memory content inspector sampling head, 25%, 50%, 75%, and tail slices
/// so multi-GB (10 GB+) files inspect in milliseconds with bounded memory.
/// </summary>
public static class ContentInspector
{
    public static ContentInspectionResult InspectBytes(
        ReadOnlySpan<byte> data,
        string? pathHint = null,
        ContentInspectorOptions? options = null)
    {
        using var ms = new MemoryStream(data.ToArray(), writable: false);
        return Inspect(ms, pathHint, options);
    }

    public static ContentInspectionResult InspectFile(
        string path,
        ContentInspectorOptions? options = null)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Target file not found for content inspection.", fullPath);
        }

        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.RandomAccess);

        return Inspect(stream, fullPath, options);
    }

    public static async Task<ContentInspectionResult> InspectFileAsync(
        string path,
        ContentInspectorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Target file not found for content inspection.", fullPath);
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        return await InspectAsync(stream, fullPath, options, cancellationToken);
    }

    public static ContentInspectionResult Inspect(
        Stream stream,
        string? pathHint = null,
        ContentInspectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("ContentInspector requires a readable, seekable stream for O(1) multi-window inspection.", nameof(stream));
        }

        options ??= new ContentInspectorOptions();
        ValidateOptions(options);

        var sw = Stopwatch.StartNew();
        var totalLength = stream.Length;
        var windowPlan = BuildWindowPlan(totalLength, options.WindowSizeBytes);

        var rentedBuffers = new List<(WindowPosition Position, long Offset, byte[] Buffer, int Length)>(windowPlan.Count);
        try
        {
            foreach (var (position, offset, length) in windowPlan)
            {
                var buf = ArrayPool<byte>.Shared.Rent(length);
                stream.Seek(offset, SeekOrigin.Begin);
                var read = ReadExactOrUntilEof(stream, buf.AsSpan(0, length));
                rentedBuffers.Add((position, offset, buf, read));
            }

            sw.Stop();
            return FinalizeInspection(rentedBuffers, totalLength, pathHint, options, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            foreach (var item in rentedBuffers)
            {
                ArrayPool<byte>.Shared.Return(item.Buffer);
            }
        }
    }

    public static async Task<ContentInspectionResult> InspectAsync(
        Stream stream,
        string? pathHint = null,
        ContentInspectorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("ContentInspector requires a readable, seekable stream for O(1) multi-window inspection.", nameof(stream));
        }

        options ??= new ContentInspectorOptions();
        ValidateOptions(options);

        var sw = Stopwatch.StartNew();
        var totalLength = stream.Length;
        var windowPlan = BuildWindowPlan(totalLength, options.WindowSizeBytes);

        var rentedBuffers = new List<(WindowPosition Position, long Offset, byte[] Buffer, int Length)>(windowPlan.Count);
        try
        {
            foreach (var (position, offset, length) in windowPlan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var buf = ArrayPool<byte>.Shared.Rent(length);
                stream.Seek(offset, SeekOrigin.Begin);
                var read = await ReadExactOrUntilEofAsync(stream, buf.AsMemory(0, length), cancellationToken);
                rentedBuffers.Add((position, offset, buf, read));
            }

            sw.Stop();
            return FinalizeInspection(rentedBuffers, totalLength, pathHint, options, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            foreach (var item in rentedBuffers)
            {
                ArrayPool<byte>.Shared.Return(item.Buffer);
            }
        }
    }

    /// <summary>
    /// Computes up to 5 non-overlapping windows at Head (0%), Quarter25 (25%), Midpoint50 (50%),
    /// Quarter75 (75%), and Tail (100% - W) without ever exceeding stream bounds or double-sampling bytes.
    /// </summary>
    public static IReadOnlyList<(WindowPosition Position, long Offset, int Length)> BuildWindowPlan(
        long totalLength,
        int windowSizeBytes)
    {
        if (totalLength <= 0)
        {
            return Array.Empty<(WindowPosition, long, int)>();
        }

        if (totalLength <= windowSizeBytes)
        {
            return new[] { (WindowPosition.Head, 0L, (int)totalLength) };
        }

        // When the file is larger than 1 window but smaller than 5 full windows, partition cleanly into
        // non-overlapping contiguous slices up to 5 windows so every byte is sampled at most once.
        if (totalLength <= (long)windowSizeBytes * 5)
        {
            var positions = new[]
            {
                WindowPosition.Head,
                WindowPosition.Quarter25,
                WindowPosition.Midpoint50,
                WindowPosition.Quarter75,
                WindowPosition.Tail,
            };

            var plan = new List<(WindowPosition, long, int)>(5);
            long cursor = 0;
            int idx = 0;
            while (cursor < totalLength && idx < positions.Length)
            {
                var remainingWindows = positions.Length - idx;
                var sliceLen = idx == positions.Length - 1
                    ? (int)(totalLength - cursor)
                    : (int)Math.Min(windowSizeBytes, Math.Max(1L, (totalLength - cursor) / remainingWindows));

                plan.Add((positions[idx], cursor, sliceLen));
                cursor += sliceLen;
                idx++;
            }

            return plan;
        }

        // Multi-megabyte / multi-gigabyte O(1) 5-window seek plan: Head, 25%, 50%, 75%, Tail
        var maxStart = totalLength - windowSizeBytes;
        return new (WindowPosition, long, int)[]
        {
            (WindowPosition.Head, 0L, windowSizeBytes),
            (WindowPosition.Quarter25, maxStart / 4, windowSizeBytes),
            (WindowPosition.Midpoint50, maxStart / 2, windowSizeBytes),
            (WindowPosition.Quarter75, (maxStart * 3) / 4, windowSizeBytes),
            (WindowPosition.Tail, maxStart, windowSizeBytes),
        };
    }

    private static ContentInspectionResult FinalizeInspection(
        IReadOnlyList<(WindowPosition Position, long Offset, byte[] Buffer, int Length)> windows,
        long totalLength,
        string? pathHint,
        ContentInspectorOptions options,
        double ioElapsedMs)
    {
        var computeSw = Stopwatch.StartNew();

        var headSpan = windows.Count > 0
            ? windows[0].Buffer.AsSpan(0, windows[0].Length)
            : ReadOnlySpan<byte>.Empty;
        var tailSpan = windows.Count > 0
            ? windows[^1].Buffer.AsSpan(0, windows[^1].Length)
            : ReadOnlySpan<byte>.Empty;

        var signature = MagicSignatureCatalog.Detect(headSpan, tailSpan, totalLength, pathHint);
        var signals = SignalAnalyzer.AnalyzeWindows(
            windows,
            options.MicroProbeSizeBytes,
            signature.StructuralStrideBytes,
            out var windowSamples);

        signature = RefineSignatureWithSignals(signature, signals, pathHint);

        long sampledBytes = 0;
        foreach (var w in windows)
        {
            sampledBytes += w.Length;
        }

        var isFullHash = sampledBytes == totalLength;
        var sampleHash = ComputeSampleHash(windows, totalLength, isFullHash);

        // Peak working buffer: window buffers + 256 KiB pooled bigram table + micro-probe scratch
        var peakBufferBytes = (int)Math.Min(int.MaxValue, sampledBytes + (256 * 1024) + (options.MicroProbeSizeBytes * 3));

        computeSw.Stop();

        return new ContentInspectionResult
        {
            Path = pathHint,
            SizeBytes = totalLength,
            SampledBytes = sampledBytes,
            PeakBufferBytes = peakBufferBytes,
            InspectionDurationMs = ioElapsedMs + computeSw.Elapsed.TotalMilliseconds,
            Signature = signature,
            Windows = windowSamples,
            Signals = signals,
            SampleFingerprintSha256 = sampleHash,
            IsFullContentHash = isFullHash,
        };
    }

    private static SignatureMatch RefineSignatureWithSignals(
        SignatureMatch initial,
        ContentInspectionSignals signals,
        string? pathHint)
    {
        // Generic binary with strong 4-byte or 8-byte stride correlation is structured binary/tabular data
        if (initial.FormatId == "Binary"
            && signals.BestStrideBytes is 4 or 8
            && signals.BestStrideEntropyReduction >= 0.45
            && (signals.PreprocessedMicroProbeRatio ?? signals.MicroProbeRatio) <= 0.72)
        {
            return initial with
            {
                ContentClass = ContentClass.DatabaseAndTabular,
                DisplayName = $"Structured Fixed-Stride Binary ({signals.BestStrideBytes}-byte records)",
                MatchedEvidence = $"Detected {signals.BestStrideBytes}-byte stride correlation (-{signals.BestStrideEntropyReduction:F2} bpb delta entropy)",
                StructuralStrideBytes = signals.BestStrideBytes,
            };
        }

        // Generic binary that is already near-8.0 bpb across windows and incompressible on 32 KiB probe
        if (initial.FormatId == "Binary"
            && signals.InteriorEntropyBitsPerByte >= 7.92
            && signals.MicroProbeRatio >= 0.995
            && signals.BestStrideEntropyReduction < 0.08)
        {
            return initial with
            {
                DisplayName = "High-Entropy Encrypted / Pre-Compressed Stream",
                MatchedEvidence = $"Multi-window entropy {signals.InteriorEntropyBitsPerByte:F2} bpb, micro-probe ratio {signals.MicroProbeRatio:F3}",
            };
        }

        // TIFF without explicit IFD0 compression tag that exhibits >= 7.85 bpb entropy and ~1.0 probe ratio is already compressed (e.g. LZW/Deflate/JPEG-in-TIFF)
        if (initial.FormatId == "TIFF"
            && initial.ContentClass == ContentClass.UncompressedRasterImage
            && signals.InteriorEntropyBitsPerByte >= 7.90
            && signals.MicroProbeRatio >= 0.99
            && (signals.PreprocessedMicroProbeRatio ?? 1.0) >= 0.98)
        {
            return initial with
            {
                ContentClass = ContentClass.CompressedImage,
                DisplayName = "TIFF Image (Pre-Compressed Payload)",
            };
        }

        _ = pathHint;
        return initial;
    }

    private static string ComputeSampleHash(
        IReadOnlyList<(WindowPosition Position, long Offset, byte[] Buffer, int Length)> windows,
        long totalLength,
        bool isFullHash)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (!isFullHash)
        {
            Span<byte> lenPrefix = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(lenPrefix, totalLength);
            hasher.AppendData(lenPrefix);
        }

        foreach (var w in windows)
        {
            if (w.Length > 0)
            {
                hasher.AppendData(w.Buffer, 0, w.Length);
            }
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static int ReadExactOrUntilEof(Stream stream, Span<byte> buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer[totalRead..]);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static async Task<int> ReadExactOrUntilEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static void ValidateOptions(ContentInspectorOptions options)
    {
        if (options.WindowSizeBytes < 512)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "WindowSizeBytes must be at least 512 bytes.");
        }

        if (options.MicroProbeSizeBytes < 512)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MicroProbeSizeBytes must be at least 512 bytes.");
        }
    }
}
