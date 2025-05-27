# darbot.yumlog

## Screen Capture & Recording

darbot.yumlog provides PowerShell-based tools for desktop screen capture and recording, leveraging FFmpeg for high performance and flexibility. These tools are useful for creating demo assets, bug reports, or automated test evidence.

### Usage Examples

#### Capture Periodic Screenshots

```powershell
# Capture screenshots at 5 FPS for 30 seconds
.\launchers\capture.ps1 -Fps 5 -DurationSec 30
```

#### Record Full-Motion Desktop Video

```powershell
# Record the desktop at 30 FPS for 15 seconds
.\launchers\record.ps1 -Fps 30 -DurationSec 15
```

#### Run Unit Tests

```powershell
# Executes Pester with detailed output
.\launchers\run-tests.ps1
```

- Output files are saved to the current directory by default, or as specified by parameters.
- Both scripts support custom FPS and duration.
- FFmpeg is automatically installed if not present (via `install.ps1`).

---

### Testing

- The test launcher supports a `-Verbosity` parameter: `None`, `Minimal`, `Normal`, `Detailed`, or `Diagnostic` (default: `Detailed`).
- Compatible with Pester 3.4.0 (included in Windows PowerShell).
- Example:

```powershell
.\launchers\run-tests.ps1 -Verbosity Minimal
```

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

---

## Project Tree (excerpt)

```
darbot.yumlog/
├── Skills/
│   ├── Capture-Screens.ps1
│   ├── Record-Screen.ps1
│   └── Run-FFmpeg.ps1
├── launchers/
│   ├── capture.ps1
│   ├── install.ps1
│   └── record.ps1
├── config/
│   └── tools.json
└── ...
```

---

For more details, see `Tasklist.md` and inline comments in each script.
