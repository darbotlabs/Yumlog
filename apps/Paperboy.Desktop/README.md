# Paperboy Desktop

Paperboy Desktop is a Windows-native .NET 11 WPF app for creating and handling
lightweight `.paperboy.zip` bundles. Desktop and the companion MCP host are
version **0.4.2**.

## Features

- Drag and drop files or folders into a modern Windows UI.
- Create manifest-backed `.paperboy.zip` archives.
- Choose `SmallestSize`, `Optimal`, `Fastest`, or `NoCompression`.
- Inspect bundle manifests without unpacking the payload.
- Unpack bundles to a selected folder.
- Toss bundles to an outbox folder or final destination path.
- Export composable agent-card JSON for A2A/livetile surfaces.
- Copy the MCP stdio launch command for agent integration.
- Discover `*.extension.json` capability packages for connectors, plugins,
  skills, workflows, agents, and MCP/API bridges, and invoke them from MCP.
- Open the APIM Surface layer for local endpoint status, route contracts, ACP
  resources, MCP bridge status, Foundry Local health, OCR readiness, and
  validation controls.
- Use WPF card/tile styling for showcase demos.
- Run offline Foundry Local metadata tagging for images, videos, audio, and documents.
- Switch to a full-page Foundry Local model-card layer for delivering language,
  vision, audio, and embedding model metadata as Paperboy bundles.
- Open a floating Paperboy 9bit live tile for local model chat and pipeline help.
- Preserve SHA-256 hashes, byte counts, and source metadata in
  `paperboy-manifest.json`.
- Keep Paperboy available in the notification area after closing its windows.

## Requirements and architecture

- Windows and a .NET 11 SDK supporting `net11.0-windows` (currently a preview SDK)
  are required to build. Framework-dependent desktop output needs the matching
  .NET Windows Desktop runtime; the MCP console host needs the .NET runtime.
- The desktop uses WPF plus Windows Forms `NotifyIcon`; it does not depend on
  the Windows App SDK runtime. Both projects currently have no NuGet package
  references.
- `Paperboy.Mcp` links the desktop's bundle, Foundry, PMM, card catalog,
  capability registry/invoker, and MCP server source files. It does not launch
  the desktop UI. Extension assets are copied into both projects' outputs.
- Basic bundle operations and discovery work without Foundry or APIM.
  Model operations require the `foundry` CLI, a suitable locally available model,
  and/or its running OpenAI-compatible endpoint. Video-frame sampling needs
  FFmpeg. OCR requires a separately built/configured Yumlog.Native executable.
- The APIM service and Widget Studio are separate applications. Repository-aware
  launcher buttons need the source-tree layout and PowerShell; a standalone
  desktop publish does not package these sibling projects or launcher scripts.

The main window references `Assets\WindowsAppSDK128.png` for branding, but that
image is not currently present in this source tree. Building succeeds without
it; the branding image therefore needs to be supplied separately. The legacy
Yumlog.Native `PackageMsix` target also references that image and
`Assets\WindowsAppSDK240.png`; neither is included here.

Bundle processing is local, but this is not a network sandbox: model downloads
can access the network, configurable HTTP/Foundry endpoints may be remote, and
extension invocation can run programs or make HTTP requests. Only use trusted
manifests and endpoints.

## Build

```powershell
dotnet build .\apps\Paperboy.Desktop\Paperboy.Desktop.csproj -c Release
dotnet build .\apps\Paperboy.Mcp\Paperboy.Mcp.csproj -c Release
```

Run the desktop from the repository root:

```powershell
dotnet run --project .\apps\Paperboy.Desktop\Paperboy.Desktop.csproj
```

## Publish

```powershell
dotnet publish .\apps\Paperboy.Desktop\Paperboy.Desktop.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

The published app appears under:

```text
apps\Paperboy.Desktop\bin\Release\net11.0-windows\win-x64\publish\
```

## Bundle format

Paperboy Desktop uses the same bundle format as `launchers\paperboy.ps1`:

```text
bundle.paperboy.zip
├── paperboy-manifest.json
└── payload/
    └── ...
```

The manifest includes source paths, archive paths, last-write timestamps,
lengths, and SHA-256 hashes.

## MCP server

Paperboy includes a companion console MCP host:

```powershell
.\apps\Paperboy.Mcp\bin\Release\net11.0-windows\Paperboy.Mcp.exe
```

Build first, then configure the executable above as the client's stdio command.
For development, `dotnet run --project .\apps\Paperboy.Mcp\Paperboy.Mcp.csproj`
also starts the host; avoid build output on the protocol stream when integrating
clients. Desktop output additionally supports `Paperboy.exe --mcp` without
starting its windows or tray.

The host advertises protocol version `2024-11-05` and currently requires
`Content-Length: <UTF-8 byte count>\r\n\r\n` framing, not newline-delimited JSON.
Clients must support that framing. `--verbose` logs host lifecycle messages to
stderr; `PAPERBOY_DEBUG=1` also enables server diagnostics. Closing stdin stops
the host; the console host also handles Ctrl+C.

It exposes 17 tools over stdio JSON-RPC/MCP (the five `paperboy.pmm.*` tools are
listed under Model Manager below):

- `paperboy.bundle.pack`
- `paperboy.bundle.inspect`
- `paperboy.bundle.unpack`
- `paperboy.bundle.toss`
- `paperboy.foundryLocal.analyze`
- `paperboy.foundryLocal.modelCards`
- `paperboy.cards.list`
- `paperboy.cards.schema`
- `paperboy.capabilities.list`
- `paperboy.extensions.list`
- `paperboy.extensions.describe`
- `paperboy.extensions.invoke`

It also implements `resources/list`, `resources/read`, `prompts/list`, and
`prompts/get`. Prompt names are `paperboy.bundle.analyze`,
`paperboy.foundry.strategy`, and `paperboy.livetile.plan`; these return prompt
templates rather than executing workflows.

Resources include `paperboy://cards`, `paperboy://cards/schema`, the three
extension resources listed below, and `paperboy://acp/capabilities`,
`paperboy://acp/context`, `paperboy://apim/trace`, and
`paperboy://acp/forwarding`. The four ACP/APIM resources are descriptive contracts:
they explicitly report `not-yet-connected` with forwarding disabled, not an
active MCP-to-APIM forwarding channel.

## Paperboy APIM surface

The **APIM Surface** button opens a WPF control-plane layer for the local
Paperboy gateway contract. It includes the default local endpoint
`http://127.0.0.1:8787/paperboy`, APIM route contracts, current and planned ACP
resource URIs, MCP bridge status, Foundry Local `/v1/models` health probing,
Yumlog.Native OCR bridge readiness, and a copyable route manifest.

The HTTP gateway scaffold is implemented separately in
`apps\Yumlog.APIM.Service`; the desktop does not host it. Tool forwarding and
route execution remain placeholders (`forwarded: false` / `executed: false`).
**Start**, **Stop**, and **Restart** delegate to
`launchers\Start-YumlogAPIMService.ps1` and
`launchers\Stop-YumlogAPIMService.ps1`. Install/configure that service separately
and use the permissions required by those scripts.

Desktop validation probes the configured gateway `/status` and Foundry
`/v1/models`, checks MCP output files, and resolves the OCR executable/project.
It does not execute OCR, run a full MCP round trip, or prove that every displayed
route works. The UI's `paperboy://apim/routes` and
`paperboy://validation/status` entries are gateway contract labels, not resources
served by the Paperboy MCP host.

## Notification-area controls

Startup opens both the main window and the 9bit widget and adds a tray icon.
Closing the main window does not quit Paperboy; the widget's close button hides
it. Double-click the tray icon or select **Open Paperboy** to reopen the main
window. Use **Exit Paperboy** to shut down the application.

The tray also offers **Open Widget Studio**, APIM service start/stop, **Run OCR
smoke**, and MCP/ACP status dialogs. Widget Studio is launched from its sibling
project when available; the tray falls back to the Paperboy widget otherwise.
Service controls and the OCR smoke action execute repository launcher scripts:
they are not passive status checks. Tray status dialogs primarily report local
files and Windows service state, not end-to-end health.

## Foundry Local offline pipelines

Paperboy expects a running Foundry Local OpenAI-compatible endpoint such as:

```text
http://127.0.0.1:52495/v1
```

Supported pipeline behavior:

- Images: send file metadata and optional image content to a vision-capable
  `/v1/responses` model.
- Videos: optionally sample the first frame with FFmpeg and send that frame plus
  file metadata to a vision-capable model.
- Audio: tag/classify using file metadata; use a Whisper model externally when
  full transcription is required.
- Documents/logs: tag/classify using file metadata and compact prompts.

The result is saved as `paperboy-foundry-metadata-*.json` and can be bundled
with the original payload for offline routing/search. In MCP, a sidecar is
written only when `outputDirectory` is supplied.

## Foundry Local model-card delivery

The **Foundry Models** viewport layer represents local model delivery as cards:

- `qwen2.5-0.5b` for fast language metadata and routing
- `qwen3.5-0.8b` for vision-capable image/video frame tagging
- `phi-3.5-mini` for document reasoning and summaries
- `whisper-tiny` for audio transcription workflows
- `qwen3-0.6b-embedding` for semantic indexing/search

Paperboy can export these cards as JSON or bundle them into a `.paperboy.zip`
for delivery to an offline Foundry Local workspace. Model weights are not
redistributed by Paperboy; the cards describe which aliases and pipeline roles a
receiving workspace should load locally.

## Paperboy Model Manager

The model-card layer includes PMM controls backed by the local Foundry CLI:

- `Download` runs `foundry model download <model> --device <device>`.
- `Load` runs `foundry model load <model> --device <device>`.
- `Info` runs `foundry model info <model>`.
- `Download all` caches every Paperboy model card.
- `Refresh cache` runs `foundry cache list`.

MCP exposes the same operations:

- `paperboy.pmm.info`
- `paperboy.pmm.download`
- `paperboy.pmm.load`
- `paperboy.pmm.cacheList`
- `paperboy.pmm.run`

## Paperboy 9bit widget

The desktop app also opens an always-on-top **Paperboy 9bit** live tile.

Click the tile to expand a compact chat surface. The widget sends prompts through
PMM (`foundry model run`) and shows the real model response. It also has a
pipeline helper action and an orchestrator discovery action that lists local
`dayourbot` or `dlm-lib` endpoints when present.

## Agent-card schema

The app marks major UI surfaces with composable card metadata and ships the card
catalog under:

```text
Assets\AgentCards\paperboy.cards.json
Assets\AgentCards\paperboy.cards.schema.json
```

Cards describe the payload picker, bundle builder, MCP bridge, and livetile
surface so agents can reason over the UI as interactive A2A cards rather than
opaque controls.

## Extensions and capability discovery

Paperboy indexes its own MCP tools together with capabilities contributed by
`*.extension.json` packages, so apps, agents, tools, skills, workflows,
connectors, plugins, and MCP/API bridges are discoverable from one contract.

### Discovery roots

Roots are probed in order; missing roots are skipped.

```text
<app>\Assets\Extensions          (app trust, shipped with Paperboy)
<app>\extensions                 (app trust, side-loaded next to the binaries)
%PROGRAMDATA%\Paperboy\extensions
%APPDATA%\Paperboy\extensions
%USERPROFILE%\.paperboy\extensions
$env:PAPERBOY_EXTENSIONS_PATH    (extra roots, ';' separated)
```

Files ending in `.schema.json` are never treated as manifests.
Discovery recurses up to four directory levels and sorts manifests within each
root. IDs are case-insensitive: the first extension/capability ID wins, and
duplicates produce diagnostics. Built-in tool IDs cannot be replaced.
Discovery is cached until **Refresh extensions** or a list tool's `refresh: true`
request; it does not watch directories or install dependencies.

### Manifest contract

The schema ships at `Assets\Extensions\paperboy.extension.schema.json` and is
served over MCP as `paperboy://extensions/schema`.

```json
{
  "schemaVersion": "1.0",
  "id": "contoso.ocr",
  "name": "Contoso OCR",
  "version": "1.0.0",
  "kind": "connector",
  "capabilities": [
    {
      "id": "contoso.ocr.extract",
      "kind": "connector",
      "title": "Extract text",
      "transport": "process",
      "command": "contoso-ocr.exe",
      "args": ["--input", "{{imagePath}}"],
      "timeoutSeconds": 120
    }
  ],
  "cards": []
}
```

Package `kind` values are `app`, `agent`, `tool`, `skill`, `workflow`, `connector`,
`plugin`, `extension`, `mcp`, and `api`. Capability kinds are `tool`, `skill`,
`workflow`, `agent`, `connector`, `resource`, `prompt`, and `api`.
Unknown kinds are preserved with warnings by the runtime, although the supplied
authoring schema enumerates the known values. Capability `transport` accepts
`process`, `http`, `mcp-stdio`, or `manual`; `builtin` is reserved for
Paperboy's own tools. `{{token}}` placeholders in `command`, `args`,
`workingDirectory`, `url`, and headers are substituted from the caller's
arguments, and a missing token is a caller error rather than an empty string.
The registry performs its own field checks, not full JSON Schema validation;
`inputSchema` and `requires` are descriptive metadata, not dependency installation
or argument validation. Declared extension `resources` are indexed metadata and
are not automatically registered with MCP `resources/read`.

### Invocation policy

`process` and `http` capabilities are invocable; `mcp-stdio` and `manual`
capabilities publish a contract without executing anything. Processes are
started with an explicit argument list and never through a shell.

Only app-trusted roots may be invoked by default. Set
`PAPERBOY_EXTENSIONS_ALLOW_INVOKE=1` to also allow user, machine, and
environment-supplied roots (`true` and `yes` are also accepted).
A manifest with `"enabled": false` is indexed but never invoked.
This is a root-based trust policy, not signature verification or isolation:
app-adjacent manifests can execute by default, and the opt-in permits all
external roots rather than approving individual packages.

Process working directories default to the manifest's directory; relative
`workingDirectory` values are resolved there. HTTP defaults to GET; methods
other than GET/HEAD send the caller's arguments as a JSON body. Invocation
defaults to 60 seconds, with a manifest or caller timeout of 1–900 seconds.
Results report success, exit/status codes, stdout/body, errors, and duration.
Missing required placeholder arguments fail before invocation.

The shipped `yumlog.local-bridges` package declares gateway status and Foundry
model-list HTTP calls, `Yumlog.Native.exe ocr {{imagePath}}` (executable must be
resolvable), and a descriptive `Paperboy.Mcp.exe` stdio launch contract.
Discoverability does not mean these services or executables are installed or
reachable.

### MCP surface

| Tool | Purpose |
| ---- | ------- |
| `paperboy.capabilities.list` | Built-in tools plus extension capabilities, filterable by `kind`/`tag`. |
| `paperboy.extensions.list` | Discovered packages, roots, policy, and manifest diagnostics. |
| `paperboy.extensions.describe` | One package with its capabilities and contributed cards. |
| `paperboy.extensions.invoke` | Run a `process` or `http` capability. |

Resources: `paperboy://capabilities`, `paperboy://extensions`, and
`paperboy://extensions/schema`. `initialize` reports discovery counts and policy
under `capabilities.experimental.paperboy.extensions`.

`paperboy.cards.list` stays backward compatible and only merges extension cards
when called with `"includeExtensions": true`.

### Desktop surface

The Agent Cards panel adds **Refresh extensions**, **Copy capability index**,
and **Open extensions folder** (which creates and opens
`%APPDATA%\Paperboy\extensions`).

### Tests

```powershell
Import-Module Pester -MinimumVersion 5.0
Invoke-Pester -Path .\Skills\Test-PaperboyBundle.Tests.ps1, `
    .\Skills\Test-PaperboyExtensions.Tests.ps1 -Output Detailed
```

Use PowerShell with Pester 5+ (these files are not compatible with Pester 3.4).
Build MCP first: round-trip cases select the Release executable, then Debug, and
are skipped when neither exists. For a default-policy test run, unset
`PAPERBOY_EXTENSIONS_ALLOW_INVOKE` and `PAPERBOY_EXTENSIONS_PATH`; existing
machine/user manifests may still affect discovery diagnostics.

Bundle tests create and clean up temporary archives. Extension tests read
manifests and launch short-lived stdio MCP children; their invocation cases
reject a descriptive transport and a missing OCR argument without launching
OCR. They do not start the desktop, APIM, Foundry, or external HTTP calls and do
not cover successful process/HTTP extension execution or interactive tray UI.
