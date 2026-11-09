using Paperboy.Desktop.Cards;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paperboy.Desktop.Capabilities;

/// <summary>
/// Extension package kinds Paperboy can host. Values are open: unknown kinds are
/// preserved so newer manifests keep loading on older Paperboy builds.
/// </summary>
public static class PaperboyExtensionKinds
{
    public const string App = "app";
    public const string Agent = "agent";
    public const string Tool = "tool";
    public const string Skill = "skill";
    public const string Workflow = "workflow";
    public const string Connector = "connector";
    public const string Plugin = "plugin";
    public const string Extension = "extension";
    public const string Mcp = "mcp";
    public const string Api = "api";

    public static readonly string[] Known =
    [
        App, Agent, Tool, Skill, Workflow, Connector, Plugin, Extension, Mcp, Api
    ];
}

/// <summary>Capability kinds exposed by an extension package.</summary>
public static class PaperboyCapabilityKinds
{
    public const string Tool = "tool";
    public const string Skill = "skill";
    public const string Workflow = "workflow";
    public const string Agent = "agent";
    public const string Connector = "connector";
    public const string Resource = "resource";
    public const string Prompt = "prompt";
    public const string Api = "api";

    public static readonly string[] Known =
    [
        Tool, Skill, Workflow, Agent, Connector, Resource, Prompt, Api
    ];
}

/// <summary>How Paperboy reaches a capability at invocation time.</summary>
public static class PaperboyCapabilityTransports
{
    /// <summary>Implemented inside the Paperboy MCP server itself.</summary>
    public const string BuiltIn = "builtin";

    /// <summary>Local process launched with an argument list (never a shell).</summary>
    public const string Process = "process";

    /// <summary>Local or remote HTTP endpoint.</summary>
    public const string Http = "http";

    /// <summary>Another MCP stdio server; Paperboy publishes the launch contract only.</summary>
    public const string McpStdio = "mcp-stdio";

    /// <summary>Documentation-only capability that a human or agent performs elsewhere.</summary>
    public const string Manual = "manual";

    public static readonly string[] Known =
    [
        BuiltIn, Process, Http, McpStdio, Manual
    ];

    public static bool IsInvocable(string transport) =>
        string.Equals(transport, Process, StringComparison.OrdinalIgnoreCase)
        || string.Equals(transport, Http, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Trust bucket derived from the root a manifest was discovered in.</summary>
public static class PaperboyExtensionTrust
{
    /// <summary>Shipped next to the Paperboy binaries; same trust as the app.</summary>
    public const string App = "app";

    /// <summary>Discovered under a user, machine, or environment-supplied root.</summary>
    public const string External = "external";
}

public sealed record PaperboyExtensionRequirements
{
    public string? Paperboy { get; init; }
    public string[] Services { get; init; } = [];
    public string[] Models { get; init; } = [];
}

public sealed record PaperboyExtensionResource
{
    public string Uri { get; init; } = "";
    public string Name { get; init; } = "";
    public string MimeType { get; init; } = "application/json";
    public string? Path { get; init; }
    public string Description { get; init; } = "";
}

/// <summary>
/// A single discoverable capability. Extensions declare capabilities; Paperboy
/// indexes them and can invoke <c>process</c> and <c>http</c> transports.
/// </summary>
public sealed record PaperboyCapabilityDescriptor
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = PaperboyCapabilityKinds.Tool;
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Transport { get; init; } = PaperboyCapabilityTransports.Manual;

    public string? Command { get; init; }
    public string[] Args { get; init; } = [];
    public string? WorkingDirectory { get; init; }

    public string? Url { get; init; }
    public string? Method { get; init; }
    public Dictionary<string, string> Headers { get; init; } = [];

    public int TimeoutSeconds { get; init; } = 60;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? InputSchema { get; init; }

    public string[] Tags { get; init; } = [];

    /// <summary>Agent-card ids this capability contributes to.</summary>
    public string[] Cards { get; init; } = [];
}

/// <summary>Deserialized <c>*.extension.json</c> package manifest.</summary>
public sealed record PaperboyExtensionManifest
{
    public const string CurrentSchemaVersion = "1.0";
    public const string SchemaUri = "https://darbotlabs.github.io/yumlog/paperboy-extension.schema.json";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "0.0.0";
    public string Kind { get; init; } = PaperboyExtensionKinds.Extension;
    public string Publisher { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string? Homepage { get; init; }
    public string[] Tags { get; init; } = [];
    public PaperboyCapabilityDescriptor[] Capabilities { get; init; } = [];
    public AgentCardDefinition[] Cards { get; init; } = [];
    public PaperboyExtensionResource[] Resources { get; init; } = [];
    public PaperboyExtensionRequirements? Requires { get; init; }
}

/// <summary>A manifest plus where it came from and whether Paperboy may invoke it.</summary>
public sealed record PaperboyExtensionSummary
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "0.0.0";
    public string Kind { get; init; } = PaperboyExtensionKinds.Extension;
    public string Publisher { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string Trust { get; init; } = PaperboyExtensionTrust.External;
    public bool InvocationAllowed { get; init; }
    public string ManifestPath { get; init; } = "";
    public string Root { get; init; } = "";
    public string[] Tags { get; init; } = [];
    public string[] CapabilityIds { get; init; } = [];
    public string[] CardIds { get; init; } = [];
    public PaperboyExtensionResource[] Resources { get; init; } = [];
    public PaperboyExtensionRequirements? Requires { get; init; }
}

/// <summary>An indexed capability with provenance and invocation eligibility.</summary>
public sealed record PaperboyCapabilityEntry
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = PaperboyCapabilityKinds.Tool;
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Transport { get; init; } = PaperboyCapabilityTransports.Manual;
    public string Source { get; init; } = PaperboyCapabilityTransports.BuiltIn;
    public string ExtensionId { get; init; } = "";
    public string Trust { get; init; } = PaperboyExtensionTrust.App;
    public bool Invocable { get; init; }
    public string InvocationNote { get; init; } = "";
    public string[] Tags { get; init; } = [];
    public string[] Cards { get; init; } = [];

    [JsonIgnore]
    public PaperboyCapabilityDescriptor? Descriptor { get; init; }

    [JsonIgnore]
    public string ManifestDirectory { get; init; } = "";
}

public sealed record PaperboyCapabilityDiagnostic(string Path, string Severity, string Message);

/// <summary>A Paperboy MCP tool projected into the capability contract.</summary>
public sealed record PaperboyBuiltInTool(string Name, string Description);

public sealed record PaperboyCapabilityPolicy
{
    public bool AllowExternalInvocation { get; init; }
    public string AllowExternalInvocationVariable { get; init; } = PaperboyCapabilityEnvironment.AllowInvokeVariable;
    public string RootsVariable { get; init; } = PaperboyCapabilityEnvironment.RootsVariable;
    public string Notes { get; init; } = "";
}

/// <summary>Environment variables that steer discovery and invocation.</summary>
public static class PaperboyCapabilityEnvironment
{
    /// <summary>Extra discovery roots, separated by <see cref="System.IO.Path.PathSeparator"/>.</summary>
    public const string RootsVariable = "PAPERBOY_EXTENSIONS_PATH";

    /// <summary>Set to <c>1</c>/<c>true</c> to allow invoking externally discovered capabilities.</summary>
    public const string AllowInvokeVariable = "PAPERBOY_EXTENSIONS_ALLOW_INVOKE";
}

/// <summary>Serializable snapshot of everything Paperboy can currently reach.</summary>
public sealed record PaperboyCapabilityIndex
{
    public string SchemaVersion { get; init; } = PaperboyExtensionManifest.CurrentSchemaVersion;
    public string Schema { get; init; } = PaperboyExtensionManifest.SchemaUri;
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string[] Roots { get; init; } = [];
    public PaperboyCapabilityPolicy Policy { get; init; } = new();
    public PaperboyExtensionSummary[] Extensions { get; init; } = [];
    public PaperboyCapabilityEntry[] Capabilities { get; init; } = [];
    public AgentCardDefinition[] ExtensionCards { get; init; } = [];
    public PaperboyCapabilityDiagnostic[] Diagnostics { get; init; } = [];
}

public sealed record PaperboyCapabilityInvocationResult
{
    public string CapabilityId { get; init; } = "";
    public string ExtensionId { get; init; } = "";
    public string Transport { get; init; } = "";
    public bool Success { get; init; }
    public int? ExitCode { get; init; }
    public int? StatusCode { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
    public double DurationMs { get; init; }
    public string? Command { get; init; }
    public string? Url { get; init; }
}
