namespace Yumlog.Native

open System
open System.IO
open System.Reflection
open System.Text.Json

/// Portable, immutable image-analysis sessions; no capture or MP4 muxing.
module NativeHandoff =
    [<Literal>]
    let Schema = "urn:yumlog:handoff:v1"

    let private outputs = [| "analysis", "analysis.json"; "mdkg", "session.mdkg" |]

    let private fail message = invalidOp $"Invalid handoff: {message}"

    let private rejectReparsePoint (path: string) =
        if File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) then
            fail $"Links/reparse points are not supported: {path}"

    let rec private checkAncestors (path: string) =
        if File.Exists(path) || Directory.Exists(path) then rejectReparsePoint path
        let parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) |> Option.ofObj |> Option.defaultValue ""
        if not (String.IsNullOrEmpty(parent)) && parent <> path then checkAncestors parent

    let private fileInfo role relativePath fullPath : HandoffFile =
        rejectReparsePoint fullPath
        { Role = role
          Path = relativePath
          Sha256 = FileHash.sha256 fullPath
          SizeBytes = FileInfo(fullPath).Length }

    let private sourceFiles input =
        checkAncestors input
        let files =
            if File.Exists(input) then
                if not (NativeAnalysis.isImage input) then
                    invalidArg "input" "Expected a PNG, JPEG, or BMP image."
                [| input |]
            elif Directory.Exists(input) then
                Directory.GetFiles(input)
                |> Array.filter NativeAnalysis.isImage
                |> Array.sortWith (fun a b -> StringComparer.Ordinal.Compare(Path.GetFileName(a), Path.GetFileName(b)))
            else
                invalidArg "input" $"Input does not exist: {input}"

        if files.Length = 0 then invalidArg "input" "Input directory contains no supported images."
        files |> Array.iter rejectReparsePoint
        files

    let private snapshot input =
        sourceFiles input
        |> Array.mapi (fun index path ->
            let extension = Path.GetExtension(path) |> Option.ofObj |> Option.defaultValue ""
            let relative = $"frames/{index + 1:D6}{extension.ToLowerInvariant()}"
            let info = fileInfo "source" relative path
            path,
            { SourcePath = Path.GetFileName(path) |> Option.ofObj |> Option.defaultValue ""
              Path = info.Path
              Sha256 = info.Sha256
              SizeBytes = info.SizeBytes })

    let private matchesSources (expected: HandoffSource array) (actual: HandoffSource array) =
        expected = actual

    let private checkMembers names (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then fail "Expected a JSON object."
        let actual = element.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.toArray
        if actual.Length <> Array.length names || Set.ofArray actual <> Set.ofArray names then
            fail "Manifest has missing, duplicate, or unknown properties."

    let private readManifest path =
        let json = File.ReadAllText(path)
        use document = JsonDocument.Parse(json)
        let root = document.RootElement
        checkMembers
            [| "schema"; "schemaVersion"; "sessionId"; "producer"; "createdUtc"; "completedUtc"
               "analysisPath"; "mdkgPath"; "options"; "sources"; "files" |] root
        checkMembers [| "name"; "version" |] (root.GetProperty("producer"))
        checkMembers [| "ocrMode" |] (root.GetProperty("options"))
        for source in root.GetProperty("sources").EnumerateArray() do
            checkMembers [| "sourcePath"; "path"; "sha256"; "sizeBytes" |] source
        for output in root.GetProperty("files").EnumerateArray() do
            checkMembers [| "role"; "path"; "sha256"; "sizeBytes" |] output
        JsonSerializer.Deserialize<HandoffManifest>(json, NativeJson.options)
        |> Option.ofObj
        |> Option.defaultWith (fun () -> fail "Manifest is null.")

    let private checkInventory directory expected =
        rejectReparsePoint directory
        let entries = Directory.GetFileSystemEntries(directory)
        entries |> Array.iter rejectReparsePoint
        let names = entries |> Array.map Path.GetFileName
        if names.Length <> Array.length expected || Set.ofArray names <> Set.ofArray expected then
            fail $"Unexpected or missing session files in {directory}."

    let private verifyFile directory (expected: HandoffFile) =
        // Paths have already been matched against the fixed, generated inventory.
        let path = Path.Combine(directory, expected.Path.Replace('/', Path.DirectorySeparatorChar))
        if not (File.Exists(path)) then fail $"Missing file: {expected.Path}"
        if expected <> fileInfo expected.Role expected.Path path then fail $"Hash or byte length mismatch: {expected.Path}"

    let private verify directory (options: HandoffOptions) (sources: HandoffSource array) =
        checkAncestors directory
        checkInventory directory [| "frames"; "analysis.json"; "session.mdkg"; "handoff.json"; "handoff.sha256" |]
        let manifestPath = Path.Combine(directory, "handoff.json")
        let checksum = File.ReadAllText(Path.Combine(directory, "handoff.sha256")).Trim()
        if checksum <> FileHash.sha256 manifestPath then fail "Manifest checksum mismatch."
        let manifest = readManifest manifestPath
        if manifest.Schema <> Schema || manifest.SchemaVersion <> "1.0" then fail "Unsupported manifest schema."
        match Guid.TryParseExact(manifest.SessionId, "D") with
        | true, id when id <> Guid.Empty -> ()
        | _ -> fail "Invalid session ID."
        if manifest.AnalysisPath <> "analysis.json" || manifest.MdkgPath <> "session.mdkg" then
            fail "Invalid analysis or MDKG path."
        if manifest.Producer.Name <> "Yumlog.Native" || String.IsNullOrWhiteSpace(manifest.Producer.Version) then
            fail "Missing or unsupported producer."
        if manifest.CreatedUtc = DateTimeOffset.MinValue || manifest.CompletedUtc < manifest.CreatedUtc then
            fail "Invalid session timestamps."
        if manifest.Options <> options then fail "Requested options differ from the existing session."
        if not (matchesSources sources manifest.Sources) then fail "Input inventory or content differs from the existing session."
        let expectedInventory =
            Array.append (sources |> Array.map (fun source -> "source", source.Path)) outputs
        if manifest.Files.Length <> expectedInventory.Length
           || Array.map (fun (item: HandoffFile) -> item.Role, item.Path) manifest.Files <> expectedInventory then
            fail "Invalid output file inventory."
        let framesDirectory = Path.Combine(directory, "frames")
        checkInventory framesDirectory (sources |> Array.map (fun source -> Path.GetFileName(source.Path)))
        for source in sources do
            verifyFile directory { Role = "source"; Path = source.Path; Sha256 = source.Sha256; SizeBytes = source.SizeBytes }
        manifest.Files |> Array.iter (verifyFile directory)
        manifest

    let private writeMdkg path (results: AnalysisResult array) =
        use writer = Mdkg.MdkgWriter.Open(path)
        writer.AddDefaultLayers()
        results
        |> Array.iteri (fun index result ->
            // A synthetic 1 Hz sequence orders still images; this is not a capture clock.
            let pts = int64 index * Mdkg.MovieTimescale
            writer.WriteTiming(
                { Qpc = 0L
                  FrameIndex = index
                  Fps = 1
                  PtsTicks = pts
                  DurationTicks = Mdkg.MovieTimescale
                  GapFromPreviousTicks = 0L
                  IsDropped = false
                  DropCount = 0
                  Notes = $"Still-image handoff; synthetic timeline; {result.Input}" })
            writer.WriteOcrText(Mdkg.Producers.ocrTextLayerSample 0L index pts Mdkg.MovieTimescale result.Ocr)
            writer.WriteUiAffordance(
                Mdkg.Producers.uiAffordanceLayerSample 0L index pts Mdkg.MovieTimescale
                    (NativeNavigation.inferHints result.Ocr)))
        writer.Close()

    let run (config: HandoffConfig) =
        if String.IsNullOrWhiteSpace(config.Input) || String.IsNullOrWhiteSpace(config.OutDir) then
            invalidArg "config" "Input and output directory are required."
        let mode = if String.IsNullOrWhiteSpace(config.OcrMode) then "none" else config.OcrMode.Trim().ToLowerInvariant()
        if not (List.contains mode [ "none"; "auto"; "windows-ai"; "raw-com" ]) then
            invalidArg "ocr" "Use none, auto, windows-ai, or raw-com."
        let options: HandoffOptions = { OcrMode = mode }
        let input = Path.GetFullPath(config.Input)
        let target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.OutDir))
        checkAncestors target
        let isWithin (parent: string) (child: string) =
            child.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        if isWithin target input || (Directory.Exists(input) && isWithin input target) then
            invalidArg "out-dir" "Input and session output paths must not overlap."
        let inputs = snapshot input
        let sources = inputs |> Array.map snd
        if File.Exists(target) then fail "Output path is an existing file."
        if Directory.Exists(target) then
            if not config.Resume then fail "Output directory already exists; use --resume to verify it."
            verify target options sources
        else
            let parent = Path.GetDirectoryName(target) |> Option.ofObj |> Option.defaultValue ""
            if String.IsNullOrEmpty(parent) then invalidArg "out-dir" "A session directory below a parent is required."
            Directory.CreateDirectory(parent) |> ignore
            checkAncestors parent
            let stage = Path.Combine(parent, $".{Path.GetFileName(target)}.staging-{Guid.NewGuid():N}")
            Directory.CreateDirectory(stage) |> ignore
            let created = DateTimeOffset.UtcNow
            try
                Directory.CreateDirectory(Path.Combine(stage, "frames")) |> ignore
                let results =
                    inputs
                    |> Array.map (fun (sourcePath, source) ->
                        let copiedPath = Path.Combine(stage, source.Path.Replace('/', Path.DirectorySeparatorChar))
                        File.Copy(sourcePath, copiedPath, false)
                        verifyFile stage { Role = "source"; Path = source.Path; Sha256 = source.Sha256; SizeBytes = source.SizeBytes }
                        let analysisConfig = { NativeConfig.defaults.Analyze with Input = copiedPath; OcrMode = mode }
                        let result = NativeAnalysis.analyze analysisConfig |> Array.exactlyOne
                        { result with Input = source.Path })
                NativeJson.write (Path.Combine(stage, "analysis.json")) results
                writeMdkg (Path.Combine(stage, "session.mdkg")) results
                let assembly = Assembly.GetExecutingAssembly()
                let manifest =
                    { Schema = Schema
                      SchemaVersion = "1.0"
                      SessionId = Guid.NewGuid().ToString("D")
                      Producer =
                          { Name = "Yumlog.Native"
                            Version = assembly.GetName().Version |> Option.ofObj |> Option.map string |> Option.defaultValue "unknown" }
                      CreatedUtc = created
                      CompletedUtc = DateTimeOffset.UtcNow
                      AnalysisPath = "analysis.json"
                      MdkgPath = "session.mdkg"
                      Options = options
                      Sources = sources
                      Files =
                          Array.append
                              (sources |> Array.map (fun source ->
                                  { Role = "source"; Path = source.Path; Sha256 = source.Sha256; SizeBytes = source.SizeBytes }))
                              (outputs |> Array.map (fun (role, name) -> fileInfo role name (Path.Combine(stage, name)))) }
                let manifestPath = Path.Combine(stage, "handoff.json")
                NativeJson.write manifestPath manifest
                File.WriteAllText(Path.Combine(stage, "handoff.sha256"), FileHash.sha256 manifestPath + Environment.NewLine)
                verify stage options sources |> ignore
                if not (matchesSources sources (snapshot input |> Array.map snd)) then
                    fail "Inputs changed during handoff creation."
                // Same-parent rename publishes only a completed session, and never overwrites a target.
                Directory.Move(stage, target)
                manifest
            with ex ->
                raise (InvalidOperationException($"Handoff was not published. Staging retained at '{stage}': {ex.Message}", ex))
