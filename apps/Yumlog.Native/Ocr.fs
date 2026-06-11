namespace Yumlog.Native

open System
open System.IO

module NativeOcr =
    let empty provider available message =
        { Provider = provider
          IsAvailable = available
          Message = message
          Text = ""
          Lines = Array.empty }

    let private windowsAiTypeCandidates =
        [ "Microsoft.Windows.AI.Imaging.TextRecognizer, Microsoft.Windows.AI.Imaging"
          "Microsoft.Windows.AI.Imaging.TextRecognizer, Microsoft.WindowsAppSDK"
          "Microsoft.Windows.AI.Imaging.TextRecognizer" ]

    let windowsAiAvailable () =
        windowsAiTypeCandidates
        |> List.exists (fun typeName -> Type.GetType(typeName, false) |> isNull |> not)

    let recognizeWindowsAi imagePath =
        if not (File.Exists(imagePath)) then
            invalidArg "imagePath" $"Image file does not exist: {imagePath}"

        if windowsAiAvailable() then
            empty "windows-ai" true "Windows AI TextRecognizer projection is present. Runtime binding is reserved for the Windows App SDK OCR provider."
        else
            empty "windows-ai" false "Windows AI OCR is not available in this runtime. It requires the Windows App SDK AI imaging projection, a supported NPU, and model readiness via TextRecognizer.EnsureReadyAsync."

    let recognize (mode: string) (imagePath: string) =
        match mode.Trim().ToLowerInvariant() with
        | "" | "none" | "off" ->
            empty "none" true "OCR disabled."
        | "windows-ai" ->
            recognizeWindowsAi imagePath
        | "auto" ->
            let result = recognizeWindowsAi imagePath
            if result.IsAvailable then
                result
            else
                { result with Provider = "auto/windows-ai" }
        | other ->
            invalidArg "mode" $"Unsupported OCR mode '{other}'. Use none, auto, or windows-ai."
