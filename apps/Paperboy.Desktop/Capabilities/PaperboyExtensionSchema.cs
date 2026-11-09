namespace Paperboy.Desktop.Capabilities;

/// <summary>
/// JSON schema for <c>*.extension.json</c> manifests. Mirrored to
/// <c>Assets\Extensions\paperboy.extension.schema.json</c> so publishers can
/// validate manifests without running Paperboy.
/// </summary>
public static class PaperboyExtensionSchema
{
    public const string Json = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "$id": "https://darbotlabs.github.io/yumlog/paperboy-extension.schema.json",
      "title": "Paperboy Extension Manifest",
      "description": "Discoverable capability package for Paperboy apps, agents, tools, skills, workflows, connectors, plugins, extensions, and MCP/API surfaces.",
      "type": "object",
      "required": ["schemaVersion", "id", "name", "version", "kind"],
      "properties": {
        "schemaVersion": { "type": "string", "pattern": "^[0-9]+(\\.[0-9]+)*$" },
        "id": { "type": "string", "pattern": "^[A-Za-z0-9][A-Za-z0-9._-]*$" },
        "name": { "type": "string" },
        "version": { "type": "string" },
        "kind": {
          "type": "string",
          "enum": ["app", "agent", "tool", "skill", "workflow", "connector", "plugin", "extension", "mcp", "api"]
        },
        "publisher": { "type": "string" },
        "description": { "type": "string" },
        "enabled": { "type": "boolean", "default": true },
        "homepage": { "type": "string" },
        "tags": { "type": "array", "items": { "type": "string" } },
        "capabilities": {
          "type": "array",
          "items": { "$ref": "#/$defs/capability" }
        },
        "cards": {
          "type": "array",
          "items": { "$ref": "https://darbotlabs.github.io/yumlog/paperboy-agent-card.schema.json" }
        },
        "resources": {
          "type": "array",
          "items": { "$ref": "#/$defs/resource" }
        },
        "requires": { "$ref": "#/$defs/requires" }
      },
      "$defs": {
        "capability": {
          "type": "object",
          "required": ["id", "kind", "transport"],
          "properties": {
            "id": { "type": "string", "pattern": "^[A-Za-z0-9][A-Za-z0-9._-]*$" },
            "kind": {
              "type": "string",
              "enum": ["tool", "skill", "workflow", "agent", "connector", "resource", "prompt", "api"]
            },
            "title": { "type": "string" },
            "description": { "type": "string" },
            "transport": {
              "type": "string",
              "enum": ["process", "http", "mcp-stdio", "manual"],
              "description": "'builtin' is reserved for Paperboy's own tools and is rejected in manifests."
            },
            "command": { "type": "string" },
            "args": { "type": "array", "items": { "type": "string" } },
            "workingDirectory": { "type": "string" },
            "url": { "type": "string", "format": "uri" },
            "method": { "type": "string", "enum": ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"] },
            "headers": { "type": "object", "additionalProperties": { "type": "string" } },
            "timeoutSeconds": { "type": "integer", "minimum": 1, "maximum": 900, "default": 60 },
            "inputSchema": { "type": "object" },
            "tags": { "type": "array", "items": { "type": "string" } },
            "cards": { "type": "array", "items": { "type": "string" } }
          },
          "allOf": [
            {
              "if": { "properties": { "transport": { "const": "process" } }, "required": ["transport"] },
              "then": { "required": ["command"] }
            },
            {
              "if": { "properties": { "transport": { "const": "http" } }, "required": ["transport"] },
              "then": { "required": ["url"] }
            }
          ]
        },
        "resource": {
          "type": "object",
          "required": ["uri", "name"],
          "properties": {
            "uri": { "type": "string" },
            "name": { "type": "string" },
            "mimeType": { "type": "string", "default": "application/json" },
            "path": { "type": "string" },
            "description": { "type": "string" }
          }
        },
        "requires": {
          "type": "object",
          "properties": {
            "paperboy": { "type": "string" },
            "services": { "type": "array", "items": { "type": "string" } },
            "models": { "type": "array", "items": { "type": "string" } }
          }
        }
      }
    }
    """;
}
