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
              Encoder = "hevc"
              Preset = "medium"
              Backend = "auto"
              Codec = "hevc"
              Lossless = true
              Container = "mp4"
              X265Path = ""
              CaptureBackend = "wgc"
              MonitorIndex = 0
              DisableCaptureBorder = true
              TimedMetadataOut = "" }
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
            // Layered over defaults: a config file that omits the native encoder
            // fields must not null them out. See NativeJson.readOver.
            NativeJson.readOver defaults path
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

        let getBool name (fallback: bool) =
            match Map.tryFind name overrides with
            | None -> fallback
            | Some (value: string) ->
                match value.Trim().ToLowerInvariant() with
                | "" | "1" | "true" | "yes" | "on" -> true
                | "0" | "false" | "no" | "off" -> false
                | other -> invalidArg name $"Expected a boolean for --{name}, got '{other}'."

        /// The record backend is a superset of the HEVC encoder backends: it also
        /// accepts the legacy FFmpeg pipeline, which is not an in-process encoder.
        let normalizeRecordBackend (value: string) =
            match (orEmptyIfNull value).Trim().ToLowerInvariant() with
            | "" -> "auto"
            | "ffmpeg" | "legacy" -> "ffmpeg"
            | _ -> value |> HevcBackend.parse |> HevcBackend.toString

        let normalizeContainer (value: string) =
            match (orEmptyIfNull value).Trim().ToLowerInvariant() with
            | "" | "mp4" -> "mp4"
            | other -> invalidArg "container" $"Unsupported container '{other}'. Only mp4 is implemented."

        /// One Preset field feeds three encoders (NVENC, x265, FFmpeg), and with
        /// backend "auto" the encoder is not known until the adapter is probed. So the
        /// vocabulary is validated here, once, against the x264/x265 names that every
        /// backend understands - NVENC translates them to its own GUIDs internally.
        /// Accepting NVENC-only names such as "p7" here would produce a config that
        /// works on an NVENC machine and hard-fails on any other.
        let normalizePreset (value: string) =
            match (orEmptyIfNull value).Trim().ToLowerInvariant() with
            | "" -> "medium"
            | "ultrafast" | "superfast" | "veryfast" | "faster" | "fast" | "medium" | "slow" | "slower"
            | "veryslow" | "placebo" as ok -> ok
            | other ->
                invalidArg
                    "preset"
                    $"Unsupported preset '{other}'. Expected one of: ultrafast, superfast, veryfast, faster, fast, medium, slow, slower, veryslow, placebo."

        /// Read by nothing yet - WgcCapture is always used. Validated so that setting
        /// it to an unimplemented value fails loudly instead of being silently ignored.
        let normalizeCaptureBackend (value: string) =
            match (orEmptyIfNull value).Trim().ToLowerInvariant() with
            | "" | "wgc" -> "wgc"
            | other ->
                invalidArg "capture-backend" $"Unsupported capture backend '{other}'. Only wgc is implemented."

        let normalizeCodec (value: string) =
            match (orEmptyIfNull value).Trim().ToLowerInvariant() with
            | "" | "hevc" | "h265" -> "hevc"
            | other -> invalidArg "codec" $"Unsupported codec '{other}'. Only hevc is implemented."

        { baseConfig with
            OutFile =
                match overrides |> Map.tryFind "out" with
                | Some (value: string) when not (String.IsNullOrWhiteSpace(value)) -> value
                | _ -> get "out-file" baseConfig.OutFile
            Fps = get "fps" (string baseConfig.Fps) |> Int32.Parse
            DurationSec = get "duration" (string baseConfig.DurationSec) |> Int32.Parse
            FFmpegPath = get "ffmpeg" baseConfig.FFmpegPath
            Encoder = get "encoder" baseConfig.Encoder
            Preset = get "preset" baseConfig.Preset |> normalizePreset
            Backend = get "backend" baseConfig.Backend |> normalizeRecordBackend
            Codec = get "codec" baseConfig.Codec |> normalizeCodec
            Lossless = getBool "lossless" baseConfig.Lossless
            Container = get "container" baseConfig.Container |> normalizeContainer
            X265Path = get "x265" baseConfig.X265Path
            CaptureBackend = get "capture-backend" baseConfig.CaptureBackend |> normalizeCaptureBackend
            MonitorIndex = get "monitor-index" (string baseConfig.MonitorIndex) |> Int32.Parse
            DisableCaptureBorder = getBool "disable-capture-border" baseConfig.DisableCaptureBorder
            TimedMetadataOut = get "timed-metadata-out" baseConfig.TimedMetadataOut }

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

