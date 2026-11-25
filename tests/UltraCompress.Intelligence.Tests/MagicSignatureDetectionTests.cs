using System.Buffers.Binary;
using System.Text;
using UltraCompress.Core.Intelligence;

namespace UltraCompress.Intelligence.Tests;

public sealed class MagicSignatureDetectionTests
{
    [Fact]
    public void Catalog_RegistersMoreThan30DistinctFormatSignatures()
    {
        Assert.True(MagicSignatureCatalog.SupportedFormats.Count >= 35,
            $"Expected >= 35 format signatures, got {MagicSignatureCatalog.SupportedFormats.Count}.");

        var uniqueIds = MagicSignatureCatalog.SupportedFormats
            .Select(f => f.FormatId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        Assert.Equal(MagicSignatureCatalog.SupportedFormats.Count, uniqueIds);
    }

    public static TheoryData<string, byte[], byte[], string?, string, ContentClass> AllSignatureCases()
    {
        var data = new TheoryData<string, byte[], byte[], string?, string, ContentClass>();

        // --- Video (7) ---
        data.Add("MP4-isom", BuildFtyp("isom", "iso2", "mp41"), [], "movie.bin", "MP4", ContentClass.CompressedVideo);
        data.Add("MOV-qt", BuildFtyp("qt  "), [], "clip.bin", "MOV", ContentClass.CompressedVideo);
        data.Add("MKV-ebml", Combine([0x1A, 0x45, 0xDF, 0xA3, 0x42, 0x82, 0x88], Ascii("matroska")), [], null, "MKV", ContentClass.CompressedVideo);
        data.Add("WebM-ebml", Combine([0x1A, 0x45, 0xDF, 0xA3, 0x42, 0x82, 0x84], Ascii("webm")), [], null, "WebM", ContentClass.CompressedVideo);
        data.Add("AVI-riff", BuildRiff("AVI "), [], null, "AVI", ContentClass.CompressedVideo);
        data.Add("FLV", [0x46, 0x4C, 0x56, 0x01, 0x05, 0x00, 0x00, 0x00, 0x09], [], null, "FLV", ContentClass.CompressedVideo);
        data.Add("MPEG-TS", BuildMpegTsPackets(), [], null, "MPEG-TS", ContentClass.CompressedVideo);

        // --- Audio (6) ---
        data.Add("MP3-ID3", Combine(Ascii("ID3"), [0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10]), [], null, "MP3", ContentClass.CompressedAudio);
        data.Add("MP3-Sync", [0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00, 0x00, 0x00], [], null, "MP3", ContentClass.CompressedAudio);
        data.Add("FLAC", Combine(Ascii("fLaC"), [0x00, 0x00, 0x00, 0x22]), [], null, "FLAC", ContentClass.CompressedAudio);
        data.Add("OGG", Combine(Ascii("OggS"), [0x00, 0x02, 0x00, 0x00]), [], null, "OGG", ContentClass.CompressedAudio);
        data.Add("WAV-PCM", BuildWavPcmHeader(channels: 2, bitsPerSample: 16), [], null, "WAV-PCM", ContentClass.UncompressedAudio);
        data.Add("M4A", BuildFtyp("M4A ", "mp42"), [], null, "M4A", ContentClass.CompressedAudio);
        data.Add("AIFF", BuildIffForm("AIFF"), [], null, "AIFF", ContentClass.UncompressedAudio);

        // --- Images & Photos (12) ---
        data.Add("JPEG-Exif", Combine([0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x16], Ascii("Exif\0\0II*\0")), [], "IMG_0001.JPG", "JPEG", ContentClass.CompressedImage);
        data.Add("PNG", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D], [], null, "PNG", ContentClass.CompressedImage);
        data.Add("WebP", BuildRiff("WEBP"), [], null, "WebP", ContentClass.CompressedImage);
        data.Add("HEIC", BuildFtyp("heic", "mif1"), [], "IMG_2048.HEIC", "HEIC", ContentClass.CompressedImage);
        data.Add("AVIF", BuildFtyp("avif", "mif1"), [], null, "AVIF", ContentClass.CompressedImage);
        data.Add("GIF", Ascii("GIF89a\x10\x00\x10\x00"), [], null, "GIF", ContentClass.CompressedImage);
        data.Add("BMP", BuildBmpHeader(width: 640, height: 480, bpp: 24), [], null, "BMP", ContentClass.UncompressedRasterImage);
        data.Add("TIFF-LE", BuildTiffIfdHeader(includeDngTag: false, compression: 1), [], "scan.tif", "TIFF", ContentClass.UncompressedRasterImage);
        data.Add("DNG-IFD", BuildTiffIfdHeader(includeDngTag: true, compression: 1), [], "frame.bin", "DNG", ContentClass.UncompressedRasterImage);
        data.Add("RAW-CR2", [0x49, 0x49, 0x2A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x43, 0x52, 0x02, 0x00], [], "IMG.CR2", "RAW", ContentClass.UncompressedRasterImage);
        data.Add("RAW-CR3", BuildFtyp("crx ", "isom"), [], "IMG.CR3", "RAW", ContentClass.UncompressedRasterImage);
        data.Add("RAW-RAF", Ascii("FUJIFILMCCD-RAW 0201FF393501"), [], "DSCF0001.RAF", "RAW", ContentClass.UncompressedRasterImage);
        data.Add("RAW-ORF", [0x49, 0x49, 0x52, 0x4F, 0x08, 0x00, 0x00, 0x00], [], "P1010001.ORF", "RAW", ContentClass.UncompressedRasterImage);

        // --- Databases & Structured Data (8) ---
        data.Add("SQLite3", BuildSqliteHeader(pageSize: 4096), [], "app.db", "SQLite", ContentClass.DatabaseAndTabular);
        data.Add("Parquet", Ascii("PAR1\x15\x04\x15\x10"), Ascii("metaPAR1"), "events.parquet", "Parquet", ContentClass.DatabaseAndTabular);
        data.Add("Arrow", Ascii("ARROW1\0\0\xFF\xFF\xFF\xFF"), [], "table.arrow", "Arrow", ContentClass.DatabaseAndTabular);
        data.Add("SQL-Dump", Ascii("-- PostgreSQL database dump\nCREATE TABLE users (id BIGINT PRIMARY KEY, email TEXT);\nINSERT INTO users VALUES (1, 'a@b.com');\n"), [], null, "SQL", ContentClass.DatabaseAndTabular);
        data.Add("NDJSON", Ascii("{\"id\":1,\"event\":\"click\",\"ts\":1700000001}\n{\"id\":2,\"event\":\"view\",\"ts\":1700000002}\n{\"id\":3,\"event\":\"buy\",\"ts\":1700000003}\n"), [], null, "NDJSON", ContentClass.DatabaseAndTabular);
        data.Add("JSON", Ascii("{\n  \"schemaVersion\": 2,\n  \"records\": [{\"id\": 1, \"name\": \"alpha\"}]\n}\n"), [], null, "JSON", ContentClass.DatabaseAndTabular);
        data.Add("CSV", Ascii("user_id,org_id,created_at,amount_cents,currency\n1001,42,2026-11-24T00:00:00Z,1999,USD\n1002,42,2026-11-24T00:00:01Z,4999,USD\n1003,43,2026-11-24T00:00:02Z,999,EUR\n"), [], null, "CSV", ContentClass.DatabaseAndTabular);
        data.Add("TSV", Ascii("user_id\torg_id\tamount_cents\tstatus\n1001\t42\t1999\tpaid\n1002\t42\t4999\tpaid\n1003\t43\t999\tpending\n"), [], null, "TSV", ContentClass.DatabaseAndTabular);

        // --- Binaries & Executables (6) ---
        data.Add("PE-Native", BuildPeHeader(includeClrDirectory: false), [], "native.exe", "PE", ContentClass.ExecutableBinary);
        data.Add("DotNet-PE", BuildPeHeader(includeClrDirectory: true), [], "Managed.dll", "DotNetAssembly", ContentClass.ExecutableBinary);
        data.Add("DotNet-BSJB", Combine(Ascii("BSJB"), [0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00]), [], "symbols.pdb", "DotNetMetadata", ContentClass.ExecutableBinary);
        data.Add("ELF-64", [0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00], [], "app.so", "ELF", ContentClass.ExecutableBinary);
        data.Add("Mach-O", [0xCF, 0xFA, 0xED, 0xFE, 0x0C, 0x00, 0x00, 0x01], [], "libfoo.dylib", "Mach-O", ContentClass.ExecutableBinary);
        data.Add("Wasm", [0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00], [], "module.wasm", "Wasm", ContentClass.ExecutableBinary);

        // --- Archives & Containers (10) ---
        data.Add("ZIP", [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00], [], null, "ZIP", ContentClass.CompressedArchive);
        data.Add("7z", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04], [], null, "7z", ContentClass.CompressedArchive);
        data.Add("Zstd", [0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x00, 0x01, 0x00], [], null, "Zstd", ContentClass.CompressedArchive);
        data.Add("GZip", [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00], [], null, "GZip", ContentClass.CompressedArchive);
        data.Add("XZ", [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0x00, 0x04], [], null, "XZ", ContentClass.CompressedArchive);
        data.Add("BZip2", Ascii("BZh91AY&SY"), [], null, "BZip2", ContentClass.CompressedArchive);
        data.Add("LZ4", [0x04, 0x22, 0x4D, 0x18, 0x60, 0x70, 0x73, 0x00], [], null, "LZ4", ContentClass.CompressedArchive);
        data.Add("RAR5", [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00], [], null, "RAR", ContentClass.CompressedArchive);
        data.Add("TAR", BuildTarHeader(), [], null, "TAR", ContentClass.ContainerDocument);
        data.Add("PDF", Ascii("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\n"), Ascii("%%EOF\n"), null, "PDF", ContentClass.ContainerDocument);

        // --- Source Code & Text (6) ---
        data.Add("CSharpSource", Ascii("using System;\nnamespace Demo;\npublic class Widget { private readonly int _id; }\n"), [], "Widget.cs", "SourceCode", ContentClass.SourceAndText);
        data.Add("XML", Ascii("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n"), [], null, "XML", ContentClass.SourceAndText);
        data.Add("HTML", Ascii("<!DOCTYPE html>\n<html lang=\"en\"><head><title>UltraCompress</title></head></html>\n"), [], null, "HTML", ContentClass.SourceAndText);
        data.Add("UTF-16LE-BOM", Combine([0xFF, 0xFE], Encoding.Unicode.GetBytes("UltraCompress UTF-16LE sample line\r\n")), [], null, "UTF-16LE", ContentClass.SourceAndText);
        data.Add("UTF-16BE-BOM", Combine([0xFE, 0xFF], Encoding.BigEndianUnicode.GetBytes("UltraCompress UTF-16BE sample line\r\n")), [], null, "UTF-16BE", ContentClass.SourceAndText);
        data.Add("UTF-8-Text", Combine([0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes("Plain UTF-8 prose note with accented characters: café, naïve, résumé.\n")), [], "notes.txt", "UTF-8", ContentClass.SourceAndText);

        return data;
    }

    [Theory]
    [MemberData(nameof(AllSignatureCases))]
    public void Detect_IdentifiesAllSupportedSignaturesAndClasses(
        string caseName,
        byte[] head,
        byte[] tail,
        string? pathHint,
        string expectedFormatId,
        ContentClass expectedClass)
    {
        var match = MagicSignatureCatalog.Detect(head, tail, Math.Max(head.Length + tail.Length, 4096), pathHint);

        Assert.True(string.Equals(expectedFormatId, match.FormatId, StringComparison.OrdinalIgnoreCase),
            $"[{caseName}] Expected FormatId '{expectedFormatId}', got '{match.FormatId}' ({match.MatchedEvidence}).");
        Assert.Equal(expectedClass, match.ContentClass);
        Assert.True(match.Confidence >= 0.85, $"[{caseName}] Expected high confidence, got {match.Confidence}.");
        Assert.False(string.IsNullOrWhiteSpace(match.MimeType));
    }

    [Fact]
    public void Detect_SqliteHeader_ExtractsExactPageSize()
    {
        var header = BuildSqliteHeader(pageSize: 16384);
        var match = MagicSignatureCatalog.Detect(header, [], 10_000_000, "analytics.sqlite");

        Assert.Equal("SQLite", match.FormatId);
        Assert.Equal(16384, match.StructuralStrideBytes);
        Assert.Contains("16384-byte", match.MatchedEvidence);
    }

    [Fact]
    public void Detect_JpegWithExif_FlagsConsentGatedMetadataCandidate()
    {
        var jpeg = Combine([0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x16], Ascii("Exif\0\0II*\0"));
        var match = MagicSignatureCatalog.Detect(jpeg, [], 4_000_000, "DSC0001.JPG");

        Assert.Equal("JPEG", match.FormatId);
        Assert.True(match.HasConsentGatedOptimizationCandidate);
        Assert.Contains("Exif", match.MatchedEvidence);
    }

    internal static byte[] BuildFtyp(string majorBrand, params string[] compatibleBrands)
    {
        var totalLen = 16 + (compatibleBrands.Length * 4);
        var buf = new byte[totalLen];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)totalLen);
        Ascii("ftyp").CopyTo(buf.AsSpan(4, 4));
        Ascii(majorBrand.PadRight(4)[..4]).CopyTo(buf.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), 0x00000200);
        for (var i = 0; i < compatibleBrands.Length; i++)
        {
            Ascii(compatibleBrands[i].PadRight(4)[..4]).CopyTo(buf.AsSpan(16 + (i * 4), 4));
        }

        return buf;
    }

    internal static byte[] BuildRiff(string formType)
    {
        var buf = new byte[32];
        Ascii("RIFF").CopyTo(buf.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), 1024);
        Ascii(formType).CopyTo(buf.AsSpan(8, 4));
        return buf;
    }

    internal static byte[] BuildIffForm(string formType)
    {
        var buf = new byte[24];
        Ascii("FORM").CopyTo(buf.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), 1024);
        Ascii(formType).CopyTo(buf.AsSpan(8, 4));
        return buf;
    }

    internal static byte[] BuildWavPcmHeader(ushort channels, ushort bitsPerSample)
    {
        var buf = new byte[44];
        Ascii("RIFF").CopyTo(buf.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), 1_000_000);
        Ascii("WAVE").CopyTo(buf.AsSpan(8, 4));
        Ascii("fmt ").CopyTo(buf.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20, 2), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24, 4), 48000);
        var blockAlign = (ushort)(channels * (bitsPerSample / 8));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28, 4), (uint)(48000 * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(32, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(34, 2), bitsPerSample);
        Ascii("data").CopyTo(buf.AsSpan(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(40, 4), 999_956);
        return buf;
    }

    internal static byte[] BuildBmpHeader(int width, int height, ushort bpp)
    {
        var buf = new byte[54];
        buf[0] = (byte)'B';
        buf[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(2, 4), (uint)(54 + (width * height * (bpp / 8))));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(10, 4), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(14, 4), 40); // BITMAPINFOHEADER
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(22, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(28, 2), bpp);
        return buf;
    }

    internal static byte[] BuildTiffIfdHeader(bool includeDngTag, ushort compression)
    {
        ushort entryCount = includeDngTag ? (ushort)3 : (ushort)2;
        var buf = new byte[8 + 2 + (entryCount * 12) + 4];
        buf[0] = 0x49;
        buf[1] = 0x49;
        buf[2] = 0x2A;
        buf[3] = 0x00;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8, 2), entryCount);

        WriteIfdEntryLe(buf.AsSpan(10, 12), tag: 0x0103, type: 3, count: 1, valueShort: compression);
        WriteIfdEntryLe(buf.AsSpan(22, 12), tag: 0x0115, type: 3, count: 1, valueShort: 3);
        if (includeDngTag)
        {
            WriteIfdEntryLe(buf.AsSpan(34, 12), tag: 0xC612, type: 1, count: 4, valueShort: 0x0401);
        }

        return buf;
    }

    private static void WriteIfdEntryLe(Span<byte> entry, ushort tag, ushort type, uint count, ushort valueShort)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(entry[..2], tag);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(2, 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(4, 4), count);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(8, 2), valueShort);
    }

    internal static byte[] BuildSqliteHeader(ushort pageSize)
    {
        var buf = new byte[100];
        Ascii("SQLite format 3\0").CopyTo(buf.AsSpan(0, 16));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(16, 2), pageSize);
        buf[18] = 1;
        buf[19] = 1;
        return buf;
    }

    internal static byte[] BuildPeHeader(bool includeClrDirectory)
    {
        var buf = new byte[512];
        buf[0] = 0x4D; // 'M'
        buf[1] = 0x5A; // 'Z'
        const int peOffset = 0x80;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0x3C, 4), peOffset);
        Ascii("PE\0\0").CopyTo(buf.AsSpan(peOffset, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(peOffset + 4, 2), 0x8664); // AMD64
        var optOffset = peOffset + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(optOffset, 2), 0x020B); // PE32+
        if (includeClrDirectory)
        {
            var clrDirOffset = optOffset + 224;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(clrDirOffset, 4), 0x2000);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(clrDirOffset + 4, 4), 0x48);
            Ascii("BSJB").CopyTo(buf.AsSpan(440, 4));
        }

        return buf;
    }

    private static byte[] BuildMpegTsPackets()
    {
        var buf = new byte[564];
        buf[0] = 0x47;
        buf[188] = 0x47;
        buf[376] = 0x47;
        return buf;
    }

    private static byte[] BuildTarHeader()
    {
        var buf = new byte[512];
        Ascii("src/Program.cs").CopyTo(buf.AsSpan(0, 14));
        Ascii("ustar\0").CopyTo(buf.AsSpan(257, 6));
        return buf;
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Combine(byte[] first, byte[] second)
    {
        var combined = new byte[first.Length + second.Length];
        first.CopyTo(combined, 0);
        second.CopyTo(combined, first.Length);
        return combined;
    }
}
