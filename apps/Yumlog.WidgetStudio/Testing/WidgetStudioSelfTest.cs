using System.Text;
using System.Text.Json;
using Yumlog.WidgetStudio.Domain;
using Yumlog.WidgetStudio.Packaging;
using Yumlog.WidgetStudio.Rendering;
using Yumlog.WidgetStudio.Validation;

namespace Yumlog.WidgetStudio.Testing;

public sealed record SelfTestCheck(string Name, bool Passed, string Detail);

public sealed record SelfTestResult(IReadOnlyList<SelfTestCheck> Checks, IReadOnlyList<string> Artifacts)
{
    public bool Passed => Checks.All(check => check.Passed);
    public int FailedCount => Checks.Count(check => !check.Passed);
}

/// <summary>
/// Contract regression suite for the Widget Studio authoring surface. Covers schema version
/// negotiation, capability contract validation, backward compatibility of 1.0 manifests,
/// package entry shape, and preview rendering.
/// </summary>
public static class WidgetStudioSelfTest
{
    private const string BaselineSample = "paperboy-livetile.widget.json";
    private const string CapabilitySample = "paperboy-capability-mesh.widget.json";
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static async Task<SelfTestResult> RunAsync(
        string samplesDirectory,
        string schemasDirectory,
        string artifactsDirectory,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<SelfTestCheck>();
        var artifacts = new List<string>();
        Directory.CreateDirectory(artifactsDirectory);

        var projectSchemaPath = Path.Combine(schemasDirectory, "widget-studio.project.schema.json");
        var capabilitySchemaPath = Path.Combine(schemasDirectory, "widget-studio.capability.schema.json");
        var schemaPaths = Directory
            .GetFiles(schemasDirectory, "*.json")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();

        CheckSchemaFiles(projectSchemaPath, capabilitySchemaPath, checks);

        var baselineProject = WidgetStudioValidator.Load(Path.Combine(samplesDirectory, BaselineSample));
        var baselineReport = WidgetStudioValidator.Validate(baselineProject);
        var capabilityProject = WidgetStudioValidator.Load(Path.Combine(samplesDirectory, CapabilitySample));
        var capabilityReport = WidgetStudioValidator.Validate(capabilityProject);

        Add(checks, "baseline.sample.version", string.Equals(baselineProject.SchemaVersion, WidgetStudioSchema.BaselineVersion, StringComparison.Ordinal),
            $"schemaVersion={baselineProject.SchemaVersion}");
        Add(checks, "baseline.sample.valid", baselineReport.IsValid, FormatErrors(baselineReport));
        Add(checks, "baseline.sample.noCapabilityIssues",
            !baselineReport.Issues.Any(issue => issue.Code.StartsWith("capability.", StringComparison.Ordinal) || issue.Code.StartsWith("extensions.", StringComparison.Ordinal)),
            "1.0 manifests must not gain capability or extension diagnostics");

        Add(checks, "capability.sample.version", string.Equals(capabilityProject.SchemaVersion, WidgetStudioSchema.CapabilityVersion, StringComparison.Ordinal),
            $"schemaVersion={capabilityProject.SchemaVersion}");
        Add(checks, "capability.sample.valid", capabilityReport.IsValid, FormatErrors(capabilityReport));

        CheckCapabilityIndex(capabilityProject, checks);
        CheckVersionNegotiation(capabilityProject, checks);
        CheckCapabilityDiagnostics(capabilityProject, checks);
        CheckExtensionBag(capabilityProject, checks);

        await CheckPackagesAsync(
            samplesDirectory,
            schemaPaths,
            artifactsDirectory,
            baselineProject,
            capabilityProject,
            checks,
            artifacts,
            cancellationToken);

        CheckPreviews(artifactsDirectory, baselineProject, baselineReport, capabilityProject, capabilityReport, checks, artifacts);

        return new SelfTestResult(checks, artifacts);
    }

    private static void CheckSchemaFiles(string projectSchemaPath, string capabilitySchemaPath, List<SelfTestCheck> checks)
    {
        using var projectSchema = JsonDocument.Parse(File.ReadAllText(projectSchemaPath));
        var projectId = projectSchema.RootElement.TryGetProperty("$id", out var id) ? id.GetString() : null;
        Add(checks, "schema.project.id", string.Equals(projectId, WidgetStudioSchema.ProjectSchemaId, StringComparison.Ordinal), projectId ?? "<missing>");

        var declaredVersions = projectSchema.RootElement
            .GetProperty("properties")
            .GetProperty("schemaVersion")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();

        Add(checks, "schema.project.versionEnum",
            WidgetStudioSchema.SupportedVersions.All(version => declaredVersions.Contains(version, StringComparer.Ordinal)),
            string.Join(", ", declaredVersions));

        if (!File.Exists(capabilitySchemaPath))
        {
            Add(checks, "schema.capability.id", false, "capability schema file is missing");
            return;
        }

        using var capabilitySchema = JsonDocument.Parse(File.ReadAllText(capabilitySchemaPath));
        var capabilityId = capabilitySchema.RootElement.TryGetProperty("$id", out var capId) ? capId.GetString() : null;
        Add(checks, "schema.capability.id", string.Equals(capabilityId, WidgetCapabilityContract.ContractSchemaId, StringComparison.Ordinal), capabilityId ?? "<missing>");
    }

    private static void CheckCapabilityIndex(WidgetStudioProject capabilityProject, List<SelfTestCheck> checks)
    {
        var index = WidgetCapabilityIndex.Create(capabilityProject, DateTimeOffset.UnixEpoch);

        Add(checks, "capabilityIndex.entries", index.Capabilities.Count == capabilityProject.Widget.Capabilities.Length,
            $"{index.Capabilities.Count} entries");

        var missingKinds = WidgetCapabilityContract.Kinds
            .Where(kind => !index.CountsByKind.ContainsKey(kind))
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .ToArray();
        Add(checks, "capabilityIndex.coversCoreKinds", missingKinds.Length == 0,
            missingKinds.Length == 0 ? "all core kinds present" : $"missing: {string.Join(", ", missingKinds)}");

        Add(checks, "capabilityIndex.vendorFlag", index.Capabilities.Any(entry => entry.VendorExtension),
            "at least one x- prefixed vendor capability is flagged");

        Add(checks, "capabilityIndex.schemaUri", string.Equals(index.SchemaUri, WidgetCapabilityContract.IndexSchemaUri, StringComparison.Ordinal), index.SchemaUri);

        var json = JsonSerializer.Serialize(index, WidgetStudioJson.Options);
        Add(checks, "capabilityIndex.serializable", json.Contains("\"capabilities\"", StringComparison.Ordinal), $"{json.Length} chars");
    }

    private static void CheckVersionNegotiation(WidgetStudioProject capabilityProject, List<SelfTestCheck> checks)
    {
        var downgraded = capabilityProject with { SchemaVersion = WidgetStudioSchema.BaselineVersion };
        var downgradedReport = WidgetStudioValidator.Validate(downgraded);
        Add(checks, "version.capabilitiesGatedTo1_1", HasCode(downgradedReport, "capability.schemaVersion.unsupported"), FormatCodes(downgradedReport));

        var unknownVersion = capabilityProject with { SchemaVersion = "9.9" };
        Add(checks, "version.unknownRejected", HasCode(WidgetStudioValidator.Validate(unknownVersion), "schemaVersion.unsupported"), "9.9 rejected");

        Add(checks, "version.supportsCapabilities",
            WidgetStudioSchema.SupportsCapabilities("1.1") && !WidgetStudioSchema.SupportsCapabilities("1.0"),
            "1.1 supports capabilities, 1.0 does not");
    }

    private static void CheckCapabilityDiagnostics(WidgetStudioProject capabilityProject, List<SelfTestCheck> checks)
    {
        var unknownKind = WithCapabilities(capabilityProject, BaseCapability() with { Kind = "notAKind" });
        Add(checks, "capability.kind.unsupported", HasCode(WidgetStudioValidator.Validate(unknownKind), "capability.kind.unsupported"), "unknown kind is an error");

        var vendorKind = WithCapabilities(capabilityProject, BaseCapability() with { Kind = "x-darbot.observer" });
        var vendorReport = WidgetStudioValidator.Validate(vendorKind);
        Add(checks, "capability.kind.vendorExtension",
            vendorReport.IsValid && HasCode(vendorReport, "capability.kind.vendorExtension"),
            "x- kinds warn but stay valid");

        var futureContract = WithCapabilities(capabilityProject, BaseCapability() with { ContractVersion = "2.0" });
        Add(checks, "capability.contractVersion.unsupported", HasCode(WidgetStudioValidator.Validate(futureContract), "capability.contractVersion.unsupported"), "major 2 rejected");

        var badContract = WithCapabilities(capabilityProject, BaseCapability() with { ContractVersion = "one" });
        Add(checks, "capability.contractVersion.invalid", HasCode(WidgetStudioValidator.Validate(badContract), "capability.contractVersion.invalid"), "non numeric rejected");

        var missingRequirement = WithCapabilities(capabilityProject, BaseCapability() with { Requires = ["cap.missing.thing"] });
        Add(checks, "capability.requires.missing", HasCode(WidgetStudioValidator.Validate(missingRequirement), "capability.requires.missing"), "unresolved requirement rejected");

        var cycle = WithCapabilities(
            capabilityProject,
            BaseCapability() with { Id = "cap.cycle.first", Requires = ["cap.cycle.second"] },
            BaseCapability() with { Id = "cap.cycle.second", Requires = ["cap.cycle.first"] });
        Add(checks, "capability.requires.cycle", HasCode(WidgetStudioValidator.Validate(cycle), "capability.requires.cycle"), "requirement cycle rejected");

        var collision = WithCapabilities(capabilityProject, BaseCapability() with { Id = capabilityProject.Widget.DataSources[0].Id });
        Add(checks, "capability.id.collision", HasCode(WidgetStudioValidator.Validate(collision), "capability.id.collision"), "capability id may not shadow a data source id");

        var missingEntryPoint = WithCapabilities(capabilityProject, BaseCapability() with { Transport = "http", EntryPoint = "" });
        Add(checks, "capability.entryPoint.required", HasCode(WidgetStudioValidator.Validate(missingEntryPoint), "capability.entryPoint.required"), "remote transports need an entry point");

        var relativeEntryPoint = WithCapabilities(capabilityProject, BaseCapability() with { Transport = "http", EntryPoint = "/relative/path" });
        Add(checks, "capability.entryPoint.invalid", HasCode(WidgetStudioValidator.Validate(relativeEntryPoint), "capability.entryPoint.invalid"), "relative entry points rejected");

        var badPermission = WithCapabilities(capabilityProject, BaseCapability() with { Permissions = ["superuser"] });
        Add(checks, "capability.permission.unsupported", HasCode(WidgetStudioValidator.Validate(badPermission), "capability.permission.unsupported"), "unknown permission rejected");

        var continuous = WithCapabilities(
            capabilityProject,
            BaseCapability() with { Kind = "continuousAi", Activation = new CapabilityActivation { Mode = "manual" } });
        Add(checks, "capability.continuousAi.activation", HasCode(WidgetStudioValidator.Validate(continuous), "capability.continuousAi.activation"), "continuous AI needs a live activation mode");

        var deprecated = WithCapabilities(capabilityProject, BaseCapability() with { Stability = "deprecated" });
        var deprecatedReport = WidgetStudioValidator.Validate(deprecated);
        Add(checks, "capability.stability.deprecated",
            deprecatedReport.IsValid && HasCode(deprecatedReport, "capability.stability.deprecated") && HasCode(deprecatedReport, "capability.replacedBy.recommended"),
            "deprecation warns without blocking");
    }

    private static void CheckExtensionBag(WidgetStudioProject capabilityProject, List<SelfTestCheck> checks)
    {
        Add(checks, "extensions.preserved", capabilityProject.Extensions.Count > 0, $"{capabilityProject.Extensions.Count} vendor extension keys");

        using var invalidKeyDocument = JsonDocument.Parse("{}");
        var invalidKey = capabilityProject with
        {
            Extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["notNamespaced"] = invalidKeyDocument.RootElement
            }
        };
        Add(checks, "extensions.key.invalid", HasCode(WidgetStudioValidator.Validate(invalidKey), "extensions.key.invalid"), "extension keys must be x- namespaced");

        var downgraded = capabilityProject with { SchemaVersion = WidgetStudioSchema.BaselineVersion };
        Add(checks, "extensions.gatedTo1_1", HasCode(WidgetStudioValidator.Validate(downgraded), "extensions.schemaVersion.unsupported"), "extension bags require 1.1");
    }

    private static async Task CheckPackagesAsync(
        string samplesDirectory,
        string[] schemaPaths,
        string artifactsDirectory,
        WidgetStudioProject baselineProject,
        WidgetStudioProject capabilityProject,
        List<SelfTestCheck> checks,
        List<string> artifacts,
        CancellationToken cancellationToken)
    {
        var baselinePackage = await WidgetPackageExporter.ExportAsync(
            baselineProject,
            Path.Combine(samplesDirectory, BaselineSample),
            schemaPaths,
            Path.Combine(artifactsDirectory, "paperboy.livetile.apim-symphony.widget.zip"),
            cancellationToken);
        artifacts.Add(baselinePackage.PackagePath);

        var capabilityPackage = await WidgetPackageExporter.ExportAsync(
            capabilityProject,
            Path.Combine(samplesDirectory, CapabilitySample),
            schemaPaths,
            Path.Combine(artifactsDirectory, "paperboy.livetile.capability-mesh.widget.zip"),
            cancellationToken);
        artifacts.Add(capabilityPackage.PackagePath);

        string[] required =
        [
            "widget.json",
            "schemas/widget-studio.project.schema.json",
            "preview/index.html",
            "validation-report.json",
            "package-manifest.json"
        ];

        Add(checks, "package.baseline.entries", required.All(entry => baselinePackage.Entries.Contains(entry, StringComparer.Ordinal)),
            string.Join(", ", baselinePackage.Entries));
        Add(checks, "package.baseline.noCapabilityIndex", !baselinePackage.Entries.Contains("capabilities/index.json", StringComparer.Ordinal),
            "1.0 packages keep their original entry set");
        Add(checks, "package.baseline.noCapabilitySchema", !baselinePackage.Entries.Contains("schemas/widget-studio.capability.schema.json", StringComparer.Ordinal),
            "1.0 packages do not ship the capability contract schema");

        Add(checks, "package.capability.entries", required.All(entry => capabilityPackage.Entries.Contains(entry, StringComparer.Ordinal)),
            string.Join(", ", capabilityPackage.Entries));
        Add(checks, "package.capability.index", capabilityPackage.Entries.Contains("capabilities/index.json", StringComparer.Ordinal),
            "capability packages publish capabilities/index.json");
        Add(checks, "package.capability.contractSchema", capabilityPackage.Entries.Contains("schemas/widget-studio.capability.schema.json", StringComparer.Ordinal),
            "capability contract schema ships with the package");
    }

    private static void CheckPreviews(
        string artifactsDirectory,
        WidgetStudioProject baselineProject,
        WidgetValidationReport baselineReport,
        WidgetStudioProject capabilityProject,
        WidgetValidationReport capabilityReport,
        List<SelfTestCheck> checks,
        List<string> artifacts)
    {
        var baselinePreview = WidgetPreviewRenderer.Render(baselineProject, baselineReport);
        var baselinePreviewPath = Path.Combine(artifactsDirectory, "paperboy-livetile.preview.html");
        File.WriteAllText(baselinePreviewPath, baselinePreview, Utf8NoBom);
        artifacts.Add(baselinePreviewPath);

        var capabilityPreview = WidgetPreviewRenderer.Render(capabilityProject, capabilityReport);
        var capabilityPreviewPath = Path.Combine(artifactsDirectory, "paperboy-capability-mesh.preview.html");
        File.WriteAllText(capabilityPreviewPath, capabilityPreview, Utf8NoBom);
        artifacts.Add(capabilityPreviewPath);

        Add(checks, "preview.baseline.noCapabilitySection", !baselinePreview.Contains("Capabilities", StringComparison.Ordinal),
            "1.0 previews are unchanged");
        Add(checks, "preview.capability.section", capabilityPreview.Contains("Capabilities", StringComparison.Ordinal),
            "1.1 previews expose capability groups");
    }

    private static WidgetStudioProject WithCapabilities(WidgetStudioProject project, params WidgetCapability[] capabilities)
        => project with { Widget = project.Widget with { Capabilities = capabilities } };

    private static WidgetCapability BaseCapability() => new()
    {
        Id = "cap.selftest.sample",
        Kind = "tool",
        DisplayName = "Self-test capability",
        Description = "Deterministic capability used by the Widget Studio self-test.",
        ContractVersion = WidgetCapabilityContract.CurrentContractVersion,
        Provider = "yumlog.widgetstudio",
        EntryPoint = "urn:yumlog:test:capability:v1",
        Transport = "inProcess",
        Stability = "stable",
        Permissions = ["invoke"],
        InputSchemaUri = "urn:yumlog:test:capability:input:v1",
        OutputSchemaUri = "urn:yumlog:test:capability:output:v1",
        Activation = new CapabilityActivation { Mode = "manual" }
    };

    private static bool HasCode(WidgetValidationReport report, string code)
        => report.Issues.Any(issue => string.Equals(issue.Code, code, StringComparison.Ordinal));

    private static string FormatErrors(WidgetValidationReport report)
    {
        var errors = report.Issues
            .Where(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase))
            .Select(issue => $"{issue.Path} {issue.Code}")
            .ToArray();

        return errors.Length == 0 ? "no errors" : string.Join("; ", errors);
    }

    private static string FormatCodes(WidgetValidationReport report)
        => string.Join(", ", report.Issues.Select(issue => issue.Code).Distinct(StringComparer.Ordinal));

    private static void Add(List<SelfTestCheck> checks, string name, bool passed, string detail)
        => checks.Add(new SelfTestCheck(name, passed, detail));
}
