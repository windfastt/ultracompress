using System.Security;
using System.Text.Json.Serialization;

namespace UltraCompress.Core.Errors;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UltraCompressErrorCode
{
    None = 0,
    InputNotFound = 1,
    EmptyInputSet = 2,
    FileLockedOrInUse = 3,
    IoFailure = 4,
    PermissionDenied = 5,
    InsufficientDiskSpace = 6,
    DestinationAlreadyExists = 7,
    CorruptArchive = 8,
    ChecksumMismatch = 9,
    PathTraversalDetected = 10,
    UnsupportedCodecOrVersion = 11,
    JobCancelled = 12,
    JobNotResumable = 13,
    InvalidArguments = 14,
}

/// <summary>
/// Structured user-facing error with stable symbolic code and actionable remediation guidance.
/// </summary>
public sealed record UltraCompressError
{
    public required UltraCompressErrorCode Code { get; init; }

    public required string SymbolicCode { get; init; }

    public required string Title { get; init; }

    public required string Message { get; init; }

    public required string Remediation { get; init; }

    public string? TargetPath { get; init; }

    public long? RequiredBytes { get; init; }

    public long? AvailableBytes { get; init; }

    public string? TechnicalDetail { get; init; }

    public string ToDisplayString() =>
        $"[{SymbolicCode}] {Title}: {Message} Remediation: {Remediation}";
}

public sealed class UltraCompressException : Exception
{
    public UltraCompressException(UltraCompressError error, Exception? innerException = null)
        : base($"[{error.SymbolicCode}] {error.Message}", innerException)
    {
        Error = error;
    }

    public UltraCompressError Error { get; }

    public UltraCompressErrorCode Code => Error.Code;
}

public static class UltraCompressErrors
{
    private const int HResultDiskFull = unchecked((int)0x80070070);
    private const int HResultHandleDiskFull = unchecked((int)0x80070027);
    private const int HResultSharingViolation = unchecked((int)0x80070020);
    private const int HResultLockViolation = unchecked((int)0x80070021);
    private const int HResultAccessDenied = unchecked((int)0x80070005);
    private const int LinuxEnospc = 28;
    private const int LinuxEacces = 13;

    public static string GetSymbolicCode(UltraCompressErrorCode code) => code switch
    {
        UltraCompressErrorCode.InputNotFound => "UC-ERR-IO-001",
        UltraCompressErrorCode.EmptyInputSet => "UC-ERR-IO-002",
        UltraCompressErrorCode.FileLockedOrInUse => "UC-ERR-IO-003",
        UltraCompressErrorCode.IoFailure => "UC-ERR-IO-004",
        UltraCompressErrorCode.PermissionDenied => "UC-ERR-PERM-001",
        UltraCompressErrorCode.InsufficientDiskSpace => "UC-ERR-DISK-001",
        UltraCompressErrorCode.DestinationAlreadyExists => "UC-ERR-DEST-001",
        UltraCompressErrorCode.CorruptArchive => "UC-ERR-ARCH-001",
        UltraCompressErrorCode.ChecksumMismatch => "UC-ERR-HASH-001",
        UltraCompressErrorCode.PathTraversalDetected => "UC-ERR-SEC-001",
        UltraCompressErrorCode.UnsupportedCodecOrVersion => "UC-ERR-ARCH-002",
        UltraCompressErrorCode.JobCancelled => "UC-ERR-JOB-001",
        UltraCompressErrorCode.JobNotResumable => "UC-ERR-JOB-002",
        UltraCompressErrorCode.InvalidArguments => "UC-ERR-ARG-001",
        _ => "UC-ERR-GEN-000",
    };

    public static UltraCompressException InputNotFound(string path, Exception? inner = null) =>
        new(new UltraCompressError
        {
            Code = UltraCompressErrorCode.InputNotFound,
            SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.InputNotFound),
            Title = "Input Path Not Found",
            Message = $"The specified file or folder could not be found: '{path}'.",
            Remediation = "Verify that the file or folder still exists, check spelling, and confirm any removable drive or network share is connected.",
            TargetPath = path,
            TechnicalDetail = inner?.Message,
        }, inner);

    public static UltraCompressException EmptyInputSet(string? detail = null) =>
        new(new UltraCompressError
        {
            Code = UltraCompressErrorCode.EmptyInputSet,
            SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.EmptyInputSet),
            Title = "No Input Paths Provided",
            Message = detail ?? "At least one file or directory is required to create an archive.",
            Remediation = "Select one or more files or folders to pack and try again.",
        });

    public static UltraCompressException PermissionDenied(string? path, Exception? inner = null) =>
        new(new UltraCompressError
        {
            Code = UltraCompressErrorCode.PermissionDenied,
            SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.PermissionDenied),
            Title = "Permission Denied",
            Message = path is null
                ? "Access to a required file or folder was denied."
                : $"Access to '{path}' was denied by the operating system.",
            Remediation = "Choose a writable user folder (such as Documents or Desktop), clear any Read-Only attribute, or adjust folder permissions.",
            TargetPath = path,
            TechnicalDetail = inner?.Message,
        }, inner);

    public static UltraCompressException FileLocked(string? path, Exception? inner = null) =>
        new(new UltraCompressError
        {
            Code = UltraCompressErrorCode.FileLockedOrInUse,
            SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.FileLockedOrInUse),
            Title = "File Locked by Another Process",
            Message = path is null
                ? "A target or source file is currently open exclusively in another application."
                : $"'{path}' is currently locked by another running process.",
            Remediation = "Close any editor, media player, or background tool holding the file open, then retry or resume the job.",
            TargetPath = path,
            TechnicalDetail = inner?.Message,
        }, inner);

    public static UltraCompressException InsufficientDiskSpace(
        string destinationPath,
        long requiredBytes,
        long availableBytes,
        Exception? inner = null) =>
        new(new UltraCompressError
        {
            Code = UltraCompressErrorCode.InsufficientDiskSpace,
            SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.InsufficientDiskSpace),
            Title = "Insufficient Free Disk Space",
            Message = $"Destination '{destinationPath}' requires {FormatBytes(requiredBytes)} free space, but only {FormatBytes(availableBytes)} is available.",
            Remediation = $"Free at least {FormatBytes(Math.Max(0, requiredBytes - availableBytes))} on the destination volume or select another drive with enough capacity.",
            TargetPath = destinationPath,
            RequiredBytes = requiredBytes,
            AvailableBytes = availableBytes,
            TechnicalDetail = inner?.Message,
        }, inner);

    public static UltraCompressException DestinationAlreadyExists(string path) =>
        new(new UltraCompressError
        {
            Code = UltraCompressErrorCode.DestinationAlreadyExists,
            SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.DestinationAlreadyExists),
            Title = "Destination File Already Exists",
            Message = $"Output target '{path}' already exists and the conflict policy is set to Fail.",
            Remediation = "Change the conflict policy to Overwrite, RenameNumbered, or SkipExisting, or choose a different output path.",
            TargetPath = path,
        });

    public static UltraCompressException CorruptArchive(string? archivePath, string reason, Exception? inner = null) =>
        new(new UltraCompressError
        {
            Code = UltraCompressErrorCode.CorruptArchive,
            SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.CorruptArchive),
            Title = "Corrupt or Invalid Archive",
            Message = archivePath is null
                ? $"Archive container is corrupt or unreadable: {reason}"
                : $"Archive '{archivePath}' is corrupt or not a valid .ucx container: {reason}",
            Remediation = "Verify the file is a complete .ucx archive. If a '.ucx.part' staging file exists from an interrupted job, use Queue Resume or Rollback instead of opening the staging file directly.",
            TargetPath = archivePath,
            TechnicalDetail = inner?.Message,
        }, inner);

    public static UltraCompressException ChecksumMismatch(
        string? archivePath,
        string subject,
        string expectedSha256,
        string actualSha256) =>
        new(new UltraCompressError
        {
            Code = UltraCompressErrorCode.ChecksumMismatch,
            SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.ChecksumMismatch),
            Title = "SHA-256 Integrity Verification Failed",
            Message = $"Integrity check failed for {subject} (expected SHA-256 {ShortHash(expectedSha256)}, computed {ShortHash(actualSha256)}).",
            Remediation = "Extraction was stopped before writing unverified output. Re-transfer or restore the archive from a known-good copy.",
            TargetPath = archivePath,
            TechnicalDetail = $"expected={expectedSha256}; actual={actualSha256}",
        });

    public static UltraCompressException PathTraversalDetected(string? archivePath, string entryPath, string reason) =>
        new(new UltraCompressError
        {
            Code = UltraCompressErrorCode.PathTraversalDetected,
            SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.PathTraversalDetected),
            Title = "Unsafe Archive Entry Path Blocked",
            Message = $"Blocked unsafe entry path '{entryPath}': {reason}",
            Remediation = "Extraction was aborted to prevent writing outside the destination directory (zip-slip protection). Do not extract untrusted tampered archives.",
            TargetPath = archivePath,
            TechnicalDetail = entryPath,
        });

    public static UltraCompressException UnsupportedCodecOrVersion(string? archivePath, string detail, Exception? inner = null) =>
        new(new UltraCompressError
        {
            Code = UltraCompressErrorCode.UnsupportedCodecOrVersion,
            SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.UnsupportedCodecOrVersion),
            Title = "Unsupported Archive Version or Codec",
            Message = detail,
            Remediation = "Upgrade UltraCompress to the latest release or ensure external 7za CLI is installed (ULTRACOMPRESS_7ZA_PATH) when opening 7z-encoded blocks.",
            TargetPath = archivePath,
            TechnicalDetail = inner?.Message,
        }, inner);

    public static UltraCompressException JobCancelled(string? jobId = null) =>
        new(new UltraCompressError
        {
            Code = UltraCompressErrorCode.JobCancelled,
            SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.JobCancelled),
            Title = "Operation Cancelled",
            Message = jobId is null ? "The archive operation was cancelled." : $"Job '{jobId}' was cancelled.",
            Remediation = "Temporary '.ucx.part' staging artifacts were cleaned up. Enqueue the job again whenever ready.",
        });

    public static UltraCompressException JobNotResumable(string jobId, string currentState) =>
        new(new UltraCompressError
        {
            Code = UltraCompressErrorCode.JobNotResumable,
            SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.JobNotResumable),
            Title = "Job Cannot Be Resumed or Rolled Back",
            Message = $"Job '{jobId}' is currently in state '{currentState}' and cannot perform the requested recovery action.",
            Remediation = "Refresh the job queue; only Paused or Interrupted jobs can be resumed.",
        });

    public static UltraCompressException InvalidArguments(string message, string? remediation = null) =>
        new(new UltraCompressError
        {
            Code = UltraCompressErrorCode.InvalidArguments,
            SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.InvalidArguments),
            Title = "Invalid Operation Parameters",
            Message = message,
            Remediation = remediation ?? "Check the command flags or request options and retry.",
        });

    public static UltraCompressError MapException(Exception ex, string? contextPath = null)
    {
        if (ex is UltraCompressException ucx)
        {
            return contextPath is not null && ucx.Error.TargetPath is null
                ? ucx.Error with { TargetPath = contextPath }
                : ucx.Error;
        }

        if (ex is OperationCanceledException)
        {
            return JobCancelled().Error;
        }

        if (ex is UnauthorizedAccessException or SecurityException)
        {
            return PermissionDenied(contextPath, ex).Error;
        }

        if (ex is FileNotFoundException fnf)
        {
            return InputNotFound(fnf.FileName ?? contextPath ?? "(unknown)", fnf).Error;
        }

        if (ex is DirectoryNotFoundException or DriveNotFoundException)
        {
            return InputNotFound(contextPath ?? "(unknown)", ex).Error;
        }

        if (ex is InvalidDataException)
        {
            return CorruptArchive(contextPath, ex.Message, ex).Error;
        }

        if (ex is IOException ioEx)
        {
            var hr = ioEx.HResult;
            var lowWord = hr & 0xFFFF;

            if (hr == HResultDiskFull || hr == HResultHandleDiskFull || hr == LinuxEnospc || lowWord == LinuxEnospc
                || ioEx.Message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)
                || ioEx.Message.Contains("not enough space on the disk", StringComparison.OrdinalIgnoreCase))
            {
                return new UltraCompressError
                {
                    Code = UltraCompressErrorCode.InsufficientDiskSpace,
                    SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.InsufficientDiskSpace),
                    Title = "Disk Full During Write",
                    Message = $"The destination drive ran out of free disk space while writing '{contextPath ?? "output"}'.",
                    Remediation = "Free disk space on the target drive or select a different destination drive, then retry or resume the job.",
                    TargetPath = contextPath,
                    TechnicalDetail = ioEx.Message,
                };
            }

            if (hr == HResultSharingViolation || hr == HResultLockViolation)
            {
                return FileLocked(contextPath, ioEx).Error;
            }

            if (hr == HResultAccessDenied || hr == LinuxEacces || lowWord == LinuxEacces)
            {
                return PermissionDenied(contextPath, ioEx).Error;
            }

            return new UltraCompressError
            {
                Code = UltraCompressErrorCode.IoFailure,
                SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.IoFailure),
                Title = "Disk or Filesystem I/O Error",
                Message = $"An I/O error occurred while accessing '{contextPath ?? "filesystem"}': {ioEx.Message}",
                Remediation = "Check storage availability, network share connectivity, and path length limits, then retry.",
                TargetPath = contextPath,
                TechnicalDetail = ioEx.ToString(),
            };
        }

        return new UltraCompressError
        {
            Code = UltraCompressErrorCode.IoFailure,
            SymbolicCode = GetSymbolicCode(UltraCompressErrorCode.IoFailure),
            Title = "Unexpected Archive Operation Error",
            Message = ex.Message,
            Remediation = "Inspect technical details in logs and verify input/output paths before retrying.",
            TargetPath = contextPath,
            TechnicalDetail = ex.ToString(),
        };
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return $"{kb:F1} KiB";
        }

        double mb = kb / 1024.0;
        if (mb < 1024)
        {
            return $"{mb:F2} MiB";
        }

        double gb = mb / 1024.0;
        return $"{gb:F2} GiB";
    }

    private static string ShortHash(string sha256) =>
        sha256.Length <= 12 ? sha256 : sha256[..12] + "...";
}
