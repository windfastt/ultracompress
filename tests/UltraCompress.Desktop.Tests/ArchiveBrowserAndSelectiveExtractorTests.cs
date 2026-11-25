using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.Services;
using UltraCompress.Desktop.Presentation.ViewModels;

namespace UltraCompress.Desktop.Tests;

public sealed class ArchiveBrowserAndSelectiveExtractorTests : IDisposable
{
    private readonly string _tempDir;

    public ArchiveBrowserAndSelectiveExtractorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"uc-desktop-browser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task UcxArchive_BrowseFilterVerifyAndSelectiveExtract_RoundtripsBitExact()
    {
        var sourceRoot = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "docs"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "media"));

        var readmeBytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("# UltraCompress Architecture\n", 80)));
        var duplicateReadmeBytes = readmeBytes.ToArray();
        var mediaBytes = new byte[4096];
        new Random(99).NextBytes(mediaBytes);

        var fileReadme = Path.Combine(sourceRoot, "docs", "readme.md");
        var fileCopy = Path.Combine(sourceRoot, "docs", "readme-copy.md");
        var fileMedia = Path.Combine(sourceRoot, "media", "clip.mp4");

        await File.WriteAllBytesAsync(fileReadme, readmeBytes);
        await File.WriteAllBytesAsync(fileCopy, duplicateReadmeBytes);
        await File.WriteAllBytesAsync(fileMedia, mediaBytes);

        var analyzer = new BatchContentAnalyzer();
        var (staged, _) = await analyzer.AnalyzePathsAsync([sourceRoot], DesktopLosslessProfile.Archive);

        var archiveService = new UcxArchiveService();
        var ucxPath = Path.Combine(_tempDir, "bundle.ucx");
        await archiveService.CreateUcxArchiveAsync(staged, ucxPath, DesktopLosslessProfile.Archive);

        var extractDir = Path.Combine(_tempDir, "extracted");
        var browserVm = new ArchiveBrowserViewModel(archiveService, extractDir);

        Assert.True(await browserVm.OpenArchiveAsync(ucxPath));
        Assert.NotNull(browserVm.ManifestSummary);
        Assert.Equal(3, browserVm.ManifestSummary!.TotalFileEntries);
        Assert.Equal(1, browserVm.ManifestSummary.DuplicateEntriesCollapsed);

        var verifyReport = await browserVm.VerifyArchiveAsync();
        Assert.NotNull(verifyReport);
        Assert.True(verifyReport!.IsAllValid);
        Assert.Equal(3, verifyReport.VerifiedCount);
        Assert.Contains(browserVm.AllEntries, n => n.VerificationStatus == Sha256VerificationStatus.DedupReferenceVerified);

        browserVm.SearchQuery = "clip.mp4";
        Assert.Single(browserVm.FilteredEntries);
        browserVm.SearchQuery = string.Empty;

        var docsDirNode = Assert.Single(browserVm.AllEntries, n => n.IsDirectory && n.EntryPath.EndsWith("docs", StringComparison.Ordinal));
        browserVm.SetNodeSelection(docsDirNode, true);
        Assert.Equal(2, browserVm.SelectedFileCount);

        var extractResult = await browserVm.ExtractSelectedAsync();
        Assert.NotNull(extractResult);
        Assert.True(extractResult!.Success);
        Assert.Equal(2, extractResult.ExtractedFileCount);

        var extractedReadme = Path.Combine(extractDir, "workspace", "docs", "readme.md");
        var extractedCopy = Path.Combine(extractDir, "workspace", "docs", "readme-copy.md");
        var unextractedMedia = Path.Combine(extractDir, "workspace", "media", "clip.mp4");

        Assert.True(File.Exists(extractedReadme));
        Assert.True(File.Exists(extractedCopy));
        Assert.False(File.Exists(unextractedMedia));
        Assert.Equal(SHA256.HashData(readmeBytes), SHA256.HashData(await File.ReadAllBytesAsync(extractedReadme)));
        Assert.Equal(SHA256.HashData(duplicateReadmeBytes), SHA256.HashData(await File.ReadAllBytesAsync(extractedCopy)));
    }

    [Fact]
    public async Task VerifyArchive_TamperedPayloadByte_FlagsMismatchAndSurfacesIntegrityErrorCard()
    {
        var inputFile = Path.Combine(_tempDir, "critical.sql");
        await File.WriteAllTextAsync(inputFile, string.Concat(Enumerable.Repeat("SELECT * FROM ledger WHERE id = 1;\n", 100)));

        var analyzer = new BatchContentAnalyzer();
        var (staged, _) = await analyzer.AnalyzePathsAsync([inputFile], DesktopLosslessProfile.Balanced);

        var archiveService = new UcxArchiveService();
        var ucxPath = Path.Combine(_tempDir, "tampered.ucx");
        await archiveService.CreateUcxArchiveAsync(staged, ucxPath, DesktopLosslessProfile.Balanced);

        var raw = await File.ReadAllBytesAsync(ucxPath);
        raw[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(ucxPath, raw);

        var browserVm = new ArchiveBrowserViewModel(archiveService, _tempDir);
        Assert.True(await browserVm.OpenArchiveAsync(ucxPath));

        var report = await browserVm.VerifyArchiveAsync();
        Assert.NotNull(report);
        Assert.False(report!.IsAllValid);
        Assert.Equal(1, report.FailedCount);
        Assert.NotNull(browserVm.LastErrorCard);
        Assert.Equal(ActionableErrorCode.Sha256IntegrityMismatch, browserVm.LastErrorCard!.Code);
    }

    [Fact]
    public async Task ZipArchive_BrowseAndExtractSelected_SucceedsAndBlocksPathTraversal()
    {
        var zipPath = Path.Combine(_tempDir, "legacy.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e1 = zip.CreateEntry("alpha/one.txt");
            await using (var w1 = new StreamWriter(e1.Open()))
            {
                await w1.WriteAsync("alpha-one");
            }

            var e2 = zip.CreateEntry("beta/two.txt");
            await using (var w2 = new StreamWriter(e2.Open()))
            {
                await w2.WriteAsync("beta-two");
            }
        }

        var extractRoot = Path.Combine(_tempDir, "zip-out");
        var vm = new ArchiveBrowserViewModel(defaultDestinationFolder: extractRoot);
        Assert.True(await vm.OpenArchiveAsync(zipPath));

        var oneNode = Assert.Single(vm.AllEntries, n => n.EntryPath == "alpha/one.txt");
        vm.SetNodeSelection(oneNode, true);

        var result = await vm.ExtractSelectedAsync();
        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal(1, result.ExtractedFileCount);
        Assert.True(File.Exists(Path.Combine(extractRoot, "alpha", "one.txt")));
        Assert.False(File.Exists(Path.Combine(extractRoot, "beta", "two.txt")));

        Assert.False(UcxArchiveService.TryResolveSafeExtractionPath(extractRoot, "../../escape.dll", out _));
        Assert.False(UcxArchiveService.TryResolveSafeExtractionPath(extractRoot, "/etc/passwd", out _));
        Assert.False(UcxArchiveService.TryResolveSafeExtractionPath(extractRoot, "C:\\Windows\\win.ini", out _));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup errors.
        }
    }
}
