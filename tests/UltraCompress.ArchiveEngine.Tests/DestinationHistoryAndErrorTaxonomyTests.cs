using UltraCompress.Core;
using UltraCompress.Core.Archive;
using UltraCompress.Core.Destination;
using UltraCompress.Core.Errors;
using UltraCompress.Core.History;

namespace UltraCompress.ArchiveEngine.Tests;

public sealed class DestinationHistoryAndErrorTaxonomyTests
{
    [Fact]
    public void DestinationResolver_SupportsSideBySideCustomDirectoryAndArchiveSubfolder()
    {
        using var ws = new TempTestWorkspace();
        var inputFile = ws.WriteTextFile("projects/report.csv", "a,b,c\n1,2,3\n");
        var resolver = new DestinationResolver();

        var sideBySide = resolver.ResolveAndValidatePack([inputFile], DestinationMode.SideBySide);
        Assert.Equal(ws.SubPath("projects", "report.ucx"), sideBySide.FinalArchivePath);

        var customDir = ws.SubPath("custom-archives");
        var custom = resolver.ResolveAndValidatePack([inputFile], DestinationMode.CustomDirectory, customOutputPathOrDirectory: customDir);
        Assert.Equal(Path.Combine(customDir, "report.ucx"), custom.FinalArchivePath);

        var subfolder = resolver.ResolveAndValidatePack([inputFile], DestinationMode.ArchiveSubfolder, archiveSubfolderName: "Compressed");
        Assert.Equal(ws.SubPath("projects", "Compressed", "report.ucx"), subfolder.FinalArchivePath);
    }

    [Fact]
    public async Task ConflictPolicies_EnforceFailOverwriteRenameNumberedAndSkipExisting()
    {
        using var ws = new TempTestWorkspace();
        var v1 = ws.WriteTextFile("v1/data.txt", "Version 1 content");
        var v2 = ws.WriteTextFile("v2/data.txt", "Version 2 updated content");
        var outDir = ws.SubPath("out");
        Directory.CreateDirectory(outDir);
        var baseArchive = Path.Combine(outDir, "data.ucx");

        var writer = new UcxArchiveWriter();
        var reader = new UcxArchiveReader();
        var resolver = new DestinationResolver();

        // Initial archive creation
        await writer.CreateFromPathAsync(baseArchive, v1);

        // 1. Fail policy throws UC-ERR-DEST-001
        var failEx = Assert.Throws<UltraCompressException>(() =>
            resolver.ResolveAndValidatePack([v2], DestinationMode.CustomDirectory, DestinationConflictPolicy.Fail, outDir));
        Assert.Equal(UltraCompressErrorCode.DestinationAlreadyExists, failEx.Code);
        Assert.Equal("UC-ERR-DEST-001", failEx.Error.SymbolicCode);

        // 2. SkipExisting marks ShouldSkipExisting = true and leaves v1 untouched
        var skip = resolver.ResolveAndValidatePack([v2], DestinationMode.CustomDirectory, DestinationConflictPolicy.SkipExisting, outDir);
        Assert.True(skip.ShouldSkipExisting);

        // 3. RenameNumbered allocates "data (1).ucx" and "data (2).ucx"
        var ren1 = resolver.ResolveAndValidatePack([v2], DestinationMode.CustomDirectory, DestinationConflictPolicy.RenameNumbered, outDir);
        Assert.Equal(Path.Combine(outDir, "data (1).ucx"), ren1.FinalArchivePath);
        await writer.CreateFromPathAsync(ren1.FinalArchivePath, v2);

        var ren2 = resolver.ResolveAndValidatePack([v2], DestinationMode.CustomDirectory, DestinationConflictPolicy.RenameNumbered, outDir);
        Assert.Equal(Path.Combine(outDir, "data (2).ucx"), ren2.FinalArchivePath);

        // 4. Overwrite replaces baseArchive atomically
        var over = resolver.ResolveAndValidatePack([v2], DestinationMode.CustomDirectory, DestinationConflictPolicy.Overwrite, outDir);
        Assert.True(over.AllowOverwrite);
        await writer.CreateFromPathAsync(over.FinalArchivePath, v2, new UcxPackOptions { OverwriteDestination = true });

        // Test Extract conflict policies (Fail, SkipExisting, RenameNumbered, Overwrite)
        var extractDir = ws.SubPath("extract-conflicts");
        await reader.ExtractEntriesAsync(baseArchive, extractDir);
        Assert.Equal("Version 2 updated content", await File.ReadAllTextAsync(Path.Combine(extractDir, "data.txt")));

        // Modify existing extracted file on disk to test SkipExisting vs RenameNumbered vs Overwrite
        await File.WriteAllTextAsync(Path.Combine(extractDir, "data.txt"), "Local edit");

        await Assert.ThrowsAsync<UltraCompressException>(() =>
            reader.ExtractEntriesAsync(baseArchive, extractDir, new UcxExtractOptions { ConflictPolicy = DestinationConflictPolicy.Fail }));

        var skipExtract = await reader.ExtractEntriesAsync(baseArchive, extractDir, new UcxExtractOptions { ConflictPolicy = DestinationConflictPolicy.SkipExisting });
        Assert.Equal(1, skipExtract.SkippedExistingFiles);
        Assert.Equal("Local edit", await File.ReadAllTextAsync(Path.Combine(extractDir, "data.txt")));

        var renameExtract = await reader.ExtractEntriesAsync(baseArchive, extractDir, new UcxExtractOptions { ConflictPolicy = DestinationConflictPolicy.RenameNumbered });
        Assert.Equal(1, renameExtract.ExtractedFiles);
        Assert.Equal("Local edit", await File.ReadAllTextAsync(Path.Combine(extractDir, "data.txt")));
        Assert.Equal("Version 2 updated content", await File.ReadAllTextAsync(Path.Combine(extractDir, "data (1).txt")));

        var overwriteExtract = await reader.ExtractEntriesAsync(baseArchive, extractDir, new UcxExtractOptions { ConflictPolicy = DestinationConflictPolicy.Overwrite });
        Assert.Equal(1, overwriteExtract.ExtractedFiles);
        Assert.Equal("Version 2 updated content", await File.ReadAllTextAsync(Path.Combine(extractDir, "data.txt")));
    }

    [Fact]
    public void PreflightDiskSpaceValidation_RejectsPackAndExtractWhenFreeSpaceInsufficient()
    {
        using var ws = new TempTestWorkspace();
        var bigInput = ws.WriteBinaryFile("big.bin", new byte[256 * 1024]);
        var lowDiskProvider = new StubDiskSpaceProvider(availableBytes: 16 * 1024);
        var resolver = new DestinationResolver(lowDiskProvider);

        var packEx = Assert.Throws<UltraCompressException>(() =>
            resolver.ResolveAndValidatePack([bigInput], DestinationMode.SideBySide));

        Assert.Equal(UltraCompressErrorCode.InsufficientDiskSpace, packEx.Code);
        Assert.Equal("UC-ERR-DISK-001", packEx.Error.SymbolicCode);
        Assert.Equal(16 * 1024, packEx.Error.AvailableBytes);
        Assert.True(packEx.Error.RequiredBytes > 256 * 1024);
        Assert.Contains("Free at least", packEx.Error.Remediation, StringComparison.Ordinal);

        var fakeArchive = ws.WriteBinaryFile("tiny.ucx", new byte[128]);
        var extractEx = Assert.Throws<UltraCompressException>(() =>
            resolver.ResolveAndValidateExtract(fakeArchive, requiredExtractBytes: 512 * 1024, DestinationMode.SideBySide));

        Assert.Equal(UltraCompressErrorCode.InsufficientDiskSpace, extractEx.Code);
    }

    [Fact]
    public void UltraCompressErrorTaxonomy_MapsAllExceptionClassesToActionableCodesAndRemediations()
    {
        var fnf = UltraCompressErrors.MapException(new FileNotFoundException("missing", "/no/such/file.txt"));
        Assert.Equal(UltraCompressErrorCode.InputNotFound, fnf.Code);
        Assert.Equal("UC-ERR-IO-001", fnf.SymbolicCode);
        Assert.False(string.IsNullOrWhiteSpace(fnf.Remediation));

        var perm = UltraCompressErrors.MapException(new UnauthorizedAccessException("denied"), "C:\\Program Files\\out.ucx");
        Assert.Equal(UltraCompressErrorCode.PermissionDenied, perm.Code);
        Assert.Equal("UC-ERR-PERM-001", perm.SymbolicCode);

        var diskFullIo = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        var diskErr = UltraCompressErrors.MapException(diskFullIo, "D:\\backup.ucx");
        Assert.Equal(UltraCompressErrorCode.InsufficientDiskSpace, diskErr.Code);
        Assert.Equal("UC-ERR-DISK-001", diskErr.SymbolicCode);

        var lockedIo = new IOException("Sharing violation", unchecked((int)0x80070020));
        var lockedErr = UltraCompressErrors.MapException(lockedIo, "D:\\locked.db");
        Assert.Equal(UltraCompressErrorCode.FileLockedOrInUse, lockedErr.Code);
        Assert.Equal("UC-ERR-IO-003", lockedErr.SymbolicCode);

        var corruptErr = UltraCompressErrors.MapException(new InvalidDataException("bad stream"), "D:\\bad.ucx");
        Assert.Equal(UltraCompressErrorCode.CorruptArchive, corruptErr.Code);
        Assert.Equal("UC-ERR-ARCH-001", corruptErr.SymbolicCode);

        var cancelErr = UltraCompressErrors.MapException(new OperationCanceledException());
        Assert.Equal(UltraCompressErrorCode.JobCancelled, cancelErr.Code);
        Assert.Equal("UC-ERR-JOB-001", cancelErr.SymbolicCode);
    }

    [Fact]
    public void SqliteCompressionHistoryStore_PersistsAcrossReopenAndComputesCumulativeSavings()
    {
        using var ws = new TempTestWorkspace();
        var dbPath = ws.SubPath("history", "jobs.db");

        using (var store1 = new SqliteCompressionHistoryStore(dbPath))
        {
            store1.Record(new CompressionHistoryRecord
            {
                JobId = "job-1",
                Operation = "Pack",
                ArchivePath = @"C:\data\repo.ucx",
                SourceSummary = @"C:\data\repo",
                DestinationPath = @"C:\data\repo.ucx",
                Profile = "Balanced",
                FileCount = 42,
                OriginalBytes = 1_000_000,
                CompressedBytes = 250_000,
                SavedBytes = 750_000,
                CompressionRatio = 0.25,
                DuplicateCount = 3,
                DuplicateBytesSaved = 120_000,
                DurationMs = 150,
                ThroughputMbPerSec = 6.35,
            });

            store1.Record(new CompressionHistoryRecord
            {
                JobId = "job-2",
                Operation = "Extract",
                ArchivePath = @"C:\data\repo.ucx",
                SourceSummary = @"C:\data\repo.ucx",
                DestinationPath = @"C:\data\restored",
                Profile = "Balanced",
                FileCount = 42,
                OriginalBytes = 1_000_000,
                CompressedBytes = 250_000,
                SavedBytes = 0,
                CompressionRatio = 0.25,
                DurationMs = 80,
                ThroughputMbPerSec = 11.92,
            });
        }

        using var store2 = new SqliteCompressionHistoryStore(dbPath);
        var all = store2.ListRecent(10);
        Assert.Equal(2, all.Count);
        Assert.Single(store2.ListRecent(10, "Pack"));
        Assert.Single(store2.ListRecent(10, "Extract"));

        var summary = store2.GetSummary();
        Assert.Equal(2, summary.TotalOperations);
        Assert.Equal(1, summary.TotalPackOperations);
        Assert.Equal(1, summary.TotalExtractOperations);
        Assert.Equal(1_000_000, summary.CumulativeOriginalBytesCompressed);
        Assert.Equal(250_000, summary.CumulativeArchiveBytesProduced);
        Assert.Equal(750_000, summary.CumulativeSavedBytes);
        Assert.Equal(120_000, summary.CumulativeDuplicateBytesSaved);
    }

    private sealed class StubDiskSpaceProvider(long availableBytes) : IDiskSpaceProvider
    {
        public long GetAvailableFreeBytes(string targetPath) => availableBytes;
    }
}
