using UltraCompress.Benchmarks.Corpus;
using UltraCompress.Benchmarks.Engine;
using UltraCompress.Benchmarks.Instrumentation;
using UltraCompress.Benchmarks.Models;

namespace UltraCompress.Benchmarks.Runners;

public sealed class UltraCompressProfileRunner : IBenchmarkToolRunner
{
    private readonly UltraCompressBenchmarkProfile _profile;
    private readonly UcxBenchmarkArchiver _archiver = new();

    public UltraCompressProfileRunner(UltraCompressBenchmarkProfile profile, string version)
    {
        _profile = profile;

        string flags = profile switch
        {
            UltraCompressBenchmarkProfile.Instant => "--profile Instant (smart-bypass + SHA-256 dedup + solid zstd-1)",
            UltraCompressBenchmarkProfile.Balanced => "--profile Balanced (content-routed bypass + dedup + ByteShuffle/Delta/BCJ + solid zstd-6)",
            UltraCompressBenchmarkProfile.Archive => "--profile Archive (solid per-class + transforms + brotli-9/zstd-15)",
            UltraCompressBenchmarkProfile.Tiny => "--profile Tiny (UCB1 pilot + multi-candidate tournament: transforms x {Store, zstd-22, brotli-11, libdeflate-12, 7z-mx9})",
            _ => $"--profile {profile}",
        };

        Descriptor = new ToolDescriptor
        {
            ToolId = $"ultracompress-{profile.ToString().ToLowerInvariant()}",
            DisplayName = $"UltraCompress ({profile})",
            ToolFamily = "UltraCompress",
            Version = version,
            CompressFlags = $"uc pack {flags}",
            DecompressFlags = "uc extract --verify-sha256",
            ExecutionMode = "ManagedInProcess",
        };
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<SingleTrialOutcome> ExecuteTrialAsync(
        CorpusCategoryManifest category,
        string scratchDirectory,
        CancellationToken cancellationToken = default)
    {
        string archivePath = Path.Combine(scratchDirectory, $"{category.Category}.{_profile.ToString().ToLowerInvariant()}.ucx");
        string extractDir = Path.Combine(scratchDirectory, "restored");
        Directory.CreateDirectory(extractDir);

        bool spawnsSevenZip = _profile == UltraCompressBenchmarkProfile.Tiny;

        var (strategySummary, compMetrics) = await ProcessMetricsCollector.MeasureManagedInProcessAsync(
            () => _archiver.CompressDirectoryAsync(category.DirectoryPath, archivePath, _profile, cancellationToken),
            compactHeapBefore: true,
            trackChildProcesses: spawnsSevenZip,
            cancellationToken: cancellationToken);

        long compressedBytes = new FileInfo(archivePath).Length;

        var (_, decompMetrics) = await ProcessMetricsCollector.MeasureManagedInProcessAsync(
            async () =>
            {
                await _archiver.ExtractDirectoryAsync(archivePath, extractDir, cancellationToken);
                return 0;
            },
            compactHeapBefore: false,
            trackChildProcesses: spawnsSevenZip,
            cancellationToken: cancellationToken);

        var (restoredHash, restoredBytes, restoredCount) = DeterministicTarArchive.ComputeDirectoryManifest(extractDir);
        bool verified = restoredCount == category.FileCount
            && restoredBytes == category.OriginalBytes
            && string.Equals(restoredHash, category.ManifestSha256, StringComparison.Ordinal);

        if (!verified)
        {
            throw new InvalidDataException(
                $"SHA-256 verification failed for {Descriptor.DisplayName} on '{category.Category}': expected {category.ManifestSha256}, got {restoredHash}.");
        }

        return new SingleTrialOutcome(
            CompressedBytes: compressedBytes,
            RoundtripSha256Verified: true,
            VerifiedManifestSha256: restoredHash,
            CompressMetrics: compMetrics,
            DecompressMetrics: decompMetrics,
            StrategySummary: strategySummary);
    }
}
