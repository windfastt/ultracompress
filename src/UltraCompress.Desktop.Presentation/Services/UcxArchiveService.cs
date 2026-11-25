using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.Primitives;

namespace UltraCompress.Desktop.Presentation.Services;

public interface IDesktopArchiveService
{
    Task<long> CreateUcxArchiveAsync(
        IReadOnlyList<StagedFileItem> items,
        string outputPath,
        DesktopLosslessProfile profile,
        Func<long, long, Task>? onProgressAsync = null,
        CancellationToken cancellationToken = default);

    Task<(ArchiveManifestSummary Summary, IReadOnlyList<ArchiveEntryNode> Entries)> InspectArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken = default);

    Task<ArchiveVerificationReport> VerifyArchiveAsync(
        string archivePath,
        IReadOnlyList<ArchiveEntryNode>? liveNodes = null,
        CancellationToken cancellationToken = default);

    Task<SelectiveExtractionResult> ExtractEntriesAsync(
        string archivePath,
        IReadOnlySet<string>? selectedEntryPaths,
        string destinationFolder,
        DestinationConflictMode conflictMode,
        CancellationToken cancellationToken = default);
}

public sealed class UcxArchiveService : IDesktopArchiveService
{
    private static readonly byte[] MagicBytes = "UCX1"u8.ToArray();
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly DestinationManagerService _destinationManager;

    public UcxArchiveService(DestinationManagerService? destinationManager = null)
    {
        _destinationManager = destinationManager ?? new DestinationManagerService();
    }

    public async Task<long> CreateUcxArchiveAsync(
        IReadOnlyList<StagedFileItem> items,
        string outputPath,
        DesktopLosslessProfile profile,
        Func<long, long, Task>? onProgressAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output archive path is required.", nameof(outputPath));
        }

        var fullOutput = Path.GetFullPath(outputPath);
        var outDir = Path.GetDirectoryName(fullOutput);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        var totalOriginalBytes = items.Sum(i => i.SizeBytes);
        long processedOriginalBytes = 0;

        await using var payloadBuffer = new MemoryStream();
        var storedBlocksByHash = new Dictionary<string, (long Offset, long Length, string CodecId, string StrategyLabel)>(
            StringComparer.OrdinalIgnoreCase);
        var manifestEntries = new List<UcxManifestEntry>(items.Count);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryPath = NormalizeArchiveEntryPath(item.RelativePath);
            if (!File.Exists(item.FullPath))
            {
                throw new FileNotFoundException($"Staged input file not found: '{item.FullPath}'.", item.FullPath);
            }

            var rawBytes = await File.ReadAllBytesAsync(item.FullPath, cancellationToken).ConfigureAwait(false);
            var actualSha256 = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();

            if (rawBytes.Length > 0 && storedBlocksByHash.TryGetValue(actualSha256, out var existingBlock))
            {
                manifestEntries.Add(new UcxManifestEntry
                {
                    EntryPath = entryPath,
                    OriginalSize = rawBytes.Length,
                    CompressedSize = 0,
                    BlockOffset = existingBlock.Offset,
                    BlockLength = existingBlock.Length,
                    CodecId = existingBlock.CodecId,
                    StrategyLabel = "SHA-256 Dedup Reference",
                    Sha256 = actualSha256,
                    IsDuplicateRef = true,
                });
            }
            else
            {
                var (encodedBytes, codecId, strategyLabel) = EncodeBlockLosslessly(rawBytes, item.Category, item.ShannonEntropyBitsPerByte, profile);
                var offset = payloadBuffer.Position;
                await payloadBuffer.WriteAsync(encodedBytes, cancellationToken).ConfigureAwait(false);
                var blockLen = (long)encodedBytes.Length;

                if (rawBytes.Length > 0)
                {
                    storedBlocksByHash[actualSha256] = (offset, blockLen, codecId, strategyLabel);
                }

                manifestEntries.Add(new UcxManifestEntry
                {
                    EntryPath = entryPath,
                    OriginalSize = rawBytes.Length,
                    CompressedSize = blockLen,
                    BlockOffset = offset,
                    BlockLength = blockLen,
                    CodecId = codecId,
                    StrategyLabel = strategyLabel,
                    Sha256 = actualSha256,
                    IsDuplicateRef = false,
                });
            }

            processedOriginalBytes += rawBytes.Length;
            if (onProgressAsync is not null)
            {
                await onProgressAsync(processedOriginalBytes, Math.Max(1, totalOriginalBytes)).ConfigureAwait(false);
            }
        }

        var manifest = new UcxManifestDocument
        {
            FormatVersion = 1,
            Profile = profile.ToString(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Entries = manifestEntries,
        };

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);

        await using var outStream = new FileStream(
            fullOutput,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65536,
            useAsync: true);

        await outStream.WriteAsync(MagicBytes, cancellationToken).ConfigureAwait(false);
        var lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, manifestBytes.Length);
        await outStream.WriteAsync(lenBuf, cancellationToken).ConfigureAwait(false);
        await outStream.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);

        payloadBuffer.Position = 0;
        await payloadBuffer.CopyToAsync(outStream, 65536, cancellationToken).ConfigureAwait(false);
        await outStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        return outStream.Length;
    }

    public async Task<(ArchiveManifestSummary Summary, IReadOnlyList<ArchiveEntryNode> Entries)> InspectArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new ArgumentException("Archive path is required.", nameof(archivePath));
        }

        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Archive file not found.", fullPath);
        }

        if (await IsUcxContainerAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            return await InspectUcxArchiveAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return InspectZipArchive(fullPath);
        }

        throw new InvalidDataException($"Unsupported or corrupted archive header in '{fullPath}'.");
    }

    public async Task<ArchiveVerificationReport> VerifyArchiveAsync(
        string archivePath,
        IReadOnlyList<ArchiveEntryNode>? liveNodes = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Archive file not found.", fullPath);
        }

        var nodeLookup = liveNodes?
            .Where(n => !n.IsDirectory)
            .ToDictionary(n => n.EntryPath, StringComparer.Ordinal);

        if (await IsUcxContainerAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            var (manifest, payloadStartOffset) = await ReadUcxManifestAsync(stream, cancellationToken).ConfigureAwait(false);

            var verifiedCount = 0;
            var failedCount = 0;
            var corrupted = new List<string>();

            foreach (var entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArchiveEntryNode? liveNode = null;
                nodeLookup?.TryGetValue(entry.EntryPath, out liveNode);

                var absoluteOffset = payloadStartOffset + entry.BlockOffset;
                if (absoluteOffset < payloadStartOffset || absoluteOffset + entry.BlockLength > stream.Length)
                {
                    failedCount++;
                    corrupted.Add(entry.EntryPath);
                    if (liveNode is not null)
                    {
                        liveNode.VerificationStatus = Sha256VerificationStatus.MissingPayload;
                    }

                    continue;
                }

                try
                {
                    var blockBytes = new byte[entry.BlockLength];
                    stream.Position = absoluteOffset;
                    await ReadExactlyAsync(stream, blockBytes, cancellationToken).ConfigureAwait(false);

                    var decoded = DecodeBlockLosslessly(blockBytes, entry.CodecId, (int)entry.OriginalSize);
                    var actualSha = Convert.ToHexString(SHA256.HashData(decoded)).ToLowerInvariant();
                    if (decoded.LongLength == entry.OriginalSize
                        && string.Equals(actualSha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        verifiedCount++;
                        if (liveNode is not null)
                        {
                            liveNode.VerificationStatus = entry.IsDuplicateRef
                                ? Sha256VerificationStatus.DedupReferenceVerified
                                : Sha256VerificationStatus.VerifiedMatch;
                        }
                    }
                    else
                    {
                        failedCount++;
                        corrupted.Add(entry.EntryPath);
                        if (liveNode is not null)
                        {
                            liveNode.VerificationStatus = Sha256VerificationStatus.MismatchFailed;
                        }
                    }
                }
                catch
                {
                    failedCount++;
                    corrupted.Add(entry.EntryPath);
                    if (liveNode is not null)
                    {
                        liveNode.VerificationStatus = Sha256VerificationStatus.MismatchFailed;
                    }
                }
            }

            var allValid = failedCount == 0;
            return new ArchiveVerificationReport
            {
                IsAllValid = allValid,
                TotalEntriesChecked = manifest.Entries.Count,
                VerifiedCount = verifiedCount,
                FailedCount = failedCount,
                CorruptedEntries = corrupted,
                SummaryMessage = allValid
                    ? $"Verified 100% bit-exact SHA-256 integrity across {verifiedCount} entry/entries."
                    : $"Integrity check failed on {failedCount} of {manifest.Entries.Count} entry/entries.",
            };
        }

        if (string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return await VerifyZipArchiveAsync(fullPath, nodeLookup, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidDataException($"Cannot verify unrecognized archive format: '{fullPath}'.");
    }

    public async Task<SelectiveExtractionResult> ExtractEntriesAsync(
        string archivePath,
        IReadOnlySet<string>? selectedEntryPaths,
        string destinationFolder,
        DestinationConflictMode conflictMode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return new SelectiveExtractionResult
            {
                Success = false,
                DestinationFolder = string.Empty,
                ConflictModeApplied = conflictMode,
                ErrorCard = ActionableErrorPresenter.FromCode(ActionableErrorCode.AccessDeniedPermission, destinationFolder),
                SummaryMessage = "Destination folder is required for extraction.",
            };
        }

        var fullArchivePath = Path.GetFullPath(archivePath);
        var fullDestinationRoot = Path.GetFullPath(destinationFolder);
        Directory.CreateDirectory(fullDestinationRoot);

        if (await IsUcxContainerAsync(fullArchivePath, cancellationToken).ConfigureAwait(false))
        {
            return await ExtractUcxEntriesAsync(
                fullArchivePath,
                selectedEntryPaths,
                fullDestinationRoot,
                conflictMode,
                cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(Path.GetExtension(fullArchivePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return await ExtractZipEntriesAsync(
                fullArchivePath,
                selectedEntryPaths,
                fullDestinationRoot,
                conflictMode,
                cancellationToken).ConfigureAwait(false);
        }

        return new SelectiveExtractionResult
        {
            Success = false,
            DestinationFolder = fullDestinationRoot,
            ConflictModeApplied = conflictMode,
            ErrorCard = ActionableErrorPresenter.FromCode(ActionableErrorCode.ArchiveHeaderCorrupted, fullArchivePath),
            SummaryMessage = "Unsupported or corrupted archive header.",
        };
    }

    private async Task<SelectiveExtractionResult> ExtractUcxEntriesAsync(
        string fullArchivePath,
        IReadOnlySet<string>? selectedEntryPaths,
        string fullDestinationRoot,
        DestinationConflictMode conflictMode,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(fullArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var (manifest, payloadStartOffset) = await ReadUcxManifestAsync(stream, cancellationToken).ConfigureAwait(false);

        var targetEntries = selectedEntryPaths is null
            ? manifest.Entries
            : manifest.Entries.Where(e => selectedEntryPaths.Contains(e.EntryPath)).ToList();

        var extractedPaths = new List<string>();
        var skippedCount = 0;
        var verifiedCount = 0;
        long totalExtractedBytes = 0;

        foreach (var entry in targetEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveSafeExtractionPath(fullDestinationRoot, entry.EntryPath, out var safeTargetPath))
            {
                return new SelectiveExtractionResult
                {
                    Success = false,
                    DestinationFolder = fullDestinationRoot,
                    ConflictModeApplied = conflictMode,
                    ErrorCard = ActionableErrorPresenter.FromCode(ActionableErrorCode.PathTraversalBlocked, entry.EntryPath),
                    SummaryMessage = $"Blocked unsafe archive entry path '{entry.EntryPath}'.",
                };
            }

            var targetDir = Path.GetDirectoryName(safeTargetPath)!;
            Directory.CreateDirectory(targetDir);
            var desiredFileName = Path.GetFileName(safeTargetPath);
            var resolvedFilePath = ResolveExtractionFileConflict(targetDir, desiredFileName, conflictMode, out var skipFile);
            if (skipFile)
            {
                skippedCount++;
                continue;
            }

            var absoluteOffset = payloadStartOffset + entry.BlockOffset;
            if (absoluteOffset < payloadStartOffset || absoluteOffset + entry.BlockLength > stream.Length)
            {
                return new SelectiveExtractionResult
                {
                    Success = false,
                    DestinationFolder = fullDestinationRoot,
                    ConflictModeApplied = conflictMode,
                    ErrorCard = ActionableErrorPresenter.FromCode(ActionableErrorCode.ArchiveHeaderCorrupted, fullArchivePath),
                    SummaryMessage = $"Truncated archive block for '{entry.EntryPath}'.",
                };
            }

            var blockBytes = new byte[entry.BlockLength];
            stream.Position = absoluteOffset;
            await ReadExactlyAsync(stream, blockBytes, cancellationToken).ConfigureAwait(false);

            var decoded = DecodeBlockLosslessly(blockBytes, entry.CodecId, (int)entry.OriginalSize);
            var actualSha = Convert.ToHexString(SHA256.HashData(decoded)).ToLowerInvariant();
            if (!string.Equals(actualSha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new SelectiveExtractionResult
                {
                    Success = false,
                    DestinationFolder = fullDestinationRoot,
                    ConflictModeApplied = conflictMode,
                    ErrorCard = ActionableErrorPresenter.FromCode(ActionableErrorCode.Sha256IntegrityMismatch, entry.EntryPath),
                    SummaryMessage = $"SHA-256 verification failed while extracting '{entry.EntryPath}'.",
                };
            }

            await File.WriteAllBytesAsync(resolvedFilePath, decoded, cancellationToken).ConfigureAwait(false);
            extractedPaths.Add(resolvedFilePath);
            verifiedCount++;
            totalExtractedBytes += decoded.LongLength;
        }

        return new SelectiveExtractionResult
        {
            Success = true,
            ExtractedFileCount = extractedPaths.Count,
            SkippedFileCount = skippedCount,
            VerifiedFileCount = verifiedCount,
            TotalExtractedBytes = totalExtractedBytes,
            DestinationFolder = fullDestinationRoot,
            ConflictModeApplied = conflictMode,
            ExtractedPaths = extractedPaths,
            SummaryMessage =
                $"Extracted {extractedPaths.Count} file(s) ({ByteSizeFormatter.FormatBytes(totalExtractedBytes)}), {verifiedCount} SHA-256 verified, {skippedCount} skipped.",
        };
    }

    private static async Task<SelectiveExtractionResult> ExtractZipEntriesAsync(
        string fullArchivePath,
        IReadOnlySet<string>? selectedEntryPaths,
        string fullDestinationRoot,
        DestinationConflictMode conflictMode,
        CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(fullArchivePath);
        var extractedPaths = new List<string>();
        var skippedCount = 0;
        long totalBytes = 0;

        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (selectedEntryPaths is not null && !selectedEntryPaths.Contains(normalized))
            {
                continue;
            }

            if (!TryResolveSafeExtractionPath(fullDestinationRoot, entry.FullName, out var safeTargetPath))
            {
                return new SelectiveExtractionResult
                {
                    Success = false,
                    DestinationFolder = fullDestinationRoot,
                    ConflictModeApplied = conflictMode,
                    ErrorCard = ActionableErrorPresenter.FromCode(ActionableErrorCode.PathTraversalBlocked, entry.FullName),
                    SummaryMessage = $"Blocked unsafe ZIP entry path '{entry.FullName}'.",
                };
            }

            var targetDir = Path.GetDirectoryName(safeTargetPath)!;
            Directory.CreateDirectory(targetDir);
            var resolvedPath = ResolveExtractionFileConflict(targetDir, Path.GetFileName(safeTargetPath), conflictMode, out var skipFile);
            if (skipFile)
            {
                skippedCount++;
                continue;
            }

            await using var entryStream = entry.Open();
            await using var outFile = new FileStream(resolvedPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await entryStream.CopyToAsync(outFile, 65536, cancellationToken).ConfigureAwait(false);
            extractedPaths.Add(resolvedPath);
            totalBytes += entry.Length;
        }

        return new SelectiveExtractionResult
        {
            Success = true,
            ExtractedFileCount = extractedPaths.Count,
            SkippedFileCount = skippedCount,
            VerifiedFileCount = extractedPaths.Count,
            TotalExtractedBytes = totalBytes,
            DestinationFolder = fullDestinationRoot,
            ConflictModeApplied = conflictMode,
            ExtractedPaths = extractedPaths,
            SummaryMessage = $"Extracted {extractedPaths.Count} ZIP entry/entries ({ByteSizeFormatter.FormatBytes(totalBytes)}), {skippedCount} skipped.",
        };
    }

    private static async Task<(ArchiveManifestSummary Summary, IReadOnlyList<ArchiveEntryNode> Entries)> InspectUcxArchiveAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        var archiveFileInfo = new FileInfo(fullPath);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, useAsync: true);
        var (manifest, _) = await ReadUcxManifestAsync(stream, cancellationToken).ConfigureAwait(false);

        var nodes = BuildHierarchicalEntryList(
            manifest.Entries.Select(e => new RawEntryDescriptor(
                e.EntryPath,
                e.OriginalSize,
                e.CompressedSize,
                e.StrategyLabel,
                e.Sha256,
                e.IsDuplicateRef)));

        var summary = new ArchiveManifestSummary
        {
            ArchivePath = fullPath,
            ArchiveFileName = archiveFileInfo.Name,
            ContainerFormat = ".ucx v1",
            TotalFileEntries = manifest.Entries.Count,
            TotalDirectoryEntries = nodes.Count(n => n.IsDirectory),
            TotalOriginalBytes = manifest.Entries.Sum(e => e.OriginalSize),
            TotalArchiveBytes = archiveFileInfo.Length,
            DuplicateEntriesCollapsed = manifest.Entries.Count(e => e.IsDuplicateRef),
        };

        return (summary, nodes);
    }

    private static (ArchiveManifestSummary Summary, IReadOnlyList<ArchiveEntryNode> Entries) InspectZipArchive(string fullPath)
    {
        var info = new FileInfo(fullPath);
        using var zip = ZipFile.OpenRead(fullPath);
        var fileEntries = zip.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .Select(e => new RawEntryDescriptor(
                e.FullName.Replace('\\', '/').TrimStart('/'),
                e.Length,
                e.CompressedLength,
                e.CompressedLength >= e.Length && e.Length > 0 ? "ZIP Store" : "ZIP Deflate",
                $"{e.Crc32:x8}",
                false))
            .ToList();

        var nodes = BuildHierarchicalEntryList(fileEntries);
        var summary = new ArchiveManifestSummary
        {
            ArchivePath = fullPath,
            ArchiveFileName = info.Name,
            ContainerFormat = ".zip",
            TotalFileEntries = fileEntries.Count,
            TotalDirectoryEntries = nodes.Count(n => n.IsDirectory),
            TotalOriginalBytes = fileEntries.Sum(e => e.OriginalSize),
            TotalArchiveBytes = info.Length,
            DuplicateEntriesCollapsed = 0,
        };

        return (summary, nodes);
    }

    private static async Task<ArchiveVerificationReport> VerifyZipArchiveAsync(
        string fullPath,
        Dictionary<string, ArchiveEntryNode>? nodeLookup,
        CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(fullPath);
        var verified = 0;
        var failed = 0;
        var corrupted = new List<string>();
        var buffer = ArrayPool<byte>.Shared.Rent(65536);

        try
        {
            foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
                ArchiveEntryNode? node = null;
                nodeLookup?.TryGetValue(normalized, out node);

                try
                {
                    await using var s = entry.Open();
                    long total = 0;
                    int read;
                    while ((read = await s.ReadAsync(buffer.AsMemory(0, 65536), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                    }

                    if (total == entry.Length)
                    {
                        verified++;
                        if (node is not null)
                        {
                            node.VerificationStatus = Sha256VerificationStatus.VerifiedMatch;
                        }
                    }
                    else
                    {
                        failed++;
                        corrupted.Add(normalized);
                        if (node is not null)
                        {
                            node.VerificationStatus = Sha256VerificationStatus.MismatchFailed;
                        }
                    }
                }
                catch
                {
                    failed++;
                    corrupted.Add(normalized);
                    if (node is not null)
                    {
                        node.VerificationStatus = Sha256VerificationStatus.MismatchFailed;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new ArchiveVerificationReport
        {
            IsAllValid = failed == 0,
            TotalEntriesChecked = verified + failed,
            VerifiedCount = verified,
            FailedCount = failed,
            CorruptedEntries = corrupted,
            SummaryMessage = failed == 0
                ? $"Verified {verified} ZIP entry/entries successfully."
                : $"ZIP integrity verification failed on {failed} entry/entries.",
        };
    }

    private static IReadOnlyList<ArchiveEntryNode> BuildHierarchicalEntryList(IEnumerable<RawEntryDescriptor> rawFiles)
    {
        var directories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = rawFiles.OrderBy(f => f.EntryPath, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var file in files)
        {
            var segments = file.EntryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var folderPath = string.Join('/', segments.Take(i + 1));
                directories.Add(folderPath);
            }
        }

        var combined = new List<(string SortKey, ArchiveEntryNode Node)>();
        foreach (var dir in directories)
        {
            var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parent = parts.Length > 1 ? string.Join('/', parts.Take(parts.Length - 1)) : string.Empty;
            combined.Add((
                dir + "/",
                new ArchiveEntryNode
                {
                    EntryPath = dir,
                    Name = parts[^1],
                    ParentFolder = parent,
                    IsDirectory = true,
                    Depth = parts.Length - 1,
                    OriginalSizeBytes = 0,
                    CompressedSizeBytes = 0,
                    StrategyCodec = "Folder",
                }));
        }

        foreach (var file in files)
        {
            var parts = file.EntryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var name = parts.Length > 0 ? parts[^1] : file.EntryPath;
            var parent = parts.Length > 1 ? string.Join('/', parts.Take(parts.Length - 1)) : string.Empty;
            combined.Add((
                file.EntryPath,
                new ArchiveEntryNode
                {
                    EntryPath = file.EntryPath,
                    Name = name,
                    ParentFolder = parent,
                    IsDirectory = false,
                    Depth = Math.Max(0, parts.Length - 1),
                    OriginalSizeBytes = file.OriginalSize,
                    CompressedSizeBytes = file.CompressedSize,
                    StrategyCodec = file.StrategyLabel,
                    ExpectedSha256 = file.Sha256,
                    IsDuplicateReference = file.IsDuplicateRef,
                }));
        }

        return combined
            .OrderBy(c => c.SortKey, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Node)
            .ToList();
    }

    private static (byte[] Encoded, string CodecId, string StrategyLabel) EncodeBlockLosslessly(
        byte[] rawBytes,
        FileContentCategory category,
        double entropy,
        DesktopLosslessProfile profile)
    {
        if (rawBytes.Length == 0)
        {
            return (Array.Empty<byte>(), "store", "Store (Empty)");
        }

        var profileSpec = LosslessProfileCatalog.Get(profile);
        if (category is FileContentCategory.AlreadyCompressedMedia or FileContentCategory.CompressedArchive
            || entropy >= profileSpec.IncompressibleEntropyGate)
        {
            return (rawBytes, "store", "Store / FastStreamBypass");
        }

        var brotliQuality = profile switch
        {
            DesktopLosslessProfile.Instant => 1,
            DesktopLosslessProfile.Balanced => 4,
            DesktopLosslessProfile.Archive => 7,
            DesktopLosslessProfile.Tiny => 9,
            DesktopLosslessProfile.Laboratory => 11,
            _ => 4,
        };

        var useByteShuffle = category == FileContentCategory.DatabaseAndTabular && rawBytes.Length >= 16 && rawBytes.Length % 4 == 0;
        var inputToCodec = useByteShuffle ? ApplyByteShuffle4(rawBytes) : rawBytes;

        using var compressedStream = new MemoryStream();
        using (var brotli = new BrotliStream(compressedStream, GetCompressionLevel(brotliQuality), leaveOpen: true))
        {
            brotli.Write(inputToCodec, 0, inputToCodec.Length);
        }

        var compressed = compressedStream.ToArray();
        if (compressed.Length >= rawBytes.Length)
        {
            return (rawBytes, "store", "Store (Incompressible Guard)");
        }

        return useByteShuffle
            ? (compressed, "shuffle4+brotli", $"ByteShuffle + Brotli-{brotliQuality}")
            : (compressed, "brotli", $"Solid Brotli-{brotliQuality} ({profileSpec.DisplayName})");
    }

    private static byte[] DecodeBlockLosslessly(byte[] encodedBytes, string codecId, int expectedOriginalLength)
    {
        if (expectedOriginalLength == 0 && encodedBytes.Length == 0)
        {
            return Array.Empty<byte>();
        }

        return codecId switch
        {
            "store" => encodedBytes,
            "brotli" => DecompressBrotli(encodedBytes),
            "shuffle4+brotli" => InverseByteShuffle4(DecompressBrotli(encodedBytes)),
            _ => throw new InvalidDataException($"Unsupported codec '{codecId}' in archive entry."),
        };
    }

    private static byte[] DecompressBrotli(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] ApplyByteShuffle4(byte[] data)
    {
        var result = new byte[data.Length];
        var groups = data.Length / 4;
        for (var i = 0; i < groups; i++)
        {
            result[i] = data[(i * 4)];
            result[groups + i] = data[(i * 4) + 1];
            result[(groups * 2) + i] = data[(i * 4) + 2];
            result[(groups * 3) + i] = data[(i * 4) + 3];
        }

        return result;
    }

    private static byte[] InverseByteShuffle4(byte[] shuffled)
    {
        if (shuffled.Length % 4 != 0)
        {
            return shuffled;
        }

        var result = new byte[shuffled.Length];
        var groups = shuffled.Length / 4;
        for (var i = 0; i < groups; i++)
        {
            result[i * 4] = shuffled[i];
            result[(i * 4) + 1] = shuffled[groups + i];
            result[(i * 4) + 2] = shuffled[(groups * 2) + i];
            result[(i * 4) + 3] = shuffled[(groups * 3) + i];
        }

        return result;
    }

    private static CompressionLevel GetCompressionLevel(int quality) => quality switch
    {
        <= 2 => CompressionLevel.Fastest,
        <= 6 => CompressionLevel.Optimal,
        _ => CompressionLevel.SmallestSize,
    };

    public static bool TryResolveSafeExtractionPath(string destinationRoot, string entryPath, out string safeFullPath)
    {
        safeFullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return false;
        }

        if (entryPath.StartsWith('/') || entryPath.StartsWith('\\') || entryPath.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var normalizedRelative = entryPath.Replace('\\', '/');
        var segments = normalizedRelative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
        {
            return false;
        }

        var rootWithSep = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var candidate = Path.GetFullPath(Path.Combine(rootWithSep, Path.Combine(segments)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootWithSep, comparison))
        {
            return false;
        }

        safeFullPath = candidate;
        return true;
    }

    private static string ResolveExtractionFileConflict(
        string directory,
        string fileName,
        DestinationConflictMode conflictMode,
        out bool skipFile)
    {
        skipFile = false;
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        switch (conflictMode)
        {
            case DestinationConflictMode.Overwrite:
                return candidate;
            case DestinationConflictMode.Skip:
                skipFile = true;
                return candidate;
            default:
            {
                var stem = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                for (var i = 1; i < 10_000; i++)
                {
                    var next = Path.Combine(directory, $"{stem} ({i}){ext}");
                    if (!File.Exists(next))
                    {
                        return next;
                    }
                }

                return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{ext}");
            }
        }
    }

    private static async Task<bool> IsUcxContainerAsync(string fullPath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64, useAsync: true);
        if (stream.Length < 8)
        {
            return false;
        }

        var header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        return header.AsSpan().SequenceEqual(MagicBytes);
    }

    private static async Task<(UcxManifestDocument Manifest, long PayloadStartOffset)> ReadUcxManifestAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        if (stream.Length < 10)
        {
            throw new InvalidDataException("UCX archive file is truncated.");
        }

        stream.Position = 0;
        var header = new byte[8];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(MagicBytes))
        {
            throw new InvalidDataException("Invalid UCX magic bytes.");
        }

        var manifestLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (manifestLength <= 0 || manifestLength > 64 * 1024 * 1024 || 8L + manifestLength > stream.Length)
        {
            throw new InvalidDataException("Corrupted UCX manifest length.");
        }

        var manifestBytes = new byte[manifestLength];
        await ReadExactlyAsync(stream, manifestBytes, cancellationToken).ConfigureAwait(false);

        var manifest = JsonSerializer.Deserialize<UcxManifestDocument>(manifestBytes, ManifestJsonOptions)
                       ?? throw new InvalidDataException("Failed to deserialize UCX manifest.");
        return (manifest, 8L + manifestLength);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("Unexpected end of archive stream.");
            }

            offset += read;
        }
    }

    private static string NormalizeArchiveEntryPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(normalized) ? "entry.bin" : normalized;
    }

    private sealed record RawEntryDescriptor(
        string EntryPath,
        long OriginalSize,
        long CompressedSize,
        string StrategyLabel,
        string Sha256,
        bool IsDuplicateRef);

    private sealed class UcxManifestDocument
    {
        public int FormatVersion { get; init; } = 1;
        public string Profile { get; init; } = "Balanced";
        public DateTimeOffset CreatedAtUtc { get; init; }
        public List<UcxManifestEntry> Entries { get; init; } = [];
    }

    private sealed class UcxManifestEntry
    {
        public required string EntryPath { get; init; }
        public required long OriginalSize { get; init; }
        public required long CompressedSize { get; init; }
        public required long BlockOffset { get; init; }
        public required long BlockLength { get; init; }
        public required string CodecId { get; init; }
        public required string StrategyLabel { get; init; }
        public required string Sha256 { get; init; }
        public bool IsDuplicateRef { get; init; }
    }
}
