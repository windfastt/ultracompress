using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.Services;
using UltraCompress.Desktop.Presentation.ViewModels;

namespace UltraCompress.Desktop.Tests;

public sealed class HistoryAndErrorPresenterTests : IDisposable
{
    private readonly string _tempDir;

    public HistoryAndErrorPresenterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"uc-desktop-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task HistoryWorkspace_PersistsKpisFiltersAndCrossNavigatesToArchiveBrowser()
    {
        var stateDir = Path.Combine(_tempDir, "state");
        var shell = new MainShellViewModel(stateDirectory: stateDir);
        shell.QueueRecovery.AutoExecuteEnqueuedJobs = false;

        var inputFile = Path.Combine(_tempDir, "dataset.json");
        await File.WriteAllTextAsync(inputFile, string.Concat(Enumerable.Repeat("{\"id\":1,\"status\":\"ok\"}\n", 200)));

        shell.CompressWorkspace.DestinationFolder = _tempDir;
        shell.CompressWorkspace.ArchiveFileName = "dataset-archive.ucx";
        shell.CompressWorkspace.SelectedProfile = DesktopLosslessProfile.Tiny;
        await shell.CompressWorkspace.StageDroppedPathsAsync([inputFile]);

        var queuedJob = shell.SubmitCurrentBatchToQueue();
        Assert.NotNull(queuedJob);
        Assert.Equal(DesktopWorkspaceTab.QueueAndRecovery, shell.ActiveTab);

        await shell.QueueRecovery.ProcessQueueAsync();

        Assert.Single(shell.HistoryWorkspace.FilteredRecords);
        Assert.Equal(1, shell.HistoryWorkspace.KpiSummary.TotalCompletedJobs);
        Assert.True(shell.HistoryWorkspace.KpiSummary.CumulativeBytesSaved > 0);
        Assert.Equal(1, shell.HistoryWorkspace.KpiSummary.BitExactVerifiedJobsCount);

        string? openedFolderTarget = null;
        shell.HistoryWorkspace.OpenFolderRequested += p => openedFolderTarget = p;
        var record = shell.HistoryWorkspace.FilteredRecords[0];
        shell.HistoryWorkspace.OpenFolderForRecord(record);
        Assert.Equal(record.OutputPath, openedFolderTarget);

        Assert.True(await shell.OpenArchiveInBrowserAsync(record.OutputPath));
        Assert.Equal(DesktopWorkspaceTab.ArchiveBrowser, shell.ActiveTab);
        Assert.True(shell.ArchiveBrowser.HasLoadedArchive);

        shell.HistoryWorkspace.SearchQuery = "non-existent-query";
        Assert.Empty(shell.HistoryWorkspace.FilteredRecords);
        shell.HistoryWorkspace.SearchQuery = "dataset-archive";
        Assert.Single(shell.HistoryWorkspace.FilteredRecords);
    }

    [Theory]
    [InlineData(ActionableErrorCode.InsufficientDiskSpace)]
    [InlineData(ActionableErrorCode.SourceFileNotFound)]
    [InlineData(ActionableErrorCode.AccessDeniedPermission)]
    [InlineData(ActionableErrorCode.FileLockedInUse)]
    [InlineData(ActionableErrorCode.ArchiveHeaderCorrupted)]
    [InlineData(ActionableErrorCode.Sha256IntegrityMismatch)]
    [InlineData(ActionableErrorCode.DestinationPathTooLong)]
    [InlineData(ActionableErrorCode.PathTraversalBlocked)]
    [InlineData(ActionableErrorCode.EnginePipeOffline)]
    [InlineData(ActionableErrorCode.ExternalToolMissing7z)]
    [InlineData(ActionableErrorCode.MutatingConsentMissing)]
    [InlineData(ActionableErrorCode.UnknownIoFailure)]
    public void ActionableErrorPresenter_AllErrorCodes_ProvideHumanReadableTitleAndRemediation(ActionableErrorCode code)
    {
        var card = ActionableErrorPresenter.FromCode(code, @"C:\Data\Archive.ucx");

        Assert.Equal(code, card.Code);
        Assert.False(string.IsNullOrWhiteSpace(card.Title));
        Assert.DoesNotContain("Exception", card.Title, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(card.Explanation));
        Assert.NotEmpty(card.RemediationSteps);
        Assert.Contains("1.", card.FormattedRemediation);
    }

    [Fact]
    public void ActionableErrorPresenter_FromExceptions_MapsToExpectedCodes()
    {
        Assert.Equal(
            ActionableErrorCode.SourceFileNotFound,
            ActionableErrorPresenter.FromException(new FileNotFoundException("missing", "a.bin")).Code);
        Assert.Equal(
            ActionableErrorCode.AccessDeniedPermission,
            ActionableErrorPresenter.FromException(new UnauthorizedAccessException("denied")).Code);
        Assert.Equal(
            ActionableErrorCode.DestinationPathTooLong,
            ActionableErrorPresenter.FromException(new PathTooLongException("long")).Code);
        Assert.Equal(
            ActionableErrorCode.ArchiveHeaderCorrupted,
            ActionableErrorPresenter.FromException(new InvalidDataException("bad magic")).Code);

        var sharingEx = new IOException("locked", unchecked((int)0x80070020));
        Assert.Equal(ActionableErrorCode.FileLockedInUse, ActionableErrorPresenter.FromException(sharingEx).Code);

        var diskFullEx = new IOException("disk full", unchecked((int)0x80070070));
        Assert.Equal(ActionableErrorCode.InsufficientDiskSpace, ActionableErrorPresenter.FromException(diskFullEx).Code);
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
            // Ignore cleanup errors.
        }
    }
}
