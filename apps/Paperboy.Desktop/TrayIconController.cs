using System.Diagnostics;
using System.IO;
using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Paperboy.Desktop;

public sealed class TrayIconController : IDisposable
{
    private const string ApimServiceName = "Yumlog.APIM.Service";
    private readonly App _app;
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _disposed;

    public TrayIconController(App app)
    {
        _app = app;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Command("Open Paperboy", (_, _) => RunOnUiThread(_app.ShowPaperboyWindow)));
        menu.Items.Add(Command("Open Widget Studio", (_, _) => OpenWidgetStudio()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Command("Start APIM Service", (_, _) => LaunchLauncherScript("Start-YumlogAPIMService.ps1", "APIM service start requested.")));
        menu.Items.Add(Command("Stop APIM Service", (_, _) => LaunchLauncherScript("Stop-YumlogAPIMService.ps1", "APIM service stop requested.")));
        menu.Items.Add(Command("Run OCR smoke", (_, _) => LaunchLauncherScript("Test-YumlogNativeOcr.ps1", "OCR smoke requested.", keepOpen: true)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Command("Show MCP status", (_, _) => ShowMcpStatus()));
        menu.Items.Add(Command("Show ACP status", (_, _) => ShowAcpStatus()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Command("Exit Paperboy", (_, _) => RunOnUiThread(_app.Shutdown)));

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Paperboy",
            Icon = Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => RunOnUiThread(_app.ShowPaperboyWindow);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private static Forms.ToolStripMenuItem Command(string text, EventHandler onClick)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += onClick;
        return item;
    }

    private void RunOnUiThread(Action action)
    {
        if (_app.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _app.Dispatcher.Invoke(action);
        }
    }

    private void OpenWidgetStudio()
    {
        var root = FindYumlogRoot();
        var project = root is null
            ? null
            : Path.Combine(root, "apps", "Yumlog.WidgetStudio", "Yumlog.WidgetStudio.csproj");

        if (!string.IsNullOrWhiteSpace(project) && File.Exists(project))
        {
            LaunchPowerShell("dotnet run --project " + Quote(project), Path.GetDirectoryName(project), keepOpen: true);
            ShowBalloon("Widget Studio", "Yumlog.WidgetStudio launch requested.");
            return;
        }

        RunOnUiThread(_app.ShowWidgetWindow);
        ShowBalloon("Widget Studio", "Widget Studio project not found; opened Paperboy widget popout instead.");
    }

    private void LaunchLauncherScript(string scriptName, string successMessage, bool keepOpen = false)
    {
        var root = FindYumlogRoot();
        var script = root is null ? null : Path.Combine(root, "launchers", scriptName);
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
        {
            System.Windows.MessageBox.Show($"Launcher script not found: {scriptName}", "Paperboy tray", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LaunchPowerShell("-ExecutionPolicy Bypass -File " + Quote(script), root, keepOpen);
        ShowBalloon("Paperboy tray", successMessage);
    }

    private static void LaunchPowerShell(string command, string? workingDirectory, bool keepOpen)
    {
        var arguments = $"{(keepOpen ? "-NoExit " : string.Empty)}-NoProfile {command}";
        Process.Start(new ProcessStartInfo("powershell.exe", arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? AppContext.BaseDirectory : workingDirectory
        });
    }

    private void ShowMcpStatus()
    {
        var appDir = AppContext.BaseDirectory;
        var paperboyExe = Path.Combine(appDir, "Paperboy.exe");
        var paperboyDll = Path.Combine(appDir, "Paperboy.dll");
        var root = FindYumlogRoot();
        var mcpProject = root is null ? null : Path.Combine(root, "apps", "Paperboy.Mcp", "Paperboy.Mcp.csproj");

        var lines = new[]
        {
            File.Exists(paperboyExe) || File.Exists(paperboyDll)
                ? "Embedded MCP bridge: available via Paperboy --mcp"
                : "Embedded MCP bridge: not found in current output",
            !string.IsNullOrWhiteSpace(mcpProject) && File.Exists(mcpProject)
                ? $"Standalone MCP project: {mcpProject}"
                : "Standalone MCP project: not found",
            "Tools: bundle, inspect, unpack, toss, Foundry Local, PMM, cards, APIM/ACP contracts"
        };

        System.Windows.MessageBox.Show(string.Join(Environment.NewLine, lines), "Paperboy MCP status", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowAcpStatus()
    {
        var root = FindYumlogRoot();
        var startScript = root is null ? null : Path.Combine(root, "launchers", "Start-YumlogAPIMService.ps1");
        var serviceStatus = QueryWindowsServiceStatus(ApimServiceName);
        var lines = new[]
        {
            $"APIM service: {serviceStatus}",
            File.Exists(startScript) ? $"APIM launcher: {startScript}" : "APIM launcher: not found",
            "ACP resources:",
            "paperboy://cards",
            "paperboy://cards/schema",
            "paperboy://apim/routes",
            "paperboy://validation/status"
        };

        System.Windows.MessageBox.Show(string.Join(Environment.NewLine, lines), "Paperboy ACP status", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string QueryWindowsServiceStatus(string serviceName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("sc.exe", $"query {serviceName}")
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (process is null)
            {
                return "unknown";
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            var stateLine = output.Split(Environment.NewLine).FirstOrDefault(line => line.TrimStart().StartsWith("STATE", StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(stateLine) ? "not installed or unavailable" : stateLine.Trim();
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.Message})";
        }
    }

    private static string? FindYumlogRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "launchers")) &&
                Directory.Exists(Path.Combine(directory.FullName, "apps")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private void ShowBalloon(string title, string text)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(2500);
    }

    private static string Quote(string value)
    {
        return '"' + value.Replace("\"", "\\\"") + '"';
    }
}
