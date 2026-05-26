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
    string DeliveryGuidance);

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
            "Exposes Paperboy pack/list/unpack/toss as agent-callable MCP tools.",
            ["jsonRpc", "toolName", "arguments"],
            ["toolResult", "manifest", "agentCardCatalog"],
            ["initialize", "tools/list", "tools/call"],
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
            ["modelAlias", "modelRole", "endpoint", "deliveryProfile"],
            ["modelCardJson", "recommendedBundle", "mcpToolHints"],
            ["showModels", "exportModelCards", "bundleModelCards", "deliverModel"],
            "catalog|selected|bundled|delivered"),
        new(
            "paperboy.livetile",
            "Live Tile",
            "a2a-card",
            "Composable UI card schema for A2A interactive surfaces.",
            ["cardId", "state", "actions"],
            ["cardJson", "tileState", "nextActions"],
            ["render", "exportCards", "copySchema"],
            "draft|active|shared")
    ]);

    public string ToJson() => JsonSerializer.Serialize(Cards, JsonOptions);

    public IReadOnlyList<FoundryLocalModelCard> FoundryModelCards { get; } =
    [
        new(
            "qwen2.5-0.5b",
            "Fast language model",
            "Compact chat, lightweight metadata summaries, routing labels, and smoke-test prompts.",
            ["chat", "classification", "metadata", "routing"],
            "Use for quick bundle descriptions and offline delivery notes."),
        new(
            "qwen3.5-0.8b",
            "Vision-capable responses model",
            "Image understanding, video-frame classification, screenshot summaries, and visual asset tags.",
            ["vision", "image", "video-frame", "classification"],
            "Use with Paperboy's Foundry Local image/video metadata pipeline."),
        new(
            "phi-3.5-mini",
            "General local reasoning model",
            "Document summarization, bundle manifests, policy notes, and concise agent instructions.",
            ["chat", "summary", "reasoning", "documents"],
            "Use for rich sidecar metadata over text-heavy bundles."),
        new(
            "whisper-tiny",
            "Audio transcription model",
            "Fast speech-to-text for lightweight audio clips before bundle tagging.",
            ["audio", "transcription", "speech"],
            "Use externally or through a future audio-native Paperboy pipeline."),
        new(
            "qwen3-0.6b-embedding",
            "Embedding model",
            "Local vector fingerprints for bundle search and duplicate detection.",
            ["embedding", "search", "similarity"],
            "Use for future semantic index bundles.")
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
