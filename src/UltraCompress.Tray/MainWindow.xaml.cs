using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using UltraCompress.Core.Ipc;
using UltraCompress.Tray.Services;

namespace UltraCompress.Tray;

public sealed partial class MainWindow : Window
{
    private readonly EngineConnectionService _engine = new();
    private readonly System.Threading.Timer _statusTimer;
    private readonly XamlUICommand _showQueueClickCommand = new();
    private readonly List<QueueJobDisplay> _jobs = [];

    public MainWindow()
    {
        InitializeComponent();
        Title = "UltraCompress — Queue";
        AppWindow.Hide();

        _showQueueClickCommand.ExecuteRequested += (_, _) => FocusFromActivation();

        _statusTimer = new System.Threading.Timer(
            _ => _ = RefreshAllAsync(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(15));

        Activated += (_, _) => _ = RefreshAllAsync();
        Closed += OnClosed;
    }

    public XamlUICommand ShowQueueClickCommand => _showQueueClickCommand;

    public void FocusFromActivation()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AppWindow.Show();
            Activate();
        });
    }

    private async void OnOpenQueue(object sender, RoutedEventArgs e)
    {
        FocusFromActivation();
        await RefreshAllAsync().ConfigureAwait(true);
    }

    private async void OnEnqueue(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var profile = (ProfileBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Balanced";
        var response = await _engine.EnqueueJobAsync(path, profile).ConfigureAwait(true);
        if (response?.Success == true)
        {
            PathBox.Text = string.Empty;
        }

        await RefreshAllAsync().ConfigureAwait(true);
    }

    private async void OnPauseAll(object sender, RoutedEventArgs e)
    {
        await _engine.PauseAllAsync().ConfigureAwait(true);
        await RefreshAllAsync().ConfigureAwait(true);
    }

    private async void OnResumeAll(object sender, RoutedEventArgs e)
    {
        await _engine.ResumeAllAsync().ConfigureAwait(true);
        await RefreshAllAsync().ConfigureAwait(true);
    }

    private async void OnReconnect(object sender, RoutedEventArgs e) => await RefreshAllAsync().ConfigureAwait(true);

    private async void OnCancelSelected(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is QueueJobDisplay job)
        {
            await _engine.CancelJobAsync(job.JobId).ConfigureAwait(true);
            await RefreshAllAsync().ConfigureAwait(true);
        }
    }

    private void OnJobItemClick(object sender, ItemClickEventArgs e)
    {
        JobList.SelectedItem = e.ClickedItem;
    }

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Exit();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _statusTimer.Dispose();
        _engine.Dispose();
        TrayIcon.Dispose();
    }

    private async Task RefreshAllAsync()
    {
        var status = await _engine.GetStatusAsync().ConfigureAwait(false);
        var jobsResponse = await _engine.ListJobsAsync().ConfigureAwait(false);
        await DispatcherQueue.EnqueueAsync(() =>
        {
            ApplyStatusTooltip(status);
            ApplyJobList(jobsResponse);
        });
    }

    private void ApplyStatusTooltip(EngineResponse? status)
    {
        if (status is null)
        {
            TrayIcon.ToolTipText = "UltraCompress — engine offline";
            return;
        }

        if (status.Type == EngineMessageTypes.Error)
        {
            TrayIcon.ToolTipText = $"UltraCompress — {status.Message}";
            return;
        }

        var paused = status.Paused == true ? " (paused)" : string.Empty;
        TrayIcon.ToolTipText =
            $"UltraCompress — active {status.ActiveJobs ?? 0}, queued {status.QueuedJobs ?? 0}{paused}";
    }

    private void ApplyJobList(EngineResponse? response)
    {
        _jobs.Clear();
        if (response?.Jobs is { Count: > 0 } jobs)
        {
            foreach (var job in jobs)
            {
                _jobs.Add(new QueueJobDisplay
                {
                    JobId = job.JobId,
                    Path = job.Path,
                    Profile = job.Profile,
                    State = job.State,
                });
            }
        }

        JobList.ItemsSource = null;
        JobList.ItemsSource = _jobs;
    }
}