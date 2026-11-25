namespace UltraCompress.Core.Archive;

/// <summary>
/// Classifies files into <see cref="UcxContentClass"/> buckets using container magic bytes,
/// extension families, and multi-window Shannon entropy so mixed directories partition into
/// optimal heterogeneous sub-blocks.
/// </summary>
public static class UcxContentClassifier
{
    public const double IncompressibleEntropyThreshold = 7.85;

    private static readonly HashSet<string> CompressedMediaAndArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".avif", ".gif",
        ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".flv",
        ".mp3", ".aac", ".m4a", ".flac", ".ogg", ".opus", ".wma",
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".br", ".ucx",
        ".nupkg", ".whl", ".jar", ".apk", ".ipa", ".docx", ".xlsx", ".pptx",
    };

    private static readonly HashSet<string> SourceAndTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".vb", ".fs", ".sln", ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".wixproj", ".wxs",
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".vue", ".svelte",
        ".py", ".pyi", ".rs", ".go", ".c", ".cc", ".cpp", ".cxx", ".h", ".hpp", ".java", ".kt", ".swift", ".rb", ".php",
        ".txt", ".md", ".markdown", ".rst", ".log", ".ini", ".cfg", ".conf", ".toml", ".yaml", ".yml", ".env",
        ".html", ".htm", ".css", ".scss", ".less", ".svg", ".xaml", ".proto", ".graphql",
        ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".cmd", ".bat", ".dockerfile", ".gitignore", ".editorconfig",
    };

    private static readonly HashSet<string> StructuredDataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".ndjson", ".jsonl", ".geojson",
        ".csv", ".tsv", ".xml", ".sql", ".sqlite", ".sqlite3", ".db",
    };

    public static (UcxContentClass ContentClass, string DetectedSignal) Classify(
        string filePath,
        long fileLength,
        ReadOnlySpan<byte> headSample,
        double sampledEntropyBitsPerByte)
    {
        if (fileLength == 0)
        {
            return (UcxContentClass.EmptyOrDirectory, "empty-file");
        }

        var ext = Path.GetExtension(filePath);
        var magicKind = DetectCompressedContainerMagic(headSample);
        if (magicKind is not null)
        {
            return (UcxContentClass.IncompressibleMedia, $"magic:{magicKind} (entropy {sampledEntropyBitsPerByte:F2} bpb)");
        }

        if (IsSqliteHeader(headSample))
        {
            return (UcxContentClass.StructuredData, $"magic:sqlite3 (entropy {sampledEntropyBitsPerByte:F2} bpb)");
        }

        // Known compressed container extensions bypass heavy compression unless file is tiny synthetic text disguised with media ext?
        // Actually real JPEGs/MP4s/ZIPs either match magic or have compressed extensions; if entropy is very low (< 6.0) on a tiny synthetic test file
        // with a media extension (e.g., fake bytes in unit tests), only classify as IncompressibleMedia when magic matches OR entropy >= 6.8 OR ext is media with realistic entropy!
        if (CompressedMediaAndArchiveExtensions.Contains(ext) && sampledEntropyBitsPerByte >= 6.5)
        {
            return (UcxContentClass.IncompressibleMedia, $"ext:{ext.ToLowerInvariant()} (entropy {sampledEntropyBitsPerByte:F2} bpb)");
        }

        if (fileLength >= 512 && sampledEntropyBitsPerByte >= IncompressibleEntropyThreshold)
        {
            return (UcxContentClass.IncompressibleMedia, $"high-entropy:{sampledEntropyBitsPerByte:F2} bpb >= {IncompressibleEntropyThreshold:F2}");
        }

        if (StructuredDataExtensions.Contains(ext))
        {
            return (UcxContentClass.StructuredData, $"structured:{ext.ToLowerInvariant()} (entropy {sampledEntropyBitsPerByte:F2} bpb)");
        }

        if (SourceAndTextExtensions.Contains(ext))
        {
            return (UcxContentClass.SourceAndText, $"source-text:{ext.ToLowerInvariant()} (entropy {sampledEntropyBitsPerByte:F2} bpb)");
        }

        if (LooksLikeUtf8Text(headSample))
        {
            return (UcxContentClass.SourceAndText, $"utf8-text (entropy {sampledEntropyBitsPerByte:F2} bpb)");
        }

        return (UcxContentClass.ExecutableAndBinary, $"binary:{(string.IsNullOrEmpty(ext) ? "raw" : ext.ToLowerInvariant())} (entropy {sampledEntropyBitsPerByte:F2} bpb)");
    }

    private static string? DetectCompressedContainerMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return "image/png";
        }

        if (header.Length >= 6 && header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F'
            && header[3] == (byte)'8' && (header[4] == (byte)'7' || header[4] == (byte)'9') && header[5] == (byte)'a')
        {
            return "image/gif";
        }

        // ISO BMFF: [size:4]['ftyp'][brand:4] -> MP4, MOV, HEIC, AVIF, M4A
        if (header.Length >= 12
            && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p')
        {
            return "video-or-image/isobmff";
        }

        // Matroska / WebM EBML header: 1A 45 DF A3
        if (header.Length >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
        {
            return "video/matroska";
        }

        // RIFF WEBP
        if (header.Length >= 12
            && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
        {
            return "image/webp";
        }

        // Ogg / FLAC
        if (header.Length >= 4 && header[0] == (byte)'O' && header[1] == (byte)'g' && header[2] == (byte)'g' && header[3] == (byte)'S')
        {
            return "audio/ogg";
        }

        if (header.Length >= 4 && header[0] == (byte)'f' && header[1] == (byte)'L' && header[2] == (byte)'a' && header[3] == (byte)'C')
        {
            return "audio/flac";
        }

        // ZIP / GZip / Zstd / XZ / 7z / UCX1
        if (header.Length >= 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04)
        {
            return "application/zip";
        }

        if (header.Length >= 2 && header[0] == 0x1F && header[1] == 0x8B)
        {
            return "application/gzip";
        }

        if (header.Length >= 4 && header[0] == 0x28 && header[1] == 0xB5 && header[2] == 0x2F && header[3] == 0xFD)
        {
            return "application/zstd";
        }

        if (header.Length >= 6 && header[0] == 0xFD && header[1] == 0x37 && header[2] == 0x7A
            && header[3] == 0x58 && header[4] == 0x5A && header[5] == 0x00)
        {
            return "application/x-xz";
        }

        if (header.Length >= 4 && header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC && header[3] == 0xAF)
        {
            return "application/x-7z";
        }

        if (header.Length >= 4 && header[0] == (byte)'U' && header[1] == (byte)'C' && header[2] == (byte)'X' && header[3] == (byte)'1')
        {
            return "application/x-ucx";
        }

        return null;
    }

    private static bool IsSqliteHeader(ReadOnlySpan<byte> header) =>
        header.Length >= 15 && header[..15].SequenceEqual("SQLite format 3"u8);

    private static bool LooksLikeUtf8Text(ReadOnlySpan<byte> sample)
    {
        if (sample.IsEmpty)
        {
            return false;
        }

        int printable = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            byte b = sample[i];
            if (b == 0)
            {
                return false;
            }

            if (b is 0x09 or 0x0A or 0x0D || (b >= 0x20 && b <= 0x7E) || b >= 0xC2)
            {
                printable++;
            }
        }

        return (double)printable / sample.Length >= 0.92;
    }
}
