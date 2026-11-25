using System.Security.Cryptography;
using System.Text.Json;
using UltraCompress.Core.Destination;
using UltraCompress.Core.Errors;
using UltraCompress.Core.Jobs;

namespace UltraCompress.Core.Archive;

/// <summary>
/// Production <c>.ucx</c> (<c>UCX1</c>) archive reader providing O(1) manifest browsing
/// (<see cref="InspectArchive(string)"/> / <see cref="ListEntries"/>), full integrity verification,
/// and selective file/subfolder/glob extraction with zip-slip protection and mandatory SHA-256 verification.
/// </summary>
public sealed class UcxArchiveReader
{
    private const int WriteChunkSize = 256 * 1024;

    public UcxArchiveInspection InspectArchive(string archivePath)
    {
        var fullPath = ResolveExistingArchivePath(archivePath);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return InspectArchive(stream, fullPath);
    }

    public UcxArchiveInspection InspectArchive(Stream archiveStream, string? archivePath = null)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        if (!archiveStream.CanSeek)
        {
            throw UltraCompressErrors.InvalidArguments("UCX archive inspection requires a seekable stream.");
        }

        var (_, manifest) = ReadHeaderAndManifestSync(archiveStream, archivePath);
        return BuildInspection(manifest, archiveStream.Length, archivePath);
    }

    public async Task<UcxArchiveInspection> InspectArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveExistingArchivePath(archivePath);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16384,
            useAsync: true);
        return await InspectArchiveAsync(stream, fullPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UcxArchiveInspection> InspectArchiveAsync(
        Stream archiveStream,
        string? archivePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        if (!archiveStream.CanSeek)
        {
            throw UltraCompressErrors.InvalidArguments("UCX archive inspection requires a seekable stream.");
        }

        var (_, manifest) = await ReadHeaderAndManifestAsync(archiveStream, archivePath, cancellationToken)
            .ConfigureAwait(false);
        return BuildInspection(manifest, archiveStream.Length, archivePath);
    }

    public IReadOnlyList<UcxArchiveEntryView> ListEntries(
        string archivePath,
        IReadOnlyList<string>? selectPatterns = null)
    {
        var inspection = InspectArchive(archivePath);
        if (selectPatterns is null || selectPatterns.Count == 0)
        {
            return inspection.Entries;
        }

        return inspection.Entries
            .Where(e => UcxPathSecurity.MatchesSelectPatterns(e.RelativePath, selectPatterns))
            .ToList();
    }

    public async Task<IReadOnlyList<UcxArchiveEntryView>> ListEntriesAsync(
        string archivePath,
        IReadOnlyList<string>? selectPatterns = null,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectArchiveAsync(archivePath, cancellationToken).ConfigureAwait(false);
        if (selectPatterns is null || selectPatterns.Count == 0)
        {
            return inspection.Entries;
        }

        return inspection.Entries
            .Where(e => UcxPathSecurity.MatchesSelectPatterns(e.RelativePath, selectPatterns))
            .ToList();
    }

    public async Task<UcxArchiveExtractResult> ExtractEntriesAsync(
        string archivePath,
        string destinationDirectory,
        UcxExtractOptions? options = null,
        IProgress<ArchiveProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullArchivePath = ResolveExistingArchivePath(archivePath);
        await using var stream = new FileStream(
            fullArchivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        return await ExtractEntriesAsync(
            stream,
            destinationDirectory,
            options,
            progress,
            fullArchivePath,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<UcxArchiveExtractResult> ExtractEntriesAsync(
        Stream archiveStream,
        string destinationDirectory,
        UcxExtractOptions? options = null,
        IProgress<ArchiveProgressSnapshot>? progress = null,
        string? archivePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        options ??= new UcxExtractOptions();
        var timer = new PauseAwareStopwatch(options.PauseToken);
        timer.Start();

        var fullDestDir = Path.GetFullPath(destinationDirectory);

        var (header, manifest) = await ReadHeaderAndManifestAsync(archiveStream, archivePath, cancellationToken)
            .ConfigureAwait(false);

        var selectedEntries = manifest.Entries
            .Where(e => UcxPathSecurity.MatchesSelectPatterns(e.RelativePath, options.SelectPatterns))
            .ToList();

        // Pre-validate ALL matched entry paths against zip-slip BEFORE creating destination directories or writing a single byte!
        var validatedTargets = new List<(UcxArchiveEntry Entry, string ResolvedPath)>(selectedEntries.Count);
        foreach (var entry in selectedEntries)
        {
            var resolved = UcxPathSecurity.ValidateAndResolveExtractPath(archivePath, entry.RelativePath, fullDestDir);
            validatedTargets.Add((entry, resolved));
        }

        try
        {
            Directory.CreateDirectory(fullDestDir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new UltraCompressException(UltraCompressErrors.MapException(ex, fullDestDir), ex);
        }

        var blockMap = manifest.Blocks.ToDictionary(b => b.BlockIndex);
        long totalSelectedBytes = validatedTargets.Where(t => !t.Entry.IsDirectory).Sum(t => t.Entry.OriginalSize);
        int totalSelectedFiles = validatedTargets.Count(t => !t.Entry.IsDirectory);

        int extractedFiles = 0;
        int extractedDirectories = 0;
        int skippedExisting = 0;
        int decompressedBlocks = 0;
        long extractedBytes = 0;
        var outputPaths = new List<string>(totalSelectedFiles);

        // Extract explicit empty directory entries first.
        foreach (var (entry, resolvedDirPath) in validatedTargets.Where(t => t.Entry.IsDirectory))
        {
            await CheckPauseAndCancelAsync(options.PauseToken, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(resolvedDirPath);
            if (options.PreserveTimestamps)
            {
                TryApplyDirectoryTimestamp(resolvedDirPath, entry.LastModifiedUtc);
            }

            extractedDirectories++;
        }

        // Group file entries by BlockIndex so each needed solid sub-block is read & decompressed at most ONCE!
        var zeroByteTargets = validatedTargets
            .Where(t => !t.Entry.IsDirectory && (t.Entry.BlockIndex is null || t.Entry.OriginalSize == 0))
            .ToList();

        foreach (var (entry, initialTargetPath) in zeroByteTargets)
        {
            await CheckPauseAndCancelAsync(options.PauseToken, cancellationToken).ConfigureAwait(false);
            var emptyHash = Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty)).ToLowerInvariant();
            if (!string.Equals(emptyHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw UltraCompressErrors.ChecksumMismatch(archivePath, entry.RelativePath, entry.Sha256, emptyHash);
            }

            var finalPath = ResolveConflictTargetPath(initialTargetPath, options.ConflictPolicy, out var skip);
            if (skip)
            {
                skippedExisting++;
                continue;
            }

            await WriteVerifiedFileSliceAsync(
                finalPath,
                ReadOnlyMemory<byte>.Empty,
                entry,
                options,
                cancellationToken).ConfigureAwait(false);

            extractedFiles++;
            outputPaths.Add(finalPath);
        }

        var blockGroups = validatedTargets
            .Where(t => !t.Entry.IsDirectory && t.Entry.BlockIndex is not null && t.Entry.OriginalSize > 0)
            .GroupBy(t => t.Entry.BlockIndex!.Value)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in blockGroups)
        {
            await CheckPauseAndCancelAsync(options.PauseToken, cancellationToken).ConfigureAwait(false);

            if (!blockMap.TryGetValue(group.Key, out var block))
            {
                throw UltraCompressErrors.CorruptArchive(
                    archivePath,
                    $"Manifest entry references missing sub-block index {group.Key}.");
            }

            ValidateBlockBounds(block, header.ManifestOffset, archivePath);

            // Fast-path check: if ALL entries in this block would be skipped by SkipExisting, don't even read/decompress the block!
            var resolvedForGroup = new List<(UcxArchiveEntry Entry, string FinalTargetPath)>();
            foreach (var (entry, initialTargetPath) in group)
            {
                var finalPath = ResolveConflictTargetPath(initialTargetPath, options.ConflictPolicy, out var skip);
                if (skip)
                {
                    skippedExisting++;
                    continue;
                }

                resolvedForGroup.Add((entry, finalPath));
            }

            if (resolvedForGroup.Count == 0)
            {
                continue;
            }

            // O(1) direct slice optimization for uncompressed Store blocks: seek directly to each entry's byte range!
            if (UcxBlockCodecEngine.NormalizeCodecName(block.Codec) == "store")
            {
                foreach (var (entry, finalTargetPath) in resolvedForGroup)
                {
                    await CheckPauseAndCancelAsync(options.PauseToken, cancellationToken).ConfigureAwait(false);
                    ValidateSliceBounds(entry, block, archivePath);

                    long sliceOffset = block.OffsetInArchive + entry.OffsetInBlock;
                    var sliceBytes = await ReadExactRangeAsync(
                        archiveStream,
                        sliceOffset,
                        (int)entry.LengthInBlock,
                        archivePath,
                        cancellationToken).ConfigureAwait(false);

                    VerifySliceSha256(sliceBytes, entry, archivePath);
                    await WriteVerifiedFileSliceAsync(finalTargetPath, sliceBytes, entry, options, cancellationToken)
                        .ConfigureAwait(false);

                    extractedFiles++;
                    extractedBytes += sliceBytes.Length;
                    outputPaths.Add(finalTargetPath);

                    ReportProgress(
                        progress,
                        "Extracting",
                        extractedBytes,
                        totalSelectedBytes,
                        extractedFiles,
                        totalSelectedFiles,
                        decompressedBlocks,
                        blockGroups.Count,
                        timer.Elapsed,
                        entry.RelativePath);
                }

                decompressedBlocks++;
                continue;
            }

            // Solid compressed sub-block: read compressed block, verify compressed SHA-256, decompress once, extract all selected slices!
            var compressedBytes = await ReadExactRangeAsync(
                archiveStream,
                block.OffsetInArchive,
                (int)block.CompressedLength,
                archivePath,
                cancellationToken).ConfigureAwait(false);

            var actualCompressedSha = Convert.ToHexString(SHA256.HashData(compressedBytes)).ToLowerInvariant();
            if (!string.Equals(actualCompressedSha, block.CompressedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw UltraCompressErrors.ChecksumMismatch(
                    archivePath,
                    $"compressed sub-block #{block.BlockIndex}",
                    block.CompressedSha256,
                    actualCompressedSha);
            }

            var uncompressedBlock = await UcxBlockCodecEngine.DecompressBlockAsync(
                compressedBytes,
                block.Codec,
                block.CodecLevel,
                block.UncompressedLength,
                archivePath,
                options.CustomBackends,
                cancellationToken).ConfigureAwait(false);

            var actualUncompressedSha = Convert.ToHexString(SHA256.HashData(uncompressedBlock)).ToLowerInvariant();
            if (!string.Equals(actualUncompressedSha, block.UncompressedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw UltraCompressErrors.ChecksumMismatch(
                    archivePath,
                    $"decompressed sub-block #{block.BlockIndex}",
                    block.UncompressedSha256,
                    actualUncompressedSha);
            }

            decompressedBlocks++;

            foreach (var (entry, finalTargetPath) in resolvedForGroup)
            {
                await CheckPauseAndCancelAsync(options.PauseToken, cancellationToken).ConfigureAwait(false);
                ValidateSliceBounds(entry, block, archivePath);

                var slice = uncompressedBlock.AsMemory((int)entry.OffsetInBlock, (int)entry.LengthInBlock);
                VerifySliceSha256(slice.Span, entry, archivePath);

                await WriteVerifiedFileSliceAsync(finalTargetPath, slice, entry, options, cancellationToken)
                    .ConfigureAwait(false);

                extractedFiles++;
                extractedBytes += slice.Length;
                outputPaths.Add(finalTargetPath);

                ReportProgress(
                    progress,
                    "Extracting",
                    extractedBytes,
                    totalSelectedBytes,
                    extractedFiles,
                    totalSelectedFiles,
                    decompressedBlocks,
                    blockGroups.Count,
                    timer.Elapsed,
                    entry.RelativePath);
            }
        }

        timer.Stop();
        ReportProgress(
            progress,
            "Completed",
            extractedBytes,
            totalSelectedBytes,
            extractedFiles,
            totalSelectedFiles,
            decompressedBlocks,
            blockGroups.Count,
            timer.Elapsed,
            null);

        return new UcxArchiveExtractResult(
            archivePath ?? "(stream)",
            fullDestDir,
            extractedFiles,
            extractedDirectories,
            skippedExisting,
            decompressedBlocks,
            extractedBytes,
            outputPaths,
            timer.Elapsed);
    }

    public async Task<UcxArchiveVerifyResult> VerifyArchiveAsync(
        string archivePath,
        IProgress<ArchiveProgressSnapshot>? progress = null,
        IPauseToken? pauseToken = null,
        IReadOnlyDictionary<string, ICompressionBackend>? customBackends = null,
        CancellationToken cancellationToken = default)
    {
        var fullArchivePath = ResolveExistingArchivePath(archivePath);
        var timer = new PauseAwareStopwatch(pauseToken);
        timer.Start();

        await using var stream = new FileStream(
            fullArchivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        var (header, manifest) = await ReadHeaderAndManifestAsync(stream, fullArchivePath, cancellationToken)
            .ConfigureAwait(false);

        foreach (var entry in manifest.Entries)
        {
            UcxPathSecurity.NormalizeAndValidateRelativePath(entry.RelativePath, fullArchivePath);
        }

        var entriesByBlock = manifest.Entries
            .Where(e => !e.IsDirectory && e.BlockIndex is not null && e.OriginalSize > 0)
            .GroupBy(e => e.BlockIndex!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        long totalBytesToVerify = manifest.Entries.Where(e => !e.IsDirectory).Sum(e => e.OriginalSize);
        int totalFilesToVerify = manifest.Entries.Count(e => !e.IsDirectory);
        long verifiedBytes = 0;
        int verifiedFiles = 0;

        // Verify zero-byte files
        foreach (var emptyEntry in manifest.Entries.Where(e => !e.IsDirectory && e.OriginalSize == 0))
        {
            var emptySha = Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty)).ToLowerInvariant();
            if (!string.Equals(emptySha, emptyEntry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw UltraCompressErrors.ChecksumMismatch(fullArchivePath, emptyEntry.RelativePath, emptyEntry.Sha256, emptySha);
            }

            verifiedFiles++;
        }

        int verifiedBlocks = 0;
        foreach (var block in manifest.Blocks.OrderBy(b => b.BlockIndex))
        {
            await CheckPauseAndCancelAsync(pauseToken, cancellationToken).ConfigureAwait(false);
            ValidateBlockBounds(block, header.ManifestOffset, fullArchivePath);

            var compressed = await ReadExactRangeAsync(
                stream,
                block.OffsetInArchive,
                (int)block.CompressedLength,
                fullArchivePath,
                cancellationToken).ConfigureAwait(false);

            var compressedSha = Convert.ToHexString(SHA256.HashData(compressed)).ToLowerInvariant();
            if (!string.Equals(compressedSha, block.CompressedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw UltraCompressErrors.ChecksumMismatch(
                    fullArchivePath,
                    $"compressed sub-block #{block.BlockIndex}",
                    block.CompressedSha256,
                    compressedSha);
            }

            var uncompressed = await UcxBlockCodecEngine.DecompressBlockAsync(
                compressed,
                block.Codec,
                block.CodecLevel,
                block.UncompressedLength,
                fullArchivePath,
                customBackends,
                cancellationToken).ConfigureAwait(false);

            var uncompressedSha = Convert.ToHexString(SHA256.HashData(uncompressed)).ToLowerInvariant();
            if (!string.Equals(uncompressedSha, block.UncompressedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw UltraCompressErrors.ChecksumMismatch(
                    fullArchivePath,
                    $"decompressed sub-block #{block.BlockIndex}",
                    block.UncompressedSha256,
                    uncompressedSha);
            }

            if (entriesByBlock.TryGetValue(block.BlockIndex, out var blockEntries))
            {
                var (count, bytes) = VerifyBlockEntriesSync(uncompressed, blockEntries, block, fullArchivePath);
                verifiedFiles += count;
                verifiedBytes += bytes;
            }

            verifiedBlocks++;
            ReportProgress(
                progress,
                "Verifying",
                verifiedBytes,
                totalBytesToVerify,
                verifiedFiles,
                totalFilesToVerify,
                verifiedBlocks,
                manifest.Blocks.Count,
                timer.Elapsed,
                $"block #{block.BlockIndex}");
        }

        if (verifiedFiles != totalFilesToVerify)
        {
            throw UltraCompressErrors.CorruptArchive(
                fullArchivePath,
                $"Manifest references unreachable block entries (verified {verifiedFiles} of {totalFilesToVerify} files).");
        }

        timer.Stop();
        return new UcxArchiveVerifyResult(
            fullArchivePath,
            IsValid: true,
            VerifiedFiles: verifiedFiles,
            VerifiedBlocks: verifiedBlocks,
            VerifiedOriginalBytes: verifiedBytes,
            Elapsed: timer.Elapsed,
            Message: $"Verified {verifiedFiles} file(s) across {verifiedBlocks} block(s) ({UltraCompressErrors.FormatBytes(verifiedBytes)}) — all SHA-256 checksums bit-exact.");
    }

    private static string ResolveExistingArchivePath(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw UltraCompressErrors.InvalidArguments("Archive path is required.");
        }

        var full = Path.GetFullPath(archivePath);
        if (!File.Exists(full))
        {
            throw UltraCompressErrors.InputNotFound(archivePath);
        }

        return full;
    }

    private static (UcxParsedHeader Header, UcxArchiveManifest Manifest) ReadHeaderAndManifestSync(
        Stream stream,
        string? archivePath)
    {
        long totalLen = stream.Length;
        Span<byte> headerBuf = stackalloc byte[UcxFormatConstants.HeaderSizeBytes];
        stream.Seek(0, SeekOrigin.Begin);
        ReadExactSync(stream, headerBuf, archivePath);

        var header = UcxFormatConstants.ParseAndValidateHeader(headerBuf, totalLen, archivePath);

        int tailLen = checked((int)header.ManifestLength + UcxFormatConstants.TrailerSizeBytes);
        var tailBuf = new byte[tailLen];
        stream.Seek(header.ManifestOffset, SeekOrigin.Begin);
        ReadExactSync(stream, tailBuf, archivePath);

        var manifest = DecodeAndVerifyManifest(header, tailBuf, archivePath);
        return (header, manifest);
    }

    private static async Task<(UcxParsedHeader Header, UcxArchiveManifest Manifest)> ReadHeaderAndManifestAsync(
        Stream stream,
        string? archivePath,
        CancellationToken cancellationToken)
    {
        long totalLen = stream.Length;
        var headerBuf = await ReadExactRangeAsync(stream, 0, UcxFormatConstants.HeaderSizeBytes, archivePath, cancellationToken)
            .ConfigureAwait(false);

        var header = UcxFormatConstants.ParseAndValidateHeader(headerBuf, totalLen, archivePath);

        int tailLen = checked((int)header.ManifestLength + UcxFormatConstants.TrailerSizeBytes);
        var tailBuf = await ReadExactRangeAsync(stream, header.ManifestOffset, tailLen, archivePath, cancellationToken)
            .ConfigureAwait(false);

        var manifest = DecodeAndVerifyManifest(header, tailBuf, archivePath);
        return (header, manifest);
    }

    private static UcxArchiveManifest DecodeAndVerifyManifest(
        UcxParsedHeader header,
        byte[] tailBuffer,
        string? archivePath)
    {
        var trailer = tailBuffer.AsSpan((int)header.ManifestLength, UcxFormatConstants.TrailerSizeBytes);
        if (!trailer.SequenceEqual(UcxFormatConstants.TrailerMagicUcxe))
        {
            throw UltraCompressErrors.CorruptArchive(
                archivePath,
                "Missing or damaged 'UCXE' container trailer (archive may be truncated).");
        }

        var storedManifest = tailBuffer.AsSpan(0, (int)header.ManifestLength);
        byte[] rawJson;
        if ((header.Flags & UcxFormatConstants.FlagManifestZstdCompressed) != 0)
        {
            try
            {
                rawJson = UcxBlockCodecEngine.DecompressZstd(storedManifest, header.ManifestUncompressedLength);
            }
            catch (Exception ex)
            {
                throw UltraCompressErrors.CorruptArchive(
                    archivePath,
                    $"Failed to decompress archive manifest: {ex.Message}",
                    ex);
            }
        }
        else
        {
            if (storedManifest.Length != header.ManifestUncompressedLength)
            {
                throw UltraCompressErrors.CorruptArchive(
                    archivePath,
                    "Manifest uncompressed length header mismatch.");
            }

            rawJson = storedManifest.ToArray();
        }

        var actualSha = SHA256.HashData(rawJson);
        if (!actualSha.AsSpan().SequenceEqual(header.ManifestSha256))
        {
            throw UltraCompressErrors.ChecksumMismatch(
                archivePath,
                "archive manifest",
                Convert.ToHexString(header.ManifestSha256).ToLowerInvariant(),
                Convert.ToHexString(actualSha).ToLowerInvariant());
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<UcxArchiveManifest>(rawJson, UcxArchiveWriter.ManifestJsonOptions);
            if (manifest is null || manifest.Entries is null || manifest.Blocks is null || manifest.Summary is null)
            {
                throw UltraCompressErrors.CorruptArchive(archivePath, "Manifest deserialized with missing required sections.");
            }

            return manifest;
        }
        catch (JsonException ex)
        {
            throw UltraCompressErrors.CorruptArchive(archivePath, $"Invalid manifest JSON: {ex.Message}", ex);
        }
    }

    private static UcxArchiveInspection BuildInspection(
        UcxArchiveManifest manifest,
        long archiveFileBytes,
        string? archivePath)
    {
        manifest.Summary.ArchiveBytes = archiveFileBytes;
        var blockMap = manifest.Blocks.ToDictionary(b => b.BlockIndex);
        var views = new List<UcxArchiveEntryView>(manifest.Entries.Count);

        foreach (var entry in manifest.Entries)
        {
            string codec = "none";
            int level = 0;
            string rationale = entry.IsDirectory
                ? "Directory metadata entry."
                : "Zero-byte file metadata entry.";
            long effectiveCompressed = 0;

            if (entry.IsDuplicate)
            {
                codec = "dedup";
                rationale = $"Exact SHA-256 duplicate of '{entry.CanonicalEntryPath}'; 0 additional payload bytes stored.";
                effectiveCompressed = 0;
            }
            else if (entry.BlockIndex is not null && blockMap.TryGetValue(entry.BlockIndex.Value, out var blk))
            {
                codec = blk.Codec;
                level = blk.CodecLevel;
                rationale = blk.CodecRationale;
                effectiveCompressed = blk.UncompressedLength > 0
                    ? (long)Math.Round((double)entry.OriginalSize / blk.UncompressedLength * blk.CompressedLength)
                    : 0;
            }

            views.Add(new UcxArchiveEntryView
            {
                RelativePath = entry.RelativePath,
                IsDirectory = entry.IsDirectory,
                OriginalSize = entry.OriginalSize,
                EffectiveCompressedSize = effectiveCompressed,
                Sha256 = entry.Sha256,
                LastModifiedUtc = entry.LastModifiedUtc,
                CreationUtc = entry.CreationUtc,
                Attributes = (FileAttributes)entry.FileAttributesRaw,
                ContentClass = entry.ContentClass,
                BlockIndex = entry.BlockIndex,
                Codec = codec,
                CodecLevel = level,
                IsDuplicate = entry.IsDuplicate,
                CanonicalEntryPath = entry.CanonicalEntryPath,
                CodecRationale = rationale,
            });
        }

        var rootTree = BuildEntryTree(views);

        return new UcxArchiveInspection
        {
            ArchivePath = archivePath,
            ArchiveFileBytes = archiveFileBytes,
            FormatVersion = $"UCX1 ({manifest.FormatVersion})",
            CreatedUtc = manifest.CreatedUtc,
            Profile = manifest.Profile,
            Summary = manifest.Summary,
            Blocks = manifest.Blocks,
            Entries = views,
            RootTree = rootTree,
        };
    }

    private static UcxEntryTreeNode BuildEntryTree(IReadOnlyList<UcxArchiveEntryView> entries)
    {
        var root = new UcxEntryTreeNode
        {
            Name = "/",
            FullPath = string.Empty,
            IsDirectory = true,
        };

        var dirLookup = new Dictionary<string, UcxEntryTreeNode>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = root,
        };

        foreach (var entry in entries)
        {
            var segments = entry.RelativePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            string parentPath = string.Empty;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                string currentPath = string.IsNullOrEmpty(parentPath)
                    ? segments[i]
                    : $"{parentPath}/{segments[i]}";

                if (!dirLookup.TryGetValue(currentPath, out var dirNode))
                {
                    dirNode = new UcxEntryTreeNode
                    {
                        Name = segments[i],
                        FullPath = currentPath,
                        IsDirectory = true,
                    };
                    dirLookup[parentPath].Children.Add(dirNode);
                    dirLookup[currentPath] = dirNode;
                }

                parentPath = currentPath;
            }

            var leafName = segments[^1];
            var leafFull = string.IsNullOrEmpty(parentPath) ? leafName : $"{parentPath}/{leafName}";

            if (entry.IsDirectory)
            {
                if (!dirLookup.ContainsKey(leafFull))
                {
                    var explicitDir = new UcxEntryTreeNode
                    {
                        Name = leafName,
                        FullPath = leafFull,
                        IsDirectory = true,
                        Entry = entry,
                    };
                    dirLookup[parentPath].Children.Add(explicitDir);
                    dirLookup[leafFull] = explicitDir;
                }

                continue;
            }

            var fileNode = new UcxEntryTreeNode
            {
                Name = leafName,
                FullPath = leafFull,
                IsDirectory = false,
                OriginalBytes = entry.OriginalSize,
                EffectiveCompressedBytes = entry.EffectiveCompressedSize,
                FileCount = 1,
                DuplicateCount = entry.IsDuplicate ? 1 : 0,
                Entry = entry,
            };

            dirLookup[parentPath].Children.Add(fileNode);

            // Roll up metrics to ancestor directories
            string rollCursor = parentPath;
            while (true)
            {
                var anc = dirLookup[rollCursor];
                anc.OriginalBytes += entry.OriginalSize;
                anc.EffectiveCompressedBytes += entry.EffectiveCompressedSize;
                anc.FileCount += 1;
                if (entry.IsDuplicate)
                {
                    anc.DuplicateCount += 1;
                }

                if (string.IsNullOrEmpty(rollCursor))
                {
                    break;
                }

                int slash = rollCursor.LastIndexOf('/');
                rollCursor = slash < 0 ? string.Empty : rollCursor[..slash];
            }
        }

        return root;
    }

    private static string ResolveConflictTargetPath(
        string candidatePath,
        DestinationConflictPolicy policy,
        out bool shouldSkip)
    {
        shouldSkip = false;
        if (!File.Exists(candidatePath))
        {
            return candidatePath;
        }

        switch (policy)
        {
            case DestinationConflictPolicy.Fail:
                throw UltraCompressErrors.DestinationAlreadyExists(candidatePath);

            case DestinationConflictPolicy.Overwrite:
                return candidatePath;

            case DestinationConflictPolicy.SkipExisting:
                shouldSkip = true;
                return candidatePath;

            case DestinationConflictPolicy.RenameNumbered:
            {
                var dir = Path.GetDirectoryName(candidatePath) ?? string.Empty;
                var stem = Path.GetFileNameWithoutExtension(candidatePath);
                var ext = Path.GetExtension(candidatePath);
                for (int i = 1; ; i++)
                {
                    var numbered = Path.Combine(dir, $"{stem} ({i}){ext}");
                    if (!File.Exists(numbered) && !Directory.Exists(numbered))
                    {
                        return numbered;
                    }
                }
            }

            default:
                return candidatePath;
        }
    }

    private static async Task WriteVerifiedFileSliceAsync(
        string finalTargetPath,
        ReadOnlyMemory<byte> verifiedBytes,
        UcxArchiveEntry entry,
        UcxExtractOptions options,
        CancellationToken cancellationToken)
    {
        var parentDir = Path.GetDirectoryName(finalTargetPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        // Write via atomic temp file in the destination directory so an interrupted/failed write never corrupts existing files.
        var tempPath = $"{finalTargetPath}.{Guid.NewGuid():N}.ucxtmp";
        try
        {
            await using (var outStream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: WriteChunkSize,
                useAsync: true))
            {
                int offset = 0;
                while (offset < verifiedBytes.Length)
                {
                    int len = Math.Min(WriteChunkSize, verifiedBytes.Length - offset);
                    await outStream.WriteAsync(verifiedBytes.Slice(offset, len), cancellationToken).ConfigureAwait(false);
                    offset += len;
                    await CheckPauseAndCancelAsync(options.PauseToken, cancellationToken).ConfigureAwait(false);
                }

                await outStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(finalTargetPath))
            {
                // Clear ReadOnly on existing target before overwrite if present.
                var existingAttr = File.GetAttributes(finalTargetPath);
                if ((existingAttr & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(finalTargetPath, existingAttr & ~FileAttributes.ReadOnly);
                }
            }

            File.Move(tempPath, finalTargetPath, overwrite: true);

            if (options.PreserveTimestamps && entry.LastModifiedUtc != default)
            {
                File.SetLastWriteTimeUtc(finalTargetPath, entry.LastModifiedUtc.UtcDateTime);
            }

            if (options.PreserveAttributes && OperatingSystem.IsWindows())
            {
                var safeAttrs = ((FileAttributes)entry.FileAttributesRaw)
                    & (FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.ReadOnly);
                if (safeAttrs != 0)
                {
                    File.SetAttributes(finalTargetPath, safeAttrs);
                }
            }
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempPath);
            if (ex is UltraCompressException or OperationCanceledException)
            {
                throw;
            }

            throw new UltraCompressException(UltraCompressErrors.MapException(ex, finalTargetPath), ex);
        }
    }

    private static (int Count, long Bytes) VerifyBlockEntriesSync(
        byte[] uncompressed,
        List<UcxArchiveEntry> blockEntries,
        UcxArchiveBlock block,
        string fullArchivePath)
    {
        int count = 0;
        long bytes = 0;
        foreach (var entry in blockEntries)
        {
            ValidateSliceBounds(entry, block, fullArchivePath);
            var slice = uncompressed.AsSpan((int)entry.OffsetInBlock, (int)entry.LengthInBlock);
            VerifySliceSha256(slice, entry, fullArchivePath);
            count++;
            bytes += entry.OriginalSize;
        }

        return (count, bytes);
    }

    private static void VerifySliceSha256(ReadOnlySpan<byte> slice, UcxArchiveEntry entry, string? archivePath)
    {
        var actualHex = Convert.ToHexString(SHA256.HashData(slice)).ToLowerInvariant();
        if (!string.Equals(actualHex, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw UltraCompressErrors.ChecksumMismatch(archivePath, $"entry '{entry.RelativePath}'", entry.Sha256, actualHex);
        }
    }

    private static void ValidateBlockBounds(UcxArchiveBlock block, long manifestOffset, string? archivePath)
    {
        if (block.OffsetInArchive < UcxFormatConstants.HeaderSizeBytes
            || block.CompressedLength < 0
            || block.UncompressedLength < 0
            || block.CompressedLength > Array.MaxLength
            || block.UncompressedLength > Array.MaxLength
            || block.OffsetInArchive + block.CompressedLength > manifestOffset)
        {
            throw UltraCompressErrors.CorruptArchive(
                archivePath,
                $"Sub-block #{block.BlockIndex} bounds (offset={block.OffsetInArchive}, len={block.CompressedLength}) overlap header or manifest.");
        }
    }

    private static void ValidateSliceBounds(UcxArchiveEntry entry, UcxArchiveBlock block, string? archivePath)
    {
        if (entry.OffsetInBlock < 0
            || entry.LengthInBlock < 0
            || entry.LengthInBlock != entry.OriginalSize
            || entry.OffsetInBlock + entry.LengthInBlock > block.UncompressedLength)
        {
            throw UltraCompressErrors.CorruptArchive(
                archivePath,
                $"Entry '{entry.RelativePath}' slice [{entry.OffsetInBlock}..+{entry.LengthInBlock}] exceeds block #{block.BlockIndex} size {block.UncompressedLength}.");
        }
    }

    private static void ReadExactSync(Stream stream, Span<byte> buffer, string? archivePath)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer[offset..]);
            if (read == 0)
            {
                throw UltraCompressErrors.CorruptArchive(archivePath, "Unexpected end of stream while reading container.");
            }

            offset += read;
        }
    }

    private static async Task<byte[]> ReadExactRangeAsync(
        Stream stream,
        long offset,
        int length,
        string? archivePath,
        CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        int readTotal = 0;
        while (readTotal < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(readTotal, length - readTotal), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw UltraCompressErrors.CorruptArchive(
                    archivePath,
                    $"Unexpected EOF reading {length} bytes at offset {offset} (got {readTotal}).");
            }

            readTotal += read;
        }

        return buffer;
    }

    private static async ValueTask CheckPauseAndCancelAsync(IPauseToken? pauseToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pauseToken is not null)
        {
            await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ReportProgress(
        IProgress<ArchiveProgressSnapshot>? progress,
        string stage,
        long processedBytes,
        long totalBytes,
        int processedFiles,
        int totalFiles,
        int completedBlocks,
        int totalBlocks,
        TimeSpan activeElapsed,
        string? currentItem)
    {
        if (progress is null)
        {
            return;
        }

        double seconds = Math.Max(activeElapsed.TotalSeconds, 0.001);
        double mbPerSec = processedBytes > 0 ? (processedBytes / (1024.0 * 1024.0)) / seconds : 0.0;
        double? eta = stage == "Completed"
            ? 0.0
            : (processedBytes > 0 && totalBytes > processedBytes
                ? (totalBytes - processedBytes) / (processedBytes / seconds)
                : null);

        progress.Report(new ArchiveProgressSnapshot
        {
            Stage = stage,
            ProcessedBytes = processedBytes,
            TotalBytes = totalBytes,
            ProcessedFiles = processedFiles,
            TotalFiles = totalFiles,
            CompletedBlocks = completedBlocks,
            TotalBlocks = totalBlocks,
            ThroughputMbPerSec = Math.Round(mbPerSec, 2),
            EstimatedRemainingSeconds = eta is null ? null : Math.Round(eta.Value, 2),
            CurrentItem = currentItem,
        });
    }

    private static void TryApplyDirectoryTimestamp(string dirPath, DateTimeOffset lastModifiedUtc)
    {
        try
        {
            if (lastModifiedUtc != default)
            {
                Directory.SetLastWriteTimeUtc(dirPath, lastModifiedUtc.UtcDateTime);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
