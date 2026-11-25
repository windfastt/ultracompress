using System.Diagnostics;
using UltraCompress.Benchmarks.Corpus;
using UltraCompress.Benchmarks.Instrumentation;
using UltraCompress.Benchmarks.Models;
using UltraCompress.Core.SevenZip;

namespace UltraCompress.Benchmarks.Runners;

public enum CompetitorKind
{
    SevenZipMx5,
    SevenZipMx9,
    Zstd3,
    Zstd19,
    Brotli6,
    Brotli11,
    Gzip6,
    LibDeflate6,
    LibDeflate12,
}

/// <summary>
/// Executes real external competitor CLI binaries on disk inputs with exact flag & version capture
/// and full SHA-256 directory roundtrip verification.
/// </summary>
public sealed class ExternalCliCompetitorRunner : IBenchmarkToolRunner
{
    private readonly CompetitorKind _kind;
    private readonly string _executablePath;

    public ExternalCliCompetitorRunner(CompetitorKind kind, string executablePath, string version)
    {
        _kind = kind;
        _executablePath = executablePath;
        string exeName = Path.GetFileNameWithoutExtension(executablePath);

        Descriptor = kind switch
        {
            CompetitorKind.SevenZipMx5 => new ToolDescriptor
            {
                ToolId = "7za-mx5",
                DisplayName = "7-Zip (LZMA2 -mx=5)",
                ToolFamily = "7-Zip",
                Version = version,
                CompressFlags = $"{exeName} a -t7z -m0=LZMA2 -mx=5 -ms=on -mmt=on",
                DecompressFlags = $"{exeName} x -y",
                ExecutionMode = "ExternalChildProcess",
            },
            CompetitorKind.SevenZipMx9 => new ToolDescriptor
            {
                ToolId = "7za-mx9",
                DisplayName = "7-Zip (LZMA2 -mx=9)",
                ToolFamily = "7-Zip",
                Version = version,
                CompressFlags = $"{exeName} a -t7z -m0=LZMA2 -mx=9 -ms=on -mmt=on",
                DecompressFlags = $"{exeName} x -y",
                ExecutionMode = "ExternalChildProcess",
            },
            CompetitorKind.Zstd3 => new ToolDescriptor
            {
                ToolId = "zstd-3",
                DisplayName = "Zstandard (zstd -3)",
                ToolFamily = "Zstandard",
                Version = version,
                CompressFlags = "zstd -3 -f -q (solid POSIX tar)",
                DecompressFlags = "zstd -d -f -q",
                ExecutionMode = "ExternalChildProcess",
            },
            CompetitorKind.Zstd19 => new ToolDescriptor
            {
                ToolId = "zstd-19",
                DisplayName = "Zstandard (zstd -19)",
                ToolFamily = "Zstandard",
                Version = version,
                CompressFlags = "zstd -19 -f -q (solid POSIX tar)",
                DecompressFlags = "zstd -d -f -q",
                ExecutionMode = "ExternalChildProcess",
            },
            CompetitorKind.Brotli6 => new ToolDescriptor
            {
                ToolId = "brotli-q6",
                DisplayName = "Brotli (brotli -q 6)",
                ToolFamily = "Brotli",
                Version = version,
                CompressFlags = "brotli -q 6 -f (solid POSIX tar)",
                DecompressFlags = "brotli -d -f",
                ExecutionMode = "ExternalChildProcess",
            },
            CompetitorKind.Brotli11 => new ToolDescriptor
            {
                ToolId = "brotli-q11",
                DisplayName = "Brotli (brotli -q 11)",
                ToolFamily = "Brotli",
                Version = version,
                CompressFlags = "brotli -q 11 -f (solid POSIX tar)",
                DecompressFlags = "brotli -d -f",
                ExecutionMode = "ExternalChildProcess",
            },
            CompetitorKind.Gzip6 => new ToolDescriptor
            {
                ToolId = "gzip-6",
                DisplayName = "GZip (gzip -6)",
                ToolFamily = "GZip",
                Version = version,
                CompressFlags = "gzip -6 -k -f (solid POSIX tar)",
                DecompressFlags = "gzip -d -k -f",
                ExecutionMode = "ExternalChildProcess",
            },
            CompetitorKind.LibDeflate6 => new ToolDescriptor
            {
                ToolId = "libdeflate-6",
                DisplayName = "LibDeflate (libdeflate-gzip -6)",
                ToolFamily = "LibDeflate",
                Version = version,
                CompressFlags = "libdeflate-gzip -6 -k -f (solid POSIX tar)",
                DecompressFlags = "libdeflate-gzip -d -k -f",
                ExecutionMode = "ExternalChildProcess",
            },
            CompetitorKind.LibDeflate12 => new ToolDescriptor
            {
                ToolId = "libdeflate-12",
                DisplayName = "LibDeflate (libdeflate-gzip -12)",
                ToolFamily = "LibDeflate",
                Version = version,
                CompressFlags = "libdeflate-gzip -12 -k -f (solid POSIX tar)",
                DecompressFlags = "libdeflate-gzip -d -k -f",
                ExecutionMode = "ExternalChildProcess",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    public ToolDescriptor Descriptor { get; }

    public static IReadOnlyList<ExternalCliCompetitorRunner> DiscoverAll(bool requireAllCompetitors)
    {
        var list = new List<ExternalCliCompetitorRunner>();

        // 1. 7-Zip (7za / 7z)
        string? sevenZip = SevenZipToolLocator.TryResolve();
        if (sevenZip is not null)
        {
            string ver = ProbeVersionLine(sevenZip, Array.Empty<string>(), linePredicate: l => l.Contains("7-Zip", StringComparison.OrdinalIgnoreCase));
            list.Add(new ExternalCliCompetitorRunner(CompetitorKind.SevenZipMx5, sevenZip, ver));
            list.Add(new ExternalCliCompetitorRunner(CompetitorKind.SevenZipMx9, sevenZip, ver));
        }
        else if (requireAllCompetitors)
        {
            throw new FileNotFoundException("Required competitor CLI '7za'/'7z' was not found on PATH.");
        }

        // 2. Zstandard (zstd)
        string? zstd = FindExecutable("zstd");
        if (zstd is not null)
        {
            string ver = ProbeVersionLine(zstd, ["--version"]);
            list.Add(new ExternalCliCompetitorRunner(CompetitorKind.Zstd3, zstd, ver));
            list.Add(new ExternalCliCompetitorRunner(CompetitorKind.Zstd19, zstd, ver));
        }
        else if (requireAllCompetitors)
        {
            throw new FileNotFoundException("Required competitor CLI 'zstd' was not found on PATH.");
        }

        // 3. Brotli (brotli)
        string? brotli = FindExecutable("brotli");
        if (brotli is not null)
        {
            string ver = ProbeVersionLine(brotli, ["--version"]);
            list.Add(new ExternalCliCompetitorRunner(CompetitorKind.Brotli6, brotli, ver));
            list.Add(new ExternalCliCompetitorRunner(CompetitorKind.Brotli11, brotli, ver));
        }
        else if (requireAllCompetitors)
        {
            throw new FileNotFoundException("Required competitor CLI 'brotli' was not found on PATH.");
        }

        // 4. GZip (gzip)
        string? gzip = FindExecutable("gzip");
        if (gzip is not null)
        {
            string ver = ProbeVersionLine(gzip, ["--version"]);
            list.Add(new ExternalCliCompetitorRunner(CompetitorKind.Gzip6, gzip, ver));
        }
        else if (requireAllCompetitors)
        {
            throw new FileNotFoundException("Required competitor CLI 'gzip' was not found on PATH.");
        }

        // 5. LibDeflate CLI (libdeflate-gzip)
        string? libdeflate = FindExecutable("libdeflate-gzip");
        if (libdeflate is not null)
        {
            string ver = ProbeVersionLine(libdeflate, ["-V"]);
            list.Add(new ExternalCliCompetitorRunner(CompetitorKind.LibDeflate6, libdeflate, ver));
            list.Add(new ExternalCliCompetitorRunner(CompetitorKind.LibDeflate12, libdeflate, ver));
        }
        else if (requireAllCompetitors)
        {
            throw new FileNotFoundException("Required competitor CLI 'libdeflate-gzip' was not found on PATH.");
        }

        return list;
    }

    public async Task<SingleTrialOutcome> ExecuteTrialAsync(
        CorpusCategoryManifest category,
        string scratchDirectory,
        CancellationToken cancellationToken = default)
    {
        string extractDir = Path.Combine(scratchDirectory, "restored");
        Directory.CreateDirectory(extractDir);

        if (_kind is CompetitorKind.SevenZipMx5 or CompetitorKind.SevenZipMx9)
        {
            int mx = _kind == CompetitorKind.SevenZipMx5 ? 5 : 9;
            string archivePath = Path.Combine(scratchDirectory, $"{category.Category}.7z");

            var compMetrics = await ProcessMetricsCollector.MeasureExternalProcessAsync(
                _executablePath,
                ["a", "-t7z", "-m0=LZMA2", $"-mx={mx}", "-ms=on", "-mmt=on", "-bso0", "-bsp0", archivePath, "."],
                workingDirectory: category.DirectoryPath,
                cancellationToken: cancellationToken);

            long compBytes = new FileInfo(archivePath).Length;

            var decompMetrics = await ProcessMetricsCollector.MeasureExternalProcessAsync(
                _executablePath,
                ["x", "-y", "-bso0", "-bsp0", $"-o{extractDir}", archivePath],
                workingDirectory: scratchDirectory,
                cancellationToken: cancellationToken);

            return VerifyAndBuildOutcome(category, extractDir, compBytes, compMetrics, decompMetrics);
        }

        // Stream compressors (zstd, brotli, gzip, libdeflate-gzip) operate on the canonical POSIX USTAR tar of the category.
        string stagedTar = Path.Combine(scratchDirectory, "input.tar");
        File.Copy(category.TarFilePath, stagedTar, overwrite: true);

        PhaseExecutionMetrics compressMetrics;
        PhaseExecutionMetrics decompressMetrics;
        string compressedFilePath;
        string restoredTarPath;

        switch (_kind)
        {
            case CompetitorKind.Zstd3:
            case CompetitorKind.Zstd19:
            {
                int level = _kind == CompetitorKind.Zstd3 ? 3 : 19;
                compressedFilePath = Path.Combine(scratchDirectory, "archive.tar.zst");
                restoredTarPath = Path.Combine(scratchDirectory, "restored.tar");

                compressMetrics = await ProcessMetricsCollector.MeasureExternalProcessAsync(
                    _executablePath,
                    [$"-{level}", "-f", "-q", stagedTar, "-o", compressedFilePath],
                    cancellationToken: cancellationToken);

                decompressMetrics = await ProcessMetricsCollector.MeasureExternalProcessAsync(
                    _executablePath,
                    ["-d", "-f", "-q", compressedFilePath, "-o", restoredTarPath],
                    cancellationToken: cancellationToken);
                break;
            }
            case CompetitorKind.Brotli6:
            case CompetitorKind.Brotli11:
            {
                int q = _kind == CompetitorKind.Brotli6 ? 6 : 11;
                compressedFilePath = Path.Combine(scratchDirectory, "archive.tar.br");
                restoredTarPath = Path.Combine(scratchDirectory, "restored.tar");

                compressMetrics = await ProcessMetricsCollector.MeasureExternalProcessAsync(
                    _executablePath,
                    ["-q", q.ToString(), "-f", stagedTar, "-o", compressedFilePath],
                    cancellationToken: cancellationToken);

                decompressMetrics = await ProcessMetricsCollector.MeasureExternalProcessAsync(
                    _executablePath,
                    ["-d", "-f", compressedFilePath, "-o", restoredTarPath],
                    cancellationToken: cancellationToken);
                break;
            }
            case CompetitorKind.Gzip6:
            case CompetitorKind.LibDeflate6:
            case CompetitorKind.LibDeflate12:
            {
                int level = _kind == CompetitorKind.LibDeflate12 ? 12 : 6;
                compressedFilePath = stagedTar + ".gz";

                compressMetrics = await ProcessMetricsCollector.MeasureExternalProcessAsync(
                    _executablePath,
                    [$"-{level}", "-k", "-f", stagedTar],
                    cancellationToken: cancellationToken);

                // Move compressed .gz into a clean decompression filename so -d -k writes to a fresh file.
                string decompInputGz = Path.Combine(scratchDirectory, "restored.tar.gz");
                restoredTarPath = Path.Combine(scratchDirectory, "restored.tar");
                File.Move(compressedFilePath, decompInputGz, overwrite: true);
                compressedFilePath = decompInputGz;

                decompressMetrics = await ProcessMetricsCollector.MeasureExternalProcessAsync(
                    _executablePath,
                    ["-d", "-k", "-f", decompInputGz],
                    cancellationToken: cancellationToken);
                break;
            }
            default:
                throw new InvalidOperationException($"Unhandled competitor kind {_kind}.");
        }

        long compressedLength = new FileInfo(compressedFilePath).Length;

        // Unpack restored USTAR archive and verify every file's SHA-256 against the category manifest.
        DeterministicTarArchive.ExtractToDirectory(restoredTarPath, extractDir);
        return VerifyAndBuildOutcome(category, extractDir, compressedLength, compressMetrics, decompressMetrics);
    }

    private SingleTrialOutcome VerifyAndBuildOutcome(
        CorpusCategoryManifest category,
        string extractDir,
        long compressedBytes,
        PhaseExecutionMetrics compMetrics,
        PhaseExecutionMetrics decompMetrics)
    {
        var (restoredHash, restoredBytes, restoredCount) = DeterministicTarArchive.ComputeDirectoryManifest(extractDir);
        bool verified = restoredCount == category.FileCount
            && restoredBytes == category.OriginalBytes
            && string.Equals(restoredHash, category.ManifestSha256, StringComparison.Ordinal);

        if (!verified)
        {
            throw new InvalidDataException(
                $"SHA-256 roundtrip verification failed for {Descriptor.DisplayName} on '{category.Category}'.");
        }

        return new SingleTrialOutcome(
            CompressedBytes: compressedBytes,
            RoundtripSha256Verified: true,
            VerifiedManifestSha256: restoredHash,
            CompressMetrics: compMetrics,
            DecompressMetrics: decompMetrics,
            StrategySummary: null);
    }

    private static string? FindExecutable(string baseName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        string[] names = OperatingSystem.IsWindows()
            ? [$"{baseName}.exe", baseName]
            : [baseName];

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                string candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static string ProbeVersionLine(
        string executable,
        IReadOnlyList<string> args,
        Func<string, bool>? linePredicate = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return "unknown";
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            var lines = (stdout + "\n" + stderr)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (linePredicate is not null)
            {
                var matched = lines.FirstOrDefault(linePredicate);
                if (matched is not null) return matched;
            }

            return lines.FirstOrDefault() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
