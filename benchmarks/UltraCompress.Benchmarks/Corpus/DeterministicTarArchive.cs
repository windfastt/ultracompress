using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UltraCompress.Benchmarks.Corpus;

/// <summary>
/// Produces and extracts canonical, byte-identical POSIX USTAR (.tar) archives
/// with zeroed timestamps/UID/GID and sorted relative paths, plus directory SHA-256 manifest verification.
/// </summary>
public static class DeterministicTarArchive
{
    private const int BlockSize = 512;

    public static void CreateFromDirectory(string sourceDirectory, string destinationTarPath)
    {
        var root = Path.GetFullPath(sourceDirectory);
        var files = EnumerateSortedRelativeFiles(root);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationTarPath))!);
        using var outStream = new FileStream(destinationTarPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);

        Span<byte> header = stackalloc byte[BlockSize];
        Span<byte> zeroPad = stackalloc byte[BlockSize];
        zeroPad.Clear();

        byte[] copyBuffer = new byte[65536];

        foreach (var (relPath, fullPath) in files)
        {
            var info = new FileInfo(fullPath);
            long size = info.Length;

            WriteUstarHeader(header, relPath, size);
            outStream.Write(header);

            using (var inStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            {
                long remaining = size;
                while (remaining > 0)
                {
                    int read = inStream.Read(copyBuffer, 0, (int)Math.Min(copyBuffer.Length, remaining));
                    if (read <= 0)
                    {
                        throw new EndOfStreamException($"Unexpected EOF while archiving '{fullPath}'.");
                    }

                    outStream.Write(copyBuffer, 0, read);
                    remaining -= read;
                }
            }

            int remainder = (int)(size % BlockSize);
            if (remainder != 0)
            {
                outStream.Write(zeroPad[..(BlockSize - remainder)]);
            }
        }

        // Two 512-byte zero blocks mark end-of-archive.
        outStream.Write(zeroPad);
        outStream.Write(zeroPad);
    }

    public static void ExtractToDirectory(string tarFilePath, string destinationDirectory)
    {
        var destRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destRoot);

        using var inStream = new FileStream(tarFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        byte[] header = new byte[BlockSize];
        byte[] copyBuffer = new byte[65536];

        while (true)
        {
            int headerRead = ReadExactOrEof(inStream, header);
            if (headerRead == 0 || IsAllZero(header))
            {
                break;
            }

            if (headerRead < BlockSize)
            {
                throw new InvalidDataException("Truncated TAR header.");
            }

            string name = ReadNullTerminatedAscii(header.AsSpan(0, 100));
            string prefix = ReadNullTerminatedAscii(header.AsSpan(345, 155));
            string relPath = string.IsNullOrEmpty(prefix) ? name : $"{prefix}/{name}";

            byte typeFlag = header[156];
            long size = ParseOctal(header.AsSpan(124, 12));

            if (typeFlag is (byte)'5')
            {
                continue;
            }

            if (typeFlag is not ((byte)'0' or 0))
            {
                SkipAligned(inStream, size);
                continue;
            }

            string safeDest = ResolveSafePath(destRoot, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(safeDest)!);

            using (var outFile = new FileStream(safeDest, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                long remaining = size;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(copyBuffer.Length, remaining);
                    int read = inStream.Read(copyBuffer, 0, toRead);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException($"Unexpected EOF extracting '{relPath}'.");
                    }

                    outFile.Write(copyBuffer, 0, read);
                    remaining -= read;
                }
            }

            int remainder = (int)(size % BlockSize);
            if (remainder != 0)
            {
                SkipExact(inStream, BlockSize - remainder);
            }
        }
    }

    public static (string ManifestSha256, long TotalBytes, int FileCount) ComputeDirectoryManifest(string directoryPath)
    {
        var root = Path.GetFullPath(directoryPath);
        var files = EnumerateSortedRelativeFiles(root);
        using var manifestHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        long totalBytes = 0;
        foreach (var (relPath, fullPath) in files)
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            string fileHash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            long length = fs.Length;
            totalBytes += length;

            string line = $"{relPath}\t{length}\t{fileHash}\n";
            manifestHasher.AppendData(Encoding.UTF8.GetBytes(line));
        }

        string manifestHash = Convert.ToHexString(manifestHasher.GetHashAndReset()).ToLowerInvariant();
        return (manifestHash, totalBytes, files.Count);
    }

    public static List<(string RelativePath, string FullPath)> EnumerateSortedRelativeFiles(string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        var list = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(fullPath =>
            {
                var rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
                return (RelativePath: rel, FullPath: fullPath);
            })
            .OrderBy(x => x.RelativePath, StringComparer.Ordinal)
            .ToList();
        return list;
    }

    public static string ResolveSafePath(string rootFullPath, string relativePath)
    {
        var normalizedRel = relativePath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalizedRel) || normalizedRel.Split('/').Contains(".."))
        {
            throw new InvalidDataException($"Unsafe archive entry path: '{relativePath}'.");
        }

        var combined = Path.GetFullPath(Path.Combine(rootFullPath, normalizedRel.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSep = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootFullPath
            : rootFullPath + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Archive entry escaped destination root: '{relativePath}'.");
        }

        return combined;
    }

    private static void WriteUstarHeader(Span<byte> header, string relativePath, long size)
    {
        header.Clear();
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');

        string name = normalized;
        string prefix = string.Empty;
        if (Encoding.ASCII.GetByteCount(normalized) > 100)
        {
            int splitIdx = normalized.LastIndexOf('/');
            if (splitIdx <= 0)
            {
                throw new InvalidOperationException($"Entry path exceeds USTAR name limit: '{normalized}'.");
            }

            prefix = normalized[..splitIdx];
            name = normalized[(splitIdx + 1)..];
            if (Encoding.ASCII.GetByteCount(prefix) > 155 || Encoding.ASCII.GetByteCount(name) > 100)
            {
                throw new InvalidOperationException($"Entry path exceeds USTAR prefix+name limit: '{normalized}'.");
            }
        }

        WriteAsciiField(header.Slice(0, 100), name);
        WriteOctalField(header.Slice(100, 8), 0644, 7);
        WriteOctalField(header.Slice(108, 8), 0, 7);     // uid = 0
        WriteOctalField(header.Slice(116, 8), 0, 7);     // gid = 0
        WriteOctalField(header.Slice(124, 12), size, 11); // size
        WriteOctalField(header.Slice(136, 12), 0, 11);   // mtime = 0 (deterministic)

        // Checksum field filled with spaces during sum computation.
        header.Slice(148, 8).Fill((byte)' ');

        header[156] = (byte)'0'; // normal file
        WriteAsciiField(header.Slice(257, 6), "ustar");
        header[263] = (byte)'0';
        header[264] = (byte)'0';
        WriteAsciiField(header.Slice(265, 32), "root");
        WriteAsciiField(header.Slice(297, 32), "root");

        if (!string.IsNullOrEmpty(prefix))
        {
            WriteAsciiField(header.Slice(345, 155), prefix);
        }

        uint sum = 0;
        for (int i = 0; i < BlockSize; i++)
        {
            sum += header[i];
        }

        WriteOctalField(header.Slice(148, 7), sum, 6);
        header[155] = (byte)' ';
    }

    private static void WriteAsciiField(Span<byte> dest, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        bytes.AsSpan(0, Math.Min(bytes.Length, dest.Length)).CopyTo(dest);
    }

    private static void WriteOctalField(Span<byte> dest, long value, int digits)
    {
        string oct = Convert.ToString(value, 8).PadLeft(digits, '0');
        var bytes = Encoding.ASCII.GetBytes(oct);
        bytes.AsSpan(0, Math.Min(bytes.Length, digits)).CopyTo(dest);
        if (digits < dest.Length)
        {
            dest[digits] = 0;
        }
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> span)
    {
        int end = span.IndexOf((byte)0);
        if (end < 0)
        {
            end = span.Length;
        }

        return Encoding.ASCII.GetString(span[..end]);
    }

    private static long ParseOctal(ReadOnlySpan<byte> span)
    {
        string raw = ReadNullTerminatedAscii(span).Trim(' ', '\0');
        return string.IsNullOrEmpty(raw) ? 0 : Convert.ToInt64(raw, 8);
    }

    private static bool IsAllZero(ReadOnlySpan<byte> span)
    {
        foreach (byte b in span)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int ReadExactOrEof(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                return offset;
            }

            offset += read;
        }

        return offset;
    }

    private static void SkipExact(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        Span<byte> scratch = stackalloc byte[512];
        while (count > 0)
        {
            int read = stream.Read(scratch[..(int)Math.Min(scratch.Length, count)]);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected EOF skipping TAR padding.");
            }

            count -= read;
        }
    }

    private static void SkipAligned(Stream stream, long size)
    {
        long aligned = ((size + BlockSize - 1) / BlockSize) * BlockSize;
        SkipExact(stream, aligned);
    }
}
