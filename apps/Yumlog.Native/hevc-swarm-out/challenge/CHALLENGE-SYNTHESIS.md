# HEVC Recorder Completeness Challenge - Consolidated Synthesis

Fresh red-team fleet of 6 dayour-swe agents, 2026-06-18. Challenged both swarms
(A = native HEVC pipeline, B = .mdkg layered track) on completeness.

## Unanimous verdict: INCOMPLETE (6/6)

| Agent | Target | Swarm | Verdict |
|---|---|---|---|
| ch-capture | WgcCapture | A | INCOMPLETE |
| ch-nvenc | NvencHevc | A | INCOMPLETE - lossless NOT proven |
| ch-x265abs | X265Encoder + Encoder abstraction | A | INCOMPLETE |
| ch-mux | HevcMux ISOBMFF | A | INCOMPLETE |
| ch-mdkg | Mdkg.fs + spec | B | INCOMPLETE |
| ch-integration | end-to-end | A+B | INCOMPLETE |

Integration agent's two decisive answers:
- "If I add all modules to the fsproj, will it compile?" -> NO (incompatible local contracts).
- "Does a recording produce synchronized metadata, or is the track empty?" -> EMPTY. No agent wired an OCR/follow producer into the record loop.

## Cross-cutting BLOCKER #1: the "lossless" claim has a colorspace hole

BOTH encoder paths convert RGB(BGRA) desktop pixels to YUV (NVENC ARGB->internal YUV; x265 BGRA->I444). The RGB->YCbCr transform is NOT reversible, so output is NOT mathematically lossless for RGB desktop content even at 4:4:4 / QP0.
- Fix direction: feed a validated 4:4:4 surface and either (a) use an explicit reversible RGB->YUV444 (e.g. GBR/identity matrix) and verify round-trip, or (b) encode RGB/GBR directly where the build supports it, then decode-and-byte-compare in tests.
- Until then RecordResult.Lossless = true is misleading.

## Cross-cutting BLOCKER #2: nothing is integrated / wired

- New modules exist only as raw markdown (except Mdkg.fs, which is on disk and compiles). fsproj does not reference them. Recording.fs still shells out to ffmpeg gdigrab (violates the no-FFmpeg goal).
- Three incompatible encoded-frame shapes (HevcAccessUnit vs NvencPacket vs EncodedFrame), Backend vs CaptureBackend, mux API mismatch (open/WriteVideoSample vs create/WriteAccessUnit), WGC frame contract mismatch. Canonical reconciliation is in ch-integration.md (contract matrix + one HevcAccessUnit / HevcDecoderConfig).
- The metadata track is structurally emitted but semantically empty: no live producer fills timing/OCR/UI/graph layers during record. Plus the mux writes raw JSON while the design says MDS1 .mdkg carrier - MIME mismatch.

## Per-swarm top findings

### Swarm A - capture (ch-capture)
- [BLOCKER] hybrid-GPU adapter bug: D3D11CreateDevice picks the default adapter (likely Intel UHD 770), but NVENC is on the NVIDIA T1000. A texture on the Intel device cannot be NVENC-registered without a cross-adapter copy -> defeats "NVENC-direct". Must enumerate DXGI adapters and select by LUID/vendor.
- [BLOCKER] no CPU BGRA readback path for the x265 fallback (only a GPU texture is exposed).
- [HIGH] polling instead of FrameArrived; no frame-pool Recreate on size change; SystemRelativeTime not mapped to a real QPC timebase; teardown can release COM while worker still runs; queued textures leak on stop.
- [HIGH] SDR/8-bit only (no Main10/HDR).

### Swarm A - NVENC (ch-nvenc)
- [BLOCKER] malformed GUID `0B514C39A-...` (9 hex digits) -> FormatException at module init.
- [BLOCKER] lossless not proven: no nvEncGetEncodeCaps gating (SUPPORT_YUV444_ENCODE / SUPPORT_LOSSLESS_ENCODE), no input-format/profile/preset validation, RGB->YUV444 reversibility unaddressed.
- [BLOCKER] Dispose() sets disposed<-true before Finish(), so EOS is never emitted; Finish() also doesn't drain the bitstream after EOS.
- [HIGH] not paste-ready (a `(box null :?> NvencEncoder)` placeholder); bufferFormat set on the DX12-only field; no input-format query; single fixed output buffer with no NEED_MORE_OUTPUT retry.
- isNvencAvailable only proves the DLL loads; rename + add a device/caps gate.

### Swarm A - x265 + abstraction (ch-x265abs)
- [BLOCKER] Encoder.fs calls NVENC/WGC/HevcMux APIs the other agents never produced (contract matrix of BROKEN calls).
- [BLOCKER/HIGH] x265 lossless is not true RGB lossless (BGRA->I444 lossy); the hand-declared x265_param struct is ABI-fragile (use x265_param_alloc/default).
- decoder-config/headers (VPS/SPS/PPS) not exposed from the encoder to the mux.

### Swarm A - mux (ch-mux)
- [BLOCKER] missing stss sync-sample box -> HEVC seeking is broken.
- [BLOCKER] hvcC hardcodes 4:2:0 while encoders are 4:4:4 -> decoder-config mismatch; build hvcC from real VPS/SPS/PPS.
- [BLOCKER] hvc1 sample entry but parameter sets likely left in-band (encoders set repeat-headers) -> must strip PS for hvc1 or use hev1.
- [HIGH] mett carrier MIME mismatch (JSON vs .mdkg MDS1); out-of-order samples produce negative stts durations; no crash-safety (moov only at close -> interrupted 4K captures unrecoverable; consider fragmented MP4).

### Swarm B - .mdkg (ch-mdkg)
- [BLOCKER] writer/reader layer-table field-order mismatch -> round-trip corrupts descriptors.
- [BLOCKER/HIGH] semantic-graph layer only sketched: node/edge ids, delta encoding (add/remove/update), and "graph state at T = keyframe + deltas" reconstruction not fully specified -> "Is the semantic-graph layer fully specified and reconstructable?" NO.
- verify 128-byte header field sum, big-endian symmetry, offset back-patching, and that unknown-layer skipping is actually implemented.

## Recommended remediation order (from ch-integration punch list)
1. Land shared contracts in Domain.fs: one HevcAccessUnit, one HevcDecoderConfig, one HevcBackend.
2. Reconcile each module to those contracts; add the canonical fsproj compile order
   (Domain, Json, Config, RuntimeIdentity, WinAppRuntime, RawWinRt, Mdkg, WgcCapture,
   NvencHevc, X265Encoder, Encoder, HevcMux, Capture, Recording, WindowsAiOcr, Ocr,
   NativeUi, Analysis, Navigation, Orchestration, Cli, Program).
3. Fix the concrete defects (NVENC GUID, Dispose/Finish EOS, mux stss + hvcC + hvc1 PS, mdkg field-order).
4. Resolve the colorspace question (reversible RGB->YUV444 or RGB/GBR) and gate NVENC on real caps; x265 fallback honest about losslessness.
5. Wire Recording.fs end-to-end: WGC -> encode -> mux video + live .mdkg metadata producer (timing always; OCR/UI/graph where available) -> close.
6. Adapter selection for the T1000/Intel hybrid topology.
7. Make the test plan runnable: lossless decode-and-compare, frame-count, metadata-sync.

Full per-agent reports: ch-mdkg.md, ch-mux (inline), ch-x265abs.md, ch-capture (inline),
ch-nvenc (inline), ch-integration.md in this folder.
