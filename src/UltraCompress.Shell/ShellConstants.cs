namespace UltraCompress.Shell;

/// <summary>
/// Stable identifiers for sparse MSIX registration and future <c>IExplorerCommand</c> COM class.
/// </summary>
public static class ShellConstants
{
    public const string ExplorerCommandClsid = "A7B8C9D0-E1F2-4A5B-9C8D-7E6F5A4B3C2D";

    public const string VerbId = "UltraCompressHere";

    public const string MenuTitle = "UltraCompress here…";

    public const string SparsePackageName = "UltraCompress.Shell";

    public const string SparsePackageFamily = "UltraCompress.Shell_8wekyb3d8bbwe";

    public const string DefaultQueueFileName = "inbox.jsonl";

    public static string DefaultQueueDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UltraCompress",
        "queue");
}