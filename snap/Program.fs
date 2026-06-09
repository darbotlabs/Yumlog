module Snap.Program

open System
open System.Drawing
open System.Drawing.Imaging
open System.IO
open System.Windows.Forms

// ---------------------------------------------------------------------------
// CLI argument parsing
// ---------------------------------------------------------------------------

type Config =
    { OutDir     : string
      Fps        : int
      DurationSec: int
      Count      : int option   // explicit frame count overrides fps*duration
      AllScreens : bool }

let defaultConfig =
    { OutDir      = "./screenshots"
      Fps         = 1
      DurationSec = 1
      Count       = None
      AllScreens  = true }

let parseArgs (argv: string[]) : Config =
    let mutable cfg = defaultConfig
    let args = argv |> Array.toList
    let rec loop = function
        | "--out-dir"   :: v :: rest -> cfg <- { cfg with OutDir      = v         }; loop rest
        | "--fps"       :: v :: rest -> cfg <- { cfg with Fps         = int v     }; loop rest
        | "--duration"  :: v :: rest -> cfg <- { cfg with DurationSec = int v     }; loop rest
        | "--count"     :: v :: rest -> cfg <- { cfg with Count       = Some(int v) }; loop rest
        | "--primary"        :: rest -> cfg <- { cfg with AllScreens  = false     }; loop rest
        | _ :: rest                  -> loop rest
        | []                         -> ()
    loop args
    cfg

// ---------------------------------------------------------------------------
// Capture logic
// ---------------------------------------------------------------------------

let captureFrame (cfg: Config) (timestamp: string) (frameIndex: int) =
    let bounds =
        if cfg.AllScreens then SystemInformation.VirtualScreen
        else
            match Screen.PrimaryScreen with
            | null -> SystemInformation.VirtualScreen
            | s    -> s.Bounds

    use bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb)
    use g   = Graphics.FromImage(bmp)
    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy)

    let filename = sprintf "screenshot_%s_%03d.png" timestamp frameIndex
    let outPath  = Path.Combine(cfg.OutDir, filename)
    bmp.Save(outPath, ImageFormat.Png)
    outPath

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

[<EntryPoint>]
let main argv =
    let sw = Diagnostics.Stopwatch.StartNew()
    let cfg = parseArgs argv

    if not (Directory.Exists(cfg.OutDir)) then
        Directory.CreateDirectory(cfg.OutDir) |> ignore

    let frameCount =
        match cfg.Count with
        | Some n -> n
        | None   -> max 1 (cfg.Fps * cfg.DurationSec)

    let intervalMs =
        if cfg.Fps > 0 then 1000 / cfg.Fps else 1000

    let timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss")

    let mutable lastPath = ""
    for i in 1 .. frameCount do
        lastPath <- captureFrame cfg timestamp i
        if i < frameCount then
            System.Threading.Thread.Sleep(intervalMs)

    sw.Stop()
    printfn "Screenshots saved to %s" cfg.OutDir
    printfn "Latest: %s" (Path.GetFullPath(lastPath))
    printfn "Elapsed: %dms" sw.ElapsedMilliseconds
    0

