namespace UltraCompress.Core.Probe;

/// <summary>
/// Back-compat facade over <see cref="FileProbe"/> for callers that used the Phase 3 stub API.
/// </summary>
public static class FileFingerprintEnricher
{
    public static FileFingerprint EnrichFromPath(string path) =>
        FileProbe.ProbeAsync(path).GetAwaiter().GetResult();

    public static FileFingerprint Enrich(FileFingerprint seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        if (!File.Exists(seed.Path))
        {
            return seed with { EntropyBitsPerByte = null };
        }

        return FileProbe.ProbeAsync(seed.Path).GetAwaiter().GetResult();
    }
}