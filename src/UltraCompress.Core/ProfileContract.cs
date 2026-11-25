using UltraCompress.Core.Safety;

namespace UltraCompress.Core;

/// <summary>
/// Concrete, inspectable outcome and resource SLA contract for a <see cref="CompressionProfile"/>.
/// Every profile enforces 100% bit-exact lossless compression with SHA-256 + XXH64 roundtrip verification.
/// </summary>
public sealed record ProfileContract
{
    public required CompressionProfile Profile { get; init; }

    /// <summary>All compression profiles execute strictly lossless compression.</summary>
    public OperationKind OperationKind => OperationKind.LosslessCompression;

    /// <summary>Shannon entropy threshold (bits/byte) at or above which uncorrelated streams bypass heavy codecs.</summary>
    public required double IncompressibleEntropyBypassBitsPerByte { get; init; }

    /// <summary>Compressed-to-original ratio at or above which output falls back to uncompressed Store (0.995).</summary>
    public double IncompressibleRatioThreshold { get; init; } = 0.995;

    /// <summary>Upper bound on working-set / dictionary memory budget in bytes.</summary>
    public required long MaxMemoryBudgetBytes { get; init; }

    /// <summary>Total pilot sample budget in bytes evaluated across multi-window chunks (0 for single-pass profiles).</summary>
    public required int PilotSampleBytes { get; init; }

    /// <summary>Number of spaced representative sample windows drawn from large streams during pilot evaluation.</summary>
    public required int PilotWindowCount { get; init; }

    /// <summary>Maximum candidate pipelines evaluated per stream.</summary>
    public required int CandidatePipelineBudget { get; init; }

    /// <summary>True when the profile runs multi-candidate UCB1/successive-halving tournament evaluation.</summary>
    public required bool UsesMultiCandidateTournament { get; init; }

    /// <summary>Target compression throughput floor on reference x64 hardware (MiB/s).</summary>
    public required double TargetThroughputFloorMiBPerSec { get; init; }

    /// <summary>Wall-clock search budget for candidate selection.</summary>
    public required TimeSpan SearchTimeBudget { get; init; }

    /// <summary>Mandatory bit-exact roundtrip verification (SHA-256 + XXH64 + byte equality).</summary>
    public bool MandatoryBitExactVerify { get; init; } = true;

    public bool VerifySha256 { get; init; } = true;

    public bool VerifyXxHash64 { get; init; } = true;

    private static readonly IReadOnlyDictionary<CompressionProfile, ProfileContract> Contracts =
        new Dictionary<CompressionProfile, ProfileContract>
        {
            [CompressionProfile.Instant] = new()
            {
                Profile = CompressionProfile.Instant,
                IncompressibleEntropyBypassBitsPerByte = 7.50,
                IncompressibleRatioThreshold = 0.995,
                MaxMemoryBudgetBytes = 64L * 1024 * 1024,
                PilotSampleBytes = 0,
                PilotWindowCount = 0,
                CandidatePipelineBudget = 1,
                UsesMultiCandidateTournament = false,
                TargetThroughputFloorMiBPerSec = 250.0,
                SearchTimeBudget = TimeSpan.FromSeconds(5),
                MandatoryBitExactVerify = true,
                VerifySha256 = true,
                VerifyXxHash64 = true,
            },
            [CompressionProfile.Balanced] = new()
            {
                Profile = CompressionProfile.Balanced,
                IncompressibleEntropyBypassBitsPerByte = 7.80,
                IncompressibleRatioThreshold = 0.995,
                MaxMemoryBudgetBytes = 256L * 1024 * 1024,
                PilotSampleBytes = 0,
                PilotWindowCount = 0,
                CandidatePipelineBudget = 1,
                UsesMultiCandidateTournament = false,
                TargetThroughputFloorMiBPerSec = 80.0,
                SearchTimeBudget = TimeSpan.FromSeconds(30),
                MandatoryBitExactVerify = true,
                VerifySha256 = true,
                VerifyXxHash64 = true,
            },
            [CompressionProfile.Archive] = new()
            {
                Profile = CompressionProfile.Archive,
                IncompressibleEntropyBypassBitsPerByte = 7.90,
                IncompressibleRatioThreshold = 0.995,
                MaxMemoryBudgetBytes = 512L * 1024 * 1024,
                PilotSampleBytes = 32 * 1024,
                PilotWindowCount = 2,
                CandidatePipelineBudget = 4,
                UsesMultiCandidateTournament = false,
                TargetThroughputFloorMiBPerSec = 20.0,
                SearchTimeBudget = TimeSpan.FromMinutes(2),
                MandatoryBitExactVerify = true,
                VerifySha256 = true,
                VerifyXxHash64 = true,
            },
            [CompressionProfile.Tiny] = new()
            {
                Profile = CompressionProfile.Tiny,
                IncompressibleEntropyBypassBitsPerByte = 7.95,
                IncompressibleRatioThreshold = 0.995,
                MaxMemoryBudgetBytes = 1024L * 1024 * 1024,
                PilotSampleBytes = 64 * 1024,
                PilotWindowCount = 3,
                CandidatePipelineBudget = 16,
                UsesMultiCandidateTournament = true,
                TargetThroughputFloorMiBPerSec = 4.0,
                SearchTimeBudget = TimeSpan.FromMinutes(10),
                MandatoryBitExactVerify = true,
                VerifySha256 = true,
                VerifyXxHash64 = true,
            },
            [CompressionProfile.Laboratory] = new()
            {
                Profile = CompressionProfile.Laboratory,
                IncompressibleEntropyBypassBitsPerByte = 7.98,
                IncompressibleRatioThreshold = 0.995,
                MaxMemoryBudgetBytes = 2048L * 1024 * 1024,
                PilotSampleBytes = 256 * 1024,
                PilotWindowCount = 4,
                CandidatePipelineBudget = 24,
                UsesMultiCandidateTournament = true,
                TargetThroughputFloorMiBPerSec = 1.0,
                SearchTimeBudget = TimeSpan.FromMinutes(30),
                MandatoryBitExactVerify = true,
                VerifySha256 = true,
                VerifyXxHash64 = true,
            },
        };

    public static IReadOnlyDictionary<CompressionProfile, ProfileContract> All => Contracts;

    public static ProfileContract Get(CompressionProfile profile) =>
        Contracts.TryGetValue(profile, out var contract)
            ? contract
            : throw new ArgumentOutOfRangeException(nameof(profile), profile, "Undefined compression profile.");

    public void ValidateContractInvariants()
    {
        if (OperationKind != OperationKind.LosslessCompression)
        {
            throw new SafetyPolicyViolationException(
                "CONTRACT_NOT_LOSSLESS",
                $"ProfileContract for '{Profile}' must enforce OperationKind.LosslessCompression.");
        }

        if (!MandatoryBitExactVerify || !VerifySha256 || !VerifyXxHash64)
        {
            throw new SafetyPolicyViolationException(
                "CONTRACT_VERIFY_DISABLED",
                $"ProfileContract for '{Profile}' must enforce MandatoryBitExactVerify with SHA-256 and XXH64.");
        }

        if (IncompressibleEntropyBypassBitsPerByte is <= 0 or > 8.0)
        {
            throw new InvalidOperationException($"Entropy threshold out of range for '{Profile}'.");
        }

        if (IncompressibleRatioThreshold is <= 0 or > 1.0)
        {
            throw new InvalidOperationException($"Incompressible ratio threshold out of range for '{Profile}'.");
        }

        if (MaxMemoryBudgetBytes <= 0 || CandidatePipelineBudget < 1 || PilotSampleBytes < 0)
        {
            throw new InvalidOperationException($"Resource budget invalid for '{Profile}'.");
        }
    }
}
