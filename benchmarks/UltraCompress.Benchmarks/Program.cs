using System.Globalization;
using UltraCompress.Benchmarks.Corpus;
using UltraCompress.Benchmarks.Models;
using UltraCompress.Benchmarks.Reporting;
using UltraCompress.Benchmarks.Runners;

namespace UltraCompress.Benchmarks;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintHelp();
            return 0;
        }

        BenchmarkScale scale = BenchmarkScale.Standard;
        int? warmupOverride = null;
        int? iterationsOverride = null;
        bool smoke = false;
        bool verifyDeterminism = false;
        bool verifyDeterminismOnly = false;
        bool requireAllCompetitors = false;
        string? generateCorpusOnlyDir = null;
        string? corpusDir = null;
        string? outputDir = null;
        string? markdownTablesOut = null;
        IReadOnlyCollection<string>? categories = null;
        IReadOnlyCollection<string>? tools = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--smoke":
                    smoke = true;
                    scale = BenchmarkScale.Smoke;
                    break;
                case "--scale":
                    scale = Enum.Parse<BenchmarkScale>(args[++i], ignoreCase: true);
                    break;
                case "--warmup":
                    warmupOverride = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--iterations":
                    iterationsOverride = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--verify-determinism":
                    verifyDeterminism = true;
                    break;
                case "--verify-determinism-only":
                    verifyDeterminism = true;
                    verifyDeterminismOnly = true;
                    break;
                case "--require-all-competitors":
                    requireAllCompetitors = true;
                    break;
                case "--generate-corpus":
                    generateCorpusOnlyDir = args[++i];
                    break;
                case "--corpus-dir":
                    corpusDir = args[++i];
                    break;
                case "--output-dir":
                    outputDir = args[++i];
                    break;
                case "--markdown-tables-out":
                    markdownTablesOut = args[++i];
                    break;
                case "--categories":
                    categories = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--tools":
                    tools = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: '{arg}'. Pass --help for usage.");
                    return 2;
            }
        }

        if (verifyDeterminism)
        {
            VerifyCorpusDeterminism(scale, categories);
            if (verifyDeterminismOnly)
            {
                return 0;
            }
        }

        if (generateCorpusOnlyDir is not null)
        {
            var built = CorpusBuilder.BuildAll(generateCorpusOnlyDir, scale, categories);
            string aggHash = CorpusBuilder.ComputeAggregateCorpusSha256(built);
            Console.WriteLine($"Generated deterministic {scale} corpus in '{Path.GetFullPath(generateCorpusOnlyDir)}'");
            foreach (var c in built)
            {
                Console.WriteLine($"  {c.Category,-18} | files={c.FileCount,4} (dups={c.DuplicateFileCount,2}) | size={c.OriginalBytes,10} B | entropy={c.MeanEntropyBitsPerByte:F4} bpb | sha256={c.ManifestSha256}");
            }

            Console.WriteLine($"Aggregate Corpus SHA-256: {aggHash}");
            return 0;
        }

        int warmup = warmupOverride ?? (smoke ? 0 : 1);
        int iterations = iterationsOverride ?? (smoke ? 1 : 3);
        string resolvedOutDir = outputDir ?? ResolveDefaultResultsDirectory();

        var runOptions = new BenchmarkRunOptions
        {
            Scale = scale,
            WarmupIterations = warmup,
            MeasuredIterations = iterations,
            RequireAllCompetitors = requireAllCompetitors,
            CorpusDirectory = corpusDir,
            CategoryFilter = categories,
            ToolFilter = tools,
        };

        var report = await BenchmarkSuiteExecutor.RunSuiteAsync(runOptions, Console.WriteLine);

        string jsonPath = Path.Combine(resolvedOutDir, "latest-benchmark-report.json");
        string csvPath = Path.Combine(resolvedOutDir, "latest-benchmark-report.csv");

        ReportExporter.WriteJson(report, jsonPath);
        ReportExporter.WriteCsv(report, csvPath);

        if (!string.IsNullOrWhiteSpace(markdownTablesOut))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(markdownTablesOut))!);
            File.WriteAllText(markdownTablesOut, ReportExporter.RenderMarkdownTables(report));
            Console.WriteLine($"Wrote Markdown tables -> {Path.GetFullPath(markdownTablesOut)}");
        }

        Console.WriteLine($"\nBenchmark suite complete ({report.Measurements.Count} verified measurements).");
        Console.WriteLine($"  JSON -> {Path.GetFullPath(jsonPath)}");
        Console.WriteLine($"  CSV  -> {Path.GetFullPath(csvPath)}");
        return 0;
    }

    private static void VerifyCorpusDeterminism(BenchmarkScale scale, IReadOnlyCollection<string>? categories)
    {
        string dirA = Path.Combine(Path.GetTempPath(), $"uc_det_a_{Guid.NewGuid():N}");
        string dirB = Path.Combine(Path.GetTempPath(), $"uc_det_b_{Guid.NewGuid():N}");
        try
        {
            var manA = CorpusBuilder.BuildAll(dirA, scale, categories);
            var manB = CorpusBuilder.BuildAll(dirB, scale, categories);

            string aggA = CorpusBuilder.ComputeAggregateCorpusSha256(manA);
            string aggB = CorpusBuilder.ComputeAggregateCorpusSha256(manB);

            if (!string.Equals(aggA, aggB, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Corpus determinism check FAILED: {aggA} != {aggB}");
            }

            for (int i = 0; i < manA.Count; i++)
            {
                if (manA[i].ManifestSha256 != manB[i].ManifestSha256 || manA[i].TarSha256 != manB[i].TarSha256)
                {
                    throw new InvalidOperationException($"Category '{manA[i].Category}' determinism check FAILED.");
                }
            }

            Console.WriteLine($"[Determinism] PASS — Two independent '{scale}' corpus builds matched bit-for-bit (SHA-256: {aggA}).");
        }
        finally
        {
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, recursive: true); } catch { }
            try { if (Directory.Exists(dirB)) Directory.Delete(dirB, recursive: true); } catch { }
        }
    }

    private static string ResolveDefaultResultsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "benchmarks")))
            {
                return Path.Combine(dir.FullName, "benchmarks", "results");
            }
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "benchmarks", "results");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            UltraCompress Reproducible Competitor Benchmark Suite

            Usage:
              dotnet run -c Release --project benchmarks/UltraCompress.Benchmarks -- [options]

            Options:
              --smoke                       Fast CI smoke mode (Smoke corpus, warmup=0, iterations=1)
              --scale <Smoke|Standard|Large> Corpus size tier (default: Standard)
              --warmup <N>                  Warmup iterations per pair (default: 1, Smoke: 0)
              --iterations <N>              Measured iterations per pair; median reported (default: 3, Smoke: 1)
              --generate-corpus <dir>       Generate deterministic corpus & print SHA-256 manifests, then exit
              --verify-determinism          Build corpus twice independently and verify bit-exact SHA-256 identity
              --require-all-competitors     Fail if any external CLI (7za, zstd, brotli, gzip, libdeflate-gzip) is missing
              --categories <c1,c2,...>      Filter categories (video-stream,source-repo,database-dump,photo-collection,native-binaries,mixed-workspace)
              --tools <t1,t2,...>           Filter tool IDs or families (e.g. ultracompress,7za,zstd,brotli,gzip,libdeflate)
              --output-dir <dir>            Output directory for latest-benchmark-report.{json,csv} (default: benchmarks/results)
              --markdown-tables-out <file>  Optional path to write rendered Markdown per-category tables
            """);
    }
}
