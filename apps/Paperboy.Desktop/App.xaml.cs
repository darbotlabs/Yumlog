using Paperboy.Desktop.Mcp;
using System.Windows;

namespace Paperboy.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private MainWindow? _mainWindow;
    private WidgetWindow? _widgetWindow;
    private TrayIconController? _trayIcon;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(arg => string.Equals(arg, "--mcp", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await PaperboyMcpServer.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), e.Args);
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        _trayIcon = new TrayIconController(this);
        ShowPaperboyWindow();
        ShowWidgetWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    public void ShowPaperboyWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }

        ShowAndActivate(_mainWindow);
    }

    public void ShowWidgetWindow()
    {
        if (_widgetWindow is null)
        {
            _widgetWindow = new WidgetWindow();
            _widgetWindow.Closed += (_, _) => _widgetWindow = null;
        }

        ShowAndActivate(_widgetWindow);
    }

    private static void ShowAndActivate(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }
}

