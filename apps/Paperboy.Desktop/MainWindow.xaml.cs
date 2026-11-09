using Microsoft.Win32;
using Paperboy.Desktop.Capabilities;
using Paperboy.Desktop.Cards;
using Paperboy.Desktop.Services;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

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
    private readonly FoundryLocalPipelineService _foundryPipeline = new();
    private readonly PaperboyModelManagerService _modelManager = new();
    private readonly PaperboyCapabilityRegistry _capabilityRegistry = PaperboyCapabilityRegistry.Shared;
    private string? _lastFoundrySidecarPath;
    private static readonly HttpClient ApimHealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static readonly JsonSerializerOptions ApimJsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string[] ApimRoutes =
    [
        "GET /paperboy/status",
        "GET /paperboy/routes",
        "GET /paperboy/acp/resources",
        "POST /paperboy/mcp/tools/call",
        "GET /paperboy/foundry/health",
        "GET /paperboy/ocr/readiness",
        "POST /paperboy/validate"
    ];

    private static readonly string[] AcpResources =
    [
        "paperboy://cards",
        "paperboy://cards/schema",
        "paperboy://apim/routes",
        "paperboy://validation/status"
    ];

    public ObservableCollection<SourceItem> Sources { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        UpdatePayloadSummary();
        RefreshApimSurfaceStatus();
        RefreshExtensionStatus(rescan: false);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
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
        var dialog = new Microsoft.Win32.SaveFileDialog
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

    private async void AnalyzeFoundry_Click(object sender, RoutedEventArgs e)
    {
        if (Sources.Count == 0)
        {
            SetStatus("Add at least one file or folder before running Foundry Local analysis.", isError: true);
            return;
        }

        if (!Uri.TryCreate(FoundryEndpointText.Text.Trim(), UriKind.Absolute, out var endpoint))
        {
            SetStatus("Foundry Local endpoint must be an absolute URI.", isError: true);
            return;
        }

        var model = FoundryModelText.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            SetStatus("Foundry Local model alias/id is required.", isError: true);
            return;
        }

        try
        {
            SetStatus("Running offline Foundry Local metadata pipeline...");
            AppendLog($"Foundry Local endpoint: {endpoint}");
            AppendLog($"Foundry Local model: {model}");

            var options = new FoundryPipelineOptions(
                endpoint,
                model,
                FoundryIncludeImagesCheck.IsChecked == true,
                FoundrySampleVideoCheck.IsChecked == true,
                2_000_000);

            var result = await _foundryPipeline.AnalyzeAsync(
                Sources.Select(s => s.Path),
                options,
                new Progress<string>(message => SetStatus(message)));

            var outputRoot = GetMetadataOutputDirectory();
            _lastFoundrySidecarPath = await _foundryPipeline.WriteSidecarAsync(result, outputRoot);

            var tagCount = result.Files.SelectMany(f => f.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            FoundryStatText.Text = $"{result.Files.Count:N0} file(s), {tagCount:N0} unique tag(s)";
            SetStatus($"Foundry Local tagged {result.Files.Count:N0} file(s). Sidecar saved.");
            AppendLog($"Foundry metadata sidecar: {_lastFoundrySidecarPath}");

            foreach (var file in result.Files.Take(8))
            {
                AppendLog($" - {System.IO.Path.GetFileName(file.Path)} :: {string.Join(", ", file.Tags.Take(6))} :: {file.Summary}");
            }

            if (!Sources.Any(s => string.Equals(s.Path, _lastFoundrySidecarPath, StringComparison.OrdinalIgnoreCase)))
            {
                AddSources([_lastFoundrySidecarPath]);
            }
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

    private void RefreshExtensions_Click(object sender, RoutedEventArgs e)
    {
        var index = RefreshExtensionStatus(rescan: true);
        if (index is null)
        {
            return;
        }

        SetStatus($"Discovered {index.Extensions.Length:N0} Paperboy extension package(s).");
        AppendLog($"Extension roots: {string.Join("; ", index.Roots)}");

        foreach (var extension in index.Extensions)
        {
            AppendLog($" - {extension.Id} v{extension.Version} ({extension.Kind}, trust={extension.Trust}) :: {extension.CapabilityIds.Length} capability(ies) :: {extension.ManifestPath}");
        }

        foreach (var diagnostic in index.Diagnostics)
        {
            AppendLog($" ! {diagnostic.Severity}: {diagnostic.Message} [{diagnostic.Path}]");
        }

        AppendLog(index.Policy.Notes);
    }

    private void CopyCapabilityIndex_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_capabilityRegistry.IndexJson());
            SetStatus("Copied Paperboy capability index to clipboard.");
            AppendLog("Capability index copied to clipboard.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            AppendLog($"ERROR: {ex}");
        }
    }

    private void OpenExtensionsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var userRoot = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Paperboy",
                "extensions");

            Directory.CreateDirectory(userRoot);
            Process.Start(new ProcessStartInfo(userRoot) { UseShellExecute = true });
            SetStatus($"Opened extension drop folder {userRoot}.");
            AppendLog($"Extension drop folder: {userRoot}");
            AppendLog($"Drop *.extension.json manifests here, then set {PaperboyCapabilityEnvironment.AllowInvokeVariable}=1 to allow invoking them.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            AppendLog($"ERROR: {ex}");
        }
    }

    private PaperboyCapabilityIndex? RefreshExtensionStatus(bool rescan)
    {
        try
        {
            var index = rescan ? _capabilityRegistry.Refresh() : _capabilityRegistry.Index;
            var externalCapabilities = index.Capabilities
                .Count(capability => !string.Equals(capability.Transport, PaperboyCapabilityTransports.BuiltIn, StringComparison.OrdinalIgnoreCase));
            var invocation = index.Policy.AllowExternalInvocation ? "external invoke on" : "app-trusted invoke only";

            ExtensionsStatusText.Text = index.Extensions.Length == 0
                ? $"No extension packages found in {index.Roots.Length} root(s). {invocation}."
                : $"{index.Extensions.Length:N0} package(s), {externalCapabilities:N0} capability(ies), {index.Diagnostics.Length:N0} diagnostic(s). {invocation}.";

            return index;
        }
        catch (Exception ex)
        {
            ExtensionsStatusText.Text = $"Extension discovery failed: {ex.Message}";
            AppendLog($"ERROR: {ex}");
            return null;
        }
    }

    private void ShowWorkspace_Click(object sender, RoutedEventArgs e)
    {
        WorkspaceLayer.Visibility = Visibility.Visible;
        FoundryModelsLayer.Visibility = Visibility.Collapsed;
        ApimLayer.Visibility = Visibility.Collapsed;
        SetStatus("Workspace layer active.");
    }

    private void ShowFoundryModels_Click(object sender, RoutedEventArgs e)
    {
        WorkspaceLayer.Visibility = Visibility.Collapsed;
        FoundryModelsLayer.Visibility = Visibility.Visible;
        ApimLayer.Visibility = Visibility.Collapsed;
        SetStatus("Foundry Local model-card delivery layer active.");
    }

    private void ShowApimSurface_Click(object sender, RoutedEventArgs e)
    {
        WorkspaceLayer.Visibility = Visibility.Collapsed;
        FoundryModelsLayer.Visibility = Visibility.Collapsed;
        ApimLayer.Visibility = Visibility.Visible;
        RefreshApimSurfaceStatus();
        SetStatus("Paperboy APIM surface active.");
    }

    private void ExportCards_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
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

    private void ExportModelCards_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Foundry Local model cards",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "foundry-local.model-cards.json",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, _cardCatalog.FoundryModelCardsJson());
            SetStatus($"Exported model cards to {dialog.FileName}.");
            AppendLog($"Exported model cards: {dialog.FileName}");
        }
    }

    private async void BundleModelCards_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var outputRoot = GetMetadataOutputDirectory();
            Directory.CreateDirectory(outputRoot);
            var modelCardsPath = System.IO.Path.Combine(outputRoot, "foundry-local.model-cards.json");
            await File.WriteAllTextAsync(modelCardsPath, _cardCatalog.FoundryModelCardsJson());

            var bundlePath = System.IO.Path.Combine(outputRoot, $"foundry-local-model-cards-{DateTime.Now:yyyyMMdd-HHmmss}.paperboy.zip");
            var result = await _bundleService.CreateBundleAsync(
                [modelCardsPath],
                bundlePath,
                CompressionLevel.Optimal,
                includeHidden: false,
                overwrite: true,
                tossDestination: TossPathText.Text.Trim());

            AddSources([bundlePath]);
            SetStatus($"Bundled Foundry Local model cards into {System.IO.Path.GetFileName(bundlePath)}.");
            AppendLog($"Model-card bundle: {result.BundlePath}");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            AppendLog($"ERROR: {ex}");
        }
    }

    private async void RefreshPmmCache_Click(object sender, RoutedEventArgs e)
    {
        await RunPmmActionAsync("Refreshing Foundry Local cache state...", async () =>
        {
            ConfigureModelManager();
            var location = await _modelManager.CacheLocationAsync();
            var cache = await _modelManager.CacheListAsync();
            var cacheLocation = _modelManager.ParseCacheLocation(location.Output);
            var cachedCount = _cardCatalog.FoundryModelCards.Count(card => _modelManager.IsCached(cache.Output, card.Alias) || _modelManager.IsCached(cache.Output, card.ModelId));
            PmmCacheStatText.Text = $"{cachedCount}/{_cardCatalog.FoundryModelCards.Count} Paperboy models cached. {cacheLocation}";
            AppendLog("PMM cache list:");
            AppendLog(cache.Output.Trim());
        });
    }

    private async void DownloadAllModels_Click(object sender, RoutedEventArgs e)
    {
        await RunPmmActionAsync("Downloading all Paperboy model cards...", async () =>
        {
            ConfigureModelManager();
            foreach (var card in _cardCatalog.FoundryModelCards)
            {
                AppendLog($"PMM download: {card.Alias}");
                SetStatus($"Downloading {card.Alias}...");
                var result = await _modelManager.DownloadModelAsync(card.Alias);
                AppendFoundryResult(result);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Download failed for {card.Alias}.");
                }
            }
            await RefreshCacheAfterPmmActionAsync();
        });
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        var model = GetModelTag(sender);
        await RunPmmActionAsync($"Downloading {model}...", async () =>
        {
            ConfigureModelManager();
            var result = await _modelManager.DownloadModelAsync(model);
            AppendFoundryResult(result);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Download failed for {model}.");
            }
            await RefreshCacheAfterPmmActionAsync();
        });
    }

    private async void LoadModel_Click(object sender, RoutedEventArgs e)
    {
        var model = GetModelTag(sender);
        await RunPmmActionAsync($"Loading {model} into Foundry Local service...", async () =>
        {
            ConfigureModelManager();
            var result = await _modelManager.LoadModelAsync(model);
            AppendFoundryResult(result);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Load failed for {model}.");
            }
            FoundryModelText.Text = model;
        });
    }

    private async void InfoModel_Click(object sender, RoutedEventArgs e)
    {
        var model = GetModelTag(sender);
        await RunPmmActionAsync($"Reading model info for {model}...", async () =>
        {
            ConfigureModelManager();
            var result = await _modelManager.ModelInfoAsync(model);
            AppendFoundryResult(result);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Model info failed for {model}.");
            }
        });
    }

    private async void RunModelSmoke_Click(object sender, RoutedEventArgs e)
    {
        var model = GetModelTag(sender);
        await RunPmmActionAsync($"Running smoke prompt on {model}...", async () =>
        {
            ConfigureModelManager();
            var result = await _modelManager.RunPromptAsync(model, "In one short sentence, describe Paperboy model delivery.");
            AppendFoundryResult(result);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Smoke prompt failed for {model}.");
            }
            FoundryModelText.Text = model;
        });
    }

    private void UseModelForFoundry_Click(object sender, RoutedEventArgs e)
    {
        var model = GetModelTag(sender);
        FoundryModelText.Text = model;
        WorkspaceLayer.Visibility = Visibility.Visible;
        FoundryModelsLayer.Visibility = Visibility.Collapsed;
        SetStatus($"{model} selected for Foundry Local metadata pipeline.");
        AppendLog($"Selected Foundry Local model: {model}");
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
        AppendLog("Tools: paperboy.bundle.*, paperboy.foundryLocal.*, paperboy.pmm.*, paperboy.cards.*, paperboy.capabilities.*, paperboy.extensions.*");
    }

    private async void ValidateApimSurface_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("Validating Paperboy APIM surface...");
            AppendLog("APIM validation started.");

            var endpointStatus = await ProbeApimEndpointAsync();
            var foundryStatus = await ProbeFoundryHealthAsync();
            var ocrStatus = RefreshOcrReadiness();
            var bridgeStatus = RefreshMcpBridgeStatus();

            ApimValidationSummaryText.Text =
                $"Routes: {ApimRoutes.Length}; ACP resources: {AcpResources.Length}; APIM endpoint: {endpointStatus}; MCP: {bridgeStatus}; Foundry: {foundryStatus}; OCR: {ocrStatus}";
            SetStatus("APIM surface validation completed.");
            AppendLog($"APIM validation summary: {ApimValidationSummaryText.Text}");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            AppendLog($"ERROR: {ex}");
        }
    }

    private async void CheckApimEndpoint_Click(object sender, RoutedEventArgs e)
    {
        var status = await ProbeApimEndpointAsync();
        SetStatus($"APIM endpoint check: {status}");
        AppendLog($"APIM endpoint check: {status}");
    }

    private async void StartApimService_Click(object sender, RoutedEventArgs e)
    {
        await RunApimServiceScriptAsync("Start", "Start-YumlogAPIMService.ps1");
    }

    private async void StopApimService_Click(object sender, RoutedEventArgs e)
    {
        await RunApimServiceScriptAsync("Stop", "Stop-YumlogAPIMService.ps1");
    }

    private async void RestartApimService_Click(object sender, RoutedEventArgs e)
    {
        await RunApimServiceScriptAsync("Stop", "Stop-YumlogAPIMService.ps1");
        await RunApimServiceScriptAsync("Start", "Start-YumlogAPIMService.ps1");
    }

    private void OpenWidgetStudio_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var studioExe = ResolveSiblingPath("Yumlog.WidgetStudio", "bin", "Debug", "net11.0", "Yumlog.WidgetStudio.exe");
            if (File.Exists(studioExe))
            {
                Process.Start(new ProcessStartInfo(studioExe) { UseShellExecute = true });
                SetStatus("Opened Widget Studio.");
                AppendLog($"Widget Studio launched: {studioExe}");
                return;
            }

            var studioProject = ResolveSiblingPath("Yumlog.WidgetStudio", "Yumlog.WidgetStudio.csproj");
            if (!File.Exists(studioProject))
            {
                throw new FileNotFoundException("Widget Studio project was not found.", studioProject);
            }

            Process.Start(new ProcessStartInfo("dotnet", $"run --project \"{studioProject}\"")
            {
                UseShellExecute = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(studioProject) ?? AppContext.BaseDirectory
            });
            SetStatus("Opening Widget Studio from project.");
            AppendLog($"Widget Studio project launched: {studioProject}");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            AppendLog($"ERROR: {ex}");
        }
    }

    private async void CheckFoundryHealth_Click(object sender, RoutedEventArgs e)
    {
        var status = await ProbeFoundryHealthAsync();
        SetStatus($"Foundry Local health check: {status}");
        AppendLog($"Foundry Local health check: {status}");
    }

    private void CheckOcrReadiness_Click(object sender, RoutedEventArgs e)
    {
        var status = RefreshOcrReadiness();
        SetStatus($"OCR readiness check: {status}");
        AppendLog($"OCR readiness check: {status}");
    }

    private void CopyApimManifest_Click(object sender, RoutedEventArgs e)
    {
        var manifest = new
        {
            id = "paperboy.apim",
            localEndpoint = ApimLocalEndpointText.Text.Trim(),
            routes = ApimRoutes,
            acpResources = AcpResources,
            mcpBridge = ApimMcpBridgeStatusText.Text,
            foundryLocal = new
            {
                endpoint = ApimFoundryEndpointText.Text.Trim(),
                health = ApimFoundryHealthText.Text
            },
            ocr = new
            {
                command = ApimOcrBridgeText.Text.Trim(),
                readiness = ApimOcrReadinessText.Text
            },
            validation = ApimValidationSummaryText.Text
        };

        Clipboard.SetText(JsonSerializer.Serialize(manifest, ApimJsonOptions));
        SetStatus("Copied Paperboy APIM route manifest to clipboard.");
        AppendLog("APIM route manifest copied to clipboard.");
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
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

        var dialog = new Microsoft.Win32.OpenFileDialog
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

    private void RefreshApimSurfaceStatus()
    {
        ApimRouteStatusText.Text = $"{ApimRoutes.Length} APIM route contract(s) ready for gateway mapping.";
        ApimAcpResourceStatusText.Text = $"{AcpResources.Length} ACP/MCP resource URI(s) declared.";
        RefreshMcpBridgeStatus();
        RefreshOcrReadiness();
    }

    private async Task RunApimServiceScriptAsync(string action, string scriptName)
    {
        try
        {
            var scriptPath = ResolveLauncherPath(scriptName);
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"APIM service launcher not found: {scriptName}", scriptPath);
            }

            var presentTense = action.Equals("Stop", StringComparison.OrdinalIgnoreCase) ? "Stopping" : $"{action}ing";
            SetStatus($"{presentTense} Yumlog APIM service...");
            AppendLog($"APIM service {action.ToLowerInvariant()} requested via {scriptPath}");

            using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Failed to start PowerShell launcher.");

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(output))
            {
                AppendLog(output.Trim());
            }
            if (!string.IsNullOrWhiteSpace(error))
            {
                AppendLog(error.Trim());
            }

            var summary = process.ExitCode == 0
                ? $"APIM service {action.ToLowerInvariant()} completed."
                : $"APIM service {action.ToLowerInvariant()} failed with exit code {process.ExitCode}.";
            ApimServiceStatusText.Text = summary;
            SetStatus(summary, process.ExitCode != 0);
        }
        catch (Exception ex)
        {
            ApimServiceStatusText.Text = ex.Message;
            SetStatus(ex.Message, isError: true);
            AppendLog($"ERROR: {ex}");
        }
    }

    private string RefreshMcpBridgeStatus()
    {
        var appDir = AppContext.BaseDirectory;
        var mcpExe = System.IO.Path.Combine(appDir, "Paperboy.Mcp.exe");
        var paperboyExe = System.IO.Path.Combine(appDir, "Paperboy.exe");
        var paperboyDll = System.IO.Path.Combine(appDir, "Paperboy.dll");

        var status = File.Exists(mcpExe)
            ? $"Standalone MCP executable found: {mcpExe}"
            : File.Exists(paperboyExe) || File.Exists(paperboyDll)
                ? "Embedded --mcp stdio bridge is available from the Paperboy app output; standalone Paperboy.Mcp.exe is not in this output."
                : "MCP bridge output not found. Build Paperboy.Desktop or Paperboy.Mcp before hosting APIM tool routes.";

        ApimMcpBridgeStatusText.Text = status;
        return status;
    }

    private async Task<string> ProbeApimEndpointAsync()
    {
        if (!Uri.TryCreate(ApimLocalEndpointText.Text.Trim(), UriKind.Absolute, out var endpoint))
        {
            ApimServiceStatusText.Text = "Invalid local APIM endpoint URI.";
            return "invalid-uri";
        }

        var statusUri = AppendEndpointSegment(endpoint, "status");
        try
        {
            using var response = await ApimHealthClient.GetAsync(statusUri);
            var status = response.IsSuccessStatusCode
                ? $"online at {statusUri} (HTTP {(int)response.StatusCode})"
                : $"listener returned HTTP {(int)response.StatusCode} at {statusUri}";
            ApimServiceStatusText.Text = status;
            return status;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            var status = $"No local APIM listener at {statusUri}. Surface contract is ready for host wiring.";
            ApimServiceStatusText.Text = status;
            return "offline/design-only";
        }
    }

    private async Task<string> ProbeFoundryHealthAsync()
    {
        var endpointText = ApimFoundryEndpointText.Text.Trim();
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
        {
            ApimFoundryHealthText.Text = "Invalid Foundry Local endpoint URI.";
            return "invalid-uri";
        }

        FoundryEndpointText.Text = endpointText;
        var modelsUri = BuildFoundryModelsUri(endpoint);

        try
        {
            using var response = await ApimHealthClient.GetAsync(modelsUri);
            var status = response.IsSuccessStatusCode
                ? $"healthy at {modelsUri} (HTTP {(int)response.StatusCode})"
                : $"HTTP {(int)response.StatusCode} from {modelsUri}";
            ApimFoundryHealthText.Text = status;
            return status;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            var status = $"offline or unreachable at {modelsUri}";
            ApimFoundryHealthText.Text = status;
            return status;
        }
    }

    private string RefreshOcrReadiness()
    {
        var command = ApimOcrBridgeText.Text.Trim();
        var resolved = ResolveExecutablePath(command);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            var status = $"Yumlog.Native OCR bridge command resolved: {resolved}";
            ApimOcrReadinessText.Text = status;
            return "bridge-resolved";
        }

        var nativeProject = ResolveYumlogNativeProjectPath();
        if (File.Exists(nativeProject))
        {
            var status = $"Yumlog.Native project found at {nativeProject}. Build or publish it to enable OCR readiness probes.";
            ApimOcrReadinessText.Text = status;
            return "project-found/build-needed";
        }

        ApimOcrReadinessText.Text = "Yumlog.Native OCR bridge not found. Configure a built Yumlog.Native.exe command.";
        return "bridge-missing";
    }

    private static Uri AppendEndpointSegment(Uri endpoint, string segment)
    {
        return new Uri($"{endpoint.ToString().TrimEnd('/')}/{segment.TrimStart('/')}");
    }

    private static Uri BuildFoundryModelsUri(Uri endpoint)
    {
        var baseText = endpoint.ToString().TrimEnd('/');
        return new Uri(baseText.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{baseText}/models"
            : $"{baseText}/v1/models");
    }

    private static string ResolveYumlogNativeProjectPath()
    {
        return ResolveSiblingPath("Yumlog.Native", "Yumlog.Native.fsproj");
    }

    private static string ResolveLauncherPath(string scriptName)
    {
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "launchers", scriptName));
    }

    private static string ResolveSiblingPath(params string[] segments)
    {
        var parts = new List<string> { AppContext.BaseDirectory, "..", "..", "..", ".." };
        parts.AddRange(segments);
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(parts.ToArray()));
    }

    private static string? ResolveExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(command.Trim('"'));
        if (System.IO.Path.IsPathFullyQualified(expanded) ||
            expanded.Contains(System.IO.Path.DirectorySeparatorChar) ||
            expanded.Contains(System.IO.Path.AltDirectorySeparatorChar))
        {
            var fullPath = System.IO.Path.GetFullPath(expanded);
            return File.Exists(fullPath) ? fullPath : null;
        }

        var names = System.IO.Path.HasExtension(expanded)
            ? [expanded]
            : new[] { expanded, $"{expanded}.exe" };

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var candidate = System.IO.Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private string GetMetadataOutputDirectory()
    {
        var output = OutputPathText.Text.Trim();
        if (!string.IsNullOrWhiteSpace(output))
        {
            var parent = System.IO.Path.GetDirectoryName(output);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                return parent;
            }
        }

        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Paperboy", "metadata");
    }

    private void ConfigureModelManager()
    {
        var cliPath = PmmCliPathText.Text.Trim();
        _modelManager.CliPath = string.IsNullOrWhiteSpace(cliPath) ? "foundry" : cliPath;
        _modelManager.Device = (PmmDeviceCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Auto";
    }

    private async Task RefreshCacheAfterPmmActionAsync()
    {
        var cache = await _modelManager.CacheListAsync();
        var cachedCount = _cardCatalog.FoundryModelCards.Count(card => _modelManager.IsCached(cache.Output, card.Alias) || _modelManager.IsCached(cache.Output, card.ModelId));
        PmmCacheStatText.Text = $"{cachedCount}/{_cardCatalog.FoundryModelCards.Count} Paperboy models cached.";
    }

    private async Task RunPmmActionAsync(string status, Func<Task> action)
    {
        try
        {
            SetStatus(status);
            AppendLog(status);
            await action();
            SetStatus("PMM action completed.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            AppendLog($"ERROR: {ex}");
        }
    }

    private void AppendFoundryResult(FoundryCliResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            AppendLog(result.Output.Trim());
        }
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            AppendLog(result.Error.Trim());
        }
        AppendLog($"Foundry CLI exit code: {result.ExitCode}");
    }

    private static string GetModelTag(object sender)
    {
        return sender is FrameworkElement element && element.Tag is string tag && !string.IsNullOrWhiteSpace(tag)
            ? tag
            : throw new InvalidOperationException("Model action is missing a model alias.");
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
