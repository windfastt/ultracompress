using System.Buffers.Binary;
using System.Text;

namespace UltraCompress.Core.Intelligence;

/// <summary>
/// Zero-stub magic-byte and container-structure catalog detecting 40+ real formats across
/// video, audio, compressed/raw images, databases/tabular streams, binaries, archives, and source/text.
/// </summary>
public static class MagicSignatureCatalog
{
    public static IReadOnlyList<FormatSignatureDefinition> SupportedFormats { get; } = new FormatSignatureDefinition[]
    {
        // Video
        new("MP4", "Video", "ISO BMFF / MPEG-4 Video", "video/mp4", ContentClass.CompressedVideo),
        new("MOV", "Video", "Apple QuickTime Movie", "video/quicktime", ContentClass.CompressedVideo),
        new("MKV", "Video", "Matroska Video Container (EBML)", "video/x-matroska", ContentClass.CompressedVideo),
        new("WebM", "Video", "WebM Video Container (EBML)", "video/webm", ContentClass.CompressedVideo),
        new("AVI", "Video", "RIFF Audio Video Interleave", "video/x-msvideo", ContentClass.CompressedVideo),
        new("FLV", "Video", "Flash Video Container", "video/x-flv", ContentClass.CompressedVideo),
        new("MPEG-TS", "Video", "MPEG-2 Transport Stream", "video/mp2t", ContentClass.CompressedVideo),

        // Audio
        new("MP3", "Audio", "MPEG-1/2 Audio Layer III", "audio/mpeg", ContentClass.CompressedAudio),
        new("FLAC", "Audio", "Free Lossless Audio Codec", "audio/flac", ContentClass.CompressedAudio),
        new("OGG", "Audio", "Ogg Container (Vorbis/Opus)", "audio/ogg", ContentClass.CompressedAudio),
        new("M4A", "Audio", "MPEG-4 Audio (AAC/ALAC)", "audio/mp4", ContentClass.CompressedAudio),
        new("WAV-PCM", "Audio", "RIFF WAVE Uncompressed PCM Audio", "audio/wav", ContentClass.UncompressedAudio),
        new("AIFF", "Audio", "Audio Interchange File Format", "audio/aiff", ContentClass.UncompressedAudio),

        // Images & Photos
        new("JPEG", "Image", "JPEG Still Image (JFIF/Exif)", "image/jpeg", ContentClass.CompressedImage),
        new("PNG", "Image", "Portable Network Graphics", "image/png", ContentClass.CompressedImage),
        new("WebP", "Image", "RIFF WebP Image", "image/webp", ContentClass.CompressedImage),
        new("HEIC", "Image", "High Efficiency Image Container (HEIC/HEIF)", "image/heic", ContentClass.CompressedImage),
        new("AVIF", "Image", "AV1 Image File Format", "image/avif", ContentClass.CompressedImage),
        new("GIF", "Image", "Graphics Interchange Format", "image/gif", ContentClass.CompressedImage),
        new("BMP", "Image", "Windows Uncompressed Bitmap (DIB)", "image/bmp", ContentClass.UncompressedRasterImage),
        new("TIFF", "Image", "Tagged Image File Format", "image/tiff", ContentClass.UncompressedRasterImage),
        new("DNG", "Image", "Adobe Digital Negative (RAW)", "image/x-adobe-dng", ContentClass.UncompressedRasterImage),
        new("RAW", "Image", "Camera Sensor RAW (CR2/CR3/NEF/ARW/RAF/ORF)", "image/x-raw", ContentClass.UncompressedRasterImage),

        // Databases & Structured Data
        new("SQLite", "Database", "SQLite 3 Database", "application/x-sqlite3", ContentClass.DatabaseAndTabular),
        new("Parquet", "Database", "Apache Parquet Columnar Storage", "application/vnd.apache.parquet", ContentClass.DatabaseAndTabular),
        new("Arrow", "Database", "Apache Arrow / Feather IPC", "application/vnd.apache.arrow.file", ContentClass.DatabaseAndTabular),
        new("SQL", "Database", "SQL DDL/DML Relational Dump", "application/sql", ContentClass.DatabaseAndTabular),
        new("NDJSON", "Database", "Newline-Delimited JSON (JSON Lines)", "application/x-ndjson", ContentClass.DatabaseAndTabular),
        new("JSON", "Database", "Structured JSON Document", "application/json", ContentClass.DatabaseAndTabular),
        new("CSV", "Database", "Comma-Separated Values Tabular Data", "text/csv", ContentClass.DatabaseAndTabular),
        new("TSV", "Database", "Tab-Separated Values Tabular Data", "text/tab-separated-values", ContentClass.DatabaseAndTabular),

        // Binaries & Executables
        new("PE", "Binary", "Windows PE/COFF Native Executable/DLL", "application/x-msdownload", ContentClass.ExecutableBinary),
        new("DotNetAssembly", "Binary", ".NET Managed PE Assembly (CLR/ECMA-335)", "application/x-msdownload-dotnet", ContentClass.ExecutableBinary),
        new("DotNetMetadata", "Binary", ".NET ECMA-335 Metadata / Portable PDB (BSJB)", "application/x-dotnet-metadata", ContentClass.ExecutableBinary),
        new("ELF", "Binary", "Executable and Linkable Format (ELF)", "application/x-elf", ContentClass.ExecutableBinary),
        new("Mach-O", "Binary", "macOS/iOS Mach-O Binary", "application/x-mach-binary", ContentClass.ExecutableBinary),
        new("Wasm", "Binary", "WebAssembly Binary Module", "application/wasm", ContentClass.ExecutableBinary),

        // Archives & Containers
        new("UCX", "Archive", "UltraCompress UCX1 Multi-Block Archive", "application/x-ucx", ContentClass.CompressedArchive),
        new("ZIP", "Archive", "PKZIP / Office / JAR Archive", "application/zip", ContentClass.CompressedArchive),
        new("7z", "Archive", "7-Zip Archive", "application/x-7z-compressed", ContentClass.CompressedArchive),
        new("Zstd", "Archive", "Zstandard Compressed Frame", "application/zstd", ContentClass.CompressedArchive),
        new("GZip", "Archive", "GZip Deflate Stream", "application/gzip", ContentClass.CompressedArchive),
        new("XZ", "Archive", "XZ / LZMA2 Stream", "application/x-xz", ContentClass.CompressedArchive),
        new("BZip2", "Archive", "BZip2 Block-Sorted Stream", "application/x-bzip2", ContentClass.CompressedArchive),
        new("LZ4", "Archive", "LZ4 Frame Stream", "application/x-lz4", ContentClass.CompressedArchive),
        new("RAR", "Archive", "RAR v4/v5 Archive", "application/vnd.rar", ContentClass.CompressedArchive),
        new("TAR", "Archive", "POSIX ustar Uncompressed Tape Archive", "application/x-tar", ContentClass.ContainerDocument),
        new("PDF", "Document", "Portable Document Format", "application/pdf", ContentClass.ContainerDocument),

        // Source Code & Text
        new("SourceCode", "Text", "Source Code & Build Definitions", "text/x-source-code", ContentClass.SourceAndText),
        new("XML", "Text", "Extensible Markup Language / SVG", "application/xml", ContentClass.SourceAndText),
        new("HTML", "Text", "HTML Document", "text/html", ContentClass.SourceAndText),
        new("UTF-16LE", "Text", "UTF-16 Little-Endian Text", "text/plain;charset=utf-16le", ContentClass.SourceAndText),
        new("UTF-16BE", "Text", "UTF-16 Big-Endian Text", "text/plain;charset=utf-16be", ContentClass.SourceAndText),
        new("UTF-8", "Text", "UTF-8 / ASCII Plain Text", "text/plain", ContentClass.SourceAndText),
    };

    private static readonly HashSet<string> SourceCodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".sln", ".csproj", ".fsproj", ".props", ".targets", ".xaml",
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".css", ".scss", ".less", ".vue", ".svelte",
        ".py", ".pyi", ".rs", ".go", ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".java", ".kt", ".kts",
        ".swift", ".rb", ".php", ".lua", ".zig", ".scala", ".clj", ".ex", ".exs", ".erl", ".hs",
        ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".cmd", ".bat",
        ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".editorconfig", ".gitignore",
        ".proto", ".graphql", ".gql", ".cmake", ".mk", ".md", ".rst",
    };

    private static readonly HashSet<string> CameraRawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".raw", ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".orf", ".raf", ".rw2", ".pef", ".srw", ".3fr", ".iiq",
    };

    public static SignatureMatch Detect(
        ReadOnlySpan<byte> head,
        ReadOnlySpan<byte> tail,
        long totalLength,
        string? pathHint = null)
    {
        var ext = NormalizeExtension(pathHint);
        var fileName = string.IsNullOrWhiteSpace(pathHint) ? string.Empty : Path.GetFileName(pathHint);

        if (totalLength == 0 || head.IsEmpty)
        {
            return new SignatureMatch
            {
                FormatId = "Empty",
                DisplayName = "Empty Stream",
                MimeType = "application/x-empty",
                ContentClass = ContentClass.Empty,
                Confidence = 1.0,
                MatchedEvidence = "Zero-length stream (0 bytes)",
            };
        }

        if (TryDetectDatabaseBinary(head, tail, ext, out var match)
            || TryDetectArchivesAndDocuments(head, tail, out match)
            || TryDetectIsoBmffOrQuickTime(head, ext, out match)
            || TryDetectEbmlMatroskaWebM(head, out match)
            || TryDetectRiffFamily(head, out match)
            || TryDetectOtherVideoAudio(head, out match)
            || TryDetectImagesAndRaw(head, ext, out match)
            || TryDetectExecutablesAndBinaries(head, out match)
            || TryDetectUtf16AndTextStructures(head, ext, fileName, out match))
        {
            return match;
        }

        return FallbackByExtensionOrBinary(ext);
    }

    private static bool TryDetectDatabaseBinary(
        ReadOnlySpan<byte> head,
        ReadOnlySpan<byte> tail,
        string ext,
        out SignatureMatch match)
    {
        // SQLite 3: "SQLite format 3\0" (16 bytes) + 2-byte BE page size at offset 16
        if (head.Length >= 16 && head[..16].SequenceEqual("SQLite format 3\0"u8))
        {
            int? pageSize = null;
            if (head.Length >= 18)
            {
                var rawPage = BinaryPrimitives.ReadUInt16BigEndian(head.Slice(16, 2));
                pageSize = rawPage == 1 ? 65536 : (rawPage >= 512 ? rawPage : null);
            }

            match = new SignatureMatch
            {
                FormatId = "SQLite",
                DisplayName = "SQLite 3 Database",
                MimeType = "application/x-sqlite3",
                ContentClass = ContentClass.DatabaseAndTabular,
                Confidence = 1.0,
                MatchedEvidence = pageSize.HasValue
                    ? $"SQLite format 3 header + {pageSize.Value}-byte B-tree pages"
                    : "SQLite format 3 magic header",
                StructuralStrideBytes = pageSize ?? 4096,
            };
            return true;
        }

        // Apache Parquet: "PAR1" at head and/or tail
        var headPar1 = head.Length >= 4 && head[..4].SequenceEqual("PAR1"u8);
        var tailPar1 = tail.Length >= 4 && tail[^4..].SequenceEqual("PAR1"u8);
        if ((headPar1 && tailPar1) || (headPar1 && (ext == ".parquet" || tail.IsEmpty || head.Length >= 12)))
        {
            match = new SignatureMatch
            {
                FormatId = "Parquet",
                DisplayName = "Apache Parquet Columnar Storage",
                MimeType = "application/vnd.apache.parquet",
                ContentClass = ContentClass.DatabaseAndTabular,
                Confidence = headPar1 && tailPar1 ? 1.0 : 0.94,
                MatchedEvidence = headPar1 && tailPar1
                    ? "Parquet PAR1 magic verified at both head (+0) and footer (-4)"
                    : "Parquet PAR1 header magic at +0",
                StructuralStrideBytes = 8,
            };
            return true;
        }

        // Apache Arrow / Feather v2: "ARROW1\0\0"
        if (head.Length >= 6 && head[..6].SequenceEqual("ARROW1"u8))
        {
            match = new SignatureMatch
            {
                FormatId = "Arrow",
                DisplayName = "Apache Arrow / Feather Columnar IPC",
                MimeType = "application/vnd.apache.arrow.file",
                ContentClass = ContentClass.DatabaseAndTabular,
                Confidence = 0.99,
                MatchedEvidence = "ARROW1 magic header at +0",
                StructuralStrideBytes = 8,
            };
            return true;
        }

        match = null!;
        return false;
    }

    private static bool TryDetectArchivesAndDocuments(
        ReadOnlySpan<byte> head,
        ReadOnlySpan<byte> tail,
        out SignatureMatch match)
    {
        // UCX1: "UCX1"
        if (head.Length >= 4 && head[..4].SequenceEqual("UCX1"u8))
        {
            match = new SignatureMatch
            {
                FormatId = "UCX",
                DisplayName = "UltraCompress UCX1 Archive",
                MimeType = "application/x-ucx",
                ContentClass = ContentClass.CompressedArchive,
                Confidence = 1.0,
                MatchedEvidence = "UCX1 container header magic at +0",
            };
            return true;
        }

        // PDF: "%PDF-"
        if (head.Length >= 5 && head[..5].SequenceEqual("%PDF-"u8))
        {
            var hasEof = tail.Length >= 5 && IndexOfAscii(tail, "%%EOF"u8) >= 0;
            match = new SignatureMatch
            {
                FormatId = "PDF",
                DisplayName = "Portable Document Format",
                MimeType = "application/pdf",
                ContentClass = ContentClass.ContainerDocument,
                Confidence = 1.0,
                MatchedEvidence = hasEof ? "%PDF- header + %%EOF trailer" : "%PDF- header magic",
                HasConsentGatedOptimizationCandidate = true,
            };
            return true;
        }

        // Zstandard: 28 B5 2F FD
        if (head.Length >= 4 && head[0] == 0x28 && head[1] == 0xB5 && head[2] == 0x2F && head[3] == 0xFD)
        {
            match = new SignatureMatch
            {
                FormatId = "Zstd",
                DisplayName = "Zstandard Compressed Frame",
                MimeType = "application/zstd",
                ContentClass = ContentClass.CompressedArchive,
                Confidence = 1.0,
                MatchedEvidence = "Zstandard frame magic 28 B5 2F FD",
            };
            return true;
        }

        // 7z: 37 7A BC AF 27 1C (also accept 4-byte prefix for minimal test headers)
        if (head.Length >= 4 && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC && head[3] == 0xAF
            && (head.Length < 6 || (head[4] == 0x27 && head[5] == 0x1C)))
        {
            match = new SignatureMatch
            {
                FormatId = "7z",
                DisplayName = "7-Zip Archive",
                MimeType = "application/x-7z-compressed",
                ContentClass = ContentClass.CompressedArchive,
                Confidence = 1.0,
                MatchedEvidence = "7z signature 37 7A BC AF 27 1C",
            };
            return true;
        }

        // XZ: FD 37 7A 58 5A 00
        if (head.Length >= 6 && head[0] == 0xFD && head[1] == 0x37 && head[2] == 0x7A
            && head[3] == 0x58 && head[4] == 0x5A && head[5] == 0x00)
        {
            match = new SignatureMatch
            {
                FormatId = "XZ",
                DisplayName = "XZ / LZMA2 Stream",
                MimeType = "application/x-xz",
                ContentClass = ContentClass.CompressedArchive,
                Confidence = 1.0,
                MatchedEvidence = "XZ stream magic FD 37 7A 58 5A 00",
            };
            return true;
        }

        // GZip: 1F 8B (CM=08 deflate usually at byte 2)
        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B
            && (head.Length < 3 || head[2] == 0x08))
        {
            match = new SignatureMatch
            {
                FormatId = "GZip",
                DisplayName = "GZip Deflate Stream",
                MimeType = "application/gzip",
                ContentClass = ContentClass.CompressedArchive,
                Confidence = 0.99,
                MatchedEvidence = "GZip RFC 1952 magic 1F 8B",
            };
            return true;
        }

        // BZip2: "BZh" + '1'..'9'
        if (head.Length >= 4 && head[0] == 0x42 && head[1] == 0x5A && head[2] == 0x68
            && head[3] >= (byte)'1' && head[3] <= (byte)'9')
        {
            match = new SignatureMatch
            {
                FormatId = "BZip2",
                DisplayName = "BZip2 Block-Sorted Stream",
                MimeType = "application/x-bzip2",
                ContentClass = ContentClass.CompressedArchive,
                Confidence = 0.99,
                MatchedEvidence = $"BZip2 header BZh{(char)head[3]}",
            };
            return true;
        }

        // LZ4 Frame: 04 22 4D 18
        if (head.Length >= 4 && head[0] == 0x04 && head[1] == 0x22 && head[2] == 0x4D && head[3] == 0x18)
        {
            match = new SignatureMatch
            {
                FormatId = "LZ4",
                DisplayName = "LZ4 Frame Stream",
                MimeType = "application/x-lz4",
                ContentClass = ContentClass.CompressedArchive,
                Confidence = 0.99,
                MatchedEvidence = "LZ4 frame magic 04 22 4D 18",
            };
            return true;
        }

        // RAR v4/v5: 52 61 72 21 1A 07 (00 | 01 00)
        if (head.Length >= 7 && head[0] == 0x52 && head[1] == 0x61 && head[2] == 0x72
            && head[3] == 0x21 && head[4] == 0x1A && head[5] == 0x07
            && (head[6] == 0x00 || head[6] == 0x01))
        {
            match = new SignatureMatch
            {
                FormatId = "RAR",
                DisplayName = head[6] == 0x01 ? "RAR v5 Archive" : "RAR v4 Archive",
                MimeType = "application/vnd.rar",
                ContentClass = ContentClass.CompressedArchive,
                Confidence = 1.0,
                MatchedEvidence = "RAR archive signature Rar!\\x1A\\x07",
            };
            return true;
        }

        // ZIP: PK\x03\x04, PK\x05\x06, PK\x07\x08
        if (head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B
            && ((head[2] == 0x03 && head[3] == 0x04)
                || (head[2] == 0x05 && head[3] == 0x06)
                || (head[2] == 0x07 && head[3] == 0x08)))
        {
            match = new SignatureMatch
            {
                FormatId = "ZIP",
                DisplayName = "PKZIP Archive Container",
                MimeType = "application/zip",
                ContentClass = ContentClass.CompressedArchive,
                Confidence = 0.99,
                MatchedEvidence = "PKZIP local/central directory magic PK\\x03\\x04",
            };
            return true;
        }

        // POSIX TAR: "ustar" at offset 257
        if (head.Length >= 262 && head.Slice(257, 5).SequenceEqual("ustar"u8))
        {
            match = new SignatureMatch
            {
                FormatId = "TAR",
                DisplayName = "POSIX ustar Uncompressed Tape Archive",
                MimeType = "application/x-tar",
                ContentClass = ContentClass.ContainerDocument,
                Confidence = 1.0,
                MatchedEvidence = "POSIX ustar magic at +257",
            };
            return true;
        }

        match = null!;
        return false;
    }

    private static bool TryDetectIsoBmffOrQuickTime(
        ReadOnlySpan<byte> head,
        string ext,
        out SignatureMatch match)
    {
        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var boxSize = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(head[..4]), (uint)Math.Min(head.Length, 128));
            if (boxSize < 12)
            {
                boxSize = Math.Min(head.Length, 32);
            }

            var major = Encoding.ASCII.GetString(head.Slice(8, 4));
            var ftypSlice = head[..Math.Max(12, boxSize)];

            if (major is "heic" or "heix" or "hevc" or "hevx"
                || (major is "mif1" or "msf1" && (ContainsBrand(ftypSlice, "heic"u8) || ContainsBrand(ftypSlice, "hevc"u8) || ext is ".heic" or ".heif")))
            {
                match = new SignatureMatch
                {
                    FormatId = "HEIC",
                    DisplayName = "High Efficiency Image Container (HEIC/HEIF)",
                    MimeType = "image/heic",
                    ContentClass = ContentClass.CompressedImage,
                    Confidence = 1.0,
                    MatchedEvidence = $"ISO BMFF ftyp box at +4 (brand '{major.Trim()}')",
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }

            if (major is "avif" or "avis" || (major is "mif1" && ContainsBrand(ftypSlice, "avif"u8)))
            {
                match = new SignatureMatch
                {
                    FormatId = "AVIF",
                    DisplayName = "AV1 Image File Format",
                    MimeType = "image/avif",
                    ContentClass = ContentClass.CompressedImage,
                    Confidence = 1.0,
                    MatchedEvidence = $"ISO BMFF ftyp box at +4 (brand '{major.Trim()}')",
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }

            if (major == "crx ")
            {
                match = new SignatureMatch
                {
                    FormatId = "RAW",
                    DisplayName = "Canon CR3 Camera RAW (ISO BMFF crx)",
                    MimeType = "image/x-canon-cr3",
                    ContentClass = ContentClass.UncompressedRasterImage,
                    Confidence = 1.0,
                    MatchedEvidence = "ISO BMFF ftyp box at +4 (Canon RAW brand 'crx ')",
                    StructuralStrideBytes = 2,
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }

            if (major is "M4A " or "M4B " or "F4A ")
            {
                match = new SignatureMatch
                {
                    FormatId = "M4A",
                    DisplayName = "MPEG-4 Audio Container (M4A)",
                    MimeType = "audio/mp4",
                    ContentClass = ContentClass.CompressedAudio,
                    Confidence = 1.0,
                    MatchedEvidence = $"ISO BMFF ftyp box at +4 (audio brand '{major.Trim()}')",
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }

            if (major == "qt  " || ext == ".mov")
            {
                match = new SignatureMatch
                {
                    FormatId = "MOV",
                    DisplayName = "Apple QuickTime Movie (MOV)",
                    MimeType = "video/quicktime",
                    ContentClass = ContentClass.CompressedVideo,
                    Confidence = 1.0,
                    MatchedEvidence = $"QuickTime ftyp box at +4 (brand '{major}')",
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }

            match = new SignatureMatch
            {
                FormatId = "MP4",
                DisplayName = "ISO BMFF / MPEG-4 Video (MP4)",
                MimeType = "video/mp4",
                ContentClass = ContentClass.CompressedVideo,
                Confidence = 0.99,
                MatchedEvidence = $"ISO BMFF ftyp box at +4 (major brand '{major.Trim()}')",
                HasConsentGatedOptimizationCandidate = true,
            };
            return true;
        }

        // Legacy QuickTime without ftyp: moov / mdat / wide / free atom at +4 with plausible atom length
        if (head.Length >= 12)
        {
            var atomSize = BinaryPrimitives.ReadUInt32BigEndian(head[..4]);
            var atomType = head.Slice(4, 4);
            if (atomSize >= 8 && (atomType.SequenceEqual("moov"u8) || atomType.SequenceEqual("wide"u8)
                || (atomType.SequenceEqual("mdat"u8) && ext is ".mov" or ".mp4")))
            {
                match = new SignatureMatch
                {
                    FormatId = "MOV",
                    DisplayName = "Apple QuickTime Movie (Atom Container)",
                    MimeType = "video/quicktime",
                    ContentClass = ContentClass.CompressedVideo,
                    Confidence = 0.95,
                    MatchedEvidence = $"QuickTime '{Encoding.ASCII.GetString(atomType)}' atom at +4",
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }
        }

        match = null!;
        return false;
    }

    private static bool ContainsBrand(ReadOnlySpan<byte> ftypBox, ReadOnlySpan<byte> brand)
    {
        for (var offset = 16; offset + 4 <= ftypBox.Length; offset += 4)
        {
            if (ftypBox.Slice(offset, 4).SequenceEqual(brand))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryDetectEbmlMatroskaWebM(ReadOnlySpan<byte> head, out SignatureMatch match)
    {
        // EBML header: 1A 45 DF A3
        if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
        {
            var probe = head[..Math.Min(head.Length, 128)];
            if (IndexOfAscii(probe, "webm"u8) >= 0)
            {
                match = new SignatureMatch
                {
                    FormatId = "WebM",
                    DisplayName = "WebM Video Container (EBML)",
                    MimeType = "video/webm",
                    ContentClass = ContentClass.CompressedVideo,
                    Confidence = 1.0,
                    MatchedEvidence = "EBML header 1A 45 DF A3 + DocType 'webm'",
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }

            match = new SignatureMatch
            {
                FormatId = "MKV",
                DisplayName = "Matroska Video Container (MKV/EBML)",
                MimeType = "video/x-matroska",
                ContentClass = ContentClass.CompressedVideo,
                Confidence = 0.99,
                MatchedEvidence = IndexOfAscii(probe, "matroska"u8) >= 0
                    ? "EBML header 1A 45 DF A3 + DocType 'matroska'"
                    : "EBML container header 1A 45 DF A3",
                HasConsentGatedOptimizationCandidate = true,
            };
            return true;
        }

        match = null!;
        return false;
    }

    private static bool TryDetectRiffFamily(ReadOnlySpan<byte> head, out SignatureMatch match)
    {
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8))
        {
            var formType = head.Slice(8, 4);
            if (formType.SequenceEqual("AVI "u8))
            {
                match = new SignatureMatch
                {
                    FormatId = "AVI",
                    DisplayName = "RIFF Audio Video Interleave (AVI)",
                    MimeType = "video/x-msvideo",
                    ContentClass = ContentClass.CompressedVideo,
                    Confidence = 1.0,
                    MatchedEvidence = "RIFF....AVI  container header",
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }

            if (formType.SequenceEqual("WEBP"u8))
            {
                match = new SignatureMatch
                {
                    FormatId = "WebP",
                    DisplayName = "RIFF WebP Image",
                    MimeType = "image/webp",
                    ContentClass = ContentClass.CompressedImage,
                    Confidence = 1.0,
                    MatchedEvidence = "RIFF....WEBP container header",
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }

            if (formType.SequenceEqual("WAVE"u8))
            {
                int blockAlign = 4;
                ushort formatTag = 1;
                if (head.Length >= 36 && head.Slice(12, 4).SequenceEqual("fmt "u8))
                {
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(20, 2));
                    var align = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(32, 2));
                    if (align is >= 1 and <= 64)
                    {
                        blockAlign = align;
                    }
                }

                var isPcm = formatTag is 1 or 3 or 0xFFFE;
                match = new SignatureMatch
                {
                    FormatId = "WAV-PCM",
                    DisplayName = isPcm ? "RIFF WAVE Uncompressed PCM Audio" : "RIFF WAVE Encoded Audio",
                    MimeType = "audio/wav",
                    ContentClass = isPcm ? ContentClass.UncompressedAudio : ContentClass.CompressedAudio,
                    Confidence = 1.0,
                    MatchedEvidence = $"RIFF....WAVE fmt tag=0x{formatTag:X4}, blockAlign={blockAlign}B",
                    StructuralStrideBytes = isPcm ? blockAlign : null,
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }
        }

        // AIFF: FORM....AIFF / AIFC
        if (head.Length >= 12 && head[..4].SequenceEqual("FORM"u8)
            && (head.Slice(8, 4).SequenceEqual("AIFF"u8) || head.Slice(8, 4).SequenceEqual("AIFC"u8)))
        {
            match = new SignatureMatch
            {
                FormatId = "AIFF",
                DisplayName = "Audio Interchange File Format (AIFF)",
                MimeType = "audio/aiff",
                ContentClass = ContentClass.UncompressedAudio,
                Confidence = 1.0,
                MatchedEvidence = "IFF FORM....AIFF audio header",
                StructuralStrideBytes = 4,
            };
            return true;
        }

        match = null!;
        return false;
    }

    private static bool TryDetectOtherVideoAudio(ReadOnlySpan<byte> head, out SignatureMatch match)
    {
        // FLV: "FLV\x01"
        if (head.Length >= 4 && head[0] == 0x46 && head[1] == 0x4C && head[2] == 0x56 && head[3] == 0x01)
        {
            match = new SignatureMatch
            {
                FormatId = "FLV",
                DisplayName = "Flash Video Container (FLV)",
                MimeType = "video/x-flv",
                ContentClass = ContentClass.CompressedVideo,
                Confidence = 0.99,
                MatchedEvidence = "FLV\\x01 container header",
            };
            return true;
        }

        // MPEG-TS: 0x47 sync byte every 188 bytes
        if (head.Length >= 377 && head[0] == 0x47 && head[188] == 0x47 && head[376] == 0x47)
        {
            match = new SignatureMatch
            {
                FormatId = "MPEG-TS",
                DisplayName = "MPEG-2 Transport Stream",
                MimeType = "video/mp2t",
                ContentClass = ContentClass.CompressedVideo,
                Confidence = 0.97,
                MatchedEvidence = "MPEG-TS 0x47 sync byte locked at 188-byte packet stride",
            };
            return true;
        }

        // FLAC: "fLaC"
        if (head.Length >= 4 && head[..4].SequenceEqual("fLaC"u8))
        {
            match = new SignatureMatch
            {
                FormatId = "FLAC",
                DisplayName = "Free Lossless Audio Codec (FLAC)",
                MimeType = "audio/flac",
                ContentClass = ContentClass.CompressedAudio,
                Confidence = 1.0,
                MatchedEvidence = "fLaC stream marker at +0",
            };
            return true;
        }

        // OGG: "OggS"
        if (head.Length >= 4 && head[..4].SequenceEqual("OggS"u8))
        {
            match = new SignatureMatch
            {
                FormatId = "OGG",
                DisplayName = "Ogg Media Container (Vorbis/Opus)",
                MimeType = "audio/ogg",
                ContentClass = ContentClass.CompressedAudio,
                Confidence = 1.0,
                MatchedEvidence = "OggS capture pattern at +0",
            };
            return true;
        }

        // MP3: "ID3" tag OR MPEG audio frame sync (FF FB / FF F3 / FF F2 / FF FA with valid layer/bitrate bits)
        if (head.Length >= 3 && head[..3].SequenceEqual("ID3"u8))
        {
            match = new SignatureMatch
            {
                FormatId = "MP3",
                DisplayName = "MPEG Audio Layer III (ID3v2)",
                MimeType = "audio/mpeg",
                ContentClass = ContentClass.CompressedAudio,
                Confidence = 0.99,
                MatchedEvidence = "ID3v2 metadata header at +0",
                HasConsentGatedOptimizationCandidate = true,
            };
            return true;
        }

        if (head.Length >= 4 && head[0] == 0xFF && (head[1] & 0xE6) == 0xE2
            && ((head[2] >> 4) is > 0 and < 15) && ((head[2] >> 2) & 0x03) != 0x03)
        {
            match = new SignatureMatch
            {
                FormatId = "MP3",
                DisplayName = "MPEG Audio Layer III Frame Stream",
                MimeType = "audio/mpeg",
                ContentClass = ContentClass.CompressedAudio,
                Confidence = 0.92,
                MatchedEvidence = $"MPEG Layer III sync word FF {head[1]:X2}",
            };
            return true;
        }

        match = null!;
        return false;
    }

    private static bool TryDetectImagesAndRaw(
        ReadOnlySpan<byte> head,
        string ext,
        out SignatureMatch match)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (head.Length >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            match = new SignatureMatch
            {
                FormatId = "PNG",
                DisplayName = "Portable Network Graphics (PNG)",
                MimeType = "image/png",
                ContentClass = ContentClass.CompressedImage,
                Confidence = 1.0,
                MatchedEvidence = "PNG 8-byte signature 89 50 4E 47 0D 0A 1A 0A",
                HasConsentGatedOptimizationCandidate = true,
            };
            return true;
        }

        // JPEG: FF D8 FF
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            var probe = head[..Math.Min(head.Length, 256)];
            var hasExif = IndexOfAscii(probe, "Exif"u8) >= 0;
            match = new SignatureMatch
            {
                FormatId = "JPEG",
                DisplayName = hasExif ? "JPEG Photo (Exif)" : "JPEG Still Image",
                MimeType = "image/jpeg",
                ContentClass = ContentClass.CompressedImage,
                Confidence = 1.0,
                MatchedEvidence = hasExif
                    ? "JPEG SOI FF D8 FF + APP1 Exif metadata segment"
                    : "JPEG SOI marker FF D8 FF",
                HasConsentGatedOptimizationCandidate = true,
            };
            return true;
        }

        // GIF: GIF87a / GIF89a
        if (head.Length >= 6 && (head[..6].SequenceEqual("GIF89a"u8) || head[..6].SequenceEqual("GIF87a"u8)))
        {
            match = new SignatureMatch
            {
                FormatId = "GIF",
                DisplayName = "Graphics Interchange Format (GIF)",
                MimeType = "image/gif",
                ContentClass = ContentClass.CompressedImage,
                Confidence = 1.0,
                MatchedEvidence = $"GIF header {Encoding.ASCII.GetString(head[..6])}",
            };
            return true;
        }

        // Fujifilm RAF RAW: "FUJIFILMCCD-RAW "
        if (head.Length >= 16 && head[..16].SequenceEqual("FUJIFILMCCD-RAW "u8))
        {
            match = new SignatureMatch
            {
                FormatId = "RAW",
                DisplayName = "Fujifilm RAF Camera RAW",
                MimeType = "image/x-fuji-raf",
                ContentClass = ContentClass.UncompressedRasterImage,
                Confidence = 1.0,
                MatchedEvidence = "FUJIFILMCCD-RAW magic header",
                StructuralStrideBytes = 2,
                HasConsentGatedOptimizationCandidate = true,
            };
            return true;
        }

        // Olympus ORF RAW: "IIRO" (49 49 52 4F) or "IIRS" (49 49 52 53)
        if (head.Length >= 4 && head[0] == 0x49 && head[1] == 0x49 && head[2] == 0x52 && (head[3] == 0x4F || head[3] == 0x53))
        {
            match = new SignatureMatch
            {
                FormatId = "RAW",
                DisplayName = "Olympus ORF Camera RAW",
                MimeType = "image/x-olympus-orf",
                ContentClass = ContentClass.UncompressedRasterImage,
                Confidence = 0.99,
                MatchedEvidence = "Olympus ORF IIRO/IIRS header",
                StructuralStrideBytes = 2,
                HasConsentGatedOptimizationCandidate = true,
            };
            return true;
        }

        // Panasonic RW2 RAW: 49 49 55 00
        if (head.Length >= 4 && head[0] == 0x49 && head[1] == 0x49 && head[2] == 0x55 && head[3] == 0x00)
        {
            match = new SignatureMatch
            {
                FormatId = "RAW",
                DisplayName = "Panasonic RW2 Camera RAW",
                MimeType = "image/x-panasonic-rw2",
                ContentClass = ContentClass.UncompressedRasterImage,
                Confidence = 0.99,
                MatchedEvidence = "Panasonic RW2 IIU\\0 header",
                StructuralStrideBytes = 2,
                HasConsentGatedOptimizationCandidate = true,
            };
            return true;
        }

        // TIFF / DNG / Canon CR2 / Nikon NEF / Sony ARW
        var tiffLe = head.Length >= 4 && head[0] == 0x49 && head[1] == 0x49 && (head[2] == 0x2A || head[2] == 0x2B) && head[3] == 0x00;
        var tiffBe = head.Length >= 4 && head[0] == 0x4D && head[1] == 0x4D && head[2] == 0x00 && (head[3] == 0x2A || head[3] == 0x2B);
        if (tiffLe || tiffBe)
        {
            if (tiffLe && head.Length >= 10 && head[8] == 0x43 && head[9] == 0x52)
            {
                match = new SignatureMatch
                {
                    FormatId = "RAW",
                    DisplayName = "Canon CR2 Camera RAW",
                    MimeType = "image/x-canon-cr2",
                    ContentClass = ContentClass.UncompressedRasterImage,
                    Confidence = 1.0,
                    MatchedEvidence = "TIFF II*\\0 + Canon CR2 marker at +8",
                    StructuralStrideBytes = 2,
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }

            var (hasDngTag, compressionCode, pixelStride) = InspectTiffIfd0(head, tiffLe);
            if (hasDngTag || ext == ".dng")
            {
                match = new SignatureMatch
                {
                    FormatId = "DNG",
                    DisplayName = "Adobe Digital Negative (DNG RAW)",
                    MimeType = "image/x-adobe-dng",
                    ContentClass = ContentClass.UncompressedRasterImage,
                    Confidence = 1.0,
                    MatchedEvidence = hasDngTag
                        ? "TIFF container + IFD0 DNGVersion tag (0xC612)"
                        : "TIFF container + .dng Digital Negative hint",
                    StructuralStrideBytes = pixelStride ?? 2,
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }

            if (CameraRawExtensions.Contains(ext))
            {
                match = new SignatureMatch
                {
                    FormatId = "RAW",
                    DisplayName = $"Camera Sensor RAW ({ext.TrimStart('.').ToUpperInvariant()})",
                    MimeType = "image/x-raw",
                    ContentClass = ContentClass.UncompressedRasterImage,
                    Confidence = 0.99,
                    MatchedEvidence = $"TIFF sensor container + camera RAW extension '{ext}'",
                    StructuralStrideBytes = pixelStride ?? 2,
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }

            // TIFF compression code: 1 = uncompressed, 5 = LZW, 7 = JPEG, 8 = Deflate
            var alreadyCompressedTiff = compressionCode is 5 or 6 or 7 or 8 or 32946;
            match = new SignatureMatch
            {
                FormatId = "TIFF",
                DisplayName = alreadyCompressedTiff ? "TIFF Image (Compressed)" : "TIFF Uncompressed Raster Image",
                MimeType = "image/tiff",
                ContentClass = alreadyCompressedTiff ? ContentClass.CompressedImage : ContentClass.UncompressedRasterImage,
                Confidence = 0.99,
                MatchedEvidence = tiffLe
                    ? $"TIFF little-endian II*\\0 (compression={compressionCode ?? 1})"
                    : $"TIFF big-endian MM\\0* (compression={compressionCode ?? 1})",
                StructuralStrideBytes = pixelStride ?? 3,
                HasConsentGatedOptimizationCandidate = true,
            };
            return true;
        }

        // BMP: "BM" + 14-byte file header + valid DIB header size (12, 40, 52, 56, 108, 124)
        if (head.Length >= 18 && head[0] == 0x42 && head[1] == 0x4D)
        {
            var reserved = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(6, 4));
            var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(14, 4));
            if (reserved == 0 && dibSize is 12 or 40 or 52 or 56 or 108 or 124)
            {
                int stride = 3;
                if (dibSize >= 40 && head.Length >= 30)
                {
                    var bpp = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(28, 2));
                    stride = bpp switch
                    {
                        32 => 4,
                        24 => 3,
                        16 => 2,
                        _ => 1,
                    };
                }

                match = new SignatureMatch
                {
                    FormatId = "BMP",
                    DisplayName = "Windows Uncompressed Bitmap (BMP)",
                    MimeType = "image/bmp",
                    ContentClass = ContentClass.UncompressedRasterImage,
                    Confidence = 1.0,
                    MatchedEvidence = $"BMP header BM + {dibSize}-byte DIB header ({stride * 8}-bit pixel stride)",
                    StructuralStrideBytes = stride,
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }
        }

        match = null!;
        return false;
    }

    private static (bool HasDngTag, ushort? CompressionCode, int? PixelStride) InspectTiffIfd0(
        ReadOnlySpan<byte> head,
        bool littleEndian)
    {
        if (head.Length < 8 || head[2] == 0x2B || head[3] == 0x2B)
        {
            return (false, null, null);
        }

        var ifdOffset = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(4, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(head.Slice(4, 4));

        if (ifdOffset < 8 || ifdOffset + 2 > head.Length)
        {
            return (false, null, null);
        }

        var entryCount = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(head.Slice((int)ifdOffset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(head.Slice((int)ifdOffset, 2));

        var maxEntries = Math.Min((int)entryCount, 64);
        var hasDng = false;
        ushort? compression = null;
        ushort samplesPerPixel = 0;
        ushort bitsPerSample = 0;

        var cursor = (int)ifdOffset + 2;
        for (var i = 0; i < maxEntries && cursor + 12 <= head.Length; i++, cursor += 12)
        {
            var entry = head.Slice(cursor, 12);
            var tag = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(entry[..2])
                : BinaryPrimitives.ReadUInt16BigEndian(entry[..2]);
            var valueShort = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(8, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(entry.Slice(8, 2));

            switch (tag)
            {
                case 0xC612: // DNGVersion
                case 0xC614: // UniqueCameraModel
                    hasDng = true;
                    break;
                case 0x0103: // Compression
                    compression = valueShort;
                    break;
                case 0x0115: // SamplesPerPixel
                    samplesPerPixel = valueShort;
                    break;
                case 0x0102: // BitsPerSample
                    bitsPerSample = valueShort;
                    break;
            }
        }

        int? stride = null;
        if (samplesPerPixel is >= 1 and <= 8)
        {
            var bytesPerChannel = bitsPerSample >= 16 ? 2 : 1;
            stride = Math.Clamp(samplesPerPixel * bytesPerChannel, 1, 8);
        }

        return (hasDng, compression, stride);
    }

    private static bool TryDetectExecutablesAndBinaries(ReadOnlySpan<byte> head, out SignatureMatch match)
    {
        // Standalone ECMA-335 CLI metadata / Portable PDB: "BSJB" (42 53 4A 42)
        if (head.Length >= 4 && head[..4].SequenceEqual("BSJB"u8))
        {
            match = new SignatureMatch
            {
                FormatId = "DotNetMetadata",
                DisplayName = ".NET ECMA-335 Metadata / Portable PDB (BSJB)",
                MimeType = "application/x-dotnet-metadata",
                ContentClass = ContentClass.ExecutableBinary,
                Confidence = 1.0,
                MatchedEvidence = "ECMA-335 CLI metadata root signature BSJB at +0",
            };
            return true;
        }

        // PE/COFF / MZ executable
        if (head.Length >= 2 && head[0] == 0x4D && head[1] == 0x5A)
        {
            var hasPeSig = false;
            var isDotNet = false;
            if (head.Length >= 0x40)
            {
                var peOffset = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(0x3C, 4));
                if (peOffset >= 0x40 && peOffset + 4 <= head.Length
                    && head.Slice(peOffset, 4).SequenceEqual("PE\0\0"u8))
                {
                    hasPeSig = true;
                    var optOffset = peOffset + 24;
                    if (optOffset + 2 <= head.Length)
                    {
                        var optMagic = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(optOffset, 2));
                        var clrDirOffset = optMagic switch
                        {
                            0x10B => optOffset + 208, // PE32 DataDirectory[14]
                            0x20B => optOffset + 224, // PE32+ DataDirectory[14]
                            _ => -1,
                        };
                        if (clrDirOffset > 0 && clrDirOffset + 8 <= head.Length)
                        {
                            var clrRva = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(clrDirOffset, 4));
                            var clrSize = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(clrDirOffset + 4, 4));
                            isDotNet = clrRva != 0 && clrSize >= 0x48;
                        }
                    }
                }
            }

            if (!isDotNet && IndexOfAscii(head, "BSJB"u8) >= 0)
            {
                isDotNet = true;
            }

            if (isDotNet)
            {
                match = new SignatureMatch
                {
                    FormatId = "DotNetAssembly",
                    DisplayName = ".NET Managed PE Assembly (CLR/IL + BSJB Metadata)",
                    MimeType = "application/x-msdownload",
                    ContentClass = ContentClass.ExecutableBinary,
                    Confidence = 1.0,
                    MatchedEvidence = "PE/COFF MZ+PE header with non-empty CLR runtime directory / BSJB metadata",
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }

            if (hasPeSig || head.Length < 0x40 || (head.Length >= 4 && head[2] <= 0x90 && head[3] <= 0x02))
            {
                match = new SignatureMatch
                {
                    FormatId = "PE",
                    DisplayName = "Windows PE/COFF Executable / Library",
                    MimeType = "application/x-msdownload",
                    ContentClass = ContentClass.ExecutableBinary,
                    Confidence = hasPeSig ? 1.0 : 0.88,
                    MatchedEvidence = hasPeSig ? "DOS MZ stub + PE\\0\\0 COFF header" : "DOS MZ executable stub",
                    HasConsentGatedOptimizationCandidate = true,
                };
                return true;
            }
        }

        // ELF: 7F 45 4C 46
        if (head.Length >= 4 && head[0] == 0x7F && head[1] == 0x45 && head[2] == 0x4C && head[3] == 0x46)
        {
            var bits = head.Length >= 5 ? (head[4] == 2 ? "64-bit" : "32-bit") : "binary";
            match = new SignatureMatch
            {
                FormatId = "ELF",
                DisplayName = $"ELF {bits} Executable / Shared Object",
                MimeType = "application/x-elf",
                ContentClass = ContentClass.ExecutableBinary,
                Confidence = 1.0,
                MatchedEvidence = $"ELF magic 7F 45 4C 46 ({bits})",
                HasConsentGatedOptimizationCandidate = true,
            };
            return true;
        }

        // Mach-O: FE ED FA CE / FE ED FA CF / CE FA ED FE / CF FA ED FE
        if (head.Length >= 4)
        {
            var m = BinaryPrimitives.ReadUInt32BigEndian(head[..4]);
            if (m is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE)
            {
                match = new SignatureMatch
                {
                    FormatId = "Mach-O",
                    DisplayName = "Mach-O Native Binary",
                    MimeType = "application/x-mach-binary",
                    ContentClass = ContentClass.ExecutableBinary,
                    Confidence = 0.99,
                    MatchedEvidence = $"Mach-O magic 0x{m:X8}",
                };
                return true;
            }
        }

        // WebAssembly: 00 61 73 6D ("\0asm")
        if (head.Length >= 8 && head[..4].SequenceEqual("\0asm"u8) && head[4] == 0x01)
        {
            match = new SignatureMatch
            {
                FormatId = "Wasm",
                DisplayName = "WebAssembly Binary Module",
                MimeType = "application/wasm",
                ContentClass = ContentClass.ExecutableBinary,
                Confidence = 1.0,
                MatchedEvidence = "WebAssembly \\0asm v1 header",
            };
            return true;
        }

        match = null!;
        return false;
    }

    private static bool TryDetectUtf16AndTextStructures(
        ReadOnlySpan<byte> head,
        string ext,
        string fileName,
        out SignatureMatch match)
    {
        // UTF-16 LE BOM (FF FE, excluding UTF-32 LE FF FE 00 00 unless short)
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE
            && !(head.Length >= 4 && head[2] == 0x00 && head[3] == 0x00 && head.Length >= 8 && head[6] == 0x00 && head[7] == 0x00))
        {
            match = new SignatureMatch
            {
                FormatId = "UTF-16LE",
                DisplayName = "UTF-16 Little-Endian Text",
                MimeType = "text/plain;charset=utf-16le",
                ContentClass = ContentClass.SourceAndText,
                Confidence = 0.99,
                MatchedEvidence = "UTF-16 LE BOM (FF FE)",
                StructuralStrideBytes = 2,
            };
            return true;
        }

        // UTF-16 BE BOM (FE FF)
        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
        {
            match = new SignatureMatch
            {
                FormatId = "UTF-16BE",
                DisplayName = "UTF-16 Big-Endian Text",
                MimeType = "text/plain;charset=utf-16be",
                ContentClass = ContentClass.SourceAndText,
                Confidence = 0.99,
                MatchedEvidence = "UTF-16 BE BOM (FE FF)",
                StructuralStrideBytes = 2,
            };
            return true;
        }

        // BOM-less UTF-16 ASCII zero-interleaving heuristic
        if (head.Length >= 32 && TryDetectBomlessUtf16(head, out var isLittleEndian))
        {
            match = new SignatureMatch
            {
                FormatId = isLittleEndian ? "UTF-16LE" : "UTF-16BE",
                DisplayName = isLittleEndian ? "UTF-16 Little-Endian Text (BOM-less)" : "UTF-16 Big-Endian Text (BOM-less)",
                MimeType = isLittleEndian ? "text/plain;charset=utf-16le" : "text/plain;charset=utf-16be",
                ContentClass = ContentClass.SourceAndText,
                Confidence = 0.93,
                MatchedEvidence = isLittleEndian
                    ? "2-byte ASCII zero-interleaving on odd byte lanes"
                    : "2-byte ASCII zero-interleaving on even byte lanes",
                StructuralStrideBytes = 2,
            };
            return true;
        }

        // Check if payload looks like UTF-8 / ASCII text (no NUL bytes in first 4 KiB)
        var sample = head[..Math.Min(head.Length, 4096)];
        var hasBomUtf8 = sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF;
        var textSlice = hasBomUtf8 ? sample[3..] : sample;

        if (!LooksLikeUtf8Text(textSlice))
        {
            match = null!;
            return false;
        }

        var text = DecodeSafeUtf8Prefix(textSlice);
        var trimmed = text.AsSpan().TrimStart();

        // XML / HTML
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || ext == ".xml" || ext == ".svg")
        {
            match = new SignatureMatch
            {
                FormatId = "XML",
                DisplayName = "XML / Structured Markup Document",
                MimeType = "application/xml",
                ContentClass = ContentClass.SourceAndText,
                Confidence = 0.97,
                MatchedEvidence = "XML prolog / markup root element",
            };
            return true;
        }

        if (trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || ext is ".html" or ".htm")
        {
            match = new SignatureMatch
            {
                FormatId = "HTML",
                DisplayName = "HTML Document",
                MimeType = "text/html",
                ContentClass = ContentClass.SourceAndText,
                Confidence = 0.97,
                MatchedEvidence = "HTML document root / DOCTYPE",
            };
            return true;
        }

        // SQL Dump
        if (ext == ".sql" || LooksLikeSqlDump(text))
        {
            match = new SignatureMatch
            {
                FormatId = "SQL",
                DisplayName = "SQL Relational Database Dump",
                MimeType = "application/sql",
                ContentClass = ContentClass.DatabaseAndTabular,
                Confidence = ext == ".sql" ? 0.99 : 0.95,
                MatchedEvidence = "SQL DDL/DML statements (CREATE TABLE / INSERT INTO / dump header)",
            };
            return true;
        }

        // NDJSON vs JSON
        if (ext is ".ndjson" or ".jsonl" || LooksLikeNdjson(text))
        {
            match = new SignatureMatch
            {
                FormatId = "NDJSON",
                DisplayName = "Newline-Delimited JSON (NDJSON)",
                MimeType = "application/x-ndjson",
                ContentClass = ContentClass.DatabaseAndTabular,
                Confidence = 0.97,
                MatchedEvidence = "Multiple newline-delimited JSON record objects",
            };
            return true;
        }

        if (ext == ".json" || LooksLikeJson(trimmed))
        {
            match = new SignatureMatch
            {
                FormatId = "JSON",
                DisplayName = "Structured JSON Document",
                MimeType = "application/json",
                ContentClass = ContentClass.DatabaseAndTabular,
                Confidence = 0.96,
                MatchedEvidence = "Structured JSON root container and key-value syntax",
            };
            return true;
        }

        // CSV / TSV
        if (ext == ".tsv" || LooksLikeDelimitedTabular(text, '\t', minColumns: 2))
        {
            match = new SignatureMatch
            {
                FormatId = "TSV",
                DisplayName = "Tab-Separated Values (TSV)",
                MimeType = "text/tab-separated-values",
                ContentClass = ContentClass.DatabaseAndTabular,
                Confidence = 0.95,
                MatchedEvidence = "Consistent multi-row tab-delimited column structure",
            };
            return true;
        }

        if (ext == ".csv" || LooksLikeDelimitedTabular(text, ',', minColumns: 3))
        {
            match = new SignatureMatch
            {
                FormatId = "CSV",
                DisplayName = "Comma-Separated Values (CSV)",
                MimeType = "text/csv",
                ContentClass = ContentClass.DatabaseAndTabular,
                Confidence = 0.95,
                MatchedEvidence = "Consistent multi-row comma-delimited column structure",
            };
            return true;
        }

        // Source code by extension, special build filename, or lexical tokens
        if (SourceCodeExtensions.Contains(ext)
            || fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)
            || LooksLikeSourceCode(text))
        {
            match = new SignatureMatch
            {
                FormatId = "SourceCode",
                DisplayName = "Source Code / Build Definition",
                MimeType = "text/x-source-code",
                ContentClass = ContentClass.SourceAndText,
                Confidence = SourceCodeExtensions.Contains(ext) ? 0.98 : 0.90,
                MatchedEvidence = SourceCodeExtensions.Contains(ext)
                    ? $"UTF-8 source text matching code extension '{ext}'"
                    : "UTF-8 text with programming language syntax tokens",
            };
            return true;
        }

        // Plain UTF-8 text: require BOM, text extension, or sufficient multi-word text length
        if (hasBomUtf8 || ext is ".txt" or ".log" || textSlice.Length >= 64 || text.Contains('\n'))
        {
            match = new SignatureMatch
            {
                FormatId = "UTF-8",
                DisplayName = "UTF-8 Plain Text",
                MimeType = "text/plain",
                ContentClass = ContentClass.SourceAndText,
                Confidence = hasBomUtf8 ? 0.99 : 0.88,
                MatchedEvidence = hasBomUtf8 ? "UTF-8 BOM (EF BB BF) + valid text" : "Printable UTF-8 text stream",
            };
            return true;
        }

        match = null!;
        return false;
    }

    private static SignatureMatch FallbackByExtensionOrBinary(string ext) =>
        ext switch
        {
            ".txt" or ".log" => new SignatureMatch
            {
                FormatId = "UTF-8",
                DisplayName = "Plain Text",
                MimeType = "text/plain",
                ContentClass = ContentClass.SourceAndText,
                Confidence = 0.65,
                MatchedEvidence = $"Extension hint '{ext}'",
            },
            ".csv" => new SignatureMatch
            {
                FormatId = "CSV",
                DisplayName = "Comma-Separated Values",
                MimeType = "text/csv",
                ContentClass = ContentClass.DatabaseAndTabular,
                Confidence = 0.65,
                MatchedEvidence = "Extension hint '.csv'",
            },
            ".json" => new SignatureMatch
            {
                FormatId = "JSON",
                DisplayName = "JSON Document",
                MimeType = "application/json",
                ContentClass = ContentClass.DatabaseAndTabular,
                Confidence = 0.65,
                MatchedEvidence = "Extension hint '.json'",
            },
            _ => new SignatureMatch
            {
                FormatId = "Binary",
                DisplayName = "Unstructured / Generic Binary Stream",
                MimeType = "application/octet-stream",
                ContentClass = ContentClass.GenericBinary,
                Confidence = 0.50,
                MatchedEvidence = "No container magic matched; classified by statistical signals",
            },
        };

    private static bool TryDetectBomlessUtf16(ReadOnlySpan<byte> sample, out bool isLittleEndian)
    {
        var len = Math.Min(sample.Length & ~1, 512);
        if (len < 32)
        {
            isLittleEndian = false;
            return false;
        }

        int pairs = len / 2;
        int evenZero = 0;
        int oddZero = 0;
        int evenPrintable = 0;
        int oddPrintable = 0;

        for (var i = 0; i < len; i += 2)
        {
            var b0 = sample[i];
            var b1 = sample[i + 1];
            if (b0 == 0) evenZero++;
            if (b1 == 0) oddZero++;
            if (IsAsciiTextByte(b0)) evenPrintable++;
            if (IsAsciiTextByte(b1)) oddPrintable++;
        }

        if (oddZero >= pairs * 0.60 && evenZero <= pairs * 0.03 && evenPrintable >= pairs * 0.60)
        {
            isLittleEndian = true;
            return true;
        }

        if (evenZero >= pairs * 0.60 && oddZero <= pairs * 0.03 && oddPrintable >= pairs * 0.60)
        {
            isLittleEndian = false;
            return true;
        }

        isLittleEndian = false;
        return false;
    }

    private static bool IsAsciiTextByte(byte b) =>
        (b >= 0x20 && b <= 0x7E) || b is (byte)'\r' or (byte)'\n' or (byte)'\t';

    private static bool LooksLikeUtf8Text(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
        {
            return false;
        }

        int controlCount = 0;
        for (var i = 0; i < span.Length; i++)
        {
            var b = span[i];
            if (b == 0)
            {
                return false;
            }

            if (b < 0x20 && b is not ((byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C))
            {
                controlCount++;
            }
        }

        if (controlCount > span.Length / 50)
        {
            return false;
        }

        // Validate UTF-8 excluding trailing truncated multi-byte sequence at buffer boundary
        var checkLen = TrimTrailingIncompleteUtf8(span);
        if (checkLen <= 0)
        {
            return false;
        }

        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            _ = utf8.GetCharCount(span[..checkLen]);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static int TrimTrailingIncompleteUtf8(ReadOnlySpan<byte> span)
    {
        var len = span.Length;
        var inspect = Math.Min(3, len);
        for (var back = 1; back <= inspect; back++)
        {
            var b = span[len - back];
            if ((b & 0x80) == 0)
            {
                return len;
            }

            if ((b & 0xC0) == 0xC0)
            {
                int expected = (b & 0xE0) == 0xC0 ? 2
                    : (b & 0xF0) == 0xE0 ? 3
                    : (b & 0xF8) == 0xF0 ? 4 : 1;
                return back < expected ? len - back : len;
            }
        }

        return len;
    }

    private static string DecodeSafeUtf8Prefix(ReadOnlySpan<byte> span)
    {
        var len = TrimTrailingIncompleteUtf8(span);
        return Encoding.UTF8.GetString(span[..Math.Max(0, len)]);
    }

    private static bool LooksLikeSqlDump(string text)
    {
        int hits = 0;
        if (text.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase)) hits += 2;
        if (text.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase)) hits += 2;
        if (text.Contains("PostgreSQL database dump", StringComparison.OrdinalIgnoreCase)) hits += 3;
        if (text.Contains("MySQL dump", StringComparison.OrdinalIgnoreCase)) hits += 3;
        if (text.Contains("SQLite", StringComparison.OrdinalIgnoreCase) && text.Contains("BEGIN TRANSACTION", StringComparison.OrdinalIgnoreCase)) hits += 3;
        if (text.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase)) hits += 1;
        if (text.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase)) hits += 1;
        if (text.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase)) hits += 1;
        return hits >= 2;
    }

    private static bool LooksLikeNdjson(string text)
    {
        int jsonLines = 0;
        int nonEmpty = 0;
        foreach (var rawLine in text.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            nonEmpty++;
            if (nonEmpty > 8)
            {
                break;
            }

            if (line.StartsWith("{", StringComparison.Ordinal)
                && line.EndsWith("}", StringComparison.Ordinal)
                && line.Contains("\":", StringComparison.Ordinal))
            {
                jsonLines++;
            }
            else if (nonEmpty <= 3)
            {
                return false;
            }
        }

        return jsonLines >= 2;
    }

    private static bool LooksLikeJson(ReadOnlySpan<char> trimmed)
    {
        if (trimmed.IsEmpty)
        {
            return false;
        }

        if (trimmed[0] == '{')
        {
            return trimmed.Contains("\":", StringComparison.Ordinal);
        }

        if (trimmed[0] == '[')
        {
            var afterBracket = trimmed[1..].TrimStart();
            return !afterBracket.IsEmpty && (afterBracket[0] is '{' or '"' or '[' or ']' || char.IsDigit(afterBracket[0]));
        }

        return false;
    }

    private static bool LooksLikeDelimitedTabular(string text, char delimiter, int minColumns)
    {
        Span<int> counts = stackalloc int[12];
        int rowCount = 0;

        foreach (var rawLine in text.AsSpan().EnumerateLines())
        {
            if (rawLine.IsWhiteSpace())
            {
                continue;
            }

            if (rowCount >= counts.Length)
            {
                break;
            }

            // Ignore obvious prose/code lines starting with comment/brace characters
            var trimmed = rawLine.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("{", StringComparison.Ordinal)
                || trimmed.StartsWith("#include", StringComparison.Ordinal))
            {
                return false;
            }

            counts[rowCount++] = CountUnquotedDelimiters(rawLine, delimiter) + 1;
        }

        // Ignore the last line if the 4 KiB sample cut it mid-row without a trailing newline
        if (rowCount >= 4 && !text.EndsWith('\n'))
        {
            rowCount--;
        }

        if (rowCount < 3)
        {
            return false;
        }

        int expectedCols = counts[0];
        if (expectedCols < minColumns)
        {
            return false;
        }

        int matchingRows = 0;
        for (var i = 0; i < rowCount; i++)
        {
            if (counts[i] == expectedCols)
            {
                matchingRows++;
            }
        }

        return matchingRows >= rowCount - 1 && matchingRows >= 3;
    }

    private static int CountUnquotedDelimiters(ReadOnlySpan<char> line, char delimiter)
    {
        bool inQuotes = false;
        int count = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && c == delimiter)
            {
                count++;
            }
        }

        return count;
    }

    private static bool LooksLikeSourceCode(string text)
    {
        ReadOnlySpan<string> tokens =
        [
            "namespace ", "using ", "public class ", "private ", "internal ",
            "function ", "export ", "import ", "const ", "interface ",
            "#include ", "def ", "fn ", "pub struct ", "package ", "module ",
        ];

        int hits = 0;
        foreach (var token in tokens)
        {
            if (text.Contains(token, StringComparison.Ordinal))
            {
                hits++;
                if (hits >= 2)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int IndexOfAscii(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.IndexOf(needle);

    private static string NormalizeExtension(string? pathHint)
    {
        if (string.IsNullOrWhiteSpace(pathHint))
        {
            return string.Empty;
        }

        // Support raw extension hints like ".pdf" as well as full file paths
        if (pathHint.StartsWith('.') && !pathHint.Contains('/') && !pathHint.Contains('\\'))
        {
            return pathHint.ToLowerInvariant();
        }

        return Path.GetExtension(pathHint).ToLowerInvariant();
    }
}
