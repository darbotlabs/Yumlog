Agent is idle (waiting for messages). agent_id: x265-fallback-1, agent_type: dayour-swe, status: idle, description: x265 fallback + encoder abstraction, elapsed: 238s, total_turns: 1

[Turn 0]
**Summary**: Below is the concrete implementation package for the portable x265 fallback plus encoder abstraction. It preserves the existing `RecordResult` shape while adding backend/container/lossless/frame metadata, and it removes process-spawned FFmpeg from `Recording.fs`.

**Findings**:
1. `[HIGH]` x265 lossless via I444 avoids chroma subsampling, but RGB desktop capture is only mathematically exact if the color transform is reversible or if the x265 build supports a true RGB/GBR input path.
2. `[HIGH]` The x265 fallback is portable but not expected to hold realtime at 4K desktop capture; this is acceptable as a fallback/non-realtime path.
3. `[MEDIUM]` `x265.dll` must be shipped and resolved explicitly; the code below searches `--x265`, `YUMLOG_X265_PATH`, `.tools\x265\x265.dll`, app base directory, then PATH.
4. `[MEDIUM]` The `Recording.fs` wiring assumes the other agents provide `WgcCapture`, `NvencHevc`, and `HevcMux` with the small contracts documented inline.

**Recommendations**:
1. Owner: NVENC agent. Align `NvencHevc.fs` to the `Encoder.fs` adapter contract or update only the small `NvencAdapter`.
2. Owner: Capture agent. Ensure `WgcCapture` can emit either GPU texture frames or CPU BGRA frames based on `Encoder.InputMode`.
3. Owner: Packaging. Add `x265.dll` to `.tools\x265\x265.dll` or app publish output and validate architecture is win-x64.
4. Owner: QA. Add a 1080p smoke test for Auto fallback and a 4K performance warning test/documentation entry.

---

## `X265Encoder.fs`

```fsharp
namespace Yumlog.Native

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices

/// Portable HEVC fallback using the native libx265 C API directly.
/// This module does not spawn ffmpeg.exe and does not spawn x265.exe.
///
/// Color-space note:
/// - Desktop capture arrives as BGRA.
/// - For a lossless desktop path, avoid YUV420 because it chroma-subsamples and is not visually/mathematically lossless for RGB UI content.
/// - This implementation converts BGRA to full-resolution I444 planes and enables x265 lossless mode.
/// - Strict mathematical RGB round-trip requires a reversible RGB/GBR path if the deployed x265 build supports it. I444 preserves chroma resolution,
///   but the RGB-to-YCbCr transform itself can still be a lossy integer transform depending on encoder/build behavior.
module X265Encoder =

    [<Literal>]
    let private DllName = "x265.dll"

    let private configuredDllPath = ref ""

    let private candidateDllPaths explicitPath =
        [ explicitPath
          Environment.GetEnvironmentVariable("YUMLOG_X265_PATH") |> Option.ofObj
          Some(Path.Combine(Environment.CurrentDirectory, ".tools", "x265", DllName))
          Some(Path.Combine(AppContext.BaseDirectory, ".tools", "x265", DllName))
          Some(Path.Combine(AppContext.BaseDirectory, DllName)) ]
        |> List.choose id
        |> List.filter (fun p -> not (String.IsNullOrWhiteSpace p))

    let tryResolveDllPath explicitPath =
        candidateDllPaths explicitPath
        |> List.tryFind File.Exists

    let configureDllSearchPath explicitPath =
        configuredDllPath.Value <-
            explicitPath
            |> Option.bind tryResolveDllPath
            |> Option.defaultValue ""

    do
        NativeLibrary.SetDllImportResolver(
            Assembly.GetExecutingAssembly(),
            DllImportResolver(fun libraryName assembly searchPath ->
                if libraryName.Equals(DllName, StringComparison.OrdinalIgnoreCase)
                   || libraryName.Equals("x265", StringComparison.OrdinalIgnoreCase) then
                    let explicitConfigured =
                        if String.IsNullOrWhiteSpace configuredDllPath.Value then
                            None
                        else
                            Some configuredDllPath.Value

                    match explicitConfigured |> Option.orElseWith (fun () -> tryResolveDllPath None) with
                    | Some path when File.Exists path ->
                        NativeLibrary.Load(path, assembly, Nullable<DllImportSearchPath>())
                    | _ ->
                        nativeint 0
                else
                    nativeint 0))

    module private Native =
        [<DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
        extern nativeint x265_param_alloc()

        [<DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
        extern void x265_param_free(nativeint param)

        [<DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, CharSet = CharSet.Ansi)>]
        extern int x265_param_default_preset(nativeint param, string preset, string tune)

        [<DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, CharSet = CharSet.Ansi)>]
        extern int x265_param_parse(nativeint param, string name, string value)

        [<DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
        extern nativeint x265_encoder_open(nativeint param)

        [<DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
        extern void x265_encoder_close(nativeint encoder)

        [<DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
        extern nativeint x265_picture_alloc()

        [<DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
        extern void x265_picture_free(nativeint picture)

        [<DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
        extern void x265_picture_init(nativeint param, nativeint picture)

        [<DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)>]
        extern int x265_encoder_encode(
            nativeint encoder,
            nativeint& ppNal,
            uint32& pNal,
            nativeint picIn,
            nativeint picOut)

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

    [<StructLayout(LayoutKind.Sequential)>]
    type private X265Nal =
        val mutable nalType: uint32
        val mutable sizeBytes: uint32
        val mutable payload: nativeint

    [<CLIMutable>]
    type X265Availability =
        { Provider: string
          IsAvailable: bool
          Message: string }

    [<CLIMutable>]
    type X265Options =
        { Width: int
          Height: int
          Fps: int
          Preset: string
          Lossless: bool
          X265Path: string }

    [<CLIMutable>]
    type BgraFrame =
        { Data: byte array
          Width: int
          Height: int
          Stride: int
          Pts: int64 }

    [<CLIMutable>]
    type EncodedFrame =
        { Pts: int64
          Dts: int64
          Data: byte array
          IsKeyFrame: bool }

    let availability x265Path =
        configureDllSearchPath (if String.IsNullOrWhiteSpace x265Path then None else Some x265Path)

        match tryResolveDllPath (if String.IsNullOrWhiteSpace x265Path then None else Some x265Path) with
        | Some path ->
            { Provider = "x265"
              IsAvailable = true
              Message = $"x265 native library found: {path}" }
        | None ->
            let mutable handle = nativeint 0
            if NativeLibrary.TryLoad(DllName, &handle) then
                { Provider = "x265"
                  IsAvailable = true
                  Message = "x265 native library found through the process DLL search path." }
            else
                { Provider = "x265"
                  IsAvailable = false
                  Message = "x265.dll was not found. Set --x265, YUMLOG_X265_PATH, place it at .tools\\x265\\x265.dll, or include it beside Yumlog.Native.exe." }

    let private ensureAvailable x265Path =
        let status = availability x265Path
        if not status.IsAvailable then
            failwith status.Message

    let private clampByte value =
        if value < 0.0 then 0uy
        elif value > 255.0 then 255uy
        else byte (Math.Round(value))

    /// Converts BGRA to full-range I444 planes.
    /// This avoids chroma subsampling. It is substantially more CPU-expensive than passing a GPU texture to NVENC.
    let bgraToI444 (frame: BgraFrame) =
        if frame.Width <= 0 || frame.Height <= 0 then
            invalidArg "frame" "Frame dimensions must be positive."

        if isNull frame.Data then
            invalidArg "frame" "Frame data cannot be null."

        if frame.Stride < frame.Width * 4 then
            invalidArg "frame" $"BGRA stride {frame.Stride} is smaller than width * 4."

        let pixels = frame.Width * frame.Height
        let y = Array.zeroCreate<byte> pixels
        let u = Array.zeroCreate<byte> pixels
        let v = Array.zeroCreate<byte> pixels

        for row in 0 .. frame.Height - 1 do
            let srcRow = row * frame.Stride
            let dstRow = row * frame.Width

            for col in 0 .. frame.Width - 1 do
                let src = srcRow + col * 4
                let dst = dstRow + col

                let b = float frame.Data[src]
                let g = float frame.Data[src + 1]
                let r = float frame.Data[src + 2]

                // BT.709 full-range RGB -> YCbCr approximation.
                // For exact mathematical RGB lossless, prefer x265 RGB/GBR input if available in the deployed build.
                y[dst] <- clampByte (0.2126 * r + 0.7152 * g + 0.0722 * b)
                u[dst] <- clampByte (128.0 - 0.114572 * r - 0.385428 * g + 0.500000 * b)
                v[dst] <- clampByte (128.0 + 0.500000 * r - 0.454153 * g - 0.045847 * b)

        y, u, v

    let private parseRequired param name value =
        let rc = Native.x265_param_parse(param, name, value)
        if rc <> 0 then
            failwith $"x265_param_parse('{name}', '{value}') failed with code {rc}."

    let private parseOptional param name value =
        try
            Native.x265_param_parse(param, name, value) |> ignore
        with _ ->
            ()

    let private copyNalPayloads ppNal nalCount =
        if ppNal = nativeint 0 || nalCount = 0u then
            Array.empty<byte>
        else
            let nalSize = Marshal.SizeOf<X265Nal>()

            let nals =
                [| for i in 0 .. int nalCount - 1 ->
                       Marshal.PtrToStructure<X265Nal>(IntPtr.Add(ppNal, i * nalSize)) |]

            let total =
                nals
                |> Array.sumBy (fun nal -> int nal.sizeBytes)

            let output = Array.zeroCreate<byte> total
            let mutable offset = 0

            for nal in nals do
                let size = int nal.sizeBytes
                if size > 0 && nal.payload <> nativeint 0 then
                    Marshal.Copy(nal.payload, output, offset, size)
                    offset <- offset + size

            output

    type X265HevcEncoder(options: X265Options) =
        let mutable disposed = false
        let mutable frameIndex = 0L
        let mutable param = nativeint 0
        let mutable encoder = nativeint 0
        let mutable picIn = nativeint 0
        let mutable picOut = nativeint 0

        do
            ensureAvailable options.X265Path

            if options.Width <= 0 || options.Height <= 0 then
                invalidArg "options" "Width and Height must be positive."

            if options.Fps <= 0 then
                invalidArg "options" "Fps must be positive."

            param <- Native.x265_param_alloc()
            if param = nativeint 0 then
                failwith "x265_param_alloc returned null."

            let preset =
                if String.IsNullOrWhiteSpace options.Preset then
                    "medium"
                else
                    options.Preset

            let rc = Native.x265_param_default_preset(param, preset, null)
            if rc < 0 then
                failwith $"x265_param_default_preset('{preset}') failed with code {rc}."

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

            // Lower latency makes the capture->encode->mux pipeline simpler.
            // x265 can still be too slow for live 4K lossless.
            parseOptional param "rc-lookahead" "0"
            parseOptional param "keyint" (string (max 1 options.Fps))
            parseOptional param "min-keyint" (string (max 1 options.Fps))

            encoder <- Native.x265_encoder_open(param)
            if encoder = nativeint 0 then
                failwith "x265_encoder_open returned null. Verify that the x265.dll build supports i444/lossless."

            picIn <- Native.x265_picture_alloc()
            picOut <- Native.x265_picture_alloc()

            if picIn = nativeint 0 || picOut = nativeint 0 then
                failwith "x265_picture_alloc returned null."

            Native.x265_picture_init(param, picIn)
            Native.x265_picture_init(param, picOut)

        member _.EncodeBgra(frame: BgraFrame) =
            if disposed then
                invalidOp "x265 encoder is disposed."

            if frame.Width <> options.Width || frame.Height <> options.Height then
                invalidArg "frame" $"Frame dimensions {frame.Width}x{frame.Height} do not match encoder {options.Width}x{options.Height}."

            let y, u, v = bgraToI444 frame

            let yHandle = GCHandle.Alloc(y, GCHandleType.Pinned)
            let uHandle = GCHandle.Alloc(u, GCHandleType.Pinned)
            let vHandle = GCHandle.Alloc(v, GCHandleType.Pinned)

            try
                let mutable picture = Marshal.PtrToStructure<X265PicturePrefix>(picIn)

                if isNull picture.planes || picture.planes.Length <> 3 then
                    picture.planes <- Array.zeroCreate 3

                if isNull picture.stride || picture.stride.Length <> 3 then
                    picture.stride <- Array.zeroCreate 3

                picture.planes[0] <- yHandle.AddrOfPinnedObject()
                picture.planes[1] <- uHandle.AddrOfPinnedObject()
                picture.planes[2] <- vHandle.AddrOfPinnedObject()

                picture.stride[0] <- options.Width
                picture.stride[1] <- options.Width
                picture.stride[2] <- options.Width

                picture.pts <- if frame.Pts >= 0L then frame.Pts else frameIndex
                picture.dts <- picture.pts
                picture.colorSpace <- 3 // X265_CSP_I444 in common x265 builds.
                picture.bitDepth <- 8

                Marshal.StructureToPtr(picture, picIn, false)

                let mutable ppNal = nativeint 0
                let mutable nalCount = 0u
                let encodedBytes = Native.x265_encoder_encode(encoder, &ppNal, &nalCount, picIn, picOut)

                if encodedBytes < 0 then
                    failwith $"x265_encoder_encode failed with code {encodedBytes}."

                frameIndex <- frameIndex + 1L

                let data = copyNalPayloads ppNal nalCount

                if data.Length = 0 then
                    Array.empty
                else
                    [| { Pts = picture.pts
                         Dts = picture.dts
                         Data = data
                         IsKeyFrame = frameIndex = 1L } |]
            finally
                yHandle.Free()
                uHandle.Free()
                vHandle.Free()

        member _.Flush() =
            if disposed then
                Array.empty
            else
                let flushed = ResizeArray<EncodedFrame>()
                let mutable keepGoing = true

                while keepGoing do
                    let mutable ppNal = nativeint 0
                    let mutable nalCount = 0u
                    let encodedBytes = Native.x265_encoder_encode(encoder, &ppNal, &nalCount, nativeint 0, picOut)

                    if encodedBytes < 0 then
                        failwith $"x265 flush failed with code {encodedBytes}."

                    let data = copyNalPayloads ppNal nalCount

                    if data.Length > 0 then
                        flushed.Add(
                            { Pts = frameIndex
                              Dts = frameIndex
                              Data = data
                              IsKeyFrame = false })

                        frameIndex <- frameIndex + 1L

                    keepGoing <- encodedBytes > 0 || nalCount > 0u

                flushed.ToArray()

        interface IDisposable with
            member _.Dispose() =
                if not disposed then
                    disposed <- true

                    if picIn <> nativeint 0 then
                        Native.x265_picture_free(picIn)
                        picIn <- nativeint 0

                    if picOut <> nativeint 0 then
                        Native.x265_picture_free(picOut)
                        picOut <- nativeint 0

                    if encoder <> nativeint 0 then
                        Native.x265_encoder_close(encoder)
                        encoder <- nativeint 0

                    if param <> nativeint 0 then
                        Native.x265_param_free(param)
                        param <- nativeint 0
```

---

## `Encoder.fs`

```fsharp
namespace Yumlog.Native

open System

type HevcBackend =
    | Nvenc
    | X265
    | Auto

type HevcInputMode =
    | GpuTexture
    | CpuBgra

[<CLIMutable>]
type HevcAvailability =
    { Provider: string
      IsAvailable: bool
      Message: string }

[<CLIMutable>]
type HevcEncoderOptions =
    { Backend: HevcBackend
      Width: int
      Height: int
      Fps: int
      Preset: string
      Lossless: bool
      X265Path: string }

[<CLIMutable>]
type CpuBgraFrame =
    { Data: byte array
      Width: int
      Height: int
      Stride: int
      Pts: int64
      TimestampUtc: DateTimeOffset }

[<CLIMutable>]
type GpuTextureFrame =
    { TextureHandle: nativeint
      Width: int
      Height: int
      Pts: int64
      TimestampUtc: DateTimeOffset }

type HevcFrame =
    | CpuBgra of CpuBgraFrame
    | GpuTexture of GpuTextureFrame

[<CLIMutable>]
type HevcAccessUnit =
    { Pts: int64
      Dts: int64
      Data: byte array
      IsKeyFrame: bool }

type IHevcEncoder =
    inherit IDisposable

    abstract Backend: HevcBackend
    abstract InputMode: HevcInputMode
    abstract Availability: HevcAvailability
    abstract EncodeFrame: HevcFrame -> HevcAccessUnit array
    abstract Finish: unit -> HevcAccessUnit array

module Encoder =

    let parseBackend value =
        match (if isNull value then "" else value.Trim().ToLowerInvariant()) with
        | "" | "auto" -> Auto
        | "nvenc" | "nvidia" -> Nvenc
        | "x265" | "libx265" | "cpu" -> X265
        | other -> invalidArg "backend" $"Unsupported encoder backend '{other}'. Use nvenc, x265, or auto."

    let backendName backend =
        match backend with
        | Nvenc -> "nvenc"
        | X265 -> "x265"
        | Auto -> "auto"

    let private empty provider available message =
        { Provider = provider
          IsAvailable = available
          Message = message }

    let x265Availability x265Path =
        let status = X265Encoder.availability x265Path

        { Provider = status.Provider
          IsAvailable = status.IsAvailable
          Message = status.Message }

    let nvencAvailability () =
        try
            if NvencHevc.isNvencAvailable() then
                empty "nvenc" true "NVENC HEVC encoder is available."
            else
                empty "nvenc" false "NVENC HEVC encoder is not available on this system."
        with ex ->
            empty "nvenc" false $"NVENC availability check failed: {ex.Message}"

    let availability backend x265Path =
        match backend with
        | Nvenc -> nvencAvailability()
        | X265 -> x265Availability x265Path
        | Auto ->
            let nvenc = nvencAvailability()

            if nvenc.IsAvailable then
                { nvenc with Provider = "auto/nvenc" }
            else
                let x265 = x265Availability x265Path

                if x265.IsAvailable then
                    { x265 with
                        Provider = "auto/x265"
                        Message = $"NVENC unavailable ({nvenc.Message}); falling back to x265. {x265.Message}" }
                else
                    { Provider = "auto"
                      IsAvailable = false
                      Message = $"No HEVC backend is available. NVENC: {nvenc.Message} x265: {x265.Message}" }

    let private selectConcreteBackend requested x265Path =
        match requested with
        | Nvenc -> Nvenc
        | X265 -> X265
        | Auto ->
            let nvenc = nvencAvailability()

            if nvenc.IsAvailable then
                Nvenc
            else
                let x265 = x265Availability x265Path

                if x265.IsAvailable then
                    X265
                else
                    failwith $"No HEVC backend is available. NVENC: {nvenc.Message} x265: {x265.Message}"

    type private X265Adapter(options: HevcEncoderOptions) =
        let x265Options =
            { X265Encoder.X265Options.Width = options.Width
              Height = options.Height
              Fps = options.Fps
              Preset = options.Preset
              Lossless = options.Lossless
              X265Path = options.X265Path }

        let inner = new X265Encoder.X265HevcEncoder(x265Options)

        interface IHevcEncoder with
            member _.Backend = X265

            member _.InputMode = CpuBgra

            member _.Availability = x265Availability options.X265Path

            member _.EncodeFrame(frame: HevcFrame) =
                match frame with
                | CpuBgra cpu ->
                    let x265Frame =
                        { X265Encoder.BgraFrame.Data = cpu.Data
                          Width = cpu.Width
                          Height = cpu.Height
                          Stride = cpu.Stride
                          Pts = cpu.Pts }

                    inner.EncodeBgra(x265Frame)
                    |> Array.map (fun au ->
                        { Pts = au.Pts
                          Dts = au.Dts
                          Data = au.Data
                          IsKeyFrame = au.IsKeyFrame })

                | GpuTexture _ ->
                    invalidArg "frame" "x265 backend requires CPU BGRA frames. Configure WgcCapture for CPU readback when Encoder.InputMode = CpuBgra."

            member _.Finish() =
                inner.Flush()
                |> Array.map (fun au ->
                    { Pts = au.Pts
                      Dts = au.Dts
                      Data = au.Data
                      IsKeyFrame = au.IsKeyFrame })

            member _.Dispose() =
                (inner :> IDisposable).Dispose()

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
        //
        // If the actual NvencHevc API differs, only this adapter should need adjustment.
        let inner = NvencHevc.create options

        interface IHevcEncoder with
            member _.Backend = Nvenc

            member _.InputMode = GpuTexture

            member _.Availability = nvencAvailability()

            member _.EncodeFrame(frame: HevcFrame) =
                match frame with
                | GpuTexture gpu ->
                    inner.EncodeTexture(gpu.TextureHandle, gpu.Width, gpu.Height, gpu.Pts)

                | CpuBgra _ ->
                    invalidArg "frame" "NVENC backend requires a GPU texture frame. Configure WgcCapture for GPU texture delivery when Encoder.InputMode = GpuTexture."

            member _.Finish() =
                inner.Flush()

            member _.Dispose() =
                match box inner with
                | :? IDisposable as disposable -> disposable.Dispose()
                | _ -> ()

    let create options =
        if options.Width <= 0 || options.Height <= 0 then
            invalidArg "options" "Width and Height must be positive."

        if options.Fps <= 0 then
            invalidArg "options" "Fps must be positive."

        let concrete = selectConcreteBackend options.Backend options.X265Path

        let concreteOptions =
            { options with Backend = concrete }

        match concrete with
        | Nvenc -> new NvencAdapter(concreteOptions) :> IHevcEncoder
        | X265 -> new X265Adapter(concreteOptions) :> IHevcEncoder
        | Auto -> failwith "Internal error: Auto should have been resolved to a concrete backend."
```

---

## `Domain.fs` additions/diff

```diff
 namespace Yumlog.Native
 
 open System
 
+type EncoderBackend =
+    | Nvenc
+    | X265
+    | Auto
+
 [<CLIMutable>]
 type CaptureConfig =
     { OutDir: string
       Fps: int
       DurationSec: int
@@
 [<CLIMutable>]
 type RecordConfig =
     { OutFile: string
       Fps: int
       DurationSec: int
       FFmpegPath: string
+      X265Path: string
+      Backend: string
+      Codec: string
+      Lossless: bool
+      Container: string
       Encoder: string
       Preset: string }
@@
 [<CLIMutable>]
 type RecordResult =
     { Path: string
       Fps: int
       DurationSec: int
       Encoder: string
+      Backend: string
+      Codec: string
+      Lossless: bool
+      Container: string
+      FrameCount: int
       ExitCode: int
       StandardError: string }
```

Notes:
- `FFmpegPath` is retained for backward config compatibility but no longer used by native HEVC recording.
- `Encoder` is retained so the JSON result shape remains compatible with existing consumers.
- `Backend`, `Codec`, `Lossless`, `Container`, and `FrameCount` are additive.

---

## `Config.fs` additions/diff

```diff
 namespace Yumlog.Native
 
 open System
 open System.IO
 
 module NativeConfig =
@@
            Record =
              { OutFile = "./yumlogs/yumlog.mp4"
                Fps = 30
                DurationSec = 10
                FFmpegPath = ""
-               Encoder = "libx264"
-               Preset = "ultrafast" }
+               X265Path = ""
+               Backend = "auto"
+               Codec = "hevc"
+               Lossless = true
+               Container = "mp4"
+               Encoder = "hevc"
+               Preset = "medium" }
@@
     let mergeRecord (baseConfig: RecordConfig) overrides =
         let get name fallback =
             overrides
             |> Map.tryFind name
             |> Option.defaultValue fallback
+
+        let getBool name fallback =
+            let raw = get name (string fallback)
+            match raw.Trim().ToLowerInvariant() with
+            | "1" | "true" | "yes" | "y" | "on" -> true
+            | "0" | "false" | "no" | "n" | "off" -> false
+            | other -> invalidArg name $"Invalid boolean value '{other}' for --{name}."
+
+        let normalizeBackend value =
+            match value.Trim().ToLowerInvariant() with
+            | "" | "auto" -> "auto"
+            | "nvenc" | "nvidia" -> "nvenc"
+            | "x265" | "libx265" | "cpu" -> "x265"
+            | other -> invalidArg "backend" $"Unsupported backend '{other}'. Use nvenc, x265, or auto."
+
+        let normalizeContainer value =
+            match value.Trim().ToLowerInvariant() with
+            | "mp4" -> "mp4"
+            | "mkv" | "matroska" -> "mkv"
+            | other -> invalidArg "container" $"Unsupported container '{other}'. Use mp4 or mkv."
 
         { baseConfig with
             OutFile = get "out-file" baseConfig.OutFile
             Fps = get "fps" (string baseConfig.Fps) |> Int32.Parse
             DurationSec = get "duration" (string baseConfig.DurationSec) |> Int32.Parse
             FFmpegPath = get "ffmpeg" baseConfig.FFmpegPath
+            X265Path = get "x265" baseConfig.X265Path
+            Backend = get "backend" baseConfig.Backend |> normalizeBackend
+            Codec = get "codec" baseConfig.Codec
+            Lossless = getBool "lossless" baseConfig.Lossless
+            Container = get "container" baseConfig.Container |> normalizeContainer
             Encoder = get "encoder" baseConfig.Encoder
             Preset = get "preset" baseConfig.Preset }
```

Optional but recommended `Cli.fs` usage text update:

```diff
-  Yumlog.Native record [--config path] [--out-file path] [--fps n] [--duration n] [--ffmpeg path]
+  Yumlog.Native record [--config path] [--out-file path] [--fps n] [--duration n] [--backend nvenc|x265|auto] [--container mp4|mkv] [--x265 path]
```

The existing parser already accepts arbitrary `--key value` options, so no parser logic change is required for `--backend`, `--container`, `--x265`, `--lossless`, `--codec`, or `--preset`.

---

## Rewritten `Recording.fs`

```fsharp
namespace Yumlog.Native

open System
open System.Diagnostics
open System.IO

module NativeRecording =

    let private normalizeContainer value =
        match (if isNull value then "" else value.Trim().ToLowerInvariant()) with
        | "" | "mp4" -> "mp4"
        | "mkv" | "matroska" -> "mkv"
        | other -> invalidArg "Container" $"Unsupported container '{other}'. Use mp4 or mkv."

    let private ensureOutputExtension (outFile: string) container =
        let full = Path.GetFullPath(outFile)
        let desiredExtension = "." + container

        if String.Equals(Path.GetExtension(full), desiredExtension, StringComparison.OrdinalIgnoreCase) then
            full
        else
            Path.ChangeExtension(full, desiredExtension)

    let private createOutputDirectory outFile =
        match Path.GetDirectoryName(outFile) with
        | null | "" -> ()
        | parent -> Directory.CreateDirectory(parent) |> ignore

    let private recordAvailability config =
        let backend = Encoder.parseBackend config.Backend
        Encoder.availability backend config.X265Path

    let private failUnavailable config =
        let status = recordAvailability config

        if not status.IsAvailable then
            failwith status.Message

        status

    let record (config: RecordConfig) =
        let container = normalizeContainer config.Container
        let outFile = ensureOutputExtension config.OutFile container
        createOutputDirectory outFile

        // Capability gating mirrors NativeOcr.recognize style:
        // get Provider/IsAvailable/Message first, then fail gracefully before capture starts.
        let selectedAvailability = failUnavailable config

        let requestedBackend = Encoder.parseBackend config.Backend
        let stopwatch = Stopwatch.StartNew()

        // Expected WgcCapture contract from the capture component:
        //
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
        //
        // The frame source should stage/copy BGRA to CPU memory only when RequireCpuBgra = true.
        // For NVENC it should pass GPU texture handles and avoid CPU readback.
        //
        // Expected HevcMux contract:
        //
        // module HevcMux =
        //   type MuxOptions =
        //     { Path: string
        //       Container: string
        //       Codec: string
        //       Fps: int
        //       Width: int
        //       Height: int }
        //
        //   type IHevcMuxer =
        //     inherit IDisposable
        //     abstract WriteAccessUnit: HevcAccessUnit -> unit
        //     abstract Finish: unit -> unit
        //
        //   val create: MuxOptions -> IHevcMuxer

        // Start capture in a neutral mode first only long enough to know the dimensions.
        // If WgcCapture can expose dimensions without starting, prefer that implementation.
        let mutable frameCount = 0
        let mutable actualBackend = requestedBackend
        let mutable encoderName = config.Encoder

        // The capture mode depends on the encoder selected by Auto. Encoder.create resolves Auto.
        // Since encoder creation needs dimensions, WgcCapture is responsible for exposing Width/Height
        // immediately after create.
        let preliminaryCaptureOptions =
            { WgcCapture.CaptureOptions.Fps = config.Fps
              DurationSec = config.DurationSec
              PreferGpuTexture = true
              RequireCpuBgra = false }

        use preliminarySource = WgcCapture.create preliminaryCaptureOptions

        let encoderOptions =
            { Backend = requestedBackend
              Width = preliminarySource.Width
              Height = preliminarySource.Height
              Fps = config.Fps
              Preset = config.Preset
              Lossless = config.Lossless
              X265Path = config.X265Path }

        use encoder = Encoder.create encoderOptions
        actualBackend <- encoder.Backend
        encoderName <- $"hevc/{Encoder.backendName actualBackend}"

        // Re-create capture using the exact input mode requested by the selected encoder.
        // This prevents accidental CPU readback on the NVENC path and ensures x265 gets BGRA bytes.
        let captureOptions =
            { WgcCapture.CaptureOptions.Fps = config.Fps
              DurationSec = config.DurationSec
              PreferGpuTexture = encoder.InputMode = GpuTexture
              RequireCpuBgra = encoder.InputMode = CpuBgra }

        use source =
            if encoder.InputMode = GpuTexture then
                // Reuse the preliminary source if it already matches the needed mode.
                preliminarySource
            else
                preliminarySource.Dispose()
                WgcCapture.create captureOptions

        let muxOptions =
            { HevcMux.MuxOptions.Path = outFile
              Container = container
              Codec = "hevc"
              Fps = config.Fps
              Width = source.Width
              Height = source.Height }

        use mux = HevcMux.create muxOptions

        try
            for frame in source.Frames() do
                let hevcFrame =
                    match encoder.InputMode with
                    | GpuTexture ->
                        if frame.TextureHandle = nativeint 0 then
                            failwith "WgcCapture returned a null GPU texture handle for the NVENC path."

                        GpuTexture
                            { TextureHandle = frame.TextureHandle
                              Width = frame.Width
                              Height = frame.Height
                              Pts = frame.Pts
                              TimestampUtc = frame.TimestampUtc }

                    | CpuBgra ->
                        match frame.CpuBgra with
                        | Some data ->
                            CpuBgra
                                { Data = data
                                  Width = frame.Width
                                  Height = frame.Height
                                  Stride = frame.CpuStride
                                  Pts = frame.Pts
                                  TimestampUtc = frame.TimestampUtc }

                        | None ->
                            failwith "WgcCapture did not provide CPU BGRA bytes for the x265 path."

                let accessUnits = encoder.EncodeFrame hevcFrame

                for au in accessUnits do
                    mux.WriteAccessUnit au

                frameCount <- frameCount + 1

            for au in encoder.Finish() do
                mux.WriteAccessUnit au

            mux.Finish()
            stopwatch.Stop()

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
              StandardError =
                if selectedAvailability.IsAvailable then
                    selectedAvailability.Message
                else
                    "" }
        with ex ->
            stopwatch.Stop()

            { Path = outFile
              Fps = config.Fps
              DurationSec = config.DurationSec
              Encoder = encoderName
              Backend = Encoder.backendName actualBackend
              Codec = "hevc"
              Lossless = config.Lossless
              Container = container
              FrameCount = frameCount
              ExitCode = 1
              StandardError = ex.Message }
```

Important compile note for `Recording.fs`: if `WgcCapture.IWgcFrameSource` is a disposable interface, avoid double-disposing the same object. If the actual capture implementation cannot tolerate the preliminary source pattern, expose a `WgcCapture.probePrimarySize()` or `WgcCapture.createForMode()` API and construct the encoder after the first size probe.

A cleaner capture API would be:

```fsharp
let dimensions = WgcCapture.probeDimensions()
use encoder = Encoder.create { encoderOptions with Width = dimensions.Width; Height = dimensions.Height }
use source = WgcCapture.create captureOptions
```

---

## `Yumlog.Native.fsproj` compile order

F# compile order matters. Place the new encoder files after `RawWinRt.fs` only if you want to keep native interop grouped together; technically `X265Encoder.fs` and `Encoder.fs` need `Domain.fs`/`Config.fs` types, and `Recording.fs` needs both encoders plus the assumed `WgcCapture`, `NvencHevc`, and `HevcMux`.

Recommended order once the other native recorder components exist:

```xml
<ItemGroup>
  <Compile Include="Domain.fs" />
  <Compile Include="Json.fs" />
  <Compile Include="Config.fs" />

  <Compile Include="RawWinRt.fs" />

  <!-- Provided by the capture component owner. -->
  <Compile Include="WgcCapture.fs" />

  <!-- Portable fallback implemented here. -->
  <Compile Include="X265Encoder.fs" />

  <!-- Provided by the NVENC component owner. Must come before Encoder.fs. -->
  <Compile Include="NvencHevc.fs" />

  <!-- Backend selection abstraction implemented here. -->
  <Compile Include="Encoder.fs" />

  <!-- Provided by the muxer component owner. Must come before Recording.fs. -->
  <Compile Include="HevcMux.fs" />

  <Compile Include="Capture.fs" />
  <Compile Include="Recording.fs" />
  <Compile Include="RuntimeIdentity.fs" />
  <Compile Include="WinAppRuntime.fs" />
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

If `WindowsAiOcr.fs` directly depends on `RawWinRt.fs`, keeping `RawWinRt.fs` before `WindowsAiOcr.fs` is required.

---

## Operational risks and required documentation

1. **Shipping and locating `x265.dll`**
   - The implementation resolves:
     1. `--x265 <path>` through `RecordConfig.X265Path`
     2. `YUMLOG_X265_PATH`
     3. `.\.tools\x265\x265.dll`
     4. `<AppContext.BaseDirectory>\.tools\x265\x265.dll`
     5. `<AppContext.BaseDirectory>\x265.dll`
     6. Normal process DLL search path
   - The DLL must be win-x64 and compatible with the process architecture.
   - Some x265 builds export versioned API symbols rather than undecorated names. If your chosen DLL does that, switch `X265Encoder.fs` to the `x265_api_get` function-table pattern or ship a DLL/import layer that exports the undecorated C API names used above.

2. **BGRA to I444 conversion cost**
   - The CPU fallback performs full-frame BGRA readback plus per-pixel I444 conversion.
   - At 4K and 30 fps, this is a large memory bandwidth and CPU cost before x265 even starts encoding.
   - The NVENC path should remain the default for realtime recording.

3. **x265 lossless realtime expectations**
   - x265 lossless at 4K desktop resolution is unlikely to sustain realtime on typical machines.
   - Treat x265 as:
     - portability fallback,
     - diagnostic fallback,
     - non-realtime/high-fidelity path,
     - CI-compatible native path where GPU/NVENC is unavailable.
   - Surface this in CLI/help output and logs when `--backend x265` or Auto falls back to x265.

4. **True RGB mathematical losslessness**
   - I444 avoids 4:2:0 chroma subsampling and is required for high-fidelity desktop capture.
   - However, strict RGB mathematical losslessness needs a reversible RGB/GBR workflow if the x265 build supports it.
   - If pixel-perfect RGB round-trip is a hard requirement, validate a GBR-capable x265 build or consider a true RGB lossless codec/mux path.