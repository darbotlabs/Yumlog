namespace Yumlog.Native

open System
open System.IO

module Program =
    let private writeJson value =
        printfn "%s" (NativeJson.serialize value)

    let private writeJsonFile path value =
        NativeJson.write path value
        printfn "Wrote %s" (Path.GetFullPath(path))

    let private execute command =
        match command with
        | Help ->
            printfn "%s" (Cli.helpText())
            0
        | ConfigShow configPath ->
            NativeConfig.load configPath |> writeJson
            0
        | ConfigInit(configPath, force) ->
            if File.Exists(configPath) && not force then
                failwith $"Config already exists: {configPath}. Pass --force to overwrite."
            NativeConfig.save configPath NativeConfig.defaults
            printfn "Initialized %s" (Path.GetFullPath(configPath))
            0
        | Capture(config, _) ->
            let frames = NativeCapture.capture config
            writeJson frames
            0
        | Record(config, _) ->
            NativeRecording.record config |> writeJson
            0
        | Analyze(config, _) ->
            let results = NativeAnalysis.analyze config
            if String.IsNullOrWhiteSpace(config.JsonOut) then
                writeJson results
            else
                writeJsonFile config.JsonOut results
            0
        | Follow(config, _) ->
            let manifest = NativeFollow.follow config
            let manifestPath = Path.Combine(config.OutDir, "follow-manifest.json")
            writeJsonFile manifestPath manifest
            0
        | Orchestrate(planPath, _) ->
            NativeOrchestration.runPlan planPath |> writeJson
            0
        | SelfTest(_, sampleOut) ->
            let report = NativeSelfTest.run sampleOut
            for case in report.Cases do
                printfn "%s %s :: %s" (if case.Passed then "PASS" else "FAIL") case.Name case.Detail
            printfn ""
            writeJson report
            if report.Passed then 0 else 1
        | Runbook(input, jsonOut, markdownOut) ->
            if not (File.Exists(input)) then
                failwith $"Recording does not exist: {input}"

            use reader = Mdkg.openReader input
            let runbook = MdkgIntel.buildRunbook reader
            let verification = MdkgIntel.verifyRunbook reader runbook

            if not (String.IsNullOrWhiteSpace(markdownOut)) then
                File.WriteAllText(markdownOut, MdkgIntel.toMarkdown runbook)
                printfn "Wrote %s" (Path.GetFullPath(markdownOut))

            if String.IsNullOrWhiteSpace(jsonOut) then
                writeJson runbook
            else
                writeJsonFile jsonOut runbook

            eprintfn
                "Proof verification: %d/%d resolved, %d drifted"
                verification.ResolvedProofs
                verification.TotalProofs
                verification.MismatchedProofs

            if verification.Verified then 0 else 2
        | ListModels ->
            let models = OnDeviceLlm.discoverAll()

            if models.Length = 0 then
                eprintfn "No on-device models found. Set YUMLOG_LLM_MODEL_DIR or pass --model to 'reason'."

            writeJson models
            if models.Length = 0 then 3 else 0
        | Reason config ->
            if not (File.Exists(config.Input)) then
                failwith $"Recording does not exist: {config.Input}"

            use reader = Mdkg.openReader config.Input
            let runbook = MdkgIntel.buildRunbook reader

            // Stream to stderr so --json-out (and stdout piping) stay machine readable
            // while the operator still sees tokens arrive live.
            let onPiece (piece: string) =
                if String.IsNullOrWhiteSpace(config.JsonOut) then
                    eprintf "%s" piece

            let result = OnDeviceLlm.reasonOverRunbook config runbook onPiece

            if String.IsNullOrWhiteSpace(config.JsonOut) then
                eprintfn ""
                writeJson result
            else
                writeJsonFile config.JsonOut result

            eprintfn
                "Reasoned over %d segments on %s in %dms (%d swaps, %d/%d tokens, %.2f tok/s)"
                result.Segments
                result.Provider
                result.TotalMs
                result.SwapCount
                result.ContextTokens
                result.ContextLimit
                result.TokensPerSecond

            0
        | Watch config ->
            let onPiece (piece: string) =
                if String.IsNullOrWhiteSpace(config.JsonOut) then
                    eprintf "%s" piece

            // Live progress on stderr: without it a long watch looks hung.
            let onTick frames atSeconds score ingested =
                eprintf "\rframe %d  t=%.1fs  change=%.0f%%  %s   " frames atSeconds (score * 100.0) (if ingested then "INGESTED" else "skipped")

            let result = OnDeviceLlm.watchScreen config onPiece onTick
            eprintfn ""

            if not result.OcrAvailable then
                eprintfn
                    "NOTE: screen text is not being read, so narration can only describe WHEN and HOW MUCH the screen changed."

            if String.IsNullOrWhiteSpace(config.JsonOut) then
                writeJson result
            else
                writeJsonFile config.JsonOut result

            eprintfn
                "Watched %.1fs on %s: %d frames, %d changes ingested, %d narrations, %d swaps, %d/%d tokens"
                result.WatchedSeconds
                result.Provider
                result.Frames
                result.Changes
                result.NarrationCount
                result.SwapCount
                result.ContextTokens
                result.ContextLimit

            0

    /// The Windows App SDK bootstrapper loads its own onnxruntime.dll (1.24.6) into the
    /// process. ONNX Runtime GenAI needs a newer ORT ABI, and whichever onnxruntime.dll
    /// is already resident wins, so initialising the SDK for a reasoning command breaks
    /// the model load with a DllNotFoundException. These commands need no WinRT surface,
    /// so they simply skip the bootstrapper.
    ///
    /// Watch is included even though --ocr auto wants WinRT: OCR initialises the SDK
    /// lazily, which is the only ordering where the model is already resident.
    let private needsWindowsAppSdk command =
        match command with
        | Reason _
        | Watch _
        | ListModels
        | Help -> false
        | _ -> true

    [<EntryPoint>]
    let main argv =
        let mutable initialized = false

        let shutdown () =
            if initialized then
                initialized <- false
                WinAppRuntime.shutdown()

        try
            let command = argv |> Cli.parse

            if needsWindowsAppSdk command then
                WinAppRuntime.tryInitialize() |> ignore
                initialized <- true

            let exitCode = execute command
            shutdown ()
            exitCode
        with ex ->
            shutdown ()
            // Always name the exception type - a bare "Object reference not set to an
            // instance of an object." is undiagnosable. Full stack behind YUMLOG_DEBUG=1.
            eprintfn "ERROR: %s: %s" (ex.GetType().Name) ex.Message

            if not (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable "YUMLOG_DEBUG")) then
                eprintfn "%s" (ex.ToString())

            1
