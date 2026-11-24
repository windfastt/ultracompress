using System.Diagnostics;
using UltraCompress.Core;

namespace UltraCompress.Engine.Services;

public sealed class WorkerProcessRunner
{
    private readonly ILogger<WorkerProcessRunner> _logger;
    private readonly string _workerDllPath;

    public WorkerProcessRunner(ILogger<WorkerProcessRunner> logger, IHostEnvironment environment)
    {
        _logger = logger;
        _workerDllPath = ResolveWorkerDll(environment);
    }

    public async Task<WorkerRunResult> RunCompressAsync(
        string inputPath,
        CompressionProfile profile,
        string? outputPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_workerDllPath))
        {
            throw new FileNotFoundException("UltraCompress.Worker assembly was not found beside the engine.", _workerDllPath);
        }

        var args = new List<string>
        {
            $"\"{_workerDllPath}\"",
            "compress",
            "-i",
            $"\"{inputPath}\"",
            "-p",
            profile.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            args.AddRange(new[] { "-o", $"\"{outputPath}\"" });
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = string.Join(' ', args),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
        {
            return WorkerRunResult.CreateFailure(-1, "Failed to start worker process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Worker exit {ExitCode}. stdout: {Stdout} stderr: {Stderr}", process.ExitCode, stdout, stderr);
        }

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return WorkerRunResult.CreateFailure(process.ExitCode, detail.Trim());
        }

        var parsed = WorkerOutputParser.Parse(stdout);
        return WorkerRunResult.CreateSuccess(parsed.OutputPath, parsed.OriginalBytes, parsed.CompressedBytes, stdout.Trim());
    }

    private static string ResolveWorkerDll(IHostEnvironment environment)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, "UltraCompress.Worker.dll");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var devCandidate = Path.GetFullPath(
            Path.Combine(environment.ContentRootPath, "..", "UltraCompress.Worker", "bin", "Debug", "net8.0", "UltraCompress.Worker.dll"));
        return devCandidate;
    }
}

public sealed record WorkerRunResult(
    bool Succeeded,
    int ExitCode,
    string? OutputPath,
    long? OriginalBytes,
    long? CompressedBytes,
    string Detail)
{
    public static WorkerRunResult CreateSuccess(string? outputPath, long? original, long? compressed, string detail) =>
        new(true, 0, outputPath, original, compressed, detail);

    public static WorkerRunResult CreateFailure(int exitCode, string detail) =>
        new(false, exitCode, null, null, null, detail);
}

internal static class WorkerOutputParser
{
    public static (string? OutputPath, long? OriginalBytes, long? CompressedBytes) Parse(string stdout)
    {
        string? outputPath = null;
        long? original = null;
        long? compressed = null;

        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.StartsWith("Output:", StringComparison.OrdinalIgnoreCase))
            {
                outputPath = rawLine[(rawLine.IndexOf(':') + 1)..].Trim();
            }
            else if (rawLine.StartsWith("Size:", StringComparison.OrdinalIgnoreCase))
            {
                var arrow = rawLine.IndexOf("->", StringComparison.Ordinal);
                if (arrow < 0)
                {
                    continue;
                }

                var left = rawLine[(rawLine.IndexOf(':') + 1)..arrow].Trim();
                var rightPart = rawLine[(arrow + 2)..].Trim();
                var space = rightPart.IndexOf(' ');
                var right = space < 0 ? rightPart : rightPart[..space];

                if (long.TryParse(left, out var o))
                {
                    original = o;
                }

                if (long.TryParse(right, out var c))
                {
                    compressed = c;
                }
            }
        }

        return (outputPath, original, compressed);
    }
}