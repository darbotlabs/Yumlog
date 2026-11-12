namespace Yumlog.Native

open System
open System.Text.Json.Serialization

/// Guards for values that cross a boundary the F# type system cannot police:
/// P/Invoke returns, JSON deserialization, and callers written in other .NET
/// languages can all produce null where the declared type says non-nullable.
[<AutoOpen>]
module Guards =
    /// Runtime reference-null test that does not fight the nullness analysis.
    /// Constrained to reference types: on a struct this would box and return
    /// false unconditionally, i.e. compile as a guard while doing nothing.
    let inline isNullRef (value: 'T when 'T: not struct) = obj.ReferenceEquals(value, null)

    /// Coalesces a possibly-null string to a fallback.
    let orEmptyIfNull (value: string | null) : string =
        match value with
        | null -> ""
        | v -> v

[<CLIMutable>]
type CaptureConfig =
    { OutDir: string
      Fps: int
      DurationSec: int
      Count: int
      AllScreens: bool
      ImageFormat: string }

[<CLIMutable>]
type RecordConfig =
    { OutFile: string
      Fps: int
      DurationSec: int
      // Legacy FFmpeg path. Retained for config compatibility; unused by the native HEVC pipeline.
      // Pinned name: JsonNamingPolicy.CamelCase would render this "fFmpegPath",
      // which does not match the "ffmpegPath" key used by shipped config files.
      [<JsonPropertyName("ffmpegPath")>]
      FFmpegPath: string
      Encoder: string
      Preset: string
      Backend: string
      Codec: string
      Lossless: bool
      Container: string
      X265Path: string
      CaptureBackend: string
      MonitorIndex: int
      DisableCaptureBorder: bool
      TimedMetadataOut: string }

[<CLIMutable>]
type AnalyzeConfig =
    { Input: string
      OcrMode: string
      JsonOut: string
      MinConfidence: float }

[<CLIMutable>]
type FollowConfig =
    { OutDir: string
      Fps: int
      DurationSec: int
      ChangeThreshold: float
      OcrMode: string
      StopOnIdleSec: int }

/// Inputs for in-process reasoning over a recording's runbook. Provider is a
/// string rather than a DU so config files stay forward compatible with ONNX
/// Runtime execution providers this build does not ship natively yet.
[<CLIMutable>]
type ReasonConfig =
    { Input: string
      ModelDir: string
      Provider: string
      Question: string
      HighWaterPct: float
      MaxNewTokens: int
      MaxSegments: int
      JsonOut: string }

/// Inputs for live on-screen reasoning. The screen is sampled frame by frame and
/// only changed frames are streamed into the resident model, so the context window
/// tracks what actually moved rather than wall-clock time.
[<CLIMutable>]
type WatchConfig =
    { OutDir: string
      Fps: int
      DurationSec: int
      ChangeThreshold: float
      OcrMode: string
      NarrateEverySec: float
      Question: string
      ModelDir: string
      Provider: string
      HighWaterPct: float
      MaxNewTokens: int
      JsonOut: string }

[<CLIMutable>]
type AppConfig =
    { Capture: CaptureConfig
      Record: RecordConfig
      Analyze: AnalyzeConfig
      Follow: FollowConfig }

[<CLIMutable>]
type TextPoint =
    { X: float
      Y: float }

[<CLIMutable>]
type TextBounds =
    { TopLeft: TextPoint
      TopRight: TextPoint
      BottomRight: TextPoint
      BottomLeft: TextPoint }

[<CLIMutable>]
type OcrWord =
    { Text: string
      Confidence: float
      BoundingBox: TextBounds }

[<CLIMutable>]
type OcrLine =
    { Text: string
      Words: OcrWord array }

[<CLIMutable>]
type OcrResult =
    { Provider: string
      IsAvailable: bool
      Message: string
      Text: string
      Lines: OcrLine array }

[<CLIMutable>]
type FrameInfo =
    { Index: int
      Path: string
      TimestampUtc: DateTimeOffset
      Width: int
      Height: int
      AllScreens: bool
      Sha256: string }

[<CLIMutable>]
type RecordResult =
    { Path: string
      Fps: int
      DurationSec: int
      Encoder: string
      Backend: string
      Codec: string
      /// True only when the encoded pixels are provably recoverable to the captured BGRA bytes.
      Lossless: bool
      /// Describes exactly what "lossless" means for this recording, including any colorspace caveat.
      LosslessMode: string
      Container: string
      Width: int
      Height: int
      FrameCount: int
      DroppedFrameCount: int
      MetadataSampleCount: int
      MetadataPath: string
      ExitCode: int
      StandardError: string }

[<CLIMutable>]
type AnalysisResult =
    { Input: string
      Kind: string
      Width: int
      Height: int
      SizeBytes: int64
      Sha256: string
      Ocr: OcrResult }

[<CLIMutable>]
type UiNavigationHint =
    { Kind: string
      Label: string
      Confidence: float
      Bounds: TextBounds }

[<CLIMutable>]
type FollowStep =
    { Index: int
      Frame: FrameInfo
      ChangeScore: float
      Ocr: OcrResult
      Hints: UiNavigationHint array }

[<CLIMutable>]
type FollowManifest =
    { StartedUtc: DateTimeOffset
      CompletedUtc: DateTimeOffset
      Frames: FrameInfo array
      Steps: FollowStep array
      Config: FollowConfig }

[<CLIMutable>]
type OrchestrationStep =
    { Name: string
      Command: string
      Arguments: string array }

[<CLIMutable>]
type OrchestrationPlan =
    { Name: string
      Steps: OrchestrationStep array }

[<CLIMutable>]
type OrchestrationStepResult =
    { Name: string
      Command: string
      ExitCode: int
      DurationMs: int64
      StandardOutput: string
      StandardError: string }

type NativeCommand =
    | Capture of CaptureConfig * string
    | Record of RecordConfig * string
    | Analyze of AnalyzeConfig * string
    | Follow of FollowConfig * string
    | Orchestrate of string * string
    | ConfigShow of string
    | ConfigInit of string * bool
    | SelfTest of string * string
    | Runbook of string * string * string
    | Reason of ReasonConfig
    | Watch of WatchConfig
    | ListModels
    | Help

// ---------------------------------------------------------------------------
// Canonical native HEVC pipeline contracts.
// Every capture, encoder, and mux module in this assembly speaks exactly these
// types. Do not redefine local variants.
// ---------------------------------------------------------------------------

/// Timebase for every Pts/Dts/QpcTicks value in the native pipeline: 100ns units.
[<RequireQualifiedAccess>]
module HevcTimebase =
    [<Literal>]
    let TicksPerSecond = 10_000_000L

type HevcBackend =
    | Auto
    | Nvenc
    | X265

[<RequireQualifiedAccess>]
module HevcBackend =
    let parse (value: string) =
        match (if isNull (box value) then "" else value).Trim().ToLowerInvariant() with
        | "" | "auto" -> Auto
        | "nvenc" -> Nvenc
        | "x265" | "cpu" | "software" -> X265
        | other -> invalidArg (nameof value) $"Unknown HEVC backend '{other}'. Expected auto, nvenc, or x265."

    let toString backend =
        match backend with
        | Auto -> "auto"
        | Nvenc -> "nvenc"
        | X265 -> "x265"

/// Chroma sampling actually used by an encoder session.
type HevcChromaFormat =
    | Yuv420
    | Yuv444

[<RequireQualifiedAccess>]
module HevcChromaFormat =
    /// ISO/IEC 23008-2 chroma_format_idc.
    let toIdc format =
        match format with
        | Yuv420 -> 1uy
        | Yuv444 -> 3uy

/// Decoder configuration for the elementary stream.
/// VpsSpsPpsAnnexB holds the parameter sets with 4-byte start codes, in VPS,SPS,PPS order.
type HevcDecoderConfig =
    { VpsSpsPpsAnnexB: byte array option
      Chroma: HevcChromaFormat
      BitDepth: int
      Width: int
      Height: int }

/// One encoded HEVC access unit in Annex B form (4-byte start codes).
type HevcAccessUnit =
    { Pts: int64
      Dts: int64
      QpcTime: int64
      Data: byte array
      IsKeyFrame: bool }

/// A single captured desktop frame. Texture is a D3D11 ID3D11Texture2D* usable by
/// NVENC on the same adapter; Bgra is a CPU copy for software encoders. At least
/// one of the two is always populated.
type CapturedFrame =
    { Index: int
      QpcTime: int64
      Pts: int64
      Width: int
      Height: int
      Texture: nativeint
      Bgra: byte array option
      RowPitch: int }
