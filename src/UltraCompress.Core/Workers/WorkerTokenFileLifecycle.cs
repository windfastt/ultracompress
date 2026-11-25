using UltraCompress.Core;

namespace UltraCompress.Core.Workers;

/// <summary>
/// Installer and operator helpers for the LAN worker token file.
/// </summary>
public static class WorkerTokenFileLifecycle
{
    public static string ResolveReadPath(string? configuredPath) =>
        string.IsNullOrWhiteSpace(configuredPath)
            ? EnginePaths.GetDefaultWorkerTokenPath()
            : Path.GetFullPath(configuredPath);

    public static void EnsureParentDirectory(string tokenPath)
    {
        var directory = Path.GetDirectoryName(tokenPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Writes a single-line token file suitable for engine + worker agent consumption.
    /// Sets Unix mode <c>600</c> when supported.
    /// </summary>
    public static void WriteTokenFile(string tokenPath, string token, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        if (File.Exists(tokenPath) && !overwrite)
        {
            throw new InvalidOperationException($"Worker token file already exists: {tokenPath}");
        }

        EnsureParentDirectory(tokenPath);
        File.WriteAllText(tokenPath, token.Trim() + Environment.NewLine);

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (PlatformNotSupportedException)
            {
                // e.g. older TFMs on non-Unix hosts — best effort only
            }
        }
    }

    public static string ReadTokenOrEmpty(string tokenPath) =>
        WorkerTokenValidator.LoadTokenFromFile(tokenPath);

    public static bool TokenFileExists(string? configuredPath = null)
    {
        var path = ResolveReadPath(configuredPath);
        return File.Exists(path);
    }
}