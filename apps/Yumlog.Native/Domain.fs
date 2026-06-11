namespace Yumlog.Native

open System

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
      FFmpegPath: string
      Encoder: string
      Preset: string }

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
    | Help
