using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.Primitives;

namespace UltraCompress.Desktop.Presentation.Services;

public sealed class DestinationManagerService
{
    private readonly IDiskSpaceProvider _diskSpaceProvider;
    private readonly long _minimumSafetyHeadroomBytes;

    public DestinationManagerService(
        IDiskSpaceProvider? diskSpaceProvider = null,
        long minimumSafetyHeadroomBytes = 16L * 1024 * 1024)
    {
        _diskSpaceProvider = diskSpaceProvider ?? new SystemDiskSpaceProvider();
        _minimumSafetyHeadroomBytes = Math.Max(0, minimumSafetyHeadroomBytes);
    }

    public string ResolveTargetFilePath(
        string destinationFolder,
        string desiredFileName,
        DestinationConflictMode conflictMode,
        out bool conflictDetected,
        out bool willSkip)
    {
        conflictDetected = false;
        willSkip = false;

        var safeFolder = string.IsNullOrWhiteSpace(destinationFolder)
            ? Path.GetTempPath()
            : Path.GetFullPath(destinationFolder.Trim());
        var cleanName = SanitizeFileName(desiredFileName);
        var candidate = Path.Combine(safeFolder, cleanName);

        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        conflictDetected = true;
        return conflictMode switch
        {
            DestinationConflictMode.Overwrite => candidate,
            DestinationConflictMode.Skip => MarkSkip(candidate, out willSkip),
            _ => FindNextAvailablePath(safeFolder, cleanName),
        };
    }

    public DiskSpacePreCheckResult EvaluatePreCheck(
        string destinationFolder,
        string desiredFileName,
        long estimatedOutputBytes,
        DestinationConflictMode conflictMode)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return new DiskSpacePreCheckResult
            {
                DestinationFolder = string.Empty,
                ResolvedOutputPath = string.Empty,
                AvailableFreeBytes = 0,
                EstimatedOutputBytes = Math.Max(0, estimatedOutputBytes),
                RequiredBytesWithHeadroom = Math.Max(0, estimatedOutputBytes),
                HasSufficientSpace = false,
                IsDestinationWritable = false,
                ExistingTargetConflictDetected = false,
                ConflictMode = conflictMode,
                WillSkipOperation = false,
                Severity = PreCheckSeverity.Blocked,
                StatusMessage = "Choose a destination folder before starting.",
            };
        }

        string fullFolder;
        try
        {
            fullFolder = Path.GetFullPath(destinationFolder.Trim());
        }
        catch
        {
            return new DiskSpacePreCheckResult
            {
                DestinationFolder = destinationFolder,
                ResolvedOutputPath = string.Empty,
                AvailableFreeBytes = 0,
                EstimatedOutputBytes = Math.Max(0, estimatedOutputBytes),
                RequiredBytesWithHeadroom = Math.Max(0, estimatedOutputBytes),
                HasSufficientSpace = false,
                IsDestinationWritable = false,
                ExistingTargetConflictDetected = false,
                ConflictMode = conflictMode,
                WillSkipOperation = false,
                Severity = PreCheckSeverity.Blocked,
                StatusMessage = "Destination folder path contains invalid characters.",
            };
        }

        var writable = _diskSpaceProvider.IsDirectoryWritable(fullFolder);
        var freeBytes = _diskSpaceProvider.TryGetAvailableFreeBytes(fullFolder) ?? long.MaxValue;
        var safeEstimated = Math.Max(0, estimatedOutputBytes);
        var headroom = Math.Max(_minimumSafetyHeadroomBytes, (long)(safeEstimated * 0.10));
        var requiredBytes = safeEstimated == 0 ? _minimumSafetyHeadroomBytes : safeEstimated + headroom;

        var resolvedPath = ResolveTargetFilePath(
            fullFolder,
            desiredFileName,
            conflictMode,
            out var conflictDetected,
            out var willSkip);

        if (!writable)
        {
            return new DiskSpacePreCheckResult
            {
                DestinationFolder = fullFolder,
                ResolvedOutputPath = resolvedPath,
                AvailableFreeBytes = freeBytes == long.MaxValue ? 0 : freeBytes,
                EstimatedOutputBytes = safeEstimated,
                RequiredBytesWithHeadroom = requiredBytes,
                HasSufficientSpace = false,
                IsDestinationWritable = false,
                ExistingTargetConflictDetected = conflictDetected,
                ConflictMode = conflictMode,
                WillSkipOperation = willSkip,
                Severity = PreCheckSeverity.Blocked,
                StatusMessage = "Destination drive or directory is not accessible or writable.",
            };
        }

        var hasSpace = freeBytes >= requiredBytes;
        if (!hasSpace)
        {
            return new DiskSpacePreCheckResult
            {
                DestinationFolder = fullFolder,
                ResolvedOutputPath = resolvedPath,
                AvailableFreeBytes = freeBytes,
                EstimatedOutputBytes = safeEstimated,
                RequiredBytesWithHeadroom = requiredBytes,
                HasSufficientSpace = false,
                IsDestinationWritable = true,
                ExistingTargetConflictDetected = conflictDetected,
                ConflictMode = conflictMode,
                WillSkipOperation = willSkip,
                Severity = PreCheckSeverity.Blocked,
                StatusMessage =
                    $"Insufficient disk space: {ByteSizeFormatter.FormatBytes(freeBytes)} free, {ByteSizeFormatter.FormatBytes(requiredBytes)} required (including staging safety headroom).",
            };
        }

        var severity = PreCheckSeverity.Ok;
        var message = $"Ready · {ByteSizeFormatter.FormatBytes(freeBytes)} available → {Path.GetFileName(resolvedPath)}";

        if (conflictDetected)
        {
            switch (conflictMode)
            {
                case DestinationConflictMode.Rename:
                    severity = PreCheckSeverity.Ok;
                    message = $"Existing archive detected — will auto-rename to '{Path.GetFileName(resolvedPath)}' ({ByteSizeFormatter.FormatBytes(freeBytes)} free).";
                    break;
                case DestinationConflictMode.Overwrite:
                    severity = PreCheckSeverity.Warning;
                    message = $"Existing file '{Path.GetFileName(resolvedPath)}' will be atomically replaced upon completion.";
                    break;
                case DestinationConflictMode.Skip:
                    severity = PreCheckSeverity.Warning;
                    message = $"Existing file '{Path.GetFileName(resolvedPath)}' already exists — job will skip writing per Skip policy.";
                    break;
            }
        }

        return new DiskSpacePreCheckResult
        {
            DestinationFolder = fullFolder,
            ResolvedOutputPath = resolvedPath,
            AvailableFreeBytes = freeBytes == long.MaxValue ? 0 : freeBytes,
            EstimatedOutputBytes = safeEstimated,
            RequiredBytesWithHeadroom = requiredBytes,
            HasSufficientSpace = true,
            IsDestinationWritable = true,
            ExistingTargetConflictDetected = conflictDetected,
            ConflictMode = conflictMode,
            WillSkipOperation = willSkip,
            Severity = severity,
            StatusMessage = message,
        };
    }

    private static string MarkSkip(string candidate, out bool willSkip)
    {
        willSkip = true;
        return candidate;
    }

    private static string FindNextAvailablePath(string folder, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(stem))
        {
            stem = "Archive";
        }

        for (var index = 1; index < 10_000; index++)
        {
            var next = Path.Combine(folder, $"{stem} ({index}){ext}");
            if (!File.Exists(next) && !Directory.Exists(next))
            {
                return next;
            }
        }

        return Path.Combine(folder, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    private static string SanitizeFileName(string? desiredFileName)
    {
        var raw = string.IsNullOrWhiteSpace(desiredFileName) ? "Archive.ucx" : desiredFileName.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            raw = raw.Replace(invalid, '_');
        }

        if (!raw.EndsWith(".ucx", StringComparison.OrdinalIgnoreCase)
            && !raw.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            raw += ".ucx";
        }

        return raw;
    }
}
