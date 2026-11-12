namespace Yumlog.Native

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open Yumlog.Native.Mdkg

module NativeRecording =
    /// Walks up from a starting directory looking for a `.tools` folder, then
    /// searches it for ffmpeg.exe. Installers nest the extracted archive under
    /// a build-specific directory, so the exact depth is not knowable up front.
    let private searchToolsDirectories (startDir: string) =
        let rec ascend (dir: DirectoryInfo option) depth =
            match dir with
            | Some d when depth < 8 ->
                let tools = Path.Combine(d.FullName, ".tools")

                let found =
                    if Directory.Exists(tools) then
                        try
                            Directory.EnumerateFiles(tools, "ffmpeg.exe", SearchOption.AllDirectories)
                            |> Seq.tryHead
                        with _ ->
                            None
                    else
                        None

                match found with
                | Some hit -> Some hit
                | None -> ascend (Option.ofObj d.Parent) (depth + 1)
            | _ -> None

        if String.IsNullOrWhiteSpace(startDir) || not (Directory.Exists(startDir)) then
            None
        else
            ascend (Some(DirectoryInfo(startDir))) 0

    let private candidatePaths (config: RecordConfig) =
        [ Some config.FFmpegPath
          Environment.GetEnvironmentVariable("FFMPEG_PATH") |> Option.ofObj
          Some(Path.Combine(Environment.CurrentDirectory, ".tools", "ffmpeg", "bin", "ffmpeg.exe"))
          Some(Path.Combine(AppContext.BaseDirectory, ".tools", "ffmpeg", "bin", "ffmpeg.exe"))
          searchToolsDirectories Environment.CurrentDirectory
          searchToolsDirectories AppContext.BaseDirectory
          Some "ffmpeg" ]
        |> List.choose id
        |> List.filter (fun value -> not (String.IsNullOrWhiteSpace(value)))

    /// FFmpeg encoder names identify an implementation, not a bitstream codec.
    /// RecordResult.Codec must name the codec so it agrees with ffprobe's codec_name.
    let private codecOfFfmpegEncoder (encoder: string) =
        match (orEmptyIfNull encoder).Trim().ToLowerInvariant() with
        | "libx264" | "h264_nvenc" | "h264_qsv" | "h264_amf" | "h264" -> "h264"
        | "libx265" | "hevc_nvenc" | "hevc_qsv" | "hevc_amf" | "hevc" | "h265" -> "hevc"
        | "libaom-av1" | "libsvtav1" | "av1_nvenc" | "av1_qsv" | "av1" -> "av1"
        | other -> other

    let private resolveFfmpeg config =
        candidatePaths config
        |> List.tryFind (fun candidate -> candidate <> "ffmpeg" && File.Exists(candidate))
        |> Option.defaultWith (fun () ->
            // Fall back to PATH resolution only if nothing concrete was found.
            if candidatePaths config |> List.contains "ffmpeg" then
                "ffmpeg"
            else
                failwith "FFmpeg was not found. Set --ffmpeg or FFMPEG_PATH, or run the installer.")

    let private recordWithFfmpeg (config: RecordConfig) =
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
        let timeoutMs = (max 1 config.DurationSec + 30) * 1000
        if not (proc.WaitForExit(timeoutMs)) then
            proc.Kill(true)
            proc.WaitForExit()
            failwith $"FFmpeg exceeded the recording timeout of {timeoutMs / 1000} seconds."

        let errorText = stderr.ToString()
        if proc.ExitCode <> 0 then
            failwith $"FFmpeg failed with exit code {proc.ExitCode}: {errorText}"

        { Path = outFile
          Fps = config.Fps
          DurationSec = config.DurationSec
          Encoder = config.Encoder
          Backend = "ffmpeg"
          // config.Encoder is an encoder implementation ("libx264"), not a codec.
          // ffprobe reports codec_name=h264 for that stream, so map rather than alias.
          Codec = codecOfFfmpegEncoder config.Encoder
          Lossless = false
          LosslessMode = "lossy: FFmpeg gdigrab pipeline with yuv420p chroma subsampling"
          Container = "mp4"
          Width = 0
          Height = 0
          FrameCount = 0
          DroppedFrameCount = 0
          MetadataSampleCount = 0
          MetadataPath = ""
          ExitCode = proc.ExitCode
          StandardError = "" }

    // ---------------------------------------------------------------------------
    // Native path: Windows Graphics Capture -> HEVC encoder -> ISOBMFF mux,
    // with a live .mdkg metadata sidecar and an in-container timed metadata track.
    // ---------------------------------------------------------------------------

    /// Opens a capture session, probing NVENC on the session's own device so the
    /// encoder and the capture texture always live on the same DXGI adapter.
    /// Returns the session together with the encoder backend that was resolved.
    let private openSessionAndBackend (config: RecordConfig) =
        let requested = HevcBackend.parse config.Backend

        let openSession needCpu =
            WgcCapture.create
                { MonitorIndex = config.MonitorIndex
                  Fps = config.Fps
                  DisableCaptureBorder = config.DisableCaptureBorder
                  NeedCpuReadback = needCpu
                  PreferredAdapterLuid = None }

        match requested with
        | X265 -> openSession true, HevcBackend.X265
        | Nvenc ->
            let session = openSession false

            if not (HevcEncoder.isNvencUsable session.D3D11Device) then
                session.Dispose()
                failwith $"NVENC was requested but is not usable on this system (adapter probe failed)."

            session, HevcBackend.Nvenc
        | _ ->
            // Auto: prefer the hardware path, fall back to x265 with CPU readback.
            let session = openSession false

            if HevcEncoder.isNvencUsable session.D3D11Device then
                session, HevcBackend.Nvenc
            elif HevcEncoder.isX265Usable config.X265Path then
                session.Dispose()
                openSession true, HevcBackend.X265
            else
                session.Dispose()

                failwith
                    "No HEVC encoder is available: NVENC is not usable on this adapter and x265 could not be loaded. Pass --backend ffmpeg to use the legacy pipeline."

    let private recordNative (config: RecordConfig) =
        let outFile = Path.GetFullPath(config.OutFile)

        match Path.GetDirectoryName(outFile) with
        | null | "" -> ()
        | parent -> Directory.CreateDirectory(parent) |> ignore

        let metadataPath =
            if String.IsNullOrWhiteSpace(config.TimedMetadataOut) then
                Path.ChangeExtension(outFile, ".mdkg")
            else
                Path.GetFullPath(config.TimedMetadataOut)

        let capture, resolvedBackend = openSessionAndBackend config
        use session = capture

        use encoder =
            HevcEncoder.create
                { Backend = resolvedBackend
                  Width = session.Width
                  Height = session.Height
                  Fps = config.Fps
                  Lossless = config.Lossless
                  Preset = config.Preset
                  D3D11Device = session.D3D11Device
                  X265Path = config.X265Path }

        use muxer =
            HevcMux.create
                { OutFile = outFile
                  Width = session.Width
                  Height = session.Height
                  Fps = config.Fps
                  DecoderConfig = encoder.DecoderConfig
                  WithMetadataTrack = true }

        let mdkg = Mdkg.``open`` metadataPath
        Mdkg.addDefaultLayers mdkg

        let frameDuration = Mdkg.frameDurationTicksFromFps config.Fps
        let mutable frameIndex = 0
        let mutable previousPts = 0L
        let mutable metadataSamples = 0

        /// Scalar copy of the fields a timing sample needs. The CapturedFrame itself
        /// cannot outlive session.ReleaseFrame, and emission is deferred by one frame,
        /// so the values must be lifted out of the frame before it is released.
        let mutable pendingTiming: struct (int64 * int64 * int * int) voption = ValueNone

        /// Emits one timing record to both the sidecar and the in-container track,
        /// so the .mp4 stays self-describing even if the sidecar is lost.
        ///
        /// durationTicks is the measured gap to the next sample, not an assumed
        /// constant derived from --fps. The assumed constant is what made
        /// mdkg://timing@pts#/durationTicks a false citation: it declared 50ms of
        /// coverage while the following sample began 16.7ms later, so sample coverage
        /// overlapped and the runbook proof contradicted the PTS it cited.
        let emitTiming (qpc: int64) (pts: int64) (index: int) (dropCount: int) (durationTicks: int64) =
            let timing =
                { Qpc = qpc
                  FrameIndex = index
                  Fps = config.Fps
                  PtsTicks = pts
                  DurationTicks = durationTicks
                  GapFromPreviousTicks = (if index = 0 then 0L else pts - previousPts)
                  IsDropped = false
                  DropCount = dropCount
                  Notes = "" }

            let payload = Mdkg.serializeJson timing
            Mdkg.writeSample mdkg Mdkg.LayerId.Timing pts durationTicks Mdkg.MdkgSampleFlags.None payload

            muxer.WriteMetadataSample
                { LayerId = Mdkg.LayerId.Timing
                  PtsTicks = pts
                  DurationTicks = durationTicks
                  Flags = uint32 Mdkg.MdkgSampleFlags.None
                  Codec = Mdkg.Registry.Timing.Codec
                  Payload = payload }

            metadataSamples <- metadataSamples + 1
            previousPts <- pts

        /// Holds each sample until the next one arrives so its duration can be measured.
        let queueTiming (qpc: int64) (pts: int64) (index: int) (dropCount: int) =
            match pendingTiming with
            | ValueSome (struct (pq, pp, pi, pd)) -> emitTiming pq pp pi pd (pts - pp)
            | ValueNone -> ()

            pendingTiming <- ValueSome(struct (qpc, pts, index, dropCount))

        /// The final sample has no successor to measure against, so it falls back to
        /// the nominal frame duration. That cannot overlap anything by construction.
        let flushTiming () =
            match pendingTiming with
            | ValueSome (struct (pq, pp, pi, pd)) ->
                emitTiming pq pp pi pd frameDuration
                pendingTiming <- ValueNone
            | ValueNone -> ()

        try
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds(float (max 1 config.DurationSec)))

            let mutable running = true

            // WGC delivers at the display refresh rate, which is unrelated to the
            // requested --fps. Without pacing the native path silently ignored --fps
            // entirely (the FFmpeg path honours it via -framerate).
            //
            // The gate is anchored to a fixed grid (anchor + n * frameDuration) rather
            // than advanced from each emitted frame, because the latter compounds the
            // rounding error: at 60Hz a 500000-tick target sits 41 ticks past three
            // refresh intervals, so a strict test waits for a fourth and yields 15fps
            // instead of 20. A quarter-interval tolerance absorbs that shortfall while
            // staying below one refresh period at any sane rate, so no frame is
            // admitted a whole slot early.
            //
            // The tolerance is deliberately NOT derived from the observed source
            // period: WGC stops delivering while the screen is static, which inflates
            // the observed period to hundreds of milliseconds and would widen the
            // tolerance enough to admit frames far ahead of their slot.
            //
            // Because the grid only ever advances by frameDuration per emission, the
            // long-run rate equals the requested rate. Residual per-frame jitter is
            // inherent to a quantised source and is recorded honestly by the measured
            // DurationTicks rather than hidden behind a nominal constant.
            //
            // Gating on capture PTS rather than wall clock keeps this deterministic
            // and replayable.
            let paceTicks = if config.Fps > 0 then frameDuration else 0L
            let paceTolerance = paceTicks / 4L
            let mutable anchorPts = 0L
            let mutable emitSlot = 0L

            while running do
                match session.TryGetNextFrame(cts.Token) with
                | None -> running <- false
                | Some frame ->
                    try
                        let due =
                            paceTicks <= 0L
                            || frameIndex = 0
                            || frame.Pts + paceTolerance >= anchorPts + (emitSlot * paceTicks)

                        if due then
                            if frameIndex = 0 then anchorPts <- frame.Pts

                            emitSlot <- emitSlot + 1L

                            for au in encoder.Encode frame do
                                muxer.WriteAccessUnit au

                            queueTiming frame.QpcTime frame.Pts frameIndex session.DroppedFrameCount
                            frameIndex <- frameIndex + 1
                    finally
                        session.ReleaseFrame frame

            flushTiming ()

            for au in encoder.Finish() do
                muxer.WriteAccessUnit au

            muxer.Finish()
        finally
            Mdkg.close mdkg

        { Path = outFile
          Fps = config.Fps
          DurationSec = config.DurationSec
          // Report what actually encoded the stream, not the legacy config field.
          // config.Encoder carries the FFmpeg codec name (e.g. "libx264"), which would
          // contradict the hvc1 stream the muxer just wrote and undermine the proofs.
          Encoder = HevcBackend.toString resolvedBackend
          Backend = HevcBackend.toString resolvedBackend
          Codec = "hevc"
          Lossless = encoder.IsMathematicallyLossless
          LosslessMode = encoder.LosslessMode
          Container = config.Container
          Width = session.Width
          Height = session.Height
          FrameCount = frameIndex
          DroppedFrameCount = session.DroppedFrameCount
          MetadataSampleCount = metadataSamples
          MetadataPath = metadataPath
          ExitCode = 0
          StandardError = "" }

    let record (config: RecordConfig) =
        if String.Equals(config.Backend, "ffmpeg", StringComparison.OrdinalIgnoreCase) then
            recordWithFfmpeg config
        else
            recordNative config
