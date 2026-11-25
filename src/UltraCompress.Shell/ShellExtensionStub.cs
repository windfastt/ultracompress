using UltraCompress.Core;

namespace UltraCompress.Shell;

/// <summary>
/// Placeholder for native <c>IExplorerCommand</c> activation until the C++ surrogate DLL lands.
/// </summary>
public static class ShellExtensionStub
{
    public static string Describe() =>
        $"{ShellConstants.MenuTitle} — sparse package + COM DLL planned (Phase 4).";

    public static Task InvokeExplorerCommandAsync(
        IReadOnlyList<string> selectedPaths,
        CompressionProfile profile = CompressionProfile.Archive,
        string? queueDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var client = new ShellEnqueueClient(queueDirectory);
        var request = new ShellEnqueueRequest(
            selectedPaths,
            profile,
            DateTimeOffset.UtcNow);
        return client.EnqueueAsync(request, cancellationToken);
    }
}