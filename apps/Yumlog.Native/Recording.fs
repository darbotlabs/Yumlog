namespace Yumlog.Native

open System
open System.Diagnostics
open System.IO
open System.Text

module NativeRecording =
    let private candidatePaths (config: RecordConfig) =
        [ Some config.FFmpegPath
          Environment.GetEnvironmentVariable("FFMPEG_PATH") |> Option.ofObj
          Some(Path.Combine(Environment.CurrentDirectory, ".tools", "ffmpeg", "bin", "ffmpeg.exe"))
          Some(Path.Combine(AppContext.BaseDirectory, ".tools", "ffmpeg", "bin", "ffmpeg.exe"))
          Some "ffmpeg" ]
        |> List.choose id
        |> List.filter (fun value -> not (String.IsNullOrWhiteSpace(value)))

    let private resolveFfmpeg config =
        candidatePaths config
        |> List.tryFind (fun candidate -> candidate = "ffmpeg" || File.Exists(candidate))
        |> Option.defaultWith (fun () -> failwith "FFmpeg was not found. Set --ffmpeg or FFMPEG_PATH, or run the installer.")

    let record (config: RecordConfig) =
        let outFile = Path.GetFullPath(config.OutFile)
        match Path.GetDirectoryName(outFile) with
        | null | "" -> ()
        | parent -> Directory.CreateDirectory(parent) |> ignore

        let ffmpeg = resolveFfmpeg config
        let args =
            [| "-y"
               "-f"; "gdigrab"
               "-framerate"; string config.Fps
               "-t"; string config.DurationSec
               "-i"; "desktop"
               "-vf"; "pad=ceil(iw/2)*2:ceil(ih/2)*2"
               "-c:v"; config.Encoder
               "-preset"; config.Preset
               "-pix_fmt"; "yuv420p"
               outFile |]

        let info = ProcessStartInfo()
        info.FileName <- ffmpeg
        for arg in args do
            info.ArgumentList.Add(arg)
        info.UseShellExecute <- false
        info.RedirectStandardError <- true
        info.RedirectStandardOutput <- true
        info.CreateNoWindow <- true

        use proc = new Process()
        proc.StartInfo <- info

        let stderr = StringBuilder()
        proc.ErrorDataReceived.Add(fun e ->
            if not (isNull e.Data) then
                stderr.AppendLine(e.Data) |> ignore)

        if not (proc.Start()) then
            failwith "FFmpeg process did not start."

        proc.BeginErrorReadLine()
        proc.WaitForExit()

        let errorText = stderr.ToString()
        if proc.ExitCode <> 0 then
            failwith $"FFmpeg failed with exit code {proc.ExitCode}: {errorText}"

        { Path = outFile
          Fps = config.Fps
          DurationSec = config.DurationSec
          Encoder = config.Encoder
          ExitCode = proc.ExitCode
          StandardError = "" }
