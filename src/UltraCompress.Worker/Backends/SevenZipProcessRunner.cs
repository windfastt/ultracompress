using System.Diagnostics;
using UltraCompress.Core;
using UltraCompress.Core.SevenZip;

namespace UltraCompress.Worker.Backends;

public sealed class SevenZipProcessRunner
{
    private readonly string _executablePath;

    public SevenZipProcessRunner(string? executablePath = null)
    {
        _executablePath = executablePath ?? SevenZipToolLocator.ResolveOrThrow();
    }

    public string ExecutablePath => _executablePath;

    public async Task<byte[]> CreateArchiveAsync(
        ReadOnlyMemory<byte> payload,
        string entryFileName,
        CompressStep step,
        CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ultracompress", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var entryPath = Path.Combine(tempDir, entryFileName);
            await File.WriteAllBytesAsync(entryPath, payload.ToArray(), cancellationToken);

            var archivePath = Path.Combine(tempDir, "out.7z");
            var args = SevenZipCommandLine.BuildAddArgs(archivePath, new[] { entryPath }, step);
            await RunAsync(args, expectStdoutData: false, cancellationToken);

            if (!File.Exists(archivePath))
            {
                throw new InvalidOperationException("7za did not produce an archive.");
            }

            return await File.ReadAllBytesAsync(archivePath, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    public async Task<byte[]> ExtractFirstEntryAsync(
        ReadOnlyMemory<byte> archive,
        string? entryFileName,
        CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ultracompress", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var archivePath = Path.Combine(tempDir, "in.7z");
            await File.WriteAllBytesAsync(archivePath, archive.ToArray(), cancellationToken);

            var args = SevenZipCommandLine.BuildExtractStdoutArgs(archivePath, entryFileName);
            return await RunAsync(args, expectStdoutData: true, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task<byte[]> RunAsync(IReadOnlyList<string> args, bool expectStdoutData, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start 7za at '{_executablePath}'.");
        }

        var stdoutTask = expectStdoutData
            ? ReadStreamAsync(process.StandardOutput.BaseStream, cancellationToken)
            : Task.FromResult(Array.Empty<byte>());

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore kill races.
            }
        });

        await process.WaitForExitAsync(cancellationToken);

        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"7za exited with code {process.ExitCode}. stderr: {TrimForMessage(stderr)}");
        }

        if (!expectStdoutData)
        {
            return Array.Empty<byte>();
        }

        var stdout = await stdoutTask;
        if (stdout.Length == 0)
        {
            throw new InvalidOperationException($"7za produced no stdout. stderr: {TrimForMessage(stderr)}");
        }

        return stdout;
    }

    private static async Task<byte[]> ReadStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return ms.ToArray();
    }

    private static string TrimForMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        const int max = 500;
        return text.Length <= max ? text.Trim() : text[..max].Trim() + "…";
    }
}