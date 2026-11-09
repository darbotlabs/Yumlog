using System.Text;
using System.Text.Json;
using Yumlog.WidgetStudio.Domain;
using Yumlog.WidgetStudio.Packaging;
using Yumlog.WidgetStudio.Rendering;
using Yumlog.WidgetStudio.Testing;
using Yumlog.WidgetStudio.Validation;

try
{
    return await WidgetStudioCli.RunAsync(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[Yumlog.WidgetStudio] {ex.Message}");
    return 1;
}

internal static class WidgetStudioCli
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        return command switch
        {
            "validate" => Validate(rest),
            "preview" => Preview(rest),
            "capabilities" => Capabilities(rest),
            "package" => await PackageAsync(rest),
            "self-test" => await SelfTestAsync(rest),
            _ => Unknown(command)
        };
    }

    private static int Validate(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("validate requires a manifest path.");
            return 1;
        }

        var manifestPath = FullPath(args[0]);
        var project = WidgetStudioValidator.Load(manifestPath);
        var report = WidgetStudioValidator.Validate(project);

        if (HasFlag(args, "--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(report, WidgetStudioJson.Options));
        }
        else
        {
            PrintReport(manifestPath, report);
        }

        return report.IsValid ? 0 : 2;
    }

    private static int Preview(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("preview requires a manifest path.");
            return 1;
        }

        var manifestPath = FullPath(args[0]);
        var outputPath = FullPath(args.Length > 1 ? args[1] : Path.ChangeExtension(manifestPath, ".preview.html"));
        var project = WidgetStudioValidator.Load(manifestPath);
        var report = WidgetStudioValidator.Validate(project);
        if (!report.IsValid)
        {
            PrintReport(manifestPath, report);
            return 2;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(outputPath, WidgetPreviewRenderer.Render(project, report), Utf8NoBom);
        Console.WriteLine($"Preview written: {outputPath}");
        return 0;
    }

    private static int Capabilities(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("capabilities requires a manifest path.");
            return 1;
        }

        var manifestPath = FullPath(args[0]);
        var project = WidgetStudioValidator.Load(manifestPath);
        var report = WidgetStudioValidator.Validate(project);
        var kindFilter = FlagValue(args, "--kind");

        var index = WidgetCapabilityIndex.Create(project);
        if (!string.IsNullOrWhiteSpace(kindFilter))
        {
            var filtered = index.Capabilities
                .Where(entry => string.Equals(entry.Kind, kindFilter, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            index = index with
            {
                Capabilities = filtered,
                CountsByKind = filtered
                    .GroupBy(entry => entry.Kind, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase)
            };
        }

        var outputPath = FlagValue(args, "--out");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var resolved = FullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(resolved) ?? Directory.GetCurrentDirectory());
            File.WriteAllText(resolved, JsonSerializer.Serialize(index, WidgetStudioJson.Options), Utf8NoBom);
            Console.WriteLine($"Capability index written: {resolved}");
        }

        if (HasFlag(args, "--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(index, WidgetStudioJson.Options));
        }
        else
        {
            Console.WriteLine($"Widget: {index.WidgetId} (schemaVersion {index.ProjectSchemaVersion}, contract major {index.SupportedContractMajor})");
            Console.WriteLine($"Capabilities: {index.Capabilities.Count}");

            foreach (var entry in index.Capabilities)
            {
                var permissions = entry.Permissions.Length == 0 ? "-" : string.Join("/", entry.Permissions);
                var requires = entry.Requires.Length == 0 ? "-" : string.Join(",", entry.Requires);
                Console.WriteLine($"  {entry.Kind,-14} {entry.Id,-34} v{entry.ContractVersion,-6} {entry.Transport,-12} {entry.Stability,-12} perms={permissions} activation={entry.Activation.Mode} requires={requires}");
            }
        }

        if (!report.IsValid)
        {
            PrintReport(manifestPath, report);
            return 2;
        }

        return 0;
    }

    private static async Task<int> PackageAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("package requires a manifest path.");
            return 1;
        }

        var manifestPath = FullPath(args[0]);
        var packagePath = FullPath(args.Length > 1 ? args[1] : Path.ChangeExtension(manifestPath, ".widget.zip"));
        var project = WidgetStudioValidator.Load(manifestPath);
        var report = WidgetStudioValidator.Validate(project);
        if (!report.IsValid)
        {
            PrintReport(manifestPath, report);
            return 2;
        }

        var result = await WidgetPackageExporter.ExportAsync(project, manifestPath, ResolveSchemaPaths(), packagePath);
        Console.WriteLine($"Package written: {result.PackagePath}");
        Console.WriteLine($"Entries: {string.Join(", ", result.Entries)}");
        Console.WriteLine($"Bytes: {result.ByteLength}");
        return 0;
    }

    private static async Task<int> SelfTestAsync(string[] args)
    {
        var artifactsDir = FullPath(FlagValue(args, "--work-dir") ?? Path.Combine("dist", "widgetstudio-selftest"));
        var result = await WidgetStudioSelfTest.RunAsync(ResolveSamplesDirectory(), ResolveSchemasDirectory(), artifactsDir);

        foreach (var check in result.Checks)
        {
            Console.WriteLine($"[{(check.Passed ? "pass" : "FAIL")}] {check.Name}: {check.Detail}");
        }

        foreach (var artifact in result.Artifacts)
        {
            Console.WriteLine($"Self-test artifact: {artifact}");
        }

        Console.WriteLine($"Self-test checks: {result.Checks.Count}, failed: {result.FailedCount}");
        return result.Passed ? 0 : 2;
    }

    private static void PrintReport(string manifestPath, WidgetValidationReport report)
    {
        Console.WriteLine($"Validated: {manifestPath}");
        Console.WriteLine(report.IsValid ? "Status: valid" : "Status: invalid");

        foreach (var issue in report.Issues)
        {
            Console.WriteLine($"[{issue.Severity}] {issue.Path} {issue.Code}: {issue.Message}");
        }
    }

    private static string ResolveSchemasDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Schemas"),
            Path.Combine(Directory.GetCurrentDirectory(), "apps", "Yumlog.WidgetStudio", "Schemas")
        };

        return candidates.First(Directory.Exists);
    }

    private static string ResolveSamplesDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Samples"),
            Path.Combine(Directory.GetCurrentDirectory(), "apps", "Yumlog.WidgetStudio", "Samples")
        };

        return candidates.First(Directory.Exists);
    }

    private static string[] ResolveSchemaPaths()
        => Directory
            .GetFiles(ResolveSchemasDirectory(), "*.json")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();

    private static string? FlagValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(IEnumerable<string> args, string flag)
        => args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

    private static string FullPath(string path)
        => Path.GetFullPath(path, Directory.GetCurrentDirectory());

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        Yumlog.WidgetStudio

        Commands:
          validate <widget.json> [--json]
          preview <widget.json> [preview.html]
          capabilities <widget.json> [--json] [--kind <kind>] [--out <index.json>]
          package <widget.json> [widget.widget.zip]
          self-test [--work-dir <path>]

        The project schema models widget id, data sources, ACP actions, APIM operation bindings,
        render schema, refresh cadence, preview generation, validation, and package export.

        schemaVersion 1.0 is the frozen baseline. schemaVersion 1.1 adds the versioned capability
        contract (tools, skills, workflows, connectors, plugins, extensions, MCP/API surfaces,
        continuous AI loops) plus 'x-' namespaced vendor extension bags.
        """);
    }
}
