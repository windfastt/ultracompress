using System.Reflection;
using UltraCompress.Benchmarks.Corpus;
using UltraCompress.Benchmarks.Instrumentation;
using UltraCompress.Benchmarks.Models;

namespace UltraCompress.Benchmarks.Runners;

public sealed record BenchmarkRunOptions
{
    public BenchmarkScale Scale { get; init; } = BenchmarkScale.Standard;
    public int WarmupIterations { get; init; } = 1;
    public int MeasuredIterations { get; init; } = 3;
    public bool RequireAllCompetitors { get; init; }
    public string? CorpusDirectory { get; init; }
    public IReadOnlyCollection<string>? CategoryFilter { get; init; }
    public IReadOnlyCollection<string>? ToolFilter { get; init; }
}

public static class BenchmarkSuiteExecutor
{
    public static IReadOnlyList<IBenchmarkToolRunner> BuildRunners(
        bool requireAllCompetitors,
        IReadOnlyCollection<string>? toolFilter = null)
    {
        string ucVersion = typeof(UltraCompress.Core.CompressionProfile).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.3.1";
        int plusIdx = ucVersion.IndexOf('+');
        if (plusIdx > 0 && ucVersion.Length > plusIdx + 8)
        {
            ucVersion = ucVersion[..(plusIdx + 8)];
        }

        var runners = new List<IBenchmarkToolRunner>
        {
            new UltraCompressProfileRunner(UltraCompressBenchmarkProfile.Instant, $"UltraCompress {ucVersion}"),
            new UltraCompressProfileRunner(UltraCompressBenchmarkProfile.Balanced, $"UltraCompress {ucVersion}"),
            new UltraCompressProfileRunner(UltraCompressBenchmarkProfile.Archive, $"UltraCompress {ucVersion}"),
            new UltraCompressProfileRunner(UltraCompressBenchmarkProfile.Tiny, $"UltraCompress {ucVersion}"),
        };

        runners.AddRange(ExternalCliCompetitorRunner.DiscoverAll(requireAllCompetitors));

        if (toolFilter is { Count: > 0 })
        {
            runners = runners
                .Where(r => toolFilter.Any(f =>
                    r.Descriptor.ToolId.Contains(f, StringComparison.OrdinalIgnoreCase)
                    || r.Descriptor.ToolFamily.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        return runners;
    }

    public static async Task<BenchmarkSuiteReport> RunSuiteAsync(
        BenchmarkRunOptions options,
        Action<string>? progressLogger = null,
        CancellationToken cancellationToken = default)
    {
        var hardware = HardwareProbe.Capture();
        var runners = BuildRunners(options.RequireAllCompetitors, options.ToolFilter);
        if (runners.Count == 0)
        {
            throw new InvalidOperationException("No benchmark tool runners matched the filter.");
        }

        bool ownsTempCorpus = string.IsNullOrWhiteSpace(options.CorpusDirectory);
        string corpusRoot = ownsTempCorpus
            ? Path.Combine(Path.GetTempPath(), $"uc_bench_corpus_{Guid.NewGuid():N}")
            : Path.GetFullPath(options.CorpusDirectory!);

        string scratchRoot = Path.Combine(Path.GetTempPath(), $"uc_bench_scratch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchRoot);

        try
        {
            progressLogger?.Invoke($"[Corpus] Generating deterministic {options.Scale} corpus at '{corpusRoot}'...");
            var categories = CorpusBuilder.BuildAll(corpusRoot, options.Scale, options.CategoryFilter);
            string aggregateSha = CorpusBuilder.ComputeAggregateCorpusSha256(categories);
            progressLogger?.Invoke($"[Corpus] Ready: {categories.Count} categories, aggregate SHA-256 = {aggregateSha[..16]}...");

            await PrewarmManagedRunnersAsync(runners, scratchRoot, cancellationToken);

            var measurements = new List<BenchmarkMeasurement>(categories.Count * runners.Count);

            foreach (var category in categories)
            {
                progressLogger?.Invoke(
                    $"\n=== Category: {category.Category} ({category.OriginalBytes / (1024.0 * 1024.0):F2} MiB, {category.FileCount} files, entropy={category.MeanEntropyBitsPerByte:F3} bpb) ===");

                foreach (var runner in runners)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int effectiveWarmups = runner.Descriptor.ExecutionMode == "ManagedInProcess"
                        ? options.WarmupIterations
                        : Math.Max(0, options.WarmupIterations - 1);

                    for (int w = 0; w < effectiveWarmups; w++)
                    {
                        string warmScratch = Path.Combine(scratchRoot, $"w_{w}");
                        RecreateDirectory(warmScratch);
                        _ = await runner.ExecuteTrialAsync(category, warmScratch, cancellationToken);
                        TryDeleteDirectory(warmScratch);
                    }

                    var trials = new List<SingleTrialOutcome>(options.MeasuredIterations);
                    for (int iter = 0; iter < options.MeasuredIterations; iter++)
                    {
                        string iterScratch = Path.Combine(scratchRoot, $"m_{iter}");
                        RecreateDirectory(iterScratch);
                        var outcome = await runner.ExecuteTrialAsync(category, iterScratch, cancellationToken);
                        trials.Add(outcome);
                        TryDeleteDirectory(iterScratch);
                    }

                    var row = AggregateTrials(category, runner.Descriptor, hardware, trials);
                    measurements.Add(row);

                    string cpuPerGibText = row.CpuSecondsPerGiBSaved.HasValue
                        ? $"{row.CpuSecondsPerGiBSaved.Value:F2} CPU-s/GiB"
                        : "N/A (0 B saved)";

                    progressLogger?.Invoke(
                        $"  {runner.Descriptor.DisplayName,-34} | ratio={row.CompressionRatio,6:F3}x ({row.SavingsPercent,6:F2}%) | " +
                        $"comp={row.CompressThroughputMBps,7:F1} MiB/s | decomp={row.DecompressThroughputMBps,7:F1} MiB/s | " +
                        $"peakRAM={row.PeakWorkingSetMB,6:F1} MiB | {cpuPerGibText}");
                }
            }

            return new BenchmarkSuiteReport
            {
                SchemaVersion = "1.0.0",
                Scale = options.Scale,
                WarmupIterations = options.WarmupIterations,
                MeasuredIterations = options.MeasuredIterations,
                CorpusManifestAggregateSha256 = aggregateSha,
                Hardware = hardware,
                Categories = categories,
                Tools = runners.Select(r => r.Descriptor).ToList(),
                Measurements = measurements,
            };
        }
        finally
        {
            TryDeleteDirectory(scratchRoot);
            if (ownsTempCorpus)
            {
                TryDeleteDirectory(corpusRoot);
            }
        }
    }

    private static BenchmarkMeasurement AggregateTrials(
        CorpusCategoryManifest category,
        ToolDescriptor tool,
        HardwareManifest hardware,
        IReadOnlyList<SingleTrialOutcome> trials)
    {
        var first = trials[0];
        long origBytes = category.OriginalBytes;
        long compBytes = first.CompressedBytes;

        double compWallMs = Median(trials.Select(t => t.CompressMetrics.WallMs));
        double decompWallMs = Median(trials.Select(t => t.DecompressMetrics.WallMs));
        double cpuUserMs = Median(trials.Select(t => t.CompressMetrics.CpuUserMs));
        double cpuKernelMs = Median(trials.Select(t => t.CompressMetrics.CpuKernelMs));
        double cpuTotalMs = Math.Round(cpuUserMs + cpuKernelMs, 3);
        double decompCpuTotalMs = Median(trials.Select(t => t.DecompressMetrics.CpuTotalMs));

        double avgRamMb = Median(trials.Select(t => Math.Max(t.CompressMetrics.AvgRamMB, t.DecompressMetrics.AvgRamMB)));
        double peakRamMb = Median(trials.Select(t => Math.Max(t.CompressMetrics.PeakWorkingSetMB, t.DecompressMetrics.PeakWorkingSetMB)));
        if (avgRamMb > peakRamMb)
        {
            avgRamMb = peakRamMb;
        }

        double origMib = origBytes / (1024.0 * 1024.0);
        double compThroughput = Math.Round(origMib / (compWallMs / 1000.0), 2);
        double decompThroughput = Math.Round(origMib / (decompWallMs / 1000.0), 2);

        double ratio = Math.Round(origBytes / (double)Math.Max(1L, compBytes), 4);
        double savingsPct = Math.Round((1.0 - (compBytes / (double)origBytes)) * 100.0, 2);

        double cpuUtilPct = Math.Round((cpuTotalMs / Math.Max(0.05, compWallMs)) * 100.0, 1);
        double cpuSysUtilPct = Math.Round(cpuUtilPct / Math.Max(1, hardware.LogicalCores), 1);

        long savedBytes = origBytes - compBytes;
        double? cpuSecPerGibSaved = null;
        if (savedBytes > 0)
        {
            double gibSaved = savedBytes / (1024.0 * 1024.0 * 1024.0);
            cpuSecPerGibSaved = Math.Round((cpuTotalMs / 1000.0) / gibSaved, 3);
        }

        var raplSamples = trials
            .Where(t => t.CompressMetrics.RaplJoules.HasValue)
            .Select(t => t.CompressMetrics.RaplJoules!.Value)
            .ToList();
        double? medianRapl = raplSamples.Count > 0 ? Median(raplSamples) : null;
        double energyMwh = HardwareProbe.ComputeEnergyMilliwattHours(cpuTotalMs, medianRapl);

        return new BenchmarkMeasurement
        {
            Category = category.Category,
            ToolId = tool.ToolId,
            ToolDisplayName = tool.DisplayName,
            ToolFamily = tool.ToolFamily,
            ToolVersion = tool.Version,
            CompressFlags = tool.CompressFlags,
            DecompressFlags = tool.DecompressFlags,
            ExecutionMode = tool.ExecutionMode,
            OriginalBytes = origBytes,
            CompressedBytes = compBytes,
            CompressionRatio = ratio,
            SavingsPercent = savingsPct,
            CompressWallMs = Math.Round(compWallMs, 3),
            DecompressWallMs = Math.Round(decompWallMs, 3),
            CompressThroughputMBps = compThroughput,
            DecompressThroughputMBps = decompThroughput,
            RoundtripSha256Verified = trials.All(t => t.RoundtripSha256Verified),
            VerifiedManifestSha256 = first.VerifiedManifestSha256,
            CpuUserMs = Math.Round(cpuUserMs, 3),
            CpuKernelMs = Math.Round(cpuKernelMs, 3),
            CpuTotalMs = cpuTotalMs,
            DecompressCpuTotalMs = Math.Round(decompCpuTotalMs, 3),
            CpuUtilizationPercent = cpuUtilPct,
            CpuSystemUtilizationPercent = cpuSysUtilPct,
            AvgRamMB = Math.Round(avgRamMb, 2),
            PeakWorkingSetMB = Math.Round(peakRamMb, 2),
            CpuSecondsPerGiBSaved = cpuSecPerGibSaved,
            EnergyMilliwattHours = energyMwh,
            StrategySummary = first.StrategySummary,
        };
    }

    private static async Task PrewarmManagedRunnersAsync(
        IReadOnlyList<IBenchmarkToolRunner> runners,
        string scratchRoot,
        CancellationToken ct)
    {
        var managedRunners = runners.Where(r => r.Descriptor.ExecutionMode == "ManagedInProcess").ToList();
        if (managedRunners.Count == 0) return;

        string jitDir = Path.Combine(scratchRoot, "jit_prewarm");
        try
        {
            var miniCat = CorpusBuilder.BuildCategory(jitDir, CorpusBuilder.DatabaseDumpCategory, BenchmarkScale.Smoke);
            foreach (var r in managedRunners)
            {
                string rScratch = Path.Combine(jitDir, r.Descriptor.ToolId);
                RecreateDirectory(rScratch);
                _ = await r.ExecuteTrialAsync(miniCat, rScratch, ct);
            }
        }
        finally
        {
            TryDeleteDirectory(jitDir);
        }
    }

    private static double Median(IEnumerable<double> source)
    {
        var sorted = source.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static void RecreateDirectory(string path)
    {
        TryDeleteDirectory(path);
        Directory.CreateDirectory(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore best-effort cleanup race.
        }
    }
}
