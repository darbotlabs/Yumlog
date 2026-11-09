namespace Yumlog.Native

open System

module Cli =
    let private usage =
        """
Yumlog.Native

Usage:
  Yumlog.Native capture [--config path] [--out-dir path] [--fps n] [--duration n] [--count n] [--primary]
  Yumlog.Native record [--config path] [--out-file path] [--fps n] [--duration n] [--ffmpeg path]
  Yumlog.Native analyze [--config path] [--input path] [--ocr none|auto|windows-ai|raw-com] [--json-out path]
  Yumlog.Native handoff --input path --out-dir path [--ocr none|auto|windows-ai|raw-com] [--resume]
  Yumlog.Native follow [--config path] [--out-dir path] [--fps n] [--duration n] [--change-threshold n] [--ocr mode]
  Yumlog.Native orchestrate --plan path
  Yumlog.Native config show [--config path]
  Yumlog.Native config init [--config path] [--force]
  Yumlog.Native extensions list [--config path] [--dir path] [--kind kind] [--name text] [--no-builtin] [--no-external]
  Yumlog.Native extensions mcp [--config path] [--dir path] [--no-builtin] [--no-external]
  Yumlog.Native extensions validate [--config path] [--dir path] [--pattern glob]

Extension kinds:
  model, agent, tool, skill, workflow, connector, plugin, extension, mcp-server, api, continuous-ai
"""

    let helpText () = usage.Trim()

    let private parseOptions args =
        let rec loop remaining configPath options =
            match remaining with
            | [] -> configPath, options
            | "--config" :: value :: rest -> loop rest value options
            | "--force" :: rest -> loop rest configPath (Map.add "force" "true" options)
            | "--primary" :: rest -> loop rest configPath (Map.add "primary" "true" options)
            | "--no-builtin" :: rest -> loop rest configPath (Map.add "no-builtin" "true" options)
            | "--no-external" :: rest -> loop rest configPath (Map.add "no-external" "true" options)
            | "--plan" :: value :: rest -> loop rest configPath (Map.add "plan" value options)
            | option :: value :: rest when option.StartsWith("--") ->
                loop rest configPath (Map.add (option.Substring(2)) value options)
            | token :: _ -> invalidArg "args" $"Unexpected argument '{token}'."

        loop args NativeConfig.defaultConfigPath Map.empty

    let private parseHandoff args =
        let rec loop remaining options =
            match remaining with
            | [] -> options
            | "--resume" :: rest when not (Map.containsKey "resume" options) ->
                loop rest (Map.add "resume" "true" options)
            | key :: value :: rest
                when List.contains key [ "--input"; "--out-dir"; "--ocr" ]
                     && not (value.StartsWith("--"))
                     && not (Map.containsKey key options) ->
                loop rest (Map.add key value options)
            | token :: _ -> invalidArg "args" $"Unexpected, duplicate, or incomplete handoff option '{token}'."

        let options = loop args Map.empty
        let required key =
            options
            |> Map.tryFind key
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultWith (fun () -> invalidArg "args" $"Missing {key} path.")

        { Input = required "--input"
          OutDir = required "--out-dir"
          OcrMode = options |> Map.tryFind "--ocr" |> Option.defaultValue "none"
          Resume = Map.containsKey "resume" options }

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
        | "handoff" :: args ->
            Handoff(parseHandoff args)
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
        | "extensions" :: "mcp" :: args ->
            let configPath, options = parseOptions args
            let appConfig = NativeConfig.load configPath
            ExtensionsMcp(NativeConfig.mergeExtensions appConfig.Extensions options, configPath)
        | "extensions" :: "validate" :: args ->
            let configPath, options = parseOptions args
            let appConfig = NativeConfig.load configPath
            ExtensionsValidate(NativeConfig.mergeExtensions appConfig.Extensions options, configPath)
        | "extensions" :: "list" :: args
        | "extensions" :: "show" :: args
        | "extensions" :: args ->
            let configPath, options = parseOptions args
            let appConfig = NativeConfig.load configPath
            let get name =
                options |> Map.tryFind name |> Option.defaultValue ""
            let query: ExtensionQuery = { Kind = get "kind"; Name = get "name" }
            ExtensionsShow(NativeConfig.mergeExtensions appConfig.Extensions options, query, configPath)
        | command :: _ ->
            invalidArg "command" $"Unknown command '{command}'."
