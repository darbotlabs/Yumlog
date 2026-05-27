using Paperboy.Desktop.Services;
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
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Bottom - Height - 24;
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
        Close();
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
        Width = 560;
        Height = 420;
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Bottom - Height - 24;
        ExpandedPanel.Visibility = Visibility.Visible;
        WidgetStatusText.Text = "Model chat ready";
    }

    private void Collapse()
    {
        ExpandedPanel.Visibility = Visibility.Collapsed;
        Width = 360;
        Height = 96;
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Bottom - Height - 24;
        WidgetStatusText.Text = "Click to open model chat";
    }
}
