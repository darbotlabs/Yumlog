namespace Yumlog.Native

open System.IO

module NativeOcr =
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

    let recognizeWindowsAi imagePath =
        if not (File.Exists(imagePath)) then
            invalidArg "imagePath" $"Image file does not exist: {imagePath}"

        match WinAppRuntime.tryInitialize() with
        | Error hr ->
            empty "windows-ai" false $"Windows App SDK bootstrap failed: {WinAppRuntime.hresultText hr}"
        | Ok () ->
            try
                WindowsAiOcr.recognize imagePath
            with ex ->
                empty "windows-ai" false $"Windows AI OCR failed: {ex.Message}"

    let recognize (mode: string) (imagePath: string) =
        match mode.Trim().ToLowerInvariant() with
        | "" | "none" | "off" ->
            empty "none" true "OCR disabled."
        | "windows-ai" ->
            recognizeWindowsAi imagePath
        | "raw-com" ->
            recognizeWindowsAi imagePath
        | "auto" ->
            let result = recognizeWindowsAi imagePath
            if result.IsAvailable then
                result
            else
                { result with Provider = "auto/windows-ai" }
        | other ->
            invalidArg "mode" $"Unsupported OCR mode '{other}'. Use none, auto, windows-ai, or raw-com."
