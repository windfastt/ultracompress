using System.Text.Json.Serialization;

namespace UltraCompress.Benchmarks.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BenchmarkScale
{
    Smoke,
    Standard,
    Large,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UltraCompressBenchmarkProfile
{
    Instant,
    Balanced,
    Archive,
    Tiny,
}

public sealed record CorpusFileEntry(
    string RelativePath,
    long SizeBytes,
    string Sha256,
    double EntropyBitsPerByte);

public sealed record CorpusCategoryManifest
{
    public required string Category { get; init; }
    public required string Description { get; init; }
    public required ulong Seed { get; init; }
    public required int FileCount { get; init; }
    public required int DuplicateFileCount { get; init; }
    public required long DuplicateBytes { get; init; }
    public required long OriginalBytes { get; init; }
    public required long TarBytes { get; init; }
    public required double MeanEntropyBitsPerByte { get; init; }
    public required string ManifestSha256 { get; init; }
    public required string TarSha256 { get; init; }
    public IReadOnlyList<CorpusFileEntry> Files { get; init; } = Array.Empty<CorpusFileEntry>();

    [JsonIgnore]
    public string DirectoryPath { get; init; } = string.Empty;

    [JsonIgnore]
    public string TarFilePath { get; init; } = string.Empty;
}

public sealed record HardwareManifest
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required string OperatingSystem { get; init; }
    public required string KernelVersion { get; init; }
    public required string Architecture { get; init; }
    public required string DotNetVersion { get; init; }
    public required string CpuModel { get; init; }
    public required string CpuVendor { get; init; }
    public required int PhysicalCores { get; init; }
    public required int LogicalCores { get; init; }
    public required string CpuInstructionSets { get; init; }
    public required double TotalMemoryMB { get; init; }
    public required string EnergyMeasurementMethod { get; init; }
    public required double ReferencePackageTdpWatts { get; init; }
    public required double ReferencePerLogicalCpuWatts { get; init; }
    public required string MemoryMeasurementMethod { get; init; }
}

public sealed record ToolDescriptor
{
    public required string ToolId { get; init; }
    public required string DisplayName { get; init; }
    public required string ToolFamily { get; init; }
    public required string Version { get; init; }
    public required string CompressFlags { get; init; }
    public required string DecompressFlags { get; init; }
    public required string ExecutionMode { get; init; }
}

public sealed record BenchmarkMeasurement
{
    public required string Category { get; init; }
    public required string ToolId { get; init; }
    public required string ToolDisplayName { get; init; }
    public required string ToolFamily { get; init; }
    public required string ToolVersion { get; init; }
    public required string CompressFlags { get; init; }
    public required string DecompressFlags { get; init; }
    public required string ExecutionMode { get; init; }

    public required long OriginalBytes { get; init; }
    public required long CompressedBytes { get; init; }
    public required double CompressionRatio { get; init; }
    public required double SavingsPercent { get; init; }

    public required double CompressWallMs { get; init; }
    public required double DecompressWallMs { get; init; }
    public required double CompressThroughputMBps { get; init; }
    public required double DecompressThroughputMBps { get; init; }

    public required bool RoundtripSha256Verified { get; init; }
    public required string VerifiedManifestSha256 { get; init; }

    public required double CpuUserMs { get; init; }
    public required double CpuKernelMs { get; init; }
    public required double CpuTotalMs { get; init; }
    public required double DecompressCpuTotalMs { get; init; }
    public required double CpuUtilizationPercent { get; init; }
    public required double CpuSystemUtilizationPercent { get; init; }

    public required double AvgRamMB { get; init; }
    public required double PeakWorkingSetMB { get; init; }

    public double? CpuSecondsPerGiBSaved { get; init; }
    public required double EnergyMilliwattHours { get; init; }

    public string? StrategySummary { get; init; }
}

public sealed record BenchmarkSuiteReport
{
    public required string SchemaVersion { get; init; }
    public required BenchmarkScale Scale { get; init; }
    public required int WarmupIterations { get; init; }
    public required int MeasuredIterations { get; init; }
    public required string CorpusManifestAggregateSha256 { get; init; }
    public required HardwareManifest Hardware { get; init; }
    public required IReadOnlyList<CorpusCategoryManifest> Categories { get; init; }
    public required IReadOnlyList<ToolDescriptor> Tools { get; init; }
    public required IReadOnlyList<BenchmarkMeasurement> Measurements { get; init; }
}
