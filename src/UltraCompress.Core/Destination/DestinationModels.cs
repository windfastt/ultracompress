using System.Text.Json.Serialization;

namespace UltraCompress.Core.Destination;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DestinationMode
{
    SideBySide = 0,
    CustomDirectory = 1,
    ArchiveSubfolder = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DestinationConflictPolicy
{
    Fail = 0,
    Overwrite = 1,
    RenameNumbered = 2,
    SkipExisting = 3,
}

public interface IDiskSpaceProvider
{
    long GetAvailableFreeBytes(string targetPath);
}

public sealed class SystemDiskSpaceProvider : IDiskSpaceProvider
{
    public static readonly SystemDiskSpaceProvider Instance = new();

    public long GetAvailableFreeBytes(string targetPath)
    {
        var fullPath = Path.GetFullPath(targetPath);
        var probeDir = Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath) ?? fullPath;

        // Walk up to the nearest existing directory so DriveInfo works on Linux mount points as well as Windows drive roots.
        var current = probeDir;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent == current)
            {
                break;
            }

            current = parent;
        }

        var targetForDrive = !string.IsNullOrEmpty(current) && Directory.Exists(current)
            ? current
            : Path.GetPathRoot(fullPath) ?? "/";

        var drive = new DriveInfo(targetForDrive);
        return drive.AvailableFreeSpace;
    }
}
