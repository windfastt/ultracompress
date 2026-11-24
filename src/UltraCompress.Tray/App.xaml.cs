using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace UltraCompress.Tray;

public partial class App : Application
{
    private const string InstanceKey = "UltraCompress.Tray";
    private MainWindow? _mainWindow;

    public App()
    {
        if (!EnsureSingleInstance())
        {
            return;
        }

        InitializeComponent();
    }

    private static bool EnsureSingleInstance()
    {
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (instance.IsCurrent)
        {
            instance.Activated += OnAnotherInstanceActivated;
            return true;
        }

        var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        instance.RedirectActivationToAsync(activatedArgs).AsTask().GetAwaiter().GetResult();
        Environment.Exit(0);
        return false;
    }

    private static void OnAnotherInstanceActivated(object? sender, AppActivationArguments args)
    {
        if (Current is App app)
        {
            app._mainWindow?.FocusFromActivation();
        }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }
}