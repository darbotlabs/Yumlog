namespace Yumlog.Native

open System
open System.IO

module NativeConfig =
    let defaultConfigPath =
        Path.Combine("config", "yumlog.native.json")

    let defaultExtensionsDir =
        Path.Combine("config", "extensions")

    let defaults =
        { Capture =
            { OutDir = "./screenshots"
              Fps = 1
              DurationSec = 1
              Count = 0
              AllScreens = true
              ImageFormat = "png" }
          Record =
            { OutFile = "./yumlogs/yumlog.mp4"
              Fps = 30
              DurationSec = 10
              FFmpegPath = ""
              Encoder = "libx264"
              Preset = "ultrafast" }
          Analyze =
            { Input = "./screenshots"
              OcrMode = "auto"
              JsonOut = ""
              MinConfidence = 0.5 }
          Follow =
            { OutDir = "./yumlogs/follow"
              Fps = 1
              DurationSec = 30
              ChangeThreshold = 0.05
              OcrMode = "auto"
              StopOnIdleSec = 0 }
          Extensions =
            { Dir = defaultExtensionsDir
              Enabled = true
              IncludeBuiltIn = true
              ManifestPattern = "*.extension.json" } }

    let private orDefault (value: 'T) (fallback: 'T) =
        if Object.ReferenceEquals(box value, null) then fallback else value

    let private textOrDefault (value: string) (fallback: string) =
        if String.IsNullOrWhiteSpace(value) then fallback else value

    /// Fills sections that are absent from older config files so newer sections stay optional.
    let normalize (config: AppConfig) =
        let extensions = orDefault config.Extensions defaults.Extensions

        { Capture = orDefault config.Capture defaults.Capture
          Record = orDefault config.Record defaults.Record
          Analyze = orDefault config.Analyze defaults.Analyze
          Follow = orDefault config.Follow defaults.Follow
          Extensions =
            { extensions with
                Dir = textOrDefault extensions.Dir defaults.Extensions.Dir
                ManifestPattern = textOrDefault extensions.ManifestPattern defaults.Extensions.ManifestPattern } }

    let load path =
        if File.Exists(path) then
            NativeJson.read<AppConfig> path |> normalize
        else
            defaults

    let save path config =
        NativeJson.write path config

    let mergeCapture (baseConfig: CaptureConfig) overrides =
        let get name fallback =
            overrides
            |> Map.tryFind name
            |> Option.defaultValue fallback

        { baseConfig with
            OutDir = get "out-dir" baseConfig.OutDir
            Fps = get "fps" (string baseConfig.Fps) |> Int32.Parse
            DurationSec = get "duration" (string baseConfig.DurationSec) |> Int32.Parse
            Count = get "count" (string baseConfig.Count) |> Int32.Parse
            AllScreens = not (Map.containsKey "primary" overrides)
            ImageFormat = get "format" baseConfig.ImageFormat }

    let mergeRecord (baseConfig: RecordConfig) overrides =
        let get name fallback =
            overrides
            |> Map.tryFind name
            |> Option.defaultValue fallback

        { baseConfig with
            OutFile = get "out-file" baseConfig.OutFile
            Fps = get "fps" (string baseConfig.Fps) |> Int32.Parse
            DurationSec = get "duration" (string baseConfig.DurationSec) |> Int32.Parse
            FFmpegPath = get "ffmpeg" baseConfig.FFmpegPath
            Encoder = get "encoder" baseConfig.Encoder
            Preset = get "preset" baseConfig.Preset }

    let mergeAnalyze (baseConfig: AnalyzeConfig) overrides =
        let get name fallback =
            overrides
            |> Map.tryFind name
            |> Option.defaultValue fallback

        { baseConfig with
            Input = get "input" baseConfig.Input
            OcrMode = get "ocr" baseConfig.OcrMode
            JsonOut = get "json-out" baseConfig.JsonOut
            MinConfidence = get "min-confidence" (string baseConfig.MinConfidence) |> Double.Parse }

    let mergeFollow (baseConfig: FollowConfig) overrides =
        let get name fallback =
            overrides
            |> Map.tryFind name
            |> Option.defaultValue fallback

        { baseConfig with
            OutDir = get "out-dir" baseConfig.OutDir
            Fps = get "fps" (string baseConfig.Fps) |> Int32.Parse
            DurationSec = get "duration" (string baseConfig.DurationSec) |> Int32.Parse
            ChangeThreshold = get "change-threshold" (string baseConfig.ChangeThreshold) |> Double.Parse
            OcrMode = get "ocr" baseConfig.OcrMode
            StopOnIdleSec = get "stop-on-idle" (string baseConfig.StopOnIdleSec) |> Int32.Parse }

    let mergeExtensions (baseConfig: ExtensionsConfig) overrides =
        let get name fallback =
            overrides
            |> Map.tryFind name
            |> Option.defaultValue fallback

        { baseConfig with
            Dir = get "dir" baseConfig.Dir
            ManifestPattern = get "pattern" baseConfig.ManifestPattern
            Enabled = baseConfig.Enabled && not (Map.containsKey "no-external" overrides)
            IncludeBuiltIn = baseConfig.IncludeBuiltIn && not (Map.containsKey "no-builtin" overrides) }

