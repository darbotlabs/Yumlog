# hevc-swarm-out — CDP harnesses and recording artifacts

Experiment surface for the parts of Yumlog that cannot be proven by a unit test:
the Edge built-in Prompt API, the on-device model, and the reasoning path over
real recordings.

Two directories share this name and they are **not** duplicates:

| Path | Contents |
|---|---|
| `hevc-swarm-out/` (this one) | CDP drivers and captured `.mdkg` / `.mp4` / runbook artifacts |
| `apps/Yumlog.Native/hevc-swarm-out/` | `MDKG-SPEC.md`, the challenge reviews, and the perf baseline |

Nothing here is part of the build. These are investigation tools, kept because
each one encodes a finding that was expensive to obtain.

## The probe browser

Every script attaches over CDP to an **isolated** Edge Canary profile. It never
touches your normal browser.

```powershell
$edge = "$env:LOCALAPPDATA\Microsoft\Edge SxS\Application\msedge.exe"
Start-Process $edge -ArgumentList @(
  "--user-data-dir=$env:TEMP\yumlog-edge-probe",
  "--remote-debugging-port=9333",
  "--remote-allow-origins=*",
  "--no-first-run", "--no-default-browser-check", "--headless=new"
)
```

Verify with `http://127.0.0.1:9333/json/version`.

Reap it by PID and by command line, never by process name — a name-based kill
takes the user's browser with it:

```powershell
Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" |
  Where-Object { $_.CommandLine -like '*yumlog-edge-probe*' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

The on-device model lives in that profile at
`%TEMP%\yumlog-edge-probe\EdgeLLMOnDeviceModel\` (~1.44 GB). Deleting the
profile deletes the model; `cdp-download.mjs` restores it.

Scripts use node's global `WebSocket`, so there are **no npm dependencies**. The
`ws` package is deliberately not required.

## Scripts

| Script | Purpose |
|---|---|
| `cdp-probe.mjs` | Minimal driver: open a page, evaluate JS, print JSON. Use it to inspect Edge AI flags and model status. |
| `cdp-download.mjs` | Downloads the Edge on-device model into the probe profile and proves it generates. Re-runnable; reports if already present. |
| `cdp-promptapi-test.mjs` | Records what the Prompt API actually does versus what the docs claim. See below. |
| `cdp-primitives.mjs` | Context primitives for hot swapping: `append()` without generation, `contextUsage` growth, overflow, `clone()` continuity, session cost. |
| `cdp-continuous.mjs` | Drives the continuous-context engine end to end and proves context hot swapping occurs, fed by a real runbook. |
| `cdp-verify.mjs` | Loads `yumlog-manager.html` and reports the mdkg Intelligence panel state plus console errors. |
| `cdp-run.mjs` | Full end-to-end: loads a runbook into the manager page and runs `analyzeRunbook()` with a genuine user gesture. |
| `cdp-e2e.mjs` | Exercises `MdkgIntel` pure logic (`compactSteps` + runbook load) against real runbook JSON. No model required. |

### cdp-promptapi-test.mjs

```powershell
node cdp-promptapi-test.mjs [port] [url] [probeFilter]
```

`probeFilter` is a comma-separated allowlist. It exists because the
`constraint-*` probes **crash the on-device model service** and latch a circuit
breaker that only resets on browser restart. Run the safe subset first:

```powershell
node cdp-promptapi-test.mjs 9333 "https://copilotstudio.microsoft.com/" `
  "api-present,availability,plain-prompt,system-prompt-index0,clone-history-rigorous"
```

Findings are recorded in `docs/ON-DEVICE-REASONING.md`. Summary: `clone()`
retains conversation history against its documentation (6/6 across three
origins), `responseConstraint` crashes the model process, `params()` is not
implemented, and the API is present even on an opaque (`"null"`) origin.

**Read the origin, not the URL.** Output reports the origin each probe actually
ran on and warns if one run spanned several. Probing
`copilotstudio.microsoft.com` unauthenticated lands on
`login.microsoftonline.com`, so results labelled with the requested URL would be
false. The harness pins `uniqueContextId` per evaluate so a mid-probe navigation
fails loudly instead of silently answering for a different origin, and retries
races on idempotent probes only.

## Artifacts

`baseline/artifacts/` holds real captures used as fixtures. They are inputs to
documented measurements — regenerating them changes the numbers in the docs.

| Artifact | Notes |
|---|---|
| `r2-churn2.mdkg` / `.mp4` | Primary fixture. `runbook` yields **155 steps, 310 proofs** (two per step). |
| `r2-churn2.runbook.json` / `.md` | Generated runbook, JSON and rendered markdown. |
| `postreview.mdkg` / `.mp4` | Post-review capture. |
| `r2-ffmpeg.mp4` | Legacy FFmpeg-backend capture, for encoder comparison. |

Regenerate a runbook with:

```powershell
.\apps\Yumlog.Native.Cli\bin\Release\net10.0-windows10.0.26100.0\win-x64\Yumlog.Native.Cli.exe `
  runbook --input hevc-swarm-out\baseline\artifacts\r2-churn2.mdkg `
          --markdown-out hevc-swarm-out\baseline\artifacts\r2-churn2.runbook.md
```

## Gotchas

- CLI JSON output has trailing non-JSON text. Slice between the first `{` and
  the last `}` before parsing.
- Unknown CLI arguments are ignored silently — a typo exits 0 and writes
  nothing. Check that the output file was actually created.
- `bin/` and `obj/` are committed despite being in `.gitignore`, so a stale exe
  can report outdated results. Rebuild before trusting `selftest`.
- The harness closes its CDP target on exit. Older runs leaked live tabs that
  contended for the model service and produced timeouts unrelated to the page.
- Never fabricate on-screen content to give a screen-observation tool something
  to look at, and never take over the display without asking. See
  `.github/copilot-instructions.md`.
