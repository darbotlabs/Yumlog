# hevc-swarm-out — specification and design record

Design inputs for the native HEVC pipeline and the `.mdkg` container. This is
the **spec and review** half of the work; the CDP harnesses and captured
recordings live in the repo-root `hevc-swarm-out/` and are a different thing
despite the shared name.

Nothing here is compiled. `MDKG-SPEC.md` is normative; everything else is a
point-in-time record kept for its reasoning.

## Contents

| Path | What it is |
|---|---|
| `MDKG-SPEC.md` | **Normative.** Full byte-layout specification for the `.mdkg` layered metadata track. 1,280 lines. The authority for layer IDs, timescale, and the unknown-layer rule. |
| `baseline/BASELINE.md` | Captured performance and functionality baseline, with the script to regenerate it. |
| `baseline/Measure-Baseline.ps1` | Regenerates `baseline/artifacts/baseline.json` so successive runs can be diffed. |
| `baseline/artifacts/` | Baseline captures, pre- and post-hardening. |
| `challenge/` | Red-team reviews of the design by a fleet of review agents. |
| `*.raw.md` | Raw per-agent transcripts from the design swarm: WGC capture, NVENC HEVC, x265 fallback, MP4/MKV mux and metadata, test strategy. |

## Reading order

1. `MDKG-SPEC.md` for the container format — this is the one that governs code.
2. `challenge/CHALLENGE-SYNTHESIS.md` for the consolidated critique.
3. The individual `challenge/ch-*.md` files for detail on the container
   (`ch-mdkg`), encoder contracts (`ch-x265abs`), and integration and test
   coverage (`ch-integration`).
4. The `*.raw.md` transcripts only when you need the original reasoning behind a
   decision. They are verbose and unedited.

## The challenge documents are historical

They were written against an earlier tree and some of their findings have since
been addressed. `ch-mdkg.md` states that no `HevcMux.fs` could be found; that
module now exists. Treat every challenge finding as a claim to re-verify against
current source, not as a live defect list.

Where spec and code disagree, the code is what ships — check
`apps/Yumlog.Native/Mdkg.fs` before trusting a spec line number. The `LayerId`
module there is the authority on layer IDs, and it has already contradicted
documentation once:

```
Timing = 1, OcrText = 2, UiAffordance = 3, SemanticGraph = 4, InputEvent = 5
```

## Regenerating the baseline

```powershell
cd apps\Yumlog.Native\hevc-swarm-out\baseline
.\Measure-Baseline.ps1 -DurationSec 10 -Fps 30
```

Rebuild the CLI first. `bin/` and `obj/` are committed despite appearing in
`.gitignore`, so a stale binary silently reports outdated results — this has
already caused `selftest` to under-report its own case count.
