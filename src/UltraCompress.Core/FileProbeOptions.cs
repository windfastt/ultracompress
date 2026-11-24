namespace UltraCompress.Core;

public sealed class FileProbeOptions
{
    /// <summary>Block size for per-block entropy (default 64 KiB).</summary>
    public int BlockSizeBytes { get; init; } = 64 * 1024;

    /// <summary>When true, compute SHA-256 over the full file via streaming.</summary>
    public bool ComputeContentHash { get; init; } = true;

    /// <summary>
    /// For files larger than this, block entropy uses evenly spaced samples instead of scanning every block.
    /// </summary>
    public long FullBlockScanMaxBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Maximum block entropy entries returned for very large files.</summary>
    public int MaxBlockSamples { get; init; } = 32;
}