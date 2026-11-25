namespace UltraCompress.Desktop.Presentation.Models;

public enum DesktopWorkspaceTab
{
    CompressBatch = 0,
    ArchiveBrowser = 1,
    QueueAndRecovery = 2,
    CompressionHistory = 3,
    OptimizationStudio = 4,
}

/// <summary>
/// Strict four-mode data safety boundary enforced across UltraCompress Desktop.
/// Lossless compression never executes destructive or mutating operations.
/// </summary>
public enum OperationKind
{
    LosslessCompression = 0,
    LossyOptimization = 1,
    FormatConversion = 2,
    DataRemoval = 3,
}

/// <summary>
/// Five measurable, 100% bit-exact lossless compression profiles.
/// </summary>
public enum DesktopLosslessProfile
{
    Instant = 0,
    Balanced = 1,
    Archive = 2,
    Tiny = 3,
    Laboratory = 4,
}

public enum DestinationConflictMode
{
    Rename = 0,
    Overwrite = 1,
    Skip = 2,
}

public enum PreCheckSeverity
{
    Ok = 0,
    Warning = 1,
    Blocked = 2,
}

public enum FileContentCategory
{
    AlreadyCompressedMedia = 0,
    CompressedArchive = 1,
    SourceAndMarkup = 2,
    DatabaseAndTabular = 3,
    NativeExecutable = 4,
    UncompressedRasterImage = 5,
    DocumentPdf = 6,
    GenericBinary = 7,
}

public enum Sha256VerificationStatus
{
    Unchecked = 0,
    VerifiedMatch = 1,
    DedupReferenceVerified = 2,
    MismatchFailed = 3,
    MissingPayload = 4,
}

public enum DesktopJobState
{
    Queued = 0,
    Running = 1,
    Paused = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
    InterruptedCrashRecovery = 6,
}

public enum OptimizationSubMode
{
    LossyOptimization = 0,
    FormatConversion = 1,
    DataRemoval = 2,
}

public enum ActionableErrorCode
{
    InsufficientDiskSpace = 0,
    SourceFileNotFound = 1,
    AccessDeniedPermission = 2,
    FileLockedInUse = 3,
    ArchiveHeaderCorrupted = 4,
    Sha256IntegrityMismatch = 5,
    DestinationPathTooLong = 6,
    PathTraversalBlocked = 7,
    EnginePipeOffline = 8,
    ExternalToolMissing7z = 9,
    MutatingConsentMissing = 10,
    UnknownIoFailure = 11,
}
