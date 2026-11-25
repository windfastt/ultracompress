using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UltraCompress.Core;
using UltraCompress.Core.Archive;
using UltraCompress.Core.Errors;

namespace UltraCompress.ArchiveEngine.Tests;

public sealed class UcxBrowseExtractAndSecurityTests
{
    [Fact]
    public async Task InspectArchive_IsTrueO1_ReadsZeroBytesFromPayloadBlocks()
    {
        using var ws = new TempTestWorkspace();
        ws.WriteTextFile("mixed/src/Engine/Queue.cs", string.Concat(Enumerable.Repeat("class Queue { int Count; }\n", 500)));
        ws.WriteTextFile("mixed/data/config.json", string.Concat(Enumerable.Repeat("{\"enabled\":true}\n", 500)));
        ws.WriteBinaryFile("mixed/assets/banner.jpg", TempTestWorkspace.CreateSyntheticJpegBytes(48_000));

        var archivePath = ws.SubPath("o1-browse.ucx");
        await new UcxArchiveWriter().CreateFromPathAsync(archivePath, ws.SubPath("mixed"));

        // Read manifest offset from header so our guard stream forbids reading [64 .. manifestOffset).
        byte[] header = new byte[UcxFormatConstants.HeaderSizeBytes];
        await using (var rawFs = File.OpenRead(archivePath))
        {
            _ = await rawFs.ReadAsync(header);
        }

        long manifestOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12, 8));
        Assert.True(manifestOffset > UcxFormatConstants.HeaderSizeBytes + 48_000);

        await using var baseStream = File.OpenRead(archivePath);
        await using var guardedStream = new RangeTrackingStream(
            baseStream,
            forbiddenStart: UcxFormatConstants.HeaderSizeBytes,
            forbiddenEndExclusive: manifestOffset);

        var reader = new UcxArchiveReader();
        var inspection = await reader.InspectArchiveAsync(guardedStream, archivePath);

        Assert.Equal(3, inspection.Summary.FileCount);
        Assert.Equal(0, guardedStream.PayloadBytesReadInRange(UcxFormatConstants.HeaderSizeBytes, manifestOffset));

        // Verify hierarchical entry tree rolled up sizes accurately
        Assert.Equal(3, inspection.RootTree.FileCount);
        Assert.Contains(inspection.RootTree.Children, n => n.Name == "src" && n.IsDirectory && n.FileCount == 1);
        Assert.Contains(inspection.RootTree.Children, n => n.Name == "assets" && n.IsDirectory && n.FileCount == 1);
    }

    [Fact]
    public async Task SelectiveExtraction_DecompressesOnlyTargetSubBlocks_SupportsFilesSubfoldersAndGlobs()
    {
        using var ws = new TempTestWorkspace();
        var jpegBytes = TempTestWorkspace.CreateSyntheticJpegBytes(30_000, seed: 303);
        ws.WriteBinaryFile("repo/assets/hero.jpg", jpegBytes);
        ws.WriteTextFile("repo/src/Core/Alpha.cs", string.Concat(Enumerable.Repeat("// Alpha source\n", 300)));
        ws.WriteTextFile("repo/src/Engine/Beta.cs", string.Concat(Enumerable.Repeat("// Beta source\n", 300)));
        ws.WriteTextFile("repo/config/appsettings.json", string.Concat(Enumerable.Repeat("{\"port\":17832}\n", 300)));
        ws.WriteTextFile("repo/docs/guide.md", "# Guide\n");

        var archivePath = ws.SubPath("selective.ucx");
        await new UcxArchiveWriter().CreateFromPathAsync(archivePath, ws.SubPath("repo"));

        var reader = new UcxArchiveReader();
        var inspection = reader.InspectArchive(archivePath);
        var sourceBlock = inspection.Blocks.Single(b => b.ContentClass == UcxContentClass.SourceAndText);
        var jsonBlock = inspection.Blocks.Single(b => b.ContentClass == UcxContentClass.StructuredData);
        var mediaBlock = inspection.Blocks.Single(b => b.ContentClass == UcxContentClass.IncompressibleMedia);

        // 1. Extract ONLY assets/hero.jpg -> neither sourceBlock nor jsonBlock may be read!
        var outMediaOnly = ws.SubPath("out-media");
        await using (var fs = File.OpenRead(archivePath))
        await using (var tracker = new RangeTrackingStream(fs))
        {
            var res = await reader.ExtractEntriesAsync(
                tracker,
                outMediaOnly,
                new UcxExtractOptions { SelectPatterns = ["assets/hero.jpg"] },
                archivePath: archivePath);

            Assert.Equal(1, res.ExtractedFiles);
            Assert.Equal(1, res.DecompressedBlockCount);
            Assert.Equal(0, tracker.PayloadBytesReadInRange(sourceBlock.OffsetInArchive, sourceBlock.OffsetInArchive + sourceBlock.CompressedLength));
            Assert.Equal(0, tracker.PayloadBytesReadInRange(jsonBlock.OffsetInArchive, jsonBlock.OffsetInArchive + jsonBlock.CompressedLength));
            Assert.Equal(jpegBytes, await File.ReadAllBytesAsync(Path.Combine(outMediaOnly, "assets", "hero.jpg")));
        }

        // 2. Extract by recursive glob "**/*.cs" -> mediaBlock and jsonBlock are untouched!
        var outCsOnly = ws.SubPath("out-cs");
        await using (var fs = File.OpenRead(archivePath))
        await using (var tracker = new RangeTrackingStream(fs))
        {
            var res = await reader.ExtractEntriesAsync(
                tracker,
                outCsOnly,
                new UcxExtractOptions { SelectPatterns = ["**/*.cs"] },
                archivePath: archivePath);

            Assert.Equal(2, res.ExtractedFiles);
            Assert.Equal(1, res.DecompressedBlockCount);
            Assert.Equal(0, tracker.PayloadBytesReadInRange(mediaBlock.OffsetInArchive, mediaBlock.OffsetInArchive + mediaBlock.CompressedLength));
            Assert.Equal(0, tracker.PayloadBytesReadInRange(jsonBlock.OffsetInArchive, jsonBlock.OffsetInArchive + jsonBlock.CompressedLength));
            Assert.True(File.Exists(Path.Combine(outCsOnly, "src", "Core", "Alpha.cs")));
            Assert.True(File.Exists(Path.Combine(outCsOnly, "src", "Engine", "Beta.cs")));
            Assert.False(File.Exists(Path.Combine(outCsOnly, "docs", "guide.md")));
        }

        // 3. Extract by subfolder prefix "src/Engine" and leaf glob "*.json"
        var outCombo = ws.SubPath("out-combo");
        var comboRes = await reader.ExtractEntriesAsync(
            archivePath,
            outCombo,
            new UcxExtractOptions { SelectPatterns = ["src/Engine", "*.json"] });

        Assert.Equal(2, comboRes.ExtractedFiles);
        Assert.True(File.Exists(Path.Combine(outCombo, "src", "Engine", "Beta.cs")));
        Assert.True(File.Exists(Path.Combine(outCombo, "config", "appsettings.json")));
        Assert.False(File.Exists(Path.Combine(outCombo, "src", "Core", "Alpha.cs")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../outside/evil.dll")]
    [InlineData("nested/../../../escape.sh")]
    [InlineData("/etc/passwd")]
    [InlineData("\\Windows\\System32\\drivers\\etc\\hosts")]
    [InlineData("C:\\evil.exe")]
    [InlineData("C:drive_relative.bin")]
    [InlineData("sub/bad\0null.txt")]
    public async Task ZipSlipProtection_RejectsMaliciousManifestPathsBeforeWritingAnyFile(string maliciousEntryPath)
    {
        using var ws = new TempTestWorkspace();
        var forgedArchivePath = ws.SubPath("malicious.ucx");
        ForgeArchiveWithCustomEntryPath(forgedArchivePath, maliciousEntryPath, Encoding.UTF8.GetBytes("pwned"));

        var destDir = ws.SubPath("safe-target", "inner");
        var reader = new UcxArchiveReader();

        var ex = await Assert.ThrowsAsync<UltraCompressException>(() =>
            reader.ExtractEntriesAsync(forgedArchivePath, destDir));

        Assert.Equal(UltraCompressErrorCode.PathTraversalDetected, ex.Code);
        Assert.Equal("UC-ERR-SEC-001", ex.Error.SymbolicCode);
        Assert.False(Directory.Exists(destDir));
        Assert.Empty(Directory.EnumerateFiles(ws.SubPath("safe-target") is var parent && Directory.Exists(parent) ? parent : ws.RootPath, "*pwned*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SymlinkEscapeProtection_BlocksExtractingThroughSymbolicLinkInsideDestination()
    {
        using var ws = new TempTestWorkspace();
        var outsideSecretDir = ws.SubPath("outside-secret");
        Directory.CreateDirectory(outsideSecretDir);

        var destDir = ws.SubPath("extract-dest");
        Directory.CreateDirectory(destDir);
        var symlinkDir = Path.Combine(destDir, "linked-sub");

        try
        {
            Directory.CreateSymbolicLink(symlinkDir, outsideSecretDir);
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // Unprivileged Windows accounts without Developer Mode may forbid CreateSymbolicLink; skip only if OS refused creation.
            return;
        }

        ws.WriteTextFile("src/linked-sub/payload.txt", "Should never land in outside-secret!");
        var archivePath = ws.SubPath("symlink-test.ucx");
        await new UcxArchiveWriter().CreateFromPathAsync(archivePath, ws.SubPath("src"));

        var ex = await Assert.ThrowsAsync<UltraCompressException>(() =>
            new UcxArchiveReader().ExtractEntriesAsync(archivePath, destDir));

        Assert.Equal(UltraCompressErrorCode.PathTraversalDetected, ex.Code);
        Assert.False(File.Exists(Path.Combine(outsideSecretDir, "payload.txt")));
    }

    [Fact]
    public async Task BitFlipInStoreOrCompressedPayload_FailsSha256VerificationAndWritesNoCorruptFile()
    {
        using var ws = new TempTestWorkspace();
        ws.WriteBinaryFile("input/photo.jpg", TempTestWorkspace.CreateSyntheticJpegBytes(8192, seed: 55));
        var archivePath = ws.SubPath("tampered.ucx");
        await new UcxArchiveWriter().CreateFromPathAsync(archivePath, ws.SubPath("input"));

        // Flip 1 bit inside the payload section (offset 100 is inside block #0 after the 64-byte header).
        var raw = await File.ReadAllBytesAsync(archivePath);
        raw[100] ^= 0x01;
        await File.WriteAllBytesAsync(archivePath, raw);

        var reader = new UcxArchiveReader();

        var verifyEx = await Assert.ThrowsAsync<UltraCompressException>(() => reader.VerifyArchiveAsync(archivePath));
        Assert.Equal(UltraCompressErrorCode.ChecksumMismatch, verifyEx.Code);
        Assert.Equal("UC-ERR-HASH-001", verifyEx.Error.SymbolicCode);

        var extractDir = ws.SubPath("tampered-out");
        var extractEx = await Assert.ThrowsAsync<UltraCompressException>(() => reader.ExtractEntriesAsync(archivePath, extractDir));
        Assert.Equal(UltraCompressErrorCode.ChecksumMismatch, extractEx.Code);
        Assert.False(File.Exists(Path.Combine(extractDir, "photo.jpg")));
    }

    [Fact]
    public void CorruptMagicOrTruncatedArchive_ThrowsCorruptArchiveWithRemediation()
    {
        using var ws = new TempTestWorkspace();
        var badMagicPath = ws.WriteBinaryFile("not-ucx.ucx", Encoding.UTF8.GetBytes(new string('X', 128)));
        var reader = new UcxArchiveReader();

        var ex = Assert.Throws<UltraCompressException>(() => reader.InspectArchive(badMagicPath));
        Assert.Equal(UltraCompressErrorCode.CorruptArchive, ex.Code);
        Assert.Equal("UC-ERR-ARCH-001", ex.Error.SymbolicCode);
        Assert.False(string.IsNullOrWhiteSpace(ex.Error.Remediation));
    }

    private static void ForgeArchiveWithCustomEntryPath(string archivePath, string maliciousPath, byte[] payload)
    {
        var payloadSha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        long blockOffset = UcxFormatConstants.HeaderSizeBytes;

        var block = new UcxArchiveBlock
        {
            BlockIndex = 0,
            ContentClass = UcxContentClass.IncompressibleMedia,
            Codec = "store",
            CodecLevel = 0,
            CodecRationale = "forged-test",
            OffsetInArchive = blockOffset,
            UncompressedLength = payload.Length,
            CompressedLength = payload.Length,
            UncompressedSha256 = payloadSha,
            CompressedSha256 = payloadSha,
            EntryCount = 1,
        };

        var entry = new UcxArchiveEntry
        {
            RelativePath = maliciousPath,
            IsDirectory = false,
            OriginalSize = payload.Length,
            Sha256 = payloadSha,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            CreationUtc = DateTimeOffset.UtcNow,
            ContentClass = UcxContentClass.IncompressibleMedia,
            BlockIndex = 0,
            OffsetInBlock = 0,
            LengthInBlock = payload.Length,
        };

        var manifest = new UcxArchiveManifest
        {
            Summary = new UcxArchiveSummary
            {
                TotalEntries = 1,
                FileCount = 1,
                BlockCount = 1,
                OriginalBytes = payload.Length,
                UniqueBytes = payload.Length,
                CompressedPayloadBytes = payload.Length,
            },
            Blocks = [block],
            Entries = [entry],
        };

        var manifestUtf8 = JsonSerializer.SerializeToUtf8Bytes(manifest, UcxArchiveWriter.ManifestJsonOptions);
        var manifestSha = SHA256.HashData(manifestUtf8);

        using var fs = File.Create(archivePath);
        var header = new byte[UcxFormatConstants.HeaderSizeBytes];
        long manifestOffset = blockOffset + payload.Length;
        UcxFormatConstants.WriteFinalHeader(header, flags: 0, manifestOffset, manifestUtf8.Length, manifestUtf8.Length, manifestSha);

        fs.Write(header);
        fs.Write(payload);
        fs.Write(manifestUtf8);
        fs.Write(UcxFormatConstants.TrailerMagicUcxe);
    }

    private sealed class RangeTrackingStream(
        Stream inner,
        long? forbiddenStart = null,
        long? forbiddenEndExclusive = null) : Stream
    {
        private readonly List<(long Start, long EndExclusive)> _reads = new();

        public long PayloadBytesReadInRange(long rangeStart, long rangeEndExclusive)
        {
            long total = 0;
            foreach (var (s, e) in _reads)
            {
                long overlapStart = Math.Max(s, rangeStart);
                long overlapEnd = Math.Min(e, rangeEndExclusive);
                if (overlapEnd > overlapStart)
                {
                    total += overlapEnd - overlapStart;
                }
            }

            return total;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            long start = inner.Position;
            int read = inner.Read(buffer, offset, count);
            RecordRead(start, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            long start = inner.Position;
            int read = await inner.ReadAsync(buffer, cancellationToken);
            RecordRead(start, read);
            return read;
        }

        private void RecordRead(long start, int count)
        {
            if (count <= 0)
            {
                return;
            }

            long end = start + count;
            if (forbiddenStart is not null && forbiddenEndExclusive is not null)
            {
                long ovStart = Math.Max(start, forbiddenStart.Value);
                long ovEnd = Math.Min(end, forbiddenEndExclusive.Value);
                if (ovEnd > ovStart)
                {
                    throw new InvalidOperationException($"O(1) browse violation: attempted to read payload range [{ovStart}..{ovEnd}).");
                }
            }

            _reads.Add((start, end));
        }
    }
}
