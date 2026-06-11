namespace Yumlog.Native

open System
open System.Drawing
open System.Drawing.Imaging
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Windows.Forms

module FileHash =
    let sha256 path =
        use stream = File.OpenRead(path)
        use sha = SHA256.Create()
        sha.ComputeHash(stream)
        |> Array.map (fun b -> b.ToString("x2"))
        |> String.concat ""

module NativeCapture =
    let private bounds allScreens =
        if allScreens then
            SystemInformation.VirtualScreen
        else
            match Screen.PrimaryScreen with
            | null -> SystemInformation.VirtualScreen
            | screen -> screen.Bounds

    let captureFrame (config: CaptureConfig) timestamp frameIndex =
        if config.ImageFormat.ToLowerInvariant() <> "png" then
            invalidArg "ImageFormat" "Only png output is currently supported."

        Directory.CreateDirectory(config.OutDir) |> ignore

        let captureBounds = bounds config.AllScreens
        use bitmap = new Bitmap(captureBounds.Width, captureBounds.Height, PixelFormat.Format32bppArgb)
        use graphics = Graphics.FromImage(bitmap)
        graphics.CopyFromScreen(captureBounds.Location, Point.Empty, captureBounds.Size, CopyPixelOperation.SourceCopy)

        let filename = $"screenshot_{timestamp}_{frameIndex:D3}.png"
        let path = Path.Combine(config.OutDir, filename)
        bitmap.Save(path, ImageFormat.Png)

        { Index = frameIndex
          Path = Path.GetFullPath(path)
          TimestampUtc = DateTimeOffset.UtcNow
          Width = captureBounds.Width
          Height = captureBounds.Height
          AllScreens = config.AllScreens
          Sha256 = FileHash.sha256 path }

    let capture (config: CaptureConfig) =
        let frameCount =
            if config.Count > 0 then
                config.Count
            else
                max 1 (config.Fps * config.DurationSec)

        let intervalMs =
            if config.Fps > 0 then
                max 1 (1000 / config.Fps)
            else
                1000

        let timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss")

        [| for index in 1 .. frameCount do
               let frame = captureFrame config timestamp index
               if index < frameCount then
                   Thread.Sleep(intervalMs)
               frame |]

