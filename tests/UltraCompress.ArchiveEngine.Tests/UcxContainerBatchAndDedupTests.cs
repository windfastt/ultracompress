using System.Security.Cryptography;
using System.Text;
using UltraCompress.Core;
using UltraCompress.Core.Archive;
using UltraCompress.Core.SevenZip;

namespace UltraCompress.ArchiveEngine.Tests;

public sealed class UcxContainerBatchAndDedupTests
{
    [Fact]
    public async Task SingleFile_PackAndExtract_PreservesBytesChecksumAndTimestamp()
    {
        using var ws = new TempTestWorkspace();
        var fixedMtime = new DateTimeOffset(2025, 06, 15, 14, 30, 00, TimeSpan.Zero);
        var payload = string.Concat(Enumerable.Repeat("UltraCompress lossless single-file payload line.\n", 600));
        var inputPath = ws.WriteTextFile("input/sample-report.txt", payload, fixedMtime);
        var archivePath = ws.SubPath("out", "sample-report.ucx");
        var extractDir = ws.SubPath("extracted");

        var writer = new UcxArchiveWriter();
        var writeResult = await writer.CreateFromPathAsync(archivePath, inputPath);

        Assert.True(File.Exists(archivePath));
        Assert.Equal(1, writeResult.Summary.FileCount);
        Assert.Equal(Encoding.UTF8.GetByteCount(payload), writeResult.Summary.OriginalBytes);
        Assert.True(writeResult.Summary.CompressedPayloadBytes < writeResult.Summary.OriginalBytes);

        var reader = new UcxArchiveReader();
        var verify = await reader.VerifyArchiveAsync(archivePath);
        Assert.True(verify.IsValid);
        Assert.Equal(1, verify.VerifiedFiles);

        var extractResult = await reader.ExtractEntriesAsync(archivePath, extractDir);
        Assert.Equal(1, extractResult.ExtractedFiles);

        var restoredPath = Path.Combine(extractDir, "sample-report.txt");
        Assert.Equal(payload, await File.ReadAllTextAsync(restoredPath));
        Assert.Equal(fixedMtime.UtcDateTime, File.GetLastWriteTimeUtc(restoredPath), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RecursiveFolderAndMultiPathBatch_PreservesHierarchyEmptyDirsZeroByteFilesAndHeterogeneousBlocks()
    {
        using var ws = new TempTestWorkspace();
        var repoDir = ws.SubPath("workspace");
        Directory.CreateDirectory(Path.Combine(repoDir, "empty-folder", "nested-empty"));

        ws.WriteTextFile("workspace/.gitkeep", string.Empty);
        ws.WriteTextFile("workspace/src/Core/ArchiveWriter.cs", string.Concat(Enumerable.Repeat("public sealed class ArchiveWriter { public void Write() {} }\n", 300)));
        ws.WriteTextFile("workspace/src/Core/ArchiveReader.cs", string.Concat(Enumerable.Repeat("public sealed class ArchiveReader { public void Read() {} }\n", 300)));
        ws.WriteTextFile("workspace/data/telemetry.json", string.Concat(Enumerable.Repeat("{\"event\":\"compress\",\"ratio\":0.24,\"ok\":true}\n", 400)));
        ws.WriteTextFile("workspace/data/metrics.csv", string.Concat(Enumerable.Repeat("id,bytes_in,bytes_out,codec\n1,65536,12040,zstd\n", 400)));

        var structuredBin = new byte[24_000];
        for (int i = 0; i < structuredBin.Length; i++)
        {
            structuredBin[i] = (byte)((i * 7) % 31);
        }

        ws.WriteBinaryFile("workspace/bin/module.bin", structuredBin);

        var jpegBytes = TempTestWorkspace.CreateSyntheticJpegBytes(32_768, seed: 11);
        var mp4Bytes = TempTestWorkspace.CreateSyntheticMp4Bytes(40_960, seed: 22);
        ws.WriteBinaryFile("workspace/media/photo.jpg", jpegBytes);
        ws.WriteBinaryFile("workspace/media/clip.mp4", mp4Bytes);

        // Multi-path batch: pack workspace folder + an extra standalone folder + colliding standalone file names
        var docsDir = ws.SubPath("docs");
        ws.WriteTextFile("docs/README.md", "# Documentation\n" + string.Concat(Enumerable.Repeat("Detailed guide section.\n", 100)));
        var standaloneA = ws.WriteTextFile("standaloneA/notes.txt", "Notes version A");
        var standaloneB = ws.WriteTextFile("standaloneB/notes.txt", "Notes version B");

        var archivePath = ws.SubPath("bundle.ucx");
        var writer = new UcxArchiveWriter();
        var writeResult = await writer.CreateAsync(
            archivePath,
            [repoDir, docsDir, standaloneA, standaloneB],
            new UcxPackOptions { Profile = CompressionProfile.Balanced });

        var reader = new UcxArchiveReader();
        var inspection = await reader.InspectArchiveAsync(archivePath);

        // Verify heterogeneous sub-block routing:
        // Media files (JPEG + MP4) -> Store blocks
        // SourceAndText (.cs/.md/.txt) -> Brotli solid block
        // StructuredData (.json/.csv) -> Zstd solid block
        // ExecutableAndBinary (.bin) -> Deflate solid block
        Assert.Contains(inspection.Blocks, b => b.ContentClass == UcxContentClass.IncompressibleMedia && b.Codec == "store");
        Assert.Contains(inspection.Blocks, b => b.ContentClass == UcxContentClass.SourceAndText && b.Codec == "brotli");
        Assert.Contains(inspection.Blocks, b => b.ContentClass == UcxContentClass.StructuredData && b.Codec == "zstd");
        Assert.Contains(inspection.Blocks, b => b.ContentClass == UcxContentClass.ExecutableAndBinary && b.Codec == "deflate");
        Assert.All(inspection.Blocks, b => Assert.False(string.IsNullOrWhiteSpace(b.CodecRationale)));

        // Verify empty folder & zero-byte file preserved
        Assert.Contains(inspection.Entries, e => e.IsDirectory && e.RelativePath == "workspace/empty-folder/nested-empty");
        Assert.Contains(inspection.Entries, e => !e.IsDirectory && e.OriginalSize == 0 && e.RelativePath == "workspace/.gitkeep");

        // Extract everything and verify bit-for-bit restoration
        var extractRoot = ws.SubPath("restored");
        var extractResult = await reader.ExtractEntriesAsync(archivePath, extractRoot);
        Assert.Equal(writeResult.Summary.FileCount, extractResult.ExtractedFiles);
        Assert.True(Directory.Exists(Path.Combine(extractRoot, "workspace", "empty-folder", "nested-empty")));
        Assert.Equal(0, new FileInfo(Path.Combine(extractRoot, "workspace", ".gitkeep")).Length);
        Assert.Equal(jpegBytes, await File.ReadAllBytesAsync(Path.Combine(extractRoot, "workspace", "media", "photo.jpg")));
        Assert.Equal(mp4Bytes, await File.ReadAllBytesAsync(Path.Combine(extractRoot, "workspace", "media", "clip.mp4")));
        Assert.Equal(structuredBin, await File.ReadAllBytesAsync(Path.Combine(extractRoot, "workspace", "bin", "module.bin")));
    }

    [Fact]
    public async Task ExactDuplicateDetection_StoresIdenticalPayloadsOnce_AndRestoresAllCopiesBitExact()
    {
        using var ws = new TempTestWorkspace();
        var rng = new Random(777);
        var sharedBlob = new byte[65_536];
        // Moderately structured data
        for (int i = 0; i < sharedBlob.Length; i++)
        {
            sharedBlob[i] = (byte)(rng.Next(0, 64) + (i % 16));
        }

        // Write 5 identical copies across different subdirectories + 1 distinct file
        ws.WriteBinaryFile("batch/pkg1/vendor/common.dat", sharedBlob);
        ws.WriteBinaryFile("batch/pkg2/vendor/common.dat", sharedBlob);
        ws.WriteBinaryFile("batch/pkg3/vendor/common.dat", sharedBlob);
        ws.WriteBinaryFile("batch/pkg4/vendor/common.dat", sharedBlob);
        ws.WriteBinaryFile("batch/backup/common-copy.dat", sharedBlob);
        var uniqueText = Encoding.UTF8.GetBytes("Unique manifest metadata\n");
        ws.WriteBinaryFile("batch/manifest.txt", uniqueText);

        var archivePath = ws.SubPath("dedup.ucx");
        var writer = new UcxArchiveWriter();
        var writeResult = await writer.CreateFromPathAsync(archivePath, ws.SubPath("batch"));

        Assert.Equal(6, writeResult.Summary.FileCount);
        Assert.Equal(4, writeResult.Summary.DuplicateCount);
        Assert.Equal(4L * sharedBlob.Length, writeResult.Summary.DuplicateBytesSaved);
        Assert.Equal(sharedBlob.Length + uniqueText.Length, writeResult.Summary.UniqueBytes);
        Assert.Equal(5L * sharedBlob.Length + uniqueText.Length, writeResult.Summary.OriginalBytes);

        // Total archive file size must be smaller than even a SINGLE copy of sharedBlob + uniqueText!
        Assert.True(new FileInfo(archivePath).Length < sharedBlob.Length);

        var reader = new UcxArchiveReader();
        var inspection = reader.InspectArchive(archivePath);
        Assert.Equal(4, inspection.Summary.DuplicateCount);
        Assert.Equal(4L * sharedBlob.Length, inspection.Summary.DuplicateBytesSaved);
        Assert.Equal(4, inspection.Entries.Count(e => e.IsDuplicate));

        var extractDir = ws.SubPath("dedup-out");
        var extractResult = await reader.ExtractEntriesAsync(archivePath, extractDir);
        Assert.Equal(6, extractResult.ExtractedFiles);

        var expectedSha = Convert.ToHexString(SHA256.HashData(sharedBlob)).ToLowerInvariant();
        foreach (var rel in new[]
                 {
                     "pkg1/vendor/common.dat",
                     "pkg2/vendor/common.dat",
                     "pkg3/vendor/common.dat",
                     "pkg4/vendor/common.dat",
                     "backup/common-copy.dat",
                 })
        {
            var actual = await File.ReadAllBytesAsync(Path.Combine(extractDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(sharedBlob, actual);
            Assert.Equal(expectedSha, Convert.ToHexString(SHA256.HashData(actual)).ToLowerInvariant());
        }
    }

    [Theory]
    [InlineData("store")]
    [InlineData("zstd")]
    [InlineData("brotli")]
    [InlineData("deflate")]
    public async Task AllInProcessCodecs_RoundtripAndVerifyBitExact(string codec)
    {
        using var ws = new TempTestWorkspace();
        ws.WriteTextFile("src/alpha.cs", string.Concat(Enumerable.Repeat("namespace Demo; public record Item(int Id, string Name);\n", 250)));
        ws.WriteTextFile("src/beta.json", string.Concat(Enumerable.Repeat("{\"id\":101,\"tags\":[\"archive\",\"ucx\"]}\n", 250)));

        var archivePath = ws.SubPath($"codec-{codec}.ucx");
        var writer = new UcxArchiveWriter();
        var res = await writer.CreateFromPathAsync(
            archivePath,
            ws.SubPath("src"),
            new UcxPackOptions { PreferredCodecOverride = codec });

        Assert.All(res.Blocks, b => Assert.Equal(codec, b.Codec));

        var reader = new UcxArchiveReader();
        var verify = await reader.VerifyArchiveAsync(archivePath);
        Assert.True(verify.IsValid);
        Assert.Equal(2, verify.VerifiedFiles);
    }

    [SkippableFact]
    public async Task SevenZipCodec_WhenSevenZipInstalled_RoundtripsSubBlocksBitExact()
    {
        Skip.If(SevenZipToolLocator.TryResolve() is null, "7za/7z CLI is not installed in this environment.");

        using var ws = new TempTestWorkspace();
        var text = string.Concat(Enumerable.Repeat("7z LZMA2 sub-block inside UCX1 multi-file container.\n", 400));
        ws.WriteTextFile("sevenzip/doc.txt", text);

        var archivePath = ws.SubPath("sevenzip.ucx");
        var writer = new UcxArchiveWriter();
        var res = await writer.CreateFromPathAsync(
            archivePath,
            ws.SubPath("sevenzip"),
            new UcxPackOptions { PreferredCodecOverride = "7z", Profile = CompressionProfile.Laboratory });

        Assert.Single(res.Blocks);
        Assert.Equal("7z", res.Blocks[0].Codec);

        var reader = new UcxArchiveReader();
        var verify = await reader.VerifyArchiveAsync(archivePath);
        Assert.True(verify.IsValid);

        var outDir = ws.SubPath("sevenzip-out");
        await reader.ExtractEntriesAsync(archivePath, outDir);
        Assert.Equal(text, await File.ReadAllTextAsync(Path.Combine(outDir, "doc.txt")));
    }

    [Theory]
    [InlineData(CompressionProfile.Tiny)]
    [InlineData(CompressionProfile.Laboratory)]
    public async Task TournamentProfiles_SelectBestBlockCodecAndRecordTrialRationale(CompressionProfile profile)
    {
        using var ws = new TempTestWorkspace();
        ws.WriteTextFile("proj/Service.cs", string.Concat(Enumerable.Repeat("public async Task ExecuteAsync(CancellationToken ct) => await Task.CompletedTask;\n", 300)));
        var archivePath = ws.SubPath($"prof-{profile}.ucx");

        var writer = new UcxArchiveWriter();
        var res = await writer.CreateFromPathAsync(archivePath, ws.SubPath("proj"), new UcxPackOptions { Profile = profile });

        Assert.Single(res.Blocks);
        Assert.Contains("tournament", res.Blocks[0].CodecRationale, StringComparison.OrdinalIgnoreCase);

        var reader = new UcxArchiveReader();
        Assert.True((await reader.VerifyArchiveAsync(archivePath)).IsValid);
    }
}
