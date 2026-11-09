using System.Text.RegularExpressions;
using Yumlog.WidgetStudio.Domain;

namespace Yumlog.WidgetStudio.Validation;

/// <summary>
/// Capability contract validation: kind/transport negotiation, contract version support,
/// least-privilege permissions, dependency resolution, and activation shape for
/// tools, skills, workflows, connectors, plugins, extensions, MCP/API, and continuous AI.
/// </summary>
public static partial class WidgetStudioValidator
{
    private static void ValidateCapabilities(
        WidgetStudioProject project,
        HashSet<string> dataSourceIds,
        HashSet<string> acpActionIds,
        HashSet<string> apimBindingIds,
        List<WidgetValidationIssue> issues)
    {
        var capabilities = project.Widget.Capabilities;
        if (capabilities.Length == 0)
        {
            return;
        }

        if (!WidgetStudioSchema.SupportsCapabilities(project.SchemaVersion))
        {
            issues.Add(new WidgetValidationIssue(
                "error",
                "$.widget.capabilities",
                "capability.schemaVersion.unsupported",
                $"Capability contracts require schemaVersion {WidgetStudioSchema.CapabilityVersion} or later."));
        }

        ValidateDuplicateIds(capabilities.Select(capability => capability.Id), "$.widget.capabilities", "capability.id.duplicate", issues);

        var capabilityIds = capabilities
            .Select(capability => capability.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var knownReferences = new HashSet<string>(capabilityIds, StringComparer.OrdinalIgnoreCase);
        knownReferences.UnionWith(dataSourceIds);
        knownReferences.UnionWith(acpActionIds);
        knownReferences.UnionWith(apimBindingIds);

        foreach (var collision in capabilityIds.Where(id => dataSourceIds.Contains(id) || acpActionIds.Contains(id) || apimBindingIds.Contains(id)))
        {
            issues.Add(new WidgetValidationIssue(
                "error",
                "$.widget.capabilities",
                "capability.id.collision",
                $"Capability id '{collision}' collides with a data source, ACP action, or APIM binding id and would make requirement references ambiguous."));
        }

        for (var i = 0; i < capabilities.Length; i++)
        {
            ValidateCapability(capabilities[i], i, knownReferences, issues);
        }

        ValidateCapabilityCycles(capabilities, capabilityIds, issues);
    }

    private static void ValidateCapability(
        WidgetCapability capability,
        int index,
        HashSet<string> knownReferences,
        List<WidgetValidationIssue> issues)
    {
        var path = $"$.widget.capabilities[{index}]";

        ValidateId(capability.Id, $"{path}.id", issues);
        Required(capability.DisplayName, $"{path}.displayName", "capability.displayName.required", issues);
        ValidateOpenEnum(capability.Kind, WidgetCapabilityContract.Kinds, $"{path}.kind", "capability.kind", "capability kind", issues);
        ValidateOpenEnum(capability.Transport, WidgetCapabilityContract.Transports, $"{path}.transport", "capability.transport", "capability transport", issues);

        if (string.IsNullOrWhiteSpace(capability.Description))
        {
            Warn($"{path}.description", "capability.description.recommended", "A description improves agent discovery of this capability.");
        }

        if (string.IsNullOrWhiteSpace(capability.Provider))
        {
            Warn($"{path}.provider", "capability.provider.recommended", "Declare the providing component so hosts can attribute the capability.");
        }

        ValidateContractVersion(capability, path, issues);
        ValidatePermissions(capability, path, issues);
        ValidateEntryPoint(capability, path, issues);
        ValidateStability(capability, path, issues);
        ValidateCapabilitySchemas(capability, path, issues);
        ValidateActivation(capability, path, issues);

        for (var i = 0; i < capability.Requires.Length; i++)
        {
            var requirement = capability.Requires[i];
            var requirePath = $"{path}.requires[{i}]";

            if (string.IsNullOrWhiteSpace(requirement))
            {
                Error(requirePath, "capability.requires.empty", "Requirement ids cannot be empty.");
                continue;
            }

            if (string.Equals(requirement, capability.Id, StringComparison.OrdinalIgnoreCase))
            {
                Error(requirePath, "capability.requires.selfReference", "A capability cannot require itself.");
                continue;
            }

            if (!knownReferences.Contains(requirement))
            {
                Error(requirePath, "capability.requires.missing", $"Requirement '{requirement}' must reference a capability, data source, ACP action, or APIM binding id.");
            }
        }

        void Error(string issuePath, string code, string message) => issues.Add(new WidgetValidationIssue("error", issuePath, code, message));
        void Warn(string issuePath, string code, string message) => issues.Add(new WidgetValidationIssue("warning", issuePath, code, message));
    }

    private static void ValidateContractVersion(WidgetCapability capability, string path, List<WidgetValidationIssue> issues)
    {
        var versionPath = $"{path}.contractVersion";
        Required(capability.ContractVersion, versionPath, "capability.contractVersion.required", issues);

        if (string.IsNullOrWhiteSpace(capability.ContractVersion))
        {
            return;
        }

        if (!WidgetCapabilityContract.TryParseContractVersion(capability.ContractVersion, out var major, out _))
        {
            issues.Add(new WidgetValidationIssue("error", versionPath, "capability.contractVersion.invalid", "contractVersion must use major.minor or major.minor.patch form."));
            return;
        }

        if (major > WidgetCapabilityContract.SupportedContractMajor)
        {
            issues.Add(new WidgetValidationIssue(
                "error",
                versionPath,
                "capability.contractVersion.unsupported",
                $"Capability contract major {major} exceeds the supported major {WidgetCapabilityContract.SupportedContractMajor}."));
        }
        else if (major < WidgetCapabilityContract.SupportedContractMajor)
        {
            issues.Add(new WidgetValidationIssue(
                "warning",
                versionPath,
                "capability.contractVersion.prerelease",
                $"Capability contract major {major} is pre-release; hosts may reject it in a future increment."));
        }
    }

    private static void ValidatePermissions(WidgetCapability capability, string path, List<WidgetValidationIssue> issues)
    {
        if (capability.Permissions.Length == 0)
        {
            issues.Add(new WidgetValidationIssue("warning", $"{path}.permissions", "capability.permissions.recommended", "Declare least-privilege permissions so hosts can gate invocation."));
            return;
        }

        for (var i = 0; i < capability.Permissions.Length; i++)
        {
            var permission = capability.Permissions[i];
            if (!SupportedPermissions.Contains(permission))
            {
                issues.Add(new WidgetValidationIssue("error", $"{path}.permissions[{i}]", "capability.permission.unsupported", $"Unsupported permission '{permission}'."));
            }
        }
    }

    private static void ValidateEntryPoint(WidgetCapability capability, string path, List<WidgetValidationIssue> issues)
    {
        var entryPointPath = $"{path}.entryPoint";
        var isInProcess = string.Equals(capability.Transport, "inProcess", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(capability.EntryPoint))
        {
            if (!isInProcess)
            {
                issues.Add(new WidgetValidationIssue("error", entryPointPath, "capability.entryPoint.required", $"Transport '{capability.Transport}' requires an entryPoint."));
            }

            return;
        }

        if (WidgetCapabilityContract.RemoteTransports.Contains(capability.Transport)
            && !Uri.TryCreate(capability.EntryPoint, UriKind.Absolute, out _))
        {
            issues.Add(new WidgetValidationIssue("error", entryPointPath, "capability.entryPoint.invalid", $"Transport '{capability.Transport}' requires an absolute URI or URN entryPoint."));
        }
    }

    private static void ValidateStability(WidgetCapability capability, string path, List<WidgetValidationIssue> issues)
    {
        var stabilityPath = $"{path}.stability";

        if (!WidgetCapabilityContract.Stabilities.Contains(capability.Stability))
        {
            issues.Add(new WidgetValidationIssue("error", stabilityPath, "capability.stability.unsupported", $"Unsupported stability '{capability.Stability}'."));
            return;
        }

        if (!string.Equals(capability.Stability, "deprecated", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(capability.ReplacedBy))
            {
                issues.Add(new WidgetValidationIssue("warning", $"{path}.replacedBy", "capability.replacedBy.unexpected", "replacedBy is only meaningful for deprecated capabilities."));
            }

            return;
        }

        issues.Add(new WidgetValidationIssue("warning", stabilityPath, "capability.stability.deprecated", $"Capability '{capability.Id}' is deprecated and should be removed before promotion."));

        if (string.IsNullOrWhiteSpace(capability.ReplacedBy))
        {
            issues.Add(new WidgetValidationIssue("warning", $"{path}.replacedBy", "capability.replacedBy.recommended", "Deprecated capabilities should declare a replacement id."));
        }
    }

    private static void ValidateCapabilitySchemas(WidgetCapability capability, string path, List<WidgetValidationIssue> issues)
    {
        if (!WidgetCapabilityContract.SchemaBearingKinds.Contains(capability.Kind))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(capability.InputSchemaUri))
        {
            issues.Add(new WidgetValidationIssue("warning", $"{path}.inputSchemaUri", "capability.inputSchemaUri.recommended", $"Capability kind '{capability.Kind}' should declare an input schema URI."));
        }

        if (string.IsNullOrWhiteSpace(capability.OutputSchemaUri))
        {
            issues.Add(new WidgetValidationIssue("warning", $"{path}.outputSchemaUri", "capability.outputSchemaUri.recommended", $"Capability kind '{capability.Kind}' should declare an output schema URI."));
        }
    }

    private static void ValidateActivation(WidgetCapability capability, string path, List<WidgetValidationIssue> issues)
    {
        var activation = capability.Activation;
        var activationPath = $"{path}.activation";

        Required(activation.Mode, $"{activationPath}.mode", "capability.activation.mode.required", issues);

        if (string.IsNullOrWhiteSpace(activation.Mode))
        {
            return;
        }

        if (!WidgetCapabilityContract.ActivationModes.Contains(activation.Mode))
        {
            issues.Add(new WidgetValidationIssue("error", $"{activationPath}.mode", "capability.activation.mode.unsupported", $"Unsupported activation mode '{activation.Mode}'."));
            return;
        }

        var isInterval = string.Equals(activation.Mode, "interval", StringComparison.OrdinalIgnoreCase);
        var isEvent = string.Equals(activation.Mode, "event", StringComparison.OrdinalIgnoreCase);
        var isContinuous = string.Equals(activation.Mode, "continuous", StringComparison.OrdinalIgnoreCase);

        if (isInterval && (activation.IntervalSeconds < 5 || activation.IntervalSeconds > 86400))
        {
            issues.Add(new WidgetValidationIssue("error", $"{activationPath}.intervalSeconds", "capability.activation.intervalSeconds.range", "Interval activation requires intervalSeconds between 5 and 86400."));
        }

        if (isEvent && string.IsNullOrWhiteSpace(activation.EventTopic))
        {
            issues.Add(new WidgetValidationIssue("error", $"{activationPath}.eventTopic", "capability.activation.eventTopic.required", "Event activation requires an event topic."));
        }

        if (isContinuous && activation.IntervalSeconds <= 0 && string.IsNullOrWhiteSpace(activation.EventTopic) && string.IsNullOrWhiteSpace(activation.Schedule))
        {
            issues.Add(new WidgetValidationIssue("error", activationPath, "capability.activation.continuous.incomplete", "Continuous activation requires intervalSeconds, an event topic, or a schedule."));
        }

        if (string.Equals(capability.Kind, "continuousAi", StringComparison.OrdinalIgnoreCase) && !(isInterval || isEvent || isContinuous))
        {
            issues.Add(new WidgetValidationIssue("error", $"{activationPath}.mode", "capability.continuousAi.activation", "continuousAi capabilities require interval, event, or continuous activation."));
        }
    }

    private static void ValidateCapabilityCycles(
        WidgetCapability[] capabilities,
        HashSet<string> capabilityIds,
        List<WidgetValidationIssue> issues)
    {
        var edges = capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability.Id))
            .GroupBy(capability => capability.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(capability => capability.Requires)
                    .Where(requirement => capabilityIds.Contains(requirement))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in edges.Keys)
        {
            Walk(id);
        }

        void Walk(string id)
        {
            if (visited.Contains(id))
            {
                return;
            }

            if (!visiting.Add(id))
            {
                if (reported.Add(id))
                {
                    issues.Add(new WidgetValidationIssue("error", "$.widget.capabilities", "capability.requires.cycle", $"Capability requirement cycle detected at '{id}'."));
                }

                return;
            }

            foreach (var next in edges.TryGetValue(id, out var requirements) ? requirements : [])
            {
                Walk(next);
            }

            visiting.Remove(id);
            visited.Add(id);
        }
    }

    private static void ValidateExtensions(
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> extensions,
        string schemaVersion,
        string path,
        List<WidgetValidationIssue> issues)
    {
        if (extensions.Count == 0)
        {
            return;
        }

        if (!WidgetStudioSchema.SupportsCapabilities(schemaVersion))
        {
            issues.Add(new WidgetValidationIssue(
                "error",
                path,
                "extensions.schemaVersion.unsupported",
                $"Vendor extension bags require schemaVersion {WidgetStudioSchema.CapabilityVersion} or later."));
        }

        foreach (var key in extensions.Keys)
        {
            if (!ExtensionKeyRegex().IsMatch(key))
            {
                issues.Add(new WidgetValidationIssue(
                    "error",
                    $"{path}['{key}']",
                    "extensions.key.invalid",
                    "Extension keys must be namespaced, for example x-yumlog.mdkg."));
            }
        }
    }

    /// <summary>
    /// Open enum check: known values pass, <c>x-</c> prefixed vendor values warn, everything else errors.
    /// This is the extension escape hatch for widget kinds, data source types, layouts, and capability contracts.
    /// </summary>
    private static void ValidateOpenEnum(
        string value,
        IReadOnlySet<string> known,
        string path,
        string codePrefix,
        string label,
        List<WidgetValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new WidgetValidationIssue("error", path, $"{codePrefix}.required", "Value is required."));
            return;
        }

        if (known.Contains(value))
        {
            return;
        }

        if (WidgetCapabilityContract.IsVendorExtension(value))
        {
            issues.Add(new WidgetValidationIssue("warning", path, $"{codePrefix}.vendorExtension", $"Vendor {label} '{value}' is outside the core contract; hosts may ignore it."));
            return;
        }

        issues.Add(new WidgetValidationIssue("error", path, $"{codePrefix}.unsupported", $"Unsupported {label} '{value}'. Use a core value or an 'x-' prefixed vendor extension."));
    }

    [GeneratedRegex("^x-[a-z0-9]+([.-][a-z0-9]+)*$", RegexOptions.Compiled)]
    private static partial Regex ExtensionKeyRegex();
}
