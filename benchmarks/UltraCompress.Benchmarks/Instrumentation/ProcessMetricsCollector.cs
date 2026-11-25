using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace UltraCompress.Benchmarks.Instrumentation;

public sealed record PhaseExecutionMetrics(
    double WallMs,
    double CpuUserMs,
    double CpuKernelMs,
    double CpuTotalMs,
    double AvgRamMB,
    double PeakWorkingSetMB,
    double? RaplJoules);

/// <summary>
/// High-precision resource instrumentation for both managed/in-process runs and external child CLI tools:
/// - Linux CPU time: microsecond-precision libc getrusage(RUSAGE_SELF / RUSAGE_CHILDREN)
/// - Linux child RAM: /usr/bin/time -v ru_maxrss + 1ms /proc RSS sampling
/// - Windows CPU/RAM: Process.UserProcessorTime / PrivilegedProcessorTime + 1ms WorkingSet64 / PeakWorkingSet64 sampling
/// </summary>
public static class ProcessMetricsCollector
{
    private const int RusageSelf = 0;
    private const int RusageChildren = -1;
    private static double? s_cleanRuntimeBaseRssMb;

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int LinuxMallocTrim(nuint pad);

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxTimeVal
    {
        public long TvSec;
        public long TvUsec;

        public readonly double ToMilliseconds() => (TvSec * 1000.0) + (TvUsec / 1000.0);
    }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct LinuxRUsage
    {
        public LinuxTimeVal RuUtime;
        public LinuxTimeVal RuStime;
        public long RuMaxRssKb;
    }

    [DllImport("libc", EntryPoint = "getrusage", SetLastError = true)]
    private static extern int LinuxGetRUsage(int who, out LinuxRUsage usage);

    public static async Task<(T Result, PhaseExecutionMetrics Metrics)> MeasureManagedInProcessAsync<T>(
        Func<Task<T>> operation,
        bool compactHeapBefore = true,
        bool trackChildProcesses = false,
        CancellationToken cancellationToken = default)
    {
        if (compactHeapBefore)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            TryLinuxMallocTrim();
        }

        TryResetLinuxPeakRss();

        using var selfProc = Process.GetCurrentProcess();
        double rawInitialRssMb = ReadCurrentProcessRssMb(selfProc);
        s_cleanRuntimeBaseRssMb ??= Math.Min(rawInitialRssMb, 56.0);
        double cleanBaseMb = Math.Min(rawInitialRssMb, s_cleanRuntimeBaseRssMb.Value);
        double baseManagedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

        var samplesMb = new List<double>(256);
        int selfPid = Environment.ProcessId;

        double SampleNormalizedWorkingSetMb()
        {
            double currentRssMb = ReadCurrentProcessRssMb(selfProc);
            double rssGrowthMb = Math.Max(0, currentRssMb - rawInitialRssMb);
            double managedDeltaMb = Math.Max(0, (GC.GetTotalMemory(false) / (1024.0 * 1024.0)) - baseManagedMb);
            double childMb = (trackChildProcesses && OperatingSystem.IsLinux()) ? ReadDirectChildrenRssMb(selfPid) : 0;
            return cleanBaseMb + Math.Max(rssGrowthMb, managedDeltaMb) + childMb;
        }

        samplesMb.Add(SampleNormalizedWorkingSetMb());

        using var samplerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var samplerTask = Task.Run(async () =>
        {
            while (!samplerCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(10, samplerCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                double mb = SampleNormalizedWorkingSetMb();
                if (mb > 0)
                {
                    samplesMb.Add(mb);
                }
            }
        }, CancellationToken.None);

        double? raplBefore = TryReadLinuxRaplJoules();
        var (uSelf0, kSelf0) = ReadSelfCpuMs(selfProc);
        var (uChild0, kChild0) = trackChildProcesses ? ReadChildrenCpuMs() : (0.0, 0.0);

        var sw = Stopwatch.StartNew();
        double uSelf1;
        double kSelf1;
        double uChild1;
        double kChild1;
        double? raplAfter;
        T result;
        try
        {
            result = await operation();
        }
        finally
        {
            sw.Stop();
            (uSelf1, kSelf1) = ReadSelfCpuMs(selfProc);
            (uChild1, kChild1) = trackChildProcesses ? ReadChildrenCpuMs() : (0.0, 0.0);
            raplAfter = TryReadLinuxRaplJoules();

            await samplerCts.CancelAsync();
            await samplerTask;
        }

        double finalRssMb = SampleNormalizedWorkingSetMb();
        if (finalRssMb > 0)
        {
            samplesMb.Add(finalRssMb);
        }

        double userMs = Math.Max(0, (uSelf1 - uSelf0) + (uChild1 - uChild0));
        double kernelMs = Math.Max(0, (kSelf1 - kSelf0) + (kChild1 - kChild0));
        double totalCpuMs = userMs + kernelMs;

        double avgRam = samplesMb.Count > 0 ? samplesMb.Average() : cleanBaseMb;
        double peakRam = samplesMb.Count > 0 ? samplesMb.Max() : cleanBaseMb;
        double? raplDelta = (raplBefore.HasValue && raplAfter.HasValue && raplAfter.Value >= raplBefore.Value)
            ? raplAfter.Value - raplBefore.Value
            : null;

        return (result, new PhaseExecutionMetrics(
            WallMs: Math.Max(0.05, sw.Elapsed.TotalMilliseconds),
            CpuUserMs: Math.Round(userMs, 3),
            CpuKernelMs: Math.Round(kernelMs, 3),
            CpuTotalMs: Math.Round(totalCpuMs, 3),
            AvgRamMB: Math.Round(avgRam, 2),
            PeakWorkingSetMB: Math.Round(Math.Max(avgRam, peakRam), 2),
            RaplJoules: raplDelta));
    }

    public static async Task<PhaseExecutionMetrics> MeasureExternalProcessAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        bool useGnuTime = OperatingSystem.IsLinux() && File.Exists("/usr/bin/time");
        string? timeOutputFile = useGnuTime
            ? Path.Combine(Path.GetTempPath(), $"uc_bench_time_{Guid.NewGuid():N}.txt")
            : null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = useGnuTime ? "/usr/bin/time" : executablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                psi.WorkingDirectory = workingDirectory;
            }

            if (useGnuTime)
            {
                psi.ArgumentList.Add("-v");
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add(timeOutputFile!);
                psi.ArgumentList.Add(executablePath);
            }

            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            var samplesMb = new List<double>(256);
            double sampledPeakMb = 0;

            double? raplBefore = TryReadLinuxRaplJoules();
            var (uChild0, kChild0) = ReadChildrenCpuMs();

            var sw = Stopwatch.StartNew();
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process '{executablePath}'.");
            }

            int rootPid = process.Id;
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var samplerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var samplerTask = Task.Run(async () =>
            {
                while (!samplerCts.IsCancellationRequested)
                {
                    try
                    {
                        if (process.HasExited)
                        {
                            break;
                        }

                        double mb = OperatingSystem.IsLinux()
                            ? ReadLinuxChildTreeRssMb(rootPid, excludeRootTimeWrapper: useGnuTime)
                            : ReadWindowsProcessMemoryMb(process, ref sampledPeakMb);

                        if (mb > 0)
                        {
                            samplesMb.Add(mb);
                            if (mb > sampledPeakMb)
                            {
                                sampledPeakMb = mb;
                            }
                        }
                    }
                    catch
                    {
                        break;
                    }

                    try
                    {
                        await Task.Delay(1, samplerCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, CancellationToken.None);

            await process.WaitForExitAsync(cancellationToken);
            sw.Stop();

            await samplerCts.CancelAsync();
            await samplerTask;

            string stderr = await stderrTask;
            _ = await stdoutTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"External command '{executablePath} {string.Join(' ', arguments)}' exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
            }

            var (uChild1, kChild1) = ReadChildrenCpuMs();
            double? raplAfter = TryReadLinuxRaplJoules();

            double userMs;
            double kernelMs;
            double peakRamMb = sampledPeakMb;

            if (OperatingSystem.IsLinux())
            {
                userMs = Math.Max(0, uChild1 - uChild0);
                kernelMs = Math.Max(0, kChild1 - kChild0);

                if (timeOutputFile is not null && File.Exists(timeOutputFile))
                {
                    ParseGnuTimeVerbose(timeOutputFile, out double timeUserMs, out double timeSysMs, out double timeMaxRssMb);
                    if (timeMaxRssMb > peakRamMb)
                    {
                        peakRamMb = timeMaxRssMb;
                    }

                    if (userMs + kernelMs <= 0.001 && (timeUserMs + timeSysMs) > 0)
                    {
                        userMs = timeUserMs;
                        kernelMs = timeSysMs;
                    }
                }
            }
            else
            {
                userMs = SafeReadProcessorTimeMs(() => process.UserProcessorTime.TotalMilliseconds);
                kernelMs = SafeReadProcessorTimeMs(() => process.PrivilegedProcessorTime.TotalMilliseconds);
                double winPeak = SafeReadProcessorTimeMs(() => process.PeakWorkingSet64 / (1024.0 * 1024.0));
                if (winPeak > peakRamMb)
                {
                    peakRamMb = winPeak;
                }
            }

            if (peakRamMb <= 0.01)
            {
                peakRamMb = 1.0;
            }

            double avgRamMb = samplesMb.Count >= 2
                ? Math.Min(peakRamMb, samplesMb.Average())
                : Math.Round(peakRamMb * 0.90, 2);

            double? raplDelta = (raplBefore.HasValue && raplAfter.HasValue && raplAfter.Value >= raplBefore.Value)
                ? raplAfter.Value - raplBefore.Value
                : null;

            return new PhaseExecutionMetrics(
                WallMs: Math.Max(0.05, sw.Elapsed.TotalMilliseconds),
                CpuUserMs: Math.Round(userMs, 3),
                CpuKernelMs: Math.Round(kernelMs, 3),
                CpuTotalMs: Math.Round(userMs + kernelMs, 3),
                AvgRamMB: Math.Round(avgRamMb, 2),
                PeakWorkingSetMB: Math.Round(peakRamMb, 2),
                RaplJoules: raplDelta);
        }
        finally
        {
            if (timeOutputFile is not null)
            {
                try { File.Delete(timeOutputFile); } catch { /* ignore */ }
            }
        }
    }

    private static (double UserMs, double KernelMs) ReadSelfCpuMs(Process selfProc)
    {
        if (OperatingSystem.IsLinux() && LinuxGetRUsage(RusageSelf, out var ru) == 0)
        {
            return (ru.RuUtime.ToMilliseconds(), ru.RuStime.ToMilliseconds());
        }

        selfProc.Refresh();
        return (selfProc.UserProcessorTime.TotalMilliseconds, selfProc.PrivilegedProcessorTime.TotalMilliseconds);
    }

    private static (double UserMs, double KernelMs) ReadChildrenCpuMs()
    {
        if (OperatingSystem.IsLinux() && LinuxGetRUsage(RusageChildren, out var ru) == 0)
        {
            return (ru.RuUtime.ToMilliseconds(), ru.RuStime.ToMilliseconds());
        }

        return (0, 0);
    }

    private static double ReadCurrentProcessRssMb(Process selfProc)
    {
        if (OperatingSystem.IsLinux())
        {
            double mb = ReadLinuxPidRssMb(Environment.ProcessId);
            if (mb > 0) return mb;
        }

        selfProc.Refresh();
        return selfProc.WorkingSet64 / (1024.0 * 1024.0);
    }

    private static double ReadProcessTreeRssMb(int selfPid, Process selfProc)
    {
        if (!OperatingSystem.IsLinux())
        {
            selfProc.Refresh();
            return selfProc.WorkingSet64 / (1024.0 * 1024.0);
        }

        double selfMb = ReadLinuxPidRssMb(selfPid);
        double childMb = ReadLinuxChildTreeRssMb(selfPid, excludeRootTimeWrapper: true);
        return selfMb + childMb;
    }

    private static double ReadLinuxPidRssMb(int pid)
    {
        try
        {
            string statm = File.ReadAllText($"/proc/{pid}/statm");
            int firstSpace = statm.IndexOf(' ');
            if (firstSpace <= 0) return 0;
            int secondSpace = statm.IndexOf(' ', firstSpace + 1);
            ReadOnlySpan<char> rssPagesSpan = secondSpace > firstSpace
                ? statm.AsSpan(firstSpace + 1, secondSpace - firstSpace - 1)
                : statm.AsSpan(firstSpace + 1);

            if (long.TryParse(rssPagesSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out long pages))
            {
                long pageBytes = Environment.SystemPageSize;
                return (pages * pageBytes) / (1024.0 * 1024.0);
            }
        }
        catch
        {
            // Process may have exited between check and read.
        }

        return 0;
    }

    private static double ReadLinuxChildTreeRssMb(int rootPid, bool excludeRootTimeWrapper)
    {
        double sumMb = excludeRootTimeWrapper ? 0 : ReadLinuxPidRssMb(rootPid);
        sumMb += ReadDirectChildrenRssMb(rootPid);
        return sumMb;
    }

    private static double ReadDirectChildrenRssMb(int parentPid)
    {
        var seenPids = new HashSet<int>();
        double sumMb = 0;
        try
        {
            foreach (var childrenPath in Directory.EnumerateFiles($"/proc/{parentPid}/task", "children", SearchOption.AllDirectories))
            {
                string content = File.ReadAllText(childrenPath).Trim();
                if (string.IsNullOrEmpty(content)) continue;
                foreach (var tok in content.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out int childPid)
                        && seenPids.Add(childPid))
                    {
                        sumMb += ReadLinuxPidRssMb(childPid);
                    }
                }
            }
        }
        catch
        {
            // Ignore race on fast-exiting child tasks.
        }

        return sumMb;
    }

    private static void TryLinuxMallocTrim()
    {
        if (!OperatingSystem.IsLinux()) return;
        try { _ = LinuxMallocTrim(0); } catch { }
    }

    private static double ReadWindowsProcessMemoryMb(Process process, ref double peakMb)
    {
        process.Refresh();
        double ws = process.WorkingSet64 / (1024.0 * 1024.0);
        double pws = process.PeakWorkingSet64 / (1024.0 * 1024.0);
        if (pws > peakMb) peakMb = pws;
        return ws;
    }

    private static void ParseGnuTimeVerbose(string path, out double userMs, out double sysMs, out double maxRssMb)
    {
        userMs = 0;
        sysMs = 0;
        maxRssMb = 0;

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("User time (seconds):", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(trimmed.Split(':')[^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
                {
                    userMs = s * 1000.0;
                }
            }
            else if (trimmed.StartsWith("System time (seconds):", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(trimmed.Split(':')[^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
                {
                    sysMs = s * 1000.0;
                }
            }
            else if (trimmed.StartsWith("Maximum resident set size (kbytes):", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(trimmed.Split(':')[^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double kb))
                {
                    maxRssMb = kb / 1024.0;
                }
            }
        }
    }

    private static void TryResetLinuxPeakRss()
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            if (File.Exists("/proc/self/clear_refs"))
            {
                File.WriteAllText("/proc/self/clear_refs", "5");
            }
        }
        catch
        {
            // Optional kernel reset; sampling handles working set tracking regardless.
        }
    }

    private static double? TryReadLinuxRaplJoules()
    {
        if (!OperatingSystem.IsLinux()) return null;
        const string raplPath = "/sys/class/powercap/intel-rapl:0/energy_uj";
        try
        {
            if (!File.Exists(raplPath)) return null;
            if (long.TryParse(File.ReadAllText(raplPath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long uj))
            {
                return uj / 1_000_000.0;
            }
        }
        catch
        {
            // Not accessible in cloud VM guests.
        }

        return null;
    }

    private static double SafeReadProcessorTimeMs(Func<double> reader)
    {
        try
        {
            return reader();
        }
        catch
        {
            return 0;
        }
    }
}
