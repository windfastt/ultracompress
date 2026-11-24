using System.Text;

namespace UltraCompress.Core.SevenZip;

public static class SevenZipCommandLine
{
    public static IReadOnlyList<string> BuildAddArgs(string archivePath, IReadOnlyList<string> entryPaths, CompressStep step)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        if (entryPaths.Count == 0)
        {
            throw new ArgumentException("At least one input path is required.", nameof(entryPaths));
        }

        var level = MapLevel(step.Level);
        var method = string.IsNullOrWhiteSpace(step.Method) ? "LZMA2" : step.Method.Trim().ToUpperInvariant();

        var args = new List<string>
        {
            "a",
            "-t7z",
            "-bso0",
            "-bsp0",
            $"-m0={method}",
            $"-mx={level}",
        };

        args.Add(step.Solid is true ? "-ms=on" : "-ms=off");
        args.Add(FormatMultiThreadArg(step.Threads));
        args.Add(archivePath);
        args.AddRange(entryPaths);
        return args;
    }

    public static IReadOnlyList<string> BuildExtractStdoutArgs(string archivePath, string? singleEntryName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);

        var args = new List<string>
        {
            "e",
            "-so",
            "-bso0",
            "-bsp0",
            archivePath,
        };

        if (!string.IsNullOrWhiteSpace(singleEntryName))
        {
            args.Add(singleEntryName);
        }

        return args;
    }

    public static string FormatArgumentList(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (var arg in args)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            if (arg.Contains(' ') || arg.Contains('"'))
            {
                sb.Append('"').Append(arg.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                sb.Append(arg);
            }
        }

        return sb.ToString();
    }

    internal static int MapLevel(int stepLevel) => Math.Clamp(stepLevel, 0, 9);

    private static string FormatMultiThreadArg(int? threads)
    {
        if (threads is null or <= 0)
        {
            return "-mmt=on";
        }

        return $"-mmt={threads}";
    }
}