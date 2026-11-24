using UltraCompress.Core;
using UltraCompress.Core.Probe;

namespace UltraCompress.Core.Tests;

public sealed class FileFingerprintEnricherTests
{
    [Fact]
    public void EnrichFromPath_SetsSizeAndLeavesEntropyPlaceholder()
    {
        var temp = Path.GetTempFileName();
        try
        {
            var payload = "ultracompress probe stub"u8.ToArray();
            File.WriteAllBytes(temp, payload);

            var fp = FileFingerprintEnricher.EnrichFromPath(temp);

            Assert.Equal(Path.GetFullPath(temp), fp.Path);
            Assert.Equal(payload.Length, fp.SizeBytes);
            Assert.Equal("tiny", fp.SizeClass);
            Assert.Null(fp.EntropyBitsPerByte);
            Assert.Null(fp.ContentHashSha256);
            Assert.Null(fp.DetectedType);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void EnrichFromPath_ThrowsWhenFileMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Throws<FileNotFoundException>(() => FileFingerprintEnricher.EnrichFromPath(missing));
    }

    [Fact]
    public void Enrich_RefreshesSizeFromDiskAndClearsEntropySlot()
    {
        var temp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(temp, "a");

            var seed = new FileFingerprint
            {
                Path = temp,
                SizeBytes = 0,
                LastModifiedUtc = DateTimeOffset.MinValue,
                EntropyBitsPerByte = 7.9,
            };

            var enriched = FileFingerprintEnricher.Enrich(seed);

            Assert.Equal(1, enriched.SizeBytes);
            Assert.Null(enriched.EntropyBitsPerByte);
            Assert.True(enriched.LastModifiedUtc > DateTimeOffset.MinValue);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Enrich_PreservesSeedWhenPathDoesNotExist()
    {
        var seed = new FileFingerprint
        {
            Path = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N")),
            SizeBytes = 42,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            EntropyBitsPerByte = 3.5,
        };

        var enriched = FileFingerprintEnricher.Enrich(seed);

        Assert.Equal(seed with { EntropyBitsPerByte = null }, enriched);
    }
}