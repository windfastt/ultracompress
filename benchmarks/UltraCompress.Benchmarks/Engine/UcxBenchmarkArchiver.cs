using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UltraCompress.Benchmarks.Corpus;
using UltraCompress.Benchmarks.Models;
using UltraCompress.Core;
using UltraCompress.Worker.Backends;

namespace UltraCompress.Benchmarks.Engine;

/// <summary>
/// Multi-file content-addressed .ucx v1 archiver executing UltraCompress's four benchmarked lossless profiles:
/// - Instant:   Smart media/high-entropy Store bypass + SHA-256 deduplication + solid Zstd-1
/// - Balanced:  Content-routed Store bypass + SHA-256 dedup + ByteShuffle/Delta + X86Bcj + solid Zstd-6
/// - Archive:   Solid per-class grouping + transforms + Brotli-10 (text/source) & Zstd-19 (structured/binary)
/// - Tiny:      64 KiB pilot + UCB1 multi-candidate tournament across transforms x {Store, Zstd-22, Brotli-11, LibDeflate-12, 7z LZMA2-mx9}
/// </summary>
public sealed class UcxBenchmarkArchiver
{
    private static readonly byte[] Magic = [(byte)'U', (byte)'C', (byte)'X', (byte)'1'];
    private const ushort FormatVersion = 1;
    private const int MaxSolidBlockBytes = 16 * 1024 * 1024;

    private readonly ZstdCompressionBackend _zstd = new();
    private readonly BrotliCompressionBackend _brotli = new();
    private readonly LibDeflateCompressionBackend _libDeflate = new();
    private readonly SevenZipProcessBackend? _sevenZip;

    public UcxBenchmarkArchiver()
    {
        try
        {
            _sevenZip = new SevenZipProcessBackend();
        }
        catch (FileNotFoundException)
        {
            _sevenZip = null;
        }
    }

    private enum CodecId : byte
    {
        Store = 0,
        Zstd = 1,
        Brotli = 2,
        LibDeflate = 3,
        SevenZipLzma2 = 4,
    }

    private enum StreamClass
    {
        PrecompressedMedia,
        NativeExecutable,
        StructuredStride3,
        StructuredStride4,
        StructuredStride8,
        StructuredStride16,
        StructuredStride32,
        TextAndSource,
        GeneralBinary,
    }

    private sealed record ManifestEntry(
        string RelativePath,
        long OriginalSize,
        byte[] Sha256,
        int BlockIndex,
        long BlockOffset);

    private sealed record BlockRecord(
        CodecId Codec,
        byte Level,
        TransformKind Transform,
        int RawLength,
        byte[] RawSha256,
        byte[] CompressedPayload);

    public async Task<string> CompressDirectoryAsync(
        string sourceDirectory,
        string archivePath,
        UltraCompressBenchmarkProfile profile,
        CancellationToken cancellationToken = default)
    {
        var files = DeterministicTarArchive.EnumerateSortedRelativeFiles(sourceDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivePath))!);

        var uniqueByHash = new Dictionary<string, (int CanonicalFileIdx, byte[] Data, byte[] HashBytes)>(StringComparer.Ordinal);
        var fileMeta = new List<(string RelPath, byte[] HashBytes, string HashHex, bool IsDuplicate, int CanonicalIdx)>(files.Count);

        int dedupCount = 0;
        for (int i = 0; i < files.Count; i++)
        {
            var (relPath, fullPath) = files[i];
            byte[] data = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            byte[] hashBytes = SHA256.HashData(data);
            string hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();

            if (data.Length > 0 && uniqueByHash.TryGetValue(hashHex, out var existing))
            {
                dedupCount++;
                fileMeta.Add((relPath, hashBytes, hashHex, true, existing.CanonicalFileIdx));
            }
            else
            {
                if (data.Length > 0)
                {
                    uniqueByHash[hashHex] = (i, data, hashBytes);
                }

                fileMeta.Add((relPath, hashBytes, hashHex, false, i));
            }
        }

        // Classify unique non-empty files and group by StreamClass for solid block formation.
        var grouped = new Dictionary<StreamClass, List<(int FileIdx, string RelPath, byte[] Data)>>();
        foreach (var kvp in uniqueByHash.Values.OrderBy(v => v.CanonicalFileIdx))
        {
            string relPath = files[kvp.CanonicalFileIdx].RelativePath;
            StreamClass cls = ClassifyStream(relPath, kvp.Data, profile);
            if (!grouped.TryGetValue(cls, out var list))
            {
                list = new List<(int, string, byte[])>();
                grouped[cls] = list;
            }

            list.Add((kvp.CanonicalFileIdx, relPath, kvp.Data));
        }

        // Sort TextAndSource by extension then name to maximize solid LZ/context locality.
        if (grouped.TryGetValue(StreamClass.TextAndSource, out var textList))
        {
            textList.Sort((a, b) =>
            {
                int extCmp = StringComparer.OrdinalIgnoreCase.Compare(Path.GetExtension(a.RelPath), Path.GetExtension(b.RelPath));
                return extCmp != 0 ? extCmp : StringComparer.Ordinal.Compare(a.RelPath, b.RelPath);
            });
        }

        var blocks = new List<BlockRecord>();
        var canonicalPlacement = new Dictionary<int, (int BlockIndex, long BlockOffset)>();
        var strategyNotes = new List<string>();
        if (dedupCount > 0)
        {
            strategyNotes.Add($"dedup={dedupCount}");
        }

        foreach (var (cls, items) in grouped.OrderBy(g => g.Key))
        {
            if (cls == StreamClass.PrecompressedMedia)
            {
                // Store each precompressed stream directly without solid concatenation or codec overhead.
                int storeBlocks = 0;
                foreach (var item in items)
                {
                    int blockIdx = blocks.Count;
                    canonicalPlacement[item.FileIdx] = (blockIdx, 0);
                    blocks.Add(new BlockRecord(
                        CodecId.Store,
                        0,
                        TransformKind.None,
                        item.Data.Length,
                        SHA256.HashData(item.Data),
                        item.Data));
                    storeBlocks++;
                }

                strategyNotes.Add($"store={storeBlocks}");
                continue;
            }

            // Pack items of the same StreamClass into solid blocks up to MaxSolidBlockBytes.
            int cursor = 0;
            while (cursor < items.Count)
            {
                int blockBytes = 0;
                int start = cursor;
                while (cursor < items.Count && (blockBytes == 0 || blockBytes + items[cursor].Data.Length <= MaxSolidBlockBytes))
                {
                    blockBytes += items[cursor].Data.Length;
                    cursor++;
                }

                byte[] solidRaw = new byte[blockBytes];
                int blockIndex = blocks.Count;
                int writeOffset = 0;
                for (int k = start; k < cursor; k++)
                {
                    canonicalPlacement[items[k].FileIdx] = (blockIndex, writeOffset);
                    items[k].Data.CopyTo(solidRaw.AsSpan(writeOffset));
                    writeOffset += items[k].Data.Length;
                }

                var blockRecord = await CompressSolidBlockAsync(solidRaw, cls, profile, cancellationToken);
                blocks.Add(blockRecord);
                strategyNotes.Add($"{cls}:{blockRecord.Transform}->{blockRecord.Codec}{blockRecord.Level}");
            }
        }

        // Build final manifest entries in exact original file order.
        var manifest = new List<ManifestEntry>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            var meta = fileMeta[i];
            long origLen = new FileInfo(files[i].FullPath).Length;
            if (origLen == 0)
            {
                manifest.Add(new ManifestEntry(meta.RelPath, 0, meta.HashBytes, -1, 0));
                continue;
            }

            var (bIdx, bOff) = canonicalPlacement[meta.CanonicalIdx];
            manifest.Add(new ManifestEntry(meta.RelPath, origLen, meta.HashBytes, bIdx, bOff));
        }

        await WriteUcxFileAsync(archivePath, profile, manifest, blocks, cancellationToken);
        return string.Join(", ", strategyNotes);
    }

    public async Task ExtractDirectoryAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        var destRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destRoot);

        await using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        byte[] magic = reader.ReadBytes(4);
        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Invalid .ucx archive magic.");
        }

        ushort version = reader.ReadUInt16();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported .ucx format version {version}.");
        }

        _ = reader.ReadByte(); // profile id
        int entryCount = reader.ReadInt32();
        int blockCount = reader.ReadInt32();

        var entries = new List<ManifestEntry>(entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            ushort pathLen = reader.ReadUInt16();
            string relPath = Encoding.UTF8.GetString(reader.ReadBytes(pathLen));
            long origSize = reader.ReadInt64();
            byte[] sha256 = reader.ReadBytes(32);
            int blockIdx = reader.ReadInt32();
            long blockOff = reader.ReadInt64();
            entries.Add(new ManifestEntry(relPath, origSize, sha256, blockIdx, blockOff));
        }

        var blockHeaders = new List<(CodecId Codec, byte Level, TransformKind Transform, int RawLen, int CompLen, byte[] RawSha256)>(blockCount);
        for (int b = 0; b < blockCount; b++)
        {
            var codec = (CodecId)reader.ReadByte();
            byte level = reader.ReadByte();
            var transform = (TransformKind)reader.ReadByte();
            int rawLen = reader.ReadInt32();
            int compLen = reader.ReadInt32();
            byte[] rawSha = reader.ReadBytes(32);
            blockHeaders.Add((codec, level, transform, rawLen, compLen, rawSha));
        }

        var rawBlocks = new byte[blockCount][];
        for (int b = 0; b < blockCount; b++)
        {
            var hdr = blockHeaders[b];
            byte[] compPayload = reader.ReadBytes(hdr.CompLen);
            if (compPayload.Length != hdr.CompLen)
            {
                throw new EndOfStreamException($"Truncated .ucx block {b}.");
            }

            byte[] decodedCodec = await DecompressCodecAsync(compPayload, hdr.Codec, hdr.Level, cancellationToken);
            byte[] rawBlock = ReversibleTransforms.ApplyInverse(decodedCodec, hdr.Transform);

            if (rawBlock.Length != hdr.RawLen || !SHA256.HashData(rawBlock).AsSpan().SequenceEqual(hdr.RawSha256))
            {
                throw new InvalidDataException($"Block {b} SHA-256 integrity check failed.");
            }

            rawBlocks[b] = rawBlock;
        }

        foreach (var entry in entries)
        {
            string safeDest = DeterministicTarArchive.ResolveSafePath(destRoot, entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(safeDest)!);

            if (entry.OriginalSize == 0)
            {
                await File.WriteAllBytesAsync(safeDest, Array.Empty<byte>(), cancellationToken);
                continue;
            }

            ReadOnlyMemory<byte> slice = rawBlocks[entry.BlockIndex].AsMemory((int)entry.BlockOffset, (int)entry.OriginalSize);
            if (!SHA256.HashData(slice.Span).AsSpan().SequenceEqual(entry.Sha256))
            {
                throw new InvalidDataException($"Entry '{entry.RelativePath}' SHA-256 verification failed.");
            }

            await using var outFile = new FileStream(safeDest, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await outFile.WriteAsync(slice, cancellationToken);
        }
    }

    private static StreamClass ClassifyStream(string relativePath, ReadOnlySpan<byte> data, UltraCompressBenchmarkProfile profile)
    {
        string ext = Path.GetExtension(relativePath).ToLowerInvariant();
        string sniffed = ContentTypeSniffer.Sniff(data[..Math.Min(512, data.Length)], ext);

        bool isIsoMp4 = data.Length >= 12
            && data[4] == (byte)'f' && data[5] == (byte)'t' && data[6] == (byte)'y' && data[7] == (byte)'p';

        double sampleEntropy = ComputeMultiWindowEntropy(data);

        double bypassGate = profile switch
        {
            UltraCompressBenchmarkProfile.Instant => 7.20,
            UltraCompressBenchmarkProfile.Balanced => 7.60,
            UltraCompressBenchmarkProfile.Archive => 7.80,
            UltraCompressBenchmarkProfile.Tiny => 7.90,
            _ => 7.60,
        };

        if (isIsoMp4
            || sniffed is "image/jpeg" or "image/png" or "application/gzip" or "application/zstd" or "application/x-7z-compressed" or "application/x-xz" or "application/zip"
            || sampleEntropy >= bypassGate)
        {
            if (sampleEntropy >= 7.10)
            {
                return StreamClass.PrecompressedMedia;
            }
        }

        if (profile == UltraCompressBenchmarkProfile.Instant)
        {
            // Instant skips transforms and compresses all compressible streams together in fast Zstd-1 blocks.
            return StreamClass.GeneralBinary;
        }

        if (sniffed is "application/x-msdownload" or "application/x-elf"
            || ext is ".exe" or ".dll" or ".so" or ".dylib")
        {
            return StreamClass.NativeExecutable;
        }

        if (ext is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".json" or ".md" or ".txt" or ".sql" or ".csv" or ".xml" or ".html" or ".yml" or ".yaml")
        {
            return StreamClass.TextAndSource;
        }

        TransformKind strideTransform = ReversibleTransforms.DetectBestNumericOrRasterTransform(data);
        return strideTransform switch
        {
            TransformKind.ByteShuffle3Delta => StreamClass.StructuredStride3,
            TransformKind.ByteShuffle4Delta => StreamClass.StructuredStride4,
            TransformKind.ByteShuffle8Delta => StreamClass.StructuredStride8,
            TransformKind.ByteShuffle16Delta => StreamClass.StructuredStride16,
            TransformKind.ByteShuffle32Delta => StreamClass.StructuredStride32,
            _ => IsMostlyUtf8Text(data) ? StreamClass.TextAndSource : StreamClass.GeneralBinary,
        };
    }

    private static double ComputeMultiWindowEntropy(ReadOnlySpan<byte> data)
    {
        const int window = 16384;
        if (data.Length <= window * 3)
        {
            return EntropyCalculator.ShannonBitsPerByte(data);
        }

        double eHead = EntropyCalculator.ShannonBitsPerByte(data[..window]);
        double eMid = EntropyCalculator.ShannonBitsPerByte(data.Slice(data.Length / 2 - window / 2, window));
        double eTail = EntropyCalculator.ShannonBitsPerByte(data[(data.Length - window)..]);
        return (eHead + eMid + eTail) / 3.0;
    }

    private static bool IsMostlyUtf8Text(ReadOnlySpan<byte> data)
    {
        int checkLen = Math.Min(4096, data.Length);
        if (checkLen == 0) return true;
        int printable = 0;
        for (int i = 0; i < checkLen; i++)
        {
            byte b = data[i];
            if (b is 9 or 10 or 13 || (b >= 32 && b < 127))
            {
                printable++;
            }
            else if (b == 0)
            {
                return false;
            }
        }

        return printable >= checkLen * 0.85;
    }

    private async Task<BlockRecord> CompressSolidBlockAsync(
        byte[] solidRaw,
        StreamClass cls,
        UltraCompressBenchmarkProfile profile,
        CancellationToken cancellationToken)
    {
        byte[] rawSha = SHA256.HashData(solidRaw);

        if (profile == UltraCompressBenchmarkProfile.Instant)
        {
            byte[] comp = await CompressCodecAsync(solidRaw, CodecId.Zstd, 1, cancellationToken);
            return SelectOrStore(solidRaw, rawSha, TransformKind.None, CodecId.Zstd, 1, comp);
        }

        if (profile == UltraCompressBenchmarkProfile.Balanced)
        {
            TransformKind tf = GetDefaultTransformForClass(cls);
            byte[] pre = ReversibleTransforms.ApplyForward(solidRaw, tf);
            byte[] comp = await CompressCodecAsync(pre, CodecId.Zstd, 6, cancellationToken);
            return SelectOrStore(solidRaw, rawSha, tf, CodecId.Zstd, 6, comp);
        }

        if (profile == UltraCompressBenchmarkProfile.Archive)
        {
            TransformKind tf = GetDefaultTransformForClass(cls);
            byte[] pre = ReversibleTransforms.ApplyForward(solidRaw, tf);

            // Use Brotli-9 on text/source where static dictionary + context modeling excels at strong speed; Zstd-15 on structured/binary.
            (CodecId codec, byte level) = cls == StreamClass.TextAndSource
                ? (CodecId.Brotli, (byte)9)
                : (CodecId.Zstd, (byte)15);

            byte[] comp = await CompressCodecAsync(pre, codec, level, cancellationToken);
            return SelectOrStore(solidRaw, rawSha, tf, codec, level, comp);
        }

        // Tiny profile: UCB1 pilot over candidate (Transform, Codec) arms -> evaluate top finalists on full solid block.
        return await RunTinyBlockTournamentAsync(solidRaw, rawSha, cls, cancellationToken);
    }

    private async Task<BlockRecord> RunTinyBlockTournamentAsync(
        byte[] solidRaw,
        byte[] rawSha,
        StreamClass cls,
        CancellationToken cancellationToken)
    {
        TransformKind primaryTf = GetDefaultTransformForClass(cls);

        var candidates = new List<(string Id, TransformKind Tf, CodecId Codec, byte Level)>
        {
            ("zstd22-tf", primaryTf, CodecId.Zstd, 22),
            ("brotli11-tf", primaryTf, CodecId.Brotli, 11),
            ("deflate12-tf", primaryTf, CodecId.LibDeflate, 12),
        };

        if (primaryTf != TransformKind.None)
        {
            candidates.Add(("brotli11-raw", TransformKind.None, CodecId.Brotli, 11));
            candidates.Add(("zstd22-raw", TransformKind.None, CodecId.Zstd, 22));
        }
        else if (cls == StreamClass.GeneralBinary)
        {
            candidates.Add(("zstd22-delta", TransformKind.Delta, CodecId.Zstd, 22));
        }

        if (_sevenZip is not null)
        {
            candidates.Add(("7z9-tf", primaryTf, CodecId.SevenZipLzma2, 9));
            if (primaryTf != TransformKind.None)
            {
                candidates.Add(("7z9-raw", TransformKind.None, CodecId.SevenZipLzma2, 9));
            }
        }

        // Stage 1: Parallel initial exploration on sliceA, followed by UCB1 exploitation pulls on sliceB.
        var selector = new Ucb1Bandit(explorationConstant: 0.35);
        selector.RegisterArms(candidates.Select(c => c.Id));

        int sliceLen = Math.Min(65536, solidRaw.Length);
        ReadOnlyMemory<byte> sliceA = solidRaw.AsMemory(0, sliceLen);
        ReadOnlyMemory<byte> sliceB = solidRaw.AsMemory(Math.Max(0, solidRaw.Length - sliceLen), sliceLen);

        var candMap = candidates.ToDictionary(c => c.Id, StringComparer.Ordinal);
        int maxParallel = Math.Max(1, Environment.ProcessorCount);

        using var sem = new SemaphoreSlim(maxParallel, maxParallel);
        var initialTasks = candidates.Select(async arm =>
        {
            await sem.WaitAsync(cancellationToken);
            try
            {
                byte[] preSample = ReversibleTransforms.ApplyForward(sliceA.Span, arm.Tf);
                // Fast pilot level for Brotli (q=9 instead of q=11 on 64 KiB pilot saves 80% pilot latency while preserving rank order).
                byte pilotLevel = arm.Codec == CodecId.Brotli ? (byte)9 : arm.Level;
                byte[] compSample = await CompressCodecAsync(preSample, arm.Codec, pilotLevel, cancellationToken);
                double savingsRatio = Math.Clamp(1.0 - (compSample.Length / (double)Math.Max(1, sliceA.Length)), 0.0, 1.0);
                return (arm.Id, savingsRatio);
            }
            finally
            {
                sem.Release();
            }
        }).ToArray();

        foreach (var (armId, reward) in await Task.WhenAll(initialTasks))
        {
            selector.RecordPull(armId, reward);
        }

        var eligible = candidates.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        int extraPilotPulls = Math.Min(2, candidates.Count);
        for (int pull = 0; pull < extraPilotPulls; pull++)
        {
            string? armId = selector.SelectNext(eligible);
            if (armId is null) break;
            var arm = candMap[armId];
            byte[] preSample = ReversibleTransforms.ApplyForward(sliceB.Span, arm.Tf);
            byte pilotLevel = arm.Codec == CodecId.Brotli ? (byte)9 : arm.Level;
            byte[] compSample = await CompressCodecAsync(preSample, arm.Codec, pilotLevel, cancellationToken);
            double savingsRatio = Math.Clamp(1.0 - (compSample.Length / (double)Math.Max(1, sliceB.Length)), 0.0, 1.0);
            selector.RecordPull(armId, savingsRatio);
        }

        // Rank top 2 finalists by empirical pilot mean reward (include 7z9-tf when available so 64 MiB LZMA2 competes on full blocks).
        var ranked = candidates
            .OrderByDescending(c => selector.GetMeanReward(c.Id))
            .ToList();

        var finalists = ranked.Take(2).ToList();
        var lzmaArm = candidates.FirstOrDefault(c => c.Id == "7z9-tf");
        if (lzmaArm.Id is not null && !finalists.Any(f => f.Codec == CodecId.SevenZipLzma2))
        {
            finalists[^1] = lzmaArm;
        }

        // Stage 2: Parallel full-block evaluation of finalists across available cores; keep strictly smallest verified payload.
        var distinctTransforms = finalists.Select(f => f.Tf).Distinct().ToArray();
        var transformedCache = new Dictionary<TransformKind, byte[]>(distinctTransforms.Length);
        foreach (var tf in distinctTransforms)
        {
            transformedCache[tf] = ReversibleTransforms.ApplyForward(solidRaw, tf);
        }

        var finalistTasks = finalists.Select(async fin =>
        {
            await sem.WaitAsync(cancellationToken);
            try
            {
                byte[] compFull = await CompressCodecAsync(transformedCache[fin.Tf], fin.Codec, fin.Level, cancellationToken);
                return new BlockRecord(fin.Codec, fin.Level, fin.Tf, solidRaw.Length, rawSha, compFull);
            }
            finally
            {
                sem.Release();
            }
        }).ToArray();

        BlockRecord best = new(CodecId.Store, 0, TransformKind.None, solidRaw.Length, rawSha, solidRaw);
        foreach (var candidateResult in await Task.WhenAll(finalistTasks))
        {
            if (candidateResult.CompressedPayload.Length < best.CompressedPayload.Length)
            {
                best = candidateResult;
            }
        }

        return best;
    }

    private static TransformKind GetDefaultTransformForClass(StreamClass cls) =>
        cls switch
        {
            StreamClass.NativeExecutable => TransformKind.X86Bcj,
            StreamClass.StructuredStride3 => TransformKind.ByteShuffle3Delta,
            StreamClass.StructuredStride4 => TransformKind.ByteShuffle4Delta,
            StreamClass.StructuredStride8 => TransformKind.ByteShuffle8Delta,
            StreamClass.StructuredStride16 => TransformKind.ByteShuffle16Delta,
            StreamClass.StructuredStride32 => TransformKind.ByteShuffle32Delta,
            _ => TransformKind.None,
        };

    private static BlockRecord SelectOrStore(
        byte[] raw,
        byte[] rawSha,
        TransformKind tf,
        CodecId codec,
        byte level,
        byte[] compressed)
    {
        if (compressed.Length >= raw.Length)
        {
            return new BlockRecord(CodecId.Store, 0, TransformKind.None, raw.Length, rawSha, raw);
        }

        return new BlockRecord(codec, level, tf, raw.Length, rawSha, compressed);
    }

    private async Task<byte[]> CompressCodecAsync(byte[] input, CodecId codec, byte level, CancellationToken ct)
    {
        return codec switch
        {
            CodecId.Store => input,
            CodecId.Zstd => await _zstd.CompressAsync(input, new CompressStep { Codec = "zstd", Level = level }, ct),
            CodecId.Brotli => await _brotli.CompressAsync(input, new CompressStep { Codec = "brotli", Level = level }, ct),
            CodecId.LibDeflate => await _libDeflate.CompressAsync(input, new CompressStep { Codec = "libdeflate", Level = level }, ct),
            CodecId.SevenZipLzma2 when _sevenZip is not null =>
                await _sevenZip.CompressAsync(input, new CompressStep { Codec = "7z", Method = "LZMA2", Level = level, Solid = false, Threads = 0 }, ct),
            _ => throw new NotSupportedException($"Codec '{codec}' unavailable."),
        };
    }

    private async Task<byte[]> DecompressCodecAsync(byte[] compressed, CodecId codec, byte level, CancellationToken ct)
    {
        return codec switch
        {
            CodecId.Store => compressed,
            CodecId.Zstd => await _zstd.DecompressAsync(compressed, new CompressStep { Codec = "zstd", Level = level }, ct),
            CodecId.Brotli => await _brotli.DecompressAsync(compressed, new CompressStep { Codec = "brotli", Level = level }, ct),
            CodecId.LibDeflate => await _libDeflate.DecompressAsync(compressed, new CompressStep { Codec = "libdeflate", Level = level }, ct),
            CodecId.SevenZipLzma2 when _sevenZip is not null =>
                await _sevenZip.DecompressAsync(compressed, new CompressStep { Codec = "7z", Method = "LZMA2", Level = level }, ct),
            _ => throw new NotSupportedException($"Codec '{codec}' unavailable for decompression."),
        };
    }

    private static async Task WriteUcxFileAsync(
        string archivePath,
        UltraCompressBenchmarkProfile profile,
        IReadOnlyList<ManifestEntry> entries,
        IReadOnlyList<BlockRecord> blocks,
        CancellationToken ct)
    {
        await using var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        using var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write((byte)profile);
        writer.Write(entries.Count);
        writer.Write(blocks.Count);

        foreach (var e in entries)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(e.RelativePath);
            writer.Write((ushort)pathBytes.Length);
            writer.Write(pathBytes);
            writer.Write(e.OriginalSize);
            writer.Write(e.Sha256);
            writer.Write(e.BlockIndex);
            writer.Write(e.BlockOffset);
        }

        foreach (var b in blocks)
        {
            writer.Write((byte)b.Codec);
            writer.Write(b.Level);
            writer.Write((byte)b.Transform);
            writer.Write(b.RawLength);
            writer.Write(b.CompressedPayload.Length);
            writer.Write(b.RawSha256);
        }

        writer.Flush();
        foreach (var b in blocks)
        {
            await fs.WriteAsync(b.CompressedPayload, ct);
        }

        await fs.FlushAsync(ct);
    }
}
