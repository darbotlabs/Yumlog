namespace Yumlog.Native

open System
open System.IO
open Microsoft.Windows.AI
open Microsoft.Windows.AI.Imaging
open Microsoft.Graphics.Imaging
open Windows.Graphics.Imaging
open Windows.Storage
open Windows.Storage.Streams

/// Proper Windows native OCR using the managed Microsoft.WindowsAppSDK.AI projections.
/// Requires the Windows App SDK bootstrapper to be initialized first (WinAppRuntime.tryInitialize).
module WindowsAiOcr =

    let readyStateName (state: AIFeatureReadyState) =
        match state with
        | AIFeatureReadyState.Ready -> "Ready"
        | AIFeatureReadyState.NotReady -> "NotReady"
        | AIFeatureReadyState.NotSupportedOnCurrentSystem -> "NotSupportedOnCurrentSystem"
        | AIFeatureReadyState.DisabledByUser -> "DisabledByUser"
        | AIFeatureReadyState.CapabilityMissing -> "CapabilityMissing"
        | AIFeatureReadyState.NotCompatibleWithSystemHardware -> "NotCompatibleWithSystemHardware"
        | AIFeatureReadyState.OSUpdateNeeded -> "OSUpdateNeeded"
        | other -> sprintf "Unknown(%d)" (int other)

    let getReadyState () = TextRecognizer.GetReadyState()

    let isReady () =
        getReadyState () = AIFeatureReadyState.Ready

    let private runSync (task: System.Threading.Tasks.Task<'T>) =
        task.GetAwaiter().GetResult()

    let private describeUnavailable (state: AIFeatureReadyState) =
        match state with
        | AIFeatureReadyState.NotSupportedOnCurrentSystem ->
            "Windows AI TextRecognizer is not supported on this system. A Copilot+ PC/NPU-class device is required."
        | AIFeatureReadyState.DisabledByUser ->
            "Windows AI TextRecognizer is disabled by the user or policy."
        | AIFeatureReadyState.CapabilityMissing ->
            sprintf "Windows AI TextRecognizer reports CapabilityMissing. %s" (RuntimeIdentity.describeSystemAiCapabilityGate())
        | AIFeatureReadyState.NotCompatibleWithSystemHardware ->
            "Windows AI TextRecognizer reports NotCompatibleWithSystemHardware. The device hardware or driver stack is insufficient."
        | AIFeatureReadyState.OSUpdateNeeded ->
            "Windows AI TextRecognizer reports OSUpdateNeeded."
        | other ->
            sprintf "Windows AI TextRecognizer is not ready (%s)." (readyStateName other)

    /// Ensures the on-device OCR model is provisioned. Downloads it via EnsureReadyAsync when NotReady.
    /// Returns Ok when the model is Ready, otherwise an explanatory Error.
    let ensureReady () : Result<unit, string> =
        match getReadyState () with
        | AIFeatureReadyState.Ready -> Ok ()
        | AIFeatureReadyState.NotReady ->
            try
                let result = runSync (TextRecognizer.EnsureReadyAsync().AsTask())
                if result.Status = AIFeatureReadyResultState.Success then
                    Ok ()
                else
                    let detail =
                        if String.IsNullOrWhiteSpace(result.ErrorDisplayText) then ""
                        else " " + result.ErrorDisplayText
                    let hresultOf (ex: exn) =
                        if isNull (box ex) then "" else sprintf " 0x%08X: %s" ex.HResult ex.Message
                    let extended =
                        let e = hresultOf result.ExtendedError
                        if e = "" then "" else " ExtendedError:" + e
                    let inner =
                        let e = hresultOf result.Error
                        if e = "" then "" else " Error:" + e
                    Error(
                        sprintf
                            "EnsureReadyAsync did not succeed (Status=%s, PackageInstallationFailed=%b).%s%s%s"
                            (string result.Status)
                            result.PackageInstallationFailed
                            detail
                            extended
                            inner)
            with ex ->
                Error(sprintf "EnsureReadyAsync threw: %s" ex.Message)
        | other -> Error(describeUnavailable other)

    let private toPoint (p: Windows.Foundation.Point) : TextPoint =
        { X = float p.X; Y = float p.Y }

    let private toBounds (b: RecognizedTextBoundingBox) : TextBounds =
        { TopLeft = toPoint b.TopLeft
          TopRight = toPoint b.TopRight
          BottomRight = toPoint b.BottomRight
          BottomLeft = toPoint b.BottomLeft }

    let private loadImageBuffer (imagePath: string) : ImageBuffer =
        let full = Path.GetFullPath(imagePath)
        let file = runSync (StorageFile.GetFileFromPathAsync(full).AsTask())
        use stream = runSync (file.OpenAsync(FileAccessMode.Read).AsTask())
        let decoder = runSync (BitmapDecoder.CreateAsync(stream).AsTask())
        use bitmap =
            runSync (decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask())
        ImageBuffer.CreateForSoftwareBitmap(bitmap)

    /// Full OCR pipeline: ensure the model, create a recognizer, decode the image,
    /// run recognition, and project results into the native OcrResult shape.
    let recognize (imagePath: string) : OcrResult =
        match ensureReady () with
        | Error message ->
            { Provider = "windows-ai"
              IsAvailable = false
              Message = message
              Text = ""
              Lines = Array.empty }
        | Ok () ->
            use recognizer = runSync (TextRecognizer.CreateAsync().AsTask())
            use imageBuffer = loadImageBuffer imagePath
            let recognized = recognizer.RecognizeTextFromImage(imageBuffer)
            let lines =
                recognized.Lines
                |> Array.map (fun line ->
                    let words =
                        line.Words
                        |> Array.map (fun word ->
                            { Text = word.Text
                              Confidence = float word.MatchConfidence
                              BoundingBox = toBounds word.BoundingBox })
                    { Text = line.Text; Words = words })
            let text =
                lines
                |> Array.map (fun line -> line.Text)
                |> String.concat Environment.NewLine
            { Provider = "windows-ai"
              IsAvailable = true
              Message = "OK"
              Text = text
              Lines = lines }
