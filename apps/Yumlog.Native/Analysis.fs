namespace Yumlog.Native

open System.Drawing
open System.IO

module NativeAnalysis =
    let private imageExtensions =
        set [ ".png"; ".jpg"; ".jpeg"; ".bmp" ]

    let isImage (path: string) =
        match Path.GetExtension(path) with
        | null -> false
        | extension -> imageExtensions.Contains(extension.ToLowerInvariant())

    let analyzeImage (config: AnalyzeConfig) (path: string) =
        if not (File.Exists(path)) then
            invalidArg "path" $"Input file does not exist: {path}"

        use image = Image.FromFile(path)
        let info = FileInfo(path)
        let ocr = NativeOcr.recognize config.OcrMode path

        { Input = Path.GetFullPath(path)
          Kind = "image"
          Width = image.Width
          Height = image.Height
          SizeBytes = info.Length
          Sha256 = FileHash.sha256 path
          Ocr = ocr }

    let analyze (config: AnalyzeConfig) =
        let input = config.Input
        let files =
            if File.Exists(input) then
                [| input |]
            elif Directory.Exists(input) then
                Directory.EnumerateFiles(input)
                |> Seq.filter isImage
                |> Seq.toArray
            else
                invalidArg "Input" $"Analyze input does not exist: {input}"

        files |> Array.map (analyzeImage config)
