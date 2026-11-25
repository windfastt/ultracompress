using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using UltraCompress.Core;
using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.Primitives;

namespace UltraCompress.Desktop.Presentation.Services;

public sealed class BatchContentAnalyzer
{
    private const int SampleWindowBytes = 32 * 1024;

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".webm", ".avi", ".mp3", ".aac", ".ogg", ".flac", ".m4a",
        ".jpg", ".jpeg", ".heic", ".heif", ".webp", ".avif", ".gif", ".png",
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".ucx", ".cab", ".nupkg",
    };

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".xaml", ".ts", ".tsx", ".js", ".jsx", ".py", ".rs", ".go",
        ".c", ".cpp", ".h", ".hpp", ".java", ".kt", ".swift", ".md", ".txt", ".json", ".yaml",
        ".yml", ".toml", ".xml", ".html", ".css", ".scss", ".sh", ".ps1", ".proto",
    };

    private static readonly HashSet<string> DatabaseAndTabularExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sql", ".sqlite", ".sqlite3", ".db", ".csv", ".tsv", ".ndjson", ".jsonl", ".parquet", ".bak",
    };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".sys", ".pdb", ".wasm",
    };

    private static readonly HashSet<string> UncompressedRasterExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".tiff", ".tif", ".raw", ".dng", ".cr2", ".nef", ".arw", ".ppm", ".tga", ".wav",
    };

    public async Task<(IReadOnlyList<StagedFileItem> Items, StrategyJustificationReport Report)> AnalyzePathsAsync(
        IEnumerable<string> rawPaths,
        DesktopLosslessProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawPaths);

        var discoveredFiles = new List<(string FullPath, string RelativePath)>();
        var seenFullPaths = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var foldersScanned = 0;

        foreach (var raw in rawPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var full = Path.GetFullPath(raw.Trim());
            if (Directory.Exists(full))
            {
                foldersScanned++;
                var rootFolderName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(rootFolderName))
                {
                    rootFolderName = "root";
                }

                foreach (var childFile in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!seenFullPaths.Add(childFile))
                    {
                        continue;
                    }

                    var subRel = Path.GetRelativePath(full, childFile).Replace('\\', '/');
                    var normalizedRel = $"{rootFolderName}/{subRel}";
                    discoveredFiles.Add((childFile, normalizedRel));
                }
            }
            else if (File.Exists(full))
            {
                if (seenFullPaths.Add(full))
                {
                    discoveredFiles.Add((full, Path.GetFileName(full)));
                }
            }
        }

        if (discoveredFiles.Count == 0)
        {
            return (Array.Empty<StagedFileItem>(), StrategyJustificationReport.Empty);
        }

        var profileSpec = LosslessProfileCatalog.Get(profile);
        var firstSeenBySha256 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stagedItems = new List<StagedFileItem>(discoveredFiles.Count);

        foreach (var (fullPath, relativePath) in discoveredFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await InspectSingleFileAsync(
                fullPath,
                relativePath,
                profileSpec,
                firstSeenBySha256,
                cancellationToken).ConfigureAwait(false);
            stagedItems.Add(item);
        }

        var report = BuildReport(stagedItems, foldersScanned, profileSpec);
        return (stagedItems, report);
    }

    public (IReadOnlyList<StagedFileItem> Items, StrategyJustificationReport Report) RecalculateForProfile(
        IReadOnlyList<StagedFileItem> existingItems,
        int foldersScanned,
        DesktopLosslessProfile newProfile)
    {
        if (existingItems.Count == 0)
        {
            return (Array.Empty<StagedFileItem>(), StrategyJustificationReport.Empty);
        }

        var profileSpec = LosslessProfileCatalog.Get(newProfile);
        var updated = new List<StagedFileItem>(existingItems.Count);
        foreach (var item in existingItems)
        {
            var (strategy, justification, estimatedBytes) = RouteStrategy(
                item.Category,
                item.SizeBytes,
                item.ShannonEntropyBitsPerByte,
                item.StrideCorrelationScore,
                item.IsDuplicate,
                item.DuplicateOfRelativePath,
                profileSpec);

            updated.Add(item.WithUpdatedRouting(strategy, justification, estimatedBytes));
        }

        return (updated, BuildReport(updated, foldersScanned, profileSpec));
    }

    private static async Task<StagedFileItem> InspectSingleFileAsync(
        string fullPath,
        string relativePath,
        LosslessProfileOption profileSpec,
        Dictionary<string, string> firstSeenBySha256,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(fullPath);
        var sizeBytes = info.Length;
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 65536,
            useAsync: true);

        var sha256 = await ComputeSha256HexAsync(stream, cancellationToken).ConfigureAwait(false);
        var (entropy, strideCorrelation, headerBytes) = await SampleSignalWindowsAsync(stream, sizeBytes, cancellationToken).ConfigureAwait(false);

        var sniffedMime = ContentTypeSniffer.Sniff(headerBytes.AsSpan(), extension);
        var category = ClassifyFile(extension, sniffedMime, headerBytes, entropy, strideCorrelation);
        var categoryDisplay = FormatCategoryName(category);

        string? duplicateOf = null;
        var isDuplicate = false;
        if (sizeBytes > 0)
        {
            if (firstSeenBySha256.TryGetValue(sha256, out var canonicalRel))
            {
                isDuplicate = true;
                duplicateOf = canonicalRel;
            }
            else
            {
                firstSeenBySha256[sha256] = relativePath;
            }
        }

        var (strategy, justification, estimatedBytes) = RouteStrategy(
            category,
            sizeBytes,
            entropy,
            strideCorrelation,
            isDuplicate,
            duplicateOf,
            profileSpec);

        return new StagedFileItem
        {
            FullPath = fullPath,
            RelativePath = relativePath,
            FileName = Path.GetFileName(fullPath),
            Extension = extension,
            SizeBytes = sizeBytes,
            ContentHashSha256 = sha256,
            ShannonEntropyBitsPerByte = entropy,
            StrideCorrelationScore = strideCorrelation,
            Category = category,
            CategoryDisplayName = categoryDisplay,
            SelectedStrategy = strategy,
            StrategyJustification = justification,
            IsDuplicate = isDuplicate,
            DuplicateOfRelativePath = duplicateOf,
            EstimatedCompressedBytes = estimatedBytes,
        };
    }

    private static async Task<string> ComputeSha256HexAsync(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, 65536), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<(double Entropy, double StrideCorrelation, byte[] Header)> SampleSignalWindowsAsync(
        Stream stream,
        long fileLength,
        CancellationToken cancellationToken)
    {
        if (fileLength == 0)
        {
            return (0.0, 0.0, Array.Empty<byte>());
        }

        double[] fractions = fileLength <= SampleWindowBytes * 2
            ? [0.0]
            : [0.0, 0.25, 0.50, 0.75, 0.95];

        var windowBuffer = new byte[Math.Min(fileLength, SampleWindowBytes)];
        byte[]? header = null;
        double weightedEntropy = 0;
        double weightedStride = 0;
        long totalBytesSampled = 0;

        foreach (var fraction in fractions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = (long)((fileLength - windowBuffer.Length) * fraction);
            offset = Math.Clamp(offset, 0, Math.Max(0, fileLength - 1));
            stream.Position = offset;

            var toRead = (int)Math.Min(windowBuffer.Length, fileLength - offset);
            var totalRead = 0;
            while (totalRead < toRead)
            {
                var read = await stream.ReadAsync(windowBuffer.AsMemory(totalRead, toRead - totalRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead == 0)
            {
                continue;
            }

            header ??= windowBuffer.AsSpan(0, Math.Min(totalRead, 512)).ToArray();

            var (windowEntropy, strideScore) = ComputeWindowSignals(windowBuffer, totalRead);

            weightedEntropy += windowEntropy * totalRead;
            weightedStride += strideScore * totalRead;
            totalBytesSampled += totalRead;
        }

        if (totalBytesSampled == 0)
        {
            return (0.0, 0.0, header ?? Array.Empty<byte>());
        }

        return (
            weightedEntropy / totalBytesSampled,
            weightedStride / totalBytesSampled,
            header ?? Array.Empty<byte>());
    }

    private static (double Entropy, double StrideScore) ComputeWindowSignals(byte[] windowBuffer, int length)
    {
        var slice = windowBuffer.AsSpan(0, length);
        return (
            EntropyCalculator.ShannonBitsPerByte(slice),
            ComputeByteStrideCorrelation(slice));
    }

    private static double ComputeByteStrideCorrelation(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
        {
            return 0.0;
        }

        var matches4 = 0;
        var matches8 = 0;
        var limit = data.Length;
        for (var i = 8; i < limit; i++)
        {
            if (data[i] == data[i - 4])
            {
                matches4++;
            }

            if (data[i] == data[i - 8])
            {
                matches8++;
            }
        }

        var count = (double)(limit - 8);
        return Math.Max(matches4 / count, matches8 / count);
    }

    private static FileContentCategory ClassifyFile(
        string extension,
        string sniffedMime,
        byte[] header,
        double entropy,
        double strideCorrelation)
    {
        if (IsMp4OrMkvContainer(header) || MediaExtensions.Contains(extension) || sniffedMime.StartsWith("image/jpeg", StringComparison.Ordinal))
        {
            if (UncompressedRasterExtensions.Contains(extension))
            {
                return FileContentCategory.UncompressedRasterImage;
            }

            return FileContentCategory.AlreadyCompressedMedia;
        }

        if (UncompressedRasterExtensions.Contains(extension) || IsBmpOrTiff(header))
        {
            return FileContentCategory.UncompressedRasterImage;
        }

        if (ArchiveExtensions.Contains(extension)
            || sniffedMime is "application/zip" or "application/gzip" or "application/zstd" or "application/x-xz" or "application/x-7z-compressed")
        {
            return FileContentCategory.CompressedArchive;
        }

        if (sniffedMime is "application/x-sqlite3" || DatabaseAndTabularExtensions.Contains(extension))
        {
            return FileContentCategory.DatabaseAndTabular;
        }

        if (sniffedMime is "application/x-msdownload" or "application/x-elf" || ExecutableExtensions.Contains(extension))
        {
            return FileContentCategory.NativeExecutable;
        }

        if (sniffedMime is "application/pdf" || string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return FileContentCategory.DocumentPdf;
        }

        if (SourceExtensions.Contains(extension) || sniffedMime.StartsWith("text/", StringComparison.Ordinal) || sniffedMime is "application/json" or "application/xml")
        {
            return FileContentCategory.SourceAndMarkup;
        }

        if (entropy >= 7.85)
        {
            return FileContentCategory.CompressedArchive;
        }

        if (strideCorrelation >= 0.22)
        {
            return FileContentCategory.DatabaseAndTabular;
        }

        return FileContentCategory.GenericBinary;
    }

    private static bool IsMp4OrMkvContainer(byte[] header)
    {
        if (header.Length >= 8 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p')
        {
            return true;
        }

        return header.Length >= 4
               && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3;
    }

    private static bool IsBmpOrTiff(byte[] header)
    {
        if (header.Length >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M')
        {
            return true;
        }

        return header.Length >= 4
               && ((header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00)
                   || (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A));
    }

    private static (string Strategy, string Justification, long EstimatedBytes) RouteStrategy(
        FileContentCategory category,
        long sizeBytes,
        double entropy,
        double strideCorrelation,
        bool isDuplicate,
        string? duplicateOfPath,
        LosslessProfileOption profileSpec)
    {
        if (sizeBytes == 0)
        {
            return ("Manifest Empty Entry", "0-byte file recorded in manifest without block allocation.", 0);
        }

        if (isDuplicate)
        {
            return (
                "SHA-256 Dedup Reference (0 B payload)",
                $"Exact SHA-256 match with '{duplicateOfPath}'; payload stored once and referenced in manifest (100% duplicate savings).",
                32);
        }

        if (category is FileContentCategory.AlreadyCompressedMedia or FileContentCategory.CompressedArchive
            || entropy >= profileSpec.IncompressibleEntropyGate)
        {
            var reason = category == FileContentCategory.AlreadyCompressedMedia
                ? string.Create(CultureInfo.InvariantCulture, $"Media container with {entropy:F2} bpb entropy; heavy LZMA2/Zstd rejected (<0.3% gain) in favor of disk-speed Store + SHA-256.")
                : string.Create(CultureInfo.InvariantCulture, $"High Shannon entropy ({entropy:F2} bpb ≥ {profileSpec.IncompressibleEntropyGate:F2} gate); bypassed heavy codecs to prevent CPU waste and block expansion.");
            return ("Store / FastStreamBypass", reason, sizeBytes + 24);
        }

        var primaryCodecLabel = profileSpec.Profile switch
        {
            DesktopLosslessProfile.Instant => "Zstd-1",
            DesktopLosslessProfile.Balanced => "Zstd-4",
            DesktopLosslessProfile.Archive => "Zstd-12 / Brotli-7",
            DesktopLosslessProfile.Tiny => "Zstd-19 / LZMA2-7",
            DesktopLosslessProfile.Laboratory => "LZMA2-9 / Zstd-22 Tournament",
            _ => "Zstd-4",
        };

        var theoreticalRatio = Math.Clamp(entropy / 8.0, 0.08, 0.98);
        var effortBonus = Math.Clamp(1.0 / profileSpec.CompressionEffortFactor, 0.72, 1.25);

        switch (category)
        {
            case FileContentCategory.SourceAndMarkup:
            {
                var ratio = Math.Clamp(theoreticalRatio * 0.55 * effortBonus, 0.06, 0.75);
                return (
                    $"Solid Block + {primaryCodecLabel}",
                    string.Create(CultureInfo.InvariantCulture, $"Low entropy ({entropy:F2} bpb) & repeated tokens; grouped into type-sorted solid dictionary block instead of isolated per-file deflate."),
                    Math.Max(32, (long)(sizeBytes * ratio)));
            }
            case FileContentCategory.DatabaseAndTabular:
            {
                var ratio = Math.Clamp(theoreticalRatio * 0.50 * effortBonus, 0.05, 0.70);
                return (
                    $"ByteShuffle + {primaryCodecLabel}",
                    string.Create(CultureInfo.InvariantCulture, $"4/8-byte stride correlation ({strideCorrelation:P0}) at {entropy:F2} bpb; reversible ByteShuffle aligns column/page bytes before {primaryCodecLabel}."),
                    Math.Max(32, (long)(sizeBytes * ratio)));
            }
            case FileContentCategory.NativeExecutable:
            {
                var ratio = Math.Clamp(theoreticalRatio * 0.62 * effortBonus, 0.12, 0.78);
                return (
                    $"x86 BCJ Filter + {primaryCodecLabel}",
                    string.Create(CultureInfo.InvariantCulture, $"Executable code stream ({entropy:F2} bpb); reversible x86 CALL/JMP relative-address filter improves {primaryCodecLabel} match lengths."),
                    Math.Max(32, (long)(sizeBytes * ratio)));
            }
            case FileContentCategory.UncompressedRasterImage:
            {
                var ratio = Math.Clamp(theoreticalRatio * 0.60 * effortBonus, 0.10, 0.80);
                return (
                    $"Delta Channel Filter + {primaryCodecLabel}",
                    string.Create(CultureInfo.InvariantCulture, $"Uncompressed raster/PCM stream ({entropy:F2} bpb); reversible byte-delta transform captures adjacent pixel correlation without lossy re-encoding."),
                    Math.Max(32, (long)(sizeBytes * ratio)));
            }
            case FileContentCategory.DocumentPdf:
            {
                var ratio = entropy >= 7.4
                    ? 0.94
                    : Math.Clamp(theoreticalRatio * 0.70 * effortBonus, 0.18, 0.90);
                return (
                    $"Stream-Aware {primaryCodecLabel}",
                    string.Create(CultureInfo.InvariantCulture, $"PDF container ({entropy:F2} bpb); losslessly compresses cross-reference/text streams while preserving embedded media bit-for-bit."),
                    Math.Max(32, (long)(sizeBytes * ratio)));
            }
            default:
            {
                var ratio = Math.Clamp(theoreticalRatio * 0.75 * effortBonus, 0.10, 0.95);
                return (
                    primaryCodecLabel,
                    string.Create(CultureInfo.InvariantCulture, $"Structured binary ({entropy:F2} bpb); routed to {primaryCodecLabel} under {profileSpec.DisplayName} SLA."),
                    Math.Max(32, (long)(sizeBytes * ratio)));
            }
        }
    }

    private static StrategyJustificationReport BuildReport(
        IReadOnlyList<StagedFileItem> items,
        int foldersScanned,
        LosslessProfileOption profileSpec)
    {
        var totalOriginal = items.Sum(i => i.SizeBytes);
        var manifestOverhead = 256L + (items.Count * 96L);
        var estimatedArchive = items.Sum(i => i.EstimatedCompressedBytes) + manifestOverhead;
        if (totalOriginal > 0 && estimatedArchive > totalOriginal && items.All(i => !i.IsDuplicate && i.Category == FileContentCategory.AlreadyCompressedMedia))
        {
            estimatedArchive = totalOriginal + manifestOverhead;
        }

        var duplicateItems = items.Where(i => i.IsDuplicate).ToList();
        var duplicateSaved = duplicateItems.Sum(i => i.SizeBytes);

        var bypassedItems = items.Where(i => !i.IsDuplicate && i.SelectedStrategy.StartsWith("Store", StringComparison.Ordinal)).ToList();
        var weightedEntropy = totalOriginal > 0
            ? items.Sum(i => i.ShannonEntropyBitsPerByte * Math.Max(1, i.SizeBytes)) / Math.Max(1, totalOriginal)
            : items.Average(i => i.ShannonEntropyBitsPerByte);

        var categories = items
            .GroupBy(i => i.Category)
            .Select(g =>
            {
                var catBytes = g.Sum(x => x.SizeBytes);
                var avgEnt = catBytes > 0
                    ? g.Sum(x => x.ShannonEntropyBitsPerByte * Math.Max(1, x.SizeBytes)) / catBytes
                    : g.Average(x => x.ShannonEntropyBitsPerByte);
                var dominantStrategy = g
                    .Where(x => !x.IsDuplicate)
                    .GroupBy(x => x.SelectedStrategy)
                    .OrderByDescending(sg => sg.Sum(x => x.SizeBytes))
                    .Select(sg => sg.Key)
                    .FirstOrDefault() ?? "SHA-256 Dedup Reference";
                var sampleReason = g.FirstOrDefault(x => !x.IsDuplicate)?.StrategyJustification
                                   ?? g.First().StrategyJustification;

                return new CategoryBreakdownItem
                {
                    Category = g.Key,
                    CategoryDisplayName = FormatCategoryName(g.Key),
                    FileCount = g.Count(),
                    TotalBytes = catBytes,
                    AverageEntropyBitsPerByte = avgEnt,
                    ChosenStrategy = dominantStrategy,
                    WhySelected = sampleReason,
                };
            })
            .OrderByDescending(c => c.TotalBytes)
            .ToList();

        var headline = string.Create(
            CultureInfo.InvariantCulture,
            $"Analyzed {items.Count} file(s) ({ByteSizeFormatter.FormatBytes(totalOriginal)}, avg {weightedEntropy:F2} bpb) under {profileSpec.DisplayName} ({profileSpec.SlaBadge}).");

        var rejectedSummary = bypassedItems.Count > 0
            ? $"Rejected uniform heavy LZMA2/Zstd on {bypassedItems.Count} high-entropy file(s) ({ByteSizeFormatter.FormatBytes(bypassedItems.Sum(x => x.SizeBytes))}) to avoid wasted CPU; routed compressible streams separately."
            : $"Rejected blind single-codec pass; grouped {categories.Count} content class(es) with per-stream preprocessors and SHA-256 content addressing.";

        return new StrategyJustificationReport
        {
            TotalFiles = items.Count,
            TotalFoldersScanned = foldersScanned,
            TotalOriginalBytes = totalOriginal,
            EstimatedArchiveBytes = Math.Min(Math.Max(64, estimatedArchive), Math.Max(64, totalOriginal + manifestOverhead)),
            DuplicateFilesCount = duplicateItems.Count,
            DuplicateReclaimableBytes = duplicateSaved,
            BypassedMediaCount = bypassedItems.Count,
            BypassedMediaBytes = bypassedItems.Sum(x => x.SizeBytes),
            AverageEntropyBitsPerByte = weightedEntropy,
            HeadlineSummary = headline,
            RejectedAlternativesSummary = rejectedSummary,
            Categories = categories,
        };
    }

    public static string FormatCategoryName(FileContentCategory category) => category switch
    {
        FileContentCategory.AlreadyCompressedMedia => "Compressed Media (Video/Photo/Audio)",
        FileContentCategory.CompressedArchive => "Pre-Compressed Archive / High-Entropy",
        FileContentCategory.SourceAndMarkup => "Source Code, Config & Text",
        FileContentCategory.DatabaseAndTabular => "Database & Structured Tabular Data",
        FileContentCategory.NativeExecutable => "Native Executables & Libraries",
        FileContentCategory.UncompressedRasterImage => "Uncompressed Raster / PCM Media",
        FileContentCategory.DocumentPdf => "PDF & Office Containers",
        _ => "General Binary Data",
    };
}
