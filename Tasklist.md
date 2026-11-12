# darbot.yumlog

Welcome to darbot.yumlog.

darbot.yumlog is a local screen capture and logging utility for Windows, designed for automation, reproducibility, and integration with other tools. It now includes a native .NET F# CLI for capture, recording orchestration, analysis manifests, follow-me screen-step extraction, configuration parsing, and OCR capability detection, alongside the original PowerShell utilities.

**License:** darbot.yumlog is licensed under the MIT License.

**No Telemetry:** darbot.yumlog is designed to run entirely locally on the user's device. It does not collect any logs, telemetry, or user data.

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
    * [Skills Directory](#skills-directory)
    * [Launchers Directory](#launchers-directory)
    * [PowerShell and FFmpeg for Dev Assets](#powershell-and-ffmpeg-for-dev-assets)
8.  [Contributing](#contributing)
9.  [License](#license)

## Features

* **Native F# CLI:** `apps\Yumlog.Native.Cli` provides no-PowerShell capture, record, analyze, follow, orchestrate, runbook, reason, watch, models, selftest, and config commands. See [`docs/NATIVE-CLI.md`](docs/NATIVE-CLI.md).
* **Hardware HEVC Recording:** Windows Graphics Capture into NVENC, with automatic fallback to x265 then FFmpeg (`--backend auto|nvenc|x265|ffmpeg`), including a lossless mode.
* **MDKG Container Format:** `Mdkg.fs` implements the `.mdkg` binary layered metadata track format for frame-accurate HEVC/MP4 metadata embedding. Layers: `Timing`(1), `OcrText`(2), `UiAffordance`(3), `SemanticGraph`(4), `InputEvent`(5). Unregistered layer IDs are skipped, so the format is forward-compatible. See `apps/Yumlog.Native/hevc-swarm-out/MDKG-SPEC.md`.
* **Verifiable Runbooks:** `MdkgIntel.fs` turns a recording into ordered steps where every claim carries a resolvable `mdkg://` citation, so a runbook can be checked rather than trusted.
* **On-Device Reasoning:** `reason` answers questions about a recording using a local model through ONNX Runtime GenAI. Fully offline. See [`docs/ON-DEVICE-REASONING.md`](docs/ON-DEVICE-REASONING.md).
* **Live Screen Watching:** `watch` observes the screen and narrates on an interval, skipping frames below a change threshold.
* **Built-in Self Test:** `selftest` runs 16 correctness checks over the container, intelligence layer, and hardware probes, including a negative control on timing coverage.
* **Screen Recording:** Record the desktop at configurable FPS and duration.
* **Screenshot Capture:** Capture periodic screenshots at configurable FPS and duration.
* **Screen Analysis:** Emit JSON image metadata, hashes, dimensions, and OCR provider status.
* **Follow-Me Manifests:** Capture frame sequences, estimate visual changes, and emit navigation-step manifests.
* **Simple CLI:** One-liner PowerShell scripts for all major actions.
* **No Dependencies:** FFmpeg is auto-installed if not present.
* **Scriptable:** Designed for automation and integration in CI/CD or test workflows.
* **Configurable:** All defaults are in `config/tools.json` and `config/yumlog.native.json`.
* **No Telemetry:** 100% local execution, including the reasoning path.
* **Testable:** Built-in Pester test suite plus the native `selftest` suite.

## Project Structure

```
darbot.yumlog/
├── Skills/                 # Reusable PowerShell functions/modules
│   ├── Capture-Screens.ps1
│   ├── Record-Screen.ps1
│   ├── Run-FFmpeg.ps1
│   └── Test-ScreenSkills.Tests.ps1
├── launchers/              # Top-level PowerShell scripts using Skills
│   ├── capture.ps1         # Capture screenshots
│   ├── install.ps1         # Installs FFmpeg if needed
│   ├── record.ps1          # Record screen video
│   └── yumlog.ps1          # Unified yumlog CLI (see below)
├── config/                 # Configuration files
│   └── tools.json          # Default yumlog configuration
│   └── yumlog.native.json  # Native CLI configuration
├── apps/
│   ├── Yumlog.Native/      # F# implementation library (capture, encode, mdkg, llm)
│   ├── Yumlog.Native.Cli/  # Runnable native CLI entry point
│   └── Yumlog.NpuEmulator/ # Rust/Tauri NPU-emulation runtime
├── docs/
│   ├── NATIVE-CLI.md       # Native CLI command reference
│   └── ON-DEVICE-REASONING.md
├── hevc-swarm-out/
│   └── baseline/           # CDP drivers and recording artifacts
├── .github/                # GitHub configuration
│   └── copilot-instructions.md  # GitHub Copilot instructions
├── yumlog-manager.html     # Web-based management interface
└── ...
```

## Getting Started

To get started with darbot.yumlog, set up your environment as follows:

### Prerequisites

* **PowerShell:** Available by default on Windows, installable on macOS/Linux.
* **FFmpeg:** Auto-installed by `install.ps1` if not present.
* **Git:** For cloning the repository.

### Cloning the Repository

```powershell
git clone https://github.com/your-username/darbot.yumlog.git
cd darbot.yumlog
```

## Installation

```powershell
.\launchers\install.ps1
```

## Usage

### Web Interface

Yumlog includes a web-based management interface for easy access to all features. To use it:

1. Open `yumlog-manager.html` in your web browser (double-click the file)
2. Use the visual interface to:
   - Generate PowerShell commands with custom settings
   - View recording statistics
   - Manage recordings and configuration
   - Access command reference

The web interface is completely local and requires no server setup.

### Screen Recording

```powershell
.\launchers\record.ps1 -Fps 30 -DurationSec 10 -OutFile .\myvideo.mp4
```

### Native F# CLI

```powershell
dotnet build .\apps\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj -c Release
$exe = ".\apps\Yumlog.Native.Cli\bin\Release\net10.0-windows10.0.26100.0\win-x64\Yumlog.Native.Cli.exe"

& $exe capture --out-dir .\screenshots --duration 1
& $exe record  --out-file .\out.mp4 --timed-metadata-out .\out.mdkg --duration 10 --fps 30
& $exe analyze --input .\screenshots --ocr auto
& $exe follow  --out-dir .\yumlogs\follow --duration 10 --ocr auto
& $exe runbook --input .\out.mdkg --markdown-out .\runbook.md
& $exe reason  --input .\out.mdkg --provider cpu --question "What happened in this recording?"
& $exe selftest

dotnet msbuild .\apps\Yumlog.Native\Yumlog.Native.fsproj -t:PackageMsix -p:Configuration=Release
```

The native CLI targets `net10.0-windows10.0.26100.0` in this repo because the raw Windows AI path needs versioned WinRT platform projections. It has no PowerShell entry points. The Windows AI OCR integration is capability-gated: `--ocr raw-com` uses F# Windows App Runtime bootstrap plus raw `RoGetActivationFactory`/vtable dispatch for `TextRecognizer.GetReadyState`, and includes raw async-operation/vector traversal helpers for recognized lines and words. Full text extraction still depends on Windows App SDK runtime registration, package/sparse-package identity with `systemAIModels`, NPU-backed hardware, and model readiness.

Note that `bin/` and `obj/` are committed in this repository despite appearing in `.gitignore`. A stale binary can report outdated results, so rebuild before trusting `selftest`. Unknown CLI arguments are currently ignored silently rather than rejected.

### Screenshot Capture

```powershell
.\launchers\capture.ps1 -Fps 2 -DurationSec 5 -OutDir .\myscreens
```

### Run Unit Tests

```powershell
.\launchers\run-tests.ps1
```

- Use `-Verbosity` to control output detail (None, Minimal, Normal, Detailed, Diagnostic).
- Requires Pester v5+ (see README for install instructions).

### Yumlog CLI Commands

The unified CLI is provided by `launchers/yumlog.ps1`:

| Command                | Description                                      |
|------------------------|--------------------------------------------------|
| start yumlog [‑Fps n] [‑DurationSec n] [‑OutFile path] | Start a new yumlog screen recording (video)      |
| pause yumlog           | Pause the current yumlog recording (if supported)|
| stop yumlog            | Stop the current yumlog recording                |
| get yumlog             | Get the latest yumlog file path                  |
| yumlog count           | Number of yumlog files in the default folder     |
| yumlog size            | Cumulative size of all yumlog files              |
| yumlog config          | Show current yumlog configuration (tools.json)   |

## Configuration

Edit `config/tools.json` to change default FPS, duration, and output locations.

## PowerShell Tooling Deep Dive

The Skills/ directory contains reusable PowerShell functions for screen capture, recording, and FFmpeg invocation. Launcher scripts in launchers/ provide user-facing commands. See inline comments for details.

## Contributing

Contributions are welcome! Please fork the repo, create a branch, and submit a pull request.

## License

MIT License