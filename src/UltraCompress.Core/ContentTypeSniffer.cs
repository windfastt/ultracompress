using UltraCompress.Core.Intelligence;

namespace UltraCompress.Core;

/// <summary>
/// Magic-byte and container-structure sniffer backed by <see cref="MagicSignatureCatalog"/> (40+ signatures).
/// </summary>
public static class ContentTypeSniffer
{
    public static string Sniff(ReadOnlySpan<byte> header, string? extensionHint)
    {
        if (header.IsEmpty)
        {
            return FallbackMimeFromExtension(extensionHint);
        }

        var match = MagicSignatureCatalog.Detect(header, ReadOnlySpan<byte>.Empty, header.Length, extensionHint);
        if (match.FormatId == "SourceCode" && string.Equals(extensionHint, ".md", StringComparison.OrdinalIgnoreCase))
        {
            return "text/markdown";
        }

        return match.MimeType;
    }

    private static string FallbackMimeFromExtension(string? extensionHint) =>
        extensionHint?.ToLowerInvariant() switch
        {
            ".txt" or ".log" => "text/plain",
            ".csv" => "text/csv",
            ".tsv" => "text/tab-separated-values",
            ".md" => "text/markdown",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            _ => "application/octet-stream",
        };
}
