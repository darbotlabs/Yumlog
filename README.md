# darbot.yumlog

## Screen Capture & Recording

darbot.yumlog provides PowerShell-based tools for desktop screen capture and recording, leveraging FFmpeg for high performance and flexibility. These tools are useful for creating demo assets, bug reports, or automated test evidence.

### Web Interface

For a visual interface to manage, configure, and use Yumlog, open `yumlog-manager.html` in your web browser. The HTML interface provides:

- Easy command generation for recording and capturing
- Statistics overview
- Configuration viewer
- PowerShell command reference

Simply double-click `yumlog-manager.html` to launch the interface in your default browser.

### Usage Examples

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

---

## Project Tree (excerpt)

```text
darbot.yumlog/
├── Skills/
│   ├── Capture-Screens.ps1
│   ├── Paperboy.ps1
│   ├── paperboy.js
│   ├── Record-Screen.ps1
│   └── Run-FFmpeg.ps1
├── launchers/
│   ├── install.ps1
│   ├── Record-Terminals.ps1
│   └── yumlog.ps1
├── bisect-test/
│   ├── bisect-extractor.html
│   ├── library.html
│   └── terminal-viewer.html
├── config/
│   └── tools.json
├── .github/
│   └── copilot-instructions.md
├── yumlog-manager.html
└── ...
```

---

For more details, see `Tasklist.md` and inline comments in each script.
