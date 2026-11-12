namespace Yumlog.Native

open System
open System.Collections.Concurrent
open System.Runtime.InteropServices
open System.Threading

/// Windows.Graphics.Capture (WGC) desktop capture built on the raw-WinRT COM/vtable
/// idiom already used by RawWinRt.fs (RoInitialize, RoGetActivationFactory, HSTRING
/// activation, manual vtable slots, StdCall delegates, IInspectable helpers).
///
/// FrameArrived vs polling (red-team HIGH #3): a proper FrameArrived subscription
/// requires materialising an ITypedEventHandler COM object with a hand-built vtable
/// and the parameterised delegate IID -- neither of which RawWinRt.fs provides. As the
/// spec allows, we therefore create the frame pool with CreateFreeThreaded and run a
/// single producer thread that drains the pool. The producer NEVER busy-spins: when no
/// frame is ready it blocks on the cancellation token's wait handle for one frame
/// interval; the consumer (TryGetNextFrame) blocks on BlockingCollection.TryTake with an
/// infinite timeout + cancellation, so neither side spins.
module WgcCapture =

    [<Literal>]
    let private S_OK = 0

    [<Literal>]
    let private D3D11_SDK_VERSION = 7u

    [<Literal>]
    let private DirectXPixelFormat_B8G8R8A8UIntNormalized = 87

    [<Literal>]
    let private MONITOR_DEFAULTTOPRIMARY = 1u

    /// Number of frame-pool buffers. 3 reduces drops relative to the 2-buffer minimum.
    [<Literal>]
    let private NumBuffers = 3

    [<Literal>]
    let private D3D11_USAGE_STAGING = 3u

    [<Literal>]
    let private D3D11_CPU_ACCESS_READ = 0x20000u

    [<Literal>]
    let private D3D11_MAP_READ = 1u

    [<Literal>]
    let private VENDOR_NVIDIA = 0x10DEu

    // ---------------------------------------------------------------------------------
    // Public contracts
    // ---------------------------------------------------------------------------------

    type CaptureOptions =
        { MonitorIndex: int
          Fps: int
          DisableCaptureBorder: bool
          /// true when a software encoder (x265) is the backend and needs CPU BGRA.
          NeedCpuReadback: bool
          /// When Some, select that DXGI adapter by LUID.
          PreferredAdapterLuid: int64 option }

    type IWgcSession =
        inherit System.IDisposable
        abstract member Width: int
        abstract member Height: int
        abstract member D3D11Device: nativeint
        abstract member AdapterDescription: string
        abstract member AdapterLuid: int64
        /// Blocks until the next frame is available or the token is cancelled.
        /// Returns None when capture has stopped. The returned frame's Texture is
        /// owned by the session and is valid only until ReleaseFrame is called.
        abstract member TryGetNextFrame: System.Threading.CancellationToken -> CapturedFrame option
        abstract member ReleaseFrame: CapturedFrame -> unit
        abstract member DroppedFrameCount: int

    // ---------------------------------------------------------------------------------
    // Interop structures
    // ---------------------------------------------------------------------------------

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type SizeInt32 =
        val Width: int
        val Height: int
        new(width: int, height: int) = { Width = width; Height = height }

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type WinRtTimeSpan =
        val Duration: int64

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type RawPoint =
        val X: int
        val Y: int
        new(x: int, y: int) = { X = x; Y = y }

    /// D3D11_TEXTURE2D_DESC. All fields are 4-byte (SampleDesc is inlined as two UINTs).
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type D3D11Texture2DDesc =
        val mutable Width: uint32
        val mutable Height: uint32
        val mutable MipLevels: uint32
        val mutable ArraySize: uint32
        val mutable Format: uint32
        val mutable SampleDescCount: uint32
        val mutable SampleDescQuality: uint32
        val mutable Usage: uint32
        val mutable BindFlags: uint32
        val mutable CpuAccessFlags: uint32
        val mutable MiscFlags: uint32

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type D3D11MappedSubresource =
        val mutable PData: nativeint
        val mutable RowPitch: uint32
        val mutable DepthPitch: uint32

    [<Flags>]
    type D3D11CreateDeviceFlags =
        | None = 0u
        | SingleThreaded = 0x1u
        | Debug = 0x2u
        | BgraSupport = 0x20u
        | VideoSupport = 0x800u

    type D3DDriverType =
        | Unknown = 0
        | Hardware = 1
        | Reference = 2
        | Null = 3
        | Software = 4
        | Warp = 5

    // ---------------------------------------------------------------------------------
    // StdCall vtable delegates
    // ---------------------------------------------------------------------------------

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type CloseFn = delegate of nativeint -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type GraphicsCaptureSessionIsSupportedFn = delegate of nativeint * byte byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type GraphicsCaptureItemInteropCreateForMonitorFn =
        delegate of nativeint * nativeint * Guid byref * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type GraphicsCaptureItemGetSizeFn = delegate of nativeint * SizeInt32 byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type FramePoolCreateFreeThreadedFn =
        delegate of nativeint * nativeint * int * int * SizeInt32 * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type FramePoolCreateFn =
        delegate of nativeint * nativeint * int * int * SizeInt32 * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type FramePoolRecreateFn =
        delegate of nativeint * nativeint * int * int * SizeInt32 -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type FramePoolTryGetNextFrameFn = delegate of nativeint * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type FramePoolCreateCaptureSessionFn = delegate of nativeint * nativeint * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type CaptureFrameGetSurfaceFn = delegate of nativeint * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type CaptureFrameGetSystemRelativeTimeFn = delegate of nativeint * WinRtTimeSpan byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type CaptureFrameGetContentSizeFn = delegate of nativeint * SizeInt32 byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type CaptureSessionStartCaptureFn = delegate of nativeint -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type CaptureSessionPutBooleanFn = delegate of nativeint * byte -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type DxgiInterfaceAccessGetInterfaceFn = delegate of nativeint * Guid byref * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type MonitorEnumProc = delegate of nativeint * nativeint * nativeint * nativeint -> bool

    // DXGI
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type DxgiFactoryEnumAdapters1Fn = delegate of nativeint * uint32 * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type DxgiAdapterGetDesc1Fn = delegate of nativeint * nativeint -> int

    // D3D11 device / context
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type Texture2DGetDescFn = delegate of nativeint * D3D11Texture2DDesc byref -> unit

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type CreateTexture2DFn =
        delegate of nativeint * D3D11Texture2DDesc byref * nativeint * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type ContextMapFn =
        delegate of nativeint * nativeint * uint32 * uint32 * uint32 * D3D11MappedSubresource byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type ContextUnmapFn = delegate of nativeint * nativeint * uint32 -> unit

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type ContextCopyResourceFn = delegate of nativeint * nativeint * nativeint -> unit

    // ---------------------------------------------------------------------------------
    // P/Invoke
    // ---------------------------------------------------------------------------------

    [<DllImport("d3d11.dll", PreserveSig = true)>]
    extern int D3D11CreateDevice(
        nativeint pAdapter,
        D3DDriverType driverType,
        nativeint software,
        D3D11CreateDeviceFlags flags,
        int[] featureLevels,
        uint32 featureLevelsCount,
        uint32 sdkVersion,
        nativeint& device,
        int& featureLevel,
        nativeint& immediateContext)

    [<DllImport("d3d11.dll", PreserveSig = true)>]
    extern int CreateDirect3D11DeviceFromDXGIDevice(nativeint dxgiDevice, nativeint& graphicsDevice)

    [<DllImport("dxgi.dll", PreserveSig = true)>]
    extern int CreateDXGIFactory1(Guid& riid, nativeint& factory)

    [<DllImport("user32.dll", PreserveSig = true)>]
    [<return: MarshalAs(UnmanagedType.Bool)>]
    extern bool EnumDisplayMonitors(nativeint hdc, nativeint lprcClip, MonitorEnumProc lpfnEnum, nativeint dwData)

    [<DllImport("user32.dll", PreserveSig = true)>]
    extern nativeint MonitorFromPoint(RawPoint pt, uint32 dwFlags)

    // ---------------------------------------------------------------------------------
    // IIDs and class names
    // ---------------------------------------------------------------------------------

    let private iidIGraphicsCaptureSessionStatics = Guid("2224A540-5974-49AA-B232-0882A0F1D0F7")
    let private iidIGraphicsCaptureItemInterop = Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")
    let private iidIGraphicsCaptureItem = Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760")
    let private iidIDirect3D11CaptureFramePoolStatics = Guid("7784056A-67AA-4D53-AE54-1088D5A8CA21")
    let private iidIDirect3D11CaptureFramePoolStatics2 = Guid("589B103F-6BBC-5DF5-A991-02E28B3B66D5")
    let private iidIGraphicsCaptureSession2 = Guid("2C39AE40-7D2E-5044-804E-8B6799D4CF9E")
    let private iidIGraphicsCaptureSession3 = Guid("F2CDD966-22AE-5EA1-9596-3A289344C3BE")
    let private iidIDXGIDevice = Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C")
    let private iidIDXGIFactory1 = Guid("770AAE78-F26F-4DBA-A829-253C83D1B387")
    let private iidID3D11Texture2D = Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C")
    let private iidIDirect3DDxgiInterfaceAccess = Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")
    let private iidIClosable = Guid("30D5A829-7FA4-4026-83BB-D75BAE4EA99E")

    let private graphicsCaptureSessionClassName = "Windows.Graphics.Capture.GraphicsCaptureSession"
    let private graphicsCaptureItemClassName = "Windows.Graphics.Capture.GraphicsCaptureItem"
    let private framePoolClassName = "Windows.Graphics.Capture.Direct3D11CaptureFramePool"

    // ---------------------------------------------------------------------------------
    // Low-level helpers
    // ---------------------------------------------------------------------------------

    let private hresult hr = RawWinRt.hresult hr

    let private checkHr operation hr =
        if hr <> S_OK then
            failwith $"{operation} failed: {hresult hr}"

    /// Raw vtable slot lookup for handles we do not wrap in a ComPtr.
    let private slotOf (handle: nativeint) (index: int) =
        let vtable = Marshal.ReadIntPtr(handle)
        Marshal.ReadIntPtr(vtable, index * IntPtr.Size)

    let private getDelegate<'T> (fnPtr: nativeint) : 'T =
        Marshal.GetDelegateForFunctionPointer<'T>(fnPtr)

    let private releaseHandle (ptr: nativeint) =
        if ptr <> nativeint 0 then
            (new RawWinRt.ComPtr(ptr)).Release()

    /// QI IClosable and Close (returns a WGC frame's buffer to the pool), then Release.
    let private closeAndReleaseHandle (ptr: nativeint) =
        if ptr <> nativeint 0 then
            let owner = new RawWinRt.ComPtr(ptr)
            match owner.QueryInterface(iidIClosable) with
            | Ok closable ->
                try
                    let close = getDelegate<CloseFn> (closable.Slot(6))
                    close.Invoke(closable.Handle) |> ignore
                finally
                    (closable :> IDisposable).Dispose()
            | Error _ -> ()

            owner.Release()

    let private closeWinRtObject (ptr: nativeint) =
        if ptr <> nativeint 0 then
            let owner = new RawWinRt.ComPtr(ptr)

            match owner.QueryInterface(iidIClosable) with
            | Ok closable ->
                try
                    let close = getDelegate<CloseFn> (closable.Slot(6))
                    close.Invoke(closable.Handle) |> ignore
                finally
                    (closable :> IDisposable).Dispose()
            | Error _ -> ()

    let private disposeCom (ptr: RawWinRt.ComPtr) =
        if not (isNull (box ptr)) then
            (ptr :> IDisposable).Dispose()

    // ---------------------------------------------------------------------------------
    // DXGI adapter enumeration / selection (fixes BLOCKER #1: hybrid-GPU adapter bug)
    // ---------------------------------------------------------------------------------

    let private createDxgiFactory1 () =
        let mutable iid = iidIDXGIFactory1
        let mutable ptr = nativeint 0
        let hr = CreateDXGIFactory1(&iid, &ptr)
        checkHr "CreateDXGIFactory1" hr

        if ptr = nativeint 0 then
            failwith "CreateDXGIFactory1 returned null."

        new RawWinRt.ComPtr(ptr)

    /// Reads DXGI_ADAPTER_DESC1 from a raw IDXGIAdapter1* using explicit field offsets
    /// (x64 layout): Description[128] WCHARs @0 (256 bytes), VendorId @256,
    /// AdapterLuid.LowPart @296, AdapterLuid.HighPart @300.
    let private readAdapterDesc1 (adapterPtr: nativeint) : int64 * string * uint32 =
        let getDesc1 = getDelegate<DxgiAdapterGetDesc1Fn> (slotOf adapterPtr 10)
        let buffer = Marshal.AllocHGlobal(400)

        try
            let hr = getDesc1.Invoke(adapterPtr, buffer)

            if hr <> S_OK then
                (0L, "", 0u)
            else
                let description =
                    match Marshal.PtrToStringUni(buffer) with
                    | null -> ""
                    | value -> value

                let vendorId = uint32 (Marshal.ReadInt32(buffer, 256))
                let luidLow = Marshal.ReadInt32(buffer, 296)
                let luidHigh = Marshal.ReadInt32(buffer, 300)
                let luid = (int64 luidHigh <<< 32) ||| int64 (uint32 luidLow)
                (luid, description, vendorId)
        finally
            Marshal.FreeHGlobal(buffer)

    /// (index, IDXGIAdapter1* wrapped, luid, description, vendorId). Each ComPtr is a live
    /// reference the caller must Release exactly once.
    let private enumerateAdapterPtrs (factory: RawWinRt.ComPtr) =
        let enumAdapters1 = getDelegate<DxgiFactoryEnumAdapters1Fn> (factory.Slot(12))
        let result = ResizeArray<int * RawWinRt.ComPtr * int64 * string * uint32>()
        let mutable index = 0u
        let mutable keepGoing = true

        while keepGoing do
            let mutable adapterPtr = nativeint 0
            let hr = enumAdapters1.Invoke(factory.Handle, index, &adapterPtr)

            if hr <> S_OK || adapterPtr = nativeint 0 then
                keepGoing <- false
            else
                let luid, description, vendorId = readAdapterDesc1 adapterPtr
                result.Add(int index, new RawWinRt.ComPtr(adapterPtr), luid, description, vendorId)
                index <- index + 1u

        List.ofSeq result

    /// Selects the target adapter and releases all others. Returns the chosen
    /// IDXGIAdapter1* plus its LUID/description/vendorId. The caller owns the returned ptr.
    let private selectAdapter (factory: RawWinRt.ComPtr) (options: CaptureOptions) =
        let adapters = enumerateAdapterPtrs factory

        if List.isEmpty adapters then
            failwith "No DXGI adapters were found."

        let chosen =
            match options.PreferredAdapterLuid with
            | Some preferred -> adapters |> List.tryFind (fun (_, _, luid, _, _) -> luid = preferred)
            | None -> adapters |> List.tryFind (fun (_, _, _, _, vendor) -> vendor = VENDOR_NVIDIA)

        let pick =
            match chosen with
            | Some adapter -> adapter
            | None -> List.head adapters // fall back to adapter 0

        let pickedIndex, _, _, _, _ = pick

        // Release every adapter except the one we keep.
        for (index, ptr, _, _, _) in adapters do
            if index <> pickedIndex then
                ptr.Release()

        let _, ptr, luid, description, vendor = pick
        (ptr, luid, description, vendor)

    let private createD3D11DeviceOnAdapter (adapter: RawWinRt.ComPtr) =
        // 11_1, 11_0, 10_1, 10_0.
        let featureLevels = [| 0xB100; 0xB000; 0xA100; 0xA000 |]
        let flags = D3D11CreateDeviceFlags.BgraSupport

        let mutable device = nativeint 0
        let mutable context = nativeint 0
        let mutable selectedFeatureLevel = 0

        // D3D_DRIVER_TYPE_UNKNOWN is required when an explicit adapter is supplied;
        // passing HARDWARE with a non-null adapter fails with E_INVALIDARG.
        let hr =
            D3D11CreateDevice(
                adapter.Handle,
                D3DDriverType.Unknown,
                nativeint 0,
                flags,
                featureLevels,
                uint32 featureLevels.Length,
                D3D11_SDK_VERSION,
                &device,
                &selectedFeatureLevel,
                &context)

        checkHr "D3D11CreateDevice(UNKNOWN, explicit adapter)" hr

        if device = nativeint 0 || context = nativeint 0 then
            releaseHandle device
            releaseHandle context
            failwith "D3D11CreateDevice returned a null device or context."

        (new RawWinRt.ComPtr(device), new RawWinRt.ComPtr(context))

    let private createWinRtDirect3DDevice (d3d11Device: RawWinRt.ComPtr) =
        match d3d11Device.QueryInterface(iidIDXGIDevice) with
        | Error hr -> failwith $"ID3D11Device.QueryInterface(IDXGIDevice) failed: {hresult hr}"
        | Ok dxgiDevice ->
            use dxgiDevice = dxgiDevice
            let mutable winRtDevice = nativeint 0
            let hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Handle, &winRtDevice)
            checkHr "CreateDirect3D11DeviceFromDXGIDevice" hr

            if winRtDevice = nativeint 0 then
                failwith "CreateDirect3D11DeviceFromDXGIDevice returned null."

            new RawWinRt.ComPtr(winRtDevice)

    // ---------------------------------------------------------------------------------
    // Monitor resolution + capture item / frame pool / session creation
    // ---------------------------------------------------------------------------------

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

            GC.KeepAlive(callback)

            if monitorIndex >= monitors.Count then
                failwith $"Monitor index {monitorIndex} was not found. Discovered {monitors.Count} monitor(s)."

            monitors[monitorIndex]

    let private createGraphicsCaptureItemForMonitor monitorIndex =
        let hMonitor = monitorFromIndex monitorIndex

        match RawWinRt.activationFactory graphicsCaptureItemClassName iidIGraphicsCaptureItemInterop with
        | Error hr ->
            failwith $"RoGetActivationFactory({graphicsCaptureItemClassName}, IGraphicsCaptureItemInterop) failed: {hresult hr}"
        | Ok interopFactory ->
            use interopFactory = interopFactory

            // IGraphicsCaptureItemInterop: 3 CreateForWindow, 4 CreateForMonitor.
            let createForMonitor =
                getDelegate<GraphicsCaptureItemInteropCreateForMonitorFn> (interopFactory.Slot(4))

            let mutable itemIid = iidIGraphicsCaptureItem
            let mutable item = nativeint 0
            let hr = createForMonitor.Invoke(interopFactory.Handle, hMonitor, &itemIid, &item)
            checkHr "IGraphicsCaptureItemInterop.CreateForMonitor" hr

            if item = nativeint 0 then
                failwith "IGraphicsCaptureItemInterop.CreateForMonitor returned null."

            new RawWinRt.ComPtr(item)

    let private getCaptureItemSize (item: RawWinRt.ComPtr) =
        let getSize = getDelegate<GraphicsCaptureItemGetSizeFn> (item.Slot(7))
        let mutable size = SizeInt32(0, 0)
        let hr = getSize.Invoke(item.Handle, &size)
        checkHr "IGraphicsCaptureItem.get_Size" hr

        if size.Width <= 0 || size.Height <= 0 then
            failwith $"GraphicsCaptureItem returned an invalid size {size.Width}x{size.Height}."

        size

    let private createFramePool (winRtDevice: RawWinRt.ComPtr) (size: SizeInt32) =
        match RawWinRt.activationFactory framePoolClassName iidIDirect3D11CaptureFramePoolStatics2 with
        | Ok statics2 ->
            use statics2 = statics2
            // IDirect3D11CaptureFramePoolStatics2: 6 CreateFreeThreaded.
            let createFreeThreaded =
                getDelegate<FramePoolCreateFreeThreadedFn> (statics2.Slot(6))

            let mutable framePool = nativeint 0

            let hr =
                createFreeThreaded.Invoke(
                    statics2.Handle,
                    winRtDevice.Handle,
                    DirectXPixelFormat_B8G8R8A8UIntNormalized,
                    NumBuffers,
                    size,
                    &framePool)

            checkHr "IDirect3D11CaptureFramePoolStatics2.CreateFreeThreaded" hr

            if framePool = nativeint 0 then
                failwith "CreateFreeThreaded returned null."

            new RawWinRt.ComPtr(framePool)
        | Error _ ->
            match RawWinRt.activationFactory framePoolClassName iidIDirect3D11CaptureFramePoolStatics with
            | Error hr -> failwith $"RoGetActivationFactory({framePoolClassName}) failed: {hresult hr}"
            | Ok statics ->
                use statics = statics
                let create = getDelegate<FramePoolCreateFn> (statics.Slot(6))
                let mutable framePool = nativeint 0

                let hr =
                    create.Invoke(
                        statics.Handle,
                        winRtDevice.Handle,
                        DirectXPixelFormat_B8G8R8A8UIntNormalized,
                        NumBuffers,
                        size,
                        &framePool)

                checkHr "IDirect3D11CaptureFramePoolStatics.Create" hr

                if framePool = nativeint 0 then
                    failwith "Create returned null."

                new RawWinRt.ComPtr(framePool)

    let private recreateFramePool (framePool: RawWinRt.ComPtr) (winRtDevice: nativeint) (size: SizeInt32) =
        // IDirect3D11CaptureFramePool: 6 Recreate.
        let recreate = getDelegate<FramePoolRecreateFn> (framePool.Slot(6))

        let hr =
            recreate.Invoke(framePool.Handle, winRtDevice, DirectXPixelFormat_B8G8R8A8UIntNormalized, NumBuffers, size)

        checkHr "IDirect3D11CaptureFramePool.Recreate" hr

    let private createCaptureSession (framePool: RawWinRt.ComPtr) (item: RawWinRt.ComPtr) =
        // IDirect3D11CaptureFramePool: 10 CreateCaptureSession.
        let createSession = getDelegate<FramePoolCreateCaptureSessionFn> (framePool.Slot(10))
        let mutable session = nativeint 0
        let hr = createSession.Invoke(framePool.Handle, item.Handle, &session)
        checkHr "IDirect3D11CaptureFramePool.CreateCaptureSession" hr

        if session = nativeint 0 then
            failwith "CreateCaptureSession returned null."

        new RawWinRt.ComPtr(session)

    let private trySetCursorCaptureEnabled (session: RawWinRt.ComPtr) enabled =
        match session.QueryInterface(iidIGraphicsCaptureSession2) with
        | Error _ -> ()
        | Ok session2 ->
            try
                // IGraphicsCaptureSession2: 7 put_IsCursorCaptureEnabled.
                let putCursor = getDelegate<CaptureSessionPutBooleanFn> (session2.Slot(7))
                putCursor.Invoke(session2.Handle, (if enabled then 1uy else 0uy)) |> ignore
            finally
                (session2 :> IDisposable).Dispose()

    /// IsBorderRequired lives on IGraphicsCaptureSession3 (Windows 11+). Probe by QI and
    /// ignore failure gracefully on down-level builds.
    let private trySetBorderRequired (session: RawWinRt.ComPtr) required =
        match session.QueryInterface(iidIGraphicsCaptureSession3) with
        | Error _ -> ()
        | Ok session3 ->
            try
                // IGraphicsCaptureSession3: 7 put_IsBorderRequired.
                let putBorder = getDelegate<CaptureSessionPutBooleanFn> (session3.Slot(7))
                putBorder.Invoke(session3.Handle, (if required then 1uy else 0uy)) |> ignore
            finally
                (session3 :> IDisposable).Dispose()

    let private startCapture (session: RawWinRt.ComPtr) =
        // IGraphicsCaptureSession: 6 StartCapture.
        let start = getDelegate<CaptureSessionStartCaptureFn> (session.Slot(6))
        let hr = start.Invoke(session.Handle)
        checkHr "IGraphicsCaptureSession.StartCapture" hr

    // ---------------------------------------------------------------------------------
    // Frame acquisition + CPU readback
    // ---------------------------------------------------------------------------------

    [<Struct>]
    type private RawFrame =
        { Frame: nativeint
          Texture: nativeint
          ContentW: int
          ContentH: int
          TexW: int
          TexH: int
          Qpc: int64 }

    /// Pulls the next frame from the pool. On success the returned Frame and Texture are
    /// live references the caller owns (Frame keeps the pool buffer checked out until it
    /// is closed/released, which is essential for the NVENC-direct GPU path). Returns None
    /// when no frame is ready, releasing anything it acquired.
    let private tryAcquireRawFrame (framePool: RawWinRt.ComPtr) : RawFrame option =
        let tryGet = getDelegate<FramePoolTryGetNextFrameFn> (framePool.Slot(7))
        let mutable framePtr = nativeint 0
        let hr = tryGet.Invoke(framePool.Handle, &framePtr)

        if hr <> S_OK || framePtr = nativeint 0 then
            None
        else
            let frame = new RawWinRt.ComPtr(framePtr)
            let mutable success = false
            let mutable texture = nativeint 0

            try
                // IDirect3D11CaptureFrame: 6 get_Surface, 7 get_SystemRelativeTime, 8 get_ContentSize.
                let getSurface = getDelegate<CaptureFrameGetSurfaceFn> (frame.Slot(6))
                let getTime = getDelegate<CaptureFrameGetSystemRelativeTimeFn> (frame.Slot(7))
                let getContentSize = getDelegate<CaptureFrameGetContentSizeFn> (frame.Slot(8))

                let mutable surfacePtr = nativeint 0

                if getSurface.Invoke(frame.Handle, &surfacePtr) <> S_OK || surfacePtr = nativeint 0 then
                    None
                else
                    use surface = new RawWinRt.ComPtr(surfacePtr)
                    let mutable timestamp = Unchecked.defaultof<WinRtTimeSpan>
                    let mutable contentSize = SizeInt32(0, 0)

                    if getTime.Invoke(frame.Handle, &timestamp) <> S_OK then
                        None
                    elif getContentSize.Invoke(frame.Handle, &contentSize) <> S_OK then
                        None
                    else
                        match surface.QueryInterface(iidIDirect3DDxgiInterfaceAccess) with
                        | Error _ -> None
                        | Ok dxgiAccess ->
                            use dxgiAccess = dxgiAccess
                            // IDirect3DDxgiInterfaceAccess: 3 GetInterface.
                            let getInterface = getDelegate<DxgiInterfaceAccessGetInterfaceFn> (dxgiAccess.Slot(3))
                            let mutable textureIid = iidID3D11Texture2D
                            let mutable tex = nativeint 0

                            if getInterface.Invoke(dxgiAccess.Handle, &textureIid, &tex) <> S_OK || tex = nativeint 0 then
                                None
                            else
                                texture <- tex
                                let getDesc = getDelegate<Texture2DGetDescFn> (slotOf tex 10)
                                let mutable desc = Unchecked.defaultof<D3D11Texture2DDesc>
                                getDesc.Invoke(tex, &desc)

                                let result =
                                    { Frame = frame.Handle
                                      Texture = tex
                                      ContentW = contentSize.Width
                                      ContentH = contentSize.Height
                                      TexW = int desc.Width
                                      TexH = int desc.Height
                                      Qpc = timestamp.Duration }

                                success <- true
                                Some result
            finally
                if not success then
                    releaseHandle texture
                    frame.Release()

    /// Copies a captured texture into a STAGING texture, maps it, and returns a tightly
    /// packed BGRA buffer (stride = width*4) even when the mapped RowPitch is padded.
    let private readbackBgra (deviceHandle: nativeint) (contextHandle: nativeint) (sourceTexture: nativeint) (texW: int) (texH: int) : byte array option =
        let getDesc = getDelegate<Texture2DGetDescFn> (slotOf sourceTexture 10)
        let mutable desc = Unchecked.defaultof<D3D11Texture2DDesc>
        getDesc.Invoke(sourceTexture, &desc)

        let mutable staging = desc
        staging.Usage <- D3D11_USAGE_STAGING
        staging.BindFlags <- 0u
        staging.CpuAccessFlags <- D3D11_CPU_ACCESS_READ
        staging.MiscFlags <- 0u

        // ID3D11Device: 5 CreateTexture2D.
        let createTexture2D = getDelegate<CreateTexture2DFn> (slotOf deviceHandle 5)
        let mutable stagingTex = nativeint 0
        let hr = createTexture2D.Invoke(deviceHandle, &staging, nativeint 0, &stagingTex)

        if hr <> S_OK || stagingTex = nativeint 0 then
            None
        else
            try
                // ID3D11DeviceContext: 47 CopyResource, 14 Map, 15 Unmap.
                let copyResource = getDelegate<ContextCopyResourceFn> (slotOf contextHandle 47)
                copyResource.Invoke(contextHandle, stagingTex, sourceTexture)

                let mapFn = getDelegate<ContextMapFn> (slotOf contextHandle 14)
                let unmapFn = getDelegate<ContextUnmapFn> (slotOf contextHandle 15)
                let mutable mapped = Unchecked.defaultof<D3D11MappedSubresource>
                let mapHr = mapFn.Invoke(contextHandle, stagingTex, 0u, D3D11_MAP_READ, 0u, &mapped)

                if mapHr <> S_OK || mapped.PData = nativeint 0 then
                    None
                else
                    try
                        let width = if texW > 0 then texW else int desc.Width
                        let height = if texH > 0 then texH else int desc.Height
                        let rowBytes = width * 4
                        let buffer = Array.zeroCreate<byte> (rowBytes * height)
                        let pitch = int mapped.RowPitch

                        for y in 0 .. height - 1 do
                            let src = IntPtr.Add(mapped.PData, y * pitch)
                            Marshal.Copy(src, buffer, y * rowBytes, rowBytes)

                        Some buffer
                    finally
                        unmapFn.Invoke(contextHandle, stagingTex, 0u)
            finally
                releaseHandle stagingTex

    // ---------------------------------------------------------------------------------
    // Session implementation
    // ---------------------------------------------------------------------------------

    type private WgcSessionImpl
        (
            roScope: RawWinRt.RoScope,
            d3d11Device: RawWinRt.ComPtr,
            d3d11Context: RawWinRt.ComPtr,
            winRtDevice: RawWinRt.ComPtr,
            item: RawWinRt.ComPtr,
            framePool: RawWinRt.ComPtr,
            captureSession: RawWinRt.ComPtr,
            adapterLuid: int64,
            adapterDescription: string,
            initialWidth: int,
            initialHeight: int,
            fps: int,
            needCpuReadback: bool
        ) =

        let capacity = max NumBuffers fps
        let frames = new BlockingCollection<CapturedFrame>(capacity)
        // GPU-path frames: maps the handed-out texture -> its owning Direct3D11CaptureFrame,
        // so ReleaseFrame can close the frame (return the pool buffer) and release the texture.
        let liveFrames = ConcurrentDictionary<nativeint, nativeint>()
        let cts = new CancellationTokenSource()
        let waitMs = max 1 (1000 / (max 1 (fps * 2)))

        let mutable disposed = 0
        let mutable dropped = 0
        let mutable frameIndex = 0
        let mutable firstQpc = 0L
        let mutable haveFirstQpc = false
        let mutable poolWidth = initialWidth
        let mutable poolHeight = initialHeight
        let mutable currentWidth = initialWidth
        let mutable currentHeight = initialHeight

        // Releases both the texture reference and (for GPU frames) the owning capture frame.
        let releaseCaptured (frame: CapturedFrame) =
            if frame.Texture <> nativeint 0 then
                releaseHandle frame.Texture

                match liveFrames.TryRemove(frame.Texture) with
                | true, framePtr -> closeAndReleaseHandle framePtr
                | _ -> ()

        // Enqueue with backpressure: on a full queue drop the frame, release it, and count it.
        let enqueue (frame: CapturedFrame) =
            if not (frames.TryAdd(frame)) then
                releaseCaptured frame
                Interlocked.Increment(&dropped) |> ignore

        let produce () =
            while not cts.IsCancellationRequested do
                match tryAcquireRawFrame framePool with
                | None ->
                    // No frame ready: block up to one (half-)frame interval; wakes on cancel.
                    cts.Token.WaitHandle.WaitOne(waitMs) |> ignore
                | Some raw ->
                    // Size change (BLOCKER-adjacent HIGH #4): recreate the pool and report new size.
                    if raw.ContentW > 0 && raw.ContentH > 0
                       && (raw.ContentW <> poolWidth || raw.ContentH <> poolHeight) then
                        try
                            recreateFramePool framePool winRtDevice.Handle (SizeInt32(raw.ContentW, raw.ContentH))
                            poolWidth <- raw.ContentW
                            poolHeight <- raw.ContentH
                        with _ ->
                            ()

                        currentWidth <- raw.ContentW
                        currentHeight <- raw.ContentH

                    if not haveFirstQpc then
                        firstQpc <- raw.Qpc
                        haveFirstQpc <- true

                    let index = frameIndex
                    frameIndex <- frameIndex + 1
                    let pts = raw.Qpc - firstQpc

                    if needCpuReadback then
                        // Software (x265) path: copy to CPU, then immediately return the pool buffer.
                        let bgra = readbackBgra d3d11Device.Handle d3d11Context.Handle raw.Texture raw.TexW raw.TexH
                        releaseHandle raw.Texture
                        closeAndReleaseHandle raw.Frame

                        match bgra with
                        | Some buffer ->
                            enqueue
                                { Index = index
                                  QpcTime = raw.Qpc
                                  Pts = pts
                                  Width = raw.TexW
                                  Height = raw.TexH
                                  Texture = nativeint 0
                                  Bgra = Some buffer
                                  RowPitch = raw.TexW * 4 }
                        | None -> Interlocked.Increment(&dropped) |> ignore
                    else
                        // NVENC-direct GPU path: hand out the texture, keep the frame alive
                        // (buffer stays checked out) until ReleaseFrame.
                        liveFrames.[raw.Texture] <- raw.Frame

                        enqueue
                            { Index = index
                              QpcTime = raw.Qpc
                              Pts = pts
                              Width = raw.TexW
                              Height = raw.TexH
                              Texture = raw.Texture
                              Bgra = None
                              RowPitch = 0 }

        let workerProc () =
            // The pool is free-threaded; initialise this worker as MTA so its WinRT calls
            // marshal correctly independently of the thread that called create.
            let scope = RawWinRt.RoScope.InitializeMta()

            try
                produce ()
            finally
                if not frames.IsAddingCompleted then
                    try
                        frames.CompleteAdding()
                    with :? InvalidOperationException ->
                        ()

                match scope with
                | Ok s -> (s :> IDisposable).Dispose()
                | Error _ -> ()

        let worker = Thread(ThreadStart(workerProc))

        do
            worker.IsBackground <- true
            worker.Name <- "Yumlog.WgcCapture"
            worker.Start()

        member private _.TryGetNextFrameCore(token: CancellationToken) : CapturedFrame option =
            if disposed <> 0 then
                None
            else
                use linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, token)

                try
                    let mutable frame = Unchecked.defaultof<CapturedFrame>

                    if frames.TryTake(&frame, Timeout.Infinite, linked.Token) then
                        Some frame
                    else
                        None
                with
                | :? OperationCanceledException -> None
                | :? ObjectDisposedException -> None
                | :? InvalidOperationException -> None

        member private _.Cleanup() =
            if Interlocked.CompareExchange(&disposed, 1, 0) = 0 then
                // 1. Stop the worker and wait for it to finish touching COM objects.
                cts.Cancel()

                if worker.IsAlive then
                    worker.Join(5000) |> ignore

                if not frames.IsAddingCompleted then
                    try
                        frames.CompleteAdding()
                    with :? InvalidOperationException ->
                        ()

                // 2. Drain and release any queued frames (textures + owning frames).
                let mutable pending = Unchecked.defaultof<CapturedFrame>

                while frames.TryTake(&pending) do
                    releaseCaptured pending

                // 3. Release any GPU frames that were handed out but never released.
                for kvp in liveFrames do
                    releaseHandle kvp.Key
                    closeAndReleaseHandle kvp.Value

                liveFrames.Clear()

                // 4. Close WinRT objects, then release all COM, then the RoInitialize scope.
                closeWinRtObject captureSession.Handle
                closeWinRtObject framePool.Handle
                closeWinRtObject winRtDevice.Handle

                disposeCom captureSession
                disposeCom framePool
                disposeCom item
                disposeCom winRtDevice
                disposeCom d3d11Context
                disposeCom d3d11Device

                cts.Dispose()
                frames.Dispose()
                (roScope :> IDisposable).Dispose()

        interface IWgcSession with
            member _.Width = currentWidth
            member _.Height = currentHeight
            member _.D3D11Device = d3d11Device.Handle
            member _.AdapterDescription = adapterDescription
            member _.AdapterLuid = adapterLuid
            member this.TryGetNextFrame(token) = this.TryGetNextFrameCore(token)
            member _.ReleaseFrame(frame) = releaseCaptured frame
            member _.DroppedFrameCount = Volatile.Read(&dropped)

        interface IDisposable with
            member this.Dispose() = this.Cleanup()

    // ---------------------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------------------

    /// True only when Windows Graphics Capture is supported. Returns false (never throws)
    /// on down-level OS or if activation fails.
    let isAvailable () : bool =
        try
            match RawWinRt.RoScope.InitializeMta() with
            | Error _ -> false
            | Ok scope ->
                use scope = scope

                match RawWinRt.activationFactory graphicsCaptureSessionClassName iidIGraphicsCaptureSessionStatics with
                | Error _ -> false
                | Ok factory ->
                    use factory = factory
                    // IGraphicsCaptureSessionStatics: 6 IsSupported.
                    let isSupported = getDelegate<GraphicsCaptureSessionIsSupportedFn> (factory.Slot(6))
                    let mutable result = 0uy
                    let hr = isSupported.Invoke(factory.Handle, &result)
                    hr = S_OK && result <> 0uy
        with _ ->
            false

    /// Enumerates DXGI adapters so the encoder can pick NVIDIA. Returns (luid, description, vendorId).
    let enumerateAdapters () : (int64 * string * uint32) list =
        let factory = createDxgiFactory1 ()

        try
            enumerateAdapterPtrs factory
            |> List.map (fun (_, ptr, luid, description, vendorId) ->
                ptr.Release()
                (luid, description, vendorId))
        finally
            (factory :> IDisposable).Dispose()

    let create (options: CaptureOptions) : IWgcSession =
        if options.Fps <= 0 then
            invalidArg "options" "CaptureOptions.Fps must be > 0."

        if options.MonitorIndex < 0 then
            invalidArg "options" "CaptureOptions.MonitorIndex must be >= 0."

        match RawWinRt.RoScope.InitializeMta() with
        | Error hr -> failwith $"RoInitialize failed: {hresult hr}"
        | Ok roScope ->
            try
                let factory = createDxgiFactory1 ()
                let adapter, adapterLuid, adapterDescription, _vendorId = selectAdapter factory options
                let d3d11Device, d3d11Context = createD3D11DeviceOnAdapter adapter

                // The device holds its own reference on the adapter; release our copies.
                adapter.Release()
                (factory :> IDisposable).Dispose()

                let winRtDevice = createWinRtDirect3DDevice d3d11Device
                let item = createGraphicsCaptureItemForMonitor options.MonitorIndex
                let size = getCaptureItemSize item
                let framePool = createFramePool winRtDevice size
                let captureSession = createCaptureSession framePool item

                trySetCursorCaptureEnabled captureSession true

                if options.DisableCaptureBorder then
                    trySetBorderRequired captureSession false

                startCapture captureSession

                new WgcSessionImpl(
                    roScope,
                    d3d11Device,
                    d3d11Context,
                    winRtDevice,
                    item,
                    framePool,
                    captureSession,
                    adapterLuid,
                    adapterDescription,
                    size.Width,
                    size.Height,
                    options.Fps,
                    options.NeedCpuReadback
                )
                :> IWgcSession
            with _ ->
                (roScope :> IDisposable).Dispose()
                reraise ()
