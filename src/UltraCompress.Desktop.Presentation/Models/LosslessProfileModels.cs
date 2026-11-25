using System.Globalization;

namespace UltraCompress.Desktop.Presentation.Models;

public sealed class LosslessProfileOption
{
    public const string CanonicalLosslessSlaBadge = "100% Bit-Exact Lossless";

    public DesktopLosslessProfile Profile { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string SlaBadge { get; init; } = CanonicalLosslessSlaBadge;

    public bool IsBitExactLossless => true;

    public int ThroughputFloorMbPerSec { get; init; }

    public int MemoryCapMb { get; init; }

    public int CandidateBudget { get; init; }

    public double IncompressibleEntropyGate { get; init; }

    public string PrimaryPipelineSummary { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public double CompressionEffortFactor { get; init; } = 1.0;

    public string SlaMetricsLine =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Floor ≥ {ThroughputFloorMbPerSec} MB/s · RAM ≤ {MemoryCapMb} MiB · Pilot budget: {CandidateBudget} candidate(s) · SHA-256 verified");
}

public static class LosslessProfileCatalog
{
    public static IReadOnlyList<LosslessProfileOption> All { get; } =
    [
        new LosslessProfileOption
        {
            Profile = DesktopLosslessProfile.Instant,
            DisplayName = "Instant",
            ThroughputFloorMbPerSec = 450,
            MemoryCapMb = 64,
            CandidateBudget = 1,
            IncompressibleEntropyGate = 7.75,
            PrimaryPipelineSummary = "FastStreamBypass + Zstd-1 + SHA-256 dedup",
            Description = "Near-disk-speed archival with immediate media bypass and content-addressed duplicate collapse.",
            CompressionEffortFactor = 0.72,
        },
        new LosslessProfileOption
        {
            Profile = DesktopLosslessProfile.Balanced,
            DisplayName = "Balanced",
            ThroughputFloorMbPerSec = 200,
            MemoryCapMb = 192,
            CandidateBudget = 1,
            IncompressibleEntropyGate = 7.85,
            PrimaryPipelineSummary = "Content-routed Zstd-4 solid blocks + media store bypass",
            Description = "Recommended everyday profile balancing fast multi-core throughput with strong solid compression.",
            CompressionEffortFactor = 0.88,
        },
        new LosslessProfileOption
        {
            Profile = DesktopLosslessProfile.Archive,
            DisplayName = "Archive",
            ThroughputFloorMbPerSec = 65,
            MemoryCapMb = 512,
            CandidateBudget = 3,
            IncompressibleEntropyGate = 7.90,
            PrimaryPipelineSummary = "Delta / ByteShuffle / x86 BCJ + Zstd-12 / Brotli-7",
            Description = "Structured preprocessors and long-window solid blocks for cold backups, source repos, and SQL dumps.",
            CompressionEffortFactor = 1.00,
        },
        new LosslessProfileOption
        {
            Profile = DesktopLosslessProfile.Tiny,
            DisplayName = "Tiny",
            ThroughputFloorMbPerSec = 18,
            MemoryCapMb = 1024,
            CandidateBudget = 6,
            IncompressibleEntropyGate = 7.94,
            PrimaryPipelineSummary = "UCB1 pilot tournament + Zstd-19 / LZMA2-7 + 16 MiB dictionary",
            Description = "High-ratio archival using chunk-level candidate pilots across Zstd, Brotli, and LZMA2.",
            CompressionEffortFactor = 1.12,
        },
        new LosslessProfileOption
        {
            Profile = DesktopLosslessProfile.Laboratory,
            DisplayName = "Laboratory",
            ThroughputFloorMbPerSec = 4,
            MemoryCapMb = 2048,
            CandidateBudget = 8,
            IncompressibleEntropyGate = 7.97,
            PrimaryPipelineSummary = "Exhaustive UCB1 tournament (LZMA2-9, Zstd-22, Brotli-11) + full preprocessors",
            Description = "Maximum bit-exact density for distribution artifacts and archival masters where ratio beats CPU time.",
            CompressionEffortFactor = 1.20,
        },
    ];

    public static LosslessProfileOption Get(DesktopLosslessProfile profile) =>
        All.First(p => p.Profile == profile);

    public static DesktopLosslessProfile ParseFromEngine(string? profileText)
    {
        if (string.IsNullOrWhiteSpace(profileText))
        {
            return DesktopLosslessProfile.Balanced;
        }

        if (Enum.TryParse<DesktopLosslessProfile>(profileText.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return profileText.Trim().ToLowerInvariant() switch
        {
            "small" => DesktopLosslessProfile.Archive,
            "experimental" => DesktopLosslessProfile.Laboratory,
            _ => DesktopLosslessProfile.Balanced,
        };
    }

    public static string ToEngineWireName(DesktopLosslessProfile profile) => profile.ToString();
}
