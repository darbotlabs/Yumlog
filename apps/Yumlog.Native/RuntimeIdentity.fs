namespace Yumlog.Native

open System
open System.Runtime.InteropServices
open System.Text

module RuntimeIdentity =
    [<Literal>]
    let private APPMODEL_ERROR_NO_PACKAGE = 15700

    [<DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)>]
    extern int GetCurrentPackageFullName(uint32& packageFullNameLength, StringBuilder packageFullName)

    let currentPackageFullName () =
        let mutable length = 0u
        let probe = GetCurrentPackageFullName(&length, Unchecked.defaultof<StringBuilder>)
        if probe = APPMODEL_ERROR_NO_PACKAGE then
            None
        elif probe <> 122 && probe <> 0 then
            None
        else
            let builder = StringBuilder(int length)
            let result = GetCurrentPackageFullName(&length, builder)
            if result = 0 then Some(builder.ToString()) else None

    let describeSystemAiCapabilityGate () =
        match currentPackageFullName() with
        | Some packageName ->
            $"Package identity is present ({packageName}). If TextRecognizer still returns CapabilityMissing, the package manifest likely lacks systemAIModels or MaxVersionTested is too low."
        | None ->
            "No package identity is present. Windows AI TextRecognizer can report CapabilityMissing until Yumlog.Native runs with MSIX/sparse-package identity declaring systemAIModels."
