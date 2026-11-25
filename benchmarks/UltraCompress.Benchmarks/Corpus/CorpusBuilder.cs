using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UltraCompress.Benchmarks.Models;
using UltraCompress.Core;

namespace UltraCompress.Benchmarks.Corpus;

/// <summary>
/// Deterministic multi-category benchmark corpus generator.
/// Given the same <see cref="BenchmarkScale"/>, produces byte-identical directory trees and USTAR .tar files
/// across machines and operating systems using fixed SplitMix64/Xoshiro256** seeds.
/// </summary>
public static class CorpusBuilder
{
    public const string VideoStreamCategory = "video-stream";
    public const string SourceRepoCategory = "source-repo";
    public const string DatabaseDumpCategory = "database-dump";
    public const string PhotoCollectionCategory = "photo-collection";
    public const string NativeBinariesCategory = "native-binaries";
    public const string MixedWorkspaceCategory = "mixed-workspace";

    public static readonly IReadOnlyList<string> AllCategories =
    [
        VideoStreamCategory,
        SourceRepoCategory,
        DatabaseDumpCategory,
        PhotoCollectionCategory,
        NativeBinariesCategory,
        MixedWorkspaceCategory,
    ];

    public static IReadOnlyList<CorpusCategoryManifest> BuildAll(
        string rootOutputDirectory,
        BenchmarkScale scale = BenchmarkScale.Standard,
        IReadOnlyCollection<string>? categoryFilter = null)
    {
        var fullRoot = Path.GetFullPath(rootOutputDirectory);
        if (Directory.Exists(fullRoot))
        {
            Directory.Delete(fullRoot, recursive: true);
        }

        Directory.CreateDirectory(fullRoot);

        var selected = AllCategories
            .Where(c => categoryFilter is null || categoryFilter.Count == 0 || categoryFilter.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var manifests = new List<CorpusCategoryManifest>(selected.Count);
        foreach (var category in selected)
        {
            manifests.Add(BuildCategory(fullRoot, category, scale));
        }

        return manifests;
    }

    public static string ComputeAggregateCorpusSha256(IEnumerable<CorpusCategoryManifest> categories)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var cat in categories.OrderBy(c => c.Category, StringComparer.Ordinal))
        {
            string line = $"{cat.Category}|{cat.Seed:x16}|{cat.OriginalBytes}|{cat.ManifestSha256}|{cat.TarSha256}\n";
            hasher.AppendData(Encoding.UTF8.GetBytes(line));
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    public static CorpusCategoryManifest BuildCategory(string rootOutputDirectory, string category, BenchmarkScale scale)
    {
        var catDir = Path.Combine(rootOutputDirectory, "files", category);
        var tarPath = Path.Combine(rootOutputDirectory, "tar", $"{category}.tar");
        Directory.CreateDirectory(catDir);
        Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);

        double factor = scale switch
        {
            BenchmarkScale.Smoke => 0.125,
            BenchmarkScale.Standard => 0.55,
            BenchmarkScale.Large => 2.5,
            _ => 0.55,
        };

        (ulong seed, string description) = category switch
        {
            VideoStreamCategory => (0x554C545241_0001UL,
                "ISO-BMFF MP4 container headers (ftyp/moov/trak) + H.264 NAL framing + high-entropy compressed media frames (~7.998 bpb)"),
            SourceRepoCategory => (0x554C545241_0002UL,
                "Multi-file C#, TypeScript, JSON, and Markdown repository tree with shared domain identifiers and exact duplicate vendored SDK files"),
            DatabaseDumpCategory => (0x554C545241_0003UL,
                "Structured SQL dump + telemetry CSV export + fixed 8-byte-stride binary column pages with strong numeric correlation"),
            PhotoCollectionCategory => (0x554C545241_0004UL,
                "Heterogeneous camera import mixing high-entropy JPEG scans (~7.98 bpb), uncompressed 24-bit BMP & 32-bit RGBA TIFF rasters, and duplicate burst shots"),
            NativeBinariesCategory => (0x554C545241_0005UL,
                "PE (.dll/.exe) and ELF (.so) x86-64 binaries with CALL/JMP rel32 code sections targeting shared subroutines plus UTF-8/UTF-16 resource tables"),
            MixedWorkspaceCategory => (0x554C545241_0006UL,
                "Combined heterogeneous workspace containing video segments, source repo + duplicate vendor tree, database dump, photo collection, and native x86-64 binaries"),
            _ => throw new ArgumentException($"Unknown corpus category '{category}'.", nameof(category)),
        };

        switch (category)
        {
            case VideoStreamCategory:
                GenerateVideoStreamCategory(catDir, seed, factor);
                break;
            case SourceRepoCategory:
                GenerateSourceRepoCategory(catDir, seed, factor);
                break;
            case DatabaseDumpCategory:
                GenerateDatabaseDumpCategory(catDir, seed, factor);
                break;
            case PhotoCollectionCategory:
                GeneratePhotoCollectionCategory(catDir, seed, factor);
                break;
            case NativeBinariesCategory:
                GenerateNativeBinariesCategory(catDir, seed, factor);
                break;
            case MixedWorkspaceCategory:
                GenerateMixedWorkspaceCategory(catDir, seed, factor);
                break;
        }

        DeterministicTarArchive.CreateFromDirectory(catDir, tarPath);
        return BuildManifest(category, description, seed, catDir, tarPath);
    }

    private static CorpusCategoryManifest BuildManifest(
        string category,
        string description,
        ulong seed,
        string catDir,
        string tarPath)
    {
        var sortedFiles = DeterministicTarArchive.EnumerateSortedRelativeFiles(catDir);
        var entries = new List<CorpusFileEntry>(sortedFiles.Count);
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);

        int duplicateFiles = 0;
        long duplicateBytes = 0;
        long totalBytes = 0;
        double weightedEntropy = 0;

        using var manifestHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var (relPath, fullPath) in sortedFiles)
        {
            byte[] data = File.ReadAllBytes(fullPath);
            string sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            double entropy = EntropyCalculator.ShannonBitsPerByte(data);

            if (!seenHashes.Add(sha256))
            {
                duplicateFiles++;
                duplicateBytes += data.LongLength;
            }

            totalBytes += data.LongLength;
            weightedEntropy += entropy * data.LongLength;

            entries.Add(new CorpusFileEntry(relPath, data.LongLength, sha256, Math.Round(entropy, 5)));
            manifestHasher.AppendData(Encoding.UTF8.GetBytes($"{relPath}\t{data.LongLength}\t{sha256}\n"));
        }

        string manifestSha256 = Convert.ToHexString(manifestHasher.GetHashAndReset()).ToLowerInvariant();
        using var tarStream = File.OpenRead(tarPath);
        string tarSha256 = Convert.ToHexString(SHA256.HashData(tarStream)).ToLowerInvariant();
        long tarBytes = new FileInfo(tarPath).Length;

        double meanEntropy = totalBytes > 0 ? Math.Round(weightedEntropy / totalBytes, 5) : 0;

        return new CorpusCategoryManifest
        {
            Category = category,
            Description = description,
            Seed = seed,
            FileCount = entries.Count,
            DuplicateFileCount = duplicateFiles,
            DuplicateBytes = duplicateBytes,
            OriginalBytes = totalBytes,
            TarBytes = tarBytes,
            MeanEntropyBitsPerByte = meanEntropy,
            ManifestSha256 = manifestSha256,
            TarSha256 = tarSha256,
            Files = entries,
            DirectoryPath = catDir,
            TarFilePath = tarPath,
        };
    }

    private static void GenerateVideoStreamCategory(string dir, ulong seed, double factor)
    {
        var rng = new DeterministicRng(seed);
        int segmentBytes = Math.Max(256 * 1024, (int)(4 * 1024 * 1024 * factor));

        WriteSyntheticMp4(Path.Combine(dir, "captures", "cam_a_1080p60_segment01.mp4"), rng, segmentBytes, 1920, 1080);
        WriteSyntheticMp4(Path.Combine(dir, "captures", "cam_b_1080p60_segment02.mp4"), rng, segmentBytes, 1920, 1080);
    }

    private static void WriteSyntheticMp4(string path, DeterministicRng rng, int totalBytes, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] buffer = new byte[totalBytes];
        Span<byte> span = buffer;

        // 1. 'ftyp' box (32 bytes): ISO Base Media File Format (MP4)
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0, 4), 32);
        Encoding.ASCII.GetBytes("ftypisom").CopyTo(span.Slice(4, 8));
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(12, 4), 0x00000200);
        Encoding.ASCII.GetBytes("isomiso2avc1mp41").CopyTo(span.Slice(16, 16));

        // 2. 'moov' box (224 bytes) with 'mvhd' and 'trak' metadata boxes
        int moovOffset = 32;
        int moovSize = 224;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(moovOffset, 4), (uint)moovSize);
        Encoding.ASCII.GetBytes("moov").CopyTo(span.Slice(moovOffset + 4, 4));

        int mvhdOffset = moovOffset + 8;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(mvhdOffset, 4), 108);
        Encoding.ASCII.GetBytes("mvhd").CopyTo(span.Slice(mvhdOffset + 4, 4));
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(mvhdOffset + 20, 4), 60000); // timescale
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(mvhdOffset + 24, 4), 600000); // duration
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(mvhdOffset + 28, 4), 0x00010000); // rate 1.0

        int trakOffset = mvhdOffset + 108;
        int trakSize = moovSize - 8 - 108;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(trakOffset, 4), (uint)trakSize);
        Encoding.ASCII.GetBytes("trak").CopyTo(span.Slice(trakOffset + 4, 4));
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(trakOffset + 8, 4), (uint)(trakSize - 8));
        Encoding.ASCII.GetBytes("tkhd").CopyTo(span.Slice(trakOffset + 12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(trakOffset + 84, 4), (uint)(width << 16));
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(trakOffset + 88, 4), (uint)(height << 16));

        // 3. 'mdat' box with H.264 NAL start codes every 128 KiB and high-entropy CABAC payload (~7.998 bpb)
        int mdatOffset = moovOffset + moovSize;
        int mdatSize = totalBytes - mdatOffset;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(mdatOffset, 4), (uint)mdatSize);
        Encoding.ASCII.GetBytes("mdat").CopyTo(span.Slice(mdatOffset + 4, 4));

        Span<byte> payload = span[(mdatOffset + 8)..];
        rng.FillBytes(payload);

        // Stamp realistic Annex-B / length-prefixed NAL headers every 131072 bytes so container structure is authentic
        // while keeping overall entropy >= 7.99 bpb.
        for (int pos = 0; pos + 8 < payload.Length; pos += 131072)
        {
            uint nalLen = (uint)Math.Min(131064, payload.Length - pos - 4);
            BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(pos, 4), nalLen);
            payload[pos + 4] = (pos / 131072) % 8 == 0 ? (byte)0x65 : (byte)0x41; // IDR vs non-IDR slice
        }

        File.WriteAllBytes(path, buffer);
    }

    private static void GenerateSourceRepoCategory(string dir, ulong seed, double factor)
    {
        var rng = new DeterministicRng(seed);
        int moduleCount = Math.Max(6, (int)Math.Round(48 * factor));
        int tsModuleCount = Math.Max(6, (int)Math.Round(36 * factor));
        int docCount = Math.Max(4, (int)Math.Round(20 * factor));

        string[] domainNouns =
        [
            "ArchiveManifest", "BlockRouter", "ChecksumVerifier", "ChunkPipeline", "CodecRegistry",
            "ContentInspector", "DeduplicationIndex", "EntropyWindow", "FrameHeader", "JobJournal",
            "LzmaSession", "PayloadSegment", "ProfileContract", "QueueDispatcher", "RecipeStore",
            "SolidBatchBuilder", "StrideAnalyzer", "StreamEnvelope", "TournamentBandit", "ZstdContext",
        ];

        string[] verbs =
        [
            "Analyze", "Build", "Compute", "Decode", "Dispatch", "Encode", "Evaluate",
            "Extract", "Inspect", "Normalize", "Plan", "Reconcile", "Schedule", "Validate", "Verify",
        ];

        // 1. C# source & test files
        for (int i = 0; i < moduleCount; i++)
        {
            string noun = domainNouns[i % domainNouns.Length];
            string sub = i % 2 == 0 ? "Core" : "Storage";
            string code = SynthesizeCSharpFile(rng, $"UltraWorkspace.{sub}", $"{noun}Service_{i:D2}", domainNouns, verbs, linesTarget: 320);
            WriteUtf8(Path.Combine(dir, "src", sub, $"{noun}Service_{i:D2}.cs"), code);

            string testCode = SynthesizeCSharpFile(rng, $"UltraWorkspace.Tests.{sub}", $"{noun}Service_{i:D2}Tests", domainNouns, verbs, linesTarget: 180);
            WriteUtf8(Path.Combine(dir, "tests", sub, $"{noun}Service_{i:D2}Tests.cs"), testCode);
        }

        // 2. TypeScript / TSX frontend files
        for (int i = 0; i < tsModuleCount; i++)
        {
            string noun = domainNouns[(i * 3) % domainNouns.Length];
            string tsx = SynthesizeTypeScriptFile(rng, $"{noun}Panel_{i:D2}", domainNouns, verbs, linesTarget: 220);
            WriteUtf8(Path.Combine(dir, "web", "src", "components", $"{noun}Panel_{i:D2}.tsx"), tsx);
        }

        // 3. Structured JSON schemas & package lockfile
        string lockJson = SynthesizePackageLockJson(rng, domainNouns, packageCount: Math.Max(30, (int)(220 * factor)));
        WriteUtf8(Path.Combine(dir, "web", "package-lock.json"), lockJson);

        for (int i = 0; i < 4; i++)
        {
            WriteUtf8(Path.Combine(dir, "config", $"appsettings.env{i}.json"), SynthesizePackageLockJson(rng, domainNouns, packageCount: Math.Max(12, (int)(45 * factor))));
        }

        // 4. Markdown technical docs
        for (int i = 0; i < docCount; i++)
        {
            string md = SynthesizeMarkdownDoc(rng, $"Architecture-RFC-{i + 100}", domainNouns, verbs, sections: 14);
            WriteUtf8(Path.Combine(dir, "docs", $"rfc-{i + 100:D3}.md"), md);
        }

        // 5. Exact duplicate vendored SDK tree across 3 workspace packages (realistic monorepo duplication)
        int vendorFileCount = Math.Max(3, (int)Math.Round(14 * factor));
        for (int v = 0; v < vendorFileCount; v++)
        {
            string vendorTs = SynthesizeTypeScriptFile(rng, $"TelemetrySdkClient_{v:D2}", domainNouns, verbs, linesTarget: 260);
            byte[] vendorBytes = Encoding.UTF8.GetBytes(vendorTs);

            WriteBytes(Path.Combine(dir, "vendor", "telemetry-sdk", $"client_{v:D2}.ts"), vendorBytes);
            WriteBytes(Path.Combine(dir, "packages", "cli", "vendor", "telemetry-sdk", $"client_{v:D2}.ts"), vendorBytes);
            WriteBytes(Path.Combine(dir, "packages", "desktop", "vendor", "telemetry-sdk", $"client_{v:D2}.ts"), vendorBytes);
        }
    }

    private static string SynthesizeCSharpFile(
        DeterministicRng rng,
        string ns,
        string className,
        string[] nouns,
        string[] verbs,
        int linesTarget)
    {
        var sb = new StringBuilder(linesTarget * 64);
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.IO;");
        sb.AppendLine("using System.Security.Cryptography;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"public sealed class {className}");
        sb.AppendLine("{");
        sb.AppendLine("    private readonly Dictionary<string, long> _metrics = new(StringComparer.Ordinal);");
        sb.AppendLine();

        int methodIdx = 0;
        int approxLines = 14;
        while (approxLines < linesTarget)
        {
            string verb = rng.Pick(verbs);
            string nounA = rng.Pick(nouns);
            string nounB = rng.Pick(nouns);
            int constVal = rng.NextInt(16, 65536);

            sb.AppendLine($"    public async ValueTask<long> {verb}{nounA}_{methodIdx:D2}Async(ReadOnlyMemory<byte> payload, string correlationId, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        ct.ThrowIfCancellationRequested();");
            sb.AppendLine($"        if (payload.IsEmpty) return {constVal}L;");
            sb.AppendLine($"        var digest = SHA256.HashData(payload.Span);");
            sb.AppendLine($"        long accumulator = {constVal}L ^ digest[0] ^ ((long)digest[1] << 8);");
            sb.AppendLine($"        for (int offset = 0; offset < payload.Length; offset += {rng.NextInt(64, 512)})");
            sb.AppendLine("        {");
            sb.AppendLine($"            accumulator = unchecked((accumulator * 31L) + payload.Span[offset]);");
            sb.AppendLine("        }");
            sb.AppendLine($"        _metrics[$\"{nounB}:{{correlationId}}\"] = accumulator;");
            sb.AppendLine("        await Task.CompletedTask;");
            sb.AppendLine("        return accumulator;");
            sb.AppendLine("    }");
            sb.AppendLine();

            methodIdx++;
            approxLines += 16;
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string SynthesizeTypeScriptFile(
        DeterministicRng rng,
        string componentName,
        string[] nouns,
        string[] verbs,
        int linesTarget)
    {
        var sb = new StringBuilder(linesTarget * 60);
        sb.AppendLine("import React, { useMemo, useState } from 'react';");
        sb.AppendLine();
        sb.AppendLine($"export interface {componentName}Props {{");
        sb.AppendLine("  readonly jobId: string;");
        sb.AppendLine("  readonly originalBytes: number;");
        sb.AppendLine("  readonly compressedBytes: number;");
        sb.AppendLine("  readonly strategyLabel: string;");
        sb.AppendLine("}");
        sb.AppendLine();

        int idx = 0;
        int lines = 10;
        while (lines < linesTarget)
        {
            string verb = rng.Pick(verbs).ToLowerInvariant();
            string noun = rng.Pick(nouns);
            int factor = rng.NextInt(2, 1024);
            sb.AppendLine($"export function {verb}{noun}Metric_{idx:D2}(props: {componentName}Props): string {{");
            sb.AppendLine("  const ratio = props.compressedBytes > 0 ? props.originalBytes / props.compressedBytes : 1.0;");
            sb.AppendLine("  const savedBytes = Math.max(0, props.originalBytes - props.compressedBytes);");
            sb.AppendLine($"  const score = (ratio * {factor}).toFixed(2);");
            sb.AppendLine($"  return `[${{props.jobId}}] {noun} ${{props.strategyLabel}} ratio=${{ratio.toFixed(3)}} saved=${{savedBytes}} score=${{score}}`;");
            sb.AppendLine("}");
            sb.AppendLine();
            idx++;
            lines += 8;
        }

        return sb.ToString();
    }

    private static string SynthesizePackageLockJson(DeterministicRng rng, string[] nouns, int packageCount)
    {
        var sb = new StringBuilder(packageCount * 220);
        sb.AppendLine("{");
        sb.AppendLine("  \"name\": \"@ultracompress/workspace\",");
        sb.AppendLine("  \"lockfileVersion\": 3,");
        sb.AppendLine("  \"requires\": true,");
        sb.AppendLine("  \"packages\": {");

        for (int i = 0; i < packageCount; i++)
        {
            string noun = rng.Pick(nouns).ToLowerInvariant();
            int major = rng.NextInt(1, 9);
            int minor = rng.NextInt(0, 24);
            int patch = rng.NextInt(0, 15);
            string comma = i + 1 < packageCount ? "," : string.Empty;

            sb.AppendLine($"    \"node_modules/@ultra/{noun}-pkg-{i:D3}\": {{");
            sb.AppendLine($"      \"version\": \"{major}.{minor}.{patch}\",");
            sb.AppendLine($"      \"resolved\": \"https://registry.npmjs.org/@ultra/{noun}-pkg-{i:D3}/-/{noun}-pkg-{i:D3}-{major}.{minor}.{patch}.tgz\",");
            sb.AppendLine($"      \"integrity\": \"sha512-{rng.NextUInt64():x16}{rng.NextUInt64():x16}{rng.NextUInt64():x16}\",");
            sb.AppendLine("      \"license\": \"MIT\"");
            sb.AppendLine($"    }}{comma}");
        }

        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string SynthesizeMarkdownDoc(
        DeterministicRng rng,
        string title,
        string[] nouns,
        string[] verbs,
        int sections)
    {
        var sb = new StringBuilder(sections * 500);
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        for (int s = 0; s < sections; s++)
        {
            string nounA = rng.Pick(nouns);
            string nounB = rng.Pick(nouns);
            string verb = rng.Pick(verbs);
            sb.AppendLine($"## {s + 1}. {verb} {nounA} and {nounB}");
            sb.AppendLine();
            sb.AppendLine($"The `{nounA}` subsystem coordinates deterministic block scheduling with `{nounB}` under strict bit-exact SHA-256 verification.");
            sb.AppendLine();
            sb.AppendLine("| Metric | Baseline | Target SLA | Verification |");
            sb.AppendLine("|--------|----------|------------|--------------|");
            sb.AppendLine($"| Throughput | {rng.NextInt(80, 220)} MB/s | {rng.NextInt(300, 900)} MB/s | SHA-256 Roundtrip |");
            sb.AppendLine($"| Peak Working Set | {rng.NextInt(64, 256)} MB | <= 128 MB | Process RSS Probe |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void GenerateDatabaseDumpCategory(string dir, ulong seed, double factor)
    {
        var rng = new DeterministicRng(seed);

        // 1a. Fixed 8-byte-stride binary columnar telemetry stream (IEEE-754 double / int64 series)
        int seriesCount = Math.Max(32768, (int)(260_000 * factor));
        byte[] columnar8 = new byte[seriesCount * 8];
        Span<byte> col8Span = columnar8;
        long baseMetric = 98_765_432_100L;
        for (int s = 0; s < seriesCount; s++)
        {
            baseMetric += rng.NextInt(-120, 125);
            BinaryPrimitives.WriteInt64LittleEndian(col8Span.Slice(s * 8, 8), baseMetric);
        }

        WriteBytes(Path.Combine(dir, "pages", "timeseries_columnar_8b.bin"), columnar8);

        // 1b. Fixed 32-byte-stride binary row pages (four 8-byte correlated numeric columns per row)
        int recordCount = Math.Max(8192, (int)(75_000 * factor));
        byte[] binaryPages = new byte[recordCount * 32];
        Span<byte> pageSpan = binaryPages;

        long timestampUs = 1_795_564_800_000_000L;
        long priceMicros = 142_500_000L;
        ulong seqId = 50_000_000UL;

        for (int r = 0; r < recordCount; r++)
        {
            timestampUs += 1000 + rng.NextInt(0, 64);
            priceMicros += rng.NextInt(-25, 26);
            seqId++;
            ulong tenantAndFlags = ((ulong)(uint)(1000 + (r / 64) % 250) << 32) | (uint)(0x0100_0000 | (r & 0xFF));

            int baseOff = r * 32;
            BinaryPrimitives.WriteInt64LittleEndian(pageSpan.Slice(baseOff, 8), timestampUs);
            BinaryPrimitives.WriteInt64LittleEndian(pageSpan.Slice(baseOff + 8, 8), priceMicros);
            BinaryPrimitives.WriteUInt64LittleEndian(pageSpan.Slice(baseOff + 16, 8), seqId);
            BinaryPrimitives.WriteUInt64LittleEndian(pageSpan.Slice(baseOff + 24, 8), tenantAndFlags);
        }

        WriteBytes(Path.Combine(dir, "pages", "timeseries_metrics_pages.bin"), binaryPages);

        // 2. Structured SQL dump
        int sqlRows = Math.Max(2500, (int)(20_000 * factor));
        var sql = new StringBuilder(sqlRows * 115);
        sql.AppendLine("-- UltraCompress Deterministic PostgreSQL/SQLite Benchmark Dump");
        sql.AppendLine("BEGIN TRANSACTION;");
        sql.AppendLine("CREATE TABLE ledger_events (id BIGINT PRIMARY KEY, ts_utc TEXT NOT NULL, tenant_code TEXT NOT NULL, sku TEXT NOT NULL, quantity INT NOT NULL, amount_cents BIGINT NOT NULL, status TEXT NOT NULL);");

        string[] tenants = ["ACME-NORTH", "ACME-EU", "GLOBEX-US", "INET-APAC", "VANDELAY-HQ", "SOYLENT-WEST"];
        string[] skus = ["UCX-PRO-SEAT", "UCX-ENT-NODE", "STORAGE-TIER-A", "ARCHIVE-VAULT-10T", "COMPUTE-BURST-XL"];
        string[] statuses = ["SETTLED", "PENDING_AUDIT", "RECONCILED", "ARCHIVED"];

        for (int i = 0; i < sqlRows; i++)
        {
            if (i % 50 == 0)
            {
                if (i > 0) sql.AppendLine(";");
                sql.AppendLine("INSERT INTO ledger_events (id, ts_utc, tenant_code, sku, quantity, amount_cents, status) VALUES");
            }
            else
            {
                sql.AppendLine(",");
            }

            string tenant = tenants[rng.NextInt(0, tenants.Length)];
            string sku = skus[rng.NextInt(0, skus.Length)];
            string status = statuses[rng.NextInt(0, statuses.Length)];
            int qty = rng.NextInt(1, 250);
            long cents = qty * 4900L + rng.NextInt(0, 99);
            int sec = i % 60;
            int min = (i / 60) % 60;
            int hr = (i / 3600) % 24;
            sql.Append(CultureInfo.InvariantCulture, $"  ({1000000 + i}, '2026-11-24T{hr:D2}:{min:D2}:{sec:D2}Z', '{tenant}', '{sku}', {qty}, {cents}, '{status}')");
        }

        sql.AppendLine(";");
        sql.AppendLine("COMMIT;");
        WriteUtf8(Path.Combine(dir, "sql", "warehouse_ledger_dump.sql"), sql.ToString());

        // 3. Telemetry & settlement CSV export
        int csvRows = Math.Max(3000, (int)(24_000 * factor));
        var csv = new StringBuilder(csvRows * 80);
        csv.AppendLine("row_id,timestamp_epoch_ms,region,tenant_code,sku,units,unit_price_usd,gross_usd,checksum_hex");
        for (int i = 0; i < csvRows; i++)
        {
            string tenant = tenants[i % tenants.Length];
            string sku = skus[(i * 7 + rng.NextInt(0, 2)) % skus.Length];
            int units = 1 + (i % 40);
            double unitPrice = 19.95 + (i % 5) * 10.0;
            double gross = units * unitPrice;
            csv.AppendLine(CultureInfo.InvariantCulture,
                $"{500000 + i},{1795564800000L + i * 250L},us-east-1,{tenant},{sku},{units},{unitPrice:F2},{gross:F2},{rng.NextUInt32():x8}");
        }

        WriteUtf8(Path.Combine(dir, "exports", "daily_settlements.csv"), csv.ToString());
    }

    private static void GeneratePhotoCollectionCategory(string dir, ulong seed, double factor)
    {
        var rng = new DeterministicRng(seed);

        // 1. High-entropy JPEG camera originals (~7.98 bpb)
        int jpegCount = 6;
        int jpegBytesEach = Math.Max(96 * 1024, (int)(700 * 1024 * factor));
        byte[]? firstJpeg = null;
        byte[]? secondJpeg = null;

        for (int i = 0; i < jpegCount; i++)
        {
            byte[] jpg = CreateSyntheticJpeg(rng, jpegBytesEach, imageIndex: 4001 + i);
            WriteBytes(Path.Combine(dir, "originals", $"DSC_{4001 + i}.jpg"), jpg);
            if (i == 0) firstJpeg = jpg;
            if (i == 1) secondJpeg = jpg;
        }

        // 2. Uncompressed 24-bit BMP studio plates (smooth RGB gradients + fine sensor dither -> strong 3-byte stride correlation)
        int bmpSide = Math.Max(160, (int)(Math.Sqrt(600_000 * factor)));
        bmpSide = (bmpSide / 4) * 4; // 4-pixel aligned so row stride has zero padding bytes
        byte[] bmp1 = CreateSyntheticBmp24(rng, bmpSide, bmpSide, paletteShift: 17);
        byte[] bmp2 = CreateSyntheticBmp24(rng, bmpSide, bmpSide, paletteShift: 93);
        WriteBytes(Path.Combine(dir, "rasters", "studio_plate_01.bmp"), bmp1);
        WriteBytes(Path.Combine(dir, "rasters", "studio_plate_02.bmp"), bmp2);

        // 3. Uncompressed 32-bit RGBA TIFF scans (4-byte stride correlation)
        int tiffSide = Math.Max(140, (int)(Math.Sqrt(450_000 * factor)));
        byte[] tif1 = CreateSyntheticTiffRgba32(rng, tiffSide, tiffSide, seedBias: 42);
        byte[] tif2 = CreateSyntheticTiffRgba32(rng, tiffSide, tiffSide, seedBias: 128);
        WriteBytes(Path.Combine(dir, "rasters", "flatbed_scan_01.tif"), tif1);
        WriteBytes(Path.Combine(dir, "rasters", "flatbed_scan_02.tif"), tif2);

        // 4. Exact duplicate burst picks / cull backups
        WriteBytes(Path.Combine(dir, "selects", "DSC_4001_pick.jpg"), firstJpeg!);
        WriteBytes(Path.Combine(dir, "selects", "DSC_4002_pick.jpg"), secondJpeg!);
        WriteBytes(Path.Combine(dir, "backup", "studio_plate_01_backup.bmp"), bmp1);
    }

    private static byte[] CreateSyntheticJpeg(DeterministicRng rng, int totalBytes, int imageIndex)
    {
        byte[] buf = new byte[totalBytes];
        Span<byte> span = buf;

        // SOI + APP0 JFIF + APP1 EXIF stub + DQT + SOF0 + SOS header (160 bytes) + entropy-coded scan + EOI
        ReadOnlySpan<byte> jfifHeader =
        [
            0xFF, 0xD8, // SOI
            0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x02, 0x01, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00,
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
        ];
        jfifHeader.CopyTo(span);
        Encoding.ASCII.GetBytes($"UltraCam ISO100 #{imageIndex:D4}      ").AsSpan(0, 26).CopyTo(span.Slice(30, 26));
        // SOS marker at 56..68
        ReadOnlySpan<byte> sos = [0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00];
        sos.CopyTo(span.Slice(56, 10));

        Span<byte> scan = span.Slice(66, totalBytes - 68);
        rng.FillBytes(scan);
        // Replace bare 0xFF with non-marker values so stream behaves like valid Huffman scan while keeping ~7.99 bpb entropy
        for (int i = 0; i < scan.Length; i++)
        {
            if (scan[i] == 0xFF)
            {
                scan[i] = (byte)(i & 0xFE);
            }
        }

        span[totalBytes - 2] = 0xFF;
        span[totalBytes - 1] = 0xD9; // EOI
        return buf;
    }

    private static byte[] CreateSyntheticBmp24(DeterministicRng rng, int width, int height, int paletteShift)
    {
        int rowBytes = ((width * 3 + 3) / 4) * 4;
        int pixelBytes = rowBytes * height;
        byte[] bmp = new byte[54 + pixelBytes];
        Span<byte> span = bmp;

        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(2, 4), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(10, 4), 54);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(14, 4), 40); // BITMAPINFOHEADER
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(22, 4), height);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(26, 2), 1);  // planes
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(28, 2), 24); // 24-bit BGR
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(34, 4), pixelBytes);

        for (int y = 0; y < height; y++)
        {
            int rowOff = 54 + y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int px = rowOff + x * 3;
                int noise = rng.NextInt(-2, 3);
                bmp[px] = (byte)Math.Clamp(((x * 255) / Math.Max(1, width)) + (paletteShift & 0x1F) + noise, 0, 255);
                bmp[px + 1] = (byte)Math.Clamp(((y * 255) / Math.Max(1, height)) + ((paletteShift >> 1) & 0x1F) + noise, 0, 255);
                bmp[px + 2] = (byte)Math.Clamp((((x + y) * 127) / Math.Max(1, width + height)) + noise, 0, 255);
            }
        }

        return bmp;
    }

    private static byte[] CreateSyntheticTiffRgba32(DeterministicRng rng, int width, int height, int seedBias)
    {
        int pixelBytes = width * height * 4;
        int headerSize = 128;
        byte[] tiff = new byte[headerSize + pixelBytes];
        Span<byte> span = tiff;

        // Little-endian TIFF magic "II*\0" + IFD offset = 8
        span[0] = (byte)'I';
        span[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(8, 2), 4); // 4 IFD entries

        WriteTiffIfdEntry(span.Slice(10, 12), tag: 256, type: 4, count: 1, valueOrOffset: (uint)width);
        WriteTiffIfdEntry(span.Slice(22, 12), tag: 257, type: 4, count: 1, valueOrOffset: (uint)height);
        WriteTiffIfdEntry(span.Slice(34, 12), tag: 273, type: 4, count: 1, valueOrOffset: (uint)headerSize);
        WriteTiffIfdEntry(span.Slice(46, 12), tag: 279, type: 4, count: 1, valueOrOffset: (uint)pixelBytes);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int off = headerSize + (y * width + x) * 4;
                int jitter = rng.NextInt(-1, 2);
                tiff[off] = (byte)Math.Clamp(((x * 220) / Math.Max(1, width)) + (seedBias & 0x1F) + jitter, 0, 255);
                tiff[off + 1] = (byte)Math.Clamp(((y * 220) / Math.Max(1, height)) + jitter, 0, 255);
                tiff[off + 2] = (byte)Math.Clamp(160 + (((x - y) * 40) / Math.Max(1, width)) + jitter, 0, 255);
                tiff[off + 3] = 255; // opaque alpha channel
            }
        }

        return tiff;
    }

    private static void WriteTiffIfdEntry(Span<byte> dest, ushort tag, ushort type, uint count, uint valueOrOffset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(0, 2), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(2, 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(4, 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(8, 4), valueOrOffset);
    }

    private static void GenerateNativeBinariesCategory(string dir, ulong seed, double factor)
    {
        var rng = new DeterministicRng(seed);
        int binSize = Math.Max(256 * 1024, (int)(2 * 1024 * 1024 * factor));

        WriteBytes(Path.Combine(dir, "win-x64", "UltraEngine.Core.dll"), SynthesizeNativeBinary(rng, binSize, isPe: true, moduleName: "UltraEngine.Core.dll"));
        WriteBytes(Path.Combine(dir, "win-x64", "UltraEngine.Host.exe"), SynthesizeNativeBinary(rng, binSize, isPe: true, moduleName: "UltraEngine.Host.exe"));
        WriteBytes(Path.Combine(dir, "linux-x64", "libultra_codec.so"), SynthesizeNativeBinary(rng, binSize, isPe: false, moduleName: "libultra_codec.so"));
        WriteBytes(Path.Combine(dir, "linux-x64", "ultra_worker_daemon"), SynthesizeNativeBinary(rng, binSize, isPe: false, moduleName: "ultra_worker_daemon"));
    }

    private static byte[] SynthesizeNativeBinary(DeterministicRng rng, int totalBytes, bool isPe, string moduleName)
    {
        byte[] bin = new byte[totalBytes];
        Span<byte> span = bin;

        if (isPe)
        {
            // MZ header + PE\0\0 signature at 0x80 + x86-64 machine 0x8664
            span[0] = (byte)'M';
            span[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0x3C, 4), 0x80);
            Encoding.ASCII.GetBytes("PE\0\0").CopyTo(span.Slice(0x80, 4));
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(0x84, 2), 0x8664); // IMAGE_FILE_MACHINE_AMD64
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(0x86, 2), 3);      // 3 sections (.text, .rdata, .rsrc)
            Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(span.Slice(0x178, 8));
            Encoding.ASCII.GetBytes(".rdata\0\0").CopyTo(span.Slice(0x1A0, 8));
            Encoding.ASCII.GetBytes(".rsrc\0\0\0").CopyTo(span.Slice(0x1C8, 8));
        }
        else
        {
            // ELF64 x86-64 header (\x7FELF, class=2, little-endian=1, version=1, e_machine=62 EM_X86_64)
            span[0] = 0x7F;
            span[1] = (byte)'E';
            span[2] = (byte)'L';
            span[3] = (byte)'F';
            span[4] = 2; // 64-bit
            span[5] = 1; // little-endian
            span[6] = 1; // version
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(16, 2), 3);  // ET_DYN
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(18, 2), 62); // EM_X86_64
        }

        int textStart = 4096;
        int textEnd = (int)(totalBytes * 0.74);

        // Shared subroutine target table inside .text (realistic helper functions called thousands of times via E8/E9 rel32)
        int[] helperTargets = new int[64];
        for (int h = 0; h < helperTargets.Length; h++)
        {
            helperTargets[h] = textStart + 256 + (h * ((textEnd - textStart - 1024) / helperTargets.Length));
        }

        ReadOnlySpan<byte[]> basicBlockTemplates =
        [
            [0x55, 0x48, 0x89, 0xE5, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x89, 0x7D, 0xF8],
            [0x48, 0x8B, 0x45, 0xF8, 0x48, 0x85, 0xC0, 0x74, 0x0A, 0x8B, 0x50, 0x08],
            [0x48, 0x8D, 0x0D, 0x40, 0x12, 0x00, 0x00, 0x48, 0x89, 0xC7, 0x31, 0xC0],
            [0x41, 0x54, 0x53, 0x48, 0x83, 0xEC, 0x18, 0x49, 0x89, 0xFC, 0x48, 0x89, 0xF3],
            [0x48, 0x83, 0xC4, 0x20, 0x5D, 0xC3, 0x90, 0x90],
        ];

        int ip = textStart;
        while (ip + 32 < textEnd)
        {
            var tpl = basicBlockTemplates[rng.NextInt(0, basicBlockTemplates.Length)];
            tpl.CopyTo(span.Slice(ip, tpl.Length));
            ip += tpl.Length;

            // Emit 2 to 4 CALL rel32 (0xE8) or JMP rel32 (0xE9) instructions targeting shared helpers
            int calls = rng.NextInt(2, 5);
            for (int c = 0; c < calls && ip + 12 < textEnd; c++)
            {
                int target = helperTargets[rng.NextInt(0, helperTargets.Length)];
                span[ip] = c + 1 == calls && rng.NextInt(0, 4) == 0 ? (byte)0xE9 : (byte)0xE8;
                int rel32 = target - (ip + 5);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(ip + 1, 4), rel32);
                ip += 5;

                // Small register move / test instruction with immediate operand
                span[ip] = 0x48;
                span[ip + 1] = 0x83;
                span[ip + 2] = 0xC0;
                span[ip + 3] = (byte)rng.NextInt(1, 64);
                ip += 4;
            }
        }

        // Fill .rdata / .rsrc symbol and UTF-16LE version resource tables
        var resSb = new StringBuilder((totalBytes - textEnd) / 2);
        int symIdx = 0;
        while (resSb.Length * 2 < totalBytes - textEnd - 128)
        {
            resSb.Append(CultureInfo.InvariantCulture,
                $"{moduleName}!UltraCompress_ExportSymbol_{symIdx:D5}_Stage_{rng.NextInt(0, 32):D2}\0");
            symIdx++;
        }

        byte[] resBytes = Encoding.Unicode.GetBytes(resSb.ToString());
        resBytes.AsSpan(0, Math.Min(resBytes.Length, totalBytes - textEnd)).CopyTo(span[textEnd..]);

        return bin;
    }

    private static void GenerateMixedWorkspaceCategory(string dir, ulong seed, double factor)
    {
        double subFactor = factor * 0.35;
        GenerateVideoStreamCategory(Path.Combine(dir, "media"), seed ^ 0x11UL, subFactor);
        GenerateSourceRepoCategory(Path.Combine(dir, "repo"), seed ^ 0x22UL, subFactor);
        GenerateDatabaseDumpCategory(Path.Combine(dir, "database"), seed ^ 0x33UL, subFactor);
        GeneratePhotoCollectionCategory(Path.Combine(dir, "photos"), seed ^ 0x44UL, subFactor);
        GenerateNativeBinariesCategory(Path.Combine(dir, "binaries"), seed ^ 0x55UL, subFactor);
    }

    private static void WriteUtf8(string path, string content) =>
        WriteBytes(path, Encoding.UTF8.GetBytes(content));

    private static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}
