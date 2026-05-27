using Paperboy.Desktop.Mcp;
using System.Windows;

namespace Paperboy.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(arg => string.Equals(arg, "--mcp", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await PaperboyMcpServer.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), e.Args);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
        new WidgetWindow().Show();
    }
}

