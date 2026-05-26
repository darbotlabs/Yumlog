using Paperboy.Desktop.Cards;
using Paperboy.Desktop.Services;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Paperboy.Desktop.Mcp;

public static class PaperboyMcpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static async Task RunAsync(Stream input, Stream output, string[] args, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        var service = new PaperboyBundleService();
        var cards = AgentCardCatalog.Default;

        while (!cancellationToken.IsCancellationRequested)
        {
            var request = await ReadMessageAsync(reader, cancellationToken);
            if (request is null)
            {
                break;
            }

            var response = await HandleRequestAsync(request, service, cards, cancellationToken);
            if (response is not null)
            {
                await WriteMessageAsync(writer, response, cancellationToken);
            }
        }
    }

    private static async Task<JsonObject?> HandleRequestAsync(JsonObject request, PaperboyBundleService service, AgentCardCatalog cards, CancellationToken cancellationToken)
    {
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>() ?? "";

        try
        {
            return method switch
            {
                "initialize" => Response(id, new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "paperboy",
                        ["version"] = "0.2.0"
                    },
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject(),
                        ["resources"] = new JsonObject()
                    }
                }),
                "notifications/initialized" => null,
                "tools/list" => Response(id, new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        Tool("paperboy.bundle.pack", "Pack files/folders into a manifest-backed .paperboy.zip bundle."),
                        Tool("paperboy.bundle.inspect", "Read a Paperboy bundle manifest."),
                        Tool("paperboy.bundle.unpack", "Expand a Paperboy bundle."),
                        Tool("paperboy.bundle.toss", "Copy a Paperboy bundle to a destination."),
                        Tool("paperboy.cards.list", "Return composable Paperboy agent cards for A2A/livetile UIs."),
                        Tool("paperboy.cards.schema", "Return the Paperboy agent-card JSON schema.")
                    }
                }),
                "resources/list" => Response(id, new JsonObject
                {
                    ["resources"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["uri"] = "paperboy://cards",
                            ["name"] = "Paperboy Agent Cards",
                            ["mimeType"] = "application/json"
                        },
                        new JsonObject
                        {
                            ["uri"] = "paperboy://cards/schema",
                            ["name"] = "Paperboy Agent Card Schema",
                            ["mimeType"] = "application/schema+json"
                        }
                    }
                }),
                "tools/call" => await HandleToolCallAsync(id, request["params"]?.AsObject(), service, cards, cancellationToken),
                _ => Error(id, -32601, $"Unsupported method: {method}")
            };
        }
        catch (Exception ex)
        {
            return Error(id, -32000, ex.Message);
        }
    }

    private static async Task<JsonObject> HandleToolCallAsync(JsonNode? id, JsonObject? parameters, PaperboyBundleService service, AgentCardCatalog cards, CancellationToken cancellationToken)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? "";
        var args = parameters?["arguments"]?.AsObject() ?? new JsonObject();

        return name switch
        {
            "paperboy.bundle.pack" => ContentResponse(id, await PackAsync(args, service, cancellationToken)),
            "paperboy.bundle.inspect" => ContentResponse(id, await InspectAsync(args, service, cancellationToken)),
            "paperboy.bundle.unpack" => ContentResponse(id, await UnpackAsync(args, service, cancellationToken)),
            "paperboy.bundle.toss" => ContentResponse(id, await TossAsync(args, service, cancellationToken)),
            "paperboy.cards.list" => ContentResponse(id, cards.ToJson()),
            "paperboy.cards.schema" => ContentResponse(id, cards.SchemaJson),
            _ => Error(id, -32602, $"Unknown tool: {name}")
        };
    }

    private static async Task<string> PackAsync(JsonObject args, PaperboyBundleService service, CancellationToken cancellationToken)
    {
        var sourcePaths = args["sourcePaths"]?.AsArray().Select(v => v?.GetValue<string>() ?? "").Where(v => !string.IsNullOrWhiteSpace(v)).ToArray()
            ?? throw new ArgumentException("sourcePaths is required.");
        var outputPath = args["outputPath"]?.GetValue<string>() ?? throw new ArgumentException("outputPath is required.");
        var compression = ParseCompression(args["compressionLevel"]?.GetValue<string>() ?? "Optimal");
        var includeHidden = args["includeHidden"]?.GetValue<bool>() ?? false;
        var overwrite = args["overwrite"]?.GetValue<bool>() ?? true;
        var tossTo = args["tossTo"]?.GetValue<string>();

        var result = await service.CreateBundleAsync(sourcePaths, outputPath, compression, includeHidden, overwrite, tossTo, cancellationToken: cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static async Task<string> InspectAsync(JsonObject args, PaperboyBundleService service, CancellationToken cancellationToken)
    {
        var bundlePath = args["bundlePath"]?.GetValue<string>() ?? throw new ArgumentException("bundlePath is required.");
        var manifest = await service.ReadManifestAsync(bundlePath, cancellationToken);
        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private static async Task<string> UnpackAsync(JsonObject args, PaperboyBundleService service, CancellationToken cancellationToken)
    {
        var bundlePath = args["bundlePath"]?.GetValue<string>() ?? throw new ArgumentException("bundlePath is required.");
        var destination = args["destination"]?.GetValue<string>() ?? throw new ArgumentException("destination is required.");
        var overwrite = args["overwrite"]?.GetValue<bool>() ?? true;
        var result = await service.ExtractBundleAsync(bundlePath, destination, overwrite, cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static async Task<string> TossAsync(JsonObject args, PaperboyBundleService service, CancellationToken cancellationToken)
    {
        var bundlePath = args["bundlePath"]?.GetValue<string>() ?? throw new ArgumentException("bundlePath is required.");
        var destination = args["destination"]?.GetValue<string>() ?? throw new ArgumentException("destination is required.");
        var overwrite = args["overwrite"]?.GetValue<bool>() ?? true;
        var tossedTo = await service.TossBundleAsync(bundlePath, destination, overwrite, cancellationToken);
        return JsonSerializer.Serialize(new { tossedTo }, JsonOptions);
    }

    private static CompressionLevel ParseCompression(string value) => value switch
    {
        "Fastest" => CompressionLevel.Fastest,
        "NoCompression" => CompressionLevel.NoCompression,
        "SmallestSize" => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal
    };

    private static JsonObject Tool(string name, string description) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = true
        }
    };

    private static JsonObject ContentResponse(JsonNode? id, string text) => Response(id, new JsonObject
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = text
            }
        }
    });

    private static JsonObject Response(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };

    private static async Task<JsonObject?> ReadMessageAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken);
        if (line is null)
        {
            return null;
        }

        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
        {
            var length = int.Parse(line["Content-Length:".Length..].Trim());
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
            {
            }

            var buffer = new char[length];
            var read = 0;
            while (read < length)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken);
                if (count == 0)
                {
                    break;
                }
                read += count;
            }

            return JsonNode.Parse(new string(buffer, 0, read))?.AsObject();
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        return JsonNode.Parse(line)?.AsObject();
    }

    private static async Task WriteMessageAsync(StreamWriter writer, JsonObject response, CancellationToken cancellationToken)
    {
        var json = response.ToJsonString(JsonOptions);
        var byteLength = Encoding.UTF8.GetByteCount(json);
        await writer.WriteAsync($"Content-Length: {byteLength}\r\n\r\n");
        await writer.WriteAsync(json.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }
}
