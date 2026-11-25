using System.Globalization;
using System.Runtime.InteropServices;
using UltraCompress.Benchmarks.Models;

namespace UltraCompress.Benchmarks.Instrumentation;

/// <summary>
/// Captures exact OS, kernel, .NET runtime, CPU model/core topology/SIMD flags, RAM,
/// and energy/memory instrumentation metadata for reproducible benchmark manifests.
/// </summary>
public static class HardwareProbe
{
    /// <summary>
    /// Documented reference TDP per logical vCPU when hardware Intel/AMD RAPL counters are unavailable in cloud VMs.
    /// 15.0 W per logical core corresponds to a 60 W 4-core / 120 W 8-core server/workstation package envelope.
    /// </summary>
    public const double DefaultPerLogicalCpuWatts = 15.0;

    public static HardwareManifest Capture()
    {
        int logicalCores = Math.Max(1, Environment.ProcessorCount);
        int physicalCores = logicalCores;
        string cpuModel = RuntimeInformation.ProcessArchitecture.ToString();
        string cpuVendor = "Unknown";
        string instructionSets = DetectRuntimeSimdFlags();
        double totalMemMb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0), 1);

        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            ParseLinuxCpuInfo(ref cpuModel, ref cpuVendor, ref physicalCores, ref instructionSets);
        }
        else if (OperatingSystem.IsWindows())
        {
            cpuModel = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? cpuModel;
        }

        bool raplAvailable = HasReadableLinuxRapl();
        double packageTdpWatts = Math.Round(DefaultPerLogicalCpuWatts * logicalCores, 1);

        string energyMethod = raplAvailable
            ? "Hardware Intel RAPL (/sys/class/powercap/intel-rapl:0/energy_uj)"
            : string.Create(CultureInfo.InvariantCulture,
                $"CPU-Energy Linear TDP Model (hardware RAPL unavailable in cloud VM: Energy_mWh = (CpuTotalSec * {DefaultPerLogicalCpuWatts:F1} W/core) / 3.6; {packageTdpWatts:F1} W {logicalCores}-vCPU reference package)");

        string memoryMethod = OperatingSystem.IsLinux() && File.Exists("/usr/bin/time")
            ? "ManagedInProcess: 1ms /proc/self/statm RSS + child /usr/bin/time -v ru_maxrss; ExternalChildProcess: /usr/bin/time -v ru_maxrss + 1ms /proc RSS sampling"
            : "Process.WorkingSet64 & Process.PeakWorkingSet64 1ms sampling";

        return new HardwareManifest
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            OperatingSystem = RuntimeInformation.OSDescription.Trim(),
            KernelVersion = Environment.OSVersion.VersionString,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            DotNetVersion = $".NET {Environment.Version} ({RuntimeInformation.FrameworkDescription})",
            CpuModel = cpuModel,
            CpuVendor = cpuVendor,
            PhysicalCores = physicalCores,
            LogicalCores = logicalCores,
            CpuInstructionSets = instructionSets,
            TotalMemoryMB = totalMemMb,
            EnergyMeasurementMethod = energyMethod,
            ReferencePackageTdpWatts = packageTdpWatts,
            ReferencePerLogicalCpuWatts = DefaultPerLogicalCpuWatts,
            MemoryMeasurementMethod = memoryMethod,
        };
    }

    public static bool HasReadableLinuxRapl()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        const string raplPath = "/sys/class/powercap/intel-rapl:0/energy_uj";
        try
        {
            if (!File.Exists(raplPath))
            {
                return false;
            }

            _ = File.ReadAllText(raplPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static double ComputeEnergyMilliwattHours(double cpuTotalMs, double? raplJoules = null)
    {
        double joules = raplJoules ?? ((cpuTotalMs / 1000.0) * DefaultPerLogicalCpuWatts);
        return Math.Round(joules / 3.6, 4);
    }

    private static void ParseLinuxCpuInfo(
        ref string cpuModel,
        ref string cpuVendor,
        ref int physicalCores,
        ref string instructionSets)
    {
        var corePairs = new HashSet<string>(StringComparer.Ordinal);
        string? physId = null;
        string? coreId = null;
        string? flagsLine = null;

        foreach (var rawLine in File.ReadLines("/proc/cpuinfo"))
        {
            int colon = rawLine.IndexOf(':');
            if (colon <= 0)
            {
                if (physId is not null && coreId is not null)
                {
                    corePairs.Add($"{physId}:{coreId}");
                }

                physId = null;
                coreId = null;
                continue;
            }

            string key = rawLine[..colon].Trim();
            string val = rawLine[(colon + 1)..].Trim();

            if (key.Equals("model name", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(val))
            {
                cpuModel = val;
            }
            else if (key.Equals("vendor_id", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(val))
            {
                cpuVendor = val;
            }
            else if (key.Equals("physical id", StringComparison.OrdinalIgnoreCase))
            {
                physId = val;
            }
            else if (key.Equals("core id", StringComparison.OrdinalIgnoreCase))
            {
                coreId = val;
            }
            else if (key.Equals("flags", StringComparison.OrdinalIgnoreCase) && flagsLine is null)
            {
                flagsLine = val;
            }
        }

        if (physId is not null && coreId is not null)
        {
            corePairs.Add($"{physId}:{coreId}");
        }

        if (corePairs.Count > 0)
        {
            physicalCores = corePairs.Count;
        }

        if (!string.IsNullOrEmpty(flagsLine))
        {
            string[] notable = ["sse4_2", "avx", "avx2", "avx512f", "avx512bw", "avx512vl", "avx512vbmi2", "bmi2", "sha_ni", "vaes"];
            var flagSet = flagsLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var present = notable.Where(flagSet.Contains).ToArray();
            if (present.Length > 0)
            {
                instructionSets = string.Join(", ", present);
            }
        }
    }

    private static string DetectRuntimeSimdFlags()
    {
        var list = new List<string>();
        if (System.Runtime.Intrinsics.X86.Sse42.IsSupported) list.Add("sse4_2");
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported) list.Add("avx2");
        if (System.Runtime.Intrinsics.X86.Avx512F.IsSupported) list.Add("avx512f");
        if (System.Runtime.Intrinsics.X86.Bmi2.IsSupported) list.Add("bmi2");
        if (System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported) list.Add("neon");
        return list.Count > 0 ? string.Join(", ", list) : RuntimeInformation.ProcessArchitecture.ToString();
    }
}
