namespace Yumlog.Native

module NativeNavigation =
    let private lower (value: string) =
        if System.String.IsNullOrWhiteSpace(value) then "" else value.Trim().ToLowerInvariant()

    let private fullBounds =
        { TopLeft = { X = 0.0; Y = 0.0 }
          TopRight = { X = 0.0; Y = 0.0 }
          BottomRight = { X = 0.0; Y = 0.0 }
          BottomLeft = { X = 0.0; Y = 0.0 } }

    let inferHints (ocr: OcrResult) =
        ocr.Lines
        |> Array.collect (fun line ->
            let text = lower line.Text
            let kind =
                if text.Contains("next") || text.Contains("continue") then Some "next"
                elif text.Contains("back") || text.Contains("previous") then Some "back"
                elif text.Contains("submit") || text.Contains("save") then Some "submit"
                elif text.Contains("cancel") || text.Contains("close") then Some "cancel"
                elif text.Contains("search") || text.Contains("find") then Some "search"
                else None

            match kind with
            | Some hintKind ->
                [| { Kind = hintKind
                     Label = line.Text
                     Confidence = 0.5
                     Bounds =
                        line.Words
                        |> Array.tryHead
                        |> Option.map (fun word -> word.BoundingBox)
                        |> Option.defaultValue fullBounds } |]
            | None -> Array.empty)
