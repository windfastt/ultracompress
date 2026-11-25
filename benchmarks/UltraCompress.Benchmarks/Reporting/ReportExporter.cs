using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UltraCompress.Benchmarks.Models;

namespace UltraCompress.Benchmarks.Reporting;

public static class ReportExporter
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void WriteJson(BenchmarkSuiteReport report, string jsonPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
        string json = JsonSerializer.Serialize(report, JsonOptions);
        File.WriteAllText(jsonPath, json + "\n", Encoding.UTF8);
    }

    public static void WriteCsv(BenchmarkSuiteReport report, string csvPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath))!);
        var sb = new StringBuilder(report.Measurements.Count * 240);

        sb.AppendLine(string.Join(',',
            "Category",
            "ToolId",
            "ToolDisplayName",
            "ToolFamily",
            "ToolVersion",
            "ExecutionMode",
            "CompressFlags",
            "OriginalBytes",
            "CompressedBytes",
            "CompressionRatio",
            "SavingsPercent",
            "CompressWallMs",
            "DecompressWallMs",
            "CompressThroughputMBps",
            "DecompressThroughputMBps",
            "RoundtripSha256Verified",
            "VerifiedManifestSha256",
            "CpuUserMs",
            "CpuKernelMs",
            "CpuTotalMs",
            "DecompressCpuTotalMs",
            "CpuUtilizationPercent",
            "CpuSystemUtilizationPercent",
            "AvgRamMB",
            "PeakWorkingSetMB",
            "CpuSecondsPerGiBSaved",
            "EnergyMilliwattHours",
            "StrategySummary"));

        foreach (var m in report.Measurements)
        {
            sb.AppendLine(string.Join(',',
                CsvEscape(m.Category),
                CsvEscape(m.ToolId),
                CsvEscape(m.ToolDisplayName),
                CsvEscape(m.ToolFamily),
                CsvEscape(m.ToolVersion),
                CsvEscape(m.ExecutionMode),
                CsvEscape(m.CompressFlags),
                F(m.OriginalBytes),
                F(m.CompressedBytes),
                F(m.CompressionRatio, "F4"),
                F(m.SavingsPercent, "F2"),
                F(m.CompressWallMs, "F3"),
                F(m.DecompressWallMs, "F3"),
                F(m.CompressThroughputMBps, "F2"),
                F(m.DecompressThroughputMBps, "F2"),
                m.RoundtripSha256Verified ? "true" : "false",
                CsvEscape(m.VerifiedManifestSha256),
                F(m.CpuUserMs, "F3"),
                F(m.CpuKernelMs, "F3"),
                F(m.CpuTotalMs, "F3"),
                F(m.DecompressCpuTotalMs, "F3"),
                F(m.CpuUtilizationPercent, "F1"),
                F(m.CpuSystemUtilizationPercent, "F1"),
                F(m.AvgRamMB, "F2"),
                F(m.PeakWorkingSetMB, "F2"),
                m.CpuSecondsPerGiBSaved.HasValue ? F(m.CpuSecondsPerGiBSaved.Value, "F3") : "",
                F(m.EnergyMilliwattHours, "F4"),
                CsvEscape(m.StrategySummary ?? string.Empty)));
        }

        File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
    }

    public static string RenderMarkdownTables(BenchmarkSuiteReport report)
    {
        var sb = new StringBuilder(16384);

        foreach (var cat in report.Categories)
        {
            sb.AppendLine($"### `{cat.Category}` ({cat.OriginalBytes / (1024.0 * 1024.0):F2} MiB, {cat.FileCount} files, mean entropy {cat.MeanEntropyBitsPerByte:F3} bpb)");
            sb.AppendLine();
            sb.AppendLine($"_{cat.Description}_");
            sb.AppendLine();
            sb.AppendLine("| Tool / Profile | Orig (MiB) | Comp (MiB) | Ratio | Savings | Comp (MiB/s) | Decomp (MiB/s) | CPU Total (ms) | CPU Util % | Avg RAM (MiB) | Peak RAM (MiB) | CPU-s / GiB Saved | Est. Energy (mWh) | SHA-256 |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---:|");

            var rows = report.Measurements.Where(m => m.Category == cat.Category).ToList();
            foreach (var r in rows)
            {
                double origMib = r.OriginalBytes / (1024.0 * 1024.0);
                double compMib = r.CompressedBytes / (1024.0 * 1024.0);
                string cpuGib = r.CpuSecondsPerGiBSaved.HasValue
                    ? r.CpuSecondsPerGiBSaved.Value.ToString("F2", CultureInfo.InvariantCulture)
                    : "N/A";

                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| **{r.ToolDisplayName}** | {origMib:F2} | {compMib:F2} | {r.CompressionRatio:F3}x | {r.SavingsPercent:F2}% | {r.CompressThroughputMBps:F1} | {r.DecompressThroughputMBps:F1} | {r.CpuTotalMs:F1} | {r.CpuUtilizationPercent:F1}% | {r.AvgRamMB:F1} | {r.PeakWorkingSetMB:F1} | {cpuGib} | {r.EnergyMilliwattHours:F4} | {(r.RoundtripSha256Verified ? "PASS" : "FAIL")} |"));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string F(long val) => val.ToString(CultureInfo.InvariantCulture);

    private static string F(double val, string fmt) => val.ToString(fmt, CultureInfo.InvariantCulture);

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }
}
