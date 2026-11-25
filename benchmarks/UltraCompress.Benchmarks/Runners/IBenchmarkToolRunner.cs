using UltraCompress.Benchmarks.Models;

namespace UltraCompress.Benchmarks.Runners;

public sealed record SingleTrialOutcome(
    long CompressedBytes,
    bool RoundtripSha256Verified,
    string VerifiedManifestSha256,
    Instrumentation.PhaseExecutionMetrics CompressMetrics,
    Instrumentation.PhaseExecutionMetrics DecompressMetrics,
    string? StrategySummary);

public interface IBenchmarkToolRunner
{
    ToolDescriptor Descriptor { get; }

    Task<SingleTrialOutcome> ExecuteTrialAsync(
        CorpusCategoryManifest category,
        string scratchDirectory,
        CancellationToken cancellationToken = default);
}
