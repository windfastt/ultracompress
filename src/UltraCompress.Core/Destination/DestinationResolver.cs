using UltraCompress.Core.Errors;

namespace UltraCompress.Core.Destination;

public sealed record ResolvedPackDestination(
    string FinalArchivePath,
    string StagingPartPath,
    bool ShouldSkipExisting,
    bool AllowOverwrite,
    long AvailableFreeBytes,
    long EstimatedRequiredBytes);

public sealed record ResolvedExtractDestination(
    string DestinationDirectory,
    long AvailableFreeBytes,
    long EstimatedRequiredBytes);

/// <summary>
/// Resolves archive pack/extract output locations across <see cref="DestinationMode"/> policies,
/// enforces <see cref="DestinationConflictPolicy"/>, and runs pre-flight free-space and permission checks.
/// </summary>
public sealed class DestinationResolver
{
    private const long SafetyMarginBytes = 64 * 1024;
    private readonly IDiskSpaceProvider _diskSpaceProvider;

    public DestinationResolver(IDiskSpaceProvider? diskSpaceProvider = null)
    {
        _diskSpaceProvider = diskSpaceProvider ?? SystemDiskSpaceProvider.Instance;
    }

    public ResolvedPackDestination ResolveAndValidatePack(
        IReadOnlyList<string> inputPaths,
        DestinationMode mode = DestinationMode.SideBySide,
        DestinationConflictPolicy conflictPolicy = DestinationConflictPolicy.Fail,
        string? customOutputPathOrDirectory = null,
        string? archiveSubfolderName = null,
        long? estimatedInputBytes = null)
    {
        if (inputPaths is null || inputPaths.Count == 0)
        {
            throw UltraCompressErrors.EmptyInputSet();
        }

        var cleanedInputs = inputPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .ToList();

        if (cleanedInputs.Count == 0)
        {
            throw UltraCompressErrors.EmptyInputSet();
        }

        foreach (var path in cleanedInputs)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                throw UltraCompressErrors.InputNotFound(path);
            }
        }

        string candidateArchivePath = ComputeInitialPackCandidatePath(
            cleanedInputs,
            mode,
            customOutputPathOrDirectory,
            archiveSubfolderName);

        string finalPath = candidateArchivePath;
        bool shouldSkip = false;
        bool allowOverwrite = false;

        if (File.Exists(candidateArchivePath))
        {
            switch (conflictPolicy)
            {
                case DestinationConflictPolicy.Fail:
                    throw UltraCompressErrors.DestinationAlreadyExists(candidateArchivePath);

                case DestinationConflictPolicy.Overwrite:
                    allowOverwrite = true;
                    break;

                case DestinationConflictPolicy.SkipExisting:
                    shouldSkip = true;
                    break;

                case DestinationConflictPolicy.RenameNumbered:
                    finalPath = FindNextNumberedPath(candidateArchivePath);
                    break;
            }
        }

        var targetDir = Path.GetDirectoryName(finalPath)
            ?? throw UltraCompressErrors.InvalidArguments($"Cannot determine parent directory for '{finalPath}'.");

        long totalInputBytes = estimatedInputBytes ?? EstimateInputBytes(cleanedInputs);
        long requiredBytes = shouldSkip ? 0 : checked(totalInputBytes + SafetyMarginBytes);

        EnsureDirectoryWritable(targetDir);
        long availableBytes = QueryAvailableBytes(targetDir);

        if (!shouldSkip && availableBytes < requiredBytes)
        {
            throw UltraCompressErrors.InsufficientDiskSpace(targetDir, requiredBytes, availableBytes);
        }

        var stagingPartPath = finalPath + ".part";
        return new ResolvedPackDestination(
            finalPath,
            stagingPartPath,
            shouldSkip,
            allowOverwrite,
            availableBytes,
            requiredBytes);
    }

    public ResolvedExtractDestination ResolveAndValidateExtract(
        string archivePath,
        long requiredExtractBytes,
        DestinationMode mode = DestinationMode.CustomDirectory,
        string? customOutputDirectory = null,
        string? subfolderNameOverride = null)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw UltraCompressErrors.InvalidArguments("Archive path is required.");
        }

        var fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath))
        {
            throw UltraCompressErrors.InputNotFound(archivePath);
        }

        var archiveParentDir = Path.GetDirectoryName(fullArchivePath) ?? Directory.GetCurrentDirectory();
        var archiveStem = Path.GetFileNameWithoutExtension(fullArchivePath);
        if (string.IsNullOrWhiteSpace(archiveStem))
        {
            archiveStem = "extracted";
        }

        string resolvedDir = mode switch
        {
            DestinationMode.SideBySide => archiveParentDir,
            DestinationMode.CustomDirectory => string.IsNullOrWhiteSpace(customOutputDirectory)
                ? archiveParentDir
                : Path.GetFullPath(customOutputDirectory.Trim()),
            DestinationMode.ArchiveSubfolder => Path.Combine(
                string.IsNullOrWhiteSpace(customOutputDirectory)
                    ? archiveParentDir
                    : Path.GetFullPath(customOutputDirectory.Trim()),
                string.IsNullOrWhiteSpace(subfolderNameOverride) ? archiveStem : subfolderNameOverride.Trim()),
            _ => archiveParentDir,
        };

        EnsureDirectoryWritable(resolvedDir);
        long totalRequired = checked(Math.Max(0, requiredExtractBytes) + SafetyMarginBytes);
        long availableBytes = QueryAvailableBytes(resolvedDir);

        if (availableBytes < totalRequired)
        {
            throw UltraCompressErrors.InsufficientDiskSpace(resolvedDir, totalRequired, availableBytes);
        }

        return new ResolvedExtractDestination(resolvedDir, availableBytes, totalRequired);
    }

    public static string FindNextNumberedPath(string baseFilePath)
    {
        var dir = Path.GetDirectoryName(baseFilePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(baseFilePath);
        var ext = Path.GetExtension(baseFilePath);

        for (int index = 1; ; index++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({index}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string ComputeInitialPackCandidatePath(
        List<string> cleanedInputs,
        DestinationMode mode,
        string? customOutputPathOrDirectory,
        string? archiveSubfolderName)
    {
        var firstInput = cleanedInputs[0];
        var inputParentDir = Path.GetDirectoryName(firstInput) ?? Directory.GetCurrentDirectory();

        string stem;
        if (cleanedInputs.Count == 1)
        {
            stem = Directory.Exists(firstInput)
                ? new DirectoryInfo(firstInput).Name
                : Path.GetFileNameWithoutExtension(firstInput);
        }
        else
        {
            var parentLeaf = Path.GetFileName(inputParentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            stem = string.IsNullOrWhiteSpace(parentLeaf) ? "archive-batch" : $"{parentLeaf}-batch";
        }

        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "archive";
        }

        // If caller explicitly passed a .ucx file path (and it is not an existing directory), honor it directly for SideBySide / CustomDirectory.
        if (!string.IsNullOrWhiteSpace(customOutputPathOrDirectory))
        {
            var trimmedCustom = customOutputPathOrDirectory.Trim();
            var fullCustom = Path.GetFullPath(trimmedCustom);
            if (trimmedCustom.EndsWith(".ucx", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(fullCustom))
            {
                if (mode == DestinationMode.ArchiveSubfolder)
                {
                    var customParent = Path.GetDirectoryName(fullCustom) ?? inputParentDir;
                    var sub = string.IsNullOrWhiteSpace(archiveSubfolderName) ? "Archives" : archiveSubfolderName.Trim();
                    return Path.Combine(customParent, sub, Path.GetFileName(fullCustom));
                }

                return fullCustom;
            }
        }

        return mode switch
        {
            DestinationMode.SideBySide => Path.Combine(inputParentDir, $"{stem}.ucx"),
            DestinationMode.CustomDirectory => string.IsNullOrWhiteSpace(customOutputPathOrDirectory)
                ? throw UltraCompressErrors.InvalidArguments(
                    "CustomOutputDirectory is required when DestinationMode is CustomDirectory.",
                    "Provide a target output folder or switch DestinationMode to SideBySide.")
                : Path.Combine(Path.GetFullPath(customOutputPathOrDirectory.Trim()), $"{stem}.ucx"),
            DestinationMode.ArchiveSubfolder => Path.Combine(
                string.IsNullOrWhiteSpace(customOutputPathOrDirectory)
                    ? inputParentDir
                    : Path.GetFullPath(customOutputPathOrDirectory.Trim()),
                string.IsNullOrWhiteSpace(archiveSubfolderName) ? "Archives" : archiveSubfolderName.Trim(),
                $"{stem}.ucx"),
            _ => Path.Combine(inputParentDir, $"{stem}.ucx"),
        };
    }

    private static long EstimateInputBytes(IReadOnlyList<string> paths)
    {
        long sum = 0;
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                sum += new FileInfo(path).Length;
            }
            else if (Directory.Exists(path))
            {
                foreach (var fi in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if ((fi.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        sum += fi.Length;
                    }
                }
            }
        }

        return sum;
    }

    private long QueryAvailableBytes(string targetDirectory)
    {
        try
        {
            return _diskSpaceProvider.GetAvailableFreeBytes(targetDirectory);
        }
        catch (UltraCompressException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new UltraCompressException(UltraCompressErrors.MapException(ex, targetDirectory), ex);
        }
    }

    private static void EnsureDirectoryWritable(string targetDirectory)
    {
        try
        {
            Directory.CreateDirectory(targetDirectory);
            var probeFile = Path.Combine(targetDirectory, $".ucx-writeprobe-{Guid.NewGuid():N}.tmp");
            using (var fs = new FileStream(probeFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            throw new UltraCompressException(UltraCompressErrors.MapException(ex, targetDirectory), ex);
        }
    }
}
