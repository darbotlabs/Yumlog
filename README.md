# darbot.yumlog

## Screen Capture & Recording

darbot.yumlog provides local Windows tools for desktop screen capture, recording, and lightweight screen analysis. The legacy PowerShell tools remain available, and the native `Yumlog.Native.Cli` F# CLI is the no-PowerShell path for capture, recording orchestration, analysis manifests, follow-me step extraction, verifiable runbooks, and on-device reasoning about a recording.

Two reference documents cover the native path in depth:

- [`docs/NATIVE-CLI.md`](docs/NATIVE-CLI.md) — full command reference, flags, config, and known rough edges
- [`docs/ON-DEVICE-REASONING.md`](docs/ON-DEVICE-REASONING.md) — how grounding works, frame-rate accuracy, and measured Edge Prompt API behaviour

Paperboy is Yumlog's lightweight courier layer: it can sample recordings with
half-step navigation and can pack files into small manifest-backed bundles that
are easy to copy, attach, or "toss" to another workspace.

### Web Interface

For a visual interface to manage, configure, and use Yumlog, open `yumlog-manager.html` in your web browser. The HTML interface provides:

- Easy command generation for recording and capturing
- Statistics overview
- Configuration viewer
- PowerShell command reference

Simply double-click `yumlog-manager.html` to launch the interface in your default browser.

### Usage Examples

#### Using the native F# CLI

The runnable binary is built from `apps\Yumlog.Native.Cli`. The `apps\Yumlog.Native` project is the implementation library.

```powershell
# Build the native CLI
dotnet build .\apps\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj -c Release
$exe = ".\apps\Yumlog.Native.Cli\bin\Release\net10.0-windows10.0.26100.0\win-x64\Yumlog.Native.Cli.exe"

# Capture a fast all-monitor screenshot sequence
& $exe capture --out-dir .\screenshots --duration 1

# Record HEVC video AND the .mdkg metadata sidecar that powers runbook/reason
& $exe record --out-file .\yumlogs\native.mp4 --timed-metadata-out .\yumlogs\native.mdkg `
              --duration 10 --fps 30 --backend auto

# Analyze image files and emit JSON metadata
& $exe analyze --input .\screenshots --ocr auto

# Capture follow-me frames and write a follow manifest
& $exe follow --out-dir .\yumlogs\follow --duration 10 --ocr auto

# Turn a recording into a runbook where every claim carries a resolvable citation
& $exe runbook --input .\yumlogs\native.mdkg --markdown-out .\runbook.md

# Ask an on-device model a question about the recording
& $exe reason --input .\yumlogs\native.mdkg --provider cpu `
              --question "At what frame rate was this captured, and were any frames dropped?"

# Watch the live screen and narrate on an interval
& $exe watch --duration 30 --fps 2 --narrate-every 10

# List installed on-device models, and run the built-in correctness suite
& $exe models
& $exe selftest

# Show or initialize native config
& $exe config show
& $exe config init --force

# Build an unsigned MSIX package layout and package artifact
dotnet msbuild .\apps\Yumlog.Native\Yumlog.Native.fsproj -t:PackageMsix -p:Configuration=Release
```

> `--timed-metadata-out` is what makes a recording answerable. Without it you get
> video only, and `runbook` and `reason` have nothing to read.

> Unknown arguments are currently ignored silently. A typo such as `--output`
> instead of `--out-file` exits 0 and writes nothing.

#### Verifiable runbooks

`runbook` converts an `.mdkg` sidecar into ordered steps, each carrying proofs —
claim / path / value triples where the path resolves back into the file:

```json
{ "claim": "frame index at this step",
  "path":  "mdkg://timing@0#/frameIndex",
  "value": "0" }
```

Because every proof re-resolves against the source, a runbook can be checked
rather than believed. Measured on `hevc-swarm-out\baseline\artifacts\r2-churn2.mdkg`:
155 steps, 310 proofs (two per step).

#### On-device reasoning

`reason` answers questions about a recording using a local model through ONNX
Runtime GenAI. Nothing leaves the machine, and Yumlog loads model weights
directly rather than calling Edge's model service.

Frame-rate reporting is verified across three independent rates — 12 fps, 15 fps,
and 30 fps each produce the correct answer with zero coverage overlaps. Answer
phrasing varies between runs, so assert on content rather than exact wording. See
[`docs/ON-DEVICE-REASONING.md`](docs/ON-DEVICE-REASONING.md) for how grounding is
assembled and why runbook proofs are *not* visible to the model.

#### Built-in correctness suite

`selftest` runs 16 checks across the MDKG container, the intelligence layer, and
hardware probes, and returns JSON with keys `passed`, `passedCount`,
`failedCount`, and `cases`. `mdkg.timing.coverageNoOverlap` ships with a negative
control so it cannot pass vacuously.

Note that `bin\` is committed in this repository, so a stale binary can report an
old pass count. Rebuild before treating `selftest` output as current.

`Yumlog.Native` is local-only and uses `config\yumlog.native.json` by default. Capture uses Windows Graphics Capture (WGC) with a Direct3D path; recording encodes HEVC through NVENC, falls back to x265, then to FFmpeg (`--backend auto|nvenc|x265|ffmpeg`). `selftest` reports NVENC availability via `probe.nvenc.driverDll`. OCR uses the Windows AI Text Recognition stack: `--ocr auto`, `--ocr windows-ai`, and `--ocr raw-com` (alias of `windows-ai`) run the full recognition pipeline and return lines, words, polygon bounds, and confidence values. The pipeline is implemented in F# (`WindowsAiOcr.fs`) on the managed `Microsoft.WindowsAppSDK.AI` projections: it bootstraps the Windows App Runtime, checks `TextRecognizer.GetReadyState`, provisions the on-device model via `TextRecognizer.EnsureReadyAsync` when `NotReady`, creates a recognizer with `CreateAsync`, decodes the image to a BGRA8 `SoftwareBitmap`/`ImageBuffer`, and projects `RecognizeTextFromImage` results into the native data model. Windows AI OCR requires Windows App SDK runtime registration, package/sparse-package identity with `systemAIModels` (run the packaged build so `EnsureReadyAsync` can deploy the `Windows.Workload.ContentExtraction` model), and Copilot+/NPU-class hardware; the unpackaged exe reports `CapabilityMissing` by design. `apps\Yumlog.Native\Package.appxmanifest` declares the `systemAIModels` capability and an `appExecutionAlias` so the packaged CLI is invocable as `Yumlog.Native.exe`.

`Mdkg.fs` implements the `.mdkg` binary container format (Magic: `MDKG\r\n\x1A\n`, version 1.0). MDKG is a layered metadata track format designed to carry frame-accurate metadata alongside HEVC/MP4 recordings. Layer samples align to the `MovieTimescale` (10,000,000 ticks/sec), and unregistered layer IDs are skipped rather than treated as corruption, so the format is forward-compatible.

| Layer | ID | Carries |
|---|---|---|
| `Timing` | 1 | Frame index, PTS, duration, drop accounting |
| `OcrText` | 2 | Recognized text, lines, words, confidence |
| `UiAffordance` | 3 | Interactive UI elements and their bounds |
| `SemanticGraph` | 4 | Entities and relations reconstructed across frames |
| `InputEvent` | 5 | Input events correlated to frames |

The format is independent of MP4 and can be embedded as a `mett` timed-metadata track or stored as a standalone `.mdkg` sidecar. See `apps/Yumlog.Native/hevc-swarm-out/MDKG-SPEC.md` for the full byte-layout specification.

#### NPU Emulator Container app

`apps\Yumlog.NpuEmulator` is the Rust/Tauri host for Yumlog's adaptive NPU-emulation runtime. It models the hard parts of Windows AI `systemAIModels`-style execution directly: feature readiness, model provisioning, container worker state, CPU-native emulated NPU execution targets, and trace capture for repeatable ground-truth analysis.

```powershell
cd apps\Yumlog.NpuEmulator
npm install
npm run tauri dev

# Rust-only validation
cd src-tauri
cargo test
cargo check
```

The first implementation exposes Tauri commands for `TextRecognizer`-style state, provisioning, model inventory, trace journal, container runtime probing, and recognition request acceptance. The next integration step is a local HTTP or named-pipe bridge so `Yumlog.Native.Cli` can call the emulator through a first-class `--ocr npu-emulator` mode.

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
It uses Windows App SDK visual assets for its showcase shell and ships
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

#### Run Unit Tests

```powershell
# Executes Pester with detailed output
.\Skills\Simple.Tests.ps1
```

- Output files are saved to `./yumlogs` by default, or as specified by parameters.
- FFmpeg is automatically installed if not present (via `install.ps1`).

---

### Testing

Run tests directly with Pester or PowerShell:

```powershell
.\Skills\Simple.Tests.ps1
```

Compatible with Pester 3.4.0+ (included in Windows PowerShell).

---

### Command Reference

#### Native CLI (`Yumlog.Native.Cli`)

| Command | Description |
|---|---|
| `capture` | Fast screenshot sequence to a directory |
| `record` | HEVC desktop video plus optional `.mdkg` metadata sidecar |
| `analyze` | Image metadata, hashes, dimensions, OCR |
| `follow` | Change-gated frame sequence plus follow manifest |
| `orchestrate` | Run a multi-step plan file |
| `runbook` | Emit a runbook whose every claim carries a resolvable citation |
| `reason` | Answer a question about a recording with an on-device model |
| `watch` | Observe the live screen and narrate on an interval |
| `models` | List installed on-device models |
| `selftest` | Run the 16-check built-in correctness suite |
| `config show` / `config init` | Inspect or scaffold native config |

Full flags in [`docs/NATIVE-CLI.md`](docs/NATIVE-CLI.md).

#### PowerShell tooling

| Command                | Description                                      |
|------------------------|--------------------------------------------------|
| start yumlog [‑Fps n] [‑DurationSec n] [‑OutFile path] | Start a new yumlog screen recording (video)      |
| pause yumlog           | Pause the current yumlog recording (if supported)|
| stop yumlog            | Stop the current yumlog recording                |
| get yumlog             | Get the latest yumlog file path                  |
| yumlog count           | Number of yumlog files in the default folder     |
| yumlog size            | Cumulative size of all yumlog files              |
| yumlog config          | Show current yumlog configuration (tools.json)   |
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
│   ├── Yumlog.Native/            # F# implementation library
│   │   ├── WgcCapture.fs         # Windows Graphics Capture
│   │   ├── NvencHevc.fs          # NVENC HEVC encoder
│   │   ├── X265Encoder.fs        # x265 software fallback
│   │   ├── HevcMux.fs            # MP4/HEVC muxer
│   │   ├── Mdkg.fs               # .mdkg layered metadata container
│   │   ├── MdkgIntel.fs          # timeline, citations, runbooks
│   │   ├── OnDeviceLlm.fs        # local model reasoning + watch
│   │   ├── SelfTest.fs           # built-in correctness suite
│   │   └── hevc-swarm-out/       # MDKG-SPEC.md, challenge reviews (see its README)
├── hevc-swarm-out/               # CDP harnesses + recording artifacts (see its README)
│   ├── Yumlog.Native.Cli/        # runnable CLI entry point
│   ├── Yumlog.NpuEmulator/       # Rust/Tauri NPU-emulation runtime
│   ├── Paperboy.Desktop/
│   └── Paperboy.Mcp/
├── docs/
│   ├── NATIVE-CLI.md
│   ├── ON-DEVICE-REASONING.md
│   └── NPU-EMULATOR-CONTAINER-SPEC.md
├── bisect-test/
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
