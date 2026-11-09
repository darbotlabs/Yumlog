using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Paperboy.Desktop.Capabilities;

/// <summary>
/// Executes discovered capabilities that declare an invocable transport.
/// Process capabilities are started with an explicit argument list (never a shell),
/// and <c>{{token}}</c> placeholders are substituted from the caller's arguments.
/// </summary>
public sealed partial class PaperboyCapabilityInvoker
{
    private const int MaxCapturedCharacters = 200_000;

    private static readonly HttpClient Http = new();

    private static readonly JsonSerializerOptions BodyOptions = new()
    {
        WriteIndented = false
    };

    public async Task<PaperboyCapabilityInvocationResult> InvokeAsync(
        PaperboyCapabilityEntry entry,
        JsonObject arguments,
        int? timeoutSecondsOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        arguments ??= [];

        var descriptor = entry.Descriptor
            ?? throw new InvalidOperationException($"Capability '{entry.Id}' has no invocable descriptor.");

        if (!entry.Invocable)
        {
            throw new InvalidOperationException($"Capability '{entry.Id}' is not invocable. {entry.InvocationNote}");
        }

        var timeoutSeconds = timeoutSecondsOverride is > 0 and <= 900
            ? timeoutSecondsOverride.Value
            : descriptor.TimeoutSeconds;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            return string.Equals(descriptor.Transport, PaperboyCapabilityTransports.Http, StringComparison.OrdinalIgnoreCase)
                ? await InvokeHttpAsync(entry, descriptor, arguments, stopwatch, timeout.Token)
                : await InvokeProcessAsync(entry, descriptor, arguments, stopwatch, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(entry, descriptor, stopwatch, $"Capability '{entry.Id}' timed out after {timeoutSeconds} second(s).");
        }
    }

    private static PaperboyCapabilityInvocationResult Failure(
        PaperboyCapabilityEntry entry,
        PaperboyCapabilityDescriptor descriptor,
        Stopwatch stopwatch,
        string message)
    {
        stopwatch.Stop();
        return new PaperboyCapabilityInvocationResult
        {
            CapabilityId = entry.Id,
            ExtensionId = entry.ExtensionId,
            Transport = descriptor.Transport,
            Success = false,
            Output = "",
            Error = message,
            DurationMs = stopwatch.Elapsed.TotalMilliseconds,
            Command = descriptor.Command,
            Url = descriptor.Url
        };
    }

    private static async Task<PaperboyCapabilityInvocationResult> InvokeProcessAsync(
        PaperboyCapabilityEntry entry,
        PaperboyCapabilityDescriptor descriptor,
        JsonObject arguments,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var command = Substitute(descriptor.Command ?? "", arguments, entry.Id);
        var workingDirectory = ResolveWorkingDirectory(entry, descriptor, arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory
        };

        foreach (var argument in descriptor.Args)
        {
            startInfo.ArgumentList.Add(Substitute(argument, arguments, entry.Id));
        }

        using var process = TryStart(startInfo);
        if (process is null)
        {
            return Failure(entry, descriptor, stopwatch, $"Capability '{entry.Id}' could not start '{command}'. Ensure the command is installed and on PATH.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var output = Truncate(await outputTask);
        var error = Truncate(await errorTask);
        stopwatch.Stop();

        return new PaperboyCapabilityInvocationResult
        {
            CapabilityId = entry.Id,
            ExtensionId = entry.ExtensionId,
            Transport = descriptor.Transport,
            Success = process.ExitCode == 0,
            ExitCode = process.ExitCode,
            Output = output,
            Error = error,
            DurationMs = stopwatch.Elapsed.TotalMilliseconds,
            Command = command
        };
    }

    private static async Task<PaperboyCapabilityInvocationResult> InvokeHttpAsync(
        PaperboyCapabilityEntry entry,
        PaperboyCapabilityDescriptor descriptor,
        JsonObject arguments,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var url = Substitute(descriptor.Url ?? "", arguments, entry.Id);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Capability '{entry.Id}' resolved to an invalid URL '{url}'.");
        }

        var method = new HttpMethod(string.IsNullOrWhiteSpace(descriptor.Method) ? "GET" : descriptor.Method.Trim().ToUpperInvariant());
        using var request = new HttpRequestMessage(method, uri);

        foreach (var header in descriptor.Headers)
        {
            request.Headers.TryAddWithoutValidation(Substitute(header.Key, arguments, entry.Id), Substitute(header.Value, arguments, entry.Id));
        }

        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            request.Content = new StringContent(arguments.ToJsonString(BodyOptions), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return Failure(entry, descriptor, stopwatch, $"Capability '{entry.Id}' could not reach {uri}: {ex.Message}");
        }

        using (response)
        {
            var body = Truncate(await response.Content.ReadAsStringAsync(cancellationToken));
            stopwatch.Stop();

            return new PaperboyCapabilityInvocationResult
            {
                CapabilityId = entry.Id,
                ExtensionId = entry.ExtensionId,
                Transport = descriptor.Transport,
                Success = response.IsSuccessStatusCode,
                StatusCode = (int)response.StatusCode,
                Output = body,
                Error = response.IsSuccessStatusCode ? "" : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                Url = uri.ToString()
            };
        }
    }

    private static Process? TryStart(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static string ResolveWorkingDirectory(PaperboyCapabilityEntry entry, PaperboyCapabilityDescriptor descriptor, JsonObject arguments)
    {
        if (string.IsNullOrWhiteSpace(descriptor.WorkingDirectory))
        {
            return string.IsNullOrWhiteSpace(entry.ManifestDirectory) ? AppContext.BaseDirectory : entry.ManifestDirectory;
        }

        var candidate = Substitute(descriptor.WorkingDirectory, arguments, entry.Id);
        var baseDirectory = string.IsNullOrWhiteSpace(entry.ManifestDirectory) ? AppContext.BaseDirectory : entry.ManifestDirectory;
        return Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(baseDirectory, candidate));
    }

    /// <summary>Replaces <c>{{name}}</c> tokens with caller-supplied argument values.</summary>
    public static string Substitute(string template, JsonObject arguments, string capabilityId)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{", StringComparison.Ordinal))
        {
            return template;
        }

        return TokenPattern().Replace(template, match =>
        {
            var name = match.Groups["name"].Value;
            if (!arguments.TryGetPropertyValue(name, out var node) || node is null)
            {
                throw new ArgumentException($"Capability '{capabilityId}' requires argument '{name}'.");
            }

            return node is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : node.ToJsonString(BodyOptions);
        });
    }

    private static string Truncate(string value) =>
        value.Length <= MaxCapturedCharacters ? value : value[..MaxCapturedCharacters] + "\n… output truncated …";

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
        }
    }

    [GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z0-9_.-]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
