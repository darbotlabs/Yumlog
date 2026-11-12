# Yumlog.Native baseline

Captured 2026-08-03 on Windows NT 10.0.29639.0, 24 logical CPUs,
NVIDIA T1000 8GB + Intel UHD Graphics 770 (hybrid graphics).

Regenerate with:

```powershell
cd apps\Yumlog.Native\hevc-swarm-out\baseline
.\Measure-Baseline.ps1 -DurationSec 10 -Fps 30
```

The script writes `artifacts\baseline.json` so successive runs can be diffed.

## Functionality

| Surface | Result |
|---|---|
| `Yumlog.Native.Cli` build (Debug) | Succeeds, 0 errors |
| `Yumlog.Native` (WinExe) build | Succeeds, 0 errors |
| `selftest` | 15/15 PASS (as captured 2026-08-03; the suite has since grown to **16/16** — rebuild before comparing) |
| `runbook` on a real 300-frame recording | exit 0, 600/600 proofs resolved, 0 drifted |
| `record --backend auto` (NVENC) | exit 0 |
| `record --backend ffmpeg` (legacy) | exit 0 |
| `config show` / `config init` | OK |
| `capture` | OK |
| `launchers\yumlog.ps1 config` | OK |
| `Yumlog.Native.exe` (WinExe) | Does not exit on `--help`; it is a windowed app, not a console entry point |

## Performance, 10 s at 30 fps, 3840x2160

| Metric | Native (NVENC) | Legacy (FFmpeg gdigrab) |
|---|---|---|
| Wall time | 10 626 ms | 10 854 ms |
| Process CPU | 1 172 ms | 188 ms (parent only, see caveat) |
| CPU as % of one core | 11.0 % | 1.7 % |
| Peak working set | 308.5 MB | 34.6 MB |
| Output size | 2.15 MB | 0.33 MB |
| Container bitrate | 1.71 Mbps | 0.27 Mbps |
| Video codec / tag | hevc / **hvc1** | hevc / hev1 |
| Pixel format | **yuv444p** | yuv420p |
| Streams | **2** (video + `mett`) | 1 |
| Frames reported | 300 | not tracked |
| Dropped frames | **0** | not tracked |
| Metadata samples | **300** | 0 |

**Caveat on FFmpeg CPU.** The legacy path spawns `ffmpeg.exe` as a child
process. `TotalProcessorTime` on the parent does not include it, so 188 ms
understates the real cost by a wide margin. The two CPU figures are not
comparable as written. Native CPU is measured correctly because encoding
happens in-process on the GPU.

## Frame pacing

Derived from the timing layer's presentation timestamps against a 33.333 ms
nominal interval, over 300 samples:

| Metric | Value |
|---|---|
| Mean interval | 33.556 ms |
| Mean jitter | 0.224 ms |
| Max interval | 99.998 ms |
| Max jitter | 66.664 ms |

Mean jitter under a quarter millisecond is good. The single 100 ms interval is
one dropped presentation, three nominal frame times. This is expected
behaviour rather than a defect: Windows Graphics Capture only produces a frame
when the desktop actually changes, and the baseline was recorded against a
mostly static desktop. It is called out because a frame count alone would hide
it, and because a capture tool that claims exact repeatability has to be able
to show its own pacing.

## Defects found by this baseline

1. **`--backend ffmpeg` was rejected** (`Unknown HEVC backend 'ffmpeg'`). The
   config layer validated the record backend against the HEVC *encoder* set,
   which does not include the legacy pipeline, so the fallback path was
   unreachable from the CLI. Fixed by giving the record backend its own
   normalizer that is a superset of the encoder backends.
2. **FFmpeg was never found.** `resolveFfmpeg` looked only at
   `.tools\ffmpeg\bin\ffmpeg.exe`, but the installer extracts to a
   build-specific subdirectory, so the real binary sat one level deeper.
   Fixed by walking up for a `.tools` directory and searching it recursively.
   Also corrected the resolver's fallback, which returned the literal string
   `ffmpeg` before testing any concrete path.
3. **`--out` was ignored** on `record`; only `--out-file` was read, so output
   silently went to the configured default. Fixed.

## Pre-existing issues, not addressed

- `Skills\*.Tests.ps1` use Pester 3 `Should Be` syntax and fail under the
  installed Pester 5.7.1: 3 passed, 4 failed. The repository targets Pester
  3.4.0, so these tests need migration or a pinned Pester version.
- `launchers\yumlog.ps1` emits `Get-ChildItem` errors for `get`, `count`, and
  `size` when the output directory does not exist. Return values are still
  correct (`0`, `0 MB`, `No yumlog found`); only the noise is wrong.
