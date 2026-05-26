using Microsoft.Win32;
using Paperboy.Desktop.Cards;
using Paperboy.Desktop.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Windows;

namespace Paperboy.Desktop;

public sealed class SourceItem
{
    public required string Path { get; init; }
    public required string Display { get; init; }
}

public partial class MainWindow : Window
{
    private readonly PaperboyBundleService _bundleService = new();
    private readonly AgentCardCatalog _cardCatalog = AgentCardCatalog.Default;

    public ObservableCollection<SourceItem> Sources { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        UpdatePayloadSummary();
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add files to Paperboy bundle",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddSources(dialog.FileNames);
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Add folder to Paperboy bundle",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddSources([dialog.FolderName]);
        }
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in SourceList.SelectedItems.Cast<SourceItem>().ToArray())
        {
            Sources.Remove(item);
        }

        UpdatePayloadSummary();
    }

    private void ClearSources_Click(object sender, RoutedEventArgs e)
    {
        Sources.Clear();
        UpdatePayloadSummary();
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Paperboy bundle",
            Filter = "Paperboy bundle (*.paperboy.zip)|*.paperboy.zip|ZIP archive (*.zip)|*.zip|All files (*.*)|*.*",
            FileName = $"paperboy-{DateTime.Now:yyyyMMdd-HHmmss}.paperboy.zip",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputPathText.Text = dialog.FileName;
        }
    }

    private void BrowseToss_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose toss destination"
        };

        if (dialog.ShowDialog(this) == true)
        {
            TossPathText.Text = dialog.FolderName;
        }
    }

    private async void PackBundle_Click(object sender, RoutedEventArgs e)
    {
        if (Sources.Count == 0)
        {
            SetStatus("Add at least one file or folder before packing.", isError: true);
            return;
        }

        var output = OutputPathText.Text.Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            output = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"paperboy-{DateTime.Now:yyyyMMdd-HHmmss}.paperboy.zip");
            OutputPathText.Text = output;
        }

        try
        {
            SetStatus("Packing bundle...");
            AppendLog($"Packing {Sources.Count} source root(s) to {output}");

            var result = await _bundleService.CreateBundleAsync(
                Sources.Select(s => s.Path),
                output,
                GetCompressionLevel(),
                IncludeHiddenCheck.IsChecked == true,
                OverwriteCheck.IsChecked == true,
                TossPathText.Text.Trim(),
                new Progress<BundleProgress>(p => SetStatus($"Packing {p.CurrentFile} ({p.CompletedFiles}/{p.TotalFiles})")));

            FilesStatText.Text = result.TotalFiles.ToString("N0");
            BundleSizeStatText.Text = FormatBytes(result.BundleBytes);
            SetStatus($"Packed {result.TotalFiles:N0} file(s) into {FormatBytes(result.BundleBytes)}.");
            AppendLog($"Created: {result.BundlePath}");
            AppendLog($"Source bytes: {FormatBytes(result.SourceBytes)}; bundle bytes: {FormatBytes(result.BundleBytes)}; ratio: {result.CompressionRatio:P1}");

            if (!string.IsNullOrWhiteSpace(result.TossedTo))
            {
                AppendLog($"Tossed to: {result.TossedTo}");
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            AppendLog($"ERROR: {ex}");
        }
    }

    private async void InspectBundle_Click(object sender, RoutedEventArgs e)
    {
        var bundlePath = await PickBundlePathAsync();
        if (bundlePath is null)
        {
            return;
        }

        try
        {
            var manifest = await _bundleService.ReadManifestAsync(bundlePath);
            FilesStatText.Text = manifest.TotalFiles.ToString("N0");
            BundleSizeStatText.Text = FormatBytes(new FileInfo(bundlePath).Length);
            SetStatus($"Bundle contains {manifest.TotalFiles:N0} file(s), {FormatBytes(manifest.TotalBytes)} before compression.");
            AppendLog($"Manifest: {manifest.Id}");
            foreach (var entry in manifest.Entries.Take(12))
            {
                AppendLog($" - {entry.BundlePath} ({FormatBytes(entry.Length)})");
            }

            if (manifest.Entries.Count > 12)
            {
                AppendLog($" - ... {manifest.Entries.Count - 12:N0} more");
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            AppendLog($"ERROR: {ex}");
        }
    }

    private async void UnpackBundle_Click(object sender, RoutedEventArgs e)
    {
        var bundlePath = await PickBundlePathAsync();
        if (bundlePath is null)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Choose unpack destination"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var result = await _bundleService.ExtractBundleAsync(bundlePath, dialog.FolderName, OverwriteCheck.IsChecked == true);
            SetStatus($"Unpacked {result.TotalFiles:N0} file(s) to {result.Destination}.");
            AppendLog($"Unpacked: {bundlePath}");
            AppendLog($"Destination: {result.Destination}");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            AppendLog($"ERROR: {ex}");
        }
    }

    private async void TossBundle_Click(object sender, RoutedEventArgs e)
    {
        var bundlePath = await PickBundlePathAsync();
        if (bundlePath is null)
        {
            return;
        }

        var destination = TossPathText.Text.Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose toss destination"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            destination = dialog.FolderName;
            TossPathText.Text = destination;
        }

        try
        {
            var tossedTo = await _bundleService.TossBundleAsync(bundlePath, destination, OverwriteCheck.IsChecked == true);
            SetStatus($"Tossed bundle to {tossedTo}.");
            AppendLog($"Tossed: {bundlePath} -> {tossedTo}");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            AppendLog($"ERROR: {ex}");
        }
    }

    private void CopyCardSchema_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_cardCatalog.SchemaJson);
        SetStatus("Copied Paperboy agent-card schema to clipboard.");
        AppendLog("Agent-card schema copied to clipboard.");
    }

    private void ExportCards_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Paperboy agent card catalog",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "paperboy.cards.json",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, _cardCatalog.ToJson());
            SetStatus($"Exported card catalog to {dialog.FileName}.");
            AppendLog($"Exported cards: {dialog.FileName}");
        }
    }

    private void ShowMcpCommand_Click(object sender, RoutedEventArgs e)
    {
        var appDir = AppContext.BaseDirectory;
        var mcpExe = System.IO.Path.Combine(appDir, "Paperboy.Mcp.exe");
        var command = File.Exists(mcpExe)
            ? $"\"{mcpExe}\""
            : "dotnet run --project .\\apps\\Paperboy.Mcp\\Paperboy.Mcp.csproj";
        Clipboard.SetText(command);
        SetStatus("Copied MCP stdio launch command to clipboard.");
        AppendLog($"MCP stdio command: {command}");
        AppendLog("Tools: paperboy.bundle.pack, inspect, unpack, toss, paperboy.cards.list, paperboy.cards.schema");
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddSources(paths);
        }
    }

    private void AddSources(IEnumerable<string> paths)
    {
        var existing = Sources.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }

            var fullPath = System.IO.Path.GetFullPath(path);
            if (!existing.Add(fullPath))
            {
                continue;
            }

            var kind = Directory.Exists(fullPath) ? "folder" : "file";
            Sources.Add(new SourceItem
            {
                Path = fullPath,
                Display = $"{System.IO.Path.GetFileName(fullPath)}  ·  {kind}  ·  {fullPath}"
            });
        }

        UpdatePayloadSummary();
    }

    private async Task<string?> PickBundlePathAsync()
    {
        var existing = OutputPathText.Text.Trim();
        if (!string.IsNullOrWhiteSpace(existing) && File.Exists(existing))
        {
            return existing;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choose Paperboy bundle",
            Filter = "Paperboy bundle (*.paperboy.zip)|*.paperboy.zip|ZIP archive (*.zip)|*.zip|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputPathText.Text = dialog.FileName;
            return dialog.FileName;
        }

        await Task.CompletedTask;
        return null;
    }

    private CompressionLevel GetCompressionLevel()
    {
        var selected = (CompressionCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString();
        return selected switch
        {
            "Fastest" => CompressionLevel.Fastest,
            "NoCompression" => CompressionLevel.NoCompression,
            "SmallestSize" => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal
        };
    }

    private void UpdatePayloadSummary()
    {
        PayloadSummaryText.Text = Sources.Count == 0
            ? "No payload selected"
            : $"{Sources.Count:N0} source root(s) selected";
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? System.Windows.Media.Brushes.DarkRed
            : System.Windows.Media.Brushes.DarkBlue;
    }

    private void AppendLog(string message)
    {
        LogText.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogText.ScrollToEnd();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}
