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
