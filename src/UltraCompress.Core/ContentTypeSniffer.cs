namespace UltraCompress.Core;

/// <summary>
/// Lightweight magic-byte and structure sniffing (TrID/libmagic-style subset).
/// </summary>
public static class ContentTypeSniffer
{
    public static string Sniff(ReadOnlySpan<byte> header, string? extensionHint)
    {
        if (header.Length >= 4 && header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46)
        {
            return "application/pdf";
        }

        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return "image/png";
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (header.Length >= 4 && header[0] == 0x50 && header[1] == 0x4B
            && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07)
            && (header[3] == 0x04 || header[3] == 0x06 || header[3] == 0x08))
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

        if (header.Length >= 4 && header[0] == 0x37 && header[1] == 0x7A
            && header[2] == 0xBC && header[3] == 0xAF)
        {
            return "application/x-7z-compressed";
        }

        if (header.Length >= 2 && header[0] == 0x4D && header[1] == 0x5A)
        {
            return "application/x-msdownload";
        }

        if (header.Length >= 16
            && header[0] == 0x53 && header[1] == 0x51 && header[2] == 0x4C && header[3] == 0x69
            && header[4] == 0x74 && header[5] == 0x65 && header[6] == 0x20 && header[7] == 0x66
            && header[8] == 0x6F && header[9] == 0x72 && header[10] == 0x6D && header[11] == 0x61
            && header[12] == 0x74 && header[13] == 0x20 && header[14] == 0x33)
        {
            return "application/x-sqlite3";
        }

        if (header.Length >= 4 && header[0] == 0x7F && header[1] == 0x45 && header[2] == 0x4C && header[3] == 0x46)
        {
            return "application/x-elf";
        }

        return extensionHint switch
        {
            ".txt" or ".log" or ".csv" or ".md" => "text/plain",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            _ => "application/octet-stream",
        };
    }
}