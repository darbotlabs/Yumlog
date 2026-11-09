using System.Text.Json;

namespace Paperboy.Desktop.Cards;

public sealed record AgentCardDefinition(
    string Id,
    string Title,
    string Role,
    string Description,
    string[] Inputs,
    string[] Outputs,
    string[] Actions,
    string LiveTileState);

public sealed record FoundryLocalModelCard(
    string Alias,
    string Title,
    string Description,
    string[] Capabilities,
    string DeliveryGuidance,
    string Task,
    string Device,
    string FileSize,
    string License,
    string ModelId,
    string[] SemanticLinks);

public sealed class AgentCardCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public IReadOnlyList<AgentCardDefinition> Cards { get; }

    private AgentCardCatalog(IReadOnlyList<AgentCardDefinition> cards)
    {
        Cards = cards;
    }

    public static AgentCardCatalog Default { get; } = new(
    [
        new(
            "paperboy.payload",
            "Payload",
            "source-picker",
            "Collects files and folders for a tossable bundle.",
            ["fileDrop", "filePicker", "folderPicker"],
            ["sourcePaths[]"],
            ["addFiles", "addFolder", "removeSelected", "clear"],
            "empty|ready|changed"),
        new(
            "paperboy.bundle",
            "Bundle",
            "bundle-builder",
            "Configures compression, destination, and bundle creation.",
            ["sourcePaths[]", "outputPath", "compressionLevel", "includeHidden", "overwrite"],
            ["bundlePath", "manifest", "compressionRatio"],
            ["pack", "inspect", "unpack", "toss"],
            "idle|packing|packed|error"),
        new(
            "paperboy.mcp",
            "MCP Bridge",
            "tool-surface",
            "Exposes Paperboy bundle, resources, prompts, and ACP capability metadata as agent-callable MCP surfaces.",
            ["jsonRpc", "toolName", "arguments"],
            ["toolResult", "manifest", "agentCardCatalog", "resources", "prompts", "acpCapabilityMetadata"],
            ["initialize", "tools/list", "tools/call", "resources/list", "resources/read", "prompts/list", "prompts/get"],
            "offline|stdio-ready|serving"),
        new(
            "paperboy.foundryLocal",
            "Foundry Local",
            "offline-metadata-pipeline",
            "Classifies and tags image, video, audio, and document payloads with an offline Foundry Local model endpoint.",
            ["endpoint", "model", "sourcePaths[]", "includeImageContent", "sampleVideoFrames"],
            ["metadataJson", "tags", "classifications", "summaries"],
            ["analyze", "writeSidecar", "bundleMetadata"],
            "offline|ready|analyzing|tagged|error"),
        new(
            "paperboy.modelCards",
            "Foundry Local Model Cards",
            "model-delivery-layer",
            "Displays language-model delivery cards that Paperboy can bundle and toss for offline Foundry Local workflows.",
            ["modelAlias", "modelRole", "endpoint", "deliveryProfile", "device"],
            ["modelCardJson", "recommendedBundle", "mcpToolHints", "cacheState"],
            ["showModels", "downloadModel", "loadModel", "modelInfo", "exportModelCards", "bundleModelCards", "deliverModel"],
            "catalog|selected|bundled|delivered"),
        new(
            "paperboy.apim",
            "APIM Surface",
            "apim-control-plane",
            "Maps Paperboy MCP, Foundry Local, OCR, and ACP resources into local APIM route contracts.",
            ["localEndpoint", "foundryEndpoint", "ocrBridge", "mcpCommand"],
            ["routeManifestJson", "serviceStatus", "validationReport"],
            ["showApimSurface", "validateApimSurface", "checkLocalEndpoint", "checkFoundryHealth", "checkOcrReadiness", "copyRouteManifest"],
            "design|configured|validating|ready|gap"),
        new(
            "paperboy.livetile",
            "Live Tile",
            "a2a-card",
            "Composable UI card schema for A2A interactive surfaces.",
            ["cardId", "state", "actions"],
            ["cardJson", "tileState", "nextActions"],
            ["render", "exportCards", "copySchema"],
            "draft|active|shared"),
        new(
            "paperboy.extensions",
            "Extensions",
            "capability-registry",
            "Discovers *.extension.json capability packages for apps, agents, tools, skills, workflows, connectors, plugins, and MCP/API bridges, and indexes them next to Paperboy's built-in tools.",
            ["extensionRoots[]", "capabilityId", "arguments", "invocationPolicy"],
            ["capabilityIndexJson", "extensionManifests", "extensionCards", "invocationResult", "diagnostics"],
            ["discover", "refresh", "listCapabilities", "describeExtension", "invokeCapability", "copyCapabilityIndex"],
            "empty|discovered|blocked|invoked|error")
    ]);

    /// <summary>
    /// Returns a catalog that also exposes cards contributed by discovered extensions.
    /// Built-in cards always win on id conflicts, so existing consumers are unaffected.
    /// </summary>
    public AgentCardCatalog WithAdditionalCards(IEnumerable<AgentCardDefinition> additionalCards)
    {
        ArgumentNullException.ThrowIfNull(additionalCards);

        var merged = new List<AgentCardDefinition>(Cards);
        var seen = merged.Select(card => card.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var card in additionalCards)
        {
            if (card is null || string.IsNullOrWhiteSpace(card.Id) || !seen.Add(card.Id))
            {
                continue;
            }

            merged.Add(card);
        }

        return merged.Count == Cards.Count ? this : new AgentCardCatalog(merged);
    }

    public string ToJson() => JsonSerializer.Serialize(Cards, JsonOptions);

    public IReadOnlyList<FoundryLocalModelCard> FoundryModelCards { get; } =
    [
        new(
            "qwen2.5-0.5b",
            "Fast language model",
            "Compact chat, lightweight metadata summaries, routing labels, and smoke-test prompts.",
            ["chat", "classification", "metadata", "routing"],
            "Use for quick bundle descriptions and offline delivery notes.",
            "chat, tools",
            "GPU",
            "0.52 GB",
            "apache-2.0",
            "qwen2.5-0.5b-instruct-cuda-gpu:4",
            ["paperboy.bundle", "paperboy.foundryLocal", "paperboy.mcp"]),
        new(
            "qwen3.5-0.8b",
            "Vision-capable responses model",
            "Image understanding, video-frame classification, screenshot summaries, and visual asset tags.",
            ["vision", "image", "video-frame", "classification"],
            "Use with Paperboy's Foundry Local image/video metadata pipeline.",
            "vision-language-chat, tools",
            "GPU",
            "1.32 GB",
            "apache-2.0",
            "qwen3.5-0.8b-cuda-gpu:2",
            ["paperboy.foundryLocal", "paperboy.livetile", "paperboy.bundle"]),
        new(
            "phi-3.5-mini",
            "General local reasoning model",
            "Document summarization, bundle manifests, policy notes, and concise agent instructions.",
            ["chat", "summary", "reasoning", "documents"],
            "Use for rich sidecar metadata over text-heavy bundles.",
            "chat",
            "GPU",
            "2.13 GB",
            "MIT",
            "Phi-3.5-mini-instruct-cuda-gpu:2",
            ["paperboy.bundle", "paperboy.cards", "paperboy.mcp"]),
        new(
            "whisper-tiny",
            "Audio transcription model",
            "Fast speech-to-text for lightweight audio clips before bundle tagging.",
            ["audio", "transcription", "speech"],
            "Use externally or through a future audio-native Paperboy pipeline.",
            "automatic-speech-recognition",
            "GPU",
            "0.08 GB",
            "apache-2.0",
            "openai-whisper-tiny-cuda-gpu:3",
            ["paperboy.foundryLocal", "paperboy.bundle"]),
        new(
            "qwen3-0.6b",
            "Tiny language/tool model",
            "Very small chat/tool model for semantic linking hints, cache probes, and lightweight delivery tests.",
            ["chat", "tools", "semantic-links"],
            "Use for semantic-linking sidecars and fast local PMM smoke tests.",
            "chat, tools",
            "GPU",
            "0.48 GB",
            "apache-2.0",
            "qwen3-0.6b-cuda-gpu:2",
            ["paperboy.modelCards", "paperboy.livetile", "paperboy.mcp"])
    ];

    public string FoundryModelCardsJson() => JsonSerializer.Serialize(FoundryModelCards, JsonOptions);

    public string SchemaJson => """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "$id": "https://darbotlabs.github.io/yumlog/paperboy-agent-card.schema.json",
      "title": "Paperboy Agent Card",
      "type": "object",
      "required": ["id", "title", "role", "description", "inputs", "outputs", "actions", "liveTileState"],
      "properties": {
        "id": { "type": "string", "pattern": "^paperboy\\.[a-z0-9.-]+$" },
        "title": { "type": "string" },
        "role": { "type": "string" },
        "description": { "type": "string" },
        "inputs": { "type": "array", "items": { "type": "string" } },
        "outputs": { "type": "array", "items": { "type": "string" } },
        "actions": { "type": "array", "items": { "type": "string" } },
        "liveTileState": { "type": "string" }
      }
    }
    """;
}
