namespace Yumlog.Native

open System.IO

module NativeOcr =
    let readiness (mode: string) : OcrReadiness =
        match mode.Trim().ToLowerInvariant() with
        | "none" -> { Ready = true; Code = "ocr-disabled"; Message = "OCR disabled; no model required." }
        | "auto" | "windows-ai" | "raw-com" ->
            match WinAppRuntime.tryInitialize() with
            | Error hr ->
                { Ready = false; Code = "runtime-unavailable"; Message = WinAppRuntime.hresultText hr }
            | Ok () ->
                try
                    let state = WindowsAiOcr.getReadyState()
                    let name = WindowsAiOcr.readyStateName state
                    let ready = name = "Ready"
                    { Ready = ready
                      Code = if ready then "ready" else name
                      Message = $"Windows AI OCR readiness: {name}. This probe never provisions models." }
                with ex ->
                    { Ready = false; Code = "probe-failed"; Message = ex.Message }
        | other -> invalidArg "mode" $"Unsupported OCR mode '{other}'. Use none, auto, windows-ai, or raw-com."

    let empty provider available message =
        { Provider = provider
          IsAvailable = available
          Message = message
          Text = ""
          Lines = Array.empty }

    let windowsAiAvailable () =
        match WinAppRuntime.tryInitialize() with
        | Error _ -> false
        | Ok () ->
            try WindowsAiOcr.isReady()
            with _ -> false

    let private recognizeWindowsAiWith recognizer imagePath =
        if not (File.Exists(imagePath)) then
            invalidArg "imagePath" $"Image file does not exist: {imagePath}"

        match WinAppRuntime.tryInitialize() with
        | Error hr ->
            empty "windows-ai" false $"Windows App SDK bootstrap failed: {WinAppRuntime.hresultText hr}"
        | Ok () ->
            try
                recognizer imagePath
            with ex ->
                empty "windows-ai" false $"Windows AI OCR failed: {ex.Message}"

    let recognizeWindowsAi imagePath =
        recognizeWindowsAiWith WindowsAiOcr.recognize imagePath

    let private recognizeWith windowsRecognizer (mode: string) (imagePath: string) =
        match mode.Trim().ToLowerInvariant() with
        | "" | "none" | "off" ->
            empty "none" true "OCR disabled."
        | "windows-ai" ->
            windowsRecognizer imagePath
        | "raw-com" ->
            windowsRecognizer imagePath
        | "auto" ->
            let result = windowsRecognizer imagePath
            if result.IsAvailable then
                result
            else
                { result with Provider = "auto/windows-ai" }
        | other ->
            invalidArg "mode" $"Unsupported OCR mode '{other}'. Use none, auto, windows-ai, or raw-com."

    let recognize mode imagePath =
        recognizeWith recognizeWindowsAi mode imagePath

    let recognizeWithoutProvisioning mode imagePath =
        recognizeWith (recognizeWindowsAiWith WindowsAiOcr.recognizeWithoutProvisioning) mode imagePath
