using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Paperboy.Desktop.Services;

public sealed record FoundryPipelineOptions(
    Uri Endpoint,
    string Model,
    bool IncludeImageContent,
    bool SampleVideoFrames,
    int MaxImageBytes);

public sealed record FoundryPipelineResult(
    DateTimeOffset CreatedAtUtc,
    Uri Endpoint,
    string Model,
    IReadOnlyList<FoundryFileMetadata> Files);

public sealed record FoundryFileMetadata(
    string Path,
    string MediaKind,
    long Length,
    DateTime LastWriteTimeUtc,
    string Sha256,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Classifications,
    string Summary,
    string RawModelResponse,
    bool UsedVisualContent);

public sealed class FoundryLocalPipelineService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(4)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<FoundryPipelineResult> AnalyzeAsync(
        IEnumerable<string> sourcePaths,
        FoundryPipelineOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = EnumerateSupportedFiles(sourcePaths).ToArray();
        var results = new List<FoundryFileMetadata>(files.Length);

        for (var i = 0; i < files.Length; i++)
        {
            var file = files[i];
            progress?.Report($"Analyzing {file.Name} ({i + 1}/{files.Length})");
            results.Add(await AnalyzeFileAsync(file, options, cancellationToken));
        }

        return new FoundryPipelineResult(DateTimeOffset.UtcNow, options.Endpoint, options.Model, results);
    }

    public async Task<string> WriteSidecarAsync(FoundryPipelineResult result, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        var path = Path.Combine(destinationDirectory, $"paperboy-foundry-metadata-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken);
        return path;
    }

    public static string ToJson(FoundryPipelineResult result) => JsonSerializer.Serialize(result, JsonOptions);

    private async Task<FoundryFileMetadata> AnalyzeFileAsync(FileInfo file, FoundryPipelineOptions options, CancellationToken cancellationToken)
    {
        var mediaKind = GetMediaKind(file.Extension);
        var sha256 = await HashAsync(file.FullName, cancellationToken);
        var visual = await TryGetVisualContentAsync(file, mediaKind, options, cancellationToken);
        var prompt = BuildPrompt(file, mediaKind, sha256, visual is not null);
        var raw = await CallResponsesAsync(options.Endpoint, options.Model, prompt, visual, cancellationToken);
        var parsed = ParseModelResponse(raw);

        return new FoundryFileMetadata(
            file.FullName,
            mediaKind,
            file.Length,
            file.LastWriteTimeUtc,
            sha256,
            parsed.Tags,
            parsed.Classifications,
            parsed.Summary,
            raw,
            visual is not null);
    }

    private static IEnumerable<FileInfo> EnumerateSupportedFiles(IEnumerable<string> sourcePaths)
    {
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
            ".mp4", ".mov", ".mkv", ".avi", ".webm",
            ".mp3", ".wav", ".m4a", ".flac", ".aac",
            ".txt", ".md", ".json", ".csv", ".log"
        };

        foreach (var sourcePath in sourcePaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(sourcePath))
            {
                var file = new FileInfo(sourcePath);
                if (supported.Contains(file.Extension))
                {
                    yield return file;
                }
            }
            else if (Directory.Exists(sourcePath))
            {
                foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories).Select(p => new FileInfo(p)))
                {
                    if (supported.Contains(file.Extension))
                    {
                        yield return file;
                    }
                }
            }
        }
    }

    private static string GetMediaKind(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => "image",
        ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" => "video",
        ".mp3" or ".wav" or ".m4a" or ".flac" or ".aac" => "audio",
        _ => "document"
    };

    private static string BuildPrompt(FileInfo file, string mediaKind, string sha256, bool includesVisual)
    {
        return $$"""
        You are Paperboy's offline Foundry Local metadata tagger.
        Return compact JSON only with this shape:
        {
          "tags": ["short-tag"],
          "classifications": ["category"],
          "summary": "one sentence"
        }

        File:
        - name: {{file.Name}}
        - extension: {{file.Extension}}
        - mediaKind: {{mediaKind}}
        - bytes: {{file.Length}}
        - lastWriteTimeUtc: {{file.LastWriteTimeUtc:o}}
        - sha256: {{sha256}}
        - visualContentIncluded: {{includesVisual}}

        Classify the content for offline file routing, search, and bundle metadata.
        """;
    }

    private static async Task<VisualContent?> TryGetVisualContentAsync(FileInfo file, string mediaKind, FoundryPipelineOptions options, CancellationToken cancellationToken)
    {
        if (!options.IncludeImageContent)
        {
            return null;
        }

        if (mediaKind == "image" && file.Length <= options.MaxImageBytes)
        {
            var bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
            return new VisualContent(Convert.ToBase64String(bytes), GetImageMime(file.Extension));
        }

        if (mediaKind == "video" && options.SampleVideoFrames)
        {
            var ffmpeg = ResolveFfmpeg();
            if (ffmpeg is null)
            {
                return null;
            }

            var temp = Path.Combine(Path.GetTempPath(), $"paperboy-frame-{Guid.NewGuid():N}.jpg");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-ss");
                psi.ArgumentList.Add("00:00:01");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(file.FullName);
                psi.ArgumentList.Add("-frames:v");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add("scale='min(512,iw)':-2");
                psi.ArgumentList.Add(temp);

                using var process = Process.Start(psi);
                if (process is null)
                {
                    return null;
                }
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0 || !File.Exists(temp))
                {
                    return null;
                }
                var bytes = await File.ReadAllBytesAsync(temp, cancellationToken);
                return new VisualContent(Convert.ToBase64String(bytes), "image/jpeg");
            }
            finally
            {
                File.Delete(temp);
            }
        }

        return null;
    }

    private static async Task<string> CallResponsesAsync(Uri endpoint, string model, string prompt, VisualContent? visual, CancellationToken cancellationToken)
    {
        var baseUri = endpoint.ToString().TrimEnd('/');
        var url = baseUri.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{baseUri}/responses"
            : $"{baseUri}/v1/responses";

        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = prompt
            }
        };

        if (visual is not null)
        {
            content.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["image_data"] = visual.Base64,
                ["media_type"] = visual.MediaType
            });
        }

        var request = new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = content
                }
            }
        };

        using var response = await HttpClient.PostAsJsonAsync(url, request, JsonOptions, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Foundry Local request failed ({(int)response.StatusCode}): {text}");
        }

        return ExtractResponseText(text);
    }

    private static string ExtractResponseText(string json)
    {
        var node = JsonNode.Parse(json);
        var outputText = node?["output_text"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(outputText))
        {
            return outputText;
        }

        var texts = new List<string>();
        Walk(node, texts);
        return texts.Count > 0 ? string.Join("\n", texts.Distinct()) : json;

        static void Walk(JsonNode? node, List<string> texts)
        {
            if (node is JsonObject obj)
            {
                if (obj.TryGetPropertyValue("text", out var textNode) && textNode is JsonValue textValue && textValue.TryGetValue<string>(out var value))
                {
                    texts.Add(value);
                }
                foreach (var child in obj.Select(kvp => kvp.Value))
                {
                    Walk(child, texts);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array)
                {
                    Walk(child, texts);
                }
            }
        }
    }

    private static ParsedMetadata ParseModelResponse(string raw)
    {
        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            var json = start >= 0 && end > start ? raw[start..(end + 1)] : raw;
            var node = JsonNode.Parse(json)?.AsObject();
            var tags = ReadStringArray(node, "tags");
            var classifications = ReadStringArray(node, "classifications");
            var summary = node?["summary"]?.GetValue<string>() ?? raw.Trim();
            return new ParsedMetadata(tags, classifications, summary);
        }
        catch
        {
            return new ParsedMetadata([], [], raw.Trim());
        }
    }

    private static string[] ReadStringArray(JsonObject? node, string property)
    {
        return node?[property]?.AsArray()
            .Select(v => v?.GetValue<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToArray() ?? [];
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetImageMime(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    private static string? ResolveFfmpeg()
    {
        var env = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var local = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".tools", "ffmpeg", "bin", "ffmpeg.exe"));
        if (File.Exists(local))
        {
            return local;
        }

        return "ffmpeg";
    }

    private sealed record VisualContent(string Base64, string MediaType);
    private sealed record ParsedMetadata(IReadOnlyList<string> Tags, IReadOnlyList<string> Classifications, string Summary);
}
