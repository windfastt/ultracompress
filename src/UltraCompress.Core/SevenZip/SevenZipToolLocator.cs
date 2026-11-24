namespace UltraCompress.Core.SevenZip;

/// <summary>
/// Resolves the path to the bundled or system <c>7za</c> executable.
/// </summary>
public static class SevenZipToolLocator
{
    public const string DefaultRelativePath = "tools/7z/7za.exe";
    public const string DefaultRelativePathUnix = "tools/7z/7za";
    public const string EnvironmentVariableName = "ULTRACOMPRESS_7ZA_PATH";

    /// <summary>
    /// Attempts to resolve <c>7za</c>. Returns null when not found (installer not laid out, CI without p7zip).
    /// </summary>
    public static string? TryResolve(string? installRoot = null)
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        installRoot ??= TryFindInstallRoot();
        if (installRoot is not null)
        {
            foreach (var relative in new[] { DefaultRelativePath, DefaultRelativePathUnix })
            {
                var candidate = Path.Combine(installRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return TryFindOnPath("7za")
            ?? TryFindOnPath("7za.exe")
            ?? TryFindOnPath("7z")
            ?? TryFindOnPath("7z.exe")
            ?? TryDefaultWindowsInstall();
    }

    public static string ResolveOrThrow(string? installRoot = null)
    {
        return TryResolve(installRoot)
            ?? throw new FileNotFoundException(
                $"7-Zip standalone CLI not found. Set {EnvironmentVariableName}, install p7zip, or place 7za under tools/7z/ (see tools/7z/README.md).");
    }

    private static string? TryFindInstallRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tools", "7z")))
            {
                return dir.FullName;
            }

            if (File.Exists(Path.Combine(dir.FullName, "UltraCompress.sln")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private static string? TryDefaultWindowsInstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var name in new[] { "7za.exe", "7z.exe" })
        {
            var candidate = Path.Combine(programFiles, "7-Zip", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
    private static string? TryFindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        foreach (var segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(segment.Trim(), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}