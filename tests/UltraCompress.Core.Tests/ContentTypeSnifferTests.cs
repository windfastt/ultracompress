using UltraCompress.Core;

namespace UltraCompress.Core.Tests;

public class ContentTypeSnifferTests
{
    [Fact]
    public void Sniff_PdfMagic_ReturnsPdfMime()
    {
        Span<byte> header = stackalloc byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 };
        Assert.Equal("application/pdf", ContentTypeSniffer.Sniff(header, ".bin"));
    }

    [Fact]
    public void Sniff_PngMagic_ReturnsPngMime()
    {
        var header = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        };
        Assert.Equal("image/png", ContentTypeSniffer.Sniff(header, ".png"));
    }

    [Fact]
    public void Sniff_GzipMagic_ReturnsGzipMime()
    {
        Span<byte> header = stackalloc byte[] { 0x1F, 0x8B, 0x08, 0x00 };
        Assert.Equal("application/gzip", ContentTypeSniffer.Sniff(header, null));
    }

    [Fact]
    public void Sniff_ExtensionFallback_WhenNoMagic()
    {
        Span<byte> header = stackalloc byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F };
        Assert.Equal("text/plain", ContentTypeSniffer.Sniff(header, ".txt"));
        Assert.Equal("application/json", ContentTypeSniffer.Sniff(header, ".json"));
    }
}