using System.Diagnostics;
using System.IO.Compression;
using UltraCompress.Core.Errors;
using UltraCompress.Core.SevenZip;
using ZstdNet;

namespace UltraCompress.Core.Archive;

public sealed record EncodedBlockResult(
    byte[] Payload,
    string Codec,
    int CodecLevel,
    string Rationale);

/// <summary>
/// Lossless sub-block codec engine supporting <c>store</c>, <c>zstd</c>, <c>brotli</c>,
/// <c>deflate</c>, and <c>7z</c> (LZMA2 via <c>7za</c>), plus optional custom <see cref="ICompressionBackend"/> overrides.
/// </summary>
public static class UcxBlockCodecEngine
{
    public static async Task<EncodedBlockResult> CompressBlockAsync(
        ReadOnlyMemory<byte> rawBlock,
        UcxContentClass contentClass,
        int entryCount,
        string contentSignalSummary,
        CompressionProfile profile,
        string? preferredCodecOverride = null,
        IReadOnlyDictionary<string, ICompressionBackend>? customBackends = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (rawBlock.Length == 0 || contentClass == UcxContentClass.EmptyOrDirectory)
        {
            return new EncodedBlockResult(
                Array.Empty<byte>(),
                "store",
                0,
                "Empty payload block; zero bytes stored.");
        }

        if (!string.IsNullOrWhiteSpace(preferredCodecOverride))
        {
            var normalizedOverride = NormalizeCodecName(preferredCodecOverride);
            var level = GetDefaultLevelForCodec(normalizedOverride, profile);
            var encoded = await EncodeWithCodecAsync(normalizedOverride, level, rawBlock, customBackends, requireSevenZip: true, cancellationToken)
                .ConfigureAwait(false);
            return new EncodedBlockResult(
                encoded,
                normalizedOverride,
                level,
                $"Explicit codec override '{normalizedOverride}' (level {level}) on {contentClass} ({entryCount} file(s)).");
        }

        if (contentClass == UcxContentClass.IncompressibleMedia)
        {
            return new EncodedBlockResult(
                rawBlock.ToArray(),
                "store",
                0,
                $"Bypassed heavy compression via Store block ({contentSignalSummary}); preserves disk-speed I/O and O(1) random slice extraction.");
        }

        // Tournament / multi-candidate evaluation on Tiny and Laboratory profiles.
        if (profile is CompressionProfile.Tiny or CompressionProfile.Laboratory)
        {
            return await SelectBestCandidateAsync(rawBlock, contentClass, entryCount, profile, customBackends, cancellationToken)
                .ConfigureAwait(false);
        }

        // Fast deterministic content-class routing for Instant, Balanced, and Archive profiles.
        var (primaryCodec, primaryLevel, reasonPrefix) = (contentClass, profile) switch
        {
            (_, CompressionProfile.Instant) =>
                ("zstd", 1, "Instant profile fast-path Zstandard level 1"),
            (UcxContentClass.SourceAndText, CompressionProfile.Archive) =>
                ("brotli", 8, "Solid SourceAndText block routed to Brotli static UTF-8 dictionary"),
            (UcxContentClass.SourceAndText, _) =>
                ("brotli", 5, "Solid SourceAndText block routed to fast Brotli UTF-8 dictionary"),
            (UcxContentClass.StructuredData, CompressionProfile.Archive) =>
                ("zstd", 11, "Solid StructuredData block routed to medium-window Zstandard"),
            (UcxContentClass.StructuredData, _) =>
                ("zstd", 5, "Solid StructuredData block routed to fast Zstandard"),
            (UcxContentClass.ExecutableAndBinary, CompressionProfile.Archive) =>
                ("zstd", 9, "Solid ExecutableAndBinary block routed to Zstandard"),
            _ =>
                ("deflate", 6, "Solid ExecutableAndBinary block routed to fast DEFLATE"),
        };

        var compressed = await EncodeWithCodecAsync(primaryCodec, primaryLevel, rawBlock, customBackends, requireSevenZip: false, cancellationToken)
            .ConfigureAwait(false);

        if (compressed.Length >= rawBlock.Length)
        {
            return new EncodedBlockResult(
                rawBlock.ToArray(),
                "store",
                0,
                $"Incompressible block guard: {primaryCodec}-{primaryLevel} produced {compressed.Length} B >= raw {rawBlock.Length} B; stored uncompressed.");
        }

        double savedPct = (1.0 - (double)compressed.Length / rawBlock.Length) * 100.0;
        return new EncodedBlockResult(
            compressed,
            primaryCodec,
            primaryLevel,
            $"{reasonPrefix} ({entryCount} file(s), {UltraCompressErrors.FormatBytes(rawBlock.Length)} -> {UltraCompressErrors.FormatBytes(compressed.Length)}, {savedPct:F1}% saved).");
    }

    public static async Task<byte[]> DecompressBlockAsync(
        ReadOnlyMemory<byte> compressedBlock,
        string codec,
        int codecLevel,
        long expectedUncompressedLength,
        string? archivePath = null,
        IReadOnlyDictionary<string, ICompressionBackend>? customBackends = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (expectedUncompressedLength < 0 || expectedUncompressedLength > Array.MaxLength)
        {
            throw UltraCompressErrors.CorruptArchive(
                archivePath,
                $"Invalid block uncompressed size {expectedUncompressedLength}.");
        }

        if (expectedUncompressedLength == 0)
        {
            return Array.Empty<byte>();
        }

        var normalized = NormalizeCodecName(codec);
        if (normalized == "store")
        {
            if (compressedBlock.Length != expectedUncompressedLength)
            {
                throw UltraCompressErrors.CorruptArchive(
                    archivePath,
                    $"Store block length mismatch (expected {expectedUncompressedLength}, found {compressedBlock.Length}).");
            }

            return compressedBlock.ToArray();
        }

        if (TryResolveCustomBackend(normalized, customBackends, out var backend))
        {
            var step = new CompressStep { Codec = backend.CodecName, Level = codecLevel };
            var decoded = await backend.DecompressAsync(compressedBlock, step, cancellationToken).ConfigureAwait(false);
            if (decoded.Length != expectedUncompressedLength)
            {
                throw UltraCompressErrors.CorruptArchive(
                    archivePath,
                    $"Decompressed {normalized} block size {decoded.Length} did not match manifest size {expectedUncompressedLength}.");
            }

            return decoded;
        }

        try
        {
            return normalized switch
            {
                "zstd" => DecompressZstd(compressedBlock.Span, (int)expectedUncompressedLength),
                "brotli" => DecompressBrotli(compressedBlock.Span, (int)expectedUncompressedLength),
                "deflate" => DecompressDeflate(compressedBlock.Span, (int)expectedUncompressedLength),
                "7z" => await DecompressSevenZipAsync(compressedBlock, (int)expectedUncompressedLength, archivePath, cancellationToken).ConfigureAwait(false),
                _ => throw UltraCompressErrors.UnsupportedCodecOrVersion(
                    archivePath,
                    $"Archive sub-block uses unsupported codec '{codec}'."),
            };
        }
        catch (UltraCompressException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw UltraCompressErrors.CorruptArchive(
                archivePath,
                $"Failed to decompress '{normalized}' payload block: {ex.Message}",
                ex);
        }
    }

    private static async Task<EncodedBlockResult> SelectBestCandidateAsync(
        ReadOnlyMemory<byte> rawBlock,
        UcxContentClass contentClass,
        int entryCount,
        CompressionProfile profile,
        IReadOnlyDictionary<string, ICompressionBackend>? customBackends,
        CancellationToken cancellationToken)
    {
        int zstdLevel = profile == CompressionProfile.Tiny ? 16 : 19;
        int brotliQuality = profile == CompressionProfile.Tiny ? 10 : 11;
        int deflateLevel = 9;

        var candidates = new List<(string Codec, int Level)>
        {
            ("zstd", zstdLevel),
            ("brotli", brotliQuality),
            ("deflate", deflateLevel),
        };

        if (profile == CompressionProfile.Laboratory
            && (TryResolveCustomBackend("7z", customBackends, out _) || SevenZipToolLocator.TryResolve() is not null))
        {
            candidates.Add(("7z", 9));
        }

        string bestCodec = "store";
        int bestLevel = 0;
        byte[] bestBytes = rawBlock.ToArray();
        var summaryParts = new List<string>(candidates.Count);

        foreach (var (codec, level) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] candidateBytes;
            try
            {
                candidateBytes = await EncodeWithCodecAsync(codec, level, rawBlock, customBackends, requireSevenZip: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            summaryParts.Add($"{codec}-{level}={candidateBytes.Length}B");
            if (candidateBytes.Length < bestBytes.Length)
            {
                bestBytes = candidateBytes;
                bestCodec = codec;
                bestLevel = level;
            }
        }

        if (bestCodec == "store")
        {
            return new EncodedBlockResult(
                bestBytes,
                "store",
                0,
                $"Profile {profile} tournament ({string.Join(", ", summaryParts)}): no codec beat raw {rawBlock.Length}B; stored uncompressed.");
        }

        double savedPct = (1.0 - (double)bestBytes.Length / rawBlock.Length) * 100.0;
        return new EncodedBlockResult(
            bestBytes,
            bestCodec,
            bestLevel,
            $"Profile {profile} tournament on {contentClass} ({entryCount} file(s)): {bestCodec}-{bestLevel} won ({savedPct:F1}% saved; trials: {string.Join(", ", summaryParts)}).");
    }

    private static async Task<byte[]> EncodeWithCodecAsync(
        string normalizedCodec,
        int level,
        ReadOnlyMemory<byte> rawBlock,
        IReadOnlyDictionary<string, ICompressionBackend>? customBackends,
        bool requireSevenZip,
        CancellationToken cancellationToken)
    {
        if (normalizedCodec == "store")
        {
            return rawBlock.ToArray();
        }

        if (TryResolveCustomBackend(normalizedCodec, customBackends, out var backend))
        {
            var step = new CompressStep { Codec = backend.CodecName, Level = level };
            return await backend.CompressAsync(rawBlock, step, cancellationToken).ConfigureAwait(false);
        }

        return normalizedCodec switch
        {
            "zstd" => CompressZstd(rawBlock.Span, level),
            "brotli" => CompressBrotli(rawBlock.Span, level),
            "deflate" => CompressDeflate(rawBlock.Span, level),
            "7z" => await CompressSevenZipAsync(rawBlock, level, requireSevenZip, cancellationToken).ConfigureAwait(false),
            _ => throw UltraCompressErrors.UnsupportedCodecOrVersion(
                null,
                $"Codec '{normalizedCodec}' is not supported. Supported codecs: store, zstd, brotli, deflate, 7z."),
        };
    }

    private static bool TryResolveCustomBackend(
        string normalizedCodec,
        IReadOnlyDictionary<string, ICompressionBackend>? customBackends,
        out ICompressionBackend backend)
    {
        backend = null!;
        if (customBackends is null)
        {
            return false;
        }

        if (customBackends.TryGetValue(normalizedCodec, out var direct) && direct is not null)
        {
            backend = direct;
            return true;
        }

        if (normalizedCodec == "deflate" && customBackends.TryGetValue("libdeflate", out var libdeflate) && libdeflate is not null)
        {
            backend = libdeflate;
            return true;
        }

        return false;
    }

    public static string NormalizeCodecName(string codec) =>
        codec.Trim().ToLowerInvariant() switch
        {
            "none" or "raw" or "store" => "store",
            "zstandard" or "zst" or "zstd" => "zstd",
            "br" or "brotli" => "brotli",
            "libdeflate" or "flate" or "deflate" => "deflate",
            "lzma" or "lzma2" or "7za" or "7z" => "7z",
            var other => other,
        };

    private static int GetDefaultLevelForCodec(string codec, CompressionProfile profile) =>
        (codec, profile) switch
        {
            ("store", _) => 0,
            ("zstd", CompressionProfile.Instant) => 1,
            ("zstd", CompressionProfile.Balanced) => 4,
            ("zstd", CompressionProfile.Archive) => 9,
            ("zstd", CompressionProfile.Tiny) => 16,
            ("zstd", _) => 19,
            ("brotli", CompressionProfile.Instant) => 1,
            ("brotli", CompressionProfile.Balanced) => 5,
            ("brotli", CompressionProfile.Archive) => 8,
            ("brotli", _) => 11,
            ("deflate", CompressionProfile.Instant) => 1,
            ("deflate", CompressionProfile.Balanced) => 5,
            ("deflate", CompressionProfile.Archive) => 7,
            ("deflate", _) => 9,
            ("7z", CompressionProfile.Instant) => 1,
            ("7z", CompressionProfile.Balanced) => 5,
            ("7z", CompressionProfile.Archive) => 7,
            ("7z", _) => 9,
            _ => 3,
        };

    internal static byte[] CompressZstd(ReadOnlySpan<byte> input, int level)
    {
        ZstdNativeInitializer.EnsureInitialized();
        using var compressor = new Compressor(new CompressionOptions(Math.Clamp(level, 1, 22)));
        return compressor.Wrap(input).ToArray();
    }

    internal static byte[] DecompressZstd(ReadOnlySpan<byte> compressed, int expectedUncompressedLength)
    {
        ZstdNativeInitializer.EnsureInitialized();
        using var decompressor = new Decompressor();
        var output = decompressor.Unwrap(compressed, expectedUncompressedLength).ToArray();
        if (output.Length != expectedUncompressedLength)
        {
            throw new InvalidDataException($"Zstd output length {output.Length} != expected {expectedUncompressedLength}.");
        }

        return output;
    }

    private static byte[] CompressBrotli(ReadOnlySpan<byte> input, int level)
    {
        var quality = Math.Clamp(level, 0, 11);
        var maxLen = BrotliEncoder.GetMaxCompressedLength(input.Length);
        var destination = new byte[maxLen];
        if (!BrotliEncoder.TryCompress(input, destination, out var written, quality, window: 22))
        {
            throw new InvalidOperationException("Brotli block compression failed.");
        }

        return destination[..written];
    }

    private static byte[] DecompressBrotli(ReadOnlySpan<byte> compressed, int expectedUncompressedLength)
    {
        var output = new byte[expectedUncompressedLength];
        if (!BrotliDecoder.TryDecompress(compressed, output, out var written) || written != expectedUncompressedLength)
        {
            throw new InvalidDataException($"Brotli decompression failed (written={written}, expected={expectedUncompressedLength}).");
        }

        return output;
    }

    private static byte[] CompressDeflate(ReadOnlySpan<byte> input, int level)
    {
        var compressionLevel = level switch
        {
            <= 3 => CompressionLevel.Fastest,
            <= 8 => CompressionLevel.Optimal,
            _ => CompressionLevel.SmallestSize,
        };

        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, compressionLevel, leaveOpen: true))
        {
            deflate.Write(input);
        }

        return ms.ToArray();
    }

    private static byte[] DecompressDeflate(ReadOnlySpan<byte> compressed, int expectedUncompressedLength)
    {
        using var input = new MemoryStream(compressed.ToArray(), writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        var output = new byte[expectedUncompressedLength];
        int offset = 0;
        while (offset < expectedUncompressedLength)
        {
            int read = deflate.Read(output, offset, expectedUncompressedLength - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (offset != expectedUncompressedLength || deflate.ReadByte() != -1)
        {
            throw new InvalidDataException($"DEFLATE stream length mismatch (decoded={offset}, expected={expectedUncompressedLength}).");
        }

        return output;
    }

    private static async Task<byte[]> CompressSevenZipAsync(
        ReadOnlyMemory<byte> rawBlock,
        int level,
        bool requireSevenZip,
        CancellationToken cancellationToken)
    {
        var exe = SevenZipToolLocator.TryResolve();
        if (exe is null)
        {
            if (requireSevenZip)
            {
                throw UltraCompressErrors.UnsupportedCodecOrVersion(
                    null,
                    "7-Zip standalone CLI (7za) was not found for '7z' block compression.");
            }

            return CompressZstd(rawBlock.Span, 19);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "ultracompress-ucx7z", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var entryPath = Path.Combine(tempDir, "block.bin");
            var archivePath = Path.Combine(tempDir, "block.7z");
            await File.WriteAllBytesAsync(entryPath, rawBlock.ToArray(), cancellationToken).ConfigureAwait(false);

            var step = new CompressStep { Codec = "7z", Level = level, Method = "LZMA2", Solid = false };
            var args = SevenZipCommandLine.BuildAddArgs(archivePath, [entryPath], step);
            await RunSevenZipProcessAsync(exe, args, expectStdout: false, cancellationToken).ConfigureAwait(false);

            return await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task<byte[]> DecompressSevenZipAsync(
        ReadOnlyMemory<byte> compressedBlock,
        int expectedUncompressedLength,
        string? archivePath,
        CancellationToken cancellationToken)
    {
        var exe = SevenZipToolLocator.TryResolve()
            ?? throw UltraCompressErrors.UnsupportedCodecOrVersion(
                archivePath,
                "Archive contains a '7z' (LZMA2) sub-block, but 7za/7z CLI was not found on PATH or ULTRACOMPRESS_7ZA_PATH.");

        var tempDir = Path.Combine(Path.GetTempPath(), "ultracompress-ucx7z", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var temp7z = Path.Combine(tempDir, "block.7z");
            await File.WriteAllBytesAsync(temp7z, compressedBlock.ToArray(), cancellationToken).ConfigureAwait(false);
            var args = SevenZipCommandLine.BuildExtractStdoutArgs(temp7z, "block.bin");
            var decoded = await RunSevenZipProcessAsync(exe, args, expectStdout: true, cancellationToken).ConfigureAwait(false);
            if (decoded.Length != expectedUncompressedLength)
            {
                throw new InvalidDataException($"7z block decoded {decoded.Length} bytes, expected {expectedUncompressedLength}.");
            }

            return decoded;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task<byte[]> RunSevenZipProcessAsync(
        string executablePath,
        IReadOnlyList<string> args,
        bool expectStdout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{executablePath}'.");
        }

        var stdoutTask = expectStdout
            ? ReadAllBytesAsync(process.StandardOutput.BaseStream, cancellationToken)
            : Task.FromResult(Array.Empty<byte>());
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await using var reg = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        });

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"7za exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        return await stdoutTask.ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
