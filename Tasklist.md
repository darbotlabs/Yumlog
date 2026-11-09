# darbot.yumlog

Welcome to darbot.yumlog.

darbot.yumlog is a Windows capture and local automation toolkit. It includes the native F# CLI and MDKG metadata containers, Paperboy desktop/MCP capabilities, a local APIM/ACP service foundation, Widget Studio authoring, and the original PowerShell utilities. This guide describes the maintained entry points; component guides contain their detailed contracts.

**License:** darbot.yumlog is licensed under the MIT License.

**Local-first:** capture, analysis, bundles, and reports are stored locally. Dependency/model downloads and explicitly configured HTTP connectors can use the network. The apps produce local logs and metadata; do not treat them as safe to publish automatically.

**@modelcontextprotocol Compliance:** This project is structured and documented following best practices for clarity, modularity, and ease of understanding, aligning with the principles of the @modelcontextprotocol to facilitate development and extension by AI agents and human developers alike.

## Table of Contents

1.  [Features](#features)
2.  [Project Structure](#project-structure)
3.  [Getting Started](#getting-started)
    * [Prerequisites](#prerequisites)
    * [Cloning the Repository](#cloning-the-repository)
4.  [Installation](#installation)
5.  [Usage](#usage)
    * [Web Interface](#web-interface)
    * [Screen Recording](#screen-recording)
    * [Screenshot Capture](#screenshot-capture)
    * [Run Unit Tests](#run-unit-tests)
    * [Yumlog CLI Commands](#yumlog-cli-commands)
6.  [Configuration](#configuration)
7.  [PowerShell Tooling Deep Dive](#powershell-tooling-deep-dive)
8.  [Contributing](#contributing)
9.  [License](#license)

## Features

* **Native F# CLI:** `apps\Yumlog.Native` provides capture, record, analyze, follow, orchestrate, config, and extension catalog commands.
* **MDKG Container Format:** `Mdkg.fs` implements standalone timestamped metadata containers with typed producers, validation, and extension-catalog provenance. MP4 metadata muxing is a separate integration design, not an automatic side effect of writing a sidecar. See [MDKG-SPEC.md](apps/Yumlog.Native/hevc-swarm-out/MDKG-SPEC.md).
* **Paperboy:** WPF desktop bundling, model-management and live-tile UI, capability/extension catalogs, and a companion stdio MCP host. See the [Paperboy guide](apps/Paperboy.Desktop/README.md).
* **APIM/ACP Service:** Local metadata catalogs and authenticated envelope scaffolding, with Windows Service launchers. See [service coverage and limitations](apps/Yumlog.APIM.Service/README.md).
* **Widget Studio:** Versioned widget contracts, semantic validation, static HTML previews, capability indexes, and package export. See the [authoring guide](apps/Yumlog.WidgetStudio/README.md).
* **Screen Recording:** Record the desktop at configurable FPS and duration to MP4 using FFmpeg.
* **Screenshot Capture:** Capture periodic screenshots at configurable FPS and duration.
* **Screen Analysis:** Emit JSON image metadata, hashes, dimensions, and OCR provider status.
* **Follow-Me Manifests:** Capture frame sequences, estimate visual changes, and emit navigation-step manifests.
* **Simple CLI:** One-liner PowerShell scripts for all major actions.
* **Explicit Dependencies:** .NET SDKs/runtimes for native apps, PowerShell for launchers, FFmpeg for video, and optional Windows AI / Foundry Local components.
* **Scriptable:** Designed for automation and integration in CI/CD or test workflows.
* **Configurable:** PowerShell settings live in `config\tools.json`, native settings in `config\yumlog.native.json`, and APIM settings in its component configuration.
* **Testable:** Pester suites, native F# round-trip scripts, APIM loopback checks, and Widget Studio's built-in `self-test`.

## Project Structure

```
darbot.yumlog/
├── Skills/                 # Reusable PowerShell functions/modules
│   ├── Capture-Screens.ps1
│   ├── Record-Screen.ps1
│   ├── Run-FFmpeg.ps1
│   └── Test-ScreenSkills.Tests.ps1
├── launchers/              # Top-level PowerShell scripts using Skills
│   ├── install.ps1         # Installs FFmpeg if needed
│   ├── yumlog.ps1          # Unified recording/capture CLI
│   ├── paperboy.ps1        # Bundle CLI
│   ├── Build-YumlogNativeMsix.ps1
│   └── Test-YumlogAPIMRoundTrip.ps1
├── config/                 # Configuration files
│   ├── tools.json          # PowerShell configuration
│   └── yumlog.native.json  # Native CLI configuration
├── apps/
│   ├── Yumlog.Native/      # Native F# CLI and metadata contracts
│   ├── Paperboy.Desktop/   # WPF UI and shared capability implementation
│   ├── Paperboy.Mcp/       # Stdio MCP host
│   ├── Yumlog.APIM.Service/ # Local HTTP/ACP service
│   └── Yumlog.WidgetStudio/ # Widget authoring CLI
├── .github/                # GitHub configuration
│   └── copilot-instructions.md  # GitHub Copilot instructions
├── yumlog-manager.html     # Web-based management interface
└── ...
```

## Getting Started

To get started with darbot.yumlog, set up your environment as follows:

### Prerequisites

* **Windows:** Required for desktop capture, Paperboy WPF, and native Windows integrations.
* **PowerShell:** Windows PowerShell for older capture utilities; PowerShell 7 for modern development/test scripts.
* **.NET:** Paperboy, APIM, and Widget Studio target .NET 11; native capture targets .NET 10 with Windows 11 24H2 projections. Framework-dependent native and Paperboy desktop apps require matching Windows Desktop runtimes.
* **FFmpeg:** Downloaded by `launchers\install.ps1` if neither PATH nor the local cache contains it.
* **Git:** For cloning the repository.
* **Optional integrations:** Foundry Local for model workflows; Windows App SDK runtime, package identity with `systemAIModels`, compatible NPU hardware, and an available OCR model for Windows AI OCR.

### Cloning the Repository

```powershell
git clone https://github.com/darbotlabs/Yumlog.git
cd Yumlog
```

## Installation

```powershell
.\launchers\install.ps1
```

## Usage

### Web Interface

Yumlog includes a static browser-based PowerShell command helper. To use it:

1. Open `yumlog-manager.html` in your web browser (double-click the file)
2. Use the visual interface to:
   - Generate PowerShell commands with custom settings
   - Display commands for retrieving recording statistics
   - Prepare commands and manually edit configuration
   - Access command reference

The page requires no server setup. It does not execute PowerShell or automatically
load recordings, statistics, or configuration from disk; its prompts explain the
manual steps.

### Screen Recording

```powershell
.\launchers\yumlog.ps1 start -Fps 30 -DurationSec 10 -OutFile .\myvideo.mp4
```

### Native F# CLI

```powershell
dotnet build .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release
dotnet run --project .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release -- capture --out-dir .\screenshots --duration 1
dotnet run --project .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release -- analyze --input .\screenshots --ocr auto
dotnet run --project .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release -- follow --out-dir .\yumlogs\follow --duration 10 --ocr auto
.\launchers\Build-YumlogNativeMsix.ps1 `
  -ProjectPath .\apps\Yumlog.Native\Yumlog.Native.fsproj `
  -OutputRoot .\dist\Yumlog.Native -Configuration Release -NoSign
```

The native CLI targets `net10.0-windows10.0.26100.0`. Its capture/analysis commands run without PowerShell, while packaging and validation helpers are PowerShell scripts. OCR uses the managed Windows App SDK AI projections; `raw-com` is a compatibility alias for `windows-ai`, not a separate raw-vtable implementation. OCR readiness depends on runtime registration, package identity, supported hardware, and model availability.

The unsigned package build requires Windows SDK `makeappx.exe` and replaces its
generated layout. Omitting `-NoSign` also creates/reuses a signing certificate
and changes CurrentUser certificate trust; installation is separate. The older
project `PackageMsix` target needs missing Paperboy branding assets and is not
the recommended packaging path.

The [native extension guide](apps/Yumlog.Native/EXTENSIBILITY.md) documents manifest loading, validation, and MCP-shaped catalog export. Exporting a capability declaration does not execute it or start an MCP server.

### Screenshot Capture

```powershell
.\launchers\yumlog.ps1 capture -Fps 2 -DurationSec 5 -OutDir .\myscreens
```

### Run Unit Tests

```powershell
Invoke-Pester -Path .\Skills\Test-PaperboyBundle.Tests.ps1, .\Skills\Test-PaperboyExtensions.Tests.ps1 -Output Detailed
.\apps\Yumlog.Native\Test-YumlogNativeExtensibility.ps1
.\launchers\Test-YumlogNativeMdkgRoundTrip.ps1
.\launchers\Test-YumlogAPIMRoundTrip.ps1
dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- self-test
```

Use modern Pester for the Paperboy suites. The older `Test-ScreenSkills.Tests.ps1`
uses Pester 3 syntax and creates mock media rather than exercising capture;
`Simple.Tests.ps1` is an always-passing placeholder. Do not equate either with
hardware validation. MSIX installation, Windows Service installation, OCR model
provisioning, and actual desktop capture are opt-in machine-dependent operations.

### Yumlog CLI Commands

The unified CLI is provided by `launchers/yumlog.ps1`:

| Command                | Description                                      |
|------------------------|--------------------------------------------------|
| `.\launchers\yumlog.ps1 start` | Record with optional `-Fps`, `-DurationSec`, `-OutFile` |
| `.\launchers\yumlog.ps1 capture` | Capture with optional `-Fps`, `-DurationSec`, `-OutDir` |
| `.\launchers\yumlog.ps1 pause` / `stop` | Not implemented; prints a placeholder message |
| `.\launchers\yumlog.ps1 get` | Latest recording path |
| `.\launchers\yumlog.ps1 count` / `size` | Recording count / total size |
| `.\launchers\yumlog.ps1 config` | Print PowerShell configuration |

## Configuration

`config\tools.json` records PowerShell defaults and is printed by `yumlog.ps1
config`; pass launcher parameters explicitly to control recording/capture.
`config\yumlog.native.json` is loaded by the native CLI, and old files without an
`extensions` section are normalized to defaults. APIM settings and API-key
behavior are documented in its component guide. Never put real API keys into
tracked configuration or extension manifests.

## PowerShell Tooling Deep Dive

The Skills/ directory contains reusable PowerShell functions for screen capture, recording, and FFmpeg invocation. Launcher scripts in launchers/ provide user-facing commands. See inline comments for details.

## Contributing

Contributions are welcome! Please fork the repo, create a branch, and submit a pull request.

### Ownership and compatibility

| Area | Source of truth | Consumer responsibility |
|---|---|---|
| Capture, recording, OCR, native analysis/follow | Yumlog | Detect capability readiness and preserve native command semantics |
| MDKG, native domain/serialization/configuration contracts | Yumlog | Pin an exact upstream revision and review schema/format changes |
| Bundle delivery, desktop/MCP orchestration, client SDK | Standalone Paperboy application | Preserve existing bundle/schema identifiers and document API changes |
| Executable APIM integration and widget lifecycle | Standalone Paperboy application | Distinguish implemented execution from authoring and catalog metadata |

This checkout retains its documented Paperboy components for existing users.
The ownership split does not delete features, deprecate working commands, or
turn the local APIM scaffold into an executable backend.

For native contract changes, propose the source change in Yumlog first, keep
the MDKG format/version and schema URNs explicit, and include round-trip and
old-input compatibility evidence. Consumers adopt an exact reviewed commit;
they must not silently track `main`. Content fingerprints can expose accidental
copy drift, but do not prove semantic compatibility.

Shared contract changes should be additive where possible. If readers cannot
safely interpret a new shape, version the affected contract and document
migration rather than changing the meaning of an existing identifier. SDK
package versions, application versions, and data-format versions are separate.

Changes flowing back from restricted downstream repositories require explicit
publication approval. Do not copy private implementation, credentials, data,
or internal roadmap details into public issues, commits, or pull requests.

### Integration and delivery sequence

The ownership and CI slice is complete. Native `handoff` now produces copied
source frames, analysis, an actual MDKG sidecar, and a versioned integrity
manifest from existing images. Its regression script
`launchers\Test-YumlogNativeHandoff.ps1` exercises synthetic images, MDKG reads,
resume, tampering, and failure cases without capturing the desktop or
provisioning a model. Delivery packaging and runtime consumption belong to the
downstream application; this repository does not claim to implement that host.

Previously committed build outputs or recordings need a separate, reviewed
index-cleanup change. Adding ignore rules does not remove tracked files.
Do not delete local captures, force-push, or rewrite existing history as part of
an ordinary source/documentation integration.

Keep source, required package assets, schemas, samples, and documentation together.
Do not newly stage `bin`, `obj`, `dist`, generated APIM reports, local signing
certificates, or the downloaded `archive\51PercentCrew` media collection.
Ignore rules do not untrack artifacts already committed on other branches.
Use explicit paths when staging and inspect the staged diff before committing.

### Remaining integration work

The APIM service still needs concrete backend dispatch and production-grade
authentication/authorization, audit, rate limiting, and installed-service tests.
Widget Studio produces packages but does not run an adaptive widget runtime.
Native extension manifests describe capabilities rather than dynamically loading
plugins. Signed MSIX distribution and hardware-dependent OCR readiness require
separate environment-specific validation. Research notes under
`apps\Yumlog.Native\hevc-swarm-out` are historical design context, not a claim that
every proposed capture/encoding/mux pipeline is implemented.

## License

MIT License