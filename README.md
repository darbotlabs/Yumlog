# darbot.yumlog

## Screen Capture & Recording

darbot.yumlog provides local Windows tools for desktop screen capture, recording, and lightweight screen analysis. The PowerShell tools remain supported alongside the native `Yumlog.Native` F# CLI for capture, recording orchestration, analysis manifests, and follow-me step extraction.

Paperboy is Yumlog's lightweight courier layer: it can sample recordings with
half-step navigation and can pack files into small manifest-backed bundles that
are easy to copy, attach, or "toss" to another workspace.

Yumlog also includes a local APIM service foundation under
`apps\Yumlog.APIM.Service`. The service exposes loopback health endpoints,
ACP resource/tool/prompt metadata, authenticated local envelope routes, and
APIM route invocation scaffolding for the Paperboy APIM app, MCP bridge,
Widget Studio, LiveTile, OCR, and MDKG workflows.

### Repository ownership

Yumlog is the source of truth for capture, recording, Windows AI OCR, native
analysis/follow manifests, and MDKG metadata contracts. The standalone Paperboy
application owns delivery/bundling, orchestration, its client SDK, APIM execution,
and widget authoring/runtime development.

The Paperboy-related components in this checkout remain available with the
behavior documented here. They are not automatically synchronized with the
standalone application: in particular, this checkout's APIM service is still
the documented foundation, not a fully connected execution backend. No existing
commands are removed by this ownership split.

Integrations should exchange versioned manifests and bundles, not assume a
shared working directory or copy unreviewed implementation changes between
repositories. See [the ownership and compatibility policy](Tasklist.md#ownership-and-compatibility)
before changing shared contracts.

### Components and prerequisites

Run the examples below from the repository root on Windows.

| Component | Purpose | Documentation |
|---|---|---|
| `apps\Yumlog.Native` | F# capture, recording, OCR, MDKG, and extension catalog | [Extension contract](apps/Yumlog.Native/EXTENSIBILITY.md) and [MDKG format](apps/Yumlog.Native/hevc-swarm-out/MDKG-SPEC.md) |
| `apps\Paperboy.Desktop` | WPF bundle UI, model manager, live tile, tray, and capability catalog | [Paperboy guide](apps/Paperboy.Desktop/README.md) |
| `apps\Paperboy.Mcp` | Paperboy stdio MCP host | [MCP configuration](apps/Paperboy.Desktop/README.md) |
| `apps\Yumlog.APIM.Service` | Local HTTP/ACP service foundation | [Service guide](apps/Yumlog.APIM.Service/README.md) |
| `apps\Yumlog.WidgetStudio` | Widget manifest validation, HTML preview, and package export | [Widget Studio guide](apps/Yumlog.WidgetStudio/README.md) |
| `Skills`, `launchers`, `snap` | PowerShell utilities, packaging/service launchers, and fast screenshot executable | [Development guide](Tasklist.md) |

Use the .NET SDK versions required by the component projects: Paperboy, APIM,
and Widget Studio target .NET 11, while Yumlog.Native targets .NET 10 with
Windows 11 24H2 projections. Install matching runtimes for framework-dependent
execution, including the Windows Desktop runtime for Paperboy Desktop and
Yumlog.Native.
PowerShell is required for launcher scripts; FFmpeg is required for recording.
`launchers\install.ps1` can download FFmpeg and build the optional `snap` helper.
Windows AI OCR has additional hardware, runtime, and package-identity requirements
described below. Foundry Local is optional and must be installed/configured
separately for model workflows.

The tools default to local operation, but dependency downloads, model acquisition,
and explicitly configured HTTP/connectors can use the network. Do not commit
API keys, signing certificates, recordings, or generated reports.

### Web Interface

For a visual interface to manage, configure, and use Yumlog, open `yumlog-manager.html` in your web browser. The HTML interface provides:

- Easy command generation for recording and capturing
- Commands for retrieving statistics
- Configuration editor with manual load/save instructions
- PowerShell command reference

Simply double-click `yumlog-manager.html` to launch the interface in your default browser.
It generates commands and instructions; it does not execute them or automatically
read local recordings/configuration.

### Usage Examples

#### Using the native F# CLI

```powershell
# Build the native CLI
dotnet build .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release

# Capture a fast all-monitor screenshot sequence
dotnet run --project .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release -- capture --out-dir .\screenshots --duration 1

# Record desktop video through FFmpeg without PowerShell entry points
dotnet run --project .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release -- record --out-file .\yumlogs\native.mp4 --duration 10 --fps 30

# Analyze image files and emit JSON metadata
dotnet run --project .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release -- analyze --input .\screenshots --ocr auto

# Capture follow-me frames and write a follow manifest
dotnet run --project .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release -- follow --out-dir .\yumlogs\follow --duration 10 --ocr auto

# Show or initialize native config
dotnet run --project .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release -- config show
dotnet run --project .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release -- config init --force

# Build an unsigned MSIX without installing or changing certificate trust
.\launchers\Build-YumlogNativeMsix.ps1 `
  -ProjectPath .\apps\Yumlog.Native\Yumlog.Native.fsproj `
  -OutputRoot .\dist\Yumlog.Native -Configuration Release -NoSign
```

The MSIX launcher requires Windows SDK `makeappx.exe` and rebuilds the package
layout under the specified output root. **Omitting `-NoSign` creates/reuses a
signing certificate and modifies CurrentUser certificate trust.** Installation
is a separate explicit action. The older project `PackageMsix` target references
two missing Paperboy image assets; use the documented native launcher instead.
See the [packaging details](apps/Yumlog.Native/EXTENSIBILITY.md#package-identity-and-ocr)
before running install or OCR helpers.

#### Using the local APIM service

```powershell
# Build the APIM service
dotnet build .\apps\Yumlog.APIM.Service\Yumlog.APIM.Service.fsproj -c Release

# Run a temporary local round-trip validation on http://127.0.0.1:5119
.\launchers\Test-YumlogAPIMRoundTrip.ps1

# Install/start/stop as a Windows Service from an elevated PowerShell session
.\launchers\Install-YumlogAPIMService.ps1
.\launchers\Start-YumlogAPIMService.ps1
.\launchers\Stop-YumlogAPIMService.ps1
.\launchers\Uninstall-YumlogAPIMService.ps1
```

APIM service endpoints include `/health`, `/ready`, `/version`,
`/capabilities`, `/acp/resources`, `/acp/resources/read`, `/acp/tools`,
`/acp/tools/call`, `/acp/prompts`, `/acp/prompts/get`, `/apim/routes`, and
`/apim/routes/invoke`. If `YUMLOG_APIM_API_KEY` or `Auth:ApiKey` is set,
protected requests must provide the key in `X-Yumlog-APIM-Key`,
`Authorization: Bearer <key>`, or `auth.token` in the JSON envelope. The service
is a foundation: catalog/validation and envelope responses do not imply that
OCR, bundling, model inference, or arbitrary route backends are executed.
See the service guide for public endpoints and implementation limits.

`Yumlog.Native` is local-only and uses `config\yumlog.native.json` by default. Capture uses .NET desktop APIs directly. Recording uses FFmpeg as a native child process. OCR uses the Windows AI Text Recognition stack: `--ocr auto`, `--ocr windows-ai`, and `--ocr raw-com` (alias of `windows-ai`) run the full recognition pipeline and return lines, words, polygon bounds, and confidence values. The pipeline is implemented in F# (`WindowsAiOcr.fs`) on the managed `Microsoft.WindowsAppSDK.AI` projections: it bootstraps the Windows App Runtime, checks `TextRecognizer.GetReadyState`, provisions the on-device model via `TextRecognizer.EnsureReadyAsync` when `NotReady`, creates a recognizer with `CreateAsync`, decodes the image to a BGRA8 `SoftwareBitmap`/`ImageBuffer`, and projects `RecognizeTextFromImage` results into the native data model. Windows AI OCR requires Windows App SDK runtime registration, package/sparse-package identity with `systemAIModels` (run the packaged build so `EnsureReadyAsync` can deploy the `Windows.Workload.ContentExtraction` model), and Copilot+/NPU-class hardware; the unpackaged exe reports `CapabilityMissing` by design. `apps\Yumlog.Native\Package.appxmanifest` declares the `systemAIModels` capability and an `appExecutionAlias` so the packaged CLI is invocable as `Yumlog.Native.exe`.

`Mdkg.fs` implements the `.mdkg` binary container format (Magic: `MDKG\r\n\x1A\n`, version 1.0). MDKG carries timestamped metadata alongside recordings as a standalone sidecar. The typed layer contracts cover timing, OCR, change scores, navigation, embeddings, semantic and integration metadata, and the extension catalog. The binary format and its proposed MP4 integration are documented in [MDKG-SPEC.md](apps/Yumlog.Native/hevc-swarm-out/MDKG-SPEC.md); writing a sidecar does not itself mux metadata into an MP4.

#### Native extension catalog

```powershell
dotnet run --project .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release -- extensions list
dotnet run --project .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release -- extensions validate --dir .\apps\Yumlog.Native\Samples
dotnet run --project .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release -- extensions mcp
```

The catalog combines built-in capability descriptors with optional JSON extension
manifests. The MCP command exports a manifest; it does not start a tool server or
execute the declared extensions. See the [extension guide](apps/Yumlog.Native/EXTENSIBILITY.md).

#### Widget Studio

```powershell
dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- validate .\apps\Yumlog.WidgetStudio\Samples\paperboy-capability-mesh.widget.json
dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- preview .\apps\Yumlog.WidgetStudio\Samples\paperboy-livetile.widget.json .\dist\widgetstudio\paperboy.preview.html
dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- self-test
```

Widget Studio supports schema versions `1.0` and `1.1`, semantic validation,
capability indexes, static HTML previews, and `.widget.zip` exports. It is an
authoring CLI, not an always-running widget host.

#### Using the Unified CLI

```powershell
# Record the desktop at 30 FPS for 15 seconds
.\launchers\yumlog.ps1 start -Fps 30 -DurationSec 15

# Get the latest recording
.\launchers\yumlog.ps1 get

# Check recording count and total size
.\launchers\yumlog.ps1 count
.\launchers\yumlog.ps1 size

# View configuration
.\launchers\yumlog.ps1 config
```

#### Recording terminal windows with Paperboy frames

```powershell
# Record visible terminal windows, extract binary-search frame samples,
# and write a manifest for the HTML viewer.
.\launchers\Record-Terminals.ps1 -DurationSec 10 -Depth 4 -Fps 15
```

The terminal recorder uses the `Paperboy` half-step navigation engine to choose
representative timestamps across a recording. The generated manifest can be
loaded by the bundled terminal viewer for quick inspection of frame changes.

#### Packing files into a Paperboy bundle

```powershell
# Pack files/folders into a manifest-backed .paperboy.zip archive.
.\launchers\paperboy.ps1 pack .\Skills, .\README.md -OutFile .\dist\yumlog.paperboy.zip

# Inspect the bundle manifest without unpacking the payload.
.\launchers\paperboy.ps1 list .\dist\yumlog.paperboy.zip

# Expand the bundle later.
.\launchers\paperboy.ps1 unpack .\dist\yumlog.paperboy.zip -Destination .\restored -Force

# Toss an existing bundle to another directory or final file path.
.\launchers\paperboy.ps1 toss .\dist\yumlog.paperboy.zip -Destination .\outbox -Force
```

Paperboy bundles store payload files under `payload/` and include
`paperboy-manifest.json` with source paths, archive paths, byte counts,
timestamps, and SHA-256 hashes. They are ordinary ZIP archives with a
`.paperboy.zip` convention so they remain easy to inspect with standard tools.

#### Using the native Paperboy desktop app

```powershell
# Build the Windows-native .NET 11 desktop app
dotnet build .\apps\Paperboy.Desktop\Paperboy.Desktop.csproj -c Release
dotnet build .\apps\Paperboy.Mcp\Paperboy.Mcp.csproj -c Release

# Publish a self-contained win-x64 app folder
dotnet publish .\apps\Paperboy.Desktop\Paperboy.Desktop.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

The desktop app provides a modern drag-and-drop UI for selecting files and
folders, choosing compression, creating `.paperboy.zip` bundles, inspecting
manifests, unpacking bundles, and tossing bundles to an outbox folder.
It uses WPF for its showcase shell and ships
composable agent-card metadata for A2A/livetile surfaces plus a companion
`Paperboy.Mcp` stdio host for agent tool calls.

#### Offline metadata tagging with Foundry Local

Paperboy can enrich payloads with offline model metadata using a running
Foundry Local OpenAI-compatible endpoint:

```powershell
# Example endpoint started by a Foundry Local web-server workflow:
# http://127.0.0.1:52495/v1
```

In the desktop app, use **Foundry Local offline tagging** to choose:

- endpoint, for example `http://127.0.0.1:52495/v1`
- model alias/id, for example `qwen3.5-0.8b` for vision-capable responses
- whether to send image content
- whether to sample a video frame with FFmpeg

The pipeline produces a `paperboy-foundry-metadata-*.json` sidecar containing
file hashes, media kind, tags, classifications, summaries, and raw model output.
The sidecar is automatically added to the payload list so it can travel inside
the same `.paperboy.zip` bundle.

Recommended offline model roles:

| Pipeline | Foundry Local model role |
|---|---|
| Image classification/tagging | Vision-capable Responses API model |
| Video classification/tagging | Vision-capable model over sampled frame plus file metadata |
| Audio metadata tagging | Chat model over file metadata, or Whisper externally for transcription |
| Document/log tagging | Small chat model over text/file metadata |

The MCP host also exposes `paperboy.foundryLocal.analyze` for agent-driven
offline tagging.

#### Delivering Foundry Local language models with Paperboy

Paperboy includes a full-page **Foundry Local Model Delivery Cards** layer. It
does not redistribute model weights. Instead, it packages model aliases, roles,
recommended prompts/config, and MCP tool hints so an offline Foundry Local
workspace can pull/load the right local model:

| Model card | Delivery role |
|---|---|
| `qwen2.5-0.5b` | Fast language model for chat, metadata, and routing |
| `qwen3.5-0.8b` | Vision-capable responses model for image/video-frame tagging |
| `phi-3.5-mini` | General local reasoning and document summarization |
| `whisper-tiny` | Audio transcription workflow |
| `qwen3-0.6b-embedding` | Semantic search and similarity fingerprints |

Use **Foundry Models** in the desktop app to export or bundle the model-card
catalog as a `.paperboy.zip`. The MCP host exposes the same catalog through
`paperboy.foundryLocal.modelCards`.

The **Paperboy Model Manager (PMM)** controls in that layer call the local
`foundry` CLI to manage the model lifecycle:

- `Download` caches the selected model locally.
- `Load` loads the selected model into the Foundry Local service.
- `Info` displays the model's Foundry catalog metadata.
- `Download all` hydrates the Paperboy model-card set.
- `Refresh cache` shows which model cards are already cached.

PMM settings include the CLI path (`foundry` by default) and target device
(`Auto`, `GPU`, `CPU`, or `NPU`). The same operations are exposed through MCP as
`paperboy.pmm.info`, `paperboy.pmm.download`, `paperboy.pmm.load`,
`paperboy.pmm.cacheList`, and `paperboy.pmm.run`.

#### Floating Paperboy 9bit widget

The Windows desktop app launches a small always-on-top **Paperboy 9bit** live
tile alongside the main window. Click the tile to expand a compact model chat
surface backed by PMM:

- choose a local model (`qwen3-0.6b`, `qwen2.5-0.5b`, or `phi-3.5-mini`)
- send a natural-language prompt through `foundry model run`
- view the real model response in the widget
- ask the widget to propose the next offline pipeline step
- inspect local orchestration endpoints from `dayourbot` or `dlm-lib` when those
  repos are present on the machine

The widget is intentionally a live tile: small by default, expandable on demand,
and model-backed rather than a static status badge.

### Testing

Use the existing component suites rather than `Simple.Tests.ps1`, which is only
an always-passing placeholder:

```powershell
Invoke-Pester -Path .\Skills\Test-PaperboyBundle.Tests.ps1, .\Skills\Test-PaperboyExtensions.Tests.ps1 -Output Detailed
.\apps\Yumlog.Native\Test-YumlogNativeExtensibility.ps1
.\launchers\Test-YumlogNativeMdkgRoundTrip.ps1
.\launchers\Test-YumlogAPIMRoundTrip.ps1
dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- self-test
```

The Paperboy suites use modern Pester syntax; see the component guide for the
supported invocation. The older `Test-ScreenSkills.Tests.ps1` uses Pester 3
syntax and mocked files, not actual screen capture. OCR and MSIX installation
are separate, machine-dependent checks; do not run installation scripts as
ordinary unit tests. APIM round-trip reports and `dist` output stay local.

CI builds the maintained desktop/MCP, APIM, and Widget Studio components and
runs their meaningful contract suites plus native extension/MDKG round trips.
It does not download FFmpeg, capture a desktop, provision OCR, sign packages,
or install a service. The always-passing/mock screen tests are not evidence of
capture readiness and are not release gates.

---

### Command Reference

| Command                | Description                                      |
|------------------------|--------------------------------------------------|
| `.\launchers\yumlog.ps1 start` | Record with optional `-Fps`, `-DurationSec`, and `-OutFile` |
| `.\launchers\yumlog.ps1 capture` | Capture with optional `-Fps`, `-DurationSec`, and `-OutDir` |
| `.\launchers\yumlog.ps1 pause` / `stop` | Placeholders; process control is not implemented |
| `.\launchers\yumlog.ps1 get` | Get the latest recording path |
| `.\launchers\yumlog.ps1 count` / `size` | Recording count / total size |
| `.\launchers\yumlog.ps1 config` | Print `config\tools.json` |
| Record-Terminals.ps1   | Record terminal windows and extract Paperboy frames |
| paperboy.ps1 pack      | Create a `.paperboy.zip` bundle with manifest    |
| paperboy.ps1 list      | Print a Paperboy bundle manifest                 |
| paperboy.ps1 unpack    | Expand a Paperboy bundle                         |
| paperboy.ps1 toss      | Copy a bundle to a destination                   |
| Paperboy.exe           | Native desktop UI for pack/list/unpack/toss workflows |

---

## Project Tree (excerpt)

```text
darbot.yumlog/
├── Skills/
│   ├── Capture-Screens.ps1
│   ├── Paperboy.Bundle.ps1
│   ├── Paperboy.ps1
│   ├── paperboy.js
│   ├── Record-Screen.ps1
│   └── Run-FFmpeg.ps1
├── launchers/
│   ├── install.ps1
│   ├── paperboy.ps1
│   ├── Record-Terminals.ps1
│   └── yumlog.ps1
├── apps/
│   ├── Paperboy.Desktop/
│       ├── MainWindow.xaml
│       └── Services/PaperboyBundleService.cs
│   ├── Paperboy.Mcp/
│   ├── Yumlog.Native/
│   ├── Yumlog.APIM.Service/
│   └── Yumlog.WidgetStudio/
├── bisect-test/
│   ├── bisect-extractor.html
│   ├── library.html
│   └── terminal-viewer.html
├── config/
│   ├── tools.json
│   └── yumlog.native.json
├── .github/
│   └── copilot-instructions.md
├── yumlog-manager.html
└── ...
```

---

For more details, see `Tasklist.md` and inline comments in each script.

Local media under `archive\51PercentCrew` and generated integration reports under
`Scratchpad\apim-roundtrip` are not application source and are excluded from new
commits. Historical research notes under `hevc-swarm-out` describe design work;
the component guides distinguish implemented behavior from planned integration.
