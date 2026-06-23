Agent is idle (waiting for messages). agent_id: wgc-capture-1, agent_type: dayour-swe, status: idle, description: WGC D3D11 capture pipeline, elapsed: 294s, total_turns: 1

[Turn 0]
**Summary**: Below is a complete `WgcCapture.fs` implementation that follows the existing raw WinRT COM/vtable idiom from `RawWinRt.fs`: `RoInitialize`, `RoGetActivationFactory`, HSTRING activation, manual COM slots, `Marshal.GetDelegateForFunctionPointer`, and `StdCall` delegates. It creates a D3D11 device, wraps it as a WinRT `IDirect3DDevice`, creates a primary-monitor `GraphicsCaptureItem`, starts a `Direct3D11CaptureFramePool` session, and queues raw `ID3D11Texture2D*` frames with `SystemRelativeTime` timestamps.

**Findings**:
1. `[HIGH]` The downstream NVENC encoder should share the exact same `ID3D11Device*` exposed by `WgcSession.D3D11Device`; this avoids cross-device copies and avoids requiring WGC frame textures to be opened by shared NT handle.
2. `[MEDIUM]` The returned `CapturedFrame.Texture` is an AddRef-owned `ID3D11Texture2D*`; the consumer must call `WgcCapture.releaseFrameTexture` exactly once per queued frame.
3. `[MEDIUM]` Fixed duration stops frame production and completes the queue; the owner should still call `stop`/`Dispose` after the encoder drains the queue so the D3D device remains valid while NVENC consumes frames.
4. `[LOW]` The capture border can be disabled through `IGraphicsCaptureSession3.IsBorderRequired <- false` on supported Windows 11 builds; older builds will ignore/fail that optional QI/setter.

**Recommendations**:
1. Owner: Recording/NVENC agent. Wire `WgcSession.D3D11Device` into the NVENC initialization path and encode each `CapturedFrame.Texture` directly on that same device.
2. Owner: Recording/NVENC agent. Add a `try/finally` around each encoded frame to call `WgcCapture.releaseFrameTexture frame.Texture`.
3. Owner: CLI/config agent. Add the `RecordConfig` fields shown below and update `Config.fs` defaults/merge logic accordingly.
4. Owner: Project file owner. Move `RawWinRt.fs` earlier in compile order and place `WgcCapture.fs` immediately after it and before `Recording.fs`.

## Code Snippets

### Complete `WgcCapture.fs`

```fsharp
namespace Yumlog.Native

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading

module WgcCapture =
    [<Literal>]
    let private S_OK = 0

    [<Literal>]
    let private D3D11_SDK_VERSION = 7u

    [<Literal>]
    let private DirectXPixelFormat_B8G8R8A8UIntNormalized = 87

    [<Literal>]
    let private MONITOR_DEFAULTTOPRIMARY = 1u

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type SizeInt32 =
        val Width: int
        val Height: int

        new(width: int, height: int) =
            { Width = width
              Height = height }

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type WinRtTimeSpan =
        val Duration: int64

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type RawPoint =
        val X: int
        val Y: int

        new(x: int, y: int) = { X = x; Y = y }

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type RawRect =
        val Left: int
        val Top: int
        val Right: int
        val Bottom: int

    [<Flags>]
    type D3D11CreateDeviceFlags =
        | None = 0u
        | SingleThreaded = 0x1u
        | Debug = 0x2u
        | SwitchToRef = 0x4u
        | PreventInternalThreadingOptimizations = 0x8u
        | BgraSupport = 0x20u
        | Debuggable = 0x40u
        | PreventAlteringLayerSettingsFromRegistry = 0x80u
        | DisableGpuTimeout = 0x100u
        | VideoSupport = 0x800u

    type D3DDriverType =
        | Unknown = 0
        | Hardware = 1
        | Reference = 2
        | Null = 3
        | Software = 4
        | Warp = 5

    [<CLIMutable>]
    type CapturedFrame =
        { Texture: nativeint
          SharedHandle: nativeint
          Width: int
          Height: int
          QpcTime: int64 }

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type AddRefFn = delegate of nativeint -> uint32

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type CloseFn = delegate of nativeint -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type GraphicsCaptureItemInteropCreateForMonitorFn =
        delegate of nativeint * nativeint * Guid byref * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type GraphicsCaptureItemGetSizeFn =
        delegate of nativeint * SizeInt32 byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type Direct3D11CaptureFramePoolCreateFn =
        delegate of nativeint * nativeint * int * int * SizeInt32 * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type Direct3D11CaptureFramePoolCreateFreeThreadedFn =
        delegate of nativeint * nativeint * int * int * SizeInt32 * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type Direct3D11CaptureFramePoolTryGetNextFrameFn =
        delegate of nativeint * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type Direct3D11CaptureFramePoolCreateCaptureSessionFn =
        delegate of nativeint * nativeint * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type Direct3D11CaptureFrameGetSurfaceFn =
        delegate of nativeint * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type Direct3D11CaptureFrameGetSystemRelativeTimeFn =
        delegate of nativeint * WinRtTimeSpan byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type Direct3D11CaptureFrameGetContentSizeFn =
        delegate of nativeint * SizeInt32 byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type GraphicsCaptureSessionStartCaptureFn =
        delegate of nativeint -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type GraphicsCaptureSessionPutBooleanFn =
        delegate of nativeint * byte -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type Direct3DDxgiInterfaceAccessGetInterfaceFn =
        delegate of nativeint * Guid byref * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type MonitorEnumProc =
        delegate of nativeint * nativeint * nativeint * nativeint -> bool

    [<DllImport("d3d11.dll", PreserveSig = true)>]
    extern int D3D11CreateDevice(
        nativeint pAdapter,
        D3DDriverType driverType,
        nativeint software,
        D3D11CreateDeviceFlags flags,
        [<MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)>] int[] featureLevels,
        uint32 featureLevelsCount,
        uint32 sdkVersion,
        nativeint& device,
        int& featureLevel,
        nativeint& immediateContext)

    [<DllImport("d3d11.dll", PreserveSig = true)>]
    extern int CreateDirect3D11DeviceFromDXGIDevice(
        nativeint dxgiDevice,
        nativeint& graphicsDevice)

    [<DllImport("user32.dll", PreserveSig = true)>]
    [<return: MarshalAs(UnmanagedType.Bool)>]
    extern bool EnumDisplayMonitors(
        nativeint hdc,
        nativeint lprcClip,
        MonitorEnumProc lpfnEnum,
        nativeint dwData)

    [<DllImport("user32.dll", PreserveSig = true)>]
    extern nativeint MonitorFromPoint(
        RawPoint pt,
        uint32 dwFlags)

    let private iidIGraphicsCaptureItemInterop =
        Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")

    let private iidIGraphicsCaptureItem =
        Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760")

    let private iidIDirect3D11CaptureFramePoolStatics =
        Guid("7784056A-67AA-4D53-AE54-1088D5A8CA21")

    let private iidIDirect3D11CaptureFramePoolStatics2 =
        Guid("589B103F-6BBC-5DF5-A991-02E28B3B66D5")

    let private iidIGraphicsCaptureSession2 =
        Guid("2C39AE40-7D2E-5044-804E-8B6799D4CF9E")

    let private iidIGraphicsCaptureSession3 =
        Guid("F2CDD966-22AE-5EA1-9596-3A289344C3BE")

    let private iidIDXGIDevice =
        Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C")

    let private iidID3D11Texture2D =
        Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C")

    let private iidIDirect3DDxgiInterfaceAccess =
        Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")

    let private iidIClosable =
        Guid("30D5A829-7FA4-4026-83BB-D75BAE4EA99E")

    let private graphicsCaptureItemClassName =
        "Windows.Graphics.Capture.GraphicsCaptureItem"

    let private direct3D11CaptureFramePoolClassName =
        "Windows.Graphics.Capture.Direct3D11CaptureFramePool"

    let private hresult hr =
        RawWinRt.hresult hr

    let private checkHr operation hr =
        if hr <> S_OK then
            failwith $"{operation} failed: {hresult hr}"

    let private slot (handle: nativeint) (slotIndex: int) =
        let vtable = Marshal.ReadIntPtr(handle)
        Marshal.ReadIntPtr(vtable, slotIndex * IntPtr.Size)

    let private addRef (ptr: nativeint) =
        if ptr <> nativeint 0 then
            let fn = Marshal.GetDelegateForFunctionPointer<AddRefFn>(slot ptr 1)
            fn.Invoke(ptr) |> ignore

    let private releaseFrameTexturePointer (ptr: nativeint) =
        if ptr <> nativeint 0 then
            let com = RawWinRt.ComPtr(ptr)
            com.Release()

    let releaseFrameTexture (texture: nativeint) =
        releaseFrameTexturePointer texture

    let private closeWinRtObject (ptr: nativeint) =
        if ptr <> nativeint 0 then
            let owner = RawWinRt.ComPtr(ptr)
            match owner.QueryInterface(iidIClosable) with
            | Ok closable ->
                try
                    let close =
                        Marshal.GetDelegateForFunctionPointer<CloseFn>(closable.Slot(6))

                    close.Invoke(closable.Handle) |> ignore
                finally
                    (closable :> IDisposable).Dispose()
            | Error _ -> ()

    let private disposeCom (ptr: RawWinRt.ComPtr) =
        if not (isNull (box ptr)) then
            (ptr :> IDisposable).Dispose()

    let private monitorFromIndex monitorIndex =
        if monitorIndex < 0 then
            invalidArg "monitorIndex" "monitorIndex must be >= 0."

        if monitorIndex = 0 then
            let primary = MonitorFromPoint(RawPoint(0, 0), MONITOR_DEFAULTTOPRIMARY)
            if primary = nativeint 0 then
                failwith "MonitorFromPoint failed for the primary monitor."
            primary
        else
            let monitors = ResizeArray<nativeint>()

            let callback =
                MonitorEnumProc(fun hMonitor _ _ _ ->
                    monitors.Add(hMonitor)
                    true)

            if not (EnumDisplayMonitors(nativeint 0, nativeint 0, callback, nativeint 0)) then
                failwith "EnumDisplayMonitors failed."

            if monitorIndex >= monitors.Count then
                failwith $"Monitor index {monitorIndex} was not found. Discovered {monitors.Count} monitor(s)."

            monitors[monitorIndex]

    let private createD3D11Device () =
        let featureLevels =
            [| 0x0000B100
               0x0000B000
               0x0000A100
               0x0000A000 |]

        let flags = D3D11CreateDeviceFlags.BgraSupport

        let tryCreate driverType =
            let mutable device = nativeint 0
            let mutable immediateContext = nativeint 0
            let mutable selectedFeatureLevel = 0

            let hr =
                D3D11CreateDevice(
                    nativeint 0,
                    driverType,
                    nativeint 0,
                    flags,
                    featureLevels,
                    uint32 featureLevels.Length,
                    D3D11_SDK_VERSION,
                    &device,
                    &selectedFeatureLevel,
                    &immediateContext)

            if hr = S_OK && device <> nativeint 0 then
                Ok(RawWinRt.ComPtr(device), RawWinRt.ComPtr(immediateContext), selectedFeatureLevel)
            else
                if immediateContext <> nativeint 0 then
                    RawWinRt.ComPtr(immediateContext).Release()

                if device <> nativeint 0 then
                    RawWinRt.ComPtr(device).Release()

                Error hr

        match tryCreate D3DDriverType.Hardware with
        | Ok result -> result
        | Error hardwareHr ->
            match tryCreate D3DDriverType.Warp with
            | Ok result -> result
            | Error warpHr ->
                failwith $"D3D11CreateDevice failed. Hardware={hresult hardwareHr}; WARP={hresult warpHr}"

    let private createWinRtDirect3DDevice (d3d11Device: RawWinRt.ComPtr) =
        match d3d11Device.QueryInterface(iidIDXGIDevice) with
        | Error hr ->
            failwith $"ID3D11Device.QueryInterface(IDXGIDevice) failed: {hresult hr}"
        | Ok dxgiDevice ->
            use dxgiDevice = dxgiDevice

            let mutable winRtDevice = nativeint 0
            let hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Handle, &winRtDevice)
            checkHr "CreateDirect3D11DeviceFromDXGIDevice" hr

            if winRtDevice = nativeint 0 then
                failwith "CreateDirect3D11DeviceFromDXGIDevice returned null."

            RawWinRt.ComPtr(winRtDevice)

    let private createGraphicsCaptureItemForMonitor monitorIndex =
        let hMonitor = monitorFromIndex monitorIndex

        match RawWinRt.activationFactory graphicsCaptureItemClassName iidIGraphicsCaptureItemInterop with
        | Error hr ->
            failwith $"RoGetActivationFactory({graphicsCaptureItemClassName}, IGraphicsCaptureItemInterop) failed: {hresult hr}"
        | Ok interopFactory ->
            use interopFactory = interopFactory

            let createForMonitor =
                Marshal.GetDelegateForFunctionPointer<GraphicsCaptureItemInteropCreateForMonitorFn>(
                    interopFactory.Slot(4))

            let mutable itemIid = iidIGraphicsCaptureItem
            let mutable item = nativeint 0
            let hr = createForMonitor.Invoke(interopFactory.Handle, hMonitor, &itemIid, &item)
            checkHr "IGraphicsCaptureItemInterop.CreateForMonitor" hr

            if item = nativeint 0 then
                failwith "IGraphicsCaptureItemInterop.CreateForMonitor returned null."

            RawWinRt.ComPtr(item)

    let private getCaptureItemSize (item: RawWinRt.ComPtr) =
        let getSize =
            Marshal.GetDelegateForFunctionPointer<GraphicsCaptureItemGetSizeFn>(item.Slot(7))

        let mutable size = SizeInt32(0, 0)
        let hr = getSize.Invoke(item.Handle, &size)
        checkHr "IGraphicsCaptureItem.get_Size" hr

        if size.Width <= 0 || size.Height <= 0 then
            failwith $"GraphicsCaptureItem returned an invalid size {size.Width}x{size.Height}."

        size

    let private createFramePool (winRtDevice: RawWinRt.ComPtr) (size: SizeInt32) =
        match RawWinRt.activationFactory direct3D11CaptureFramePoolClassName iidIDirect3D11CaptureFramePoolStatics2 with
        | Ok statics2 ->
            use statics2 = statics2

            let createFreeThreaded =
                Marshal.GetDelegateForFunctionPointer<Direct3D11CaptureFramePoolCreateFreeThreadedFn>(
                    statics2.Slot(6))

            let mutable framePool = nativeint 0

            let hr =
                createFreeThreaded.Invoke(
                    statics2.Handle,
                    winRtDevice.Handle,
                    DirectXPixelFormat_B8G8R8A8UIntNormalized,
                    2,
                    size,
                    &framePool)

            checkHr "IDirect3D11CaptureFramePoolStatics2.CreateFreeThreaded" hr

            if framePool = nativeint 0 then
                failwith "CreateFreeThreaded returned null."

            RawWinRt.ComPtr(framePool)
        | Error _ ->
            match RawWinRt.activationFactory direct3D11CaptureFramePoolClassName iidIDirect3D11CaptureFramePoolStatics with
            | Error hr ->
                failwith $"RoGetActivationFactory({direct3D11CaptureFramePoolClassName}) failed: {hresult hr}"
            | Ok statics ->
                use statics = statics

                let create =
                    Marshal.GetDelegateForFunctionPointer<Direct3D11CaptureFramePoolCreateFn>(
                        statics.Slot(6))

                let mutable framePool = nativeint 0

                let hr =
                    create.Invoke(
                        statics.Handle,
                        winRtDevice.Handle,
                        DirectXPixelFormat_B8G8R8A8UIntNormalized,
                        2,
                        size,
                        &framePool)

                checkHr "IDirect3D11CaptureFramePoolStatics.Create" hr

                if framePool = nativeint 0 then
                    failwith "Create returned null."

                RawWinRt.ComPtr(framePool)

    let private createCaptureSession (framePool: RawWinRt.ComPtr) (item: RawWinRt.ComPtr) =
        let createSession =
            Marshal.GetDelegateForFunctionPointer<Direct3D11CaptureFramePoolCreateCaptureSessionFn>(
                framePool.Slot(10))

        let mutable session = nativeint 0
        let hr = createSession.Invoke(framePool.Handle, item.Handle, &session)
        checkHr "IDirect3D11CaptureFramePool.CreateCaptureSession" hr

        if session = nativeint 0 then
            failwith "CreateCaptureSession returned null."

        RawWinRt.ComPtr(session)

    let private trySetCursorCaptureEnabled (session: RawWinRt.ComPtr) enabled =
        match session.QueryInterface(iidIGraphicsCaptureSession2) with
        | Error _ -> ()
        | Ok session2 ->
            try
                let putCursor =
                    Marshal.GetDelegateForFunctionPointer<GraphicsCaptureSessionPutBooleanFn>(
                        session2.Slot(7))

                putCursor.Invoke(session2.Handle, if enabled then 1uy else 0uy) |> ignore
            finally
                (session2 :> IDisposable).Dispose()

    let private trySetBorderRequired (session: RawWinRt.ComPtr) required =
        match session.QueryInterface(iidIGraphicsCaptureSession3) with
        | Error _ -> ()
        | Ok session3 ->
            try
                let putBorder =
                    Marshal.GetDelegateForFunctionPointer<GraphicsCaptureSessionPutBooleanFn>(
                        session3.Slot(7))

                putBorder.Invoke(session3.Handle, if required then 1uy else 0uy) |> ignore
            finally
                (session3 :> IDisposable).Dispose()

    let private startCapture (session: RawWinRt.ComPtr) =
        let startCapture =
            Marshal.GetDelegateForFunctionPointer<GraphicsCaptureSessionStartCaptureFn>(
                session.Slot(6))

        let hr = startCapture.Invoke(session.Handle)
        checkHr "IGraphicsCaptureSession.StartCapture" hr

    let private tryGetNextCapturedFrame (framePool: RawWinRt.ComPtr) =
        let tryGetNextFrame =
            Marshal.GetDelegateForFunctionPointer<Direct3D11CaptureFramePoolTryGetNextFrameFn>(
                framePool.Slot(7))

        let mutable framePtr = nativeint 0
        let hr = tryGetNextFrame.Invoke(framePool.Handle, &framePtr)

        if hr <> S_OK || framePtr = nativeint 0 then
            None
        else
            use frame = new RawWinRt.ComPtr(framePtr)

            let getSurface =
                Marshal.GetDelegateForFunctionPointer<Direct3D11CaptureFrameGetSurfaceFn>(
                    frame.Slot(6))

            let getSystemRelativeTime =
                Marshal.GetDelegateForFunctionPointer<Direct3D11CaptureFrameGetSystemRelativeTimeFn>(
                    frame.Slot(7))

            let getContentSize =
                Marshal.GetDelegateForFunctionPointer<Direct3D11CaptureFrameGetContentSizeFn>(
                    frame.Slot(8))

            let mutable surfacePtr = nativeint 0
            let surfaceHr = getSurface.Invoke(frame.Handle, &surfacePtr)

            if surfaceHr <> S_OK || surfacePtr = nativeint 0 then
                None
            else
                use surface = new RawWinRt.ComPtr(surfacePtr)

                let mutable timestamp = Unchecked.defaultof<WinRtTimeSpan>
                let timeHr = getSystemRelativeTime.Invoke(frame.Handle, &timestamp)

                let mutable contentSize = SizeInt32(0, 0)
                let sizeHr = getContentSize.Invoke(frame.Handle, &contentSize)

                if timeHr <> S_OK || sizeHr <> S_OK then
                    None
                else
                    match surface.QueryInterface(iidIDirect3DDxgiInterfaceAccess) with
                    | Error _ -> None
                    | Ok dxgiAccess ->
                        use dxgiAccess = dxgiAccess

                        let getInterface =
                            Marshal.GetDelegateForFunctionPointer<Direct3DDxgiInterfaceAccessGetInterfaceFn>(
                                dxgiAccess.Slot(3))

                        let mutable textureIid = iidID3D11Texture2D
                        let mutable texture = nativeint 0
                        let textureHr = getInterface.Invoke(dxgiAccess.Handle, &textureIid, &texture)

                        if textureHr <> S_OK || texture = nativeint 0 then
                            None
                        else
                            Some
                                { Texture = texture
                                  SharedHandle = nativeint 0
                                  Width = contentSize.Width
                                  Height = contentSize.Height
                                  QpcTime = timestamp.Duration }

    type WgcSession internal
        (
            roScope: RawWinRt.RoScope,
            d3d11Device: RawWinRt.ComPtr,
            d3d11ImmediateContext: RawWinRt.ComPtr,
            winRtDevice: RawWinRt.ComPtr,
            item: RawWinRt.ComPtr,
            framePool: RawWinRt.ComPtr,
            captureSession: RawWinRt.ComPtr,
            width: int,
            height: int,
            fps: int,
            durationSec: int
        ) as this =

        let boundedCapacity = max 2 (fps * 2)
        let frames = new BlockingCollection<CapturedFrame>(ConcurrentQueue<CapturedFrame>(), boundedCapacity)
        let cancellation = new CancellationTokenSource()
        let mutable stopped = 0

        let worker =
            Thread(
                ThreadStart(fun () ->
                    this.CaptureLoop()))

        do
            worker.IsBackground <- true
            worker.Name <- "Yumlog.WgcCapture"
            worker.Start()

        member _.Frames = frames

        member _.D3D11Device = d3d11Device.Handle

        member _.D3D11ImmediateContext = d3d11ImmediateContext.Handle

        member _.WinRtDirect3DDevice = winRtDevice.Handle

        member _.Width = width

        member _.Height = height

        member _.Fps = fps

        member _.DurationSec = durationSec

        member private _.CaptureLoop() =
            let intervalMs =
                if fps > 0 then
                    max 1 (1000 / fps)
                else
                    16

            let deadlineUtc =
                if durationSec > 0 then
                    Some(DateTimeOffset.UtcNow.AddSeconds(float durationSec))
                else
                    None

            let withinDuration () =
                match deadlineUtc with
                | None -> true
                | Some deadline -> DateTimeOffset.UtcNow < deadline

            try
                while not cancellation.IsCancellationRequested && withinDuration () do
                    match tryGetNextCapturedFrame framePool with
                    | Some frame ->
                        let accepted =
                            try
                                frames.TryAdd(frame, 0, cancellation.Token)
                            with
                            | :? OperationCanceledException -> false
                            | :? InvalidOperationException -> false

                        if not accepted then
                            releaseFrameTexturePointer frame.Texture
                    | None -> ()

                    if not cancellation.IsCancellationRequested && withinDuration () then
                        Thread.Sleep(intervalMs)
            finally
                if not frames.IsAddingCompleted then
                    try
                        frames.CompleteAdding()
                    with
                    | :? InvalidOperationException -> ()

        member private _.StopInternal(joinWorker: bool) =
            if Interlocked.CompareExchange(&stopped, 1, 0) = 0 then
                cancellation.Cancel()

                if joinWorker && worker.IsAlive then
                    worker.Join(5000) |> ignore

                if not frames.IsAddingCompleted then
                    try
                        frames.CompleteAdding()
                    with
                    | :? InvalidOperationException -> ()

                closeWinRtObject captureSession.Handle
                closeWinRtObject framePool.Handle
                closeWinRtObject winRtDevice.Handle

                disposeCom captureSession
                disposeCom framePool
                disposeCom item
                disposeCom winRtDevice
                disposeCom d3d11ImmediateContext
                disposeCom d3d11Device

                cancellation.Dispose()
                (roScope :> IDisposable).Dispose()

        member this.Stop() =
            this.StopInternal(true)

        interface IDisposable with
            member this.Dispose() =
                this.Stop()

    let startForDuration monitorIndex fps durationSec =
        if fps <= 0 then
            invalidArg "fps" "fps must be > 0."

        if durationSec < 0 then
            invalidArg "durationSec" "durationSec must be >= 0."

        match RawWinRt.RoScope.InitializeMta() with
        | Error hr ->
            failwith $"RoInitialize failed: {hresult hr}"
        | Ok roScope ->
            try
                let d3d11Device, d3d11ImmediateContext, _featureLevel = createD3D11Device()
                let winRtDevice = createWinRtDirect3DDevice d3d11Device
                let item = createGraphicsCaptureItemForMonitor monitorIndex
                let size = getCaptureItemSize item
                let framePool = createFramePool winRtDevice size
                let captureSession = createCaptureSession framePool item

                trySetCursorCaptureEnabled captureSession true

                // Supported on newer Windows 11 builds. Older builds simply ignore/fail this optional interface path.
                trySetBorderRequired captureSession false

                startCapture captureSession

                new WgcSession(
                    roScope,
                    d3d11Device,
                    d3d11ImmediateContext,
                    winRtDevice,
                    item,
                    framePool,
                    captureSession,
                    size.Width,
                    size.Height,
                    fps,
                    durationSec)
            with
            | ex ->
                (roScope :> IDisposable).Dispose()
                reraise()

    let start monitorIndex fps =
        startForDuration monitorIndex fps 0

    let stop (session: WgcSession) =
        if not (isNull (box session)) then
            session.Stop()
```

### Exact P/Invoke signatures added

These are the new native imports in `WgcCapture.fs`:

```fsharp
[<DllImport("d3d11.dll", PreserveSig = true)>]
extern int D3D11CreateDevice(
    nativeint pAdapter,
    D3DDriverType driverType,
    nativeint software,
    D3D11CreateDeviceFlags flags,
    [<MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)>] int[] featureLevels,
    uint32 featureLevelsCount,
    uint32 sdkVersion,
    nativeint& device,
    int& featureLevel,
    nativeint& immediateContext)

[<DllImport("d3d11.dll", PreserveSig = true)>]
extern int CreateDirect3D11DeviceFromDXGIDevice(
    nativeint dxgiDevice,
    nativeint& graphicsDevice)

[<DllImport("user32.dll", PreserveSig = true)>]
[<return: MarshalAs(UnmanagedType.Bool)>]
extern bool EnumDisplayMonitors(
    nativeint hdc,
    nativeint lprcClip,
    MonitorEnumProc lpfnEnum,
    nativeint dwData)

[<DllImport("user32.dll", PreserveSig = true)>]
extern nativeint MonitorFromPoint(
    RawPoint pt,
    uint32 dwFlags)
```

No `dxgi.dll` function import is required for this component. DXGI is used through COM interfaces returned from D3D11:

```fsharp
let private iidIDXGIDevice =
    Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C")
```

The WinRT activation imports are reused from `RawWinRt.fs`, not duplicated:

```fsharp
RoInitialize
RoUninitialize
WindowsCreateString
WindowsDeleteString
WindowsGetStringRawBuffer
RoGetActivationFactory
```

### COM/WinRT GUIDs and vtable slots used

```fsharp
IGraphicsCaptureItemInterop =
    3628E81B-3CAC-4C60-B7F4-23CE0E0C3356

IGraphicsCaptureItem =
    79C3F95B-31F7-4EC2-A464-632EF5D30760

IDirect3D11CaptureFramePoolStatics =
    7784056A-67AA-4D53-AE54-1088D5A8CA21

IDirect3D11CaptureFramePoolStatics2 =
    589B103F-6BBC-5DF5-A991-02E28B3B66D5

IGraphicsCaptureSession2 =
    2C39AE40-7D2E-5044-804E-8B6799D4CF9E

IGraphicsCaptureSession3 =
    F2CDD966-22AE-5EA1-9596-3A289344C3BE

IDXGIDevice =
    54EC77FA-1377-44E6-8C32-88FD5F44C84C

ID3D11Texture2D =
    6F15AAF2-D208-4E89-9AB4-489535D34F9C

IDirect3DDxgiInterfaceAccess =
    A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1

IClosable =
    30D5A829-7FA4-4026-83BB-D75BAE4EA99E
```

Vtable slots:

```text
IUnknown:
  0 QueryInterface
  1 AddRef
  2 Release

IInspectable:
  3 GetIids
  4 GetRuntimeClassName
  5 GetTrustLevel

IGraphicsCaptureItemInterop:
  3 CreateForWindow
  4 CreateForMonitor

IGraphicsCaptureItem:
  6 get_DisplayName
  7 get_Size
  8 add_Closed
  9 remove_Closed

IDirect3D11CaptureFramePoolStatics:
  6 Create

IDirect3D11CaptureFramePoolStatics2:
  6 CreateFreeThreaded

IDirect3D11CaptureFramePool:
  6 Recreate
  7 TryGetNextFrame
  8 add_FrameArrived
  9 remove_FrameArrived
  10 CreateCaptureSession
  11 get_DispatcherQueue

IDirect3D11CaptureFrame:
  6 get_Surface
  7 get_SystemRelativeTime
  8 get_ContentSize

IGraphicsCaptureSession:
  6 StartCapture

IGraphicsCaptureSession2:
  6 get_IsCursorCaptureEnabled
  7 put_IsCursorCaptureEnabled

IGraphicsCaptureSession3:
  6 get_IsBorderRequired
  7 put_IsBorderRequired

IDirect3DDxgiInterfaceAccess:
  3 GetInterface

IClosable:
  6 Close
```

### Required `Domain.fs` additions

Minimum additions to make WGC/NVENC selectable from config while keeping existing fields:

```fsharp
[<CLIMutable>]
type RecordConfig =
    { OutFile: string
      Fps: int
      DurationSec: int
      FFmpegPath: string
      Encoder: string
      Preset: string
      CaptureBackend: string
      MonitorIndex: int
      Container: string
      TimedMetadataOut: string
      DisableCaptureBorder: bool }
```

Suggested defaults in `Config.fs` for the existing `defaultConfig` path:

```fsharp
Record =
    { OutFile = "recording.mp4"
      Fps = 30
      DurationSec = 10
      FFmpegPath = ""
      Encoder = "hevc_nvenc_lossless"
      Preset = "lossless"
      CaptureBackend = "wgc"
      MonitorIndex = 0
      Container = "mp4"
      TimedMetadataOut = ""
      DisableCaptureBorder = true }
```

If you need strict backward compatibility with existing JSON config files, keep `CaptureBackend = "ffmpeg"` as the default until the new recorder path is fully wired.

### Required `.fsproj` compile order

Because `WgcCapture.fs` depends on `RawWinRt.fs`, and the new `Recording.fs` will likely depend on `WgcCapture.fs`, move `RuntimeIdentity.fs`, `WinAppRuntime.fs`, and `RawWinRt.fs` earlier and insert `WgcCapture.fs` immediately after `RawWinRt.fs`:

```xml
<ItemGroup>
  <Compile Include="Domain.fs" />
  <Compile Include="Json.fs" />
  <Compile Include="Config.fs" />
  <Compile Include="Capture.fs" />
  <Compile Include="RuntimeIdentity.fs" />
  <Compile Include="WinAppRuntime.fs" />
  <Compile Include="RawWinRt.fs" />
  <Compile Include="WgcCapture.fs" />
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

### Known risks and assumptions

1. WGC OS requirement: Windows Graphics Capture requires Windows 10 1903+ for the stable desktop capture path used here. Older machines will fail at activation or session creation.
2. Primary monitor: `start 0 fps` uses `MonitorFromPoint(0,0, MONITOR_DEFAULTTOPRIMARY)` for the primary monitor. Nonzero `monitorIndex` uses `EnumDisplayMonitors` ordering, which is stable enough for local capture but should eventually be paired with display identity metadata if you expose monitor selection in UI.
3. Yellow capture border: WGC shows a capture border by default. This implementation attempts `IGraphicsCaptureSession3.put_IsBorderRequired(false)`; this only works on supported newer Windows 11 builds and may be ignored on older systems.
4. Texture lifetime: every queued `CapturedFrame.Texture` is an AddRef-owned `ID3D11Texture2D*`. The encoder must release it by calling `WgcCapture.releaseFrameTexture`.
5. NVENC handoff: preferred path is same-device handoff. Initialize NVENC with `session.D3D11Device` and register each `CapturedFrame.Texture`. Avoid shared NT handles unless another process or another D3D device is unavoidable.
6. Shared handle: `CapturedFrame.SharedHandle` is currently `nativeint 0` intentionally. Creating a shared NT handle from WGC-provided textures is not always supported and can introduce unnecessary copies/synchronization. If required later, add an explicit copy into a `D3D11_RESOURCE_MISC_SHARED_NTHANDLE` texture owned by the session device, then call `IDXGIResource1.CreateSharedHandle`.
7. Duration behavior: `startForDuration monitorIndex fps durationSec` stops producing frames and completes `Frames` after the duration. Call `stop session` after the encoder drains the queue to close WGC, release D3D11, and release WinRT objects cleanly.