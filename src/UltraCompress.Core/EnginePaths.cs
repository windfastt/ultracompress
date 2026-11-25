namespace UltraCompress.Core;

public static class EnginePaths
{
    public const int DefaultGrpcPort = 17_832;

    public const string ProductFolderName = "UltraCompress";

    public const string WorkerTokenFileName = "worker.token";

    /// <summary>
    /// Machine-wide data root on Windows (<c>%ProgramData%\UltraCompress</c>).
    /// The worker token file lives here so the Engine service and tray can read it without a per-user profile.
    /// </summary>
    /// <remarks>
    /// <b>ACL (Windows, production):</b> restrict <c>worker.token</c> to
    /// <c>SYSTEM</c>, <c>Administrators</c>, and the UltraCompress Engine service SID.
    /// Deny read for <c>Users</c> so LAN tokens are not world-readable on shared lab machines.
    /// WiX should create the folder with inherited ProgramData ACLs, then tighten the file after first token write.
    /// </remarks>
    public static string GetProgramDataRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, ProductFolderName);
        }

        var overrideRoot = Environment.GetEnvironmentVariable("ULTRACOMPRESS_PROGRAM_DATA");
        return string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(Path.GetTempPath(), ProductFolderName)
            : Path.Combine(overrideRoot, ProductFolderName);
    }

    public static string GetLogDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(GetProgramDataRoot(), "logs");
        }

        var overrideDir = Environment.GetEnvironmentVariable("ULTRACOMPRESS_LOG_DIR");
        return string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(GetProgramDataRoot(), "logs")
            : overrideDir;
    }

    public static string GetStagingDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, ProductFolderName, "staging");
        }

        return Path.Combine(Path.GetTempPath(), ProductFolderName, "staging");
    }

    /// <summary>
    /// Default path for the distributed-worker shared secret file (first non-comment line is the token).
    /// </summary>
    public static string GetDefaultWorkerTokenPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ULTRACOMPRESS_WORKER_TOKEN_FILE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv.Trim());
        }

        return Path.Combine(GetProgramDataRoot(), WorkerTokenFileName);
    }
}