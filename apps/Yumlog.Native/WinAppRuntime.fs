namespace Yumlog.Native

open System
open System.Runtime.InteropServices

[<AutoOpen>]
module private WinAppRuntimeNative =
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type PackageVersion =
        val Revision: uint16
        val Build: uint16
        val Minor: uint16
        val Major: uint16

        new(major: uint16, minor: uint16, build: uint16, revision: uint16) =
            { Major = major
              Minor = minor
              Build = build
              Revision = revision }

        static member Zero = PackageVersion(0us, 0us, 0us, 0us)

    [<Flags>]
    type BootstrapInitializeOptions =
        | None = 0
        | OnError_DebugBreak = 0x0001
        | OnError_DebugBreak_IfDebugger = 0x0002
        | OnError_FailFast = 0x0004
        | OnNoMatch_ShowUI = 0x0008
        | OnPackageIdentity_NOOP = 0x0010

    [<DllImport("Microsoft.WindowsAppRuntime.Bootstrap.dll",
                EntryPoint = "MddBootstrapInitialize2",
                CharSet = CharSet.Unicode,
                ExactSpelling = true)>]
    extern int MddBootstrapInitialize2(
        uint32 majorMinorVersion,
        string versionTag,
        PackageVersion packageVersion,
        BootstrapInitializeOptions options)

    [<DllImport("Microsoft.WindowsAppRuntime.Bootstrap.dll", ExactSpelling = true)>]
    extern void MddBootstrapShutdown()

module WinAppRuntime =
    [<Literal>]
    let private WindowsAppSdkMajorMinor = 0x00020002u

    let private gate = obj ()
    let mutable private state: Result<unit, int> option = None

    let tryInitialize () =
        match state with
        | Some result -> result
        | None ->
            lock gate (fun () ->
                match state with
                | Some result -> result
                | None ->
                    let result =
                        try
                            let hr =
                                MddBootstrapInitialize2(
                                    WindowsAppSdkMajorMinor,
                                    Unchecked.defaultof<string>,
                                    PackageVersion.Zero,
                                    BootstrapInitializeOptions.OnPackageIdentity_NOOP)

                            if hr >= 0 then Ok () else Error hr
                        with ex ->
                            Error ex.HResult

                    state <- Some result
                    result)

    let shutdown () =
        match state with
        | Some (Ok ()) -> MddBootstrapShutdown()
        | _ -> ()

    let hresultText hr =
        let uhr = uint32 hr
        match uhr with
        | 0x80070002u -> "Windows App Runtime 2.2 was not found. Install or repair Microsoft.WindowsAppRuntime 2.x."
        | 0x80070005u -> "Access denied. The Windows App SDK bootstrapper must run non-elevated."
        | 0x80073CF3u -> "Windows App Runtime package dependency resolution failed."
        | 0x80073D54u -> "Windows App Runtime package is not registered for this user."
        | 0x80131524u -> "Microsoft.WindowsAppRuntime.Bootstrap.dll was not found in the app output."
        | _ -> $"HRESULT 0x{uhr:X8}"
