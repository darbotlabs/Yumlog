Agent is idle (waiting for messages). agent_id: ch-x265abs, agent_type: dayour-swe, status: idle, description: Challenge x265 + abstraction contracts, elapsed: 253s, total_turns: 1

[Turn 0]
**Summary**: The proposed x265 fallback and encoder abstraction are not contract-complete. The biggest failures are at the integration seams: `Encoder.fs` assumes NVENC, WGC, and HevcMux APIs that the other agents did not actually produce, and the x265 path is not truly lossless for RGB desktop pixels because the BGRA-to-I444 conversion is not reversible.

## Findings

1. `[BLOCKER] Encoder.fs does not match the actual NvencHevc.fs contract`

   **Encoder.fs expected contract, from `x265-fallback.raw.md`:**

   ```fsharp
   // x265-fallback.raw.md lines 646-659
   type private NvencAdapter(options: HevcEncoderOptions) =
       // Expected NvencHevc.fs contract from the NVENC owner:
       //
       // module NvencHevc =
       //   val isNvencAvailable: unit -> bool
       //   val create: HevcEncoderOptions -> <encoder object>
       //
       // Returned encoder object:
       //   member EncodeTexture: nativeint * int * int * int64 -> HevcAccessUnit array
       //   member Flush: unit -> HevcAccessUnit array
       //   interface IDisposable
       let inner = NvencHevc.create options
   ```

   ```fsharp
   // x265-fallback.raw.md lines 668-678
   member _.EncodeFrame(frame: HevcFrame) =
       match frame with
       | GpuTexture gpu ->
           inner.EncodeTexture(gpu.TextureHandle, gpu.Width, gpu.Height, gpu.Pts)

   member _.Finish() =
       inner.Flush()
   ```

   **Actual NvencHevc.fs contract, from `nvenc-hevc.raw.md`:**

   ```fsharp
   // nvenc-hevc.raw.md lines 1179-1198
   let isNvencAvailable () =
       ...

   let create (d3dDevice: nativeint) (width: int) (height: int) (fps: int) =
       NvencEncoder.Create(d3dDevice, width, height, fps)

   let encodeFrame (encoder: NvencEncoder) (texture: nativeint) (qpcTime: int64) =
       encoder.EncodeFrame(texture, qpcTime)

   let finish (encoder: NvencEncoder) =
       encoder.Finish()

   let dispose (encoder: NvencEncoder) =
       encoder.Dispose()
   ```

   ```fsharp
   // nvenc-hevc.raw.md lines 942-947
   member this.EncodeFrame(texture: nativeint, qpcTime: int64) =
       if disposed then
           invalidOp "NvencEncoder is disposed."
       if texture = nativeint 0 then
           invalidArg "texture" "Texture pointer must not be null."
   ```

   ```fsharp
   // nvenc-hevc.raw.md lines 1042-1047
   let packet = { QpcTime = qpcTime; Data = data }
   frameEncoded.Trigger(packet)

   data
   ```

   ```fsharp
   // nvenc-hevc.raw.md lines 1054-1070
   member _.Finish() =
       if not disposed then
           ...
           let status = api.EncodePicture.Invoke(encoder, &pic)
           ...
   ```

   **Mismatches:**

   - `Encoder.fs` calls `NvencHevc.create options`; actual signature is curried: `create d3dDevice width height fps`.
   - `HevcEncoderOptions` does not carry `D3D11Device`, but actual NVENC cannot be created without a D3D11 device.
   - `Encoder.fs` expects `inner.EncodeTexture(...)`; actual member is `EncodeFrame(texture, qpcTime)`.
   - `Encoder.fs` expects `inner.Flush() : HevcAccessUnit array`; actual `Finish()` returns `unit`.
   - Actual `EncodeFrame` returns `byte array`, not `HevcAccessUnit array`.
   - Actual NVENC emits `NvencPacket = { QpcTime; Data }`, not `Pts/Dts/IsKeyFrame`.
   - There is no adapter converting NVENC byte arrays into `HevcAccessUnit` with correct QPC timestamps and keyframe/config semantics.

   **Concrete fix:**

   Make the abstraction match the actual NVENC surface, or change NVENC to implement the abstraction directly. Minimal adapter-side fix:

   ```fsharp
   type HevcEncoderOptions =
       { Backend: HevcBackend
         Width: int
         Height: int
         Fps: int
         Preset: string
         Lossless: bool
         X265Path: string
         D3D11Device: nativeint option }

   type private NvencAdapter(options: HevcEncoderOptions) =
       let d3d =
           options.D3D11Device
           |> Option.defaultWith (fun () -> invalidArg "options" "NVENC requires WgcSession.D3D11Device.")

       let inner = NvencHevc.create d3d options.Width options.Height options.Fps

       interface IHevcEncoder with
           member _.Backend = Nvenc
           member _.InputMode = GpuTexture
           member _.Availability = nvencAvailability()

           member _.EncodeFrame(frame) =
               match frame with
               | GpuTexture gpu ->
                   let data = NvencHevc.encodeFrame inner gpu.TextureHandle gpu.Pts
                   if data.Length = 0 then
                       Array.empty
                   else
                       [| { Pts = gpu.Pts
                            Dts = gpu.Pts
                            Data = data
                            IsKeyFrame = false } |]
               | CpuBgra _ ->
                   invalidArg "frame" "NVENC requires GPU texture input."

           member _.Finish() =
               NvencHevc.finish inner
               Array.empty

           member _.Dispose() =
               NvencHevc.dispose inner
   ```

   This still does not solve keyframe detection or hvcC extraction; those need explicit NVENC support.

2. `[BLOCKER] Encoder.InputMode is not honored by the actual WgcCapture contract; x265 backend is dead`

   **Encoder abstraction defines two modes:**

   ```fsharp
   // x265-fallback.raw.md lines 466-469
   type HevcInputMode =
       | GpuTexture
       | CpuBgra
   ```

   ```fsharp
   // x265-fallback.raw.md lines 611-633
   member _.InputMode = CpuBgra
   ...
   | GpuTexture _ ->
       invalidArg "frame" "x265 backend requires CPU BGRA frames. Configure WgcCapture for CPU readback when Encoder.InputMode = CpuBgra."
   ```

   ```fsharp
   // x265-fallback.raw.md lines 664-674
   member _.InputMode = GpuTexture
   ...
   | CpuBgra _ ->
       invalidArg "frame" "NVENC backend requires a GPU texture frame. Configure WgcCapture for GPU texture delivery when Encoder.InputMode = GpuTexture."
   ```

   **Recording.fs expects WGC to provide selectable CPU/GPU output:**

   ```fsharp
   // x265-fallback.raw.md lines 889-914
   // module WgcCapture =
   //   type CaptureOptions =
   //     { Fps: int
   //       DurationSec: int
   //       PreferGpuTexture: bool
   //       RequireCpuBgra: bool }
   //
   //   type CaptureFrame =
   //     { Width: int
   //       Height: int
   //       Pts: int64
   //       TimestampUtc: DateTimeOffset
   //       CpuBgra: byte array option
   //       CpuStride: int
   //       TextureHandle: nativeint }
   //
   //   type IWgcFrameSource =
   //     inherit IDisposable
   //     abstract Width: int
   //     abstract Height: int
   //     abstract Frames: unit -> seq<CaptureFrame>
   //
   //   val create: CaptureOptions -> IWgcFrameSource
   ```

   **Actual WgcCapture only emits GPU texture frames:**

   ```fsharp
   // wgc-capture.raw.md lines 92-99
   [<CLIMutable>]
   type CapturedFrame =
       { Texture: nativeint
         SharedHandle: nativeint
         Width: int
         Height: int
         QpcTime: int64 }
   ```

   ```fsharp
   // wgc-capture.raw.md lines 588-602
   member _.Frames = frames
   member _.D3D11Device = d3d11Device.Handle
   member _.D3D11ImmediateContext = d3d11ImmediateContext.Handle
   member _.WinRtDirect3DDevice = winRtDevice.Handle
   member _.Width = width
   member _.Height = height
   member _.Fps = fps
   member _.DurationSec = durationSec
   ```

   ```fsharp
   // wgc-capture.raw.md lines 680-724
   let startForDuration monitorIndex fps durationSec =
       ...
   let start monitorIndex fps =
       startForDuration monitorIndex fps 0
   ```

   **Mismatches:**

   - No `WgcCapture.CaptureOptions`.
   - No `WgcCapture.create`.
   - No `IWgcFrameSource`.
   - No `Frames() : seq<CaptureFrame>`.
   - No `CpuBgra`.
   - No `CpuStride`.
   - No `TextureHandle`; actual field is `Texture`.
   - No `Pts`; actual field is `QpcTime`.
   - No `TimestampUtc`.
   - No CPU readback path at all.
   - `Encoder.InputMode = CpuBgra` is therefore impossible to satisfy.
   - x265 fallback cannot receive frames from WGC.

   **Concrete fix:**

   Either add a CPU readback/staging path to WGC, or make x265 explicitly unsupported until that exists. Required WGC API:

   ```fsharp
   type CaptureOutputMode =
       | GpuTexture
       | CpuBgra

   type CaptureOptions =
       { MonitorIndex: int
         Fps: int
         DurationSec: int
         OutputMode: CaptureOutputMode }

   type CaptureFrame =
       { Width: int
         Height: int
         QpcTime: int64
         TimestampUtc: DateTimeOffset
         Texture: nativeint option
         CpuBgra: byte array option
         CpuStride: int }

   type IWgcFrameSource =
       inherit IDisposable
       abstract D3D11Device: nativeint
       abstract Width: int
       abstract Height: int
       abstract Frames: BlockingCollection<CaptureFrame>

   val create: CaptureOptions -> IWgcFrameSource
   val releaseFrameTexture: nativeint -> unit
   ```

   For `CpuBgra`, WGC must copy the `ID3D11Texture2D` into a staging texture with `D3D11_USAGE_STAGING`, map it, preserve stride, and copy to managed bytes.

3. `[BLOCKER] Rewritten Recording.fs call-sites do not resolve against actual WGC, Encoder, or HevcMux modules`

   The proposed `Recording.fs` contains multiple non-resolving calls.

   **Broken WGC calls:**

   ```fsharp
   // x265-fallback.raw.md lines 945-951
   let preliminaryCaptureOptions =
       { WgcCapture.CaptureOptions.Fps = config.Fps
         DurationSec = config.DurationSec
         PreferGpuTexture = true
         RequireCpuBgra = false }

   use preliminarySource = WgcCapture.create preliminaryCaptureOptions
   ```

   Actual WGC provides:

   ```fsharp
   // wgc-capture.raw.md lines 680-724
   let startForDuration monitorIndex fps durationSec = ...
   let start monitorIndex fps = ...
   ```

   **Broken frame fields:**

   ```fsharp
   // x265-fallback.raw.md lines 993-1019
   for frame in source.Frames() do
       ...
       if frame.TextureHandle = nativeint 0 then ...
       ...
       Pts = frame.Pts
       TimestampUtc = frame.TimestampUtc
       ...
       match frame.CpuBgra with
       | Some data ->
           Stride = frame.CpuStride
   ```

   Actual WGC frame:

   ```fsharp
   // wgc-capture.raw.md lines 93-99
   type CapturedFrame =
       { Texture: nativeint
         SharedHandle: nativeint
         Width: int
         Height: int
         QpcTime: int64 }
   ```

   **Broken mux calls:**

   ```fsharp
   // x265-fallback.raw.md lines 982-990
   let muxOptions =
       { HevcMux.MuxOptions.Path = outFile
         Container = container
         Codec = "hevc"
         Fps = config.Fps
         Width = source.Width
         Height = source.Height }

   use mux = HevcMux.create muxOptions
   ```

   Actual mux provides:

   ```fsharp
   // mux-metadata-mdkg.raw.md lines 817-833
   type Muxer(path: string, width: int, height: int, fps: int, hvcCOrFirstNals: byte array) =
       ...
       let hvcc =
           if looksLikeHvcc hvcCOrFirstNals then
               hvcCOrFirstNals
           else
               buildHvccFromVpsSpsPps hvcCOrFirstNals
   ```

   ```fsharp
   // mux-metadata-mdkg.raw.md lines 933-943
   let ``open`` (path: string) (width: int) (height: int) (fps: int) (hvcCOrFirstNals: byte array) =
       new Muxer(path, width, height, fps, hvcCOrFirstNals)

   let writeVideoSample (muxer: Muxer) (nal: byte array) (qpc: int64) =
       muxer.WriteVideoSample(nal, qpc)

   let writeMetadataSample (muxer: Muxer) (payload: byte array) (qpc: int64) =
       muxer.WriteMetadataSample(payload, qpc)

   let close (muxer: Muxer) =
       muxer.Close()
   ```

   **Broken access-unit write:**

   ```fsharp
   // x265-fallback.raw.md lines 1021-1031
   let accessUnits = encoder.EncodeFrame hevcFrame

   for au in accessUnits do
       mux.WriteAccessUnit au

   for au in encoder.Finish() do
       mux.WriteAccessUnit au

   mux.Finish()
   ```

   Actual mux has no `WriteAccessUnit` and no `Finish`; it has `WriteVideoSample`, `WriteMetadataSample`, and `Close`.

   **Concrete fix:**

   The pipeline must be rewritten around the actual APIs:

   ```fsharp
   use session = WgcCapture.startForDuration 0 config.Fps config.DurationSec

   let encoderOptions =
       { Backend = requestedBackend
         Width = session.Width
         Height = session.Height
         Fps = config.Fps
         Preset = config.Preset
         Lossless = config.Lossless
         X265Path = config.X265Path
         D3D11Device = Some session.D3D11Device }

   use encoder = Encoder.create encoderOptions

   // Encode first frame before opening mux, because HevcMux requires hvcC or first VPS/SPS/PPS.
   let firstAu = encodeFirstFrame session encoder
   use mux = HevcMux.``open`` outFile session.Width session.Height config.Fps firstAu.Data

   HevcMux.writeVideoSample mux firstAu.Data firstAu.Pts

   for au in remainingAccessUnits do
       HevcMux.writeVideoSample mux au.Data au.Pts

   HevcMux.close mux
   ```

   This still requires a clear rule: `Pts` must be QPC ticks if passed to `HevcMux`.

4. `[BLOCKER] HevcMux requires hvcC or first VPS/SPS/PPS before creation; Recording.fs creates the mux too early`

   Actual mux constructor:

   ```fsharp
   // mux-metadata-mdkg.raw.md lines 817-833
   type Muxer(path: string, width: int, height: int, fps: int, hvcCOrFirstNals: byte array) =
       ...
       let hvcc =
           if looksLikeHvcc hvcCOrFirstNals then
               hvcCOrFirstNals
           else
               buildHvccFromVpsSpsPps hvcCOrFirstNals
   ```

   Proposed Recording creates mux before encoding any frame:

   ```fsharp
   // x265-fallback.raw.md lines 982-990
   let muxOptions =
       { HevcMux.MuxOptions.Path = outFile
         Container = container
         Codec = "hevc"
         Fps = config.Fps
         Width = source.Width
         Height = source.Height }

   use mux = HevcMux.create muxOptions
   ```

   This cannot work because the muxer needs decoder configuration bytes up front. The encoder abstraction does not expose hvcC, VPS/SPS/PPS, or “first access unit contains headers” as a formal contract.

   **Concrete fix:**

   Add one of these to `IHevcEncoder`:

   ```fsharp
   type IHevcEncoder =
       inherit IDisposable
       abstract Backend: HevcBackend
       abstract InputMode: HevcInputMode
       abstract EncodeFrame: HevcFrame -> HevcAccessUnit array
       abstract Finish: unit -> HevcAccessUnit array
       abstract DecoderConfig: byte array option
   ```

   Or require the recorder to encode the first frame, extract VPS/SPS/PPS from the first access unit, then open the mux:

   ```fsharp
   let firstAccessUnits = encoder.EncodeFrame firstFrame
   let firstConfigSource =
       firstAccessUnits
       |> Array.tryFind (fun au -> containsVpsSpsPps au.Data)
       |> Option.defaultWith (fun () -> failwith "First HEVC access unit did not contain VPS/SPS/PPS.")

   use mux = HevcMux.``open`` outFile width height fps firstConfigSource.Data
   ```

5. `[HIGH] HevcMux metadata contract is not consumed; the pipeline writes video only`

   HevcMux provides explicit metadata support:

   ```fsharp
   // mux-metadata-mdkg.raw.md lines 172-177
   let metadataPayloadJson
       (frameIndex: int)
       (qpc: int64)
       (ocr: OcrResult option)
       (hints: UiNavigationHint array option)
       : byte array =
   ```

   ```fsharp
   // mux-metadata-mdkg.raw.md lines 936-940
   let writeVideoSample (muxer: Muxer) (nal: byte array) (qpc: int64) =
       muxer.WriteVideoSample(nal, qpc)

   let writeMetadataSample (muxer: Muxer) (payload: byte array) (qpc: int64) =
       muxer.WriteMetadataSample(payload, qpc)
   ```

   Proposed Recording only writes access units:

   ```fsharp
   // x265-fallback.raw.md lines 1021-1031
   for au in accessUnits do
       mux.WriteAccessUnit au
   ...
   mux.Finish()
   ```

   There is no call to `NativeOcr.recognize`, no `UiNavigationHint` generation, no `HevcMux.metadataPayloadJson`, and no `writeMetadataSample`.

   **Concrete fix:**

   Either scope this recorder explicitly as “video-only for now” and remove metadata claims, or wire per-frame metadata:

   ```fsharp
   let metadata =
       HevcMux.metadataPayloadJson frameIndex qpc None None

   HevcMux.writeVideoSample mux au.Data qpc
   HevcMux.writeMetadataSample mux metadata qpc
   ```

   If OCR is required, the WGC pipeline also needs a frame-to-image or SoftwareBitmap path; `Ocr.fs` currently recognizes image paths, not in-memory WGC frames.

6. `[BLOCKER] x265 P/Invoke mostly avoids x265_param corruption, but x265_picture marshalling is still dangerous and likely version-sensitive`

   The good part: the implementation does not hand-declare `x265_param`. It uses lib allocation:

   ```fsharp
   // x265-fallback.raw.md lines 85-99
   extern nativeint x265_param_alloc()
   extern void x265_param_free(nativeint param)
   extern int x265_param_default_preset(nativeint param, string preset, string tune)
   extern int x265_param_parse(nativeint param, string name, string value)
   extern nativeint x265_encoder_open(nativeint param)
   ```

   That avoids the most catastrophic `x265_param` ABI problem.

   The risky part is `x265_picture`:

   ```fsharp
   // x265-fallback.raw.md lines 121-139
   /// Prefix of x265_picture sufficient to supply planes/stride/pts.
   /// x265_picture_alloc allocates the full native structure; this managed structure writes only the stable prefix.
   [<StructLayout(LayoutKind.Sequential)>]
   type private X265PicturePrefix =
       [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)>]
       val mutable planes: nativeint array

       [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)>]
       val mutable stride: int array

       val mutable pts: int64
       val mutable dts: int64
       val mutable userData: nativeint
       val mutable colorSpace: int
       val mutable sliceType: int
       val mutable forceQp: int
       val mutable picStruct: int
       val mutable bitDepth: int
   ```

   ```fsharp
   // x265-fallback.raw.md lines 354-375
   let mutable picture = Marshal.PtrToStructure<X265PicturePrefix>(picIn)
   ...
   picture.colorSpace <- 3 // X265_CSP_I444 in common x265 builds.
   picture.bitDepth <- 8

   Marshal.StructureToPtr(picture, picIn, false)
   ```

   Problems:

   - `x265_picture` is also ABI/version-sensitive.
   - This prefix assumes field order and field presence.
   - The comment says “stable prefix,” but that must be proven against the exact x265 header version being shipped.
   - `bitDepth` is especially suspicious; if that field is not in this position for the loaded x265 build, `StructureToPtr` writes into the wrong native field.
   - `forceQp` and `picStruct` ordering must exactly match the header. If not, picture flags are corrupted.
   - `[<MarshalAs(UnmanagedType.ByValArray)>]` inside a partial struct copied into a native allocation is fragile compared to offset writes.
   - The code never calls a native helper to set planes/strides; it writes assumed offsets.

   **Concrete fix:**

   Avoid managed struct overlay for `x265_picture`. Use one of:

   - A small C shim compiled against the exact shipped x265 headers:

     ```c
     void yumlog_x265_picture_set_i444(
         x265_picture* pic,
         uint8_t* y,
         uint8_t* u,
         uint8_t* v,
         int strideY,
         int strideU,
         int strideV,
         int64_t pts)
     {
         pic->planes[0] = y;
         pic->planes[1] = u;
         pic->planes[2] = v;
         pic->stride[0] = strideY;
         pic->stride[1] = strideU;
         pic->stride[2] = strideV;
         pic->pts = pts;
         pic->colorSpace = X265_CSP_I444;
     }
     ```

   - Or generate exact field offsets from the target x265 header at build time and use `Marshal.WriteIntPtr` / `Marshal.WriteInt32` only at verified offsets.

   Until this is fixed, the x265 path is not production-safe.

7. `[HIGH] x265 “lossless” is not true RGB desktop lossless because BGRA-to-I444 conversion is lossy`

   The code sets x265 lossless mode and I444 input:

   ```fsharp
   // x265-fallback.raw.md lines 309-319
   parseRequired param "input-res" $"{options.Width}x{options.Height}"
   parseRequired param "fps" (string options.Fps)
   parseRequired param "input-csp" "i444"
   parseRequired param "annexb" "1"
   parseRequired param "repeat-headers" "1"
   parseRequired param "bframes" "0"

   if options.Lossless then
       parseRequired param "lossless" "1"
       parseOptional param "cu-lossless" "1"
       parseOptional param "profile" "main444-8"
   ```

   But BGRA is converted with floating-point YCbCr formulas:

   ```fsharp
   // x265-fallback.raw.md lines 234-238
   // BT.709 full-range RGB -> YCbCr approximation.
   // For exact mathematical RGB lossless, prefer x265 RGB/GBR input if available in the deployed build.
   y[dst] <- clampByte (0.2126 * r + 0.7152 * g + 0.0722 * b)
   u[dst] <- clampByte (128.0 - 0.114572 * r - 0.385428 * g + 0.500000 * b)
   v[dst] <- clampByte (128.0 + 0.500000 * r - 0.454153 * g - 0.045847 * b)
   ```

   This is not reversible. x265 lossless will preserve the I444 samples it receives, but the original BGRA desktop pixels have already been irreversibly transformed and rounded.

   Additional problems:

   - No explicit full-range x265 signaling is set, only an informal comment.
   - No matrix/range parameters are guaranteed.
   - No GBR/RGB path is implemented.
   - “Lossless” in `RecordResult.Lossless = true` would be misleading for RGB desktop byte-exactness.

   **Concrete fix:**

   Rename the mode unless a reversible path exists:

   ```fsharp
   Lossless = false
   Encoder = "hevc/x265-i444-lossless-over-yuv"
   ```

   Or implement a validated RGB/GBR path:

   ```fsharp
   parseRequired param "input-csp" "gbr"
   parseRequired param "lossless" "1"
   parseRequired param "range" "full"
   ```

   Only do this if the shipped x265 build supports GBR input through the C API and the mux/decoder path is tested byte-exact.

8. `[HIGH] BGRA-to-I444 conversion has stride validation gaps and 4K performance risks`

   Current validation:

   ```fsharp
   // x265-fallback.raw.md lines 207-216
   let bgraToI444 (frame: BgraFrame) =
       if frame.Width <= 0 || frame.Height <= 0 then
           invalidArg "frame" "Frame dimensions must be positive."

       if isNull frame.Data then
           invalidArg "frame" "Frame data cannot be null."

       if frame.Stride < frame.Width * 4 then
           invalidArg "frame" $"BGRA stride {frame.Stride} is smaller than width * 4."
   ```

   Missing validation:

   ```fsharp
   requiredLength = (height - 1) * stride + width * 4
   frame.Data.Length >= requiredLength
   ```

   At 4K:

   - BGRA input is about 31.6 MiB per frame.
   - I444 output allocates three full planes, about 23.7 MiB per frame.
   - The conversion allocates roughly 55 MiB per frame before encoder work.
   - At 30 fps this causes extreme allocation pressure and GC churn.

   **Concrete fix:**

   Add bounds validation and reuse pinned buffers:

   ```fsharp
   let required =
       checked ((frame.Height - 1) * frame.Stride + frame.Width * 4)

   if frame.Data.Length < required then
       invalidArg "frame" $"BGRA data length {frame.Data.Length} is smaller than required {required}."

   // Allocate y/u/v once per encoder instance and reuse them per frame.
   ```

9. `[HIGH] Container abstraction claims MKV support but HevcMux is MP4-only`

   Config diff accepts MKV:

   ```fsharp
   // x265-fallback.raw.md lines 803-807
   let normalizeContainer value =
       match value.Trim().ToLowerInvariant() with
       | "mp4" -> "mp4"
       | "mkv" | "matroska" -> "mkv"
       | other -> invalidArg "container" $"Unsupported container '{other}'. Use mp4 or mkv."
   ```

   Recording also accepts MKV:

   ```fsharp
   // x265-fallback.raw.md lines 845-849
   let private normalizeContainer value =
       match (if isNull value then "" else value.Trim().ToLowerInvariant()) with
       | "" | "mp4" -> "mp4"
       | "mkv" | "matroska" -> "mkv"
       | other -> invalidArg "Container" $"Unsupported container '{other}'. Use mp4 or mkv."
   ```

   But HevcMux implementation is explicitly ISO BMFF/MP4:

   ```fsharp
   // mux-metadata-mdkg.raw.md lines 69-79
   Implement a native F# ISO BMFF/MP4 writer in `HevcMux.fs`.
   ...
   Video samples are stored as MP4-compatible length-prefixed HEVC NAL units.
   ```

   There is no MKV muxer.

   **Concrete fix:**

   Reject MKV until an MKV muxer exists:

   ```fsharp
   let normalizeContainer value =
       match value.Trim().ToLowerInvariant() with
       | "" | "mp4" -> "mp4"
       | "mkv" | "matroska" ->
           invalidArg "container" "MKV is not implemented. Current native muxer supports mp4 only."
       | other ->
           invalidArg "container" $"Unsupported container '{other}'. Use mp4."
   ```

10. `[MEDIUM] RecordResult is mostly backward-compatible, but the semantic change to Encoder can break consumers`

   Current `RecordResult`:

   ```fsharp
   // Domain.fs lines 88-94
   type RecordResult =
       { Path: string
         Fps: int
         DurationSec: int
         Encoder: string
         ExitCode: int
         StandardError: string }
   ```

   Proposed additive result:

   ```fsharp
   // x265-fallback.raw.md lines 736-748
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
         ExitCode: int
         StandardError: string }
   ```

   `Program.fs` is generic and will serialize the new fields:

   ```fsharp
   // Program.fs lines 7-8
   let private writeJson value =
       printfn "%s" (NativeJson.serialize value)
   ```

   This is source-compatible only if every `RecordResult` construction is updated. The proposed `Recording.fs` does that. Existing JSON consumers that ignore unknown fields should survive. Consumers with strict schemas may fail.

   More importantly, `Encoder` changes meaning:

   ```fsharp
   // Current Recording.fs lines 69-74
   { Path = outFile
     Fps = config.Fps
     DurationSec = config.DurationSec
     Encoder = config.Encoder
     ExitCode = proc.ExitCode
     StandardError = "" }
   ```

   Proposed:

   ```fsharp
   // x265-fallback.raw.md lines 1034-1043
   { Path = outFile
     Fps = config.Fps
     DurationSec = config.DurationSec
     Encoder = encoderName
     Backend = Encoder.backendName actualBackend
     Codec = "hevc"
     Lossless = config.Lossless
     Container = container
     FrameCount = frameCount
     ExitCode = 0
   ```

   **Concrete fix:**

   Keep `Encoder` stable and add explicit new fields:

   ```fsharp
   Encoder = config.Encoder // legacy field, if supplied
   Codec = "hevc"
   Backend = Encoder.backendName actualBackend
   ```

   Or document that `Encoder` now means “effective encoder.”

11. `[HIGH] Config/CLI is incomplete in the actual repo and ambiguous in the proposed diff`

   Current config has no native HEVC fields:

   ```fsharp
   // Domain.fs lines 15-21
   type RecordConfig =
       { OutFile: string
         Fps: int
         DurationSec: int
         FFmpegPath: string
         Encoder: string
         Preset: string }
   ```

   ```fsharp
   // Config.fs lines 18-24
   Record =
       { OutFile = "./yumlogs/yumlog.mp4"
         Fps = 30
         DurationSec = 10
         FFmpegPath = ""
         Encoder = "libx264"
         Preset = "ultrafast" }
   ```

   Proposed diff adds fields, but it does not update current source yet. Also:

   - `Cli.fs` parser accepts arbitrary `--key value`, so `--backend nvenc` works.
   - Boolean flags do not work as bare flags. `--lossless` without a value will be parsed as an unexpected argument or will consume the next token as its value.
   - Usage text omits `--lossless`, `--codec`, and `--preset`.
   - `--codec` is accepted but not validated; non-HEVC values can reach a HEVC-only pipeline.
   - `--ffmpeg` remains in the config but should be explicitly marked ignored for native recording.

   Current usage still says FFmpeg:

   ```fsharp
   // Cli.fs line 12
   Yumlog.Native record [--config path] [--out-file path] [--fps n] [--duration n] [--ffmpeg path]
   ```

   **Concrete fix:**

   ```fsharp
   | option :: rest when option = "--lossless" ->
       loop rest configPath (Map.add "lossless" "true" options)
   | option :: value :: rest when option.StartsWith("--") ->
       loop rest configPath (Map.add (option.Substring(2)) value options)
   ```

   And reject unsupported codecs:

   ```fsharp
   let normalizeCodec value =
       match value.Trim().ToLowerInvariant() with
       | "" | "hevc" | "h265" | "h.265" -> "hevc"
       | other -> invalidArg "codec" $"Unsupported codec '{other}'. Native recorder currently supports hevc only."
   ```

12. `[MEDIUM] x265.dll resolution is implemented in the proposal but not fully safe or documented in live docs`

   Proposed x265 resolver:

   ```fsharp
   // x265-fallback.raw.md lines 46-51
   let private candidateDllPaths explicitPath =
       [ explicitPath
         Environment.GetEnvironmentVariable("YUMLOG_X265_PATH") |> Option.ofObj
         Some(Path.Combine(Environment.CurrentDirectory, ".tools", "x265", DllName))
         Some(Path.Combine(AppContext.BaseDirectory, ".tools", "x265", DllName))
         Some(Path.Combine(AppContext.BaseDirectory, DllName)) ]
   ```

   Operational note:

   ```fsharp
   // x265-fallback.raw.md lines 1135-1136
   // Some x265 builds export versioned API symbols rather than undecorated names.
   // If your chosen DLL does that, switch `X265Encoder.fs` to the `x265_api_get` function-table pattern.
   ```

   Current README still says recording uses FFmpeg:

   ```markdown
   // README.md line 50
   Recording uses FFmpeg as a native child process.
   ```

   **Concrete fix:**

   - Update README and CLI help.
   - Add startup diagnostic showing the resolved x265 path.
   - Prefer `x265_api_get` over undecorated direct imports if the shipped DLL uses versioned symbols.

13. `[HIGH] Removing the FFmpeg gdigrab path breaks docs, tests, and launchers unless those are updated or intentionally left legacy`

   Current native recorder uses FFmpeg:

   ```fsharp
   // Recording.fs lines 29-40
   let ffmpeg = resolveFfmpeg config
   let args =
       [| "-y"
          "-f"; "gdigrab"
          "-framerate"; string config.Fps
          "-t"; string config.DurationSec
          "-i"; "desktop"
          "-vf"; "pad=ceil(iw/2)*2:ceil(ih/2)*2"
          "-c:v"; config.Encoder
          "-preset"; config.Preset
          "-pix_fmt"; "yuv420p"
          outFile |]
   ```

   README documents FFmpeg:

   ```markdown
   // README.md lines 33-34
   # Record desktop video through FFmpeg without PowerShell entry points
   dotnet run --project .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release -- record --out-file .\yumlogs\native.mp4 --duration 10 --fps 30
   ```

   PowerShell launcher still uses `Record-Screen`:

   ```powershell
   // launchers/yumlog.ps1 lines 23-26
   'start' {
       if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
       Record-Screen -Fps $Fps -DurationSec $DurationSec -OutFile $OutFile
   }
   ```

   `Record-Screen.ps1` directly uses FFmpeg gdigrab:

   ```powershell
   // Skills/Record-Screen.ps1 lines 13-14
   $args = @("-y", "-f", "gdigrab", "-framerate", $Fps, "-t", $DurationSec, "-i", "desktop", "-c:v", "libx264", "-preset", "ultrafast", $OutFile)
   Run-FFmpeg -Arguments $args
   ```

   Existing tests even assert FFmpeg availability:

   ```powershell
   // Skills/Test-ScreenSkills.Tests.ps1 lines 45-49
   Describe "yumlog FFmpeg availability" {
       It "FFmpeg is available on PATH or via install.ps1" {
           $true | Should Be $true
       }
   }
   ```

   **Concrete fix:**

   Decide scope:

   - If native CLI only changes, leave PowerShell launcher as legacy FFmpeg and document it.
   - If the product removes FFmpeg recording, update `launchers/yumlog.ps1`, `Record-Screen.ps1`, README, and Pester tests.
   - Add native recording tests from `test-strategy.raw.md`.

14. `[HIGH] F# compile order is not updated in the actual project`

   Current fsproj:

   ```xml
   // Yumlog.Native.fsproj lines 29-45
   <Compile Include="Domain.fs" />
   <Compile Include="Json.fs" />
   <Compile Include="Config.fs" />
   <Compile Include="Capture.fs" />
   <Compile Include="Recording.fs" />
   <Compile Include="RuntimeIdentity.fs" />
   <Compile Include="WinAppRuntime.fs" />
   <Compile Include="RawWinRt.fs" />
   <Compile Include="WindowsAiOcr.fs" />
   <Compile Include="Ocr.fs" />
   ...
   ```

   Proposed new files are not present in the project:

   - `WgcCapture.fs`
   - `X265Encoder.fs`
   - `NvencHevc.fs`
   - `Encoder.fs`
   - `HevcMux.fs`

   Also `Recording.fs` currently compiles before `RawWinRt.fs`, which would be wrong if rewritten to depend on WGC/native modules.

   **Concrete fix:**

   Use this order:

   ```xml
   <Compile Include="Domain.fs" />
   <Compile Include="Json.fs" />
   <Compile Include="Config.fs" />
   <Compile Include="RawWinRt.fs" />
   <Compile Include="WgcCapture.fs" />
   <Compile Include="X265Encoder.fs" />
   <Compile Include="NvencHevc.fs" />
   <Compile Include="Encoder.fs" />
   <Compile Include="HevcMux.fs" />
   <Compile Include="Capture.fs" />
   <Compile Include="Recording.fs" />
   ```

15. `[MEDIUM] NVENC source itself contains a known compile-time landmine`

   The NVENC deliverable admits this:

   ```fsharp
   // nvenc-hevc.raw.md lines 1201-1208
   NOTE: There is one intentionally ugly-looking private member in the class body that should be removed before paste if your compiler flags it:

   member private _.MapTexture(texture: nativeint) =
       let registered = (box null :?> NvencEncoder).RegisterTexture texture

   That member is unused and only exists because I initially factored `RegisterTexture` separately, then inlined the resource-register logic inside `EncodeFrame` to avoid private-member capture quirks. Delete that `MapTexture` member.
   ```

   This is not acceptable in a “complete” integration package. If pasted as-is, it may compile but contains a guaranteed runtime null cast if ever called and should not be present.

   **Concrete fix:**

   Delete `MapTexture` and have `EncodeFrame` call `this.RegisterTexture(texture)`.

## Recommendations

1. Owner: Encoder abstraction. Redesign `IHevcEncoder` around the actual needs:
   - NVENC requires `D3D11Device`.
   - x265 requires CPU BGRA.
   - mux requires hvcC or VPS/SPS/PPS before close or at construction.
   - timestamps must be QPC-compatible.

2. Owner: WGC capture. Add a real `CpuBgra` readback path or remove x265 fallback from the WGC pipeline until CPU frames exist.

3. Owner: NVENC. Expose a first-class abstraction-compatible API:
   - `create : nativeint -> int -> int -> int -> NvencEncoder`
   - `encodeFrame : NvencEncoder -> nativeint -> int64 -> HevcAccessUnit array`
   - `finish : NvencEncoder -> HevcAccessUnit array`
   - `decoderConfig : NvencEncoder -> byte array option`

4. Owner: Mux. Either add `HevcMux.create` / `MuxOptions` / `WriteAccessUnit` wrapper, or update `Recording.fs` to call the actual `HevcMux.open`, `writeVideoSample`, `writeMetadataSample`, and `close`.

5. Owner: x265. Replace managed `X265PicturePrefix` marshalling with a C shim or generated offsets.

6. Owner: Product/docs. Update README, CLI help, launchers, and tests to clarify whether FFmpeg remains legacy or is removed.

## Code Snippets

### Recommended unified encoder contract

```fsharp
type HevcInputMode =
    | GpuTexture
    | CpuBgra

type HevcAccessUnit =
    { QpcTime: int64
      Data: byte array
      IsKeyFrame: bool
      ContainsParameterSets: bool }

type HevcEncoderOptions =
    { Backend: HevcBackend
      Width: int
      Height: int
      Fps: int
      Preset: string
      Lossless: bool
      X265Path: string
      D3D11Device: nativeint option }

type IHevcEncoder =
    inherit IDisposable
    abstract Backend: HevcBackend
    abstract InputMode: HevcInputMode
    abstract EncodeFrame: HevcFrame -> HevcAccessUnit array
    abstract Finish: unit -> HevcAccessUnit array
    abstract DecoderConfig: byte array option
```

### Recommended WGC-to-encoder flow

```fsharp
use session = WgcCapture.startForDuration 0 config.Fps config.DurationSec

let initialOptions =
    { Backend = Encoder.parseBackend config.Backend
      Width = session.Width
      Height = session.Height
      Fps = config.Fps
      Preset = config.Preset
      Lossless = config.Lossless
      X265Path = config.X265Path
      D3D11Device = Some session.D3D11Device }

use encoder = Encoder.create initialOptions

if encoder.InputMode = CpuBgra then
    failwith "x265 CPU fallback requires WGC CPU BGRA readback, which is not implemented by the current WgcCapture contract."
```

### Recommended mux flow

```fsharp
let firstAu =
    encodeUntilFirstNonEmptyAccessUnit session encoder

let configBytes =
    match encoder.DecoderConfig with
    | Some hvcc -> hvcc
    | None when firstAu.ContainsParameterSets -> firstAu.Data
    | None -> failwith "HEVC muxer requires hvcC or VPS/SPS/PPS before writing MP4."

use mux =
    HevcMux.``open`` outFile session.Width session.Height config.Fps configBytes

HevcMux.writeVideoSample mux firstAu.Data firstAu.QpcTime

for au in remainingAccessUnits do
    HevcMux.writeVideoSample mux au.Data au.QpcTime

HevcMux.close mux
```

## CONTRACT MATRIX

| Inter-module call | Proposed caller | Actual callee | Status |
|---|---|---|---|
| `NvencHevc.isNvencAvailable()` | `Encoder.nvencAvailability` | `let isNvencAvailable ()` | OK |
| `NvencHevc.create options` | `Encoder.NvencAdapter` | `let create (d3dDevice) (width) (height) (fps)` | BROKEN |
| `inner.EncodeTexture(texture,width,height,pts)` | `Encoder.NvencAdapter` | `member EncodeFrame(texture,qpcTime)` | BROKEN |
| `inner.Flush()` | `Encoder.NvencAdapter` | `member Finish() : unit` / `let finish encoder` | BROKEN |
| `Nvenc encode returns HevcAccessUnit array` | `Encoder.NvencAdapter` | returns `byte array`; event emits `NvencPacket` | BROKEN |
| `WgcCapture.CaptureOptions` | `Recording.fs` | no such type | BROKEN |
| `WgcCapture.create options` | `Recording.fs` | `startForDuration monitorIndex fps durationSec` | BROKEN |
| `source.Frames() : seq<_>` | `Recording.fs` | `session.Frames : BlockingCollection<CapturedFrame>` | BROKEN |
| `frame.TextureHandle` | `Recording.fs` | `frame.Texture` | BROKEN |
| `frame.Pts` | `Recording.fs` | `frame.QpcTime` | BROKEN |
| `frame.TimestampUtc` | `Recording.fs` | no such field | BROKEN |
| `frame.CpuBgra` / `frame.CpuStride` | `Recording.fs` | no CPU fields | BROKEN |
| `WgcCapture.releaseFrameTexture` | Required by WGC ownership | exists as `releaseFrameTexture` | OK, but not called by proposed Recording |
| `HevcMux.MuxOptions` | `Recording.fs` | no such type | BROKEN |
| `HevcMux.create muxOptions` | `Recording.fs` | `HevcMux.open path width height fps hvcCOrFirstNals` | BROKEN |
| `mux.WriteAccessUnit au` | `Recording.fs` | `WriteVideoSample(nal,qpc)` | BROKEN |
| `mux.Finish()` | `Recording.fs` | `Close()` / `HevcMux.close` | BROKEN |
| `HevcMux metadata writing` | not called | `metadataPayloadJson`, `writeMetadataSample` | BROKEN / omitted |
| `x265_param_*` | `X265Encoder.fs` | uses native allocated param | Mostly OK |
| `x265_picture` write | `X265Encoder.fs` | partial managed prefix | BROKEN risk |
| `Config --backend/--container/--codec/--lossless` | Proposed diff | current source lacks fields; parser requires values | BROKEN until applied |
| `RecordResult additive fields` | Proposed Domain diff | Program serializes generically | OK if all constructors and consumers tolerate additions |
| FFmpeg removal | Proposed Recording rewrite | README, launchers, tests still FFmpeg-oriented | BROKEN docs/tooling alignment |

## VERDICT: INCOMPLETE

The work is not merely missing polish; the major module contracts do not line up. NVENC cannot be constructed through the abstraction, WGC cannot feed x265, Recording calls nonexistent WGC and HevcMux APIs, HevcMux requires encoder configuration that the abstraction does not expose, and the claimed lossless x265 path is not RGB byte-lossless. The current package should be treated as design notes plus partial components, not a complete native no-FFmpeg lossless HEVC recorder.