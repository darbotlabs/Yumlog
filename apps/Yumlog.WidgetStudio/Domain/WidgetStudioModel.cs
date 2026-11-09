using System.Text.Json;

namespace Yumlog.WidgetStudio.Domain;

public static class WidgetStudioSchema
{
    /// <summary>Current authoring schema version emitted by Widget Studio.</summary>
    public const string Version = "1.1";

    /// <summary>Oldest project schema version that still loads and validates unchanged.</summary>
    public const string BaselineVersion = "1.0";

    /// <summary>First project schema version that accepts capability contracts and vendor extension bags.</summary>
    public const string CapabilityVersion = "1.1";

    public const string ProjectSchemaId = "https://darbotlabs.github.io/yumlog/widget-studio.project.schema.json";

    public static readonly string[] SupportedVersions = [BaselineVersion, CapabilityVersion];

    public static bool IsSupportedVersion(string? version)
        => !string.IsNullOrWhiteSpace(version)
            && SupportedVersions.Contains(version, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the declared project schema version accepts capability contracts.</summary>
    public static bool SupportsCapabilities(string? version)
        => TryParseVersion(version, out var major, out var minor) && (major > 1 || (major == 1 && minor >= 1));

    public static bool TryParseVersion(string? version, out int major, out int minor)
    {
        major = 0;
        minor = 0;

        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var segments = version.Split('.');
        return segments.Length == 2
            && int.TryParse(segments[0], out major)
            && int.TryParse(segments[1], out minor)
            && major >= 0
            && minor >= 0;
    }
}

public static class WidgetStudioJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };
}

public sealed record WidgetStudioProject
{
    public string SchemaVersion { get; init; } = WidgetStudioSchema.Version;
    public string ProjectId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "0.1.0";
    public string Owner { get; init; } = "";
    public WidgetDefinition Widget { get; init; } = new();
    public WidgetPackageDefinition Package { get; init; } = new();
    public string[] HandoffNotes { get; init; } = [];

    /// <summary>
    /// Namespaced vendor extension bag (<c>x-*</c> keys). Preserved verbatim through validation,
    /// preview, and package export so downstream hosts can carry forward-compatible metadata.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

public sealed record WidgetDefinition
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string Kind { get; init; } = "adaptiveLiveTile";
    public string[] Tags { get; init; } = [];
    public WidgetDataSource[] DataSources { get; init; } = [];
    public AcpActionDefinition[] AcpActions { get; init; } = [];
    public ApimOperationBinding[] ApimBindings { get; init; } = [];

    /// <summary>
    /// Versioned capability contracts (tools, skills, workflows, connectors, plugins,
    /// extensions, MCP/API surfaces, continuous AI loops). Requires schemaVersion 1.1+.
    /// </summary>
    public WidgetCapability[] Capabilities { get; init; } = [];

    public RenderSchema Render { get; init; } = new();
    public RefreshCadence RefreshCadence { get; init; } = new();
}

public sealed record WidgetDataSource
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string BindingRef { get; init; } = "";
    public string PayloadSchemaUri { get; init; } = "";
    public string RefreshPolicy { get; init; } = "inherit";
    public JsonElement? SamplePayload { get; init; }
}

public sealed record AcpActionDefinition
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Capability { get; init; } = "";
    public string EnvelopeSchemaUri { get; init; } = "";
    public string InputSchemaUri { get; init; } = "";
    public string OutputSchemaUri { get; init; } = "";
    public string Permission { get; init; } = "invoke";
    public string TargetDataSourceId { get; init; } = "";
}

public sealed record ApimOperationBinding
{
    public string Id { get; init; } = "";
    public string BackendId { get; init; } = "";
    public string OperationId { get; init; } = "";
    public string Method { get; init; } = "GET";
    public string Route { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string AuthScheme { get; init; } = "none";
    public string RequestSchemaUri { get; init; } = "";
    public string ResponseSchemaUri { get; init; } = "";
    public string[] PolicyTags { get; init; } = [];
}

public sealed record RenderSchema
{
    public string SchemaUri { get; init; } = "urn:yumlog:widget:render:v1";
    public string Layout { get; init; } = "card";
    public string Theme { get; init; } = "paperboy";
    public RenderState[] States { get; init; } = [];
    public RenderBinding[] Bindings { get; init; } = [];
}

public sealed record RenderState
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Intent { get; init; } = "";
    public string[] VisibleWhen { get; init; } = [];
}

public sealed record RenderBinding
{
    public string Id { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string FieldPath { get; init; } = "";
    public string Target { get; init; } = "";
    public string Format { get; init; } = "text";
}

public sealed record RefreshCadence
{
    public string Mode { get; init; } = "manual";
    public int IntervalSeconds { get; init; }
    public int JitterSeconds { get; init; }
    public int StaleAfterSeconds { get; init; }
    public string Cron { get; init; } = "";
    public string EventTopic { get; init; } = "";
}

public sealed record WidgetPackageDefinition
{
    public string PackageId { get; init; } = "";
    public string Format { get; init; } = "yumlog.widget.v1+zip";
    public bool IncludePreview { get; init; } = true;
    public bool IncludeSchemas { get; init; } = true;

    /// <summary>Emit <c>capabilities/index.json</c> when the project declares capability contracts.</summary>
    public bool IncludeCapabilityIndex { get; init; } = true;
}
