namespace UltraCompress.Core;

public static class EnginePaths
{
    public const int DefaultGrpcPort = 17_832;

    public static string GetLogDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "UltraCompress", "logs");
        }

        var overrideDir = Environment.GetEnvironmentVariable("ULTRACOMPRESS_LOG_DIR");
        return string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(Path.GetTempPath(), "ultracompress", "logs")
            : overrideDir;
    }

    public static string GetStagingDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "UltraCompress", "staging");
        }

        return Path.Combine(Path.GetTempPath(), "ultracompress", "staging");
    }
}