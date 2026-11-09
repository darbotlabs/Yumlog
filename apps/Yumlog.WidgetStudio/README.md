# Yumlog.WidgetStudio

Widget Studio is the Phase 6 foundation for adaptive LiveTile widgets in the Yumlog APIM Symphony. It defines a buildable authoring surface for widget ids, data sources, ACP actions, APIM operation bindings, render schemas, refresh cadence, preview rendering, validation, and package export.

Schema `1.1` adds a versioned **capability contract** so widgets can declare extension points for tools, skills, workflows, connectors, plugins, extensions, MCP/API surfaces, and continuous AI loops without breaking `1.0` manifests.

This is a command-line authoring tool, not a visual editor, widget runtime, or
plugin host. Previews are static HTML generated from manifest/sample data.
ACP/APIM bindings, permissions, activation modes, and continuous AI loops are
declarations only: these commands do not call endpoints, launch capabilities,
install widgets, or schedule refreshes.

## Prerequisites

- .NET 11 SDK supporting `net11.0` (currently preview); .NET 10 alone cannot build it.
- A compatible .NET 11 runtime for framework-dependent output. Unlike APIM,
  this project has no Windows-specific target or ASP.NET Core dependency.
- PowerShell for the examples below, run from the **repository root**. No
  elevation, running APIM service, Windows AI/OCR, or FFmpeg is required.

## Commands

```powershell
dotnet build .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release
dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- validate .\apps\Yumlog.WidgetStudio\Samples\paperboy-livetile.widget.json
dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- preview .\apps\Yumlog.WidgetStudio\Samples\paperboy-livetile.widget.json .\dist\widgetstudio\paperboy-livetile.preview.html
dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- capabilities .\apps\Yumlog.WidgetStudio\Samples\paperboy-capability-mesh.widget.json
dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- package .\apps\Yumlog.WidgetStudio\Samples\paperboy-livetile.widget.json .\dist\widgetstudio\paperboy.livetile.apim-symphony.widget.zip
dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- self-test
```

`validate` accepts `--json`. `capabilities` accepts `--json`, `--kind <kind>`
(case-insensitive), and `--out <index.json>`. It can emit an index even when
validation fails, so check the exit code before consuming it. `preview` and
`package` reject invalid projects before writing output. When their output path
is omitted, output is placed beside the input manifest. Existing output files
may be overwritten.

`self-test` writes two ZIP packages and two HTML previews under
`.\dist\widgetstudio-selftest`; override with `--work-dir <path>`.
Exit codes are `0` for success, `2` for validation/self-test failures, and `1`
for command errors or exceptions. The self-test covers contract negotiation,
diagnostics, package entries, and previews, not live integrations.

## Contract files

- `Schemas\widget-studio.project.schema.json`: Draft 2020-12 JSON schema for project manifests (`schemaVersion` `1.0` or `1.1`).
- `Schemas\widget-studio.capability.schema.json`: Draft 2020-12 JSON schema for a single capability contract, referenced by the project schema and consumable standalone by plugin hosts.
- `Samples\paperboy-livetile.widget.json`: Paperboy LiveTile seed manifest (`schemaVersion 1.0`) with APIM and ACP bindings.
- `Samples\paperboy-capability-mesh.widget.json`: `schemaVersion 1.1` seed exercising every core capability kind plus vendor extensions.
- `Domain\WidgetStudioModel.cs`: C# domain model for authoring and package handoff.
- `Domain\WidgetCapabilityModel.cs`: capability contract records, host-negotiation constants, and the `capabilities/index.json` projection.
- `Validation\WidgetStudioValidator.cs`: Semantic validator for identifiers, references, APIM routes, render bindings, and refresh cadence.
- `Validation\WidgetStudioValidator.Capabilities.cs`: capability contract validation and the shared open-enum extension rule.
- `Rendering\WidgetPreviewRenderer.cs`: Self-contained HTML preview renderer.
- `Packaging\WidgetPackageExporter.cs`: `.widget.zip` export flow.
- `Testing\WidgetStudioSelfTest.cs`: contract regression suite executed by `self-test`.

## Versioning policy

| schemaVersion | Status | Adds |
| --- | --- | --- |
| `1.0` | Frozen baseline, still loads and validates unchanged | widget, data sources, ACP actions, APIM bindings, render schema, refresh cadence, package |
| `1.1` | Current authoring version | `widget.capabilities[]`, project `extensions` bag, `package.includeCapabilityIndex` |

Rules:

- Both versions are accepted by `validate`, `preview`, `capabilities`, and `package`; an unknown version is `schemaVersion.unsupported`.
- The CLI rejects `1.0` manifests with nonempty `capabilities` or `extensions` (`capability.schemaVersion.unsupported`, `extensions.schemaVersion.unsupported`). Empty collections do not trigger those semantic checks.
- `1.0` packages keep their original entry set: no `capabilities/index.json` and no capability schema.
- The project version (`schemaVersion`), each capability's `contractVersion`,
  package format (`yumlog.widget.v1+zip`), and application version are distinct.
  Capability contract major `1` is supported; major `0` warns, larger majors fail.

The CLI deserializes the C# model and runs semantic validation; it does not run
a general JSON Schema validator. The published Draft 2020-12 schemas are
separate contracts for external consumers. For example, the CLI permits
comments/trailing commas and defaults an omitted `schemaVersion` to `1.1`.
Use an explicit version in authored manifests and do not treat CLI success as
proof that every JSON Schema constraint was checked.

## Capability contract

Each entry in `widget.capabilities[]` is validated against the capability contract:

| Field | Contract |
| --- | --- |
| `id` | lower-case dot-separated semantic id, unique, may not collide with a data source, ACP action, or APIM binding id |
| `kind` | `tool`, `skill`, `workflow`, `connector`, `plugin`, `extension`, `mcpServer`, `apiSurface`, `continuousAi`, or an `x-` vendor kind |
| `contractVersion` | `major.minor[.patch]`; a major above the host's `SupportedContractMajor` (currently `1`) is rejected |
| `transport` | `inProcess`, `stdio`, `http`, `mcp`, `acp`, `apim`, `event`, `file`, or an `x-` vendor transport |
| `entryPoint` | required for every non-`inProcess` transport; must be an absolute URI/URN for `http`, `mcp`, `acp`, `apim` |
| `permissions` | subset of `read`, `invoke`, `write`, `admin` |
| `requires` | ids of other capabilities, data sources, ACP actions, or APIM bindings; self-references and cycles are rejected |
| `stability` | `experimental`, `preview`, `stable`, `deprecated` (deprecated warns and should set `replacedBy`) |
| `activation` | `manual`, `onLoad`, `interval`, `event`, `continuous`; `continuousAi` requires `interval`, `event`, or `continuous` |
| `config` | free-form JSON metadata retained for a future capability host |

### Extension escape hatch

Widget kinds, data source types, render layouts, capability kinds, and capability transports are **open enums**: core values pass, `x-` prefixed vendor values raise a `*.vendorExtension` warning but stay valid, anything else is an error. Project-level `extensions` keys must match `^x-[a-z0-9]+([.-][a-z0-9]+)*$`.

## Handoff

Every `.widget.zip` contains `widget.json` (the original input file),
`validation-report.json`, and `package-manifest.json`.
`package.includeSchemas` and `package.includePreview` control
`schemas/widget-studio.project.schema.json` and `preview/index.html`
(both default to `true`). Nonempty capability lists additionally enable
`schemas/widget-studio.capability.schema.json` when schemas are included, and
`capabilities/index.json` when `package.includeCapabilityIndex` is `true`
(the default). The index uses `urn:yumlog:widget:capability-index:v1` and
contains the capability metadata, per-kind counts, and supported contract major
for downstream host negotiation; it is not an executable dependency resolver.

Verification agents should run `self-test` and require a valid validation report before promotion. `self-test` fails with exit code `2` when any contract check regresses.
