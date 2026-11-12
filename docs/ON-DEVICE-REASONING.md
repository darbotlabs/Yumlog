# On-Device Reasoning

Yumlog can answer questions about a recording using a local LLM. Nothing leaves
the machine.

```powershell
& $exe reason --input .\out.mdkg `
              --question "At what frame rate was this captured, and were any frames dropped?" `
              --provider cpu --max-tokens 140
```

```
The user is asking about the frame rate and whether any frames were dropped in
the capture. The provided telemetry shows that the capture frame rate is 15 fps.
There are no frames dropped in the capture process.
```

> **Answer style varies between runs.** The model often restates the question
> before answering, and occasionally stops *after* the restatement without
> producing the facts at all. Re-running resolves it. Measured across
> `--max-tokens` 60, 100, and 200 the facts were present and correct every time,
> so this is sampling variance and not a token-budget truncation. If you consume
> `reason` programmatically, assert that the answer contains the value you need
> rather than assuming the first run is usable.

## How grounding works

`reason` walks the `.mdkg` timeline, selects segments, and builds a text
**grounding** block that is handed to the model. This is the *only* channel the
model has.

`stepLine` ingests exactly three fields per step:

```
text=   ui=   graph=
```

Recording-level constants (frame rate, dropped-frame count, codec) are not
per-step, so they are appended to the grounding block separately in
`OnDeviceLlm.fs`:

```fsharp
| Some fps     -> $"; capture frame rate={fps} fps"
| Some dropped -> $"; frames dropped by the capture pipeline={dropped}"
```

### The consequence, stated plainly

**Runbook proofs are never sent to the model.** `MdkgIntel.fs` emits citable
proofs for `/fps`, `/durationTicks`, and `/dropCount`, and those are correct and
resolvable — but they feed `runbook`, not `reason`. Adding proofs changed the
runbook output and left the model's answer byte-identical.

If you want the model to know a fact, it must be in the grounding string.

> **A diagnostic trap.** `contextTokens` equals `segmentTokens` and does **not**
> count the grounding block. An unchanged `contextTokens` therefore does *not*
> prove the grounding text is unchanged. Do not use it to verify a grounding
> edit landed.

## Frame-rate accuracy

An early build answered "30 fps" for a 15 fps recording. That was a grounding
gap, not a capture regression: the sidecar said 15, the PTS-derived effective
rate was 15.00, and the model was simply guessing a plausible constant.

Verified after the fix by recording at three rates and asking the identical
question:

| Recorded | Reported rate | Frames | Coverage overlaps |
|---|---|---|---|
| 12 fps | 12 fps, no frames dropped | 37 | 0 |
| 15 fps | 15 fps, no frames dropped | 61 | 0 |
| 30 fps | 30 fps, no frames dropped | 122 | 0 |

A single passing rate would not have distinguished a correct read from a latched
constant. Three do.

Timing detail for the 15 fps run: median inter-frame gap 666,655 ticks against a
nominal 666,667 (0.002% off, pure 60 Hz quantisation). The min/max spread
(333,325 / 999,980) is the grid-anchored pacing correcting phase — an early frame
is offset by a late one, so jitter never shifts the mean.

## Stop-marker handling

The chat template emits terminator markers. Three consecutive runs produced three
*different* malformed terminators:

```
</|im_start|>      </im      </assistant|>
```

Enumerating known-bad strings was an unwinnable game. The list was replaced with
two sentinels that cannot occur in telemetry prose:

```fsharp
let private stopMarkers = [| "<|"; "</" |]
```

Streaming introduces a second problem: emitting each chunk immediately can leak a
*partial* marker before the full marker is recognised. The fix is `danglingMarkerLength`,
which holds back the longest suffix of the buffer that is a proper prefix of any
marker. The streamed output and the returned answer are now byte-identical.

## Providers and models

```powershell
& $exe models
```

```json
[ { "version": "2026.5.30.1", "modelType": "qwen3",
    "contextLength": 8192, "configuredProvider": "webgpu",
    "weightMegabytes": 1440.6 } ]
```

`--provider cpu` runs through ONNX Runtime GenAI on CPU and is the most portable
path. Yumlog loads model weights directly; it does **not** call Edge's model
service, so Edge-side failures cannot affect `reason`.

`hevc-swarm-out\baseline\cdp-download.mjs` restores the on-device model if it is
deleted. Keep it — its absence once made a deletion unrecoverable without
archaeology.

## Edge built-in Prompt API: measured behaviour

Findings from `hevc-swarm-out\baseline\cdp-promptapi-test.mjs`, run against live
`https://` pages. Environment: Edge Canary 152.0.4190.0, Aion-1.0-Instruct
2026.5.30.1. Origins tested: `example.com`, an opaque sandboxed child origin, and
`copilotstudio.microsoft.com` (an auth-gated enterprise SPA).

| Behaviour | Result |
|---|---|
| `responseConstraint` (JSON schema, string, enum, regex) | **Crashes the model process.** `UnknownError: kErrorUnknown` |
| Session state after a constraint failure | **Destroyed.** Later prompts throw `InvalidStateError` |
| Repeated attempts | `NotSupportedError: The model process crashed too many times for this version.` Resets on browser restart |
| `clone()` | **Retains conversation history**, contradicting the documentation. Reproduced 6/6 across all three origins |
| System prompt at non-zero index | Rejects with `TypeError`, as documented |
| `params()` | Not implemented in this build |
| Availability on an enterprise SPA | `"available"` — no CSP or service-worker interference observed |
| Availability on an opaque (`"null"`) origin | Binding **present**. The API is not gated on a tuple origin |

### Navigation races on auth-gated sites

On `copilotstudio.microsoft.com` the API itself behaves identically to
`example.com`, but the **page** introduces a failure mode that static sites do
not: the site redirects to `login.microsoftonline.com`, and if that redirect
lands mid-prompt the execution context is torn down and the in-flight call dies
with `Inspected target navigated or closed`.

This is not an API defect. The identical probe succeeded on a re-run, and later
probes in the same pass reported `login.microsoftonline.com` as their origin —
the redirect had simply already completed by then.

### How the harness fixes it

The naive fix — sleep a couple of seconds before probing — does not work. The
page can navigate at *any* point, not just at startup, so a fixed wait is both
too slow on settled pages and insufficient on redirecting ones. The harness uses
three layers instead, and only the second is load-bearing:

1. **Settle on navigation events, not a timer.** Wait for a live default
   execution context whose origin is non-opaque, preferring a quiet window since
   the last main-frame navigation. Quiescence is *preferred, never required* —
   an unauthenticated MSAL flow can redirect-loop indefinitely, and requiring
   quiet there deadlocks. After a short budget the harness proceeds regardless.
2. **Pin `uniqueContextId` on every evaluate.** This is what makes the result
   trustworthy. If the document was replaced between settling and evaluating,
   CDP fails the call outright rather than silently running the probe against a
   different origin than the one reported.
3. **Retry only idempotent probes.** A race is retried up to twice. The
   `constraint-*` probes are never retried: they crash the model service and
   latch a circuit breaker, so a retry compounds the damage instead of
   recovering.

Each probe records the origin it actually ran on, and the harness prints a
warning if a single run spanned more than one origin.

Two lessons worth carrying beyond this harness:

- **Report the origin actually reached, not the URL requested.** A result
  labelled `copilotstudio.microsoft.com` may have been measured on the login
  origin. Every probe now carries its own origin for this reason.
- **A single failure on a redirecting site proves nothing.** `kErrorUnknown` is
  a model crash; `Inspected target navigated or closed`, `uniqueContextId not
  found`, and `Execution context was destroyed` are races. Classify before
  concluding.

Two bugs found while building this fix, both worth remembering:

- **Never null a context on `Runtime.executionContextsCleared`.** That event can
  arrive *after* the replacement context's `executionContextCreated`, so acting
  on it discards a live context and deadlocks re-acquisition.
- **Never block solely on holding a pinned context.** If a context event is
  missed, fall back to an unpinned evaluate. Losing an event should degrade
  accuracy, not hang the run. The harness also closes its CDP target on exit;
  without that, repeated runs leak live tabs that contend for the model service
  and cause unrelated timeouts.

### Why this does not matter for Yumlog

`responseConstraint` constrains **form, not fact**. Even working perfectly, a
JSON schema could not have turned "30" into "15" — the bug was that the model was
never told the number. Constrained decoding is a formatting tool, not a grounding
tool. Reach for it to guarantee parseable output, never to guarantee true output.

## Experimental-design lessons

Three tests in this area nearly produced confident false results.

1. **A crash-per-session invalidates every later attempt.** Reusing one session
   across constraint cases made cases 2..n false negatives. Use a fresh session
   per case plus a post-failure health check.
2. **Refusal reads as amnesia.** Asking a model to "list every secret codeword"
   triggers a privacy refusal even when it plainly holds the word. The tell is
   that the *parent* session also claims ignorance. Use phrasing demonstrably
   shown to elicit recall, and always test the control.
3. **CDP probes must wait for navigation.** A fresh target starts at
   `about:blank`; probing before the document commits silently tests an opaque
   origin. Detectable via execution-context id prefixes (`.1.x` vs `.2.x`).
