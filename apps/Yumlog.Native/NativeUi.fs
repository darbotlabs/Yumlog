namespace Yumlog.Native

open System
open System.Diagnostics
open System.Drawing
open System.IO
open System.Linq
open System.Runtime.InteropServices
open System.Threading.Tasks
open System.Windows.Forms

module NativeUi =
    [<Literal>]
    let private SW_RESTORE = 9

    [<DllImport("user32.dll")>]
    extern bool private SetForegroundWindow(nativeint hWnd)

    [<DllImport("user32.dll")>]
    extern bool private ShowWindow(nativeint hWnd, int nCmdShow)

    let private appendLine (box: TextBox) text =
        box.AppendText(text + Environment.NewLine)

    let private rawOcrStatus () =
        match WinAppRuntime.tryInitialize() with
        | Error hr -> $"Windows App Runtime bootstrap failed: {WinAppRuntime.hresultText hr}"
        | Ok () ->
            try
                let state = WindowsAiOcr.getReadyState()
                $"Windows AI TextRecognizer state: {WindowsAiOcr.readyStateName state} ({int state})"
            with ex ->
                $"Windows AI TextRecognizer probe failed: {ex.Message}"

    let private ensureOcrModel (output: TextBox) =
        appendLine output "Ensuring Windows AI OCR model readiness..."
        Task.Run(fun () ->
            let message =
                match WinAppRuntime.tryInitialize() with
                | Error hr -> "Windows App Runtime bootstrap failed: " + WinAppRuntime.hresultText hr
                | Ok () ->
                    match WindowsAiOcr.ensureReady() with
                    | Ok () -> "OCR model is ready. " + rawOcrStatus()
                    | Error error -> "EnsureReady failed or is unavailable: " + error
            output.BeginInvoke(Action(fun () -> appendLine output message)) |> ignore) |> ignore

    let private tryFindUpwards startDir relativePath =
        let rec loop (dir: DirectoryInfo option) =
            match dir with
            | None -> None
            | Some current ->
                let candidate = Path.Combine(current.FullName, relativePath)
                if File.Exists(candidate) || Directory.Exists(candidate) then
                    Some candidate
                else
                    current.Parent |> Option.ofObj |> loop

        if Directory.Exists(startDir) then
            DirectoryInfo(startDir) |> Some |> loop
        else
            None

    let private paperboyProjectPath () =
        [ AppContext.BaseDirectory
          Environment.CurrentDirectory
          Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..")) ]
        |> List.tryPick (fun start -> tryFindUpwards start (Path.Combine("apps", "Paperboy.Desktop", "Paperboy.Desktop.csproj")))

    let private paperboyRoot () =
        paperboyProjectPath()
        |> Option.bind (fun path -> Path.GetDirectoryName(path) |> Option.ofObj)

    let private paperboyExeCandidates root =
        [ Path.Combine(root, "bin", "Release", "net11.0-windows", "Paperboy.exe")
          Path.Combine(root, "bin", "Debug", "net11.0-windows", "Paperboy.exe") ]

    let private paperboyExePath () =
        paperboyRoot()
        |> Option.bind (fun root ->
            paperboyExeCandidates root
            |> List.tryFind File.Exists)

    let private paperboyProcess () =
        Process.GetProcessesByName("Paperboy")
        |> Array.tryFind (fun proc -> not proc.HasExited)

    let private paperboyStatus () =
        match paperboyProcess(), paperboyExePath() with
        | Some proc, _ -> $"Status: running (PID {proc.Id})"
        | None, Some _ -> "Status: built, not running"
        | None, None -> "Status: not built"

    let private startProcess fileName args workingDir =
        let info = ProcessStartInfo()
        info.FileName <- fileName
        info.Arguments <- args
        info.WorkingDirectory <- workingDir
        info.UseShellExecute <- true
        Process.Start(info) |> ignore

    let private focusProcessWindow (proc: Process) =
        if proc.MainWindowHandle <> nativeint 0 then
            ShowWindow(proc.MainWindowHandle, SW_RESTORE) |> ignore
            SetForegroundWindow(proc.MainWindowHandle) |> ignore
            true
        else
            false

    let private buildPaperboy (output: TextBox) =
        match paperboyProjectPath() with
        | None ->
            appendLine output "Paperboy project not found. Expected apps\\Paperboy.Desktop\\Paperboy.Desktop.csproj under the Yumlog repo."
        | Some project ->
            appendLine output $"Building Paperboy: {project}"
            Task.Run(fun () ->
                let info = ProcessStartInfo()
                info.FileName <- "dotnet"
                info.ArgumentList.Add("build")
                info.ArgumentList.Add(project)
                info.ArgumentList.Add("-c")
                info.ArgumentList.Add("Release")
                info.ArgumentList.Add("--nologo")
                info.WorkingDirectory <- Path.GetDirectoryName(project) |> Option.ofObj |> Option.defaultValue Environment.CurrentDirectory
                info.UseShellExecute <- false
                info.RedirectStandardOutput <- true
                info.RedirectStandardError <- true
                info.CreateNoWindow <- true
                use proc = new Process()
                proc.StartInfo <- info
                proc.Start() |> ignore
                let stdout = proc.StandardOutput.ReadToEnd()
                let stderr = proc.StandardError.ReadToEnd()
                proc.WaitForExit()
                output.BeginInvoke(Action(fun () ->
                    if proc.ExitCode = 0 then
                        appendLine output "Paperboy build succeeded."
                        match paperboyExePath() with
                        | Some exe -> appendLine output $"Paperboy executable: {exe}"
                        | None -> appendLine output "Paperboy executable was not found after build."
                    else
                        appendLine output $"Paperboy build failed with exit code {proc.ExitCode}."
                        if not (String.IsNullOrWhiteSpace(stdout)) then appendLine output stdout
                        if not (String.IsNullOrWhiteSpace(stderr)) then appendLine output stderr)) |> ignore) |> ignore

    let run () =
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(false)

        use form = new Form()
        form.Text <- "Yumlog Native"
        form.StartPosition <- FormStartPosition.CenterScreen
        form.MinimumSize <- Size(720, 460)
        form.Size <- Size(860, 560)

        use root = new TableLayoutPanel()
        root.Dock <- DockStyle.Fill
        root.ColumnCount <- 1
        root.RowCount <- 5
        root.Padding <- Padding(12)
        root.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        root.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        root.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        root.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        root.RowStyles.Add(RowStyle(SizeType.Percent, 100.0f)) |> ignore

        use title = new Label()
        title.Text <- "Yumlog Native"
        title.Font <- new Font(FontFamily.GenericSansSerif, 18.0f, FontStyle.Bold)
        title.AutoSize <- true

        use subtitle = new Label()
        subtitle.Text <- "Native F# capture, recording, Windows AI OCR, and local analysis."
        subtitle.AutoSize <- true
        subtitle.Padding <- Padding(0, 0, 0, 8)

        use buttonPanel = new FlowLayoutPanel()
        buttonPanel.Dock <- DockStyle.Top
        buttonPanel.AutoSize <- true

        use captureButton = new Button()
        captureButton.Text <- "Capture screen"
        captureButton.AutoSize <- true

        use ocrButton = new Button()
        ocrButton.Text <- "Probe OCR runtime"
        ocrButton.AutoSize <- true

        use ensureOcrButton = new Button()
        ensureOcrButton.Text <- "Ensure OCR model"
        ensureOcrButton.AutoSize <- true

        use paperboyPanel = new GroupBox()
        paperboyPanel.Text <- "Paperboy"
        paperboyPanel.Dock <- DockStyle.Top
        paperboyPanel.AutoSize <- true
        paperboyPanel.Padding <- Padding(10)

        use paperboyLayout = new TableLayoutPanel()
        paperboyLayout.Dock <- DockStyle.Fill
        paperboyLayout.ColumnCount <- 1
        paperboyLayout.RowCount <- 2
        paperboyLayout.AutoSize <- true

        use paperboyLabel = new Label()
        paperboyLabel.AutoSize <- true
        paperboyLabel.Text <-
            match paperboyProjectPath(), paperboyExePath() with
            | Some project, Some exe -> $"Source: {project}{Environment.NewLine}Executable: {exe}{Environment.NewLine}{paperboyStatus()}"
            | Some project, None -> $"Source: {project}{Environment.NewLine}Executable: not built yet{Environment.NewLine}{paperboyStatus()}"
            | None, _ -> "Paperboy source was not found under this Yumlog checkout."

        use paperboyButtons = new FlowLayoutPanel()
        paperboyButtons.Dock <- DockStyle.Top
        paperboyButtons.AutoSize <- true

        use launchPaperboyButton = new Button()
        launchPaperboyButton.Text <- "Launch Paperboy"
        launchPaperboyButton.AutoSize <- true

        use buildPaperboyButton = new Button()
        buildPaperboyButton.Text <- "Build Paperboy"
        buildPaperboyButton.AutoSize <- true

        use openPaperboyFolderButton = new Button()
        openPaperboyFolderButton.Text <- "Open Paperboy folder"
        openPaperboyFolderButton.AutoSize <- true

        use output = new TextBox()
        output.Multiline <- true
        output.ReadOnly <- true
        output.ScrollBars <- ScrollBars.Vertical
        output.Dock <- DockStyle.Fill
        output.Font <- new Font("Consolas", 10.0f)

        let identity =
            RuntimeIdentity.currentPackageFullName()
            |> Option.defaultValue "No package identity"

        appendLine output $"Package identity: {identity}"
        appendLine output (rawOcrStatus())
        appendLine output ""
        appendLine output "Paperboy:"
        match paperboyProjectPath(), paperboyExePath() with
        | Some project, Some exe ->
            appendLine output $"  Source: {project}"
            appendLine output $"  Executable: {exe}"
            appendLine output $"  {paperboyStatus()}"
        | Some project, None ->
            appendLine output $"  Source: {project}"
            appendLine output "  Executable: not built yet. Click Build Paperboy."
            appendLine output $"  {paperboyStatus()}"
        | None, _ ->
            appendLine output "  Not found. Expected apps\\Paperboy.Desktop under the Yumlog repo."
        appendLine output ""
        appendLine output "Use the CLI for automation, for example:"
        appendLine output "Yumlog.Native.exe capture --out-dir .\\screenshots --count 1"
        appendLine output "Yumlog.Native.exe analyze --input <png> --ocr windows-ai"

        captureButton.Click.Add(fun _ ->
            try
                let outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Yumlog.Native")
                let config =
                    { NativeConfig.defaults.Capture with
                        OutDir = outDir
                        Count = 1
                        DurationSec = 1 }
                let frames = NativeCapture.capture config
                appendLine output $"Captured: {frames[0].Path}"
            with ex ->
                appendLine output $"Capture failed: {ex.Message}")

        ocrButton.Click.Add(fun _ ->
            appendLine output (rawOcrStatus()))

        ensureOcrButton.Click.Add(fun _ ->
            ensureOcrModel output)

        launchPaperboyButton.Click.Add(fun _ ->
            match paperboyProcess(), paperboyExePath() with
            | Some proc, _ ->
                appendLine output $"Paperboy is already running (PID {proc.Id})."
                if focusProcessWindow proc then
                    appendLine output "Focused the existing Paperboy window."
                else
                    appendLine output "Paperboy is running but has no visible main window yet."
            | None, Some exe ->
                appendLine output $"Launching Paperboy: {exe}"
                startProcess exe "" (Path.GetDirectoryName(exe) |> Option.ofObj |> Option.defaultValue Environment.CurrentDirectory)
            | None, None ->
                appendLine output "Paperboy executable not found. Click Build Paperboy first.")

        buildPaperboyButton.Click.Add(fun _ ->
            buildPaperboy output)

        openPaperboyFolderButton.Click.Add(fun _ ->
            match paperboyRoot() with
            | Some root -> startProcess "explorer.exe" $"\"{root}\"" root
            | None -> appendLine output "Paperboy folder not found.")

        buttonPanel.Controls.Add(captureButton) |> ignore
        buttonPanel.Controls.Add(ocrButton) |> ignore
        buttonPanel.Controls.Add(ensureOcrButton) |> ignore
        paperboyButtons.Controls.Add(launchPaperboyButton) |> ignore
        paperboyButtons.Controls.Add(buildPaperboyButton) |> ignore
        paperboyButtons.Controls.Add(openPaperboyFolderButton) |> ignore
        paperboyLayout.Controls.Add(paperboyLabel, 0, 0)
        paperboyLayout.Controls.Add(paperboyButtons, 0, 1)
        paperboyPanel.Controls.Add(paperboyLayout)

        root.Controls.Add(title, 0, 0)
        root.Controls.Add(subtitle, 0, 1)
        root.Controls.Add(buttonPanel, 0, 2)
        root.Controls.Add(paperboyPanel, 0, 3)
        root.Controls.Add(output, 0, 4)
        form.Controls.Add(root)

        Application.Run(form)
