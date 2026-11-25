using System.Text.Json;
using UltraCompress.Benchmarks.Corpus;
using UltraCompress.Benchmarks.Models;
using UltraCompress.Benchmarks.Reporting;
using UltraCompress.Benchmarks.Runners;

namespace UltraCompress.Benchmarks.Tests;

public sealed class SmokeBenchmarkSuiteTests
{
    [Fact]
    public async Task SmokeSuite_ExecutesVerifiesSha256AndExportsValidJsonAndCsv()
    {
        string outDir = Path.Combine(Path.GetTempPath(), $"uc_smoke_out_{Guid.NewGuid():N}");
        try
        {
            var report = await BenchmarkSuiteExecutor.RunSuiteAsync(new BenchmarkRunOptions
            {
                Scale = BenchmarkScale.Smoke,
                WarmupIterations = 0,
                MeasuredIterations = 1,
                RequireAllCompetitors = false,
                CategoryFilter = [CorpusBuilder.VideoStreamCategory, CorpusBuilder.DatabaseDumpCategory],
                ToolFilter = ["ultracompress-instant", "ultracompress-balanced"],
            });

            Assert.Equal(4, report.Measurements.Count);
            foreach (var m in report.Measurements)
            {
                Assert.True(m.RoundtripSha256Verified);
                Assert.True(m.OriginalBytes > 0);
                Assert.True(m.CompressedBytes > 0);
                Assert.True(m.CompressThroughputMBps > 0);
                Assert.True(m.DecompressThroughputMBps > 0);
                Assert.True(m.PeakWorkingSetMB > 0);
                Assert.True(m.AvgRamMB > 0);
            }

            string jsonPath = Path.Combine(outDir, "latest-benchmark-report.json");
            string csvPath = Path.Combine(outDir, "latest-benchmark-report.csv");
            ReportExporter.WriteJson(report, jsonPath);
            ReportExporter.WriteCsv(report, csvPath);

            Assert.True(File.Exists(jsonPath));
            Assert.True(File.Exists(csvPath));

            var roundtripped = JsonSerializer.Deserialize<BenchmarkSuiteReport>(
                await File.ReadAllTextAsync(jsonPath),
                ReportExporter.JsonOptions);
            Assert.NotNull(roundtripped);
            Assert.Equal(4, roundtripped!.Measurements.Count);

            var csvLines = await File.ReadAllLinesAsync(csvPath);
            Assert.Equal(5, csvLines.Length); // header + 4 rows
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }
}
