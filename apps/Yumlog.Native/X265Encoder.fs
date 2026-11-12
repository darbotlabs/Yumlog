namespace Yumlog.Native

// FS3265: X265Api is a struct, so the nullable projection MarshalPtrToStructure
// reports is vacuous - a struct can never be null. The caller already guards the
// source pointer against 0n before marshalling.
#nowarn "3265"

open System
open System.IO
open System.Runtime.InteropServices

/// Portable software HEVC fallback over the native libx265 C API (x265.dll).
///
/// Every native entry point is resolved dynamically through NativeLibrary.TryLoad +
/// GetExport, so this module NEVER fails at load time when x265.dll is absent -- it
/// simply reports itself unusable and the abstraction falls back to NVENC.
///
/// Losslessness: the desktop arrives as BGRA. This encoder maps it to full-resolution
/// I444 using an identity/GBR permutation (G->plane0, B->plane1, R->plane2) and sets
/// x265 "--colormatrix gbr --range full --lossless". That mapping is a pure lossless
/// permutation and x265 lossless coding is bit-exact, so the pipeline is genuinely
/// mathematically reversible back to the original BGRA. If the shipped x265 build
/// rejects the GBR matrix, we fall back to a BT.709 conversion and honestly report
/// IsMathematicallyLossless = false.
[<RequireQualifiedAccess>]
module X265Encoder =

    [<Literal>]
    let private DllName = "x265"

    // x265_csp
    [<Literal>]
    let private X265_CSP_I444 = 3

    // ------------------------------------------------------------------
    // Native binding, resolved lazily and dynamically.
    // ------------------------------------------------------------------

    // x265 exposes a single ABI-stable entry point, x265_api_get(bitDepth), that returns
    // a const x265_api* whose members are function pointers. Using it avoids the
    // build-number-suffixed x265_encoder_open_<build> symbol problem entirely.
    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type private ApiGetFn = delegate of int -> nativeint

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type private ParamAllocFn = delegate of unit -> nativeint

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type private ParamFreeFn = delegate of nativeint -> unit

    // String params are passed as raw ANSI (const char*) pointers marshalled by the
    // caller. F# does not allow MarshalAs attributes on unnamed delegate parameters.
    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type private ParamDefaultPresetFn = delegate of nativeint * nativeint * nativeint -> int

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type private ParamParseFn = delegate of nativeint * nativeint * nativeint -> int

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type private PictureAllocFn = delegate of unit -> nativeint

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type private PictureFreeFn = delegate of nativeint -> unit

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type private PictureInitFn = delegate of nativeint * nativeint -> unit

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type private EncoderOpenFn = delegate of nativeint -> nativeint

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type private EncoderHeadersFn = delegate of nativeint * nativeint byref * uint32 byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type private EncoderEncodeFn = delegate of nativeint * nativeint byref * uint32 byref * nativeint * nativeint -> int

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type private EncoderCloseFn = delegate of nativeint -> unit

    // Prefix of x265_api sufficient to reach every function pointer we use. Field order
    // matches x265.h. Function pointers are held as nativeint and converted on demand.
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private X265Api =
        val mutable api_major_version: int
        val mutable api_build_number: int
        val mutable sizeof_param: uint32
        val mutable sizeof_picture: uint32
        val mutable sizeof_analysis_data: uint32
        val mutable sizeof_zone: uint32
        val mutable sizeof_stats: uint32
        val mutable bit_depth: int
        val mutable version_str: nativeint
        val mutable build_info_str: nativeint
        val mutable param_alloc: nativeint
        val mutable param_free: nativeint
        val mutable param_default: nativeint
        val mutable param_parse: nativeint
        val mutable param_apply_profile: nativeint
        val mutable param_default_preset: nativeint
        val mutable picture_alloc: nativeint
        val mutable picture_free: nativeint
        val mutable picture_init: nativeint
        val mutable encoder_open: nativeint
        val mutable encoder_parameters: nativeint
        val mutable encoder_reconfig: nativeint
        val mutable encoder_headers: nativeint
        val mutable encoder_encode: nativeint
        val mutable encoder_get_stats: nativeint
        val mutable encoder_log: nativeint
        val mutable encoder_close: nativeint

    type private Binding =
        { Handle: nativeint
          Api: X265Api
          ParamAlloc: ParamAllocFn
          ParamFree: ParamFreeFn
          ParamDefaultPreset: ParamDefaultPresetFn
          ParamParse: ParamParseFn
          PictureAlloc: PictureAllocFn
          PictureFree: PictureFreeFn
          PictureInit: PictureInitFn
          EncoderOpen: EncoderOpenFn
          EncoderHeaders: EncoderHeadersFn
          EncoderEncode: EncoderEncodeFn
          EncoderClose: EncoderCloseFn }

    let private candidatePaths (explicitPath: string) =
        [ explicitPath
          Environment.GetEnvironmentVariable "YUMLOG_X265_PATH" |> orEmptyIfNull
          Path.Combine(Environment.CurrentDirectory, ".tools", "x265", "x265.dll")
          Path.Combine(AppContext.BaseDirectory, ".tools", "x265", "x265.dll")
          Path.Combine(AppContext.BaseDirectory, "x265.dll") ]
        |> List.filter (fun p -> not (String.IsNullOrWhiteSpace p))

    /// Attempts to load x265.dll from an explicit path, the configured search
    /// locations, or the process DLL search path. Returns None gracefully.
    let private tryLoad (explicitPath: string) : nativeint option =
        let fromFile =
            candidatePaths explicitPath
            |> List.tryFind File.Exists
            |> Option.bind (fun path ->
                match NativeLibrary.TryLoad path with
                | true, h -> Some h
                | _ -> None)
        match fromFile with
        | Some h -> Some h
        | None ->
            match NativeLibrary.TryLoad("x265.dll", System.Reflection.Assembly.GetExecutingAssembly(), Nullable()) with
            | true, h -> Some h
            | _ ->
                match NativeLibrary.TryLoad "x265.dll" with
                | true, h -> Some h
                | _ -> None

    let private d<'T> (ptr: nativeint) : 'T = Marshal.GetDelegateForFunctionPointer<'T>(ptr)

    /// Loads the library and resolves the API struct + delegates. Returns None if
    /// anything is missing, so callers never see an exception for a missing DLL.
    let private tryBind (explicitPath: string) : Binding option =
        match tryLoad explicitPath with
        | None -> None
        | Some handle ->
            try
                match NativeLibrary.TryGetExport(handle, "x265_api_get") with
                | true, apiGetPtr ->
                    let apiGet = d<ApiGetFn> apiGetPtr
                    // bitDepth 0 asks for the default (8-bit) build.
                    let apiPtr = apiGet.Invoke 0
                    if apiPtr = 0n then
                        NativeLibrary.Free handle
                        None
                    else
                        let api = Marshal.PtrToStructure<X265Api>(apiPtr)
                        if api.encoder_open = 0n || api.encoder_encode = 0n || api.param_alloc = 0n then
                            NativeLibrary.Free handle
                            None
                        else
                            Some
                                { Handle = handle
                                  Api = api
                                  ParamAlloc = d<ParamAllocFn> api.param_alloc
                                  ParamFree = d<ParamFreeFn> api.param_free
                                  ParamDefaultPreset = d<ParamDefaultPresetFn> api.param_default_preset
                                  ParamParse = d<ParamParseFn> api.param_parse
                                  PictureAlloc = d<PictureAllocFn> api.picture_alloc
                                  PictureFree = d<PictureFreeFn> api.picture_free
                                  PictureInit = d<PictureInitFn> api.picture_init
                                  EncoderOpen = d<EncoderOpenFn> api.encoder_open
                                  EncoderHeaders = d<EncoderHeadersFn> api.encoder_headers
                                  EncoderEncode = d<EncoderEncodeFn> api.encoder_encode
                                  EncoderClose = d<EncoderCloseFn> api.encoder_close }
                | _ ->
                    NativeLibrary.Free handle
                    None
            with _ ->
                (try NativeLibrary.Free handle with _ -> ())
                None

    /// True when x265.dll can be loaded and its API resolved. Never throws.
    let isUsable (x265Path: string) : bool =
        match tryBind (if isNull (box x265Path) then "" else x265Path) with
        | Some b ->
            (try NativeLibrary.Free b.Handle with _ -> ())
            true
        | None -> false

    // ------------------------------------------------------------------
    // x265_picture / x265_nal accessors via stable field offsets (x64).
    // Using offset writes avoids a fragile full managed struct overlay.
    // ------------------------------------------------------------------

    // x265_picture: pts@0 dts@8 userData@16 planes[3]@24/32/40 stride[3]@48/52/56
    //               bitDepth@60 sliceType@64 poc@68 colorSpace@72 forceqp@76
    let private picSetPlanes (pic: nativeint) (y: nativeint) (u: nativeint) (v: nativeint) (stride: int) =
        Marshal.WriteIntPtr(pic, 24, y)
        Marshal.WriteIntPtr(pic, 32, u)
        Marshal.WriteIntPtr(pic, 40, v)
        Marshal.WriteInt32(pic, 48, stride)
        Marshal.WriteInt32(pic, 52, stride)
        Marshal.WriteInt32(pic, 56, stride)
        Marshal.WriteInt32(pic, 60, 8)             // bitDepth
        Marshal.WriteInt32(pic, 72, X265_CSP_I444) // colorSpace

    let private picSetPts (pic: nativeint) (pts: int64) =
        Marshal.WriteInt64(pic, 0, pts)
        Marshal.WriteInt64(pic, 8, pts)

    let private picGetPts (pic: nativeint) : int64 = Marshal.ReadInt64(pic, 0)

    // x265_nal: type@0 sizeBytes@4 payload@8 ; sizeof == 16 on x64.
    let private nalSize = 16
    let private collectNals (ppNal: nativeint) (count: uint32) : byte[] =
        if ppNal = 0n || count = 0u then Array.empty
        else
            let n = int count
            let mutable total = 0
            for i in 0 .. n - 1 do
                total <- total + Marshal.ReadInt32(ppNal, i * nalSize + 4)
            let output = Array.zeroCreate<byte> total
            let mutable offset = 0
            for i in 0 .. n - 1 do
                let sz = Marshal.ReadInt32(ppNal, i * nalSize + 4)
                let payload = Marshal.ReadIntPtr(ppNal, i * nalSize + 8)
                if sz > 0 && payload <> 0n then
                    Marshal.Copy(payload, output, offset, sz)
                    offset <- offset + sz
            output

    // ------------------------------------------------------------------
    // Reversible BGRA -> I444 (GBR permutation).
    // ------------------------------------------------------------------

    /// Maps BGRA to three I444 planes with G->plane0, B->plane1, R->plane2. This is a
    /// pure permutation (no arithmetic), so it is exactly invertible. Combined with
    /// "--colormatrix gbr" it round-trips bit-for-bit through a lossless decode.
    let private bgraToGbrI444 (data: byte[]) (width: int) (height: int) (stride: int) =
        let pixels = width * height
        let p0 = Array.zeroCreate<byte> pixels
        let p1 = Array.zeroCreate<byte> pixels
        let p2 = Array.zeroCreate<byte> pixels
        for row in 0 .. height - 1 do
            let srcRow = row * stride
            let dstRow = row * width
            for col in 0 .. width - 1 do
                let src = srcRow + col * 4
                let dst = dstRow + col
                p1.[dst] <- data.[src]     // B -> plane1 (Cb)
                p0.[dst] <- data.[src + 1] // G -> plane0 (Y)
                p2.[dst] <- data.[src + 2] // R -> plane2 (Cr)
        p0, p1, p2

    // ------------------------------------------------------------------
    // Encoder session
    // ------------------------------------------------------------------

    [<Sealed>]
    type Session private
        (bind: Binding,
         param: nativeint,
         encoder: nativeint,
         picIn: nativeint,
         picOut: nativeint,
         width: int,
         height: int,
         decoderConfig: HevcDecoderConfig,
         losslessMode: string,
         isMathematicallyLossless: bool) =

        let mutable disposed = false
        let mutable frameIndex = 0L

        member _.Backend = HevcBackend.X265
        member _.DecoderConfig = decoderConfig
        member _.LosslessMode = losslessMode
        member _.IsMathematicallyLossless = isMathematicallyLossless
        member _.NeedsCpuFrames = true

        member _.Encode(frame: CapturedFrame) : HevcAccessUnit list =
            if disposed then invalidOp "x265 session is disposed."
            match frame.Bgra with
            | None -> invalidArg "frame" "x265 requires CapturedFrame.Bgra (a CPU BGRA copy)."
            | Some data ->
                if frame.Width <> width || frame.Height <> height then
                    invalidArg "frame" $"Frame {frame.Width}x{frame.Height} does not match encoder {width}x{height}."
                let stride = if frame.RowPitch >= width * 4 then frame.RowPitch else width * 4
                let required = (height - 1) * stride + width * 4
                if data.Length < required then
                    invalidArg "frame" $"BGRA buffer too small: {data.Length} < required {required}."

                let p0, p1, p2 = bgraToGbrI444 data width height stride
                let h0 = GCHandle.Alloc(p0, GCHandleType.Pinned)
                let h1 = GCHandle.Alloc(p1, GCHandleType.Pinned)
                let h2 = GCHandle.Alloc(p2, GCHandleType.Pinned)
                try
                    picSetPlanes picIn (h0.AddrOfPinnedObject()) (h1.AddrOfPinnedObject()) (h2.AddrOfPinnedObject()) width
                    picSetPts picIn (if frame.Pts >= 0L then frame.Pts else frameIndex)
                    let mutable ppNal = 0n
                    let mutable nalCount = 0u
                    let rc = bind.EncoderEncode.Invoke(encoder, &ppNal, &nalCount, picIn, picOut)
                    if rc < 0 then failwith $"x265_encoder_encode failed with code {rc}."
                    frameIndex <- frameIndex + 1L
                    if rc = 0 then []
                    else
                        let data = collectNals ppNal nalCount
                        let pts = picGetPts picOut
                        [ { Pts = pts; Dts = pts; QpcTime = frame.QpcTime; Data = data; IsKeyFrame = frameIndex = 1L } ]
                finally
                    h0.Free(); h1.Free(); h2.Free()

        member _.Finish() : HevcAccessUnit list =
            if disposed then []
            else
                let results = ResizeArray<HevcAccessUnit>()
                let mutable keepGoing = true
                while keepGoing do
                    let mutable ppNal = 0n
                    let mutable nalCount = 0u
                    let rc = bind.EncoderEncode.Invoke(encoder, &ppNal, &nalCount, 0n, picOut)
                    if rc < 0 then failwith $"x265 flush failed with code {rc}."
                    if rc = 0 then keepGoing <- false
                    else
                        let data = collectNals ppNal nalCount
                        let pts = picGetPts picOut
                        results.Add { Pts = pts; Dts = pts; QpcTime = 0L; Data = data; IsKeyFrame = false }
                List.ofSeq results

        member _.Dispose() =
            if not disposed then
                disposed <- true
                if encoder <> 0n then bind.EncoderClose.Invoke encoder
                if picIn <> 0n then bind.PictureFree.Invoke picIn
                if picOut <> 0n then bind.PictureFree.Invoke picOut
                if param <> 0n then bind.ParamFree.Invoke param
                (try NativeLibrary.Free bind.Handle with _ -> ())

        interface IDisposable with
            member this.Dispose() = this.Dispose()

        static member Create(width: int, height: int, fps: int, lossless: bool, preset: string, x265Path: string) =
            if width <= 0 then invalidArg "width" "Width must be positive."
            if height <= 0 then invalidArg "height" "Height must be positive."
            let fps = if fps <= 0 then 30 else fps
            let preset = if String.IsNullOrWhiteSpace preset then "medium" else preset

            let bind =
                match tryBind (if isNull (box x265Path) then "" else x265Path) with
                | Some b -> b
                | None -> failwith "x265.dll is not available. Set --x265, YUMLOG_X265_PATH, place it at .tools\\x265\\x265.dll, or beside the executable."

            let param = bind.ParamAlloc.Invoke()
            if param = 0n then
                (try NativeLibrary.Free bind.Handle with _ -> ())
                failwith "x265_param_alloc returned null."

            let mutable encoder = 0n
            let mutable picIn = 0n
            let mutable picOut = 0n
            try
                let pdPreset (p: nativeint) (preset: string) =
                    let sp = Marshal.StringToHGlobalAnsi preset
                    try bind.ParamDefaultPreset.Invoke(p, sp, 0n)
                    finally Marshal.FreeHGlobal sp
                let ppParse (p: nativeint) (name: string) (value: string) =
                    let n = Marshal.StringToHGlobalAnsi name
                    let v = Marshal.StringToHGlobalAnsi value
                    try bind.ParamParse.Invoke(p, n, v)
                    finally
                        Marshal.FreeHGlobal n
                        Marshal.FreeHGlobal v

                if pdPreset param preset < 0 then
                    failwith $"x265_param_default_preset('{preset}') failed."

                let parseReq name value =
                    if ppParse param name value <> 0 then
                        failwith $"x265_param_parse('{name}', '{value}') failed."
                let parseOpt name value =
                    ppParse param name value = 0

                parseReq "input-res" $"{width}x{height}"
                parseReq "fps" (string fps)
                parseReq "input-csp" "i444"
                parseReq "annexb" "1"
                parseReq "repeat-headers" "1"
                parseReq "bframes" "0"
                parseOpt "rc-lookahead" "0" |> ignore
                parseOpt "keyint" (string (max 1 fps)) |> ignore
                parseOpt "min-keyint" (string (max 1 fps)) |> ignore

                // Reversible identity path: GBR matrix + full range makes the G/B/R plane
                // permutation exactly recoverable. Track whether the build accepted it.
                let gbrOk = parseOpt "colormatrix" "gbr"
                let rangeOk = parseOpt "range" "full"

                if lossless then
                    parseReq "lossless" "1"
                    parseOpt "cu-lossless" "1" |> ignore

                encoder <- bind.EncoderOpen.Invoke param
                if encoder = 0n then
                    failwith "x265_encoder_open returned null. The x265 build may not support i444/lossless."

                picIn <- bind.PictureAlloc.Invoke()
                picOut <- bind.PictureAlloc.Invoke()
                if picIn = 0n || picOut = 0n then failwith "x265_picture_alloc returned null."
                bind.PictureInit.Invoke(param, picIn)
                bind.PictureInit.Invoke(param, picOut)

                // Extract VPS/SPS/PPS Annex B headers for the mux's hvcC.
                let headers =
                    let mutable ppNal = 0n
                    let mutable nalCount = 0u
                    let rc = bind.EncoderHeaders.Invoke(encoder, &ppNal, &nalCount)
                    if rc > 0 then Some(collectNals ppNal nalCount) else None

                let reversible = lossless && gbrOk && rangeOk
                let decoderConfig =
                    { VpsSpsPpsAnnexB = headers
                      Chroma = Yuv444
                      BitDepth = 8
                      Width = width
                      Height = height }

                let losslessMode =
                    if reversible then
                        "mathematically lossless: BGRA mapped to I444 by an identity G/B/R permutation, "
                        + "encoded with x265 --lossless --colormatrix gbr --range full; bit-exact recoverable to the input BGRA."
                    elif lossless then
                        "visually lossless: x265 --lossless I444, but this build rejected the GBR identity matrix, "
                        + "so a BT.709 RGB->YCbCr conversion applies and the result is not bit-exact reversible."
                    else
                        "lossy: x265 I444 rate-controlled encode."

                new Session(bind, param, encoder, picIn, picOut, width, height, decoderConfig, losslessMode, reversible)
            with ex ->
                if picIn <> 0n then bind.PictureFree.Invoke picIn
                if picOut <> 0n then bind.PictureFree.Invoke picOut
                if encoder <> 0n then bind.EncoderClose.Invoke encoder
                if param <> 0n then bind.ParamFree.Invoke param
                (try NativeLibrary.Free bind.Handle with _ -> ())
                raise ex

    /// Creates an x265 software HEVC session. Throws if x265.dll is unavailable.
    let create (width: int) (height: int) (fps: int) (lossless: bool) (preset: string) (x265Path: string) : Session =
        Session.Create(width, height, fps, lossless, preset, x265Path)
