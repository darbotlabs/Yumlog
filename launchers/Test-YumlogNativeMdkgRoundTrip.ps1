param(
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$Project = Join-Path $RepoRoot 'apps\Yumlog.Native\Yumlog.Native.fsproj'

dotnet build $Project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }

$Dll = Join-Path $RepoRoot "apps\Yumlog.Native\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64\Yumlog.Native.dll"
if (-not (Test-Path $Dll)) {
    throw "Yumlog.Native.dll was not produced at $Dll"
}

$WorkDir = Join-Path $RepoRoot 'apps\Yumlog.Native\obj\MdkgRoundTrip'
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$FsxPath = Join-Path $WorkDir 'MdkgRoundTrip.fsx'
$MdkgPath = Join-Path $WorkDir 'roundtrip.mdkg'
$EscapedDll = $Dll.Replace('"', '""')
$EscapedMdkg = $MdkgPath.Replace('"', '""')

@"
#r @"$EscapedDll"

open System
open System.IO
open Yumlog.Native
open Yumlog.Native.Mdkg

let fail message = raise (InvalidOperationException(message))
let assertTrue message condition = if not condition then fail message
let assertEqual message expected actual = if not (Object.Equals(expected, actual)) then fail `$"{message}. Expected={expected}; Actual={actual}"

let now = DateTimeOffset.Parse("2026-01-02T03:04:05Z")
let point x y : TextPoint = { X = x; Y = y }
let bounds : TextBounds =
    { TopLeft = point 0.1 0.1
      TopRight = point 0.4 0.1
      BottomRight = point 0.4 0.3
      BottomLeft = point 0.1 0.3 }

let confidence = Producers.semanticConfidence 0.96 "round-trip-test" "deterministic" now
let sid kind key = Producers.semanticIdentifier "yumlog.test" kind key "v1"
let attr = Producers.semanticAttribute "test" "true" "boolean" confidence
let mdkgAttr = Producers.mdkgAttribute "source" "round-trip"

let ocrWord : OcrWord = { Text = "Hello"; Confidence = 0.98; BoundingBox = bounds }
let ocrLine : OcrLine = { Text = "Hello"; Words = [| ocrWord |] }
let ocr : OcrResult =
    { Provider = "unit"
      IsAvailable = true
      Message = "ok"
      Text = "Hello"
      Lines = [| ocrLine |] }
let hint : UiNavigationHint = { Kind = "button"; Label = "Submit"; Confidence = 0.94; Bounds = bounds }

let graphNode1 =
    Producers.semanticGraphNode "node:submit" "ui.button" "Submit" 0.95 true bounds 0L 100000L [| mdkgAttr |]
let graphNode2 =
    Producers.semanticGraphNode "node:panel" "ui.panel" "Panel" 0.91 true bounds 100000L 200000L [| mdkgAttr |]
let graphEdge =
    Producers.semanticGraphEdge "edge:panel-submit" "contains" graphNode2.Id graphNode1.Id 0.90 100000L 200000L [| mdkgAttr |]
let graphKey =
    Producers.semanticGraphLayerSample 10L 0 0L 100000L "graph:roundtrip" true [| graphNode1 |] Array.empty (Producers.emptySemanticGraphDelta())
let graphDelta =
    Producers.semanticGraphLayerSample
        20L
        1
        100000L
        100000L
        "graph:roundtrip"
        false
        Array.empty
        Array.empty
        (Producers.semanticGraphDelta [| graphNode2 |] Array.empty Array.empty [| graphEdge |] Array.empty Array.empty)

let semanticNode : SemanticNode =
    { Id = sid "node" "submit"
      Label = "Submit"
      Role = "button"
      Kind = "ui.button"
      HasBounds = true
      Bounds = { X = 0.1; Y = 0.1; Width = 0.3; Height = 0.2 }
      Attributes = [| attr |] }
let semanticGraph : SemanticGraphSnapshot =
    { GraphId = sid "graph" "roundtrip"
      CapturedUtc = now
      Nodes = [| semanticNode |]
      Edges = Array.empty
      Attributes = [| attr |] }

let backend : ApimBackend =
    { Id = sid "apim-backend" "local"
      DisplayName = "Local"
      BaseUrl = "https://example.test"
      AuthScheme = "none"
      Tags = [| "test" |] }
let operation : ApimOperation =
    { Id = sid "apim-operation" "get-widget"
      BackendId = backend.Id
      Method = "GET"
      Route = "/widgets/{id}"
      SchemaUri = "urn:yumlog:test:widget:v1"
      Intent = "read"
      Tags = [| "widget" |] }
let policy : ApimPolicyBinding =
    { Id = sid "apim-policy" "default"
      OperationId = operation.Id
      PolicyName = "default"
      PolicyJson = "{}"
      AppliesTo = "operation" }
let trace : ApimTraceTelemetry =
    { TraceId = sid "apim-trace" "trace-1"
      TimestampUtc = now
      Backend = backend
      Operation = operation
      Policies = [| policy |]
      RequestId = "req-1"
      CorrelationId = "corr-1"
      StatusCode = 200
      DurationMs = 42L
      Outcome = "success"
      TraceJson = "{}"
      Attributes = [| attr |] }
let acp : AcpContextEnvelope =
    { ContextId = sid "acp-context" "ctx-1"
      Producer = "round-trip-test"
      SchemaUri = "urn:yumlog:test:acp:v1"
      Capability = "test"
      PayloadJson = "{}"
      CreatedUtc = now
      Attributes = [| attr |] }
let telemetry : ArtFrameTelemetry =
    { StreamId = sid "stream" "main"
      FrameIndex = 0L
      TimestampUtc = now
      Width = 1920
      Height = 1080
      ChangeScore = 0.5
      DirtyRegions = [| { X = 0.0; Y = 0.0; Width = 0.5; Height = 0.5 } |]
      Ocr = ocr
      UiAffordances = [| hint |]
      SemanticGraph = semanticGraph }
let widgetDefinition : LiveTileWidgetDefinition =
    { WidgetId = sid "widget" "clock"
      DisplayName = "Clock"
      Description = "Clock widget"
      WidgetKind = "live-tile"
      SchemaUri = "urn:yumlog:test:widget:v1"
      DefaultRefreshIntervalMs = 1000
      ApimOperations = [| operation |]
      Tags = [| "clock" |] }
let widgetState : LiveTileWidgetState =
    { WidgetId = widgetDefinition.WidgetId
      InstanceId = sid "widget-instance" "clock-1"
      Status = "ready"
      RefreshIntervalMs = 1000
      LastUpdatedUtc = now
      DataJson = "{}"
      SemanticGraph = semanticGraph }
let project : WidgetStudioProject =
    { ProjectId = sid "widget-project" "roundtrip"
      Name = "RoundTrip"
      Description = "Round-trip test"
      Widgets = [| widgetDefinition |]
      ApimBackends = [| backend |]
      ApimPolicies = [| policy |]
      CreatedUtc = now
      UpdatedUtc = now }

let mdkgPath = @"$EscapedMdkg"
if File.Exists(mdkgPath) then File.Delete(mdkgPath)

do
    use writer = MdkgWriter.Open(mdkgPath)
    writer.AddDefaultLayers()
    writer.WriteOcrText(Producers.ocrTextLayerSample 10L 0 0L 100000L ocr)
    writer.WriteUiAffordance(Producers.uiAffordanceLayerSample 10L 0 0L 100000L [| hint |])
    writer.WriteSemanticGraph(graphKey)
    writer.WriteSemanticGraph(graphDelta)
    writer.WriteArtFrame(Producers.artFrameLayerSample 10L 0L 0L 100000L telemetry)
    writer.WriteAcpContext(Producers.acpContextLayerSample 0L 100000L acp)
    writer.WriteApimTrace(Producers.apimTraceLayerSample 0L 100000L trace)
    writer.WriteLiveTile(Producers.liveTileLayerSample 0L 100000L [| widgetDefinition |] [| widgetState |] [| attr |])
    writer.WriteWidgetStudio(Producers.widgetStudioLayerSample 0L 100000L project)

do
    use reader = MdkgReader.Open(mdkgPath)
    let layerNames = reader.Layers |> Array.map (fun layer -> layer.Name) |> Set.ofArray
    for name in [ "ocr-text"; "ui-affordance"; "semantic-graph"; "art-frame"; "acp-context"; "apim-trace"; "live-tile"; "widget-studio" ] do
        assertTrue (`$"schema registry missing {name}") (layerNames.Contains name)

    let one layerId = reader.ReadLayer(layerId) |> Array.exactlyOne
    assertEqual "ocr text" "Hello" (reader.ReadOcrText(one LayerId.OcrText).Ocr.Text)
    assertEqual "ui affordance" "Submit" (reader.ReadUiAffordance(one LayerId.UiAffordance).Hints[0].Label)
    let graphEntries = reader.ReadLayer(LayerId.SemanticGraph)
    assertEqual "semantic graph sample count" 2 graphEntries.Length
    let reconstructed =
        graphEntries
        |> Array.map reader.ReadSemanticGraph
        |> Graph.reconstruct
    assertEqual "semantic graph id" "graph:roundtrip" reconstructed.GraphId
    assertEqual "semantic graph node count" 2 reconstructed.Nodes.Length
    assertEqual "semantic graph edge count" 1 reconstructed.Edges.Length
    assertEqual "art frame width" 1920 (reader.ReadArtFrame(one LayerId.ArtFrame).Telemetry.Width)
    assertEqual "acp producer" "round-trip-test" (reader.ReadAcpContext(one LayerId.AcpContext).Context.Producer)
    assertEqual "apim status" 200 (reader.ReadApimTrace(one LayerId.ApimTrace).Trace.StatusCode)
    assertEqual "live tile definition" "Clock" (reader.ReadLiveTile(one LayerId.LiveTile).Definitions[0].DisplayName)
    assertEqual "widget studio project" "RoundTrip" (reader.ReadWidgetStudio(one LayerId.WidgetStudio).Project.Name)

printfn "MDKG round-trip/schema/graph reconstruction checks passed: %s" mdkgPath
"@ | Set-Content -LiteralPath $FsxPath -Encoding UTF8

dotnet fsi --exec $FsxPath
if ($LASTEXITCODE -ne 0) { throw "MDKG round-trip checks failed with exit code $LASTEXITCODE." }
