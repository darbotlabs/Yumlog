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

    [<EntryPoint>]
    let main argv =
        try
            WinAppRuntime.tryInitialize() |> ignore
            let exitCode =
                if argv.Length = 0 && RuntimeIdentity.currentPackageFullName().IsSome then
                    NativeUi.run()
                    0
                else
                    argv |> Cli.parse |> execute
            WinAppRuntime.shutdown()
            exitCode
        with ex ->
            WinAppRuntime.shutdown()
            eprintfn "ERROR: %s" ex.Message
            1
