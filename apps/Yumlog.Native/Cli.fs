namespace Yumlog.Native

open System

module Cli =
    let private usage =
        """
Yumlog.Native.Cli

Usage:
  Yumlog.Native.Cli capture [--config path] [--out-dir path] [--fps n] [--duration n] [--count n] [--primary]
  Yumlog.Native.Cli record [--config path] [--out-file path] [--fps n] [--duration n]
                           [--backend auto|nvenc|x265|ffmpeg] [--lossless true|false] [--preset name]
                           [--container mp4] [--x265 path] [--capture-backend wgc]
                           [--monitor-index n] [--disable-capture-border true|false]
                           [--timed-metadata-out path]
  Yumlog.Native.Cli analyze [--config path] [--input path] [--ocr none|auto|windows-ai|raw-com] [--json-out path]
  Yumlog.Native.Cli follow [--config path] [--out-dir path] [--fps n] [--duration n] [--change-threshold n] [--ocr mode]
  Yumlog.Native.Cli orchestrate --plan path
  Yumlog.Native.Cli config show [--config path]
  Yumlog.Native.Cli config init [--config path] [--force]
  Yumlog.Native.Cli selftest [--config path] [--sample-out path.mdkg]
  Yumlog.Native.Cli runbook --input path.mdkg [--json-out path] [--markdown-out path]
  Yumlog.Native.Cli reason --input path.mdkg [--question text] [--model dir]
                           [--provider cpu|auto|webgpu|dml|cuda] [--high-water 0..1]
                           [--max-tokens n] [--max-segments n] [--json-out path]
  Yumlog.Native.Cli watch [--duration n] [--fps n] [--change-threshold n] [--ocr none|auto]
                          [--narrate-every seconds] [--question text] [--out dir]
                          [--model dir] [--provider cpu|auto] [--high-water 0..1]
                          [--max-tokens n] [--json-out path]
  Yumlog.Native.Cli models
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
        | "selftest" :: args ->
            let configPath, options = parseOptions args
            SelfTest(configPath, options |> Map.tryFind "sample-out" |> Option.defaultValue "")
        | "runbook" :: args ->
            let _, options = parseOptions args
            let input =
                options
                |> Map.tryFind "input"
                |> Option.defaultWith (fun () -> invalidArg "input" "Missing --input path to a .mdkg recording.")
            let jsonOut = options |> Map.tryFind "json-out" |> Option.defaultValue ""
            let markdownOut = options |> Map.tryFind "markdown-out" |> Option.defaultValue ""
            Runbook(input, jsonOut, markdownOut)
        | "models" :: _ -> ListModels
        | "reason" :: args ->
            let _, options = parseOptions args

            let input =
                options
                |> Map.tryFind "input"
                |> Option.defaultWith (fun () -> invalidArg "input" "Missing --input path to a .mdkg recording.")

            let number key fallback =
                match options |> Map.tryFind key with
                | Some value ->
                    match Double.TryParse(value, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                    | true, parsed -> parsed
                    | _ -> invalidArg key $"Expected a number for --{key}, got '{value}'."
                | None -> fallback

            Reason
                { OnDeviceLlm.defaultOptions with
                    Input = input
                    ModelDir = options |> Map.tryFind "model" |> Option.defaultValue ""
                    Provider =
                        options
                        |> Map.tryFind "provider"
                        |> Option.defaultValue OnDeviceLlm.defaultOptions.Provider
                    Question =
                        options
                        |> Map.tryFind "question"
                        |> Option.defaultValue OnDeviceLlm.defaultOptions.Question
                    HighWaterPct = number "high-water" OnDeviceLlm.defaultOptions.HighWaterPct
                    MaxNewTokens = int (number "max-tokens" (float OnDeviceLlm.defaultOptions.MaxNewTokens))
                    MaxSegments = int (number "max-segments" (float OnDeviceLlm.defaultOptions.MaxSegments))
                    JsonOut = options |> Map.tryFind "json-out" |> Option.defaultValue "" }
        | "watch" :: args ->
            let _, options = parseOptions args

            let number key fallback =
                match options |> Map.tryFind key with
                | Some value ->
                    match Double.TryParse(value, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                    | true, parsed -> parsed
                    | _ -> invalidArg key $"Expected a number for --{key}, got '{value}'."
                | None -> fallback

            Watch
                { OutDir =
                    options
                    |> Map.tryFind "out"
                    |> Option.defaultValue (IO.Path.Combine(IO.Path.GetTempPath(), "yumlog-watch"))
                  Fps = int (number "fps" 1.0)
                  DurationSec = int (number "duration" 20.0)
                  ChangeThreshold = number "change-threshold" 0.001
                  OcrMode = options |> Map.tryFind "ocr" |> Option.defaultValue "none"
                  NarrateEverySec = number "narrate-every" 10.0
                  Question =
                    options
                    |> Map.tryFind "question"
                    |> Option.defaultValue "In one or two sentences, what is happening on screen right now?"
                  ModelDir = options |> Map.tryFind "model" |> Option.defaultValue ""
                  Provider = options |> Map.tryFind "provider" |> Option.defaultValue "cpu"
                  HighWaterPct = number "high-water" 0.6
                  MaxNewTokens = int (number "max-tokens" 96.0)
                  JsonOut = options |> Map.tryFind "json-out" |> Option.defaultValue "" }
        | command :: _ ->
            invalidArg "command" $"Unknown command '{command}'."
