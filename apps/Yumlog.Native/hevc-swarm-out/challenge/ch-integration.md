Agent is idle (waiting for messages). agent_id: ch-integration, agent_type: dayour-swe, status: idle, description: Challenge integration + test coverage, elapsed: 211s, total_turns: 1

[Turn 0]
## Summary

The integrated deliverables are not complete end-to-end. Individually, the agents produced plausible component sketches, but the combined system does not currently compile, does not replace the FFmpeg recorder path, does not wire capture to encode to mux, and does not produce synchronized live metadata samples.

## Findings

1. [CRITICAL] The whole does not compile as-is.
   - `Yumlog.Native.fsproj` currently references none of the new native HEVC modules.
   - The delivered modules do not agree on contracts:
     - `Recording.fs` from the x265 agent expects `WgcCapture.create`, `WgcCapture.CaptureOptions`, and `IWgcFrameSource`, but `WgcCapture.fs` actually exposes `startForDuration`, `WgcSession`, and `CapturedFrame`.
     - `Encoder.fs` expects `NvencHevc.create options`, `EncodeTexture`, and `Flush`, but `NvencHevc.fs` exposes `create d3dDevice width height fps`, `encodeFrame`, `finish`, and `dispose`.
     - `Recording.fs` expects `HevcMux.create`, `MuxOptions`, `WriteAccessUnit`, and `Finish`, but `HevcMux.fs` exposes `HevcMux.open path width height fps hvcCOrFirstNals`, `WriteVideoSample`, `WriteMetadataSample`, and `Close`.
   - `RecordConfig` and `RecordResult` have conflicting additive fields across agents.

2. [CRITICAL] The current product still records through FFmpeg.
   - Current `Recording.fs` shells out to `ffmpeg` with `gdigrab`.
   - It forces `-pix_fmt yuv420p`, which directly violates the stated lossless desktop HEVC requirement.
   - `Program.fs` routes `record` to `NativeRecording.record`, but that is still the FFmpeg implementation.

3. [CRITICAL] Metadata is not wired into live recording.
   - The current recorder produces no `.mdkg`, no MP4 timed metadata track, and no per-frame OCR/navigation metadata.
   - The x265 rewritten `Recording.fs` loops `capture -> encode -> mux.WriteAccessUnit`, but does not call `HevcMux.metadataPayloadJson`, `WriteMetadataSample`, `Mdkg.WriteTiming`, `Mdkg.WriteOcrText`, or `Mdkg.WriteUiAffordance`.
   - The test goal requires metadata from analyze/follow OCR/navigation; no live metadata producer exists.

4. [HIGH] The muxer cannot be opened from the encoder abstraction as delivered.
   - `HevcMux.Muxer` requires `hvcCOrFirstNals` at construction.
   - Neither `Encoder.fs`, `NvencHevc.fs`, nor `X265Encoder.fs` exposes a canonical `DecoderConfig`, `hvcC`, or VPS/SPS/PPS property before mux open.
   - x265 uses `repeat-headers=1`, so VPS/SPS/PPS may appear in the first access unit, but the recording loop opens the mux before the first encoded access unit exists.

5. [HIGH] The native capture-to-NVENC GPU path is incomplete.
   - WGC exposes `WgcSession.D3D11Device` and AddRef-owned `CapturedFrame.Texture`.
   - NVENC creation requires the same `ID3D11Device`.
   - The proposed recording glue does not pass `session.D3D11Device` into `NvencHevc.create`.
   - It also does not release each WGC texture with `WgcCapture.releaseFrameTexture`.

6. [HIGH] x265 fallback is not truly desktop-RGB lossless as written.
   - `X265Encoder.fs` converts BGRA to I444 using a BT.709 floating-point conversion.
   - That preserves full chroma resolution, but it is not a reversible byte-exact RGB/BGRA round trip.
   - This may be lossless in the encoder’s YUV444 input domain, not necessarily lossless against original desktop pixels.

7. [HIGH] CLI surface is not actually updated.
   - `Cli.fs` usage still documents `record ... [--ffmpeg path]`.
   - The parser accepts arbitrary `--key value`, but `NativeConfig.mergeRecord` currently ignores `--backend`, `--container`, `--x265`, `--lossless`, `--capture-backend`, `--monitor-index`, and metadata options.
   - Therefore `record --backend nvenc` currently has no effect.

8. [MEDIUM] Native dependencies are not packaged.
   - `nvEncodeAPI64.dll` is driver-provided, acceptable.
   - `x265.dll` must be shipped or resolved explicitly; there is no project content item or packaging rule for it.
   - WGC is OS-provided via raw WinRT, so no managed projection is strictly required.
   - Existing `Microsoft.WindowsAppSDK.AI` remains for OCR; no additional managed projection should be necessary if the raw WinRT approach is retained.

9. [MEDIUM] Test plan is directionally strong but not runnable against the current CLI.
   - It assumes test-only synthetic source hooks that do not exist.
   - It assumes native recorder backend selection that is not wired.
   - It assumes timed metadata extraction from produced MP4, but the current recorder produces no such stream.
   - It does not currently validate the concrete WGC texture lifetime, same-device NVENC handoff, `hvcC` extraction, or x265 packaging.

---

## (a) Canonical fsproj Compile order

This is the correct top-down order after contract reconciliation. It is not sufficient by itself to compile the as-is deliverables because the APIs conflict, but it is the correct intended order once the contracts are fixed.

```xml
<ItemGroup>
  <Compile Include="Domain.fs" />
  <Compile Include="Json.fs" />
  <Compile Include="Config.fs" />

  <Compile Include="RuntimeIdentity.fs" />
  <Compile Include="WinAppRuntime.fs" />
  <Compile Include="RawWinRt.fs" />

  <Compile Include="Mdkg.fs" />
  <Compile Include="WgcCapture.fs" />
  <Compile Include="NvencHevc.fs" />
  <Compile Include="X265Encoder.fs" />
  <Compile Include="Encoder.fs" />
  <Compile Include="HevcMux.fs" />

  <Compile Include="Capture.fs" />
  <Compile Include="Recording.fs" />
  <Compile Include="WindowsAiOcr.fs" />
  <Compile Include="Ocr.fs" />
  <Compile Include="NativeUi.fs" />
  <Compile Include="Analysis.fs" />
  <Compile Include="Navigation.fs" />
  <Compile Include="Orchestration.fs" />
  <Compile Include="Cli.fs" />
  <Compile Include="Program.fs" />
</ItemGroup>
```

### Dependency notes

- `Domain.fs` must stay first because nearly every module consumes domain records.
- `Json.fs` must precede `Config.fs`.
- `RawWinRt.fs` must precede `WgcCapture.fs` and `WindowsAiOcr.fs`.
- `Mdkg.fs` must precede `HevcMux.fs` if MP4 metadata samples carry `Mdkg.Carrier.encode`.
- `NvencHevc.fs` and `X265Encoder.fs` must precede `Encoder.fs`.
- `Encoder.fs` must precede `Recording.fs`.
- `HevcMux.fs` must precede `Recording.fs`.
- No unavoidable circular dependency exists if shared contracts are moved into either `Domain.fs` or a pre-encoder contract module. As written, the circularity is logical rather than F# topological: components each invented incompatible local contracts.

---

## (b) CONTRACT/CONFLICT matrix

| Area | Current / Delivered contracts | Conflict | Canonical reconciliation |
|---|---|---|---|
| `RecordConfig` | Current has `OutFile`, `Fps`, `DurationSec`, `FFmpegPath`, `Encoder`, `Preset`. WGC adds `CaptureBackend`, `MonitorIndex`, `Container`, `TimedMetadataOut`, `DisableCaptureBorder`. x265 adds `X265Path`, `Backend`, `Codec`, `Lossless`, `Container`. | Multiple agents add different fields. `Container` overlaps. `Backend` vs `CaptureBackend` are different concepts. | Keep all additive fields with clear meaning: `Backend` for encoder backend, `CaptureBackend` for capture source, `X265Path`, `Codec`, `Lossless`, `Container`, `MonitorIndex`, `TimedMetadataOut`, `DisableCaptureBorder`, legacy `FFmpegPath`, legacy `Encoder`, `Preset`. |
| `RecordResult` | Current has `Path`, `Fps`, `DurationSec`, `Encoder`, `ExitCode`, `StandardError`. x265 adds `Backend`, `Codec`, `Lossless`, `Container`, `FrameCount`. | No metadata counts or backend diagnostics. | Add `Backend`, `Codec`, `Lossless`, `Container`, `FrameCount`, `MetadataSampleCount`, `MetadataPath`, `ExitCode`, `StandardError`. |
| Encoder backend type | x265 adds `EncoderBackend`; `Encoder.fs` defines `HevcBackend`. | Duplicate concept under different names. | Use one type, preferably `HevcBackend = Nvenc | X265 | Auto`, in a shared pre-encoder contract location or `Encoder.fs`; config remains string for JSON compatibility. |
| WGC frame contract | WGC delivers `CapturedFrame = { Texture; SharedHandle; Width; Height; QpcTime }` and `WgcSession.Frames`. Recording expects `CaptureFrame = { Width; Height; Pts; TimestampUtc; CpuBgra; CpuStride; TextureHandle }` and `IWgcFrameSource.Frames()`. | Direct compile break. No CPU BGRA readback API for x265. No UTC timestamp. | Add a WGC adapter or change `WgcCapture` to expose `CaptureOptions`, `CaptureFrame`, `IWgcFrameSource`, `create`, `D3D11Device`, and guaranteed texture release semantics. |
| NVENC contract | `NvencHevc.create d3dDevice width height fps`; `encodeFrame encoder texture qpcTime`; `finish`. Encoder adapter expects `NvencHevc.create options`, object with `EncodeTexture` and `Flush`. | Direct compile break. | Either adapt `Encoder.NvencAdapter` to actual NVENC API and pass WGC `D3D11Device`, or change `NvencHevc.fs` to implement the adapter’s expected object contract. |
| x265 contract | `X265Encoder.X265HevcEncoder.EncodeBgra` returns `EncodedFrame`. `Encoder.fs` maps to `HevcAccessUnit`. | Mostly reconcilable, but no decoder config/VPS-SPS-PPS property. | Expose `HevcAccessUnit` and `DecoderConfig` or `GetHeaders()` from the encoder abstraction. |
| HEVC sample contract | `Encoder.fs` defines `HevcAccessUnit`. `NvencHevc.fs` defines `NvencPacket`. `X265Encoder.fs` defines `EncodedFrame`. `HevcMux.fs` accepts raw `byte array`. | Three incompatible encoded-frame shapes. | Define one canonical `HevcAccessUnit = { Pts; Dts; Data; IsKeyFrame; QpcTime; DecoderConfig option }`. |
| Mux API | `HevcMux.fs` exposes `open`, `WriteVideoSample`, `WriteMetadataSample`, `Close`. Recording expects `MuxOptions`, `create`, `WriteAccessUnit`, `Finish`. | Direct compile break. | Add an adapter `HevcMux.create : MuxOptions -> IHevcMuxer`, or rewrite Recording to use `HevcMux.open` after obtaining `hvcC` or first VPS/SPS/PPS. |
| Metadata schema | `HevcMux.metadataPayloadJson` writes compact JSON. `Mdkg.fs` defines layered `.mdkg` plus carrier sample model. Test plan expects frame metadata with OCR and hints. | No single metadata producer or carrier policy. | Canonical: `.mdkg` sidecar is authoritative; MP4 metadata samples carry `Mdkg.Carrier.encode` for timing, OCR, UI affordance, semantic graph, and input events. |
| CLI | Current usage exposes FFmpeg. x265 suggests backend/container/x265 flags. WGC suggests monitor/capture metadata flags. | User cannot intentionally invoke the native recorder. | Update `Cli.fs` usage and `Config.mergeRecord` for all record flags. |
| Native dependencies | x265 requires `x265.dll`; NVENC driver DLL; WGC OS APIs. | x265 not packaged. | Add publish/package copy logic for `x265.dll`; no new managed projections unless raw WinRT approach is abandoned. |

---

## Canonical `Domain.fs` / `Config.fs` change-set

### `Domain.fs`

```fsharp
[<CLIMutable>]
type RecordConfig =
    { OutFile: string
      Fps: int
      DurationSec: int

      // Legacy compatibility. Should not be used by native HEVC path.
      FFmpegPath: string
      Encoder: string

      // Native HEVC path.
      Backend: string          // auto | nvenc | x265
      Codec: string            // hevc
      Lossless: bool
      Preset: string
      Container: string        // mp4 initially; mkv not implemented yet
      X265Path: string

      // Capture path.
      CaptureBackend: string   // wgc initially
      MonitorIndex: int
      DisableCaptureBorder: bool

      // Metadata.
      TimedMetadataOut: string }
```

```fsharp
[<CLIMutable>]
type RecordResult =
    { Path: string
      Fps: int
      DurationSec: int
      Encoder: string
      Backend: string
      Codec: string
      Lossless: bool
      Container: string
      FrameCount: int
      MetadataSampleCount: int
      MetadataPath: string
      ExitCode: int
      StandardError: string }
```

### `Config.fs`

```fsharp
Record =
    { OutFile = "./yumlogs/yumlog.mp4"
      Fps = 30
      DurationSec = 10
      FFmpegPath = ""
      Encoder = "hevc"
      Backend = "auto"
      Codec = "hevc"
      Lossless = true
      Preset = "medium"
      Container = "mp4"
      X265Path = ""
      CaptureBackend = "wgc"
      MonitorIndex = 0
      DisableCaptureBorder = true
      TimedMetadataOut = "" }
```

`mergeRecord` must parse at least:

```fsharp
{ baseConfig with
    OutFile = get "out-file" baseConfig.OutFile
    Fps = get "fps" (string baseConfig.Fps) |> Int32.Parse
    DurationSec = get "duration" (string baseConfig.DurationSec) |> Int32.Parse
    FFmpegPath = get "ffmpeg" baseConfig.FFmpegPath
    Encoder = get "encoder" baseConfig.Encoder
    Backend = get "backend" baseConfig.Backend |> normalizeBackend
    Codec = get "codec" baseConfig.Codec
    Lossless = getBool "lossless" baseConfig.Lossless
    Preset = get "preset" baseConfig.Preset
    Container = get "container" baseConfig.Container |> normalizeContainer
    X265Path = get "x265" baseConfig.X265Path
    CaptureBackend = get "capture-backend" baseConfig.CaptureBackend
    MonitorIndex = get "monitor-index" (string baseConfig.MonitorIndex) |> Int32.Parse
    DisableCaptureBorder = getBool "disable-capture-border" baseConfig.DisableCaptureBorder
    TimedMetadataOut = get "timed-metadata-out" baseConfig.TimedMetadataOut }
```

---

## (c) Prioritized remaining-work punch list

1. [BLOCKER] Owner: Integration / `Recording.fs`
   - Replace the current FFmpeg recorder with native control flow:
     - create WGC source,
     - select encoder,
     - obtain dimensions and D3D device,
     - obtain HEVC decoder config or first VPS/SPS/PPS,
     - open mux,
     - per frame: encode, write video sample, produce metadata, write metadata sample,
     - flush encoder,
     - close mux and `.mdkg`.

2. [BLOCKER] Owner: Contracts / `Domain.fs`, `Encoder.fs`
   - Define one shared encoded-frame contract:
     ```fsharp
     type HevcAccessUnit =
         { Pts: int64
           Dts: int64
           QpcTime: int64
           Data: byte array
           IsKeyFrame: bool }
     ```
   - Define one decoder-config contract:
     ```fsharp
     type HevcDecoderConfig =
         { HvcC: byte array option
           VpsSpsPpsAnnexB: byte array option }
     ```

3. [BLOCKER] Owner: Capture / `WgcCapture.fs`
   - Reconcile WGC API with recorder needs.
   - Either expose:
     ```fsharp
     create : CaptureOptions -> IWgcFrameSource
     ```
     or update `Recording.fs` to use `startForDuration`.
   - Must expose:
     - `D3D11Device`,
     - width/height,
     - frame sequence,
     - QPC timestamp,
     - GPU texture pointer,
     - optional CPU BGRA readback for x265,
     - deterministic release contract.

4. [BLOCKER] Owner: NVENC / `NvencHevc.fs`, `Encoder.fs`
   - Reconcile `NvencHevc.create`.
   - Current actual shape:
     ```fsharp
     NvencHevc.create d3dDevice width height fps
     ```
   - Encoder adapter expected shape:
     ```fsharp
     NvencHevc.create options
     ```
   - The canonical path should pass `WgcSession.D3D11Device` into NVENC and encode `CapturedFrame.Texture`.

5. [BLOCKER] Owner: Mux / `HevcMux.fs`
   - Reconcile mux API.
   - Either add:
     ```fsharp
     type MuxOptions = ...
     val create : MuxOptions -> IHevcMuxer
     ```
     or rewrite `Recording.fs` to call:
     ```fsharp
     HevcMux.``open`` outFile width height fps hvcCOrFirstNals
     ```
   - Add `WriteAccessUnit` convenience wrapper.

6. [BLOCKER] Owner: Metadata / `Mdkg.fs`, `HevcMux.fs`, `Recording.fs`
   - Implement live metadata producer.
   - For every captured frame, write:
     - timing layer,
     - OCR layer,
     - UI affordance layer,
     - optional semantic graph layer,
     - optional input-event layer.
   - MP4 metadata samples should use `Mdkg.Carrier.encode`.
   - Sidecar `.mdkg` should be written when `TimedMetadataOut` is set or by default next to the MP4.

7. [HIGH] Owner: OCR / Navigation integration
   - Decide how live OCR is obtained during record.
   - Current `NativeOcr.recognize` works on image paths, not GPU frames.
   - Need either:
     - per-frame image extraction for OCR, expensive,
     - a WIC/SoftwareBitmap conversion path,
     - OCR on sampled frames only with timing gaps,
     - or an explicit "metadata empty unless follow/analyze sidecar is supplied" product limitation.
   - Without this, the synchronized metadata track is structurally present at best but semantically empty.

8. [HIGH] Owner: Encoder / Mux
   - Implement reliable `hvcC`/VPS/SPS/PPS plumbing.
   - NVENC must expose first IDR headers or actual HEVC decoder configuration.
   - x265 must expose headers via `x265_encoder_headers` or first access unit before mux construction.
   - Do not rely on a partial SPS parser for production without tests.

9. [HIGH] Owner: x265 / Packaging
   - Ship `x265.dll` for win-x64.
   - Add content/publish/package copy rules.
   - Validate the deployed DLL supports i444 and lossless.
   - Decide claim language: YUV444-domain lossless vs desktop-BGRA byte-exact lossless.

10. [HIGH] Owner: CLI / `Cli.fs`, `Config.fs`, `Program.fs`
    - Update CLI usage:
      ```text
      Yumlog.Native record [--config path] [--out-file path] [--fps n] [--duration n]
        [--backend auto|nvenc|x265]
        [--container mp4]
        [--x265 path]
        [--lossless true|false]
        [--monitor-index n]
        [--timed-metadata-out path]
      ```
    - Ensure `mergeRecord` consumes every option.
    - Ensure `Program.fs` routes to the native implementation.

11. [HIGH] Owner: Tests
    - Turn `test-strategy.raw.md` into actual Pester tests.
    - Add missing product hooks:
      - deterministic synthetic source,
      - known text overlay,
      - backend override,
      - metadata extraction.
    - Tests should fail against current FFmpeg/yuv420 implementation.

12. [MEDIUM] Owner: Container / Product
    - Remove or defer `mkv` from config.
    - The delivered muxer is MP4/ISO BMFF only.
    - Advertising `mkv` before implementing it creates a false CLI contract.

13. [MEDIUM] Owner: Capture / Test
    - Add real WGC smoke tests:
      - frame count,
      - monotonic QPC,
      - texture release,
      - multi-monitor selection,
      - capture border behavior.

14. [MEDIUM] Owner: NVENC / Test
    - Add real hardware tests:
      - NVENC present,
      - NVENC absent fallback,
      - same-device texture registration,
      - ARGB vs AYUV/YUV444 format validation.

---

## Control-flow assessment

### Required goal flow

```text
record
  -> WGC capture loop
  -> for each frame:
       encode frame to HEVC access unit
       mux.writeVideoSample(accessUnit, qpc)
       produce Mdkg timing/OCR/UI/navigation metadata
       mux.writeMetadataSample(mdkgCarrier, qpc)
       mdkg.writeLayerSamples(...)
  -> encoder.flush
  -> mux.close
  -> mdkg.close
```

### Current actual flow

```text
record
  -> spawn ffmpeg
  -> gdigrab desktop
  -> yuv420p
  -> write ordinary video file
```

### Proposed x265-agent flow

```text
record
  -> WGC source
  -> encoder
  -> mux.WriteAccessUnit
  -> mux.Finish
```

That proposed flow still omits synchronized metadata and does not match actual WGC/NVENC/mux APIs.

---

## Native dependency assessment

1. `nvEncodeAPI64.dll`
   - Driver-provided.
   - Acceptable.
   - No project packaging needed, but runtime capability checks are required.

2. `x265.dll`
   - Must ship with the app or be configured by `--x265` / `YUMLOG_X265_PATH`.
   - Current project has no content/publish/package rule for it.
   - This is a release blocker for fallback.

3. Windows.Graphics.Capture
   - OS-provided WinRT API.
   - Raw WinRT activation avoids adding managed projections.
   - No new `PackageReference` is required if the raw COM/vtable approach stays.
   - Requires supported Windows build and likely user capture permissions/OS policy.

4. Windows App SDK / OCR
   - Existing project already references `Microsoft.WindowsAppSDK.AI`.
   - This is not sufficient for WGC recording.
   - It may remain for OCR, but OCR is not integrated into recording metadata.

---

## Test coverage assessment

The test plan is strong as a target specification, but not runnable against the current code.

### It covers conceptually

- Lossless round trip.
- Pixel-format gate against `yuv420p`, `nv12`, and 4:2:0.
- Frame count and timestamp accuracy.
- Metadata sample count and timestamp sync.
- Backend fallback.
- Performance smoke.

### It assumes missing implementation

- Test-only synthetic source mode.
- Native recorder backend selection.
- Native HEVC mux output.
- Timed metadata stream.
- Metadata payload parser.
- Known text overlay fixture.
- Backend capability reporting.
- x265 packaging.
- NVENC hardware path.

### Untested completeness-critical paths

- WGC texture lifetime and `releaseFrameTexture`.
- Same-device D3D11 handoff to NVENC.
- `hvcC` correctness from real NVENC/x265 output.
- Actual MP4 playback/parse compatibility of `hvc1` plus `mett`.
- `.mdkg` sidecar and MP4 carrier equivalence.
- Live OCR/navigation metadata production during recording.
- Failure behavior when WGC permissions or OS APIs are unavailable.

---

## Explicit yes/no answers

1. If I extract all modules as-is and add them to the fsproj, will it compile?

No.

The as-is deliverables have incompatible F# contracts between WGC, NVENC, x265, encoder abstraction, muxer, recording, and domain config. The fsproj order can be fixed, but compile will still fail until the module APIs are reconciled.

2. Does a recording currently produce synchronized metadata, or is that track empty?

No, it does not currently produce synchronized metadata.

The current recorder uses FFmpeg and produces no metadata track. The proposed rewritten native recorder also does not call `WriteMetadataSample` or write `.mdkg` layer samples. At best, metadata is an unwired design artifact; in the current runnable product it is absent.

---

## Recommendations

1. Owner: Integration
   - Stop calling the swarms "done" until a real build uses the new modules in `Yumlog.Native.fsproj` and `dotnet build` passes.

2. Owner: Contracts
   - Create one canonical contract layer before implementing more native code:
     - capture frame,
     - encoder access unit,
     - decoder config,
     - mux options,
     - record result,
     - metadata sample.

3. Owner: Recording
   - Implement the native record loop only after contracts compile in isolation.

4. Owner: Metadata
   - Treat metadata as a first-class required path, not optional post-processing.
   - The mux write of a video sample and metadata sample must happen in the same frame loop with the same QPC-derived PTS.

5. Owner: Test
   - Convert the test strategy into runnable Pester tests only after the CLI exposes the native pipeline.
   - First acceptance gate should be:
     ```powershell
     dotnet build E:\Yumlog\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release
     Yumlog.Native record --backend x265 --duration 1 --fps 5 --out-file smoke.mp4
     ```
     followed by metadata and pixel-format verification.

---

## Code Snippets

### Minimal canonical recording loop shape

```fsharp
for frame in source.Frames() do
    let accessUnits =
        encoder.EncodeFrame(frame)

    for au in accessUnits do
        mux.WriteVideoSample(au.Data, frame.QpcTime)

    let ptsTicks =
        Mdkg.qpcToPtsTicks qpcFrequency firstQpc frame.QpcTime

    let timing =
        { Mdkg.TimingLayerSample.Qpc = frame.QpcTime
          FrameIndex = frame.Index
          Fps = config.Fps
          PtsTicks = ptsTicks
          DurationTicks = frameDurationTicks
          GapFromPreviousTicks = 0L
          IsDropped = false
          DropCount = 0
          Notes = "" }

    mdkg.WriteTiming(timing)

    let carrier =
        Mdkg.Carrier.encode
            { LayerId = Mdkg.LayerId.Timing
              PtsTicks = ptsTicks
              DurationTicks = frameDurationTicks
              Flags = uint32 Mdkg.MdkgSampleFlags.KeyFrame
              Codec = Mdkg.Registry.Timing.Codec
              Payload = Mdkg.serializeJson timing }

    mux.WriteMetadataSample(carrier, frame.QpcTime)
```

### Required adapter decision point

```fsharp
type NativeCaptureFrame =
    { Index: int
      Width: int
      Height: int
      QpcTime: int64
      TimestampUtc: DateTimeOffset
      Texture: nativeint
      CpuBgra: byte array option
      CpuStride: int }

type NativeEncodedFrame =
    { Pts: int64
      Dts: int64
      QpcTime: int64
      Data: byte array
      IsKeyFrame: bool }
```

---

VERDICT: INCOMPLETE.