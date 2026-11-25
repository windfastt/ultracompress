namespace UltraCompress.Desktop.Presentation.Services;

public interface IDiskSpaceProvider
{
    long? TryGetAvailableFreeBytes(string directoryPath);

    bool IsDirectoryWritable(string directoryPath);
}

public sealed class SystemDiskSpaceProvider : IDiskSpaceProvider
{
    public long? TryGetAvailableFreeBytes(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        try
        {
            var probePath = ResolveExistingAncestorOrRoot(Path.GetFullPath(directoryPath));
            var drive = new DriveInfo(probePath);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }

    public bool IsDirectoryWritable(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(directoryPath);
            var existingAncestor = ResolveExistingAncestorOrRoot(fullPath);
            return Directory.Exists(existingAncestor);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveExistingAncestorOrRoot(string fullPath)
    {
        var current = fullPath;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        return string.IsNullOrEmpty(current) ? Path.GetPathRoot(fullPath) ?? fullPath : current;
    }
}
