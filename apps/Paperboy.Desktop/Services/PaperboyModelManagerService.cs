using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Paperboy.Desktop.Services;

public sealed record FoundryCliResult(int ExitCode, string Output, string Error);

public sealed class PaperboyModelManagerService
{
    public string CliPath { get; set; } = "foundry";
    public string Device { get; set; } = "Auto";

    public async Task<FoundryCliResult> ModelInfoAsync(string model, CancellationToken cancellationToken = default)
    {
        return await RunFoundryAsync(["model", "info", model], cancellationToken);
    }

    public async Task<FoundryCliResult> DownloadModelAsync(string model, CancellationToken cancellationToken = default)
    {
        return await RunFoundryAsync(["model", "download", model, "--device", Device], cancellationToken);
    }

    public async Task<FoundryCliResult> LoadModelAsync(string model, CancellationToken cancellationToken = default)
    {
        return await RunFoundryAsync(["model", "load", model, "--device", Device], cancellationToken);
    }

    public async Task<FoundryCliResult> RunPromptAsync(string model, string prompt, CancellationToken cancellationToken = default)
    {
        return await RunFoundryAsync(["model", "run", model, "--device", Device, "--retain", "--prompt", prompt], cancellationToken);
    }

    public async Task<FoundryCliResult> CacheListAsync(CancellationToken cancellationToken = default)
    {
        return await RunFoundryAsync(["cache", "list"], cancellationToken);
    }

    public async Task<FoundryCliResult> CacheLocationAsync(CancellationToken cancellationToken = default)
    {
        return await RunFoundryAsync(["cache", "location"], cancellationToken);
    }

    public bool IsCached(string cacheOutput, string model)
    {
        return cacheOutput.Contains(model, StringComparison.OrdinalIgnoreCase);
    }

    public string ParseCacheLocation(string output)
    {
        var match = Regex.Match(output, @"Cache directory path:\s*(?<path>.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups["path"].Value.Trim() : "";
    }

    public string CleanModelResponse(string output)
    {
        var lines = output
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !Regex.IsMatch(line, @"^\[\d{2}:\d{2}:\d{2}\s+ERR\]\s+Failed to process model #0 on page \d+\.?$"))
            .Where(line => !Regex.IsMatch(line, @"^Model .+ was found in the local cache\.$", RegexOptions.IgnoreCase))
            .Where(line => !line.Contains("Thinking", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Equals("🤖", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var cleaned = string.Join(Environment.NewLine, lines).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? output.Trim() : cleaned;
    }

    private async Task<FoundryCliResult> RunFoundryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CliPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {CliPath}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        return new FoundryCliResult(process.ExitCode, await outputTask, await errorTask);
    }
}
