using System.Buffers;
using System.Security.Cryptography;
using UltraCompress.Core.Intelligence;

namespace UltraCompress.Core;

/// <summary>
/// Static file probe combining multi-window O(1) <see cref="ContentInspector"/> analysis
/// with optional full-stream SHA-256 hashing and blocked entropy sampling.
/// </summary>
public static class FileProbe
{
    public static async Task<FileFingerprint> ProbeAsync(
        string path,
        FileProbeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new FileProbeOptions();
        if (options.BlockSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BlockSizeBytes must be positive.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Input file not found.", fullPath);
        }

        var fileInfo = new FileInfo(fullPath);
        var extension = Path.GetExtension(fullPath);

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        var inspection = await ContentInspector.InspectAsync(
            stream,
            fullPath,
            new ContentInspectorOptions { WindowSizeBytes = Math.Max(512, options.BlockSizeBytes) },
            cancellationToken);

        string? contentHash = null;
        if (options.ComputeContentHash)
        {
            if (inspection.IsFullContentHash && fileInfo.Length > 0)
            {
                contentHash = inspection.SampleFingerprintSha256;
            }
            else
            {
                stream.Position = 0;
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        hasher.AppendData(buffer, 0, read);
                    }

                    contentHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        stream.Position = 0;
        var (globalEntropy, blockSamples) = await ComputeEntropyAsync(
            stream,
            fileInfo.Length,
            options,
            inspection,
            cancellationToken);

        return new FileFingerprint
        {
            Path = fullPath,
            SizeBytes = fileInfo.Length,
            LastModifiedUtc = fileInfo.LastWriteTimeUtc,
            Extension = string.IsNullOrEmpty(extension) ? null : extension,
            ContentHashSha256 = contentHash,
            DetectedType = inspection.Signature.MimeType,
            EntropyBitsPerByte = globalEntropy,
            BlockEntropies = blockSamples,
            Inspection = inspection,
        };
    }

    private static async Task<(double? Global, IReadOnlyList<BlockEntropySample> Blocks)> ComputeEntropyAsync(
        Stream stream,
        long fileLength,
        FileProbeOptions options,
        ContentInspectionResult inspection,
        CancellationToken cancellationToken)
    {
        if (fileLength == 0)
        {
            return (0, Array.Empty<BlockEntropySample>());
        }

        if (fileLength > options.FullBlockScanMaxBytes && options.MaxBlockSamples == 5 && inspection.Windows.Count > 0)
        {
            var winBlocks = inspection.Windows
                .Select(w => new BlockEntropySample
                {
                    Offset = w.Offset,
                    Length = w.Length,
                    EntropyBitsPerByte = w.EntropyBitsPerByte,
                })
                .ToList();
            return (inspection.Signals.MeanEntropyBitsPerByte, winBlocks);
        }

        if (fileLength <= options.FullBlockScanMaxBytes)
        {
            var allBytes = new byte[fileLength];
            var offset = 0;
            while (offset < allBytes.Length)
            {
                var read = await stream.ReadAsync(allBytes.AsMemory(offset), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            return (
                EntropyCalculator.ShannonBitsPerByte(allBytes, 0, offset),
                EntropyCalculator.BlockedEntropy(allBytes, 0, offset, options.BlockSizeBytes));
        }

        var blockSamples = new List<BlockEntropySample>();
        var sampleCount = Math.Min(options.MaxBlockSamples, (int)Math.Ceiling((double)fileLength / options.BlockSizeBytes));
        if (sampleCount <= 0)
        {
            sampleCount = 1;
        }

        var stride = fileLength / sampleCount;
        if (stride < options.BlockSizeBytes)
        {
            stride = options.BlockSizeBytes;
        }

        var blockBuffer = new byte[options.BlockSizeBytes];
        long totalSampled = 0;
        double weightedEntropy = 0;

        for (var i = 0; i < sampleCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = i * stride;
            if (offset >= fileLength)
            {
                break;
            }

            stream.Position = offset;
            var read = await stream.ReadAsync(blockBuffer.AsMemory(0, blockBuffer.Length), cancellationToken);
            if (read == 0)
            {
                continue;
            }

            var entropy = EntropyCalculator.ShannonBitsPerByte(blockBuffer, 0, read);
            blockSamples.Add(new BlockEntropySample
            {
                Offset = offset,
                Length = read,
                EntropyBitsPerByte = entropy,
            });

            weightedEntropy += entropy * read;
            totalSampled += read;
        }

        var global = totalSampled > 0 ? weightedEntropy / totalSampled : (double?)null;
        return (global, blockSamples);
    }
}
