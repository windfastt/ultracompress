using UltraCompress.Core.Intelligence;

namespace UltraCompress.Core.Probe;

/// <summary>
/// Enriches file fingerprints and produces O(1) content-inspection results for the intelligent advisor.
/// </summary>
public static class FileFingerprintEnricher
{
    public static FileFingerprint EnrichFromPath(string path) =>
        FileProbe.ProbeAsync(path).GetAwaiter().GetResult();

    public static FileFingerprint EnrichFast(string path) =>
        FileProbe.ProbeAsync(
            path,
            new FileProbeOptions
            {
                ComputeContentHash = false,
                FullBlockScanMaxBytes = 64 * 1024,
                MaxBlockSamples = 5,
            }).GetAwaiter().GetResult();

    public static ContentInspectionResult Inspect(string path, ContentInspectorOptions? options = null) =>
        ContentInspector.InspectFile(path, options);

    public static FileFingerprint Enrich(FileFingerprint seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        if (!File.Exists(seed.Path))
        {
            return seed with { EntropyBitsPerByte = null, Inspection = null };
        }

        return FileProbe.ProbeAsync(seed.Path).GetAwaiter().GetResult();
    }
}
