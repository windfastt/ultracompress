using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using UltraCompress.Tray.Services;

namespace UltraCompress.Tray;

public sealed partial class MainWindow : Window
{
    private readonly EngineConnectionService _engine = new();
    private readonly System.Threading.Timer _statusTimer;
    private readonly XamlUICommand _showQueueClickCommand = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "UltraCompress";
        AppWindow.Hide();

        _showQueueClickCommand.ExecuteRequested += (_, _) => FocusFromActivation();

        _statusTimer = new System.Threading.Timer(
            _ => _ = RefreshEngineStatusAsync(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(15));

        Activated += (_, _) => _ = RefreshEngineStatusAsync();
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
        await RefreshEngineStatusAsync().ConfigureAwait(true);
    }

    private async void OnPauseAll(object sender, RoutedEventArgs e)
    {
        var ok = await _engine.PauseAllAsync().ConfigureAwait(true);
        TrayIcon.ToolTipText = ok ? "UltraCompress — all jobs paused" : "UltraCompress — pause failed (engine offline?)";
    }

    private async void OnReconnect(object sender, RoutedEventArgs e)
    {
        await RefreshEngineStatusAsync().ConfigureAwait(true);
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Application.Current.Exit();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _statusTimer.Dispose();
        _engine.Dispose();
        TrayIcon.Dispose();
    }

    private async Task RefreshEngineStatusAsync()
    {
        var status = await _engine.GetStatusAsync().ConfigureAwait(false);
        await DispatcherQueue.EnqueueAsync(() =>
        {
            if (status is null)
            {
                TrayIcon.ToolTipText = "UltraCompress — engine offline";
                return;
            }

            if (status.Type == UltraCompress.Core.Ipc.EngineMessageTypes.Error)
            {
                TrayIcon.ToolTipText = $"UltraCompress — {status.Message}";
                return;
            }

            var paused = status.Paused == true ? " (paused)" : string.Empty;
            TrayIcon.ToolTipText =
                $"UltraCompress — active {status.ActiveJobs ?? 0}, queued {status.QueuedJobs ?? 0}{paused}";
        });
    }
}