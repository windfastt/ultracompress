using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UltraCompress.Core.Errors;
using UltraCompress.Core.Jobs;
using UltraCompress.Core.Safety;

namespace UltraCompress.Core.Archive;

/// <summary>
/// Production <c>.ucx</c> (<c>UCX1</c>) multi-file archive writer supporting single files,
/// recursive directory trees, multi-path batches, exact SHA-256 deduplication, heterogeneous
/// per-class solid/store sub-blocks, cooperative pause/cancel, and crash-recoverable <c>.part</c> checkpoints.
/// </summary>
public sealed class UcxArchiveWriter
{
    private const int IoChunkSize = 256 * 1024;
    private const int HeadSampleSize = 4096;

    internal static readonly JsonSerializerOptions ManifestJsonOptions = CreateManifestJsonOptions();

    public Task<UcxArchiveWriteResult> CreateFromPathAsync(
        string outputArchivePath,
        string inputPath,
        UcxPackOptions? options = null,
        IProgress<ArchiveProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(outputArchivePath, [inputPath], options, progress, cancellationToken);

    public async Task<UcxArchiveWriteResult> CreateAsync(
        string outputArchivePath,
        IEnumerable<string> inputPaths,
        UcxPackOptions? options = null,
        IProgress<ArchiveProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputArchivePath);
        ArgumentNullException.ThrowIfNull(inputPaths);

        options ??= new UcxPackOptions();
        MutatingOperationGuard.EnforceProfileBoundary(options.Profile, OperationKind.LosslessCompression);
        if (options.MaxSolidBlockBytes <= 0)
        {
            throw UltraCompressErrors.InvalidArguments("MaxSolidBlockBytes must be positive.");
        }

        var fullOutputPath = Path.GetFullPath(outputArchivePath);
        if (!options.OverwriteDestination && File.Exists(fullOutputPath))
        {
            throw UltraCompressErrors.DestinationAlreadyExists(fullOutputPath);
        }

        var outputDir = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            try
            {
                Directory.CreateDirectory(outputDir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new UltraCompressException(UltraCompressErrors.MapException(ex, outputDir), ex);
            }
        }

        bool callerManagedStaging = !string.IsNullOrWhiteSpace(options.StagingPartPath);
        var stagingPath = callerManagedStaging
            ? Path.GetFullPath(options.StagingPartPath!)
            : fullOutputPath + ".part";

        var stagingDir = Path.GetDirectoryName(stagingPath);
        if (!string.IsNullOrEmpty(stagingDir))
        {
            Directory.CreateDirectory(stagingDir);
        }

        var activeTimer = new PauseAwareStopwatch(options.PauseToken);
        activeTimer.Start();

        try
        {
            var rawItems = DiscoverInputItems(inputPaths, options.PreserveRootFolderName);
            long totalDiscoverableBytes = rawItems.Where(i => !i.IsDirectory).Sum(i => i.Length);
            int totalFiles = rawItems.Count(i => !i.IsDirectory);
            int totalDirs = rawItems.Count(i => i.IsDirectory);

            ReportProgress(progress, "Scanning", 0, totalDiscoverableBytes, 0, totalFiles, 0, 0, activeTimer.Elapsed, null);

            // Phase 1: Scan files, compute SHA-256, classify content, and deduplicate identical payloads.
            var entries = new List<UcxArchiveEntry>(rawItems.Count);
            var uniqueItems = new List<ScannedUniqueFile>();
            var canonicalByShaAndSize = new Dictionary<(string Sha256, long Size), ScannedUniqueFile>();

            long scannedBytes = 0;
            int scannedFiles = 0;
            int duplicateCount = 0;
            long duplicateBytesSaved = 0;
            var duplicateEntries = new List<(UcxArchiveEntry Entry, ScannedUniqueFile Canonical)>();

            foreach (var item in rawItems)
            {
                await CheckPauseAndCancelAsync(options.PauseToken, cancellationToken).ConfigureAwait(false);

                if (item.IsDirectory)
                {
                    entries.Add(new UcxArchiveEntry
                    {
                        RelativePath = item.RelativePath,
                        IsDirectory = true,
                        OriginalSize = 0,
                        Sha256 = EmptyBytesSha256,
                        LastModifiedUtc = item.LastModifiedUtc,
                        CreationUtc = item.CreationUtc,
                        FileAttributesRaw = (int)item.Attributes,
                        ContentClass = UcxContentClass.EmptyOrDirectory,
                    });
                    continue;
                }

                var scan = await ScanSingleFileAsync(
                    item.FullSourcePath,
                    options.PauseToken,
                    bytesDelta =>
                    {
                        scannedBytes += bytesDelta;
                    },
                    cancellationToken).ConfigureAwait(false);

                scannedFiles++;
                var (contentClass, signal) = UcxContentClassifier.Classify(
                    item.FullSourcePath,
                    scan.Length,
                    scan.HeadBytes,
                    scan.EntropyBitsPerByte);

                var entry = new UcxArchiveEntry
                {
                    RelativePath = item.RelativePath,
                    IsDirectory = false,
                    OriginalSize = scan.Length,
                    Sha256 = scan.Sha256Hex,
                    LastModifiedUtc = item.LastModifiedUtc,
                    CreationUtc = item.CreationUtc,
                    FileAttributesRaw = (int)item.Attributes,
                    ContentClass = contentClass,
                };

                if (scan.Length > 0 && canonicalByShaAndSize.TryGetValue((scan.Sha256Hex, scan.Length), out var canonical))
                {
                    entry.IsDuplicate = true;
                    entry.CanonicalEntryPath = canonical.Entry.RelativePath;
                    duplicateCount++;
                    duplicateBytesSaved += scan.Length;
                    duplicateEntries.Add((entry, canonical));
                }
                else if (scan.Length > 0)
                {
                    var unique = new ScannedUniqueFile(item.FullSourcePath, entry, contentClass, signal);
                    canonicalByShaAndSize[(scan.Sha256Hex, scan.Length)] = unique;
                    uniqueItems.Add(unique);
                }

                entries.Add(entry);
                ReportProgress(
                    progress,
                    "Scanning",
                    Math.Min(scannedBytes, totalDiscoverableBytes),
                    totalDiscoverableBytes,
                    scannedFiles,
                    totalFiles,
                    0,
                    0,
                    activeTimer.Elapsed,
                    item.RelativePath);
            }

            // Phase 2: Deterministic heterogeneous sub-block partitioning.
            var plannedBlocks = PlanSubBlocks(uniqueItems, options.MaxSolidBlockBytes);
            options.OnTotalBlocksPlanned?.Invoke(plannedBlocks.Count);

            // Assign each unique entry its slice coordinates and propagate to exact duplicates.
            foreach (var block in plannedBlocks)
            {
                long cursor = 0;
                foreach (var unique in block.Files)
                {
                    unique.Entry.BlockIndex = block.BlockIndex;
                    unique.Entry.OffsetInBlock = cursor;
                    unique.Entry.LengthInBlock = unique.Entry.OriginalSize;
                    cursor += unique.Entry.OriginalSize;
                }
            }

            foreach (var (dupEntry, canonical) in duplicateEntries)
            {
                dupEntry.BlockIndex = canonical.Entry.BlockIndex;
                dupEntry.OffsetInBlock = canonical.Entry.OffsetInBlock;
                dupEntry.LengthInBlock = canonical.Entry.LengthInBlock;
            }

            long totalUniqueBytes = uniqueItems.Sum(u => u.Entry.OriginalSize);

            // Phase 3: Open .part staging file, validate any crash-recovery checkpoints, and compress remaining sub-blocks.
            var finalizedBlocks = new List<UcxArchiveBlock>(plannedBlocks.Count);
            int resumedBlockCount = 0;
            int newlyCompressedBlocks = 0;
            long processedUniqueBytes = 0;

            await using (var partStream = new FileStream(
                stagingPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                resumedBlockCount = await ValidateCheckpointsAndPositionStreamAsync(
                    partStream,
                    plannedBlocks,
                    options.ExistingCheckpoints,
                    finalizedBlocks,
                    cancellationToken).ConfigureAwait(false);

                for (int i = 0; i < resumedBlockCount; i++)
                {
                    processedUniqueBytes += plannedBlocks[i].UncompressedLength;
                }

                for (int blockIdx = resumedBlockCount; blockIdx < plannedBlocks.Count; blockIdx++)
                {
                    await CheckPauseAndCancelAsync(options.PauseToken, cancellationToken).ConfigureAwait(false);

                    var planned = plannedBlocks[blockIdx];
                    ReportProgress(
                        progress,
                        "CompressingBlocks",
                        processedUniqueBytes,
                        totalUniqueBytes,
                        scannedFiles,
                        totalFiles,
                        blockIdx,
                        plannedBlocks.Count,
                        activeTimer.Elapsed,
                        planned.Files[0].Entry.RelativePath);

                    var rawBlockBytes = await AssembleUncompressedBlockAsync(
                        planned,
                        options.PauseToken,
                        cancellationToken).ConfigureAwait(false);

                    var uncompressedSha256 = ComputeSha256Hex(rawBlockBytes);

                    await CheckPauseAndCancelAsync(options.PauseToken, cancellationToken).ConfigureAwait(false);

                    var encoded = await UcxBlockCodecEngine.CompressBlockAsync(
                        rawBlockBytes,
                        planned.ContentClass,
                        planned.Files.Count,
                        planned.SignalSummary,
                        options.Profile,
                        options.PreferredCodecOverride,
                        options.CustomBackends,
                        cancellationToken).ConfigureAwait(false);

                    await CheckPauseAndCancelAsync(options.PauseToken, cancellationToken).ConfigureAwait(false);

                    long blockOffset = partStream.Position;
                    await partStream.WriteAsync(encoded.Payload, cancellationToken).ConfigureAwait(false);
                    await partStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    partStream.Flush(flushToDisk: true);

                    var compressedSha256 = ComputeSha256Hex(encoded.Payload);
                    var blockDescriptor = new UcxArchiveBlock
                    {
                        BlockIndex = blockIdx,
                        ContentClass = planned.ContentClass,
                        Codec = encoded.Codec,
                        CodecLevel = encoded.CodecLevel,
                        CodecRationale = encoded.Rationale,
                        OffsetInArchive = blockOffset,
                        UncompressedLength = rawBlockBytes.Length,
                        CompressedLength = encoded.Payload.Length,
                        UncompressedSha256 = uncompressedSha256,
                        CompressedSha256 = compressedSha256,
                        EntryCount = planned.Files.Count,
                    };

                    finalizedBlocks.Add(blockDescriptor);
                    newlyCompressedBlocks++;
                    processedUniqueBytes += rawBlockBytes.Length;

                    if (options.OnBlockCommittedAsync is not null)
                    {
                        var checkpoint = new UcxBlockCheckpoint(
                            blockIdx,
                            planned.PlanHash,
                            planned.ContentClass,
                            encoded.Codec,
                            encoded.CodecLevel,
                            encoded.Rationale,
                            blockOffset,
                            rawBlockBytes.Length,
                            encoded.Payload.Length,
                            uncompressedSha256,
                            compressedSha256,
                            planned.Files.Count);
                        await options.OnBlockCommittedAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                    }

                    ReportProgress(
                        progress,
                        "CompressingBlocks",
                        processedUniqueBytes,
                        totalUniqueBytes,
                        totalFiles,
                        totalFiles,
                        blockIdx + 1,
                        plannedBlocks.Count,
                        activeTimer.Elapsed,
                        planned.Files[^1].Entry.RelativePath);
                }

                // Phase 4: Serialize indexed manifest + write UCX1 header + UCXE trailer.
                await CheckPauseAndCancelAsync(options.PauseToken, cancellationToken).ConfigureAwait(false);
                ReportProgress(
                    progress,
                    "WritingManifest",
                    totalUniqueBytes,
                    totalUniqueBytes,
                    totalFiles,
                    totalFiles,
                    plannedBlocks.Count,
                    plannedBlocks.Count,
                    activeTimer.Elapsed,
                    null);

                long compressedPayloadTotal = finalizedBlocks.Sum(b => b.CompressedLength);
                long originalTotal = entries.Where(e => !e.IsDirectory).Sum(e => e.OriginalSize);

                var summary = new UcxArchiveSummary
                {
                    TotalEntries = entries.Count,
                    FileCount = totalFiles,
                    DirectoryCount = totalDirs,
                    BlockCount = finalizedBlocks.Count,
                    OriginalBytes = originalTotal,
                    UniqueBytes = totalUniqueBytes,
                    CompressedPayloadBytes = compressedPayloadTotal,
                    DuplicateCount = duplicateCount,
                    DuplicateBytesSaved = duplicateBytesSaved,
                };

                var manifest = new UcxArchiveManifest
                {
                    FormatVersion = "1.0",
                    CreatedUtc = DateTimeOffset.UtcNow,
                    Profile = options.Profile,
                    Summary = summary,
                    Blocks = finalizedBlocks,
                    Entries = entries,
                };

                var manifestUtf8 = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);
                var manifestSha256 = SHA256.HashData(manifestUtf8);

                uint headerFlags = 0;
                byte[] manifestStored = manifestUtf8;
                if (manifestUtf8.Length >= 512)
                {
                    var zstdManifest = UcxBlockCodecEngine.CompressZstd(manifestUtf8, 6);
                    if (zstdManifest.Length < manifestUtf8.Length)
                    {
                        manifestStored = zstdManifest;
                        headerFlags |= UcxFormatConstants.FlagManifestZstdCompressed;
                    }
                }

                long manifestOffset = partStream.Position;
                await partStream.WriteAsync(manifestStored, cancellationToken).ConfigureAwait(false);
                await partStream.WriteAsync(UcxFormatConstants.TrailerMagicUcxe.ToArray(), cancellationToken).ConfigureAwait(false);

                long finalLength = partStream.Position;
                if (partStream.Length > finalLength)
                {
                    partStream.SetLength(finalLength);
                }

                var headerBytes = new byte[UcxFormatConstants.HeaderSizeBytes];
                UcxFormatConstants.WriteFinalHeader(
                    headerBytes,
                    headerFlags,
                    manifestOffset,
                    manifestStored.Length,
                    manifestUtf8.Length,
                    manifestSha256);

                partStream.Seek(0, SeekOrigin.Begin);
                await partStream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
                await partStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                partStream.Flush(flushToDisk: true);

                summary.ArchiveBytes = finalLength;

                // Close stream before atomic rename.
            }

            // Phase 5: Atomic staging commit (.ucx.part -> .ucx).
            File.Move(stagingPath, fullOutputPath, overwrite: options.OverwriteDestination);
            activeTimer.Stop();

            var finalFileSize = new FileInfo(fullOutputPath).Length;
            var finalSummary = new UcxArchiveSummary
            {
                TotalEntries = entries.Count,
                FileCount = totalFiles,
                DirectoryCount = totalDirs,
                BlockCount = finalizedBlocks.Count,
                OriginalBytes = entries.Where(e => !e.IsDirectory).Sum(e => e.OriginalSize),
                UniqueBytes = totalUniqueBytes,
                CompressedPayloadBytes = finalizedBlocks.Sum(b => b.CompressedLength),
                ArchiveBytes = finalFileSize,
                DuplicateCount = duplicateCount,
                DuplicateBytesSaved = duplicateBytesSaved,
            };

            ReportProgress(
                progress,
                "Completed",
                totalUniqueBytes,
                totalUniqueBytes,
                totalFiles,
                totalFiles,
                plannedBlocks.Count,
                plannedBlocks.Count,
                activeTimer.Elapsed,
                null);

            return new UcxArchiveWriteResult(
                fullOutputPath,
                finalSummary,
                finalizedBlocks,
                resumedBlockCount,
                newlyCompressedBlocks,
                activeTimer.Elapsed);
        }
        catch (Exception ex)
        {
            if (!callerManagedStaging)
            {
                TryDeleteFile(stagingPath);
            }

            if (ex is UltraCompressException or OperationCanceledException)
            {
                throw;
            }

            throw new UltraCompressException(UltraCompressErrors.MapException(ex, fullOutputPath), ex);
        }
    }

    private static string EmptyBytesSha256 { get; } =
        Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty)).ToLowerInvariant();

    private static string ComputeSha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static async ValueTask CheckPauseAndCancelAsync(IPauseToken? pauseToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pauseToken is not null)
        {
            await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static List<DiscoveredSourceItem> DiscoverInputItems(IEnumerable<string> inputPaths, bool preserveRootFolderName)
    {
        var materialized = inputPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList();

        if (materialized.Count == 0)
        {
            throw UltraCompressErrors.EmptyInputSet();
        }

        bool isMultiInput = materialized.Count > 1;
        var discovered = new List<DiscoveredSourceItem>();
        var usedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in materialized)
        {
            var full = Path.GetFullPath(raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (File.Exists(full))
            {
                var fi = new FileInfo(full);
                var leaf = fi.Name;
                var rel = EnsureUniqueRelativePath(leaf, full, usedRelativePaths);
                discovered.Add(new DiscoveredSourceItem(
                    full,
                    UcxPathSecurity.NormalizeAndValidateRelativePath(rel),
                    IsDirectory: false,
                    fi.Length,
                    fi.LastWriteTimeUtc,
                    fi.CreationTimeUtc,
                    fi.Attributes));
            }
            else if (Directory.Exists(full))
            {
                var rootInfo = new DirectoryInfo(full);
                var rootPrefix = (isMultiInput || preserveRootFolderName)
                    ? EnsureUniqueRelativePath(rootInfo.Name, full, usedRelativePaths)
                    : string.Empty;

                EnumerateDirectoryRecursive(rootInfo, full, rootPrefix, discovered, usedRelativePaths);
            }
            else
            {
                throw UltraCompressErrors.InputNotFound(raw);
            }
        }

        discovered.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return discovered;
    }

    private static void EnumerateDirectoryRecursive(
        DirectoryInfo currentDir,
        string rootDirFull,
        string rootPrefix,
        List<DiscoveredSourceItem> discovered,
        HashSet<string> usedRelativePaths)
    {
        FileSystemInfo[] children;
        try
        {
            children = currentDir.GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new UltraCompressException(UltraCompressErrors.MapException(ex, currentDir.FullName), ex);
        }

        Array.Sort(children, (a, b) => string.CompareOrdinal(a.Name, b.Name));

        if (children.Length == 0 && !string.Equals(currentDir.FullName, rootDirFull, StringComparison.Ordinal))
        {
            var relDir = BuildRelativeFromRoot(rootDirFull, currentDir.FullName, rootPrefix);
            var normalized = UcxPathSecurity.NormalizeAndValidateRelativePath(relDir);
            if (usedRelativePaths.Add(normalized))
            {
                discovered.Add(new DiscoveredSourceItem(
                    currentDir.FullName,
                    normalized,
                    IsDirectory: true,
                    0,
                    currentDir.LastWriteTimeUtc,
                    currentDir.CreationTimeUtc,
                    currentDir.Attributes | FileAttributes.Directory));
            }

            return;
        }

        foreach (var child in children)
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0 || child.LinkTarget is not null)
            {
                // Skip symlinks/junctions during recursive directory traversal to prevent infinite loops or unintended host leaks.
                continue;
            }

            if (child is DirectoryInfo subDir)
            {
                EnumerateDirectoryRecursive(subDir, rootDirFull, rootPrefix, discovered, usedRelativePaths);
            }
            else if (child is FileInfo fi)
            {
                var relFile = BuildRelativeFromRoot(rootDirFull, fi.FullName, rootPrefix);
                var normalized = UcxPathSecurity.NormalizeAndValidateRelativePath(relFile);
                usedRelativePaths.Add(normalized);
                discovered.Add(new DiscoveredSourceItem(
                    fi.FullName,
                    normalized,
                    IsDirectory: false,
                    fi.Length,
                    fi.LastWriteTimeUtc,
                    fi.CreationTimeUtc,
                    fi.Attributes));
            }
        }
    }

    private static string BuildRelativeFromRoot(string rootDirFull, string itemFull, string rootPrefix)
    {
        var rel = Path.GetRelativePath(rootDirFull, itemFull).Replace('\\', '/');
        return string.IsNullOrEmpty(rootPrefix) ? rel : $"{rootPrefix}/{rel}";
    }

    private static string EnsureUniqueRelativePath(string candidateName, string fullSourcePath, HashSet<string> usedRelativePaths)
    {
        if (usedRelativePaths.Add(candidateName))
        {
            return candidateName;
        }

        var parentName = Path.GetFileName(Path.GetDirectoryName(fullSourcePath) ?? string.Empty);
        if (!string.IsNullOrEmpty(parentName))
        {
            var withParent = $"{parentName}/{candidateName}";
            if (usedRelativePaths.Add(withParent))
            {
                return withParent;
            }
        }

        var stem = Path.GetFileNameWithoutExtension(candidateName);
        var ext = Path.GetExtension(candidateName);
        for (int i = 1; ; i++)
        {
            var numbered = $"{stem}_{i}{ext}";
            if (usedRelativePaths.Add(numbered))
            {
                return numbered;
            }
        }
    }

    private static async Task<FileScanResult> ScanSingleFileAsync(
        string fullPath,
        IPauseToken? pauseToken,
        Action<int> onBytesRead,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: IoChunkSize,
            useAsync: true);

        long length = stream.Length;
        if (length == 0)
        {
            return new FileScanResult(0, EmptyBytesSha256, Array.Empty<byte>(), 0.0);
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(IoChunkSize);
        var head = new byte[(int)Math.Min(HeadSampleSize, length)];
        int headCaptured = 0;

        // Track byte frequency table across entire streamed read for exact O(1)-memory Shannon entropy.
        var freq = new long[256];

        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, IoChunkSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (headCaptured < head.Length)
                {
                    int toCopy = Math.Min(read, head.Length - headCaptured);
                    buffer.AsSpan(0, toCopy).CopyTo(head.AsSpan(headCaptured));
                    headCaptured += toCopy;
                }

                sha.AppendData(buffer, 0, read);
                for (int i = 0; i < read; i++)
                {
                    freq[buffer[i]]++;
                }

                onBytesRead(read);
                await CheckPauseAndCancelAsync(pauseToken, cancellationToken).ConfigureAwait(false);
            }

            var shaHex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            double entropy = ComputeEntropyFromFrequencies(freq, length);
            return new FileScanResult(length, shaHex, head[..headCaptured], entropy);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static double ComputeEntropyFromFrequencies(long[] freq, long totalBytes)
    {
        if (totalBytes <= 0)
        {
            return 0.0;
        }

        double entropy = 0.0;
        double invTotal = 1.0 / totalBytes;
        for (int i = 0; i < 256; i++)
        {
            long c = freq[i];
            if (c == 0)
            {
                continue;
            }

            double p = c * invTotal;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    private static List<PlannedSubBlock> PlanSubBlocks(List<ScannedUniqueFile> uniqueFiles, long maxSolidBlockBytes)
    {
        var blocks = new List<PlannedSubBlock>();
        if (uniqueFiles.Count == 0)
        {
            return blocks;
        }

        // 1. IncompressibleMedia: each media file gets its own Store sub-block for instant O(1) random access.
        foreach (var media in uniqueFiles
                     .Where(f => f.ContentClass == UcxContentClass.IncompressibleMedia)
                     .OrderBy(f => f.Entry.RelativePath, StringComparer.Ordinal))
        {
            blocks.Add(CreatePlannedBlock(blocks.Count, UcxContentClass.IncompressibleMedia, [media]));
        }

        // 2. Compressible content classes: group by class, sort by (extension, path) for solid cross-file dictionary locality,
        //    and cap each solid block at maxSolidBlockBytes for fast selective extraction.
        UcxContentClass[] solidClasses =
        [
            UcxContentClass.SourceAndText,
            UcxContentClass.StructuredData,
            UcxContentClass.ExecutableAndBinary,
        ];

        foreach (var contentClass in solidClasses)
        {
            var group = uniqueFiles
                .Where(f => f.ContentClass == contentClass)
                .OrderBy(f => Path.GetExtension(f.Entry.RelativePath), StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Entry.RelativePath, StringComparer.Ordinal)
                .ToList();

            if (group.Count == 0)
            {
                continue;
            }

            var currentBatch = new List<ScannedUniqueFile>();
            long currentBytes = 0;

            foreach (var file in group)
            {
                if (currentBatch.Count > 0 && currentBytes + file.Entry.OriginalSize > maxSolidBlockBytes)
                {
                    blocks.Add(CreatePlannedBlock(blocks.Count, contentClass, currentBatch));
                    currentBatch = new List<ScannedUniqueFile>();
                    currentBytes = 0;
                }

                currentBatch.Add(file);
                currentBytes += file.Entry.OriginalSize;
            }

            if (currentBatch.Count > 0)
            {
                blocks.Add(CreatePlannedBlock(blocks.Count, contentClass, currentBatch));
            }
        }

        return blocks;
    }

    private static PlannedSubBlock CreatePlannedBlock(
        int blockIndex,
        UcxContentClass contentClass,
        List<ScannedUniqueFile> files)
    {
        var sb = new StringBuilder();
        sb.Append(contentClass).Append(':');
        foreach (var f in files)
        {
            sb.Append(f.Entry.RelativePath).Append('=').Append(f.Entry.Sha256).Append(';');
        }

        var planHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        long uncompressedLen = files.Sum(f => f.Entry.OriginalSize);
        string signal = files.Count == 1
            ? files[0].Signal
            : $"{files.Count} {contentClass} files ({files[0].Signal})";

        return new PlannedSubBlock(blockIndex, contentClass, files, uncompressedLen, planHash, signal);
    }

    private static async Task<int> ValidateCheckpointsAndPositionStreamAsync(
        FileStream partStream,
        List<PlannedSubBlock> plannedBlocks,
        IReadOnlyList<UcxBlockCheckpoint>? existingCheckpoints,
        List<UcxArchiveBlock> finalizedBlocks,
        CancellationToken cancellationToken)
    {
        if (existingCheckpoints is null || existingCheckpoints.Count == 0 || partStream.Length < UcxFormatConstants.HeaderSizeBytes)
        {
            partStream.SetLength(0);
            var placeholder = new byte[UcxFormatConstants.HeaderSizeBytes];
            UcxFormatConstants.WriteIncompleteStagingHeader(placeholder);
            await partStream.WriteAsync(placeholder, cancellationToken).ConfigureAwait(false);
            await partStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var ordered = existingCheckpoints.OrderBy(c => c.BlockIndex).ToList();
        long expectedOffset = UcxFormatConstants.HeaderSizeBytes;
        int validCount = 0;

        for (int i = 0; i < ordered.Count && i < plannedBlocks.Count; i++)
        {
            var cp = ordered[i];
            var planned = plannedBlocks[i];
            if (cp.BlockIndex != i
                || !string.Equals(cp.BlockPlanHash, planned.PlanHash, StringComparison.OrdinalIgnoreCase)
                || cp.OffsetInArchive != expectedOffset
                || cp.UncompressedLength != planned.UncompressedLength
                || cp.OffsetInArchive + cp.CompressedLength > partStream.Length)
            {
                break;
            }

            // Verify on-disk compressed block SHA-256 to guarantee zero torn writes from crash!
            partStream.Seek(cp.OffsetInArchive, SeekOrigin.Begin);
            var onDiskCompressed = new byte[cp.CompressedLength];
            int totalRead = 0;
            while (totalRead < onDiskCompressed.Length)
            {
                int r = await partStream.ReadAsync(onDiskCompressed.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
                if (r == 0)
                {
                    break;
                }

                totalRead += r;
            }

            if (totalRead != onDiskCompressed.Length)
            {
                break;
            }

            var actualSha = ComputeSha256Hex(onDiskCompressed);
            if (!string.Equals(actualSha, cp.CompressedSha256, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            finalizedBlocks.Add(new UcxArchiveBlock
            {
                BlockIndex = cp.BlockIndex,
                ContentClass = cp.ContentClass,
                Codec = cp.Codec,
                CodecLevel = cp.CodecLevel,
                CodecRationale = cp.CodecRationale,
                OffsetInArchive = cp.OffsetInArchive,
                UncompressedLength = cp.UncompressedLength,
                CompressedLength = cp.CompressedLength,
                UncompressedSha256 = cp.UncompressedSha256,
                CompressedSha256 = cp.CompressedSha256,
                EntryCount = cp.EntryCount,
            });

            expectedOffset += cp.CompressedLength;
            validCount++;
        }

        if (validCount == 0)
        {
            partStream.SetLength(0);
            var placeholder = new byte[UcxFormatConstants.HeaderSizeBytes];
            UcxFormatConstants.WriteIncompleteStagingHeader(placeholder);
            await partStream.WriteAsync(placeholder, cancellationToken).ConfigureAwait(false);
            await partStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        // Truncate any torn partial tail after the last verified block checkpoint.
        if (partStream.Length != expectedOffset)
        {
            partStream.SetLength(expectedOffset);
        }

        partStream.Seek(expectedOffset, SeekOrigin.Begin);
        return validCount;
    }

    private static async Task<byte[]> AssembleUncompressedBlockAsync(
        PlannedSubBlock block,
        IPauseToken? pauseToken,
        CancellationToken cancellationToken)
    {
        if (block.UncompressedLength > Array.MaxLength)
        {
            throw UltraCompressErrors.InvalidArguments(
                $"Sub-block size ({block.UncompressedLength} B) exceeds single-block memory cap.");
        }

        var output = new byte[(int)block.UncompressedLength];
        int cursor = 0;

        foreach (var file in block.Files)
        {
            await CheckPauseAndCancelAsync(pauseToken, cancellationToken).ConfigureAwait(false);

            await using var fs = new FileStream(
                file.FullSourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: IoChunkSize,
                useAsync: true);

            int remainingForFile = (int)file.Entry.OriginalSize;
            using var fileHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            while (remainingForFile > 0)
            {
                int chunk = Math.Min(IoChunkSize, remainingForFile);
                int read = await fs.ReadAsync(output.AsMemory(cursor, chunk), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw UltraCompressErrors.CorruptArchive(
                        null,
                        $"Source file '{file.FullSourcePath}' changed size concurrently while packing.");
                }

                fileHasher.AppendData(output, cursor, read);
                cursor += read;
                remainingForFile -= read;
                await CheckPauseAndCancelAsync(pauseToken, cancellationToken).ConfigureAwait(false);
            }

            var rereadHash = Convert.ToHexString(fileHasher.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(rereadHash, file.Entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UltraCompressException(new UltraCompressError
                {
                    Code = UltraCompressErrorCode.IoFailure,
                    SymbolicCode = UltraCompressErrors.GetSymbolicCode(UltraCompressErrorCode.IoFailure),
                    Title = "Source File Modified During Archiving",
                    Message = $"Source file '{file.FullSourcePath}' changed while being packed into the archive.",
                    Remediation = "Pause active writers modifying the folder or close open applications, then re-run pack.",
                    TargetPath = file.FullSourcePath,
                });
            }
        }

        return output;
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
        double mbPerSec = processedBytes > 0
            ? (processedBytes / (1024.0 * 1024.0)) / seconds
            : 0.0;

        double? eta = null;
        if (stage == "Completed")
        {
            eta = 0.0;
        }
        else if (processedBytes > 0 && totalBytes > processedBytes)
        {
            double bytesPerSec = processedBytes / seconds;
            eta = bytesPerSec > 0 ? (totalBytes - processedBytes) / bytesPerSec : null;
        }

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

    private static JsonSerializerOptions CreateManifestJsonOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        opts.Converters.Add(new JsonStringEnumConverter());
        return opts;
    }

    private sealed record DiscoveredSourceItem(
        string FullSourcePath,
        string RelativePath,
        bool IsDirectory,
        long Length,
        DateTimeOffset LastModifiedUtc,
        DateTimeOffset CreationUtc,
        FileAttributes Attributes);

    private sealed record FileScanResult(
        long Length,
        string Sha256Hex,
        byte[] HeadBytes,
        double EntropyBitsPerByte);

    private sealed record ScannedUniqueFile(
        string FullSourcePath,
        UcxArchiveEntry Entry,
        UcxContentClass ContentClass,
        string Signal);

    private sealed record PlannedSubBlock(
        int BlockIndex,
        UcxContentClass ContentClass,
        List<ScannedUniqueFile> Files,
        long UncompressedLength,
        string PlanHash,
        string SignalSummary);
}

/// <summary>
/// Tracks active processing elapsed time while excluding cooperative pause intervals
/// so live MB/s throughput and ETA remain accurate across pauses.
/// </summary>
public sealed class PauseAwareStopwatch
{
    private readonly Stopwatch _sw = new();
    private readonly IPauseToken? _pauseToken;

    public PauseAwareStopwatch(IPauseToken? pauseToken = null)
    {
        _pauseToken = pauseToken;
        if (pauseToken is CooperativePauseController controller)
        {
            controller.PauseStateChanged += OnPauseStateChanged;
        }
    }

    public TimeSpan Elapsed => _sw.Elapsed;

    public void Start()
    {
        if (_pauseToken?.IsPaused != true)
        {
            _sw.Start();
        }
    }

    public void Stop() => _sw.Stop();

    private void OnPauseStateChanged(bool paused)
    {
        if (paused)
        {
            _sw.Stop();
        }
        else
        {
            _sw.Start();
        }
    }
}
