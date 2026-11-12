namespace Yumlog.Native

open System
open System.Diagnostics
open System.Drawing
open System.IO
open System.Reflection
open System.Text

module NativeFollow =
    /// Fraction of sampled pixels that differ between two frames.
    ///
    /// sampleStep trades sensitivity for cost. The default derived step is tuned for
    /// whole-window changes; text-level UI churn on a large desktop moves well under
    /// 1% of pixels and needs a finer grid to register at all.
    let frameChangeScoreAt (sampleStep: int) (previousPath: string) (currentPath: string) =
        if String.IsNullOrWhiteSpace(previousPath) || not (File.Exists(previousPath)) then
            1.0
        else
            use previous = new Bitmap(previousPath)
            use current = new Bitmap(currentPath)
            let step =
                if sampleStep > 0 then
                    sampleStep
                else
                    max 1 (min previous.Width previous.Height / 64)

            let mutable total = 0
            let mutable changed = 0

            let width = min previous.Width current.Width
            let height = min previous.Height current.Height
            for x in 0 .. step .. (width - 1) do
                for y in 0 .. step .. (height - 1) do
                    total <- total + 1
                    if previous.GetPixel(x, y).ToArgb() <> current.GetPixel(x, y).ToArgb() then
                        changed <- changed + 1

            if total = 0 then 0.0 else float changed / float total

    let frameChangeScore (previousPath: string) (currentPath: string) =
        frameChangeScoreAt 0 previousPath currentPath

    let follow (config: FollowConfig) =
        let started = DateTimeOffset.UtcNow
        let captureConfig =
            { OutDir = config.OutDir
              Fps = config.Fps
              DurationSec = config.DurationSec
              Count = 0
              AllScreens = true
              ImageFormat = "png" }

        let frames = NativeCapture.capture captureConfig
        let mutable previous = ""

        let steps =
            frames
            |> Array.mapi (fun index frame ->
                let changeScore = frameChangeScore previous frame.Path
                previous <- frame.Path
                let ocr = NativeOcr.recognize config.OcrMode frame.Path
                { Index = index + 1
                  Frame = frame
                  ChangeScore = changeScore
                  Ocr = ocr
                  Hints = NativeNavigation.inferHints ocr })
            |> Array.filter (fun step -> step.ChangeScore >= config.ChangeThreshold || step.Index = 1)

        { StartedUtc = started
          CompletedUtc = DateTimeOffset.UtcNow
          Frames = frames
          Steps = steps
          Config = config }

module NativeOrchestration =
    let loadPlan path =
        if not (File.Exists(path)) then
            invalidArg "path" $"Orchestration plan does not exist: {path}"
        NativeJson.read<OrchestrationPlan> path

    let private currentInvocation () =
        let assemblyPath =
            match Assembly.GetEntryAssembly() with
            | null -> failwith "Could not locate the Yumlog.Native entry assembly for orchestration."
            | assembly when String.IsNullOrWhiteSpace(assembly.Location) ->
                failwith "Could not locate the Yumlog.Native entry assembly path for orchestration."
            | assembly -> assembly.Location

        if not (String.Equals(Path.GetFileNameWithoutExtension(assemblyPath), "Yumlog.Native.Cli", StringComparison.OrdinalIgnoreCase)) then
            failwith "Orchestration is available through Yumlog.Native.Cli to keep the desktop UI isolated from automation runs."

        match Environment.ProcessPath with
        | null | "" ->
            "dotnet", [| assemblyPath |]
        | processPath ->
            let processName = Path.GetFileName(processPath)
            if String.Equals(processName, "dotnet.exe", StringComparison.OrdinalIgnoreCase) then
                processPath, [| assemblyPath |]
            else
                processPath, Array.empty

    let private runStep (step: OrchestrationStep) =
        let executable, prefixArgs = currentInvocation()
        let info = ProcessStartInfo()
        info.FileName <- executable
        for arg in prefixArgs do
            info.ArgumentList.Add(arg)
        info.ArgumentList.Add(step.Command)
        for arg in step.Arguments do
            info.ArgumentList.Add(arg)

        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.CreateNoWindow <- true

        let stdout = StringBuilder()
        let stderr = StringBuilder()
        let sw = Stopwatch.StartNew()

        use proc = new Process()
        proc.StartInfo <- info
        proc.OutputDataReceived.Add(fun e ->
            if not (isNull e.Data) then stdout.AppendLine(e.Data) |> ignore)
        proc.ErrorDataReceived.Add(fun e ->
            if not (isNull e.Data) then stderr.AppendLine(e.Data) |> ignore)

        if not (proc.Start()) then
            failwith $"Could not start orchestration step '{step.Name}'."

        proc.BeginOutputReadLine()
        proc.BeginErrorReadLine()
        proc.WaitForExit()
        sw.Stop()

        { Name = step.Name
          Command = step.Command
          ExitCode = proc.ExitCode
          DurationMs = sw.ElapsedMilliseconds
          StandardOutput = stdout.ToString()
          StandardError = stderr.ToString() }

    let runPlan path =
        let plan = loadPlan path
        plan.Steps
        |> Array.map (fun step ->
            let result = runStep step
            if result.ExitCode <> 0 then
                failwith $"Orchestration step '{step.Name}' failed with exit code {result.ExitCode}: {result.StandardError}"
            result)
