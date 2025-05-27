# darbot.yumlog

Welcome to darbot.yumlog.

darbot.yumlog is a PowerShell-based screen capture and logging utility for Windows, designed for automation, reproducibility, and integration with other tools. It provides high-performance desktop video recording and periodic screenshot capture using FFmpeg, with simple CLI commands and scriptable workflows.

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

* **Screen Recording:** Record the desktop at configurable FPS and duration to MP4 using FFmpeg.
* **Screenshot Capture:** Capture periodic screenshots at configurable FPS and duration.
* **Simple CLI:** One-liner PowerShell scripts for all major actions.
* **No Dependencies:** FFmpeg is auto-installed if not present.
* **Scriptable:** Designed for automation and integration in CI/CD or test workflows.
* **Configurable:** All defaults are in `config/tools.json`.
* **No Telemetry:** 100% local execution.
* **Testable:** Built-in Pester test suite to verify functionality.

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

### Screen Recording

```powershell
.\launchers\record.ps1 -Fps 30 -DurationSec 10 -OutFile .\myvideo.mp4
```

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