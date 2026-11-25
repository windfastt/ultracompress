using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.Primitives;

namespace UltraCompress.Desktop.Presentation.Services;

public static class ActionableErrorPresenter
{
    public static ActionableErrorCard FromException(Exception exception, string? affectedPath = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            FileNotFoundException fnf => FromCode(
                ActionableErrorCode.SourceFileNotFound,
                affectedPath ?? fnf.FileName,
                fnf.Message),
            DirectoryNotFoundException dnf => FromCode(
                ActionableErrorCode.SourceFileNotFound,
                affectedPath,
                dnf.Message),
            PathTooLongException ptl => FromCode(
                ActionableErrorCode.DestinationPathTooLong,
                affectedPath,
                ptl.Message),
            UnauthorizedAccessException uae => FromCode(
                ActionableErrorCode.AccessDeniedPermission,
                affectedPath,
                uae.Message),
            InvalidDataException ide => FromCode(
                ActionableErrorCode.ArchiveHeaderCorrupted,
                affectedPath,
                ide.Message),
            IOException ioEx when IsDiskFullHResult(ioEx.HResult) => FromCode(
                ActionableErrorCode.InsufficientDiskSpace,
                affectedPath,
                ioEx.Message),
            IOException ioEx when IsSharingViolationHResult(ioEx.HResult) => FromCode(
                ActionableErrorCode.FileLockedInUse,
                affectedPath,
                ioEx.Message),
            _ => FromCode(
                ActionableErrorCode.UnknownIoFailure,
                affectedPath,
                exception.Message),
        };
    }

    public static ActionableErrorCard ForInsufficientDiskSpace(
        string destinationFolder,
        long availableBytes,
        long requiredBytes) =>
        new()
        {
            Code = ActionableErrorCode.InsufficientDiskSpace,
            Title = "Not enough free disk space in destination",
            Explanation =
                $"Destination '{destinationFolder}' has {ByteSizeFormatter.FormatBytes(availableBytes)} available, " +
                $"but safely staging and finalizing this archive requires at least {ByteSizeFormatter.FormatBytes(requiredBytes)}.",
            RemediationSteps =
            [
                "Select a destination folder on another drive with sufficient free capacity.",
                "Free disk space on the target drive or discard stale staging files in Queue & Recovery.",
                "Switch to 'Archive' or 'Tiny' profile if compressible data needs a smaller output footprint.",
            ],
            AffectedPath = destinationFolder,
            PrimaryActionHint = "Choose another destination folder",
        };

    public static ActionableErrorCard FromCode(
        ActionableErrorCode code,
        string? affectedPath = null,
        string? technicalDetail = null)
    {
        var pathDisplay = string.IsNullOrWhiteSpace(affectedPath) ? "the selected path" : $"'{affectedPath}'";

        return code switch
        {
            ActionableErrorCode.InsufficientDiskSpace => new ActionableErrorCard
            {
                Code = code,
                Title = "Not enough free disk space to complete job",
                Explanation = $"Writing to {pathDisplay} stopped because the target drive ran out of free space.",
                RemediationSteps =
                [
                    "Pick a destination folder on a drive with more free space.",
                    "Click 'Discard Temp' on any interrupted jobs to reclaim `.part` staging space.",
                    "Retry the job once space is available — source files were not modified.",
                ],
                AffectedPath = affectedPath,
                TechnicalDetail = technicalDetail,
                PrimaryActionHint = "Change destination drive",
            },
            ActionableErrorCode.SourceFileNotFound => new ActionableErrorCard
            {
                Code = code,
                Title = "Input file or folder could not be found",
                Explanation = $"UltraCompress could not find {pathDisplay}; it may have been moved, renamed, or disconnected.",
                RemediationSteps =
                [
                    "Verify the file or external drive is still connected and accessible in File Explorer.",
                    "Remove the missing item from the staged batch and re-add its updated location.",
                ],
                AffectedPath = affectedPath,
                TechnicalDetail = technicalDetail,
                PrimaryActionHint = "Re-select input files",
            },
            ActionableErrorCode.AccessDeniedPermission => new ActionableErrorCard
            {
                Code = code,
                Title = "Permission denied accessing file or destination",
                Explanation = $"Windows blocked read or write access to {pathDisplay}.",
                RemediationSteps =
                [
                    "Choose a user-writable destination such as Documents, Desktop, or a non-system folder.",
                    "Check folder permissions or Controlled Folder Access settings if writing to protected directories.",
                    "Close applications holding exclusive administrator locks on the target path.",
                ],
                AffectedPath = affectedPath,
                TechnicalDetail = technicalDetail,
                PrimaryActionHint = "Select writable folder",
            },
            ActionableErrorCode.FileLockedInUse => new ActionableErrorCard
            {
                Code = code,
                Title = "File is currently locked by another application",
                Explanation = $"Another process has an exclusive lock open on {pathDisplay}, preventing safe bit-exact reading or replacement.",
                RemediationSteps =
                [
                    "Close any editor, database engine, or media player currently using the file.",
                    "Wait for background cloud-sync or antivirus scans on the file to finish, then click Resume/Retry.",
                ],
                AffectedPath = affectedPath,
                TechnicalDetail = technicalDetail,
                PrimaryActionHint = "Close locking app and retry",
            },
            ActionableErrorCode.ArchiveHeaderCorrupted => new ActionableErrorCard
            {
                Code = code,
                Title = "Archive header or manifest is unreadable",
                Explanation = $"{pathDisplay} is truncated, corrupted, or not a valid `.ucx` / `.zip` archive container.",
                RemediationSteps =
                [
                    "Check whether the archive download or file copy finished completely (look for a `.part` file).",
                    "Re-copy the archive from its backup source and run 'Verify Archive' before extracting.",
                ],
                AffectedPath = affectedPath,
                TechnicalDetail = technicalDetail,
                PrimaryActionHint = "Inspect or re-download archive",
            },
            ActionableErrorCode.Sha256IntegrityMismatch => new ActionableErrorCard
            {
                Code = code,
                Title = "Bit-exact SHA-256 integrity check failed",
                Explanation = $"One or more entries in {pathDisplay} did not match their recorded SHA-256 cryptographic digest.",
                RemediationSteps =
                [
                    "Do not delete original source backups — at least one archive block suffered storage bit-rot or truncation.",
                    "Inspect the Archive Browser 'SHA-256 Status' column to isolate unaffected verified files you can still selectively extract.",
                    "Run disk health diagnostics (`chkdsk` / SMART) on the storage media hosting the archive.",
                ],
                AffectedPath = affectedPath,
                TechnicalDetail = technicalDetail,
                PrimaryActionHint = "Extract verified entries only",
            },
            ActionableErrorCode.DestinationPathTooLong => new ActionableErrorCard
            {
                Code = code,
                Title = "Destination file path exceeds OS path limits",
                Explanation = $"Extracting or writing to {pathDisplay} exceeded the maximum supported path length.",
                RemediationSteps =
                [
                    "Choose a shorter destination root path (for example `C:\\Extract` instead of a deeply nested folder).",
                    "Enable Windows Win32 Long Paths (`LongPathsEnabled`) in system policy if working with deep source trees.",
                ],
                AffectedPath = affectedPath,
                TechnicalDetail = technicalDetail,
                PrimaryActionHint = "Pick shorter destination path",
            },
            ActionableErrorCode.PathTraversalBlocked => new ActionableErrorCard
            {
                Code = code,
                Title = "Unsafe archive entry path blocked for security",
                Explanation = $"An entry inside {pathDisplay} attempted to write outside the chosen destination folder (`..` or rooted path).",
                RemediationSteps =
                [
                    "Do not extract untrusted archives from unknown sources.",
                    "Use Selective Extraction to inspect individual safe entries before extracting.",
                ],
                AffectedPath = affectedPath,
                TechnicalDetail = technicalDetail,
                PrimaryActionHint = "Extraction aborted safely",
            },
            ActionableErrorCode.EnginePipeOffline => new ActionableErrorCard
            {
                Code = code,
                Title = "Background Engine service is offline (running in local desktop mode)",
                Explanation = "The named-pipe connection to `UltraCompress.Engine` (`\\\\.\\pipe\\UltraCompress.Engine`) did not respond; local desktop archive operations remain available.",
                RemediationSteps =
                [
                    "Start the `UltraCompressEngine` Windows service or run `dotnet run --project src/UltraCompress.Engine`.",
                    "Click 'Reconnect to Engine' in the top bar or system tray menu once the service is running.",
                ],
                AffectedPath = affectedPath,
                TechnicalDetail = technicalDetail,
                PrimaryActionHint = "Reconnect to Engine",
            },
            ActionableErrorCode.ExternalToolMissing7z => new ActionableErrorCard
            {
                Code = code,
                Title = "External 7-Zip LZMA2 tool (`7za`) not found",
                Explanation = "The selected LZMA2 backend requires `7za.exe` / `7z`, which was not found in `tools/7z` or system `PATH`.",
                RemediationSteps =
                [
                    "Install 7-Zip or set `ULTRACOMPRESS_7ZA_PATH` to the full path of `7z.exe` / `7za.exe`.",
                    "Or switch to `Instant`, `Balanced`, or `Archive` to use built-in Zstd/Brotli codecs immediately.",
                ],
                AffectedPath = affectedPath,
                TechnicalDetail = technicalDetail,
                PrimaryActionHint = "Configure 7-Zip or use Zstd profile",
            },
            ActionableErrorCode.MutatingConsentMissing => new ActionableErrorCard
            {
                Code = code,
                Title = "Explicit safety consent required before mutating files",
                Explanation = "Lossy Optimization, Format Conversion, and Metadata Removal modify data or container fidelity and cannot run without explicit confirmation.",
                RemediationSteps =
                [
                    "Review the itemized Impact & Preview summary in Optimization Studio.",
                    "Check all required safety consent boxes confirming non-bit-exact modification.",
                    "Keep 'Preserve Original File' enabled (recommended) or explicitly confirm original replacement.",
                    "For 100% bit-exact lossless archiving, switch back to the 'Compress & Batch' workspace.",
                ],
                AffectedPath = affectedPath,
                TechnicalDetail = technicalDetail,
                PrimaryActionHint = "Complete safety consent checklist",
            },
            _ => new ActionableErrorCard
            {
                Code = ActionableErrorCode.UnknownIoFailure,
                Title = "Archive operation encountered an I/O error",
                Explanation = $"UltraCompress could not finish processing {pathDisplay}. Original input files remain untouched.",
                RemediationSteps =
                [
                    "Check that both source and destination drives remain connected with available space.",
                    "Verify no other program is modifying the files simultaneously, then retry the operation.",
                ],
                AffectedPath = affectedPath,
                TechnicalDetail = technicalDetail,
                PrimaryActionHint = "Check paths and retry",
            },
        };
    }

    private static bool IsDiskFullHResult(int hResult)
    {
        const int ErrorHandleDiskFull = unchecked((int)0x80070027);
        const int ErrorDiskFull = unchecked((int)0x80070070);
        const int LinuxEnospc = 28;
        return hResult is ErrorHandleDiskFull or ErrorDiskFull or LinuxEnospc;
    }

    private static bool IsSharingViolationHResult(int hResult)
    {
        const int ErrorSharingViolation = unchecked((int)0x80070020);
        const int ErrorLockViolation = unchecked((int)0x80070021);
        return hResult is ErrorSharingViolation or ErrorLockViolation;
    }
}
