namespace Yumlog.Native

open System
open System.Drawing
open System.IO
open System.Linq
open System.Threading.Tasks
open System.Windows.Forms

module NativeUi =
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

    let private loadOcrModel (button: Button) (output: TextBox) =
        button.Enabled <- false
        appendLine output "Loading Windows AI OCR model..."
        Task.Run(fun () ->
            let message =
                match WinAppRuntime.tryInitialize() with
                | Error hr -> "Windows App Runtime bootstrap failed: " + WinAppRuntime.hresultText hr
                | Ok () ->
                    match WindowsAiOcr.ensureReady() with
                    | Ok () -> "OCR model loaded. " + rawOcrStatus()
                    | Error error -> "OCR model load failed or is unavailable: " + error
            output.BeginInvoke(Action(fun () ->
                button.Enabled <- true
                appendLine output message))
            |> ignore)
        |> ignore

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
        root.RowCount <- 4
        root.Padding <- Padding(12)
        root.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        root.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        root.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        root.RowStyles.Add(RowStyle(SizeType.Percent, 100.0f)) |> ignore

        use title = new Label()
        title.Text <- "Yumlog Native"
        title.Font <- new Font(FontFamily.GenericSansSerif, 18.0f, FontStyle.Bold)
        title.AutoSize <- true

        use subtitle = new Label()
        subtitle.Text <- "Your local ground-truth toolbox for capture, recording, OCR, and analysis."
        subtitle.AutoSize <- true
        subtitle.Padding <- Padding(0, 0, 0, 8)

        use buttonPanel = new FlowLayoutPanel()
        buttonPanel.Dock <- DockStyle.Top
        buttonPanel.AutoSize <- true

        use captureButton = new Button()
        captureButton.Text <- "Capture screen"
        captureButton.AutoSize <- true

        use ocrButton = new Button()
        ocrButton.Text <- "Verify Windows AI OCR model load"
        ocrButton.AutoSize <- true

        use loadOcrButton = new Button()
        loadOcrButton.Text <- "Load OCR model"
        loadOcrButton.AutoSize <- true

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
        appendLine output "Capture, OCR, and local analysis stay independent from Paperboy."
        appendLine output "For automation, use Yumlog.Native.Cli.exe."

        captureButton.Click.Add(fun _ ->
            captureButton.Enabled <- false
            Task.Run(fun () ->
                let message =
                    try
                        let outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Yumlog.Native")
                        let config =
                            { NativeConfig.defaults.Capture with
                                OutDir = outDir
                                Count = 1
                                DurationSec = 1 }
                        let frames = NativeCapture.capture config
                        $"Captured: {frames[0].Path}"
                    with ex ->
                        $"Capture failed: {ex.Message}"
                output.BeginInvoke(Action(fun () ->
                    captureButton.Enabled <- true
                    appendLine output message))
                |> ignore)
            |> ignore)

        ocrButton.Click.Add(fun _ ->
            appendLine output (rawOcrStatus()))

        loadOcrButton.Click.Add(fun _ ->
            loadOcrModel loadOcrButton output)

        buttonPanel.Controls.Add(captureButton) |> ignore
        buttonPanel.Controls.Add(ocrButton) |> ignore
        buttonPanel.Controls.Add(loadOcrButton) |> ignore

        root.Controls.Add(title, 0, 0)
        root.Controls.Add(subtitle, 0, 1)
        root.Controls.Add(buttonPanel, 0, 2)
        root.Controls.Add(output, 0, 3)
        form.Controls.Add(root)

        Application.Run(form)
