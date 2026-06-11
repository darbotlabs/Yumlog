namespace Yumlog.Native

open System

module Cli =
    let private usage =
        """
Yumlog.Native

Usage:
  Yumlog.Native capture [--config path] [--out-dir path] [--fps n] [--duration n] [--count n] [--primary]
  Yumlog.Native record [--config path] [--out-file path] [--fps n] [--duration n] [--ffmpeg path]
  Yumlog.Native analyze [--config path] [--input path] [--ocr none|auto|windows-ai] [--json-out path]
  Yumlog.Native follow [--config path] [--out-dir path] [--fps n] [--duration n] [--change-threshold n] [--ocr mode]
  Yumlog.Native orchestrate --plan path
  Yumlog.Native config show [--config path]
  Yumlog.Native config init [--config path] [--force]
"""

    let helpText () = usage.Trim()

    let private parseOptions args =
        let rec loop remaining configPath options =
            match remaining with
            | [] -> configPath, options
            | "--config" :: value :: rest -> loop rest value options
            | "--force" :: rest -> loop rest configPath (Map.add "force" "true" options)
            | "--primary" :: rest -> loop rest configPath (Map.add "primary" "true" options)
            | "--plan" :: value :: rest -> loop rest configPath (Map.add "plan" value options)
            | option :: value :: rest when option.StartsWith("--") ->
                loop rest configPath (Map.add (option.Substring(2)) value options)
            | token :: _ -> invalidArg "args" $"Unexpected argument '{token}'."

        loop args NativeConfig.defaultConfigPath Map.empty

    let parse argv =
        match Array.toList argv with
        | [] -> Help
        | "help" :: _ | "--help" :: _ | "-h" :: _ -> Help
        | "capture" :: args ->
            let configPath, options = parseOptions args
            let appConfig = NativeConfig.load configPath
            Capture(NativeConfig.mergeCapture appConfig.Capture options, configPath)
        | "record" :: args ->
            let configPath, options = parseOptions args
            let appConfig = NativeConfig.load configPath
            Record(NativeConfig.mergeRecord appConfig.Record options, configPath)
        | "analyze" :: args ->
            let configPath, options = parseOptions args
            let appConfig = NativeConfig.load configPath
            Analyze(NativeConfig.mergeAnalyze appConfig.Analyze options, configPath)
        | "follow" :: args ->
            let configPath, options = parseOptions args
            let appConfig = NativeConfig.load configPath
            Follow(NativeConfig.mergeFollow appConfig.Follow options, configPath)
        | "orchestrate" :: args ->
            let configPath, options = parseOptions args
            let plan =
                options
                |> Map.tryFind "plan"
                |> Option.defaultWith (fun () -> invalidArg "plan" "Missing --plan path.")
            Orchestrate(plan, configPath)
        | "config" :: "show" :: args ->
            let configPath, _ = parseOptions args
            ConfigShow configPath
        | "config" :: "init" :: args ->
            let configPath, options = parseOptions args
            ConfigInit(configPath, Map.containsKey "force" options)
        | command :: _ ->
            invalidArg "command" $"Unknown command '{command}'."

