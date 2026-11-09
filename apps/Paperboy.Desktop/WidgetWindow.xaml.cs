using Paperboy.Desktop.Services;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Paperboy.Desktop;

public partial class WidgetWindow : Window
{
    private readonly PaperboyModelManagerService _modelManager = new() { Device = "GPU" };
    private readonly LocalOrchestratorService _orchestrators = new();

    public WidgetWindow()
    {
        InitializeComponent();
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        PositionForCurrentSize();
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        base.OnClosed(e);
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.WorkArea))
        {
            PositionForCurrentSize();
        }
    }

    private void TileButton_Click(object sender, RoutedEventArgs e)
    {
        Expand();
    }

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var prompt = WidgetPromptText.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            WidgetStatusText.Text = "Enter a prompt first";
            return;
        }

        var model = ((WidgetModelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()) ?? "qwen3-0.6b";
        WidgetStatusText.Text = $"Prompting {model}...";
        WidgetResponseText.Text = $"You: {prompt}{Environment.NewLine}{Environment.NewLine}";

        try
        {
            var result = await _modelManager.RunPromptAsync(model, prompt);
            var cleanOutput = _modelManager.CleanModelResponse(result.Output);
            if (!string.IsNullOrWhiteSpace(result.Output))
            {
                WidgetResponseText.AppendText($"Model ({model}):{Environment.NewLine}{cleanOutput}");
            }
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                WidgetResponseText.AppendText($"{Environment.NewLine}{Environment.NewLine}{result.Error.Trim()}");
            }
            WidgetStatusText.Text = result.ExitCode == 0 ? "Response received" : $"Model exited {result.ExitCode}";
        }
        catch (Exception ex)
        {
            WidgetStatusText.Text = "Prompt failed";
            WidgetResponseText.AppendText(ex.ToString());
        }
    }

    private async void FinishPipeline_Click(object sender, RoutedEventArgs e)
    {
        Expand();
        var model = ((WidgetModelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()) ?? "qwen3-0.6b";
        var prompt = """
        Review the current Paperboy pipeline idea and propose the next concrete offline step for delivering Foundry Local model bundles. Keep it brief and actionable.
        """;
        WidgetPromptText.Text = prompt;
        WidgetStatusText.Text = "Finishing pipeline with local model...";
        try
        {
            var result = await _modelManager.RunPromptAsync(model, prompt);
            WidgetResponseText.Text = _modelManager.CleanModelResponse(result.Output);
            WidgetStatusText.Text = result.ExitCode == 0 ? "Pipeline suggestion ready" : $"Pipeline model exited {result.ExitCode}";
        }
        catch (Exception ex)
        {
            WidgetStatusText.Text = "Pipeline prompt failed";
            WidgetResponseText.Text = ex.ToString();
        }
    }

    private void ShowOrchestrators_Click(object sender, RoutedEventArgs e)
    {
        var endpoints = _orchestrators.Discover();
        WidgetResponseText.Text = endpoints.Count == 0
            ? "No local dayourbot or dlm-lib orchestrator endpoints were found."
            : string.Join(Environment.NewLine, endpoints.Select(e => $"{e.Name} · {e.Kind} · {e.Path}"));
        WidgetStatusText.Text = $"{endpoints.Count} orchestrator endpoint(s)";
    }

    private void Expand()
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(560, Math.Max(360, workArea.Width - 48));
        Height = Math.Min(420, Math.Max(220, workArea.Height - 48));
        PositionForCurrentSize();
        ExpandedPanel.Visibility = Visibility.Visible;
        WidgetStatusText.Text = "Model chat ready";
    }

    private void Collapse()
    {
        ExpandedPanel.Visibility = Visibility.Collapsed;
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(360, Math.Max(240, workArea.Width - 48));
        Height = Math.Min(96, Math.Max(72, workArea.Height - 48));
        PositionForCurrentSize();
        WidgetStatusText.Text = "Click to open model chat";
    }

    private void PositionForCurrentSize()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 12, workArea.Right - Width - 24);
        Top = Math.Max(workArea.Top + 12, workArea.Bottom - Height - 24);
    }
}
