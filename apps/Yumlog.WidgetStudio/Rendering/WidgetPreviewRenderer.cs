using System.Net;
using System.Text;
using System.Text.Json;
using Yumlog.WidgetStudio.Domain;
using Yumlog.WidgetStudio.Validation;

namespace Yumlog.WidgetStudio.Rendering;

public static class WidgetPreviewRenderer
{
    public static string Render(WidgetStudioProject project, WidgetValidationReport? report = null)
    {
        var widget = project.Widget;
        var validation = report ?? WidgetStudioValidator.Validate(project);
        var builder = new StringBuilder();

        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("  <meta charset=\"utf-8\" />");
        builder.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        builder.AppendLine($"  <title>{Encode(widget.DisplayName)} Preview</title>");
        builder.AppendLine("  <style>");
        builder.AppendLine("    :root { color-scheme: dark; --bg: #0f172a; --panel: #111827; --tile: #1f2937; --accent: #7c3aed; --text: #e5e7eb; --muted: #9ca3af; --ok: #22c55e; --warn: #f59e0b; --error: #ef4444; }");
        builder.AppendLine("    body { margin: 0; font-family: Segoe UI, Arial, sans-serif; background: linear-gradient(135deg, #0f172a, #111827); color: var(--text); }");
        builder.AppendLine("    main { max-width: 1120px; margin: 0 auto; padding: 32px; }");
        builder.AppendLine("    .tile { border: 1px solid rgba(255,255,255,.12); border-radius: 24px; background: rgba(31,41,55,.88); box-shadow: 0 24px 72px rgba(0,0,0,.35); overflow: hidden; }");
        builder.AppendLine("    .hero { padding: 28px; background: radial-gradient(circle at top right, rgba(124,58,237,.48), transparent 34%), var(--tile); }");
        builder.AppendLine("    .badge { display: inline-flex; gap: 8px; align-items: center; padding: 6px 10px; border-radius: 999px; background: rgba(124,58,237,.22); color: #ddd6fe; font-size: 12px; text-transform: uppercase; letter-spacing: .08em; }");
        builder.AppendLine("    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 16px; padding: 24px; }");
        builder.AppendLine("    section, .card { border: 1px solid rgba(255,255,255,.10); border-radius: 18px; background: rgba(17,24,39,.84); padding: 18px; }");
        builder.AppendLine("    h1, h2, h3 { margin: 0 0 10px; } p { color: var(--muted); line-height: 1.5; } code, pre { background: rgba(15,23,42,.9); border-radius: 12px; } pre { overflow: auto; padding: 14px; color: #c4b5fd; }");
        builder.AppendLine("    ul { margin: 0; padding-left: 20px; } li { margin: 6px 0; } .ok { color: var(--ok); } .warning { color: var(--warn); } .error { color: var(--error); }");
        builder.AppendLine("  </style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("  <main>");
        builder.AppendLine("    <article class=\"tile\">");
        builder.AppendLine("      <div class=\"hero\">");
        builder.AppendLine($"        <span class=\"badge\">{Encode(widget.Kind)} · {Encode(widget.RefreshCadence.Mode)}</span>");
        builder.AppendLine($"        <h1>{Encode(widget.DisplayName)}</h1>");
        builder.AppendLine($"        <p>{Encode(widget.Description)}</p>");
        builder.AppendLine($"        <p><strong>Widget id:</strong> <code>{Encode(widget.Id)}</code></p>");
        builder.AppendLine("      </div>");
        builder.AppendLine("      <div class=\"grid\">");
        builder.AppendLine(RenderList("Data sources", widget.DataSources.Select(source => $"{source.Id} ({source.Type}) -> {source.BindingRef}")));
        builder.AppendLine(RenderList("ACP actions", widget.AcpActions.Select(action => $"{action.Id} · {action.Capability} · {action.Permission}")));
        builder.AppendLine(RenderList("APIM bindings", widget.ApimBindings.Select(binding => $"{binding.Method} {binding.Route} · {binding.OperationId}")));
        builder.AppendLine(RenderList("Render states", widget.Render.States.Select(state => $"{state.Id} · {state.Title} · {state.Intent}")));
        builder.AppendLine("      </div>");

        var capabilitiesMarkup = RenderCapabilities(widget.Capabilities);
        if (capabilitiesMarkup.Length > 0)
        {
            builder.AppendLine(capabilitiesMarkup);
        }

        builder.AppendLine("      <div class=\"grid\">");
        builder.AppendLine("        <section>");
        builder.AppendLine("          <h2>Refresh cadence</h2>");
        builder.AppendLine($"          <p>Mode <code>{Encode(widget.RefreshCadence.Mode)}</code>, interval <code>{widget.RefreshCadence.IntervalSeconds}s</code>, stale after <code>{widget.RefreshCadence.StaleAfterSeconds}s</code>.</p>");
        builder.AppendLine("        </section>");
        builder.AppendLine("        <section>");
        builder.AppendLine("          <h2>Validation</h2>");
        builder.AppendLine($"          <p class=\"{(validation.IsValid ? "ok" : "error")}\">{(validation.IsValid ? "Ready for package handoff." : "Blocked until errors are fixed.")}</p>");
        builder.AppendLine(RenderIssues(validation.Issues));
        builder.AppendLine("        </section>");
        builder.AppendLine("      </div>");
        builder.AppendLine("      <div class=\"grid\">");
        builder.AppendLine("        <section>");
        builder.AppendLine("          <h2>Sample payload</h2>");
        builder.AppendLine($"          <pre>{Encode(FirstSamplePayload(widget.DataSources))}</pre>");
        builder.AppendLine("        </section>");
        builder.AppendLine("        <section>");
        builder.AppendLine("          <h2>Render bindings</h2>");
        builder.AppendLine(RenderList("Bindings", widget.Render.Bindings.Select(binding => $"{binding.Id}: {binding.SourceId}{binding.FieldPath} -> {binding.Target} ({binding.Format})")));
        builder.AppendLine("        </section>");
        builder.AppendLine("      </div>");
        builder.AppendLine("    </article>");
        builder.AppendLine("  </main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder.ToString();
    }

    private static string RenderList(string title, IEnumerable<string> rows)
    {
        var items = rows.Select(row => $"<li>{Encode(row)}</li>").ToArray();
        return $"""
               <section>
                 <h2>{Encode(title)}</h2>
                 <ul>{string.Join("", items)}</ul>
               </section>
               """;
    }

    private static string RenderCapabilities(IReadOnlyList<WidgetCapability> capabilities)
    {
        if (capabilities.Count == 0)
        {
            return string.Empty;
        }

        var groups = capabilities
            .GroupBy(capability => capability.Kind, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => RenderList(
                $"Capabilities · {group.Key}",
                group.Select(capability =>
                    $"{capability.Id} v{capability.ContractVersion} · {capability.Transport} · {capability.Stability} · {(capability.Permissions.Length == 0 ? "no-permissions" : string.Join("/", capability.Permissions))} · {capability.Activation.Mode}")))
            .ToArray();

        return $"""
               <div class="grid">
               {string.Join(Environment.NewLine, groups)}
               </div>
               """;
    }

    private static string RenderIssues(IEnumerable<WidgetValidationIssue> issues)
    {
        var rows = issues.Select(issue => $"<li class=\"{Encode(issue.Severity)}\">[{Encode(issue.Severity)}] {Encode(issue.Path)} {Encode(issue.Message)}</li>").ToArray();
        return rows.Length == 0 ? "<p>No validation issues.</p>" : $"<ul>{string.Join("", rows)}</ul>";
    }

    private static string FirstSamplePayload(IEnumerable<WidgetDataSource> sources)
    {
        var sample = sources.FirstOrDefault(source => source.SamplePayload.HasValue)?.SamplePayload;
        return sample.HasValue
            ? JsonSerializer.Serialize(sample.Value, new JsonSerializerOptions { WriteIndented = true })
            : "{}";
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
