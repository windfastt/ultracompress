namespace UltraCompress.Core.Ipc;

/// <summary>Well-known names for tray ↔ engine IPC on local machine.</summary>
public static class EnginePipeNames
{
    /// <summary>Named pipe identifier (no \\.\pipe\ prefix).</summary>
    public const string PipeName = "UltraCompress.Engine";

    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan DefaultReadWriteTimeout = TimeSpan.FromSeconds(10);
}