namespace UltraCompress.Core.Probe;

/// <summary>
/// Phase 3 probe stub: fills filesystem-derived fields on <see cref="FileFingerprint"/>.
/// Shannon entropy and content hashing are reserved for a later native analyze step.
/// </summary>
public static class FileFingerprintEnricher
{
    /// <summary>
    /// Entropy is intentionally unset until a full probe backend is wired.
    /// Callers should treat <see cref="FileFingerprint.EntropyBitsPerByte"/> as unknown when null.
    /// </summary>
    public static FileFingerprint EnrichFromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Cannot probe missing file.", path);
        }

        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);

        return new FileFingerprint
        {
            Path = fullPath,
            SizeBytes = info.Length,
            LastModifiedUtc = info.LastWriteTimeUtc,
            EntropyBitsPerByte = null,
        };
    }

    /// <summary>
    /// Applies stub enrichment to an existing fingerprint (re-reads size/timestamp from <paramref name="seed"/>.<see cref="FileFingerprint.Path"/> when the file exists).
    /// </summary>
    public static FileFingerprint Enrich(FileFingerprint seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        if (!File.Exists(seed.Path))
        {
            return seed with { EntropyBitsPerByte = null };
        }

        var info = new FileInfo(seed.Path);
        return seed with
        {
            SizeBytes = info.Length,
            LastModifiedUtc = info.LastWriteTimeUtc,
            EntropyBitsPerByte = null,
        };
    }
}