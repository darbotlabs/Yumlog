namespace Yumlog.Native

open System
open System.IO

module NativeConfig =
    let defaultConfigPath =
        Path.Combine("config", "yumlog.native.json")

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
              StopOnIdleSec = 0 } }

    let load path =
        if File.Exists(path) then
            NativeJson.read<AppConfig> path
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

