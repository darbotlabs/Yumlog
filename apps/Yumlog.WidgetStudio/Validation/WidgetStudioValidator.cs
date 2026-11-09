using System.Text.Json;
using System.Text.RegularExpressions;
using Yumlog.WidgetStudio.Domain;

namespace Yumlog.WidgetStudio.Validation;

public sealed record WidgetValidationIssue(string Severity, string Path, string Code, string Message);

public sealed record WidgetValidationReport(IReadOnlyList<WidgetValidationIssue> Issues)
{
    public bool IsValid => !Issues.Any(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
}

public static partial class WidgetStudioValidator
{
    private static readonly HashSet<string> SupportedDataSourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "apimOperation",
        "acpEnvelope",
        "staticJson",
        "mdkgLayer"
    };

    private static readonly HashSet<string> SupportedWidgetKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "adaptiveLiveTile",
        "paperboyLiveTile",
        "dashboardTile",
        "agentCardTile"
    };

    private static readonly HashSet<string> SupportedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET",
        "POST",
        "PUT",
        "PATCH",
        "DELETE"
    };

    private static readonly HashSet<string> SupportedRefreshModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "manual",
        "interval",
        "cron",
        "eventDriven"
    };

    private static readonly HashSet<string> SupportedLayouts = new(StringComparer.OrdinalIgnoreCase)
    {
        "card",
        "grid",
        "hero",
        "list",
        "stack"
    };

    private static readonly HashSet<string> SupportedPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "read",
        "invoke",
        "write",
        "admin"
    };

    public static WidgetStudioProject Load(string manifestPath)
    {
        using var stream = File.OpenRead(manifestPath);
        return JsonSerializer.Deserialize<WidgetStudioProject>(stream, WidgetStudioJson.Options)
            ?? throw new InvalidOperationException($"Widget Studio manifest was empty: {manifestPath}");
    }

    public static WidgetValidationReport Validate(WidgetStudioProject project)
    {
        var issues = new List<WidgetValidationIssue>();

        Required(project.SchemaVersion, "$.schemaVersion", "schemaVersion.required", issues);
        if (!WidgetStudioSchema.IsSupportedVersion(project.SchemaVersion))
        {
            Error("$.schemaVersion", "schemaVersion.unsupported", $"Expected schemaVersion to be one of: {string.Join(", ", WidgetStudioSchema.SupportedVersions)}.");
        }

        ValidateId(project.ProjectId, "$.projectId", issues);
        Required(project.Name, "$.name", "name.required", issues);
        Required(project.Version, "$.version", "version.required", issues);

        var widget = project.Widget;
        ValidateId(widget.Id, "$.widget.id", issues);
        Required(widget.DisplayName, "$.widget.displayName", "widget.displayName.required", issues);
        Required(widget.Description, "$.widget.description", "widget.description.required", issues);
        ValidateOpenEnum(widget.Kind, SupportedWidgetKinds, "$.widget.kind", "widget.kind", "widget kind", issues);

        ValidateDuplicateIds(widget.DataSources.Select(source => source.Id), "$.widget.dataSources", "dataSource.id.duplicate", issues);
        ValidateDuplicateIds(widget.AcpActions.Select(action => action.Id), "$.widget.acpActions", "acpAction.id.duplicate", issues);
        ValidateDuplicateIds(widget.ApimBindings.Select(binding => binding.Id), "$.widget.apimBindings", "apimBinding.id.duplicate", issues);
        ValidateDuplicateIds(widget.Render.States.Select(state => state.Id), "$.widget.render.states", "renderState.id.duplicate", issues);
        ValidateDuplicateIds(widget.Render.Bindings.Select(binding => binding.Id), "$.widget.render.bindings", "renderBinding.id.duplicate", issues);

        var dataSourceIds = widget.DataSources.Select(source => source.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var apimBindingIds = widget.ApimBindings.Select(binding => binding.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var acpActionIds = widget.AcpActions.Select(action => action.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (widget.DataSources.Length == 0)
        {
            Error("$.widget.dataSources", "dataSource.required", "At least one data source is required.");
        }

        for (var i = 0; i < widget.DataSources.Length; i++)
        {
            ValidateDataSource(widget.DataSources[i], i, apimBindingIds, issues);
        }

        if (widget.ApimBindings.Length == 0)
        {
            Error("$.widget.apimBindings", "apimBinding.required", "At least one APIM operation binding is required.");
        }

        for (var i = 0; i < widget.ApimBindings.Length; i++)
        {
            ValidateApimBinding(widget.ApimBindings[i], i, issues);
        }

        for (var i = 0; i < widget.AcpActions.Length; i++)
        {
            ValidateAcpAction(widget.AcpActions[i], i, dataSourceIds, issues);
        }

        ValidateRenderSchema(widget.Render, dataSourceIds, issues);
        ValidateRefreshCadence(widget.RefreshCadence, issues);
        ValidateCapabilities(project, dataSourceIds, acpActionIds, apimBindingIds, issues);
        ValidateExtensions(project.Extensions, project.SchemaVersion, "$.extensions", issues);
        ValidatePackage(project.Package, project.Widget.Id, issues);

        return new WidgetValidationReport(issues);

        void Error(string path, string code, string message) => issues.Add(new WidgetValidationIssue("error", path, code, message));
    }

    private static void ValidateDataSource(
        WidgetDataSource source,
        int index,
        HashSet<string> apimBindingIds,
        List<WidgetValidationIssue> issues)
    {
        var path = $"$.widget.dataSources[{index}]";
        ValidateId(source.Id, $"{path}.id", issues);
        Required(source.DisplayName, $"{path}.displayName", "dataSource.displayName.required", issues);
        Required(source.Type, $"{path}.type", "dataSource.type.required", issues);

        if (!string.IsNullOrWhiteSpace(source.Type) && !SupportedDataSourceTypes.Contains(source.Type))
        {
            ValidateOpenEnum(source.Type, SupportedDataSourceTypes, $"{path}.type", "dataSource.type", "data source type", issues);
        }

        if (string.Equals(source.Type, "apimOperation", StringComparison.OrdinalIgnoreCase)
            && !apimBindingIds.Contains(source.BindingRef))
        {
            issues.Add(new WidgetValidationIssue("error", $"{path}.bindingRef", "dataSource.bindingRef.missing", "APIM data sources must reference a declared APIM binding."));
        }

        if (string.IsNullOrWhiteSpace(source.PayloadSchemaUri))
        {
            issues.Add(new WidgetValidationIssue("warning", $"{path}.payloadSchemaUri", "dataSource.payloadSchemaUri.recommended", "A payload schema URI improves package verification."));
        }
    }

    private static void ValidateApimBinding(ApimOperationBinding binding, int index, List<WidgetValidationIssue> issues)
    {
        var path = $"$.widget.apimBindings[{index}]";
        ValidateId(binding.Id, $"{path}.id", issues);
        Required(binding.BackendId, $"{path}.backendId", "apimBinding.backendId.required", issues);
        Required(binding.OperationId, $"{path}.operationId", "apimBinding.operationId.required", issues);
        Required(binding.Route, $"{path}.route", "apimBinding.route.required", issues);
        Required(binding.ResponseSchemaUri, $"{path}.responseSchemaUri", "apimBinding.responseSchemaUri.required", issues);

        if (!SupportedMethods.Contains(binding.Method))
        {
            issues.Add(new WidgetValidationIssue("error", $"{path}.method", "apimBinding.method.unsupported", $"Unsupported HTTP method '{binding.Method}'."));
        }

        if (!string.IsNullOrWhiteSpace(binding.Route) && !binding.Route.StartsWith('/'))
        {
            issues.Add(new WidgetValidationIssue("error", $"{path}.route", "apimBinding.route.invalid", "APIM routes must start with '/'."));
        }

        if (!string.IsNullOrWhiteSpace(binding.BaseUrl)
            && !Uri.TryCreate(binding.BaseUrl, UriKind.Absolute, out _))
        {
            issues.Add(new WidgetValidationIssue("error", $"{path}.baseUrl", "apimBinding.baseUrl.invalid", "baseUrl must be an absolute URI when supplied."));
        }
    }

    private static void ValidateAcpAction(
        AcpActionDefinition action,
        int index,
        HashSet<string> dataSourceIds,
        List<WidgetValidationIssue> issues)
    {
        var path = $"$.widget.acpActions[{index}]";
        ValidateId(action.Id, $"{path}.id", issues);
        Required(action.DisplayName, $"{path}.displayName", "acpAction.displayName.required", issues);
        Required(action.Capability, $"{path}.capability", "acpAction.capability.required", issues);
        Required(action.EnvelopeSchemaUri, $"{path}.envelopeSchemaUri", "acpAction.envelopeSchemaUri.required", issues);

        if (!SupportedPermissions.Contains(action.Permission))
        {
            issues.Add(new WidgetValidationIssue("error", $"{path}.permission", "acpAction.permission.unsupported", $"Unsupported permission '{action.Permission}'."));
        }

        if (!string.IsNullOrWhiteSpace(action.TargetDataSourceId) && !dataSourceIds.Contains(action.TargetDataSourceId))
        {
            issues.Add(new WidgetValidationIssue("error", $"{path}.targetDataSourceId", "acpAction.targetDataSourceId.missing", "ACP action targetDataSourceId must reference a data source."));
        }
    }

    private static void ValidateRenderSchema(RenderSchema render, HashSet<string> dataSourceIds, List<WidgetValidationIssue> issues)
    {
        Required(render.SchemaUri, "$.widget.render.schemaUri", "render.schemaUri.required", issues);

        if (!SupportedLayouts.Contains(render.Layout))
        {
            ValidateOpenEnum(render.Layout, SupportedLayouts, "$.widget.render.layout", "render.layout", "render layout", issues);
        }

        if (render.States.Length == 0)
        {
            issues.Add(new WidgetValidationIssue("error", "$.widget.render.states", "render.states.required", "At least one render state is required."));
        }

        for (var i = 0; i < render.States.Length; i++)
        {
            var state = render.States[i];
            ValidateId(state.Id, $"$.widget.render.states[{i}].id", issues);
            Required(state.Title, $"$.widget.render.states[{i}].title", "renderState.title.required", issues);
            Required(state.Intent, $"$.widget.render.states[{i}].intent", "renderState.intent.required", issues);
        }

        if (render.Bindings.Length == 0)
        {
            issues.Add(new WidgetValidationIssue("warning", "$.widget.render.bindings", "render.bindings.recommended", "Render bindings should connect data sources to preview targets."));
        }

        for (var i = 0; i < render.Bindings.Length; i++)
        {
            var binding = render.Bindings[i];
            ValidateId(binding.Id, $"$.widget.render.bindings[{i}].id", issues);
            Required(binding.SourceId, $"$.widget.render.bindings[{i}].sourceId", "renderBinding.sourceId.required", issues);
            Required(binding.FieldPath, $"$.widget.render.bindings[{i}].fieldPath", "renderBinding.fieldPath.required", issues);
            Required(binding.Target, $"$.widget.render.bindings[{i}].target", "renderBinding.target.required", issues);

            if (!string.IsNullOrWhiteSpace(binding.SourceId) && !dataSourceIds.Contains(binding.SourceId))
            {
                issues.Add(new WidgetValidationIssue("error", $"$.widget.render.bindings[{i}].sourceId", "renderBinding.sourceId.missing", "Render binding sourceId must reference a data source."));
            }
        }
    }

    private static void ValidateRefreshCadence(RefreshCadence cadence, List<WidgetValidationIssue> issues)
    {
        Required(cadence.Mode, "$.widget.refreshCadence.mode", "refreshCadence.mode.required", issues);

        if (!SupportedRefreshModes.Contains(cadence.Mode))
        {
            issues.Add(new WidgetValidationIssue("error", "$.widget.refreshCadence.mode", "refreshCadence.mode.unsupported", $"Unsupported refresh mode '{cadence.Mode}'."));
            return;
        }

        if (string.Equals(cadence.Mode, "interval", StringComparison.OrdinalIgnoreCase)
            && (cadence.IntervalSeconds < 5 || cadence.IntervalSeconds > 86400))
        {
            issues.Add(new WidgetValidationIssue("error", "$.widget.refreshCadence.intervalSeconds", "refreshCadence.intervalSeconds.range", "Interval refresh requires intervalSeconds between 5 and 86400."));
        }

        if (string.Equals(cadence.Mode, "cron", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(cadence.Cron))
        {
            issues.Add(new WidgetValidationIssue("error", "$.widget.refreshCadence.cron", "refreshCadence.cron.required", "Cron refresh requires a cron expression."));
        }

        if (string.Equals(cadence.Mode, "eventDriven", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(cadence.EventTopic))
        {
            issues.Add(new WidgetValidationIssue("error", "$.widget.refreshCadence.eventTopic", "refreshCadence.eventTopic.required", "Event-driven refresh requires an event topic."));
        }
    }

    private static void ValidatePackage(WidgetPackageDefinition package, string widgetId, List<WidgetValidationIssue> issues)
    {
        Required(package.PackageId, "$.package.packageId", "package.packageId.required", issues);
        Required(package.Format, "$.package.format", "package.format.required", issues);

        if (!string.IsNullOrWhiteSpace(package.PackageId) && !package.PackageId.StartsWith(widgetId, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new WidgetValidationIssue("warning", "$.package.packageId", "package.packageId.prefix", "Package id should start with the widget id for deterministic handoff."));
        }
    }

    private static void Required(string value, string path, string code, List<WidgetValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new WidgetValidationIssue("error", path, code, "Value is required."));
        }
    }

    private static void ValidateId(string value, string path, List<WidgetValidationIssue> issues)
    {
        Required(value, path, "id.required", issues);

        if (!string.IsNullOrWhiteSpace(value) && !WidgetIdRegex().IsMatch(value))
        {
            issues.Add(new WidgetValidationIssue("error", path, "id.invalid", "Ids must use lower-case dot-separated semantic identifiers, for example paperboy.livetile.status."));
        }
    }

    private static void ValidateDuplicateIds(IEnumerable<string> values, string path, string code, List<WidgetValidationIssue> issues)
    {
        var duplicates = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        foreach (var duplicate in duplicates)
        {
            issues.Add(new WidgetValidationIssue("error", path, code, $"Duplicate id '{duplicate}'."));
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z0-9][a-z0-9-]*)+$", RegexOptions.Compiled)]
    private static partial Regex WidgetIdRegex();
}
