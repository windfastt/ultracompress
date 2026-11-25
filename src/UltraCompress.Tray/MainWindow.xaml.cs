using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using UltraCompress.Desktop.Presentation.Models;
using UltraCompress.Desktop.Presentation.ViewModels;
using UltraCompress.Tray.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace UltraCompress.Tray;

public sealed partial class MainWindow : Window
{
    private readonly EngineConnectionService _engine = new();
    private readonly MainShellViewModel _shell;
    private readonly System.Threading.Timer _statusTimer;
    private readonly XamlUICommand _showDesktopWindowCommand = new();
    private bool _suppressControlCallbacks;

    public MainWindow()
        : this(false)
    {
    }

    public MainWindow(bool startHiddenInTray)
    {
        InitializeComponent();

        Title = "UltraCompress Desktop — Archive Manager";
        AppWindow.Resize(new SizeInt32(1380, 880));

        _shell = new MainShellViewModel(_engine.PipeClient);
        _shell.HistoryWorkspace.OpenFolderRequested += OpenPathInExplorer;
        _shell.QueueRecovery.JobCompletedHistoryProduced += OnHistoryRecordProduced;
        _shell.OptimizationStudio.OptimizationHistoryProduced += OnHistoryRecordProduced;

        _showDesktopWindowCommand.ExecuteRequested += (_, _) => FocusFromActivation();

        BindInitialDefaults();
        SyncAllBindingsToUi();

        if (startHiddenInTray)
        {
            AppWindow.Hide();
        }

        _statusTimer = new System.Threading.Timer(
            _ => _ = PollStatusAsync(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10));

        Activated += (_, _) => _ = PollStatusAsync();
        Closed += OnClosed;
    }

    public XamlUICommand ShowDesktopWindowCommand => _showDesktopWindowCommand;

    internal MainShellViewModel Shell => _shell;

    public void FocusFromActivation()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AppWindow.Show();
            Activate();
        });
    }

    private void OnHistoryRecordProduced(CompressionHistoryRecord record)
    {
        _ = DispatcherQueue.EnqueueAsync(SyncAllBindingsToUi);
    }

    private void BindInitialDefaults()
    {
        _suppressControlCallbacks = true;
        try
        {
            LosslessProfileCombo.SelectedIndex = (int)_shell.CompressWorkspace.SelectedProfile;
            CompressDestFolderBox.Text = _shell.CompressWorkspace.DestinationFolder;
            ArchiveFileNameBox.Text = _shell.CompressWorkspace.ArchiveFileName;
            ExtractDestFolderBox.Text = _shell.ArchiveBrowser.DestinationFolder;
            StudioOutputDirBox.Text = _shell.OptimizationStudio.OutputDirectory;
            ReloadStudioPresetCombo();
        }
        finally
        {
            _suppressControlCallbacks = false;
        }
    }

    private void SyncAllBindingsToUi()
    {
        SyncCompressWorkspaceUi();
        SyncArchiveBrowserUi();
        SyncQueueWorkspaceUi();
        SyncHistoryWorkspaceUi();
        SyncOptimizationStudioUi();

        HeaderEngineStatusText.Text = _shell.QueueRecovery.EngineStatusSummary;
        TrayIcon.ToolTipText = _shell.TrayTooltipText;
        FooterSavedKpiText.Text = $"Cumulative Space Saved: {_shell.HistoryWorkspace.CumulativeSpaceSavedHeadline}";
    }

    private void SyncCompressWorkspaceUi()
    {
        var vm = _shell.CompressWorkspace;
        CompressStatusSubtitleText.Text = vm.WorkspaceStatusMessage;
        ProfileDescriptionText.Text = vm.SelectedProfileOption.Description;
        ProfileSlaMetricsText.Text = vm.ProfileSlaSummary;
        StrategyHeadlineText.Text = vm.StrategyReport.HeadlineSummary;
        BatchDuplicateSummaryTextBlock.Text = vm.DuplicateBadgeText;
        RejectedAlternativesText.Text = vm.StrategyReport.RejectedAlternativesSummary;
        BeforeAfterHeadlineText.Text = vm.BeforeAfterComparisonHeadline;
        EstimatedAfterBarFill.Width = vm.EstimatedAfterBarWidth;
        StartBatchCompressButton.IsEnabled = vm.CanCompressBatch;

        StagedFilesList.ItemsSource = vm.StagedFiles
            .Select(s => new StagedFileRow
            {
                FullPath = s.FullPath,
                RelativePath = s.RelativePath,
                EntropyFormatted = s.EntropyFormatted,
                SelectedStrategy = s.SelectedStrategy,
                DuplicateBadgeText = s.DuplicateBadgeText,
                BeforeToAfterFormatted = s.BeforeToAfterFormatted,
                StrategyJustification = s.StrategyJustification,
            })
            .ToList();

        CategoryBreakdownList.ItemsSource = vm.CategoryBreakdowns
            .Select(c => new CategoryBreakdownRow
            {
                SummaryLine = c.SummaryLine,
                WhySelected = c.WhySelected,
            })
            .ToList();

        DiskSpaceInfoBar.Message = vm.DiskPreCheckSummary;
        DiskSpaceInfoBar.Severity = vm.DiskPreCheck.Severity switch
        {
            PreCheckSeverity.Blocked => InfoBarSeverity.Error,
            PreCheckSeverity.Warning => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Success,
        };

        ApplyErrorInfoBar(CompressErrorInfoBar, vm.LastErrorCard);
        FooterStatusText.Text = vm.WorkspaceStatusMessage;
    }

    private void SyncArchiveBrowserUi()
    {
        var vm = _shell.ArchiveBrowser;
        _suppressControlCallbacks = true;
        try
        {
            if (!string.Equals(ArchivePathBox.Text, vm.CurrentArchivePath, StringComparison.Ordinal))
            {
                ArchivePathBox.Text = vm.CurrentArchivePath;
            }

            ArchiveCodecFilterCombo.ItemsSource = vm.AvailableCodecFilters.ToList();
            var rows = vm.FilteredEntries
                .Select(n => new ArchiveEntryRow
                {
                    EntryPath = n.EntryPath,
                    IndentedDisplayName = n.IndentedDisplayName,
                    OriginalSizeFormatted = n.OriginalSizeFormatted,
                    CompressedSizeFormatted = n.CompressedSizeFormatted,
                    SavingsFormatted = n.SavingsFormatted,
                    StrategyCodec = n.StrategyCodec,
                    Sha256StatusDisplay = n.Sha256StatusDisplay,
                })
                .ToList();

            ArchiveEntriesList.ItemsSource = rows;
            for (var i = 0; i < rows.Count; i++)
            {
                if (vm.FilteredEntries[i].IsSelected)
                {
                    ArchiveEntriesList.SelectedItems.Add(rows[i]);
                }
            }
        }
        finally
        {
            _suppressControlCallbacks = false;
        }

        ArchiveManifestSummaryText.Text = vm.ManifestSummaryLine;
        ArchiveSelectionSummaryText.Text = vm.SelectionSummaryText;
        ArchiveOperationStatusText.Text = vm.StatusMessage;
        VerifyArchiveButton.IsEnabled = vm.CanVerifyArchive;
        ExtractSelectedButton.IsEnabled = vm.CanExtractSelected;
        ExtractAllButton.IsEnabled = vm.CanExtractAll;
        ApplyErrorInfoBar(ArchiveErrorInfoBar, vm.LastErrorCard);
    }

    private void SyncQueueWorkspaceUi()
    {
        var vm = _shell.QueueRecovery;
        var previousSelectedId = (QueueJobsList.SelectedItem as QueueJobRow)?.JobId;

        CrashRecoveryBanner.Visibility = vm.HasCrashRecoveryItems ? Visibility.Visible : Visibility.Collapsed;
        CrashRecoveryBannerTitleText.Text = vm.CrashRecoveryBannerTitle;
        CrashRecoveryBannerDetailText.Text = vm.CrashRecoveryBannerMessage;
        QueueSummaryCounterText.Text = $"{vm.QueueOverviewBadge} · {vm.EngineStatusSummary}";

        InterruptedRecoveryList.ItemsSource = vm.InterruptedJobs
            .Select(i => new InterruptedRecoveryRow
            {
                JobId = i.JobId,
                RecoverySummary = i.RecoverySummary,
            })
            .ToList();

        var jobRows = vm.Jobs
            .Select(j => new QueueJobRow
            {
                JobId = j.JobId,
                JobTitle = j.Title,
                InputSummary = j.InputSummary,
                PriorityLabel = j.PriorityLabel,
                ProfileName = j.ProfileName,
                StateBadgeText = j.StateBadgeText,
                ProgressPercent = j.ProgressPercent,
                MetricsSummaryLine = j.MetricsSummaryLine,
                ErrorDetailBlock = j.HasError
                    ? $"{j.ErrorTitle} — {j.ErrorExplanation}\n{j.ErrorRemediation}"
                    : string.Empty,
            })
            .ToList();

        QueueJobsList.ItemsSource = jobRows;
        if (jobRows.Count > 0)
        {
            QueueJobsList.SelectedItem = jobRows.FirstOrDefault(r => string.Equals(r.JobId, previousSelectedId, StringComparison.Ordinal))
                                         ?? jobRows[0];
        }

        SyncSelectedQueueJobButtons();
    }

    private void SyncSelectedQueueJobButtons()
    {
        var selectedId = (QueueJobsList.SelectedItem as QueueJobRow)?.JobId;
        var selectedJob = _shell.QueueRecovery.Jobs.FirstOrDefault(j => string.Equals(j.JobId, selectedId, StringComparison.Ordinal));
        PauseSelectedJobButton.IsEnabled = selectedJob?.CanPause == true;
        ResumeSelectedJobButton.IsEnabled = selectedJob?.CanResume == true;
        CancelSelectedJobButton.IsEnabled = selectedJob?.CanCancel == true;
    }

    private void SyncHistoryWorkspaceUi()
    {
        var vm = _shell.HistoryWorkspace;
        HistoryKpiSavedText.Text = vm.CumulativeSpaceSavedHeadline;
        HistoryKpiAvgRatioText.Text = vm.AverageSavingsHeadline;
        HistoryKpiTotalJobsText.Text = vm.TotalJobsHeadline;
        HistoryKpiVerifiedText.Text = vm.VerifiedCountHeadline;

        var rows = vm.FilteredRecords
            .Select(r => new HistoryRecordRow
            {
                RecordId = r.RecordId,
                CompletedAtFormatted = r.CompletedAtFormatted,
                ArchiveName = r.ArchiveName,
                SourceSummary = r.SourceSummary,
                ProfileName = r.ProfileName,
                StrategySummary = r.StrategySummary,
                BeforeAfterComparisonText = r.BeforeAfterComparisonText,
                VerificationBadge = r.VerificationBadge,
            })
            .ToList();

        HistoryRecordsList.ItemsSource = rows;
        if (rows.Count > 0 && HistoryRecordsList.SelectedItem is null)
        {
            HistoryRecordsList.SelectedItem = rows[0];
        }
    }

    private void SyncOptimizationStudioUi()
    {
        var vm = _shell.OptimizationStudio;
        _suppressControlCallbacks = true;
        try
        {
            PreserveOriginalToggle.IsOn = vm.PreserveOriginalFile;
            ConsentNonBitExactCheck.IsChecked = vm.ConsentUnderstandNonBitExact;
            ConsentPreviewReviewedCheck.IsChecked = vm.ConsentApproveItemizedPreview;
            ConsentDestructiveReplaceCheck.IsChecked = vm.ConsentAllowReplaceOriginalWhenUnpreserved;
            ConsentDestructiveReplaceCheck.Visibility = vm.RequiresDestructiveReplaceConsent
                ? Visibility.Visible
                : Visibility.Collapsed;
            StudioItemizedPreviewList.ItemsSource = vm.ItemizedPreviewLines.ToList();
        }
        finally
        {
            _suppressControlCallbacks = false;
        }

        StudioPreviewHeadlineText.Text = vm.PreviewSummaryHeadline;
        StudioFidelityImpactText.Text = vm.PreviewFidelitySummary;
        StudioPreservationBadgeText.Text = vm.PreviewPreservationBadge;
        StudioGateInfoBar.Message = vm.GateStatusMessage;
        StudioGateInfoBar.Severity = vm.CanExecuteOptimization ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ExecuteStudioButton.IsEnabled = vm.CanExecuteOptimization;
        StudioExecutionStatusText.Text = vm.StatusMessage;
        ApplyErrorInfoBar(StudioErrorInfoBar, vm.LastErrorCard);
    }

    private static void ApplyErrorInfoBar(InfoBar bar, ActionableErrorCard? errorCard)
    {
        if (errorCard is null)
        {
            bar.IsOpen = false;
            return;
        }

        bar.Title = errorCard.Title;
        bar.Message = $"{errorCard.Explanation}\n{errorCard.FormattedRemediation}";
        bar.IsOpen = true;
    }

    private void SwitchVisibleWorkspace(DesktopWorkspaceTab tab)
    {
        _shell.ActiveTab = tab;
        CompressWorkspacePanel.Visibility = tab == DesktopWorkspaceTab.CompressBatch ? Visibility.Visible : Visibility.Collapsed;
        ArchiveBrowserPanel.Visibility = tab == DesktopWorkspaceTab.ArchiveBrowser ? Visibility.Visible : Visibility.Collapsed;
        QueueWorkspacePanel.Visibility = tab == DesktopWorkspaceTab.QueueAndRecovery ? Visibility.Visible : Visibility.Collapsed;
        HistoryWorkspacePanel.Visibility = tab == DesktopWorkspaceTab.CompressionHistory ? Visibility.Visible : Visibility.Collapsed;
        OptimizationStudioPanel.Visibility = tab == DesktopWorkspaceTab.OptimizationStudio ? Visibility.Visible : Visibility.Collapsed;

        NavCompressItem.IsSelected = tab == DesktopWorkspaceTab.CompressBatch;
        NavBrowserItem.IsSelected = tab == DesktopWorkspaceTab.ArchiveBrowser;
        NavQueueItem.IsSelected = tab == DesktopWorkspaceTab.QueueAndRecovery;
        NavHistoryItem.IsSelected = tab == DesktopWorkspaceTab.CompressionHistory;
        NavStudioItem.IsSelected = tab == DesktopWorkspaceTab.OptimizationStudio;

        SyncAllBindingsToUi();
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        if (Enum.TryParse<DesktopWorkspaceTab>(tag, ignoreCase: true, out var tab))
        {
            SwitchVisibleWorkspace(tab);
        }
    }

    private void OnBatchDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Analyze & stage in UltraCompress (100% lossless)";
            e.DragUIOverride.IsCaptionVisible = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void OnBatchDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items
            .Select(i => i.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        await StagePathsIntoBatchUiAsync(paths).ConfigureAwait(true);
    }

    private async void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");

        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
        {
            await StagePathsIntoBatchUiAsync(files.Select(f => f.Path)).ConfigureAwait(true);
        }
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null && !string.IsNullOrWhiteSpace(folder.Path))
        {
            await StagePathsIntoBatchUiAsync([folder.Path]).ConfigureAwait(true);
        }
    }

    private async void OnStagePathClick(object sender, RoutedEventArgs e)
    {
        var raw = QuickStagePathBox.Text?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }

        await StagePathsIntoBatchUiAsync([raw]).ConfigureAwait(true);
        if (!_shell.CompressWorkspace.HasError)
        {
            QuickStagePathBox.Text = string.Empty;
        }
    }

    private async Task StagePathsIntoBatchUiAsync(IEnumerable<string> paths)
    {
        await _shell.CompressWorkspace.StageDroppedPathsAsync(paths).ConfigureAwait(true);
        _suppressControlCallbacks = true;
        try
        {
            ArchiveFileNameBox.Text = _shell.CompressWorkspace.ArchiveFileName;
            CompressDestFolderBox.Text = _shell.CompressWorkspace.DestinationFolder;
        }
        finally
        {
            _suppressControlCallbacks = false;
        }

        SyncCompressWorkspaceUi();
    }

    private async void OnRemoveSelectedStagedFileClick(object sender, RoutedEventArgs e)
    {
        if (StagedFilesList.SelectedItem is StagedFileRow selected)
        {
            await _shell.CompressWorkspace.RemoveStagedFileAsync(selected.FullPath).ConfigureAwait(true);
            SyncCompressWorkspaceUi();
        }
    }

    private void OnClearBatchClick(object sender, RoutedEventArgs e)
    {
        _shell.CompressWorkspace.ClearBatch();
        SyncCompressWorkspaceUi();
    }

    private void OnLosslessProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressControlCallbacks)
        {
            return;
        }

        if (LosslessProfileCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<DesktopLosslessProfile>(tag, out var profile))
        {
            _shell.CompressWorkspace.SelectedProfile = profile;
            SyncCompressWorkspaceUi();
        }
    }

    private async void OnPickCompressDestinationClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null && !string.IsNullOrWhiteSpace(folder.Path))
        {
            CompressDestFolderBox.Text = folder.Path;
        }
    }

    private void OnCompressDestChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressControlCallbacks)
        {
            return;
        }

        _shell.CompressWorkspace.DestinationFolder = CompressDestFolderBox.Text;
        SyncCompressWorkspaceUi();
    }

    private void OnArchiveFileNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressControlCallbacks)
        {
            return;
        }

        _shell.CompressWorkspace.ArchiveFileName = ArchiveFileNameBox.Text;
        SyncCompressWorkspaceUi();
    }

    private void OnCompressConflictChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressControlCallbacks)
        {
            return;
        }

        if (CompressConflictModeCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<DestinationConflictMode>(tag, out var mode))
        {
            _shell.CompressWorkspace.SelectedConflictMode = mode;
            SyncCompressWorkspaceUi();
        }
    }

    private void OnStartBatchCompressClick(object sender, RoutedEventArgs e)
    {
        var job = _shell.SubmitCurrentBatchToQueue(switchToQueueTab: true);
        if (job is not null)
        {
            SwitchVisibleWorkspace(DesktopWorkspaceTab.QueueAndRecovery);
        }
        else
        {
            SyncCompressWorkspaceUi();
        }
    }

    private async void OnPickArchiveFileClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".ucx");
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file is not null && !string.IsNullOrWhiteSpace(file.Path))
        {
            ArchivePathBox.Text = file.Path;
            await _shell.ArchiveBrowser.OpenArchiveAsync(file.Path).ConfigureAwait(true);
            SyncArchiveBrowserUi();
        }
    }

    private async void OnOpenArchivePathClick(object sender, RoutedEventArgs e)
    {
        var path = ArchivePathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await _shell.ArchiveBrowser.OpenArchiveAsync(path).ConfigureAwait(true);
        SyncArchiveBrowserUi();
    }

    private void OnArchiveSearchChanged(object sender, TextChangedEventArgs e)
    {
        _shell.ArchiveBrowser.SearchQuery = ArchiveSearchBox.Text;
        SyncArchiveBrowserUi();
    }

    private void OnArchiveCodecFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressControlCallbacks)
        {
            return;
        }

        if (ArchiveCodecFilterCombo.SelectedItem is string selected)
        {
            _shell.ArchiveBrowser.SelectedCodecFilter = selected;
            SyncArchiveBrowserUi();
        }
    }

    private void OnArchiveEntriesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressControlCallbacks)
        {
            return;
        }

        foreach (var added in e.AddedItems.OfType<ArchiveEntryRow>())
        {
            var node = _shell.ArchiveBrowser.AllEntries.FirstOrDefault(n =>
                string.Equals(n.EntryPath, added.EntryPath, StringComparison.Ordinal));
            if (node is not null)
            {
                _shell.ArchiveBrowser.SetNodeSelection(node, true);
            }
        }

        foreach (var removed in e.RemovedItems.OfType<ArchiveEntryRow>())
        {
            var node = _shell.ArchiveBrowser.AllEntries.FirstOrDefault(n =>
                string.Equals(n.EntryPath, removed.EntryPath, StringComparison.Ordinal));
            if (node is not null)
            {
                _shell.ArchiveBrowser.SetNodeSelection(node, false);
            }
        }

        ArchiveSelectionSummaryText.Text = _shell.ArchiveBrowser.SelectionSummaryText;
        ExtractSelectedButton.IsEnabled = _shell.ArchiveBrowser.CanExtractSelected;
    }

    private void OnSelectAllArchiveEntriesClick(object sender, RoutedEventArgs e)
    {
        _shell.ArchiveBrowser.SelectAllVisible();
        SyncArchiveBrowserUi();
    }

    private void OnClearArchiveSelectionClick(object sender, RoutedEventArgs e)
    {
        _shell.ArchiveBrowser.ClearSelection();
        SyncArchiveBrowserUi();
    }

    private async void OnPickExtractDestinationClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null && !string.IsNullOrWhiteSpace(folder.Path))
        {
            ExtractDestFolderBox.Text = folder.Path;
        }
    }

    private void OnExtractDestFolderChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressControlCallbacks)
        {
            return;
        }

        _shell.ArchiveBrowser.DestinationFolder = ExtractDestFolderBox.Text;
    }

    private void OnExtractConflictModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressControlCallbacks)
        {
            return;
        }

        if (ExtractConflictModeCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<DestinationConflictMode>(tag, out var mode))
        {
            _shell.ArchiveBrowser.SelectedConflictMode = mode;
        }
    }

    private async void OnVerifyArchiveClick(object sender, RoutedEventArgs e)
    {
        await _shell.ArchiveBrowser.VerifyArchiveAsync().ConfigureAwait(true);
        SyncArchiveBrowserUi();
    }

    private async void OnExtractSelectedClick(object sender, RoutedEventArgs e)
    {
        await _shell.ArchiveBrowser.ExtractSelectedAsync().ConfigureAwait(true);
        SyncArchiveBrowserUi();
    }

    private async void OnExtractAllClick(object sender, RoutedEventArgs e)
    {
        await _shell.ArchiveBrowser.ExtractAllAsync().ConfigureAwait(true);
        SyncArchiveBrowserUi();
    }

    private async void OnResumeSelectedInterruptedClick(object sender, RoutedEventArgs e)
    {
        var targetId = (InterruptedRecoveryList.SelectedItem as InterruptedRecoveryRow)?.JobId
                       ?? _shell.QueueRecovery.InterruptedJobs.FirstOrDefault()?.JobId;
        if (!string.IsNullOrEmpty(targetId))
        {
            await _shell.QueueRecovery.ResumeInterruptedJobAsync(targetId).ConfigureAwait(true);
            SyncAllBindingsToUi();
        }
    }

    private void OnDiscardSelectedInterruptedClick(object sender, RoutedEventArgs e)
    {
        var targetId = (InterruptedRecoveryList.SelectedItem as InterruptedRecoveryRow)?.JobId
                       ?? _shell.QueueRecovery.InterruptedJobs.FirstOrDefault()?.JobId;
        if (!string.IsNullOrEmpty(targetId))
        {
            _shell.QueueRecovery.DiscardInterruptedJobTemp(targetId);
            SyncAllBindingsToUi();
        }
    }

    private async void OnResumeAllInterruptedClick(object sender, RoutedEventArgs e)
    {
        await _shell.QueueRecovery.ResumeAllInterruptedAsync().ConfigureAwait(true);
        SyncAllBindingsToUi();
    }

    private void OnDiscardAllInterruptedClick(object sender, RoutedEventArgs e)
    {
        _shell.QueueRecovery.DiscardAllInterruptedTemp();
        SyncAllBindingsToUi();
    }

    private void OnQueueJobSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SyncSelectedQueueJobButtons();

    private void OnSelectedJobMoveUpClick(object sender, RoutedEventArgs e)
    {
        if (QueueJobsList.SelectedItem is QueueJobRow row)
        {
            _shell.QueueRecovery.MovePriorityUp(row.JobId);
            SyncQueueWorkspaceUi();
        }
    }

    private void OnSelectedJobMoveDownClick(object sender, RoutedEventArgs e)
    {
        if (QueueJobsList.SelectedItem is QueueJobRow row)
        {
            _shell.QueueRecovery.MovePriorityDown(row.JobId);
            SyncQueueWorkspaceUi();
        }
    }

    private async void OnSelectedJobPauseClick(object sender, RoutedEventArgs e)
    {
        if (QueueJobsList.SelectedItem is QueueJobRow row)
        {
            await _shell.QueueRecovery.PauseJobAsync(row.JobId).ConfigureAwait(true);
            SyncAllBindingsToUi();
        }
    }

    private async void OnSelectedJobResumeClick(object sender, RoutedEventArgs e)
    {
        if (QueueJobsList.SelectedItem is QueueJobRow row)
        {
            await _shell.QueueRecovery.ResumeJobAsync(row.JobId).ConfigureAwait(true);
            SyncAllBindingsToUi();
        }
    }

    private async void OnSelectedJobCancelClick(object sender, RoutedEventArgs e)
    {
        if (QueueJobsList.SelectedItem is QueueJobRow row)
        {
            await _shell.QueueRecovery.CancelJobAsync(row.JobId).ConfigureAwait(true);
            SyncAllBindingsToUi();
        }
    }

    private async void OnPauseAllClick(object sender, RoutedEventArgs e)
    {
        await _shell.QueueRecovery.PauseAllAsync().ConfigureAwait(true);
        SyncAllBindingsToUi();
    }

    private async void OnResumeAllClick(object sender, RoutedEventArgs e)
    {
        await _shell.QueueRecovery.ResumeAllAsync().ConfigureAwait(true);
        SyncAllBindingsToUi();
    }

    private void OnHistorySearchChanged(object sender, TextChangedEventArgs e)
    {
        _shell.HistoryWorkspace.SearchQuery = HistorySearchBox.Text;
        SyncHistoryWorkspaceUi();
    }

    private void OnHistoryProfileFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressControlCallbacks)
        {
            return;
        }

        if (HistoryProfileFilterCombo.SelectedItem is ComboBoxItem { Content: string filter })
        {
            _shell.HistoryWorkspace.SelectedProfileFilter = filter;
            SyncHistoryWorkspaceUi();
        }
    }

    private void OnHistoryOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var selectedId = (HistoryRecordsList.SelectedItem as HistoryRecordRow)?.RecordId;
        var record = _shell.HistoryWorkspace.FilteredRecords.FirstOrDefault(r =>
                         string.Equals(r.RecordId, selectedId, StringComparison.Ordinal))
                     ?? _shell.HistoryWorkspace.FilteredRecords.FirstOrDefault();
        _shell.HistoryWorkspace.OpenFolderForRecord(record);
    }

    private async void OnHistoryExtractClick(object sender, RoutedEventArgs e)
    {
        var selectedId = (HistoryRecordsList.SelectedItem as HistoryRecordRow)?.RecordId;
        var record = _shell.HistoryWorkspace.FilteredRecords.FirstOrDefault(r =>
                         string.Equals(r.RecordId, selectedId, StringComparison.Ordinal))
                     ?? _shell.HistoryWorkspace.FilteredRecords.FirstOrDefault();
        if (record is not null)
        {
            await _shell.OpenArchiveInBrowserAsync(record.OutputPath).ConfigureAwait(true);
            SwitchVisibleWorkspace(DesktopWorkspaceTab.ArchiveBrowser);
        }
    }

    private void ReloadStudioPresetCombo()
    {
        StudioTargetPresetCombo.Items.Clear();
        foreach (var opt in _shell.OptimizationStudio.AvailableTargets)
        {
            StudioTargetPresetCombo.Items.Add(new ComboBoxItem
            {
                Content = opt.DisplayName,
                Tag = opt.Id,
            });
        }

        if (StudioTargetPresetCombo.Items.Count > 0)
        {
            StudioTargetPresetCombo.SelectedIndex = 0;
        }
    }

    private void OnStudioSubModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressControlCallbacks)
        {
            return;
        }

        if (StudioSubModeCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<OptimizationSubMode>(tag, out var subMode))
        {
            _suppressControlCallbacks = true;
            try
            {
                _shell.OptimizationStudio.SelectedSubMode = subMode;
                ReloadStudioPresetCombo();
            }
            finally
            {
                _suppressControlCallbacks = false;
            }

            SyncOptimizationStudioUi();
        }
    }

    private void OnStudioTargetPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressControlCallbacks)
        {
            return;
        }

        if (StudioTargetPresetCombo.SelectedItem is ComboBoxItem { Tag: string presetId })
        {
            var match = _shell.OptimizationStudio.AvailableTargets.FirstOrDefault(t =>
                string.Equals(t.Id, presetId, StringComparison.Ordinal));
            if (match is not null)
            {
                _shell.OptimizationStudio.SelectedTarget = match;
                SyncOptimizationStudioUi();
            }
        }
    }

    private async void OnStudioAddFilesClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");

        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
        {
            _shell.OptimizationStudio.StageFiles(files.Select(f => f.Path));
            SyncOptimizationStudioUi();
        }
    }

    private void OnStudioAddPathClick(object sender, RoutedEventArgs e)
    {
        var path = StudioQuickPathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        _shell.OptimizationStudio.StageFiles([path]);
        if (!_shell.OptimizationStudio.HasError)
        {
            StudioQuickPathBox.Text = string.Empty;
        }

        SyncOptimizationStudioUi();
    }

    private void OnStudioClearFilesClick(object sender, RoutedEventArgs e)
    {
        _shell.OptimizationStudio.ClearFiles();
        SyncOptimizationStudioUi();
    }

    private async void OnStudioPickOutputDirClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null && !string.IsNullOrWhiteSpace(folder.Path))
        {
            StudioOutputDirBox.Text = folder.Path;
        }
    }

    private void OnStudioOutputDirChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressControlCallbacks)
        {
            return;
        }

        _shell.OptimizationStudio.OutputDirectory = StudioOutputDirBox.Text;
    }

    private void OnPreserveOriginalToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressControlCallbacks)
        {
            return;
        }

        _shell.OptimizationStudio.PreserveOriginalFile = PreserveOriginalToggle.IsOn;
        SyncOptimizationStudioUi();
    }

    private void OnStudioConsentChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressControlCallbacks)
        {
            return;
        }

        _shell.OptimizationStudio.ConsentUnderstandNonBitExact = ConsentNonBitExactCheck.IsChecked == true;
        _shell.OptimizationStudio.ConsentApproveItemizedPreview = ConsentPreviewReviewedCheck.IsChecked == true;
        _shell.OptimizationStudio.ConsentAllowReplaceOriginalWhenUnpreserved = ConsentDestructiveReplaceCheck.IsChecked == true;
        SyncOptimizationStudioUi();
    }

    private async void OnExecuteStudioOperationClick(object sender, RoutedEventArgs e)
    {
        await _shell.OptimizationStudio.ExecuteOptimizationAsync().ConfigureAwait(true);
        SyncAllBindingsToUi();
    }

    private async void OnReconnectEngineClick(object sender, RoutedEventArgs e) =>
        await PollStatusAsync().ConfigureAwait(true);

    private void OnHideToTrayClick(object sender, RoutedEventArgs e) => AppWindow.Hide();

    private void OnOpenDesktopFromTray(object sender, RoutedEventArgs e) => FocusFromActivation();

    private void OnOpenCompressFromTray(object sender, RoutedEventArgs e)
    {
        FocusFromActivation();
        SwitchVisibleWorkspace(DesktopWorkspaceTab.CompressBatch);
    }

    private void OnOpenBrowserFromTray(object sender, RoutedEventArgs e)
    {
        FocusFromActivation();
        SwitchVisibleWorkspace(DesktopWorkspaceTab.ArchiveBrowser);
    }

    private void OnOpenQueueFromTray(object sender, RoutedEventArgs e)
    {
        FocusFromActivation();
        SwitchVisibleWorkspace(DesktopWorkspaceTab.QueueAndRecovery);
    }

    private void OnExitApplicationClick(object sender, RoutedEventArgs e) => Application.Current.Exit();

    private async Task PollStatusAsync()
    {
        await _shell.RefreshEngineAndTrayStateAsync().ConfigureAwait(false);
        await DispatcherQueue.EnqueueAsync(SyncAllBindingsToUi);
    }

    private static void OpenPathInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true,
                    });
                }
                else
                {
                    var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = folder,
                            UseShellExecute = true,
                        });
                    }
                }
            }
        }
        catch
        {
            // Ignore shell launch errors in non-interactive sessions.
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _statusTimer.Dispose();
        _engine.Dispose();
        TrayIcon.Dispose();
    }
}
