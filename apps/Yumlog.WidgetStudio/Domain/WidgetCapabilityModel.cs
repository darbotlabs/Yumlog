using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yumlog.WidgetStudio.Domain;

/// <summary>
/// Versioned plugin/extension capability contract shared by Widget Studio authoring,
/// package handoff, and host loaders (ACP, APIM, MCP, and continuous AI surfaces).
/// </summary>
public static class WidgetCapabilityContract
{
    public const string ContractSchemaId = "https://darbotlabs.github.io/yumlog/widget-studio.capability.schema.json";
    public const string ContractSchemaFileName = "widget-studio.capability.schema.json";
    public const string ContractSchemaUri = "urn:yumlog:widget:capability:v1";
    public const string IndexSchemaUri = "urn:yumlog:widget:capability-index:v1";
    public const string CurrentContractVersion = "1.0";
    public const int SupportedContractMajor = 1;
    public const string VendorPrefix = "x-";

    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "tool",
        "skill",
        "workflow",
        "connector",
        "plugin",
        "extension",
        "mcpServer",
        "apiSurface",
        "continuousAi"
    };

    public static readonly IReadOnlySet<string> Transports = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "inProcess",
        "stdio",
        "http",
        "mcp",
        "acp",
        "apim",
        "event",
        "file"
    };

    public static readonly IReadOnlySet<string> Stabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "experimental",
        "preview",
        "stable",
        "deprecated"
    };

    public static readonly IReadOnlySet<string> ActivationModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "manual",
        "onLoad",
        "interval",
        "event",
        "continuous"
    };

    /// <summary>Transports that require a resolvable absolute entry point URI.</summary>
    public static readonly IReadOnlySet<string> RemoteTransports = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "mcp",
        "acp",
        "apim"
    };

    /// <summary>Kinds whose input/output schema URIs are expected for deterministic agent handoff.</summary>
    public static readonly IReadOnlySet<string> SchemaBearingKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "tool",
        "skill",
        "workflow",
        "mcpServer",
        "apiSurface",
        "continuousAi"
    };

    public static bool IsVendorExtension(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.StartsWith(VendorPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool TryParseContractVersion(string? value, out int major, out int minor)
    {
        major = 0;
        minor = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var segments = value.Split('.');
        if (segments.Length is < 2 or > 3)
        {
            return false;
        }

        if (!int.TryParse(segments[0], out major) || !int.TryParse(segments[1], out minor) || major < 0 || minor < 0)
        {
            return false;
        }

        return segments.Length != 3 || (int.TryParse(segments[2], out var patch) && patch >= 0);
    }
}

/// <summary>Declarative activation contract for tools, workflows, and continuous AI loops.</summary>
public sealed record CapabilityActivation
{
    public string Mode { get; init; } = "manual";
    public int IntervalSeconds { get; init; }
    public string EventTopic { get; init; } = "";
    public string Schedule { get; init; } = "";
}

/// <summary>
/// A single extensible capability exposed by a widget: tool, skill, workflow, connector,
/// plugin, extension, MCP/API surface, or continuous AI loop.
/// </summary>
public sealed record WidgetCapability
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "tool";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string ContractVersion { get; init; } = WidgetCapabilityContract.CurrentContractVersion;
    public string Provider { get; init; } = "";
    public string EntryPoint { get; init; } = "";
    public string Transport { get; init; } = "inProcess";
    public string Stability { get; init; } = "experimental";
    public string[] Permissions { get; init; } = [];
    public string[] Requires { get; init; } = [];
    public string InputSchemaUri { get; init; } = "";
    public string OutputSchemaUri { get; init; } = "";
    public string[] Tags { get; init; } = [];
    public string ReplacedBy { get; init; } = "";
    public CapabilityActivation Activation { get; init; } = new();
    public JsonElement? Config { get; init; }
}

public sealed record WidgetCapabilityIndexEntry
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string ContractVersion { get; init; } = "";
    public string Provider { get; init; } = "";
    public string EntryPoint { get; init; } = "";
    public string Transport { get; init; } = "";
    public string Stability { get; init; } = "";
    public string[] Permissions { get; init; } = [];
    public string[] Requires { get; init; } = [];
    public string InputSchemaUri { get; init; } = "";
    public string OutputSchemaUri { get; init; } = "";
    public string[] Tags { get; init; } = [];
    public CapabilityActivation Activation { get; init; } = new();
    public bool VendorExtension { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Config { get; init; }
}

/// <summary>
/// Host-loadable projection of the declared capability contracts. Emitted to
/// <c>capabilities/index.json</c> so package, MCP, and APIM agents can discover
/// widget extension points without parsing the full authoring manifest.
/// </summary>
public sealed record WidgetCapabilityIndex
{
    public string SchemaUri { get; init; } = WidgetCapabilityContract.IndexSchemaUri;
    public string ContractSchemaId { get; init; } = WidgetCapabilityContract.ContractSchemaId;
    public string ProjectSchemaVersion { get; init; } = WidgetStudioSchema.Version;
    public int SupportedContractMajor { get; init; } = WidgetCapabilityContract.SupportedContractMajor;
    public string WidgetId { get; init; } = "";
    public string PackageId { get; init; } = "";
    public DateTimeOffset GeneratedUtc { get; init; }
    public IReadOnlyDictionary<string, int> CountsByKind { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<WidgetCapabilityIndexEntry> Capabilities { get; init; } = [];

    public static WidgetCapabilityIndex Create(WidgetStudioProject project, DateTimeOffset? generatedUtc = null)
    {
        var capabilities = project.Widget.Capabilities;

        return new WidgetCapabilityIndex
        {
            ProjectSchemaVersion = project.SchemaVersion,
            WidgetId = project.Widget.Id,
            PackageId = project.Package.PackageId,
            GeneratedUtc = generatedUtc ?? DateTimeOffset.UtcNow,
            CountsByKind = capabilities
                .GroupBy(capability => capability.Kind, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            Capabilities = capabilities
                .Select(capability => new WidgetCapabilityIndexEntry
                {
                    Id = capability.Id,
                    Kind = capability.Kind,
                    DisplayName = capability.DisplayName,
                    Description = capability.Description,
                    ContractVersion = capability.ContractVersion,
                    Provider = capability.Provider,
                    EntryPoint = capability.EntryPoint,
                    Transport = capability.Transport,
                    Stability = capability.Stability,
                    Permissions = capability.Permissions,
                    Requires = capability.Requires,
                    InputSchemaUri = capability.InputSchemaUri,
                    OutputSchemaUri = capability.OutputSchemaUri,
                    Tags = capability.Tags,
                    Activation = capability.Activation,
                    VendorExtension = WidgetCapabilityContract.IsVendorExtension(capability.Kind),
                    Config = capability.Config
                })
                .ToArray()
        };
    }
}
