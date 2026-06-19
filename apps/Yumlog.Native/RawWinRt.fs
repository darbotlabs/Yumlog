namespace Yumlog.Native

open System
open System.Runtime.InteropServices
open System.Threading

module RawWinRt =
    [<Literal>]
    let S_OK = 0

    [<Literal>]
    let S_FALSE = 1

    [<Literal>]
    let RPC_E_CHANGED_MODE = -2147417850

    [<Flags>]
    type RoInitType =
        | SingleThreaded = 0
        | MultiThreaded = 1

    [<DllImport("combase.dll", PreserveSig = true)>]
    extern int RoInitialize(RoInitType initType)

    [<DllImport("combase.dll", PreserveSig = true)>]
    extern void RoUninitialize()

    [<DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)>]
    extern int WindowsCreateString(string sourceString, uint32 length, nativeint& hstring)

    [<DllImport("combase.dll", PreserveSig = true)>]
    extern int WindowsDeleteString(nativeint hstring)

    [<DllImport("combase.dll", PreserveSig = true)>]
    extern nativeint WindowsGetStringRawBuffer(nativeint hstring, uint32& length)

    [<DllImport("combase.dll", PreserveSig = true)>]
    extern int RoGetActivationFactory(nativeint activatableClassId, Guid& iid, nativeint& factory)

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type ReleaseFn = delegate of nativeint -> uint32

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type QueryInterfaceFn = delegate of nativeint * Guid byref * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type TextRecognizerGetReadyStateFn = delegate of nativeint * int byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type TextRecognizerEnsureReadyAsyncFn = delegate of nativeint * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type TextRecognizerCreateAsyncFn = delegate of nativeint * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type TextRecognizerRecognizeTextFromImageFn = delegate of nativeint * nativeint * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type AsyncInfoGetStatusFn = delegate of nativeint * int byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type AsyncInfoGetErrorCodeFn = delegate of nativeint * int byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type AsyncInfoCloseFn = delegate of nativeint -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type AsyncOperationGetResultsPtrFn = delegate of nativeint * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type VectorViewGetAtPtrFn = delegate of nativeint * uint32 * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type VectorViewGetSizeFn = delegate of nativeint * uint32 byref -> int

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type RawPoint =
        val X: single
        val Y: single

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type RawTextBounds =
        val TopLeft: RawPoint
        val TopRight: RawPoint
        val BottomRight: RawPoint
        val BottomLeft: RawPoint

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type RecognizedTextGetLinesFn = delegate of nativeint * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type RecognizedTextGetTextAngleFn = delegate of nativeint * single byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type RecognizedLineGetBoundsFn = delegate of nativeint * RawTextBounds byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type RecognizedLineGetTextFn = delegate of nativeint * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type RecognizedLineGetWordsFn = delegate of nativeint * nativeint byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type RecognizedWordGetBoundsFn = delegate of nativeint * RawTextBounds byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type RecognizedWordGetConfidenceFn = delegate of nativeint * single byref -> int

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type RecognizedWordGetTextFn = delegate of nativeint * nativeint byref -> int

    type RoScope private (shouldUninitialize: bool) =
        static member InitializeMta() =
            let hr = RoInitialize(RoInitType.MultiThreaded)
            match hr with
            | S_OK | S_FALSE -> Ok(new RoScope(true))
            | RPC_E_CHANGED_MODE -> Ok(new RoScope(false))
            | _ -> Error hr

        interface IDisposable with
            member _.Dispose() =
                if shouldUninitialize then
                    RoUninitialize()

    type HString private (handle: nativeint) =
        member _.Handle = handle

        static member Create(value: string) =
            let mutable handle = nativeint 0
            let hr = WindowsCreateString(value, uint32 value.Length, &handle)
            if hr <> S_OK then
                Error hr
            else
                Ok(new HString(handle))

        interface IDisposable with
            member _.Dispose() =
                if handle <> nativeint 0 then
                    WindowsDeleteString(handle) |> ignore

    let hstringToString handle =
        if handle = nativeint 0 then
            ""
        else
            let mutable length = 0u
            let buffer = WindowsGetStringRawBuffer(handle, &length)
            if buffer = nativeint 0 then "" else Marshal.PtrToStringUni(buffer, int length)

    type ComPtr(handle: nativeint) =
        member _.Handle = handle

        member _.Slot(slot: int) =
            let vtable = Marshal.ReadIntPtr(handle)
            Marshal.ReadIntPtr(vtable, slot * IntPtr.Size)

        member this.Release() =
            if handle <> nativeint 0 then
                let fn = Marshal.GetDelegateForFunctionPointer<ReleaseFn>(this.Slot(2))
                fn.Invoke(handle) |> ignore

        member this.QueryInterface(iid: Guid) =
            let fn = Marshal.GetDelegateForFunctionPointer<QueryInterfaceFn>(this.Slot(0))
            let mutable iid = iid
            let mutable ptr = nativeint 0
            let hr = fn.Invoke(handle, &iid, &ptr)
            if hr <> S_OK then Error hr else Ok(new ComPtr(ptr))

        interface IDisposable with
            member this.Dispose() = this.Release()

    let hresult hr = $"HRESULT 0x{uint32 hr:X8}"

    let activationFactory className iid =
        match HString.Create(className) with
        | Error hr -> Error hr
        | Ok hstring ->
            use hstring = hstring
            let mutable iid = iid
            let mutable factory = nativeint 0
            let hr = RoGetActivationFactory(hstring.Handle, &iid, &factory)
            if hr <> S_OK then
                Error hr
            else
                Ok(new ComPtr(factory))

module RawTextRecognizer =
    type ReadyState =
        | Ready = 0
        | NotReady = 1
        | NotSupportedOnCurrentSystem = 2
        | DisabledByUser = 3
        | CapabilityMissing = 4
        | NotCompatibleWithSystemHardware = 5
        | OSUpdateNeeded = 6

    let private textRecognizerClassName = "Microsoft.Windows.AI.Imaging.TextRecognizer"
    let private iidTextRecognizerStatics = Guid("3788C2FD-E496-53AB-85A7-E54A135824E9")

    let readyStateName value =
        match value with
        | 0 -> "Ready"
        | 1 -> "NotReady"
        | 2 -> "NotSupportedOnCurrentSystem"
        | 3 -> "DisabledByUser"
        | 4 -> "CapabilityMissing"
        | 5 -> "NotCompatibleWithSystemHardware"
        | 6 -> "OSUpdateNeeded"
        | other -> $"Unknown({other})"

    let getReadyStateRaw () =
        match RawWinRt.RoScope.InitializeMta() with
        | Error hr -> Error $"RoInitialize failed: {RawWinRt.hresult hr}"
        | Ok scope ->
            use scope = scope
            match RawWinRt.activationFactory textRecognizerClassName iidTextRecognizerStatics with
            | Error hr -> Error $"RoGetActivationFactory({textRecognizerClassName}) failed: {RawWinRt.hresult hr}"
            | Ok factory ->
                use factory = factory
                let fn =
                    Marshal.GetDelegateForFunctionPointer<RawWinRt.TextRecognizerGetReadyStateFn>(
                        factory.Slot(6))
                let mutable state = -1
                let hr = fn.Invoke(factory.Handle, &state)
                if hr <> RawWinRt.S_OK then
                    Error $"ITextRecognizerStatics.GetReadyState failed: {RawWinRt.hresult hr}"
                else
                    Ok state

    let private staticsFactory () =
        match RawWinRt.RoScope.InitializeMta() with
        | Error hr -> Error $"RoInitialize failed: {RawWinRt.hresult hr}"
        | Ok scope ->
            match RawWinRt.activationFactory textRecognizerClassName iidTextRecognizerStatics with
            | Error hr ->
                (scope :> IDisposable).Dispose()
                Error $"RoGetActivationFactory({textRecognizerClassName}) failed: {RawWinRt.hresult hr}"
            | Ok factory ->
                Ok(scope, factory)

    let private waitForAsyncPointerResult (operation: RawWinRt.ComPtr) (timeoutMs: int) =
        let getStatus = Marshal.GetDelegateForFunctionPointer<RawWinRt.AsyncInfoGetStatusFn>(operation.Slot(7))
        let getErrorCode = Marshal.GetDelegateForFunctionPointer<RawWinRt.AsyncInfoGetErrorCodeFn>(operation.Slot(8))
        let close = Marshal.GetDelegateForFunctionPointer<RawWinRt.AsyncInfoCloseFn>(operation.Slot(10))
        let getResults = Marshal.GetDelegateForFunctionPointer<RawWinRt.AsyncOperationGetResultsPtrFn>(operation.Slot(13))

        let started = DateTimeOffset.UtcNow
        let mutable completed = false
        let mutable status = 0
        while not completed do
            let hr = getStatus.Invoke(operation.Handle, &status)
            if hr <> RawWinRt.S_OK then
                completed <- true
                status <- -1
            elif status = 1 || status = 2 || status = 3 then
                completed <- true
            elif (DateTimeOffset.UtcNow - started).TotalMilliseconds > float timeoutMs then
                completed <- true
                status <- -2
            else
                Thread.Sleep(25)

        match status with
        | 1 ->
            let mutable result = nativeint 0
            let hr = getResults.Invoke(operation.Handle, &result)
            close.Invoke(operation.Handle) |> ignore
            if hr <> RawWinRt.S_OK then Error $"Async GetResults failed: {RawWinRt.hresult hr}"
            elif result = nativeint 0 then Error "Async GetResults returned null."
            else Ok(new RawWinRt.ComPtr(result))
        | 2 ->
            close.Invoke(operation.Handle) |> ignore
            Error "Async operation was canceled."
        | 3 ->
            let mutable errorCode = 0
            getErrorCode.Invoke(operation.Handle, &errorCode) |> ignore
            close.Invoke(operation.Handle) |> ignore
            Error $"Async operation failed: {RawWinRt.hresult errorCode}"
        | -2 ->
            close.Invoke(operation.Handle) |> ignore
            Error $"Async operation timed out after {timeoutMs}ms."
        | _ ->
            close.Invoke(operation.Handle) |> ignore
            Error $"Async operation ended in unexpected state {status}."

    let tryCreateRecognizerRaw timeoutMs =
        match staticsFactory() with
        | Error message -> Error message
        | Ok(scope, factory) ->
            use scope = scope
            use factory = factory
            let createAsync = Marshal.GetDelegateForFunctionPointer<RawWinRt.TextRecognizerCreateAsyncFn>(factory.Slot(8))
            let mutable opPtr = nativeint 0
            let hr = createAsync.Invoke(factory.Handle, &opPtr)
            if hr <> RawWinRt.S_OK then
                Error $"ITextRecognizerStatics.CreateAsync failed: {RawWinRt.hresult hr}"
            elif opPtr = nativeint 0 then
                Error "ITextRecognizerStatics.CreateAsync returned null."
            else
                use operation = new RawWinRt.ComPtr(opPtr)
                waitForAsyncPointerResult operation timeoutMs

    let tryEnsureReadyRaw timeoutMs =
        match staticsFactory() with
        | Error message -> Error message
        | Ok(scope, factory) ->
            use scope = scope
            use factory = factory
            let ensureReadyAsync = Marshal.GetDelegateForFunctionPointer<RawWinRt.TextRecognizerEnsureReadyAsyncFn>(factory.Slot(7))
            let mutable opPtr = nativeint 0
            let hr = ensureReadyAsync.Invoke(factory.Handle, &opPtr)
            if hr <> RawWinRt.S_OK then
                Error $"ITextRecognizerStatics.EnsureReadyAsync failed: {RawWinRt.hresult hr}"
            elif opPtr = nativeint 0 then
                Error "ITextRecognizerStatics.EnsureReadyAsync returned null."
            else
                use operation = new RawWinRt.ComPtr(opPtr)
                waitForAsyncPointerResult operation timeoutMs |> Result.map ignore

    let private rawPoint (point: RawWinRt.RawPoint) =
        { X = float point.X; Y = float point.Y }

    let private rawBounds (bounds: RawWinRt.RawTextBounds) =
        { TopLeft = rawPoint bounds.TopLeft
          TopRight = rawPoint bounds.TopRight
          BottomRight = rawPoint bounds.BottomRight
          BottomLeft = rawPoint bounds.BottomLeft }

    let private getHStringText (owner: RawWinRt.ComPtr) (slot: int) =
        let getter = Marshal.GetDelegateForFunctionPointer<RawWinRt.RecognizedWordGetTextFn>(owner.Slot(slot))
        let mutable hstring = nativeint 0
        let hr = getter.Invoke(owner.Handle, &hstring)
        if hr <> RawWinRt.S_OK then
            Error $"HSTRING getter at slot {slot} failed: {RawWinRt.hresult hr}"
        else
            try Ok(RawWinRt.hstringToString hstring)
            finally
                if hstring <> nativeint 0 then
                    RawWinRt.WindowsDeleteString(hstring) |> ignore

    let private getBounds (owner: RawWinRt.ComPtr) (slot: int) =
        let getter = Marshal.GetDelegateForFunctionPointer<RawWinRt.RecognizedWordGetBoundsFn>(owner.Slot(slot))
        let mutable bounds = Unchecked.defaultof<RawWinRt.RawTextBounds>
        let hr = getter.Invoke(owner.Handle, &bounds)
        if hr <> RawWinRt.S_OK then Error $"Bounds getter at slot {slot} failed: {RawWinRt.hresult hr}"
        else Ok(rawBounds bounds)

    let private getVectorItems (vector: RawWinRt.ComPtr) =
        let getAt = Marshal.GetDelegateForFunctionPointer<RawWinRt.VectorViewGetAtPtrFn>(vector.Slot(6))
        let getSize = Marshal.GetDelegateForFunctionPointer<RawWinRt.VectorViewGetSizeFn>(vector.Slot(7))
        let mutable size = 0u
        let hr = getSize.Invoke(vector.Handle, &size)
        if hr <> RawWinRt.S_OK then
            Error $"IVectorView.get_Size failed: {RawWinRt.hresult hr}"
        else
            let items =
                [| for index in 0u .. (if size = 0u then 0u else size - 1u) do
                       if size > 0u then
                           let mutable item = nativeint 0
                           let itemHr = getAt.Invoke(vector.Handle, index, &item)
                           if itemHr = RawWinRt.S_OK && item <> nativeint 0 then
                               yield new RawWinRt.ComPtr(item) |]
            Ok items

    let private mapWord (wordPtr: RawWinRt.ComPtr) =
        let text = getHStringText wordPtr 8 |> Result.defaultValue ""
        let confidence =
            let getter = Marshal.GetDelegateForFunctionPointer<RawWinRt.RecognizedWordGetConfidenceFn>(wordPtr.Slot(7))
            let mutable value = 0.0f
            if getter.Invoke(wordPtr.Handle, &value) = RawWinRt.S_OK then float value else 0.0
        let bounds =
            getBounds wordPtr 6
            |> Result.defaultValue { TopLeft = { X = 0.0; Y = 0.0 }; TopRight = { X = 0.0; Y = 0.0 }; BottomRight = { X = 0.0; Y = 0.0 }; BottomLeft = { X = 0.0; Y = 0.0 } }
        { Text = text; Confidence = confidence; BoundingBox = bounds }

    let private mapLine (linePtr: RawWinRt.ComPtr) =
        let text = getHStringText linePtr 9 |> Result.defaultValue ""
        let words =
            let getter = Marshal.GetDelegateForFunctionPointer<RawWinRt.RecognizedLineGetWordsFn>(linePtr.Slot(10))
            let mutable vectorPtr = nativeint 0
            if getter.Invoke(linePtr.Handle, &vectorPtr) <> RawWinRt.S_OK || vectorPtr = nativeint 0 then
                Array.empty
            else
                use vector = new RawWinRt.ComPtr(vectorPtr)
                match getVectorItems vector with
                | Error _ -> Array.empty
                | Ok items ->
                    items
                    |> Array.map (fun item ->
                        try mapWord item
                        finally (item :> IDisposable).Dispose())
        { Text = text; Words = words }

    let mapRecognizedTextPointer (recognizedTextPtr: RawWinRt.ComPtr) =
        let getter = Marshal.GetDelegateForFunctionPointer<RawWinRt.RecognizedTextGetLinesFn>(recognizedTextPtr.Slot(6))
        let mutable linesVectorPtr = nativeint 0
        let hr = getter.Invoke(recognizedTextPtr.Handle, &linesVectorPtr)
        if hr <> RawWinRt.S_OK then
            Error $"RecognizedText.Lines failed: {RawWinRt.hresult hr}"
        elif linesVectorPtr = nativeint 0 then
            Ok { Provider = "windows-ai/raw-com"; IsAvailable = true; Message = "OK"; Text = ""; Lines = Array.empty }
        else
            use linesVector = new RawWinRt.ComPtr(linesVectorPtr)
            match getVectorItems linesVector with
            | Error message -> Error message
            | Ok items ->
                let lines =
                    items
                    |> Array.map (fun item ->
                        try mapLine item
                        finally (item :> IDisposable).Dispose())
                let text = lines |> Array.map (fun line -> line.Text) |> String.concat Environment.NewLine
                Ok { Provider = "windows-ai/raw-com"; IsAvailable = true; Message = "OK"; Text = text; Lines = lines }
