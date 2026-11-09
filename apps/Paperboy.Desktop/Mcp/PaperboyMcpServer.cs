using Paperboy.Desktop.Capabilities;
using Paperboy.Desktop.Cards;
using Paperboy.Desktop.Services;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Paperboy.Desktop.Mcp;

public static class PaperboyMcpServer
{
    private const int MaxContentLength = 10 * 1024 * 1024;
    private static readonly TimeSpan FoundryHealthTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] SupportedMcpMethods =
    [
        "initialize",
        "notifications/initialized",
        "tools/list",
        "tools/call",
        "resources/list",
        "resources/read",
        "prompts/list",
        "prompts/get"
    ];

    private static readonly AcpCapabilityResource[] AcpCapabilityResources =
    [
        new(
            "paperboy://acp/capabilities",
            "Paperboy ACP Capability Status",
            "application/json",
            "paperboy-acp-capabilities",
            "urn:yumlog:paperboy:acp-capabilities:v1",
            "application/vnd.yumlog.paperboy.acp-capabilities+json;version=1",
            "Paperboy MCP ACP forwarding capability manifest. Forwarding is stubbed until the APIM bridge contract is connected."),
        new(
            "paperboy://acp/context",
            "Yumlog ACP Context Contract",
            "application/json",
            "acp-context",
            "urn:yumlog:mdkg:layer:acp-context:v1",
            "application/vnd.yumlog.mdkg.acp-context+json;version=1",
            "Adaptive Context Protocol envelopes for agents, apps, runtime schemas, and contextual payloads."),
        new(
            "paperboy://apim/trace",
            "Yumlog APIM Trace Contract",
            "application/json",
            "apim-trace",
            "urn:yumlog:mdkg:layer:apim-trace:v1",
            "application/vnd.yumlog.mdkg.apim-trace+json;version=1",
            "Azure API Management operation, policy, route, and backend trace metadata aligned to semantic graph nodes."),
        new(
            "paperboy://acp/forwarding",
            "Paperboy ACP Forwarding Hook",
            "application/json",
            "paperboy-acp-forwarding-hook",
            "urn:yumlog:paperboy:acp-forwarding-hook:v1",
            "application/vnd.yumlog.paperboy.acp-forwarding-hook+json;version=1",
            "Disabled Paperboy MCP to APIM ACP forwarding hook prepared for APIM service connection.")
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly HttpClient HealthClient = new()
    {
        Timeout = FoundryHealthTimeout
    };

    private static readonly PaperboyCapabilityRegistry Capabilities = PaperboyCapabilityRegistry.Shared;
    private static readonly PaperboyCapabilityInvoker CapabilityInvoker = new();

    public static async Task RunAsync(Stream input, Stream output, string[] args, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        var service = new PaperboyBundleService();
        var foundry = new FoundryLocalPipelineService();
        var pmm = new PaperboyModelManagerService();
        var cards = AgentCardCatalog.Default;

        Log("Paperboy MCP server started.");

        while (!cancellationToken.IsCancellationRequested)
        {
            var request = await ReadMessageAsync(reader, cancellationToken);
            if (request is null)
            {
                break;
            }

            var response = await HandleRequestAsync(request, service, foundry, pmm, cards, cancellationToken);
            if (response is not null)
            {
                await WriteMessageAsync(writer, response, cancellationToken);
            }
        }
    }

    private static async Task<JsonObject?> HandleRequestAsync(JsonObject request, PaperboyBundleService service, FoundryLocalPipelineService foundry, PaperboyModelManagerService pmm, AgentCardCatalog cards, CancellationToken cancellationToken)
    {
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>() ?? "";
        Log($"Handling method {method}.");

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
                        ["version"] = ServerVersion()
                    },
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject
                        {
                            ["listChanged"] = false
                        },
                        ["resources"] = new JsonObject
                        {
                            ["subscribe"] = false,
                            ["listChanged"] = false
                        },
                        ["prompts"] = new JsonObject
                        {
                            ["listChanged"] = false
                        },
                        ["experimental"] = new JsonObject
                        {
                            ["paperboy"] = AcpCapabilityMetadata()
                        }
                    }
                }),
                "notifications/initialized" => null,
                "tools/list" => Response(id, new JsonObject
                {
                    ["tools"] = ToolList()
                }),
                "resources/list" => Response(id, new JsonObject
                {
                    ["resources"] = ResourceList()
                }),
                "resources/read" => HandleResourceRead(id, request["params"]?.AsObject(), cards),
                "prompts/list" => Response(id, new JsonObject
                {
                    ["prompts"] = PromptList()
                }),
                "prompts/get" => HandlePromptGet(id, request["params"]?.AsObject()),
                "tools/call" => await HandleToolCallAsync(id, request["params"]?.AsObject(), service, foundry, pmm, cards, cancellationToken),
                _ => Error(id, -32601, $"Unsupported method: {method}")
            };
        }
        catch (ArgumentException ex)
        {
            return Error(id, -32602, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(id, -32603, ex.Message);
        }
        catch (Exception ex)
        {
            Log($"Unhandled MCP error: {ex}");
            return Error(id, -32000, ex.Message);
        }
    }

    private static async Task<JsonObject> HandleToolCallAsync(JsonNode? id, JsonObject? parameters, PaperboyBundleService service, FoundryLocalPipelineService foundry, PaperboyModelManagerService pmm, AgentCardCatalog cards, CancellationToken cancellationToken)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? "";
        var args = parameters?["arguments"]?.AsObject() ?? new JsonObject();

        return name switch
        {
            "paperboy.bundle.pack" => ContentResponse(id, await PackAsync(args, service, cancellationToken)),
            "paperboy.bundle.inspect" => ContentResponse(id, await InspectAsync(args, service, cancellationToken)),
            "paperboy.bundle.unpack" => ContentResponse(id, await UnpackAsync(args, service, cancellationToken)),
            "paperboy.bundle.toss" => ContentResponse(id, await TossAsync(args, service, cancellationToken)),
            "paperboy.foundryLocal.analyze" => ContentResponse(id, await AnalyzeFoundryAsync(args, foundry, cancellationToken)),
            "paperboy.foundryLocal.modelCards" => ContentResponse(id, cards.FoundryModelCardsJson()),
            "paperboy.pmm.info" => ContentResponse(id, await PmmInfoAsync(args, pmm, cancellationToken)),
            "paperboy.pmm.download" => ContentResponse(id, await PmmDownloadAsync(args, pmm, cancellationToken)),
            "paperboy.pmm.load" => ContentResponse(id, await PmmLoadAsync(args, pmm, cancellationToken)),
            "paperboy.pmm.cacheList" => ContentResponse(id, await PmmCacheListAsync(args, pmm, cancellationToken)),
            "paperboy.pmm.run" => ContentResponse(id, await PmmRunAsync(args, pmm, cancellationToken)),
            "paperboy.cards.list" => ContentResponse(id, CardsJson(args, cards)),
            "paperboy.cards.schema" => ContentResponse(id, cards.SchemaJson),
            "paperboy.capabilities.list" => ContentResponse(id, CapabilityListJson(args)),
            "paperboy.extensions.list" => ContentResponse(id, ExtensionListJson(args)),
            "paperboy.extensions.describe" => ContentResponse(id, ExtensionDescribeJson(args)),
            "paperboy.extensions.invoke" => ContentResponse(id, await ExtensionInvokeAsync(args, cancellationToken)),
            _ => Error(id, -32602, $"Unknown tool: {name}")
        };
    }

    private static string CardsJson(JsonObject args, AgentCardCatalog cards)
    {
        var includeExtensions = args["includeExtensions"]?.GetValue<bool>() ?? false;
        if (!includeExtensions)
        {
            return cards.ToJson();
        }

        return cards.WithAdditionalCards(Capabilities.Index.ExtensionCards).ToJson();
    }

    private static string CapabilityListJson(JsonObject args)
    {
        if (args["refresh"]?.GetValue<bool>() == true)
        {
            Capabilities.Refresh();
        }

        var index = Capabilities.Index;
        var kind = args["kind"]?.GetValue<string>();
        var tag = args["tag"]?.GetValue<string>();
        var includeBuiltIn = args["includeBuiltIn"]?.GetValue<bool>() ?? true;
        var matches = Capabilities.Query(kind, tag, includeBuiltIn);

        return PaperboyCapabilityRegistry.Serialize(new
        {
            index.SchemaVersion,
            index.Schema,
            index.GeneratedAtUtc,
            index.Roots,
            index.Policy,
            Filter = new { kind, tag, includeBuiltIn },
            Count = matches.Count,
            Capabilities = matches,
            index.Diagnostics
        });
    }

    private static string ExtensionListJson(JsonObject args)
    {
        if (args["refresh"]?.GetValue<bool>() == true)
        {
            Capabilities.Refresh();
        }

        return Capabilities.ExtensionsJson();
    }

    private static string ExtensionDescribeJson(JsonObject args)
    {
        var id = Required(args, "id");
        var extension = Capabilities.FindExtension(id)
            ?? throw new ArgumentException($"Unknown extension id: {id}");

        var capabilities = Capabilities.Index.Capabilities
            .Where(capability => string.Equals(capability.ExtensionId, extension.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var cards = Capabilities.Index.ExtensionCards
            .Where(card => extension.CardIds.Contains(card.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        return PaperboyCapabilityRegistry.Serialize(new
        {
            Extension = extension,
            Capabilities = capabilities,
            Cards = cards,
            Capabilities.Index.Policy
        });
    }

    private static async Task<string> ExtensionInvokeAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var capabilityId = Required(args, "capabilityId");
        var entry = Capabilities.FindCapability(capabilityId)
            ?? throw new ArgumentException($"Unknown capability id: {capabilityId}");

        if (string.Equals(entry.Transport, PaperboyCapabilityTransports.BuiltIn, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Capability '{capabilityId}' is a built-in Paperboy tool. Call it directly with tools/call.");
        }

        var invocationArguments = args["arguments"]?.DeepClone().AsObject() ?? new JsonObject();
        var timeoutSeconds = args["timeoutSeconds"]?.GetValue<int>();

        var result = await CapabilityInvoker.InvokeAsync(entry, invocationArguments, timeoutSeconds, cancellationToken);
        return PaperboyCapabilityRegistry.Serialize(result);
    }

    private static async Task<string> PackAsync(JsonObject args, PaperboyBundleService service, CancellationToken cancellationToken)
    {
        var sourcePaths = args["sourcePaths"]?.AsArray().Select(v => ValidatePath(v?.GetValue<string>() ?? "", "sourcePaths")).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray()
            ?? throw new ArgumentException("sourcePaths is required.");
        var outputPath = ValidatePath(Required(args, "outputPath"), "outputPath");
        var compression = ParseCompression(args["compressionLevel"]?.GetValue<string>() ?? "Optimal");
        var includeHidden = args["includeHidden"]?.GetValue<bool>() ?? false;
        var overwrite = args["overwrite"]?.GetValue<bool>() ?? true;
        var tossTo = args["tossTo"]?.GetValue<string>();

        var result = await service.CreateBundleAsync(sourcePaths, outputPath, compression, includeHidden, overwrite, tossTo, cancellationToken: cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static async Task<string> InspectAsync(JsonObject args, PaperboyBundleService service, CancellationToken cancellationToken)
    {
        var bundlePath = ValidatePath(Required(args, "bundlePath"), "bundlePath");
        var manifest = await service.ReadManifestAsync(bundlePath, cancellationToken);
        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private static async Task<string> UnpackAsync(JsonObject args, PaperboyBundleService service, CancellationToken cancellationToken)
    {
        var bundlePath = ValidatePath(Required(args, "bundlePath"), "bundlePath");
        var destination = ValidatePath(Required(args, "destination"), "destination");
        var overwrite = args["overwrite"]?.GetValue<bool>() ?? true;
        var result = await service.ExtractBundleAsync(bundlePath, destination, overwrite, cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static async Task<string> TossAsync(JsonObject args, PaperboyBundleService service, CancellationToken cancellationToken)
    {
        var bundlePath = ValidatePath(Required(args, "bundlePath"), "bundlePath");
        var destination = ValidatePath(Required(args, "destination"), "destination");
        var overwrite = args["overwrite"]?.GetValue<bool>() ?? true;
        var tossedTo = await service.TossBundleAsync(bundlePath, destination, overwrite, cancellationToken);
        return JsonSerializer.Serialize(new { tossedTo }, JsonOptions);
    }

    private static async Task<string> AnalyzeFoundryAsync(JsonObject args, FoundryLocalPipelineService foundry, CancellationToken cancellationToken)
    {
        var sourcePaths = args["sourcePaths"]?.AsArray().Select(v => ValidatePath(v?.GetValue<string>() ?? "", "sourcePaths")).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray()
            ?? throw new ArgumentException("sourcePaths is required.");
        var endpointText = args["endpoint"]?.GetValue<string>() ?? "http://127.0.0.1:52495/v1";
        var model = args["model"]?.GetValue<string>() ?? throw new ArgumentException("model is required.");
        var includeImages = args["includeImageContent"]?.GetValue<bool>() ?? true;
        var sampleVideoFrames = args["sampleVideoFrames"]?.GetValue<bool>() ?? true;
        var maxImageBytes = args["maxImageBytes"]?.GetValue<int>() ?? 2_000_000;
        var outputDirectory = args["outputDirectory"] is null ? null : ValidatePath(args["outputDirectory"]!.GetValue<string>(), "outputDirectory");

        var endpoint = new Uri(endpointText);
        await EnsureFoundryEndpointHealthyAsync(endpoint, cancellationToken);

        var result = await foundry.AnalyzeAsync(
            sourcePaths,
            new FoundryPipelineOptions(endpoint, model, includeImages, sampleVideoFrames, maxImageBytes),
            cancellationToken: cancellationToken);

        string? sidecar = null;
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            sidecar = await foundry.WriteSidecarAsync(result, outputDirectory, cancellationToken);
        }

        return JsonSerializer.Serialize(new { result, sidecar }, JsonOptions);
    }

    private static async Task<string> PmmInfoAsync(JsonObject args, PaperboyModelManagerService pmm, CancellationToken cancellationToken)
    {
        ConfigurePmm(args, pmm);
        var result = await pmm.ModelInfoAsync(Required(args, "model"), cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static async Task<string> PmmDownloadAsync(JsonObject args, PaperboyModelManagerService pmm, CancellationToken cancellationToken)
    {
        ConfigurePmm(args, pmm);
        var result = await pmm.DownloadModelAsync(Required(args, "model"), cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static async Task<string> PmmLoadAsync(JsonObject args, PaperboyModelManagerService pmm, CancellationToken cancellationToken)
    {
        ConfigurePmm(args, pmm);
        var result = await pmm.LoadModelAsync(Required(args, "model"), cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static async Task<string> PmmCacheListAsync(JsonObject args, PaperboyModelManagerService pmm, CancellationToken cancellationToken)
    {
        ConfigurePmm(args, pmm);
        var result = await pmm.CacheListAsync(cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static async Task<string> PmmRunAsync(JsonObject args, PaperboyModelManagerService pmm, CancellationToken cancellationToken)
    {
        ConfigurePmm(args, pmm);
        var result = await pmm.RunPromptAsync(Required(args, "model"), args["prompt"]?.GetValue<string>() ?? "Describe Paperboy model delivery in one sentence.", cancellationToken);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static void ConfigurePmm(JsonObject args, PaperboyModelManagerService pmm)
    {
        pmm.CliPath = args["cliPath"]?.GetValue<string>() ?? "foundry";
        pmm.Device = args["device"]?.GetValue<string>() ?? "Auto";
    }

    private static string Required(JsonObject args, string property)
    {
        return args[property]?.GetValue<string>() ?? throw new ArgumentException($"{property} is required.");
    }

    private static string ValidatePath(string path, string property)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"{property} must not be empty.");
        }

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException($"{property} contains invalid path characters.");
        }

        return Path.GetFullPath(path);
    }

    private static CompressionLevel ParseCompression(string value) => value switch
    {
        "Fastest" => CompressionLevel.Fastest,
        "NoCompression" => CompressionLevel.NoCompression,
        "SmallestSize" => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal
    };

    private static JsonObject Tool(string name, string description, string[] required, JsonObject properties) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["additionalProperties"] = false
        }
    };

    private static JsonArray ToolList() => new()
    {
        Tool(
            "paperboy.bundle.pack",
            "Pack files/folders into a manifest-backed .paperboy.zip bundle. Required: sourcePaths, outputPath. Optional: compressionLevel, includeHidden, overwrite, tossTo.",
            ["sourcePaths", "outputPath"],
            new JsonObject
            {
                ["sourcePaths"] = ArrayOf(StringSchema("Files or folders to include.")),
                ["outputPath"] = StringSchema("Destination .paperboy.zip path."),
                ["compressionLevel"] = EnumSchema(["Fastest", "Optimal", "SmallestSize", "NoCompression"]),
                ["includeHidden"] = BooleanSchema("Include hidden files and directories."),
                ["overwrite"] = BooleanSchema("Overwrite an existing output bundle."),
                ["tossTo"] = StringSchema("Optional destination folder to copy the bundle after packing.")
            }),
        Tool("paperboy.bundle.inspect", "Read a Paperboy bundle manifest. Required: bundlePath.", ["bundlePath"], new JsonObject
        {
            ["bundlePath"] = StringSchema("Path to a .paperboy.zip bundle.")
        }),
        Tool("paperboy.bundle.unpack", "Expand a Paperboy bundle. Required: bundlePath, destination. Optional: overwrite.", ["bundlePath", "destination"], new JsonObject
        {
            ["bundlePath"] = StringSchema("Path to a .paperboy.zip bundle."),
            ["destination"] = StringSchema("Destination directory."),
            ["overwrite"] = BooleanSchema("Overwrite files in the destination.")
        }),
        Tool("paperboy.bundle.toss", "Copy a Paperboy bundle to a destination. Required: bundlePath, destination. Optional: overwrite.", ["bundlePath", "destination"], new JsonObject
        {
            ["bundlePath"] = StringSchema("Path to a .paperboy.zip bundle."),
            ["destination"] = StringSchema("Destination directory."),
            ["overwrite"] = BooleanSchema("Overwrite existing bundle at destination.")
        }),
        Tool("paperboy.foundryLocal.analyze", "Analyze media/documents with a local Foundry endpoint and return tags/classifications. Required: sourcePaths, model. Optional: endpoint, includeImageContent, sampleVideoFrames, maxImageBytes, outputDirectory.", ["sourcePaths", "model"], new JsonObject
        {
            ["sourcePaths"] = ArrayOf(StringSchema("Files or folders to analyze.")),
            ["model"] = StringSchema("Foundry Local model name."),
            ["endpoint"] = StringSchema("Foundry Local OpenAI-compatible endpoint. Defaults to http://127.0.0.1:52495/v1."),
            ["includeImageContent"] = BooleanSchema("Send image bytes for supported image files."),
            ["sampleVideoFrames"] = BooleanSchema("Sample a video frame with FFmpeg for video files."),
            ["maxImageBytes"] = IntegerSchema("Maximum image byte size sent inline."),
            ["outputDirectory"] = StringSchema("Optional directory for the generated metadata sidecar.")
        }),
        Tool("paperboy.foundryLocal.modelCards", "Return Foundry Local model delivery cards for Paperboy bundles.", [], new JsonObject()),
        Tool("paperboy.pmm.info", "Read Foundry Local model information via Paperboy Model Manager. Required: model. Optional: cliPath, device.", ["model"], PmmProperties()),
        Tool("paperboy.pmm.download", "Download a Foundry Local model to the local cache. Required: model. Optional: cliPath, device.", ["model"], PmmProperties()),
        Tool("paperboy.pmm.load", "Load a Foundry Local model into the local service. Required: model. Optional: cliPath, device.", ["model"], PmmProperties()),
        Tool("paperboy.pmm.cacheList", "List Foundry Local cached models. Optional: cliPath, device.", [], new JsonObject
        {
            ["cliPath"] = StringSchema("Foundry CLI path."),
            ["device"] = StringSchema("Target device.")
        }),
        Tool("paperboy.pmm.run", "Run a short prompt against a Foundry Local model. Required: model. Optional: prompt, cliPath, device.", ["model"], new JsonObject
        {
            ["model"] = StringSchema("Foundry Local model name."),
            ["prompt"] = StringSchema("Prompt text."),
            ["cliPath"] = StringSchema("Foundry CLI path."),
            ["device"] = StringSchema("Target device.")
        }),
        Tool("paperboy.cards.list", "Return composable Paperboy agent cards for A2A/livetile UIs. Optional: includeExtensions.", [], new JsonObject
        {
            ["includeExtensions"] = BooleanSchema("Merge agent cards contributed by discovered Paperboy extensions.")
        }),
        Tool("paperboy.cards.schema", "Return the Paperboy agent-card JSON schema.", [], new JsonObject()),
        Tool("paperboy.capabilities.list", "Enumerate Paperboy's capability index: built-in MCP tools plus capabilities contributed by discovered extensions, connectors, skills, and workflows. Optional: kind, tag, includeBuiltIn, refresh.", [], new JsonObject
        {
            ["kind"] = EnumSchema(PaperboyCapabilityKinds.Known),
            ["tag"] = StringSchema("Filter capabilities that declare this tag."),
            ["includeBuiltIn"] = BooleanSchema("Include Paperboy's own MCP tools. Defaults to true."),
            ["refresh"] = BooleanSchema("Re-scan the extension roots before answering.")
        }),
        Tool("paperboy.extensions.list", "List discovered Paperboy extension packages, discovery roots, invocation policy, and manifest diagnostics. Optional: refresh.", [], new JsonObject
        {
            ["refresh"] = BooleanSchema("Re-scan the extension roots before answering.")
        }),
        Tool("paperboy.extensions.describe", "Describe one discovered extension package with its capabilities and contributed agent cards. Required: id.", ["id"], new JsonObject
        {
            ["id"] = StringSchema("Extension manifest id.")
        }),
        Tool("paperboy.extensions.invoke", "Invoke an extension capability that declares a process or http transport. Required: capabilityId. Optional: arguments, timeoutSeconds.", ["capabilityId"], new JsonObject
        {
            ["capabilityId"] = StringSchema("Capability id from paperboy.capabilities.list."),
            ["arguments"] = ObjectSchema("Values substituted into {{token}} placeholders and sent as the HTTP JSON body."),
            ["timeoutSeconds"] = IntegerSchema("Override the capability timeout, 1-900 seconds.")
        })
    };

    /// <summary>Paperboy's own MCP tools, projected for the capability registry.</summary>
    public static IReadOnlyList<PaperboyBuiltInTool> BuiltInToolDescriptors() =>
        ToolList()
            .OfType<JsonObject>()
            .Select(tool => new PaperboyBuiltInTool(
                tool["name"]?.GetValue<string>() ?? "",
                tool["description"]?.GetValue<string>() ?? ""))
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .ToArray();

    private static JsonObject PmmProperties() => new()
    {
        ["model"] = StringSchema("Foundry Local model name."),
        ["cliPath"] = StringSchema("Foundry CLI path."),
        ["device"] = StringSchema("Target device.")
    };

    private static JsonObject StringSchema(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description
    };

    private static JsonObject BooleanSchema(string description) => new()
    {
        ["type"] = "boolean",
        ["description"] = description
    };

    private static JsonObject IntegerSchema(string description) => new()
    {
        ["type"] = "integer",
        ["description"] = description,
        ["minimum"] = 1
    };

    private static JsonObject EnumSchema(string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
    };

    private static JsonObject ArrayOf(JsonObject itemSchema) => new()
    {
        ["type"] = "array",
        ["items"] = itemSchema,
        ["minItems"] = 1
    };

    private static JsonObject ObjectSchema(string description) => new()
    {
        ["type"] = "object",
        ["description"] = description,
        ["additionalProperties"] = true
    };

    private static JsonArray ResourceList()
    {
        var resources = new JsonArray
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
            },
            new JsonObject
            {
                ["uri"] = "paperboy://capabilities",
                ["name"] = "Paperboy Capability Index",
                ["mimeType"] = "application/json",
                ["description"] = "Built-in MCP tools plus capabilities contributed by discovered extensions, connectors, skills, and workflows."
            },
            new JsonObject
            {
                ["uri"] = "paperboy://extensions",
                ["name"] = "Paperboy Extension Packages",
                ["mimeType"] = "application/json",
                ["description"] = "Discovered Paperboy extension manifests, discovery roots, invocation policy, and diagnostics."
            },
            new JsonObject
            {
                ["uri"] = "paperboy://extensions/schema",
                ["name"] = "Paperboy Extension Manifest Schema",
                ["mimeType"] = "application/schema+json",
                ["description"] = "JSON schema for *.extension.json Paperboy capability manifests."
            }
        };

        foreach (var capability in AcpCapabilityResources)
        {
            resources.Add(new JsonObject
            {
                ["uri"] = capability.Uri,
                ["name"] = capability.Name,
                ["mimeType"] = capability.MimeType,
                ["description"] = capability.Description
            });
        }

        return resources;
    }

    private static JsonObject HandleResourceRead(JsonNode? id, JsonObject? parameters, AgentCardCatalog cards)
    {
        var uri = parameters?["uri"]?.GetValue<string>() ?? throw new ArgumentException("uri is required.");
        var capability = AcpCapabilityResources.FirstOrDefault(capability => string.Equals(capability.Uri, uri, StringComparison.Ordinal));
        var (mimeType, text) = uri switch
        {
            "paperboy://cards" => ("application/json", cards.ToJson()),
            "paperboy://cards/schema" => ("application/schema+json", cards.SchemaJson),
            "paperboy://capabilities" => ("application/json", Capabilities.IndexJson()),
            "paperboy://extensions" => ("application/json", Capabilities.ExtensionsJson()),
            "paperboy://extensions/schema" => ("application/schema+json", PaperboyExtensionSchema.Json),
            _ when capability is not null => (capability.MimeType, AcpCapabilityJson(capability)),
            _ => throw new ArgumentException($"Unsupported resource URI: {uri}")
        };

        return Response(id, new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = uri,
                    ["mimeType"] = mimeType,
                    ["text"] = text
                }
            }
        });
    }

    private static string AcpCapabilityJson(AcpCapabilityResource capability)
    {
        var isManifest = string.Equals(capability.Uri, "paperboy://acp/capabilities", StringComparison.Ordinal);
        var isForwardingHook = string.Equals(capability.Uri, "paperboy://acp/forwarding", StringComparison.Ordinal);
        var payload = new
        {
            status = "not-yet-connected",
            connected = false,
            capability = new
            {
                capability.Id,
                capability.Name,
                resourceUri = capability.Uri,
                capability.SchemaUri,
                capability.Codec,
                capability.Description
            },
            forwarding = new
            {
                enabled = false,
                mode = "disabled",
                toolCallHook = isForwardingHook,
                reason = "APIM ACP forwarding is intentionally not active until the APIM service provides endpoint, auth, and payload contract bindings."
            },
            contracts = isManifest
                ? AcpCapabilityResources
                    .Where(resource => !string.Equals(resource.Uri, "paperboy://acp/capabilities", StringComparison.Ordinal))
                    .Select(resource => new
                    {
                        resource.Id,
                        resourceUri = resource.Uri,
                        resource.SchemaUri,
                        resource.Codec,
                        resource.Description,
                        status = "not-yet-connected"
                    })
                : null,
            handoff = new
            {
                apimServiceAgent = "Connect APIM route/backend URL, authentication policy, and ACP envelope mapping before enabling forwarding.",
                verificationAgent = "Keep MCP initialize, tools/list, tools/call, resources/list, resources/read, prompts/list, and prompts/get smoke coverage green."
            },
            mcp = AcpCapabilityMetadata()
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static JsonObject AcpCapabilityMetadata() => new()
    {
        ["server"] = "paperboy",
        ["protocolVersion"] = "2024-11-05",
        ["status"] = "not-yet-connected",
        ["supportedMethods"] = new JsonArray(SupportedMcpMethods.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["resourceUris"] = new JsonArray(ResourceUris().Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["promptNames"] = new JsonArray(PromptNames().Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["acpResources"] = new JsonArray(AcpCapabilityResources.Select(resource => (JsonNode?)new JsonObject
        {
            ["uri"] = resource.Uri,
            ["id"] = resource.Id,
            ["schemaUri"] = resource.SchemaUri,
            ["codec"] = resource.Codec,
            ["description"] = resource.Description
        }).ToArray()),
        ["extensions"] = ExtensionCapabilityMetadata()
    };

    private static JsonObject ExtensionCapabilityMetadata()
    {
        var metadata = new JsonObject
        {
            ["schemaVersion"] = PaperboyExtensionManifest.CurrentSchemaVersion,
            ["schemaUri"] = PaperboyExtensionManifest.SchemaUri,
            ["manifestPattern"] = PaperboyCapabilityRegistry.ManifestSearchPattern,
            ["rootsVariable"] = PaperboyCapabilityEnvironment.RootsVariable,
            ["allowInvokeVariable"] = PaperboyCapabilityEnvironment.AllowInvokeVariable
        };

        try
        {
            var index = Capabilities.Index;
            metadata["roots"] = new JsonArray(index.Roots.Select(root => (JsonNode?)JsonValue.Create(root)).ToArray());
            metadata["discovered"] = index.Extensions.Length;
            metadata["capabilityCount"] = index.Capabilities.Length;
            metadata["externalInvocationAllowed"] = index.Policy.AllowExternalInvocation;
            metadata["diagnosticCount"] = index.Diagnostics.Length;
        }
        catch (Exception ex)
        {
            Log($"Extension discovery failed: {ex}");
            metadata["status"] = "discovery-failed";
            metadata["error"] = ex.Message;
        }

        return metadata;
    }

    public static IReadOnlyList<string> ResourceUris() =>
    [
        "paperboy://cards",
        "paperboy://cards/schema",
        "paperboy://capabilities",
        "paperboy://extensions",
        "paperboy://extensions/schema",
        .. AcpCapabilityResources.Select(resource => resource.Uri)
    ];

    public static IReadOnlyList<string> PromptNames() =>
    [
        "paperboy.bundle.analyze",
        "paperboy.foundry.strategy",
        "paperboy.livetile.plan"
    ];

    private static JsonArray PromptList() => new()
    {
        Prompt("paperboy.bundle.analyze", "Analyze Paperboy bundle contents and suggest metadata tagging.", new JsonArray
        {
            PromptArgument("bundlePath", "Path to the Paperboy bundle.", true)
        }),
        Prompt("paperboy.foundry.strategy", "Suggest Foundry Local models for analyzing a bundle's media types.", new JsonArray
        {
            PromptArgument("mediaKinds", "Comma-separated media kinds such as image, video, audio, document.", false)
        }),
        Prompt("paperboy.livetile.plan", "Plan adaptive LiveTile metadata, actions, and refresh cadence for a payload.", new JsonArray
        {
            PromptArgument("payloadDescription", "Payload or workflow description.", true)
        })
    };

    private static JsonObject Prompt(string name, string description, JsonArray arguments) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["arguments"] = arguments
    };

    private static JsonObject PromptArgument(string name, string description, bool required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["required"] = required
    };

    private static JsonObject HandlePromptGet(JsonNode? id, JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? throw new ArgumentException("name is required.");
        var args = parameters?["arguments"]?.AsObject() ?? new JsonObject();

        var messages = name switch
        {
            "paperboy.bundle.analyze" => new JsonArray
            {
                PromptMessage($"Analyze the Paperboy bundle at {args["bundlePath"]?.GetValue<string>() ?? "<bundlePath>"} and propose tags, classifications, summary fields, and follow-up actions.")
            },
            "paperboy.foundry.strategy" => new JsonArray
            {
                PromptMessage($"Recommend Foundry Local models and routing strategy for media kinds: {args["mediaKinds"]?.GetValue<string>() ?? "image, video, audio, document"}.")
            },
            "paperboy.livetile.plan" => new JsonArray
            {
                PromptMessage($"Design an adaptive LiveTile card schema, refresh cadence, APIM route, and agent actions for: {args["payloadDescription"]?.GetValue<string>() ?? "<payloadDescription>"}.")
            },
            _ => throw new ArgumentException($"Unsupported prompt: {name}")
        };

        return Response(id, new JsonObject
        {
            ["description"] = PromptList().OfType<JsonObject>().First(p => p["name"]?.GetValue<string>() == name)["description"]?.DeepClone(),
            ["messages"] = messages
        });
    }

    private static JsonObject PromptMessage(string text) => new()
    {
        ["role"] = "user",
        ["content"] = new JsonObject
        {
            ["type"] = "text",
            ["text"] = text
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

            if (read != length)
            {
                throw new InvalidOperationException($"Expected {length} message bytes but read {read}.");
            }

            return JsonNode.Parse(new string(buffer, 0, read))?.AsObject();
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        throw new InvalidOperationException("MCP stdio messages must include a Content-Length header.");
    }

    private static async Task EnsureFoundryEndpointHealthyAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var healthUri = new Uri(endpoint, endpoint.AbsolutePath.TrimEnd('/') + "/models");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUri);
            using var response = await HealthClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Foundry endpoint {endpoint} returned HTTP {(int)response.StatusCode} from {healthUri}.");
            }
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Foundry endpoint {endpoint} did not respond within {FoundryHealthTimeout.TotalSeconds:N0} seconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Foundry endpoint unreachable at {endpoint}. Ensure Foundry Local is running.", ex);
        }
    }

    private static string ServerVersion()
    {
        var version = typeof(PaperboyMcpServer).Assembly.GetName().Version;
        return version is null ? "0.4.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static void Log(string message)
    {
        if (Environment.GetEnvironmentVariable("PAPERBOY_DEBUG") == "1")
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:O}] {message}");
        }
    }

    private static async Task WriteMessageAsync(StreamWriter writer, JsonObject response, CancellationToken cancellationToken)
    {
        var json = response.ToJsonString(JsonOptions);
        var byteLength = Encoding.UTF8.GetByteCount(json);
        await writer.WriteAsync($"Content-Length: {byteLength}\r\n\r\n");
        await writer.WriteAsync(json.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private sealed record AcpCapabilityResource(
        string Uri,
        string Name,
        string MimeType,
        string Id,
        string SchemaUri,
        string Codec,
        string Description);
}
