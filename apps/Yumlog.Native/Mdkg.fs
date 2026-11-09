namespace Yumlog.Native

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Text.Json.Serialization

module Mdkg =

    [<Literal>]
    let MovieTimescale = 10_000_000L

    [<Literal>]
    let MajorVersion = 1us

    [<Literal>]
    let MinorVersion = 0us

    [<Literal>]
    let private FixedHeaderSize = 128

    let private Magic =
        [| 0x4Duy; 0x44uy; 0x4Buy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]

    /// Raise an InvalidDataException; generic return so it composes in any value position.
    let private invalidData (message: string) : 'a =
        raise (InvalidDataException message)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool QueryPerformanceFrequency(int64& frequency)

    [<Flags>]
    type MdkgLayerFlags =
        | None = 0u
        | Required = 0x00000001u
        | Sparse = 0x00000002u
        | Graph = 0x00000004u
        | Extension = 0x00000008u

    [<Flags>]
    type MdkgSampleFlags =
        | None = 0u
        | KeyFrame = 0x00000001u
        | Delta = 0x00000002u
        | Gap = 0x00000004u
        | Dropped = 0x00000008u
        | Sparse = 0x00000010u

    module LayerId =
        [<Literal>]
        let Timing = 1u

        [<Literal>]
        let OcrText = 2u

        [<Literal>]
        let UiAffordance = 3u

        [<Literal>]
        let SemanticGraph = 4u

        [<Literal>]
        let InputEvent = 5u

        [<Literal>]
        let ArtFrame = 6u

        [<Literal>]
        let AcpContext = 7u

        [<Literal>]
        let ApimTrace = 8u

        [<Literal>]
        let LiveTile = 9u

        [<Literal>]
        let WidgetStudio = 10u

        [<Literal>]
        let ExtensionCatalog = 11u

    [<CLIMutable>]
    type MdkgLayerDescriptor =
        { Id: uint32
          Name: string
          SchemaUri: string
          Codec: string
          Flags: uint32
          Description: string }

    [<CLIMutable>]
    type MdkgSampleEntry =
        { LayerId: uint32
          PtsTicks: int64
          DurationTicks: int64
          ByteOffset: uint64
          ByteLength: uint32
          Flags: uint32 }

    [<CLIMutable>]
    type MdkgSample =
        { Entry: MdkgSampleEntry
          Payload: byte array }

    [<CLIMutable>]
    type TimingLayerSample =
        { Qpc: int64
          FrameIndex: int
          Fps: int
          PtsTicks: int64
          DurationTicks: int64
          GapFromPreviousTicks: int64
          IsDropped: bool
          DropCount: int
          Notes: string }

    [<CLIMutable>]
    type OcrTextLayerSample =
        { Qpc: int64
          FrameIndex: int
          PtsTicks: int64
          DurationTicks: int64
          Ocr: OcrResult }

    [<CLIMutable>]
    type UiAffordanceLayerSample =
        { Qpc: int64
          FrameIndex: int
          PtsTicks: int64
          DurationTicks: int64
          Hints: UiNavigationHint array }

    [<CLIMutable>]
    type MdkgAttribute =
        { Key: string
          Value: string }

    [<CLIMutable>]
    type SemanticGraphNode =
        { Id: string
          Kind: string
          Label: string
          Confidence: float
          HasBounds: bool
          Bounds: TextBounds
          FirstPtsTicks: int64
          LastPtsTicks: int64
          Attributes: MdkgAttribute array }

    [<CLIMutable>]
    type SemanticGraphEdge =
        { Id: string
          Kind: string
          SourceNodeId: string
          TargetNodeId: string
          Confidence: float
          FirstPtsTicks: int64
          LastPtsTicks: int64
          Attributes: MdkgAttribute array }

    [<CLIMutable>]
    type SemanticGraphDelta =
        { AddNodes: SemanticGraphNode array
          UpdateNodes: SemanticGraphNode array
          RemoveNodeIds: string array
          AddEdges: SemanticGraphEdge array
          UpdateEdges: SemanticGraphEdge array
          RemoveEdgeIds: string array }

    [<CLIMutable>]
    type SemanticGraphLayerSample =
        { Qpc: int64
          FrameIndex: int
          PtsTicks: int64
          DurationTicks: int64
          GraphId: string
          IsKeyFrame: bool
          KeyFrameNodes: SemanticGraphNode array
          KeyFrameEdges: SemanticGraphEdge array
          Delta: SemanticGraphDelta }

    [<CLIMutable>]
    type InputEvent =
        { EventId: string
          Kind: string
          Qpc: int64
          PtsTicks: int64
          Attributes: MdkgAttribute array }

    [<CLIMutable>]
    type AppFocusChange =
        { Qpc: int64
          PtsTicks: int64
          ProcessId: int
          ProcessName: string
          WindowTitle: string
          WindowId: string }

    [<CLIMutable>]
    type InputEventLayerSample =
        { PtsTicks: int64
          DurationTicks: int64
          Events: InputEvent array
          FocusChanges: AppFocusChange array }

    [<CLIMutable>]
    type ArtFrameLayerSample =
        { Qpc: int64
          FrameIndex: int64
          PtsTicks: int64
          DurationTicks: int64
          Telemetry: ArtFrameTelemetry }

    [<CLIMutable>]
    type AcpContextLayerSample =
        { PtsTicks: int64
          DurationTicks: int64
          Context: AcpContextEnvelope }

    [<CLIMutable>]
    type ApimTraceLayerSample =
        { PtsTicks: int64
          DurationTicks: int64
          Trace: ApimTraceTelemetry }

    [<CLIMutable>]
    type LiveTileLayerSample =
        { PtsTicks: int64
          DurationTicks: int64
          Definitions: LiveTileWidgetDefinition array
          States: LiveTileWidgetState array
          Attributes: SemanticAttribute array }

    [<CLIMutable>]
    type WidgetStudioLayerSample =
        { PtsTicks: int64
          DurationTicks: int64
          Project: WidgetStudioProject }

    [<CLIMutable>]
    type ExtensionCatalogLayerSample =
        { PtsTicks: int64
          DurationTicks: int64
          Catalog: ExtensionCatalog }

    [<CLIMutable>]
    type MdkgCarrierSample =
        { LayerId: uint32
          PtsTicks: int64
          DurationTicks: int64
          Flags: uint32
          Codec: string
          Payload: byte array }

    [<CLIMutable>]
    type MdkgHeader =
        { MajorVersion: uint16
          MinorVersion: uint16
          HeaderLength: uint32
          MovieTimescale: uint64
          LayerTableOffset: uint64
          LayerTableLength: uint64
          LayerCount: uint32
          SampleIndexOffset: uint64
          SampleIndexLength: uint64
          DataOffset: uint64
          DataLength: uint64
          FileFlags: uint32 }

    let private jsonOptions =
        let options =
            JsonSerializerOptions(
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false)

        options.PropertyNameCaseInsensitive <- true
        options

    let serializeJson<'T> (value: 'T) =
        JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions)

    let deserializeJson<'T when 'T: not struct> (payload: byte array) : 'T =
        if Object.ReferenceEquals(payload, null) then
            invalidArg (nameof payload) "JSON payload must not be null."

        match JsonSerializer.Deserialize<'T>(payload, jsonOptions) with
        | null -> invalidData "JSON payload deserialized to null."
        | value -> value

    module Validation =

        let private isNullReference (value: obj) =
            Object.ReferenceEquals(value, null)

        let private requireObject field (value: obj) =
            if isNullReference value then
                invalidArg field $"{field} must not be null."

        let private requireText field (value: string) =
            if String.IsNullOrWhiteSpace(value) then
                invalidArg field $"{field} is required."

        let private requireNonNegativeInt field value =
            if value < 0 then
                invalidArg field $"{field} must be non-negative."

        let private requirePositiveInt field value =
            if value <= 0 then
                invalidArg field $"{field} must be positive."

        let private requireNonNegativeInt64 field value =
            if value < 0L then
                invalidArg field $"{field} must be non-negative."

        let private requireFinite field value =
            if Double.IsNaN(value) || Double.IsInfinity(value) then
                invalidArg field $"{field} must be finite."

        let private requireRange field minimum maximum value =
            requireFinite field value

            if value < minimum || value > maximum then
                invalidArg field $"{field} must be between {minimum} and {maximum}."

        let private requireArray field (values: 'T array) =
            if isNullReference values then
                invalidArg field $"{field} must not be null."

            values

        let private validateItems field validate (items: 'T array) =
            let values = requireArray field items

            values
            |> Array.iteri (fun index item -> validate $"{field}[{index}]" item |> ignore)

            values

        let private requireStringArray field (items: string array) =
            let values = requireArray field items

            values
            |> Array.iteri (fun index item ->
                if isNullReference item then
                    invalidArg $"{field}[{index}]" $"{field}[{index}] must not be null.")

            values

        let private validateJsonString field allowBlank (value: string) =
            if String.IsNullOrWhiteSpace(value) then
                if not allowBlank then
                    invalidArg field $"{field} must contain valid JSON."
            else
                try
                    use _document = JsonDocument.Parse(value)
                    ()
                with :? JsonException as ex ->
                    invalidArg field $"{field} must contain valid JSON. {ex.Message}"

        let private validateSampleClock field ptsTicks durationTicks =
            requireNonNegativeInt64 $"{field}.ptsTicks" ptsTicks
            requireNonNegativeInt64 $"{field}.durationTicks" durationTicks

        let private validateTextPoint field (point: TextPoint) =
            requireObject field point
            requireFinite $"{field}.x" point.X
            requireFinite $"{field}.y" point.Y
            point

        let private validateTextBounds field (bounds: TextBounds) =
            requireObject field bounds
            validateTextPoint $"{field}.topLeft" bounds.TopLeft |> ignore
            validateTextPoint $"{field}.topRight" bounds.TopRight |> ignore
            validateTextPoint $"{field}.bottomRight" bounds.BottomRight |> ignore
            validateTextPoint $"{field}.bottomLeft" bounds.BottomLeft |> ignore
            bounds

        let private validateOcrWord field (word: OcrWord) =
            requireObject field word
            requireText $"{field}.text" word.Text
            requireRange $"{field}.confidence" 0.0 1.0 word.Confidence
            validateTextBounds $"{field}.boundingBox" word.BoundingBox |> ignore
            word

        let private validateOcrLine field (line: OcrLine) =
            requireObject field line
            requireText $"{field}.text" line.Text
            validateItems $"{field}.words" validateOcrWord line.Words |> ignore
            line

        let private validateOcrResult field (ocr: OcrResult) =
            requireObject field ocr
            requireText $"{field}.provider" ocr.Provider
            validateItems $"{field}.lines" validateOcrLine ocr.Lines |> ignore
            ocr

        let private validateUiNavigationHint field (hint: UiNavigationHint) =
            requireObject field hint
            requireText $"{field}.kind" hint.Kind
            requireText $"{field}.label" hint.Label
            requireRange $"{field}.confidence" 0.0 1.0 hint.Confidence
            validateTextBounds $"{field}.bounds" hint.Bounds |> ignore
            hint

        let validateOcrTextLayerSample field (sample: OcrTextLayerSample) =
            requireObject field sample
            requireNonNegativeInt64 $"{field}.qpc" sample.Qpc
            requireNonNegativeInt $"{field}.frameIndex" sample.FrameIndex
            validateSampleClock field sample.PtsTicks sample.DurationTicks
            validateOcrResult $"{field}.ocr" sample.Ocr |> ignore
            sample

        let validateUiAffordanceLayerSample field (sample: UiAffordanceLayerSample) =
            requireObject field sample
            requireNonNegativeInt64 $"{field}.qpc" sample.Qpc
            requireNonNegativeInt $"{field}.frameIndex" sample.FrameIndex
            validateSampleClock field sample.PtsTicks sample.DurationTicks
            validateItems $"{field}.hints" validateUiNavigationHint sample.Hints |> ignore
            sample

        let validateMdkgAttribute field (attribute: MdkgAttribute) =
            requireObject field attribute
            requireText $"{field}.key" attribute.Key
            attribute

        let validateSemanticGraphNode field (node: SemanticGraphNode) =
            requireObject field node
            requireText $"{field}.id" node.Id
            requireText $"{field}.kind" node.Kind
            requireRange $"{field}.confidence" 0.0 1.0 node.Confidence
            if node.HasBounds then
                validateTextBounds $"{field}.bounds" node.Bounds |> ignore
            requireNonNegativeInt64 $"{field}.firstPtsTicks" node.FirstPtsTicks
            requireNonNegativeInt64 $"{field}.lastPtsTicks" node.LastPtsTicks
            validateItems $"{field}.attributes" validateMdkgAttribute node.Attributes |> ignore
            node

        let validateSemanticGraphEdge field (edge: SemanticGraphEdge) =
            requireObject field edge
            requireText $"{field}.id" edge.Id
            requireText $"{field}.kind" edge.Kind
            requireText $"{field}.sourceNodeId" edge.SourceNodeId
            requireText $"{field}.targetNodeId" edge.TargetNodeId
            requireRange $"{field}.confidence" 0.0 1.0 edge.Confidence
            requireNonNegativeInt64 $"{field}.firstPtsTicks" edge.FirstPtsTicks
            requireNonNegativeInt64 $"{field}.lastPtsTicks" edge.LastPtsTicks
            validateItems $"{field}.attributes" validateMdkgAttribute edge.Attributes |> ignore
            edge

        let validateSemanticGraphDelta field (delta: SemanticGraphDelta) =
            requireObject field delta
            validateItems $"{field}.addNodes" validateSemanticGraphNode delta.AddNodes |> ignore
            validateItems $"{field}.updateNodes" validateSemanticGraphNode delta.UpdateNodes |> ignore
            requireStringArray $"{field}.removeNodeIds" delta.RemoveNodeIds |> ignore
            validateItems $"{field}.addEdges" validateSemanticGraphEdge delta.AddEdges |> ignore
            validateItems $"{field}.updateEdges" validateSemanticGraphEdge delta.UpdateEdges |> ignore
            requireStringArray $"{field}.removeEdgeIds" delta.RemoveEdgeIds |> ignore
            delta

        let validateSemanticGraphLayerSample field (sample: SemanticGraphLayerSample) =
            requireObject field sample
            requireNonNegativeInt64 $"{field}.qpc" sample.Qpc
            requireNonNegativeInt $"{field}.frameIndex" sample.FrameIndex
            validateSampleClock field sample.PtsTicks sample.DurationTicks
            requireText $"{field}.graphId" sample.GraphId
            validateItems $"{field}.keyFrameNodes" validateSemanticGraphNode sample.KeyFrameNodes |> ignore
            validateItems $"{field}.keyFrameEdges" validateSemanticGraphEdge sample.KeyFrameEdges |> ignore
            validateSemanticGraphDelta $"{field}.delta" sample.Delta |> ignore
            sample

        let validateSemanticIdentifier field (id: SemanticIdentifier) =
            requireObject field id
            requireText $"{field}.namespace" id.Namespace
            requireText $"{field}.kind" id.Kind
            requireText $"{field}.key" id.Key
            requireText $"{field}.version" id.Version
            id

        let validateSemanticConfidence field (confidence: SemanticConfidence) =
            requireObject field confidence
            requireRange $"{field}.score" 0.0 1.0 confidence.Score
            requireText $"{field}.source" confidence.Source
            confidence

        let validateNormalizedRect field (rect: NormalizedRect) =
            requireObject field rect
            requireRange $"{field}.x" 0.0 1.0 rect.X
            requireRange $"{field}.y" 0.0 1.0 rect.Y
            requireRange $"{field}.width" 0.0 1.0 rect.Width
            requireRange $"{field}.height" 0.0 1.0 rect.Height

            if rect.X + rect.Width > 1.000001 then
                invalidArg field $"{field} exceeds normalized width."

            if rect.Y + rect.Height > 1.000001 then
                invalidArg field $"{field} exceeds normalized height."

            rect

        let validateSemanticAttribute field (attribute: SemanticAttribute) =
            requireObject field attribute
            requireText $"{field}.name" attribute.Name
            requireText $"{field}.valueType" attribute.ValueType
            validateSemanticConfidence $"{field}.confidence" attribute.Confidence |> ignore
            attribute

        let validateSemanticNode field (node: SemanticNode) =
            requireObject field node
            validateSemanticIdentifier $"{field}.id" node.Id |> ignore
            requireText $"{field}.kind" node.Kind

            if node.HasBounds then
                validateNormalizedRect $"{field}.bounds" node.Bounds |> ignore

            validateItems $"{field}.attributes" validateSemanticAttribute node.Attributes |> ignore
            node

        let validateSemanticEdge field (edge: SemanticEdge) =
            requireObject field edge
            validateSemanticIdentifier $"{field}.id" edge.Id |> ignore
            validateSemanticIdentifier $"{field}.sourceId" edge.SourceId |> ignore
            validateSemanticIdentifier $"{field}.targetId" edge.TargetId |> ignore
            requireText $"{field}.relation" edge.Relation
            validateSemanticConfidence $"{field}.confidence" edge.Confidence |> ignore
            validateItems $"{field}.attributes" validateSemanticAttribute edge.Attributes |> ignore
            edge

        let validateSemanticGraphSnapshot field (snapshot: SemanticGraphSnapshot) =
            requireObject field snapshot
            validateSemanticIdentifier $"{field}.graphId" snapshot.GraphId |> ignore
            validateItems $"{field}.nodes" validateSemanticNode snapshot.Nodes |> ignore
            validateItems $"{field}.edges" validateSemanticEdge snapshot.Edges |> ignore
            validateItems $"{field}.attributes" validateSemanticAttribute snapshot.Attributes |> ignore
            snapshot

        let validateApimBackend field (backend: ApimBackend) =
            requireObject field backend
            validateSemanticIdentifier $"{field}.id" backend.Id |> ignore
            requireText $"{field}.displayName" backend.DisplayName
            requireText $"{field}.baseUrl" backend.BaseUrl
            requireText $"{field}.authScheme" backend.AuthScheme
            requireStringArray $"{field}.tags" backend.Tags |> ignore
            backend

        let validateApimOperation field (operation: ApimOperation) =
            requireObject field operation
            validateSemanticIdentifier $"{field}.id" operation.Id |> ignore
            validateSemanticIdentifier $"{field}.backendId" operation.BackendId |> ignore
            requireText $"{field}.method" operation.Method
            requireText $"{field}.route" operation.Route
            requireText $"{field}.schemaUri" operation.SchemaUri
            requireStringArray $"{field}.tags" operation.Tags |> ignore
            operation

        let validateApimPolicyBinding field (policy: ApimPolicyBinding) =
            requireObject field policy
            validateSemanticIdentifier $"{field}.id" policy.Id |> ignore
            validateSemanticIdentifier $"{field}.operationId" policy.OperationId |> ignore
            requireText $"{field}.policyName" policy.PolicyName
            requireText $"{field}.appliesTo" policy.AppliesTo
            validateJsonString $"{field}.policyJson" false policy.PolicyJson
            policy

        let validateApimTraceTelemetry field (trace: ApimTraceTelemetry) =
            requireObject field trace
            validateSemanticIdentifier $"{field}.traceId" trace.TraceId |> ignore
            validateApimBackend $"{field}.backend" trace.Backend |> ignore
            validateApimOperation $"{field}.operation" trace.Operation |> ignore
            validateItems $"{field}.policies" validateApimPolicyBinding trace.Policies |> ignore

            if trace.StatusCode <> 0 && (trace.StatusCode < 100 || trace.StatusCode > 599) then
                invalidArg $"{field}.statusCode" $"{field}.statusCode must be 0 or an HTTP status code."

            requireNonNegativeInt64 $"{field}.durationMs" trace.DurationMs
            requireText $"{field}.outcome" trace.Outcome
            validateJsonString $"{field}.traceJson" true trace.TraceJson
            validateItems $"{field}.attributes" validateSemanticAttribute trace.Attributes |> ignore
            trace

        let validateAcpContextEnvelope field (context: AcpContextEnvelope) =
            requireObject field context
            validateSemanticIdentifier $"{field}.contextId" context.ContextId |> ignore
            requireText $"{field}.producer" context.Producer
            requireText $"{field}.schemaUri" context.SchemaUri
            requireText $"{field}.capability" context.Capability
            validateJsonString $"{field}.payloadJson" false context.PayloadJson
            validateItems $"{field}.attributes" validateSemanticAttribute context.Attributes |> ignore
            context

        let validateArtFrameTelemetry field (telemetry: ArtFrameTelemetry) =
            requireObject field telemetry
            validateSemanticIdentifier $"{field}.streamId" telemetry.StreamId |> ignore
            requireNonNegativeInt64 $"{field}.frameIndex" telemetry.FrameIndex
            requirePositiveInt $"{field}.width" telemetry.Width
            requirePositiveInt $"{field}.height" telemetry.Height
            requireRange $"{field}.changeScore" 0.0 1.0 telemetry.ChangeScore
            validateItems $"{field}.dirtyRegions" validateNormalizedRect telemetry.DirtyRegions |> ignore
            requireObject $"{field}.ocr" telemetry.Ocr
            requireArray $"{field}.uiAffordances" telemetry.UiAffordances |> ignore
            validateSemanticGraphSnapshot $"{field}.semanticGraph" telemetry.SemanticGraph |> ignore
            telemetry

        let validateLiveTileWidgetDefinition field (definition: LiveTileWidgetDefinition) =
            requireObject field definition
            validateSemanticIdentifier $"{field}.widgetId" definition.WidgetId |> ignore
            requireText $"{field}.displayName" definition.DisplayName
            requireText $"{field}.widgetKind" definition.WidgetKind
            requireText $"{field}.schemaUri" definition.SchemaUri
            requirePositiveInt $"{field}.defaultRefreshIntervalMs" definition.DefaultRefreshIntervalMs
            validateItems $"{field}.apimOperations" validateApimOperation definition.ApimOperations |> ignore
            requireStringArray $"{field}.tags" definition.Tags |> ignore
            definition

        let validateLiveTileWidgetState field (state: LiveTileWidgetState) =
            requireObject field state
            validateSemanticIdentifier $"{field}.widgetId" state.WidgetId |> ignore
            validateSemanticIdentifier $"{field}.instanceId" state.InstanceId |> ignore
            requireText $"{field}.status" state.Status
            requirePositiveInt $"{field}.refreshIntervalMs" state.RefreshIntervalMs
            validateJsonString $"{field}.dataJson" false state.DataJson
            validateSemanticGraphSnapshot $"{field}.semanticGraph" state.SemanticGraph |> ignore
            state

        let validateWidgetStudioProject field (project: WidgetStudioProject) =
            requireObject field project
            validateSemanticIdentifier $"{field}.projectId" project.ProjectId |> ignore
            requireText $"{field}.name" project.Name
            validateItems $"{field}.widgets" validateLiveTileWidgetDefinition project.Widgets |> ignore
            validateItems $"{field}.apimBackends" validateApimBackend project.ApimBackends |> ignore
            validateItems $"{field}.apimPolicies" validateApimPolicyBinding project.ApimPolicies |> ignore
            project

        let validateArtFrameLayerSample field (sample: ArtFrameLayerSample) =
            requireObject field sample
            requireNonNegativeInt64 $"{field}.qpc" sample.Qpc
            requireNonNegativeInt64 $"{field}.frameIndex" sample.FrameIndex
            validateSampleClock field sample.PtsTicks sample.DurationTicks
            validateArtFrameTelemetry $"{field}.telemetry" sample.Telemetry |> ignore
            sample

        let validateAcpContextLayerSample field (sample: AcpContextLayerSample) =
            requireObject field sample
            validateSampleClock field sample.PtsTicks sample.DurationTicks
            validateAcpContextEnvelope $"{field}.context" sample.Context |> ignore
            sample

        let validateApimTraceLayerSample field (sample: ApimTraceLayerSample) =
            requireObject field sample
            validateSampleClock field sample.PtsTicks sample.DurationTicks
            validateApimTraceTelemetry $"{field}.trace" sample.Trace |> ignore
            sample

        let validateLiveTileLayerSample field (sample: LiveTileLayerSample) =
            requireObject field sample
            validateSampleClock field sample.PtsTicks sample.DurationTicks
            validateItems $"{field}.definitions" validateLiveTileWidgetDefinition sample.Definitions |> ignore
            validateItems $"{field}.states" validateLiveTileWidgetState sample.States |> ignore
            validateItems $"{field}.attributes" validateSemanticAttribute sample.Attributes |> ignore
            sample

        let validateWidgetStudioLayerSample field (sample: WidgetStudioLayerSample) =
            requireObject field sample
            validateSampleClock field sample.PtsTicks sample.DurationTicks
            validateWidgetStudioProject $"{field}.project" sample.Project |> ignore
            sample

        let validateExtensionInvocation field (invocation: ExtensionInvocation) =
            requireObject field invocation
            requireText $"{field}.transport" invocation.Transport
            requireStringArray $"{field}.arguments" invocation.Arguments |> ignore
            invocation

        let validateExtensionCapability field (capability: ExtensionCapability) =
            requireObject field capability
            validateSemanticIdentifier $"{field}.id" capability.Id |> ignore
            requireText $"{field}.kind" capability.Kind
            requireText $"{field}.displayName" capability.DisplayName
            requireText $"{field}.provider" capability.Provider
            requireText $"{field}.status" capability.Status
            validateExtensionInvocation $"{field}.invocation" capability.Invocation |> ignore
            requireStringArray $"{field}.tags" capability.Tags |> ignore
            validateItems $"{field}.attributes" validateSemanticAttribute capability.Attributes |> ignore
            capability

        let validateExtensionManifest field (manifest: ExtensionManifest) =
            requireObject field manifest
            validateSemanticIdentifier $"{field}.id" manifest.Id |> ignore
            requireText $"{field}.name" manifest.Name
            requireText $"{field}.version" manifest.Version
            requireText $"{field}.schemaUri" manifest.SchemaUri
            validateItems $"{field}.capabilities" validateExtensionCapability manifest.Capabilities |> ignore
            manifest

        let validateExtensionCatalog field (catalog: ExtensionCatalog) =
            requireObject field catalog
            validateSemanticIdentifier $"{field}.catalogId" catalog.CatalogId |> ignore
            requireText $"{field}.host" catalog.Host
            validateItems $"{field}.manifests" validateExtensionManifest catalog.Manifests |> ignore
            validateItems $"{field}.capabilities" validateExtensionCapability catalog.Capabilities |> ignore
            catalog

        let validateExtensionCatalogLayerSample field (sample: ExtensionCatalogLayerSample) =
            requireObject field sample
            validateSampleClock field sample.PtsTicks sample.DurationTicks
            validateExtensionCatalog $"{field}.catalog" sample.Catalog |> ignore
            sample

    module LayerPayloads =

        let serializeOcrText (sample: OcrTextLayerSample) =
            Validation.validateOcrTextLayerSample "ocr-text" sample |> ignore
            serializeJson sample

        let deserializeOcrText payload =
            let sample = deserializeJson<OcrTextLayerSample> payload
            Validation.validateOcrTextLayerSample "ocr-text" sample |> ignore
            sample

        let serializeUiAffordance (sample: UiAffordanceLayerSample) =
            Validation.validateUiAffordanceLayerSample "ui-affordance" sample |> ignore
            serializeJson sample

        let deserializeUiAffordance payload =
            let sample = deserializeJson<UiAffordanceLayerSample> payload
            Validation.validateUiAffordanceLayerSample "ui-affordance" sample |> ignore
            sample

        let serializeSemanticGraph (sample: SemanticGraphLayerSample) =
            Validation.validateSemanticGraphLayerSample "semantic-graph" sample |> ignore
            serializeJson sample

        let deserializeSemanticGraph payload =
            let sample = deserializeJson<SemanticGraphLayerSample> payload
            Validation.validateSemanticGraphLayerSample "semantic-graph" sample |> ignore
            sample

        let serializeArtFrame (sample: ArtFrameLayerSample) =
            Validation.validateArtFrameLayerSample "art-frame" sample |> ignore
            serializeJson sample

        let deserializeArtFrame payload =
            let sample = deserializeJson<ArtFrameLayerSample> payload
            Validation.validateArtFrameLayerSample "art-frame" sample |> ignore
            sample

        let serializeAcpContext (sample: AcpContextLayerSample) =
            Validation.validateAcpContextLayerSample "acp-context" sample |> ignore
            serializeJson sample

        let deserializeAcpContext payload =
            let sample = deserializeJson<AcpContextLayerSample> payload
            Validation.validateAcpContextLayerSample "acp-context" sample |> ignore
            sample

        let serializeApimTrace (sample: ApimTraceLayerSample) =
            Validation.validateApimTraceLayerSample "apim-trace" sample |> ignore
            serializeJson sample

        let deserializeApimTrace payload =
            let sample = deserializeJson<ApimTraceLayerSample> payload
            Validation.validateApimTraceLayerSample "apim-trace" sample |> ignore
            sample

        let serializeLiveTile (sample: LiveTileLayerSample) =
            Validation.validateLiveTileLayerSample "live-tile" sample |> ignore
            serializeJson sample

        let deserializeLiveTile payload =
            let sample = deserializeJson<LiveTileLayerSample> payload
            Validation.validateLiveTileLayerSample "live-tile" sample |> ignore
            sample

        let serializeWidgetStudio (sample: WidgetStudioLayerSample) =
            Validation.validateWidgetStudioLayerSample "widget-studio" sample |> ignore
            serializeJson sample

        let deserializeWidgetStudio payload =
            let sample = deserializeJson<WidgetStudioLayerSample> payload
            Validation.validateWidgetStudioLayerSample "widget-studio" sample |> ignore
            sample

        let serializeExtensionCatalog (sample: ExtensionCatalogLayerSample) =
            Validation.validateExtensionCatalogLayerSample "extension-catalog" sample |> ignore
            serializeJson sample

        let deserializeExtensionCatalog payload =
            let sample = deserializeJson<ExtensionCatalogLayerSample> payload
            Validation.validateExtensionCatalogLayerSample "extension-catalog" sample |> ignore
            sample

    module Producers =

        let semanticIdentifier ns kind key version =
            let id =
                { Namespace = ns
                  Kind = kind
                  Key = key
                  Version = version }

            Validation.validateSemanticIdentifier "id" id |> ignore
            id

        let semanticConfidence score source model timestampUtc =
            let confidence =
                { Score = score
                  Source = source
                  Model = model
                  TimestampUtc = timestampUtc }

            Validation.validateSemanticConfidence "confidence" confidence |> ignore
            confidence

        let semanticAttribute name value valueType confidence =
            let attribute =
                { Name = name
                  Value = value
                  ValueType = valueType
                  Confidence = confidence }

            Validation.validateSemanticAttribute "attribute" attribute |> ignore
            attribute

        let ocrTextLayerSample qpc frameIndex ptsTicks durationTicks ocr =
            let sample =
                { Qpc = qpc
                  FrameIndex = frameIndex
                  PtsTicks = ptsTicks
                  DurationTicks = durationTicks
                  Ocr = ocr }

            Validation.validateOcrTextLayerSample "ocr-text" sample |> ignore
            sample

        let uiAffordanceLayerSample qpc frameIndex ptsTicks durationTicks hints =
            let sample =
                { Qpc = qpc
                  FrameIndex = frameIndex
                  PtsTicks = ptsTicks
                  DurationTicks = durationTicks
                  Hints = hints }

            Validation.validateUiAffordanceLayerSample "ui-affordance" sample |> ignore
            sample

        let mdkgAttribute key value =
            let attribute = { Key = key; Value = value }
            Validation.validateMdkgAttribute "attribute" attribute |> ignore
            attribute

        let semanticGraphDelta addNodes updateNodes removeNodeIds addEdges updateEdges removeEdgeIds =
            let delta =
                { AddNodes = addNodes
                  UpdateNodes = updateNodes
                  RemoveNodeIds = removeNodeIds
                  AddEdges = addEdges
                  UpdateEdges = updateEdges
                  RemoveEdgeIds = removeEdgeIds }

            Validation.validateSemanticGraphDelta "semantic-graph.delta" delta |> ignore
            delta

        let emptySemanticGraphDelta () =
            semanticGraphDelta Array.empty Array.empty Array.empty Array.empty Array.empty Array.empty

        let semanticGraphNode id kind label confidence hasBounds bounds firstPtsTicks lastPtsTicks attributes =
            let node =
                { Id = id
                  Kind = kind
                  Label = label
                  Confidence = confidence
                  HasBounds = hasBounds
                  Bounds = bounds
                  FirstPtsTicks = firstPtsTicks
                  LastPtsTicks = lastPtsTicks
                  Attributes = attributes }

            Validation.validateSemanticGraphNode "semantic-graph.node" node |> ignore
            node

        let semanticGraphEdge id kind sourceNodeId targetNodeId confidence firstPtsTicks lastPtsTicks attributes =
            let edge =
                { Id = id
                  Kind = kind
                  SourceNodeId = sourceNodeId
                  TargetNodeId = targetNodeId
                  Confidence = confidence
                  FirstPtsTicks = firstPtsTicks
                  LastPtsTicks = lastPtsTicks
                  Attributes = attributes }

            Validation.validateSemanticGraphEdge "semantic-graph.edge" edge |> ignore
            edge

        let semanticGraphLayerSample qpc frameIndex ptsTicks durationTicks graphId isKeyFrame keyFrameNodes keyFrameEdges delta =
            let sample =
                { Qpc = qpc
                  FrameIndex = frameIndex
                  PtsTicks = ptsTicks
                  DurationTicks = durationTicks
                  GraphId = graphId
                  IsKeyFrame = isKeyFrame
                  KeyFrameNodes = keyFrameNodes
                  KeyFrameEdges = keyFrameEdges
                  Delta = delta }

            Validation.validateSemanticGraphLayerSample "semantic-graph" sample |> ignore
            sample

        let artFrameLayerSample qpc frameIndex ptsTicks durationTicks telemetry =
            let sample =
                { Qpc = qpc
                  FrameIndex = frameIndex
                  PtsTicks = ptsTicks
                  DurationTicks = durationTicks
                  Telemetry = telemetry }

            Validation.validateArtFrameLayerSample "art-frame" sample |> ignore
            sample

        let acpContextLayerSample ptsTicks durationTicks context =
            let sample =
                { PtsTicks = ptsTicks
                  DurationTicks = durationTicks
                  Context = context }

            Validation.validateAcpContextLayerSample "acp-context" sample |> ignore
            sample

        let apimTraceLayerSample ptsTicks durationTicks trace =
            let sample =
                { PtsTicks = ptsTicks
                  DurationTicks = durationTicks
                  Trace = trace }

            Validation.validateApimTraceLayerSample "apim-trace" sample |> ignore
            sample

        let liveTileLayerSample ptsTicks durationTicks definitions states attributes =
            let sample =
                { PtsTicks = ptsTicks
                  DurationTicks = durationTicks
                  Definitions = definitions
                  States = states
                  Attributes = attributes }

            Validation.validateLiveTileLayerSample "live-tile" sample |> ignore
            sample

        let widgetStudioLayerSample ptsTicks durationTicks project =
            let sample =
                { PtsTicks = ptsTicks
                  DurationTicks = durationTicks
                  Project = project }

            Validation.validateWidgetStudioLayerSample "widget-studio" sample |> ignore
            sample

        let extensionCatalogLayerSample ptsTicks durationTicks catalog =
            let sample =
                { PtsTicks = ptsTicks
                  DurationTicks = durationTicks
                  Catalog = catalog }

            Validation.validateExtensionCatalogLayerSample "extension-catalog" sample |> ignore
            sample

    [<CLIMutable>]
    type ReconstructedSemanticGraph =
        { GraphId: string
          Nodes: SemanticGraphNode array
          Edges: SemanticGraphEdge array
          LastPtsTicks: int64 }

    module Graph =

        let reconstruct (samples: SemanticGraphLayerSample seq) =
            if isNull (box samples) then
                invalidArg (nameof samples) "Semantic graph samples must not be null."

            let nodes = Dictionary<string, SemanticGraphNode>(StringComparer.Ordinal)
            let edges = Dictionary<string, SemanticGraphEdge>(StringComparer.Ordinal)
            let mutable graphId = ""
            let mutable lastPtsTicks = 0L

            let upsertNode (node: SemanticGraphNode) =
                nodes[node.Id] <- node

            let upsertEdge (edge: SemanticGraphEdge) =
                edges[edge.Id] <- edge

            samples
            |> Seq.sortBy (fun sample -> sample.PtsTicks)
            |> Seq.iter (fun sample ->
                Validation.validateSemanticGraphLayerSample "semantic-graph" sample |> ignore
                graphId <- sample.GraphId
                lastPtsTicks <- sample.PtsTicks

                if sample.IsKeyFrame then
                    nodes.Clear()
                    edges.Clear()
                    sample.KeyFrameNodes |> Array.iter upsertNode
                    sample.KeyFrameEdges |> Array.iter upsertEdge

                sample.Delta.RemoveEdgeIds |> Array.iter (edges.Remove >> ignore)
                sample.Delta.RemoveNodeIds
                |> Array.iter (fun nodeId ->
                    nodes.Remove nodeId |> ignore
                    edges
                    |> Seq.filter (fun pair -> pair.Value.SourceNodeId = nodeId || pair.Value.TargetNodeId = nodeId)
                    |> Seq.map (fun pair -> pair.Key)
                    |> Seq.toArray
                    |> Array.iter (edges.Remove >> ignore))

                sample.Delta.AddNodes |> Array.iter upsertNode
                sample.Delta.UpdateNodes |> Array.iter upsertNode
                sample.Delta.AddEdges |> Array.iter upsertEdge
                sample.Delta.UpdateEdges |> Array.iter upsertEdge)

            { GraphId = graphId
              Nodes = nodes.Values |> Seq.toArray
              Edges = edges.Values |> Seq.toArray
              LastPtsTicks = lastPtsTicks }

    let frameDurationTicksFromFps fps =
        if fps <= 0 then
            invalidArg (nameof fps) "fps must be positive."

        int64 (Math.Round(float MovieTimescale / float fps))

    let qpcFrequency () =
        let mutable frequency = 0L

        if not (QueryPerformanceFrequency(&frequency)) || frequency <= 0L then
            failwith "QueryPerformanceFrequency failed."

        frequency

    let qpcToPtsTicks (frequency: int64) (firstQpc: int64) (qpc: int64) =
        if frequency <= 0L then
            invalidArg (nameof frequency) "QPC frequency must be positive."

        let delta = qpc - firstQpc

        if delta < 0L then
            invalidArg (nameof qpc) "QPC must be greater than or equal to firstQpc."

        (delta * MovieTimescale) / frequency

    module Registry =

        let Timing =
            { Id = LayerId.Timing
              Name = "timing"
              SchemaUri = "urn:yumlog:mdkg:layer:timing:v1"
              Codec = "application/vnd.yumlog.mdkg.timing+json;version=1"
              Flags = uint32 MdkgLayerFlags.Required
              Description = "Dense per-frame timing layer: QPC, frame index, FPS, PTS, duration, gaps, and drops." }

        let OcrText =
            { Id = LayerId.OcrText
              Name = "ocr-text"
              SchemaUri = "urn:yumlog:mdkg:layer:ocr-text:v1"
              Codec = "application/vnd.yumlog.mdkg.ocr-text+json;version=1"
              Flags = uint32 MdkgLayerFlags.Sparse
              Description = "Sparse or dense OCR layer using Yumlog.Native OcrResult/OcrLine/OcrWord shapes." }

        let UiAffordance =
            { Id = LayerId.UiAffordance
              Name = "ui-affordance"
              SchemaUri = "urn:yumlog:mdkg:layer:ui-affordance:v1"
              Codec = "application/vnd.yumlog.mdkg.ui-affordance+json;version=1"
              Flags = uint32 MdkgLayerFlags.Sparse
              Description = "Sparse or dense UI navigation affordance layer using UiNavigationHint records." }

        let SemanticGraph =
            { Id = LayerId.SemanticGraph
              Name = "semantic-graph"
              SchemaUri = "urn:yumlog:mdkg:layer:semantic-graph:v1"
              Codec = "application/vnd.yumlog.mdkg.semantic-graph+json;version=1"
              Flags = uint32 (MdkgLayerFlags.Sparse ||| MdkgLayerFlags.Graph)
              Description = "Knowledge graph layer: keyframe graph states plus per-sample graph deltas." }

        let InputEvent =
            { Id = LayerId.InputEvent
              Name = "input-event"
              SchemaUri = "urn:yumlog:mdkg:layer:input-event:v1"
              Codec = "application/vnd.yumlog.mdkg.input-event+json;version=1"
              Flags = uint32 (MdkgLayerFlags.Sparse ||| MdkgLayerFlags.Extension)
              Description = "Optional input and app-focus event layer." }

        let ArtFrame =
            { Id = LayerId.ArtFrame
              Name = "art-frame"
              SchemaUri = "urn:yumlog:mdkg:layer:art-frame:v1"
              Codec = "application/vnd.yumlog.mdkg.art-frame+json;version=1"
              Flags = uint32 (MdkgLayerFlags.Sparse ||| MdkgLayerFlags.Extension)
              Description = "Adaptive/Agent Real Time frame telemetry: dirty regions, OCR, UI affordances, and graph snapshot references." }

        let AcpContext =
            { Id = LayerId.AcpContext
              Name = "acp-context"
              SchemaUri = "urn:yumlog:mdkg:layer:acp-context:v1"
              Codec = "application/vnd.yumlog.mdkg.acp-context+json;version=1"
              Flags = uint32 (MdkgLayerFlags.Sparse ||| MdkgLayerFlags.Extension)
              Description = "Adaptive Context Protocol envelopes for agents, apps, runtime schemas, and contextual payloads." }

        let ApimTrace =
            { Id = LayerId.ApimTrace
              Name = "apim-trace"
              SchemaUri = "urn:yumlog:mdkg:layer:apim-trace:v1"
              Codec = "application/vnd.yumlog.mdkg.apim-trace+json;version=1"
              Flags = uint32 (MdkgLayerFlags.Sparse ||| MdkgLayerFlags.Extension)
              Description = "Azure API Management operation, policy, route, and backend trace metadata aligned to semantic graph nodes." }

        let LiveTile =
            { Id = LayerId.LiveTile
              Name = "live-tile"
              SchemaUri = "urn:yumlog:mdkg:layer:live-tile:v1"
              Codec = "application/vnd.yumlog.mdkg.live-tile+json;version=1"
              Flags = uint32 (MdkgLayerFlags.Sparse ||| MdkgLayerFlags.Extension)
              Description = "Adaptive LiveTile widget definitions, instance state, refresh cadence, APIM bindings, and semantic outputs." }

        let WidgetStudio =
            { Id = LayerId.WidgetStudio
              Name = "widget-studio"
              SchemaUri = "urn:yumlog:mdkg:layer:widget-studio:v1"
              Codec = "application/vnd.yumlog.mdkg.widget-studio+json;version=1"
              Flags = uint32 (MdkgLayerFlags.Sparse ||| MdkgLayerFlags.Extension)
              Description = "Widget Studio project metadata for authoring, testing, packaging, and publishing adaptive LiveTile widgets." }

        let ExtensionCatalog =
            { Id = LayerId.ExtensionCatalog
              Name = "extension-catalog"
              SchemaUri = "urn:yumlog:mdkg:layer:extension-catalog:v1"
              Codec = "application/vnd.yumlog.mdkg.extension-catalog+json;version=1"
              Flags = uint32 (MdkgLayerFlags.Sparse ||| MdkgLayerFlags.Extension)
              Description = "Extensibility catalog: models, agents, tools, skills, workflows, connectors, plugins, extensions, MCP/API surfaces, and continuous AI loops." }

        let BuiltIn =
            [| Timing
               OcrText
               UiAffordance
               SemanticGraph
               InputEvent
               ArtFrame
               AcpContext
               ApimTrace
               LiveTile
               WidgetStudio
               ExtensionCatalog |]

        let tryFindById id =
            BuiltIn |> Array.tryFind (fun layer -> layer.Id = id)

        let tryFindByName name =
            BuiltIn
            |> Array.tryFind (fun layer -> String.Equals(layer.Name, name, StringComparison.OrdinalIgnoreCase))

    type private BeWriter(stream: Stream) =

        member _.WriteUInt8(value: byte) =
            stream.WriteByte(value)

        member _.WriteBytes(bytes: byte array) =
            stream.Write(bytes, 0, bytes.Length)

        member _.WriteZero(count: int) =
            if count > 0 then
                let bytes = Array.zeroCreate<byte> count
                stream.Write(bytes, 0, bytes.Length)

        member _.WriteUInt16(value: uint16) =
            let bytes = Array.zeroCreate<byte> 2
            BinaryPrimitives.WriteUInt16BigEndian(Span<byte>(bytes), value)
            stream.Write(bytes, 0, bytes.Length)

        member _.WriteUInt32(value: uint32) =
            let bytes = Array.zeroCreate<byte> 4
            BinaryPrimitives.WriteUInt32BigEndian(Span<byte>(bytes), value)
            stream.Write(bytes, 0, bytes.Length)

        member _.WriteUInt64(value: uint64) =
            let bytes = Array.zeroCreate<byte> 8
            BinaryPrimitives.WriteUInt64BigEndian(Span<byte>(bytes), value)
            stream.Write(bytes, 0, bytes.Length)

        member _.WriteInt64(value: int64) =
            let bytes = Array.zeroCreate<byte> 8
            BinaryPrimitives.WriteInt64BigEndian(Span<byte>(bytes), value)
            stream.Write(bytes, 0, bytes.Length)

        member this.WriteString16(value: string) =
            let actual =
                if Object.ReferenceEquals(value, null) then
                    ""
                else
                    value

            let bytes = Encoding.UTF8.GetBytes(actual)

            if bytes.Length > int UInt16.MaxValue then
                invalidArg (nameof value) "String is too long for a uint16 length field."

            this.WriteUInt16(uint16 bytes.Length)
            this.WriteBytes(bytes)

    type private BeReader(stream: Stream) =

        member _.ReadBytesExact(count: int) =
            let bytes = Array.zeroCreate<byte> count
            let mutable offset = 0

            while offset < count do
                let read = stream.Read(bytes, offset, count - offset)

                if read = 0 then
                    raise (EndOfStreamException())

                offset <- offset + read

            bytes

        member this.ReadUInt16() =
            let bytes = this.ReadBytesExact(2)
            BinaryPrimitives.ReadUInt16BigEndian(ReadOnlySpan<byte>(bytes))

        member this.ReadUInt32() =
            let bytes = this.ReadBytesExact(4)
            BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan<byte>(bytes))

        member this.ReadUInt64() =
            let bytes = this.ReadBytesExact(8)
            BinaryPrimitives.ReadUInt64BigEndian(ReadOnlySpan<byte>(bytes))

        member this.ReadInt64() =
            let bytes = this.ReadBytesExact(8)
            BinaryPrimitives.ReadInt64BigEndian(ReadOnlySpan<byte>(bytes))

        member this.ReadString16() =
            let length = this.ReadUInt16() |> int
            let bytes = this.ReadBytesExact(length)
            Encoding.UTF8.GetString(bytes)

    let private writeHeader (stream: Stream) (header: MdkgHeader) =
        stream.Position <- 0L
        let writer = BeWriter(stream)

        writer.WriteBytes(Magic)
        writer.WriteUInt16(header.MajorVersion)
        writer.WriteUInt16(header.MinorVersion)
        writer.WriteUInt32(header.HeaderLength)
        writer.WriteUInt64(header.MovieTimescale)
        writer.WriteUInt64(header.LayerTableOffset)
        writer.WriteUInt64(header.LayerTableLength)
        writer.WriteUInt32(header.LayerCount)
        writer.WriteUInt64(header.SampleIndexOffset)
        writer.WriteUInt64(header.SampleIndexLength)
        writer.WriteUInt64(header.DataOffset)
        writer.WriteUInt64(header.DataLength)
        writer.WriteUInt32(header.FileFlags)
        writer.WriteZero(FixedHeaderSize - 80)

    let private readHeader (stream: Stream) =
        stream.Position <- 0L
        let reader = BeReader(stream)
        let magic = reader.ReadBytesExact(8)

        if not (Array.forall2 (=) magic Magic) then
            invalidData "Invalid .mdkg magic bytes."

        let major = reader.ReadUInt16()
        let minor = reader.ReadUInt16()

        if major <> MajorVersion then
            invalidData $"Unsupported .mdkg major version {major}."

        let header =
            { MajorVersion = major
              MinorVersion = minor
              HeaderLength = reader.ReadUInt32()
              MovieTimescale = reader.ReadUInt64()
              LayerTableOffset = reader.ReadUInt64()
              LayerTableLength = reader.ReadUInt64()
              LayerCount = reader.ReadUInt32()
              SampleIndexOffset = reader.ReadUInt64()
              SampleIndexLength = reader.ReadUInt64()
              DataOffset = reader.ReadUInt64()
              DataLength = reader.ReadUInt64()
              FileFlags = reader.ReadUInt32() }

        if header.HeaderLength <> uint32 FixedHeaderSize then
            invalidData $"Unsupported .mdkg header length {header.HeaderLength}."

        if header.MovieTimescale <> uint64 MovieTimescale then
            invalidData $"Unsupported .mdkg timescale {header.MovieTimescale}."

        header

    let private writeLayerTable (layers: MdkgLayerDescriptor array) =
        use ms = new MemoryStream()
        let writer = BeWriter(ms)

        writer.WriteUInt32(uint32 layers.Length)

        for layer in layers do
            writer.WriteUInt32(layer.Id)
            writer.WriteUInt32(layer.Flags)
            writer.WriteString16(layer.Name)
            writer.WriteString16(layer.SchemaUri)
            writer.WriteString16(layer.Codec)
            writer.WriteString16(layer.Description)

        ms.ToArray()

    let private readLayerTable (stream: Stream) (header: MdkgHeader) =
        stream.Position <- int64 header.LayerTableOffset
        let reader = BeReader(stream)
        let count = reader.ReadUInt32() |> int

        if uint32 count <> header.LayerCount then
            invalidData $".mdkg layer table count {count} does not match header count {header.LayerCount}."

        [| for _ in 1 .. count do
               let layer: MdkgLayerDescriptor =
                   { Id = reader.ReadUInt32()
                     Flags = reader.ReadUInt32()
                     Name = reader.ReadString16()
                     SchemaUri = reader.ReadString16()
                     Codec = reader.ReadString16()
                     Description = reader.ReadString16() }

               yield
                   layer |]

    let private writeSampleIndex (entriesByLayer: Dictionary<uint32, ResizeArray<MdkgSampleEntry>>) =
        use ms = new MemoryStream()
        let writer = BeWriter(ms)

        let nonEmpty =
            entriesByLayer
            |> Seq.map (fun kvp -> kvp.Key, kvp.Value.ToArray())
            |> Seq.filter (fun (_, samples) -> samples.Length > 0)
            |> Seq.sortBy fst
            |> Seq.toArray

        writer.WriteUInt32(uint32 nonEmpty.Length)

        for layerId, samples in nonEmpty do
            writer.WriteUInt32(layerId)
            writer.WriteUInt32(uint32 samples.Length)

            for sample in samples do
                writer.WriteInt64(sample.PtsTicks)
                writer.WriteInt64(sample.DurationTicks)
                writer.WriteUInt64(sample.ByteOffset)
                writer.WriteUInt32(sample.ByteLength)
                writer.WriteUInt32(sample.Flags)

        ms.ToArray()

    let private readSampleIndex (stream: Stream) (header: MdkgHeader) =
        stream.Position <- int64 header.SampleIndexOffset
        let reader = BeReader(stream)
        let layerCount = reader.ReadUInt32() |> int
        let result = Dictionary<uint32, MdkgSampleEntry array>()

        for _ in 1 .. layerCount do
            let layerId = reader.ReadUInt32()
            let sampleCount = reader.ReadUInt32() |> int

            let samples =
                [| for _ in 1 .. sampleCount do
                       yield
                           { LayerId = layerId
                             PtsTicks = reader.ReadInt64()
                             DurationTicks = reader.ReadInt64()
                             ByteOffset = reader.ReadUInt64()
                             ByteLength = reader.ReadUInt32()
                             Flags = reader.ReadUInt32() } |]

            result[layerId] <- samples

        result

    let private ensureLayerExists (layers: ResizeArray<MdkgLayerDescriptor>) layerId =
        if not (layers |> Seq.exists (fun layer -> layer.Id = layerId)) then
            invalidArg (nameof layerId) $"Layer {layerId} is not registered."

    type MdkgWriter private (path: string, timescale: int64) =

        let filePath = Path.GetFullPath(path)

        do
            match Path.GetDirectoryName(filePath) with
            | null
            | "" -> ()
            | parent -> Directory.CreateDirectory(parent) |> ignore

        let stream =
            new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read)

        let layers = ResizeArray<MdkgLayerDescriptor>()
        let entriesByLayer = Dictionary<uint32, ResizeArray<MdkgSampleEntry>>()

        let mutable closed = false
        let mutable layerTableWritten = false
        let mutable layerTableOffset = 0UL
        let mutable layerTableLength = 0UL
        let mutable dataOffset = 0UL
        let mutable dataLength = 0UL

        do
            if timescale <> MovieTimescale then
                invalidArg (nameof timescale) ".mdkg v1 requires a 10,000,000 Hz movie timescale."

            writeHeader
                stream
                { MajorVersion = MajorVersion
                  MinorVersion = MinorVersion
                  HeaderLength = uint32 FixedHeaderSize
                  MovieTimescale = uint64 MovieTimescale
                  LayerTableOffset = 0UL
                  LayerTableLength = 0UL
                  LayerCount = 0u
                  SampleIndexOffset = 0UL
                  SampleIndexLength = 0UL
                  DataOffset = 0UL
                  DataLength = 0UL
                  FileFlags = 0u }

            stream.Position <- int64 FixedHeaderSize

        static member Open(path: string, ?timescale: int64) =
            new MdkgWriter(path, defaultArg timescale MovieTimescale)

        member _.Path = filePath

        member _.AddLayer(layer: MdkgLayerDescriptor) =
            if closed then
                invalidOp "Cannot add layers after close()."

            if layerTableWritten then
                invalidOp "Cannot add layers after samples have been written."

            if layers |> Seq.exists (fun existing -> existing.Id = layer.Id) then
                invalidArg (nameof layer) $"Layer id {layer.Id} is already registered."

            if String.IsNullOrWhiteSpace(layer.Name) then
                invalidArg (nameof layer) "Layer name is required."

            if String.IsNullOrWhiteSpace(layer.Codec) then
                invalidArg (nameof layer) "Layer codec is required."

            layers.Add(layer)
            entriesByLayer[layer.Id] <- ResizeArray<MdkgSampleEntry>()

        member this.AddDefaultLayers() =
            for layer in Registry.BuiltIn do
                this.AddLayer(layer)

        member private _.EnsureLayerTable() =
            if not layerTableWritten then
                if layers.Count = 0 then
                    invalidOp "At least one layer must be registered before writing samples."

                let table = writeLayerTable (layers.ToArray())
                layerTableOffset <- uint64 stream.Position
                stream.Write(table, 0, table.Length)
                layerTableLength <- uint64 table.Length
                dataOffset <- uint64 stream.Position
                layerTableWritten <- true

        member this.WriteSample(layerId: uint32, ptsTicks: int64, durationTicks: int64, flags: MdkgSampleFlags, payload: byte array) =
            if closed then
                invalidOp "Cannot write samples after close()."

            if Object.ReferenceEquals(payload, null) then
                invalidArg (nameof payload) "Payload must not be null."

            if ptsTicks < 0L then
                invalidArg (nameof ptsTicks) "Sample PTS must be non-negative."

            if durationTicks < 0L then
                invalidArg (nameof durationTicks) "Sample duration must be non-negative."

            ensureLayerExists layers layerId
            this.EnsureLayerTable()

            let offset = uint64 stream.Position
            stream.Write(payload, 0, payload.Length)

            entriesByLayer[layerId].Add(
                { LayerId = layerId
                  PtsTicks = ptsTicks
                  DurationTicks = durationTicks
                  ByteOffset = offset
                  ByteLength = uint32 payload.Length
                  Flags = uint32 flags })

        member this.WriteTiming(sample: TimingLayerSample) =
            let flags =
                if sample.IsDropped then
                    MdkgSampleFlags.KeyFrame ||| MdkgSampleFlags.Dropped
                elif sample.GapFromPreviousTicks > sample.DurationTicks then
                    MdkgSampleFlags.KeyFrame ||| MdkgSampleFlags.Gap
                else
                    MdkgSampleFlags.KeyFrame

            this.WriteSample(
                LayerId.Timing,
                sample.PtsTicks,
                sample.DurationTicks,
                flags,
                serializeJson sample)

        member this.WriteOcrText(sample: OcrTextLayerSample) =
            this.WriteSample(
                LayerId.OcrText,
                sample.PtsTicks,
                sample.DurationTicks,
                MdkgSampleFlags.Sparse,
                LayerPayloads.serializeOcrText sample)

        member this.WriteUiAffordance(sample: UiAffordanceLayerSample) =
            this.WriteSample(
                LayerId.UiAffordance,
                sample.PtsTicks,
                sample.DurationTicks,
                MdkgSampleFlags.Sparse,
                LayerPayloads.serializeUiAffordance sample)

        member this.WriteSemanticGraph(sample: SemanticGraphLayerSample) =
            let flags =
                if sample.IsKeyFrame then
                    MdkgSampleFlags.KeyFrame
                else
                    MdkgSampleFlags.Delta

            this.WriteSample(
                LayerId.SemanticGraph,
                sample.PtsTicks,
                sample.DurationTicks,
                flags,
                LayerPayloads.serializeSemanticGraph sample)

        member this.WriteInputEvent(sample: InputEventLayerSample) =
            this.WriteSample(
                LayerId.InputEvent,
                sample.PtsTicks,
                sample.DurationTicks,
                MdkgSampleFlags.Sparse,
                serializeJson sample)

        member this.WriteArtFrame(sample: ArtFrameLayerSample) =
            this.WriteSample(
                LayerId.ArtFrame,
                sample.PtsTicks,
                sample.DurationTicks,
                MdkgSampleFlags.Sparse,
                LayerPayloads.serializeArtFrame sample)

        member this.WriteAcpContext(sample: AcpContextLayerSample) =
            this.WriteSample(
                LayerId.AcpContext,
                sample.PtsTicks,
                sample.DurationTicks,
                MdkgSampleFlags.Sparse,
                LayerPayloads.serializeAcpContext sample)

        member this.WriteApimTrace(sample: ApimTraceLayerSample) =
            this.WriteSample(
                LayerId.ApimTrace,
                sample.PtsTicks,
                sample.DurationTicks,
                MdkgSampleFlags.Sparse,
                LayerPayloads.serializeApimTrace sample)

        member this.WriteLiveTile(sample: LiveTileLayerSample) =
            this.WriteSample(
                LayerId.LiveTile,
                sample.PtsTicks,
                sample.DurationTicks,
                MdkgSampleFlags.Sparse,
                LayerPayloads.serializeLiveTile sample)

        member this.WriteWidgetStudio(sample: WidgetStudioLayerSample) =
            this.WriteSample(
                LayerId.WidgetStudio,
                sample.PtsTicks,
                sample.DurationTicks,
                MdkgSampleFlags.Sparse,
                LayerPayloads.serializeWidgetStudio sample)

        member this.WriteExtensionCatalog(sample: ExtensionCatalogLayerSample) =
            this.WriteSample(
                LayerId.ExtensionCatalog,
                sample.PtsTicks,
                sample.DurationTicks,
                MdkgSampleFlags.KeyFrame ||| MdkgSampleFlags.Sparse,
                LayerPayloads.serializeExtensionCatalog sample)

        member _.Close() =
            if not closed then
                closed <- true

                if not layerTableWritten then
                    if layers.Count = 0 then
                        for layer in Registry.BuiltIn do
                            layers.Add(layer)
                            entriesByLayer[layer.Id] <- ResizeArray<MdkgSampleEntry>()

                    let table = writeLayerTable (layers.ToArray())
                    layerTableOffset <- uint64 stream.Position
                    stream.Write(table, 0, table.Length)
                    layerTableLength <- uint64 table.Length
                    dataOffset <- uint64 stream.Position
                    layerTableWritten <- true

                dataLength <- uint64 stream.Position - dataOffset

                let indexOffset = uint64 stream.Position
                let index = writeSampleIndex entriesByLayer
                stream.Write(index, 0, index.Length)
                let indexLength = uint64 index.Length

                writeHeader
                    stream
                    { MajorVersion = MajorVersion
                      MinorVersion = MinorVersion
                      HeaderLength = uint32 FixedHeaderSize
                      MovieTimescale = uint64 MovieTimescale
                      LayerTableOffset = layerTableOffset
                      LayerTableLength = layerTableLength
                      LayerCount = uint32 layers.Count
                      SampleIndexOffset = indexOffset
                      SampleIndexLength = indexLength
                      DataOffset = dataOffset
                      DataLength = dataLength
                      FileFlags = 0u }

                stream.Flush()
                stream.Dispose()

        interface IDisposable with
            member this.Dispose() =
                this.Close()

    type MdkgReader private (path: string) =

        let filePath = Path.GetFullPath(path)
        let stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read)
        let header = readHeader stream
        let layers = readLayerTable stream header
        let sampleIndex = readSampleIndex stream header

        static member Open(path: string) =
            new MdkgReader(path)

        member _.Path = filePath

        member _.Header = header

        member _.Layers =
            layers |> Array.copy

        member _.ReadLayer(layerId: uint32) =
            match sampleIndex.TryGetValue(layerId) with
            | true, samples -> samples |> Array.copy
            | false, _ -> Array.empty

        member _.ReadSample(entry: MdkgSampleEntry) =
            stream.Position <- int64 entry.ByteOffset
            let payload = Array.zeroCreate<byte> (int entry.ByteLength)
            let mutable offset = 0

            while offset < payload.Length do
                let read = stream.Read(payload, offset, payload.Length - offset)

                if read = 0 then
                    raise (EndOfStreamException())

                offset <- offset + read

            { Entry = entry; Payload = payload }

        member this.Seek(layerId: uint32, ptsTicks: int64) =
            let samples = this.ReadLayer(layerId)

            samples
            |> Array.tryFind (fun sample ->
                ptsTicks >= sample.PtsTicks
                && ptsTicks < sample.PtsTicks + max 1L sample.DurationTicks)
            |> Option.orElseWith (fun () ->
                samples
                |> Array.filter (fun sample -> sample.PtsTicks <= ptsTicks)
                |> Array.sortByDescending _.PtsTicks
                |> Array.tryHead)
            |> Option.map this.ReadSample

        member this.SeekExact(layerId: uint32, ptsTicks: int64) =
            this.ReadLayer(layerId)
            |> Array.tryFind (fun sample -> sample.PtsTicks = ptsTicks)
            |> Option.map this.ReadSample

        member this.ReadTiming(entry: MdkgSampleEntry) =
            this.ReadSample(entry).Payload |> deserializeJson<TimingLayerSample>

        member this.ReadOcrText(entry: MdkgSampleEntry) =
            this.ReadSample(entry).Payload |> LayerPayloads.deserializeOcrText

        member this.ReadUiAffordance(entry: MdkgSampleEntry) =
            this.ReadSample(entry).Payload |> LayerPayloads.deserializeUiAffordance

        member this.ReadSemanticGraph(entry: MdkgSampleEntry) =
            this.ReadSample(entry).Payload |> LayerPayloads.deserializeSemanticGraph

        member this.ReadInputEvent(entry: MdkgSampleEntry) =
            this.ReadSample(entry).Payload |> deserializeJson<InputEventLayerSample>

        member this.ReadArtFrame(entry: MdkgSampleEntry) =
            this.ReadSample(entry).Payload |> LayerPayloads.deserializeArtFrame

        member this.ReadAcpContext(entry: MdkgSampleEntry) =
            this.ReadSample(entry).Payload |> LayerPayloads.deserializeAcpContext

        member this.ReadApimTrace(entry: MdkgSampleEntry) =
            this.ReadSample(entry).Payload |> LayerPayloads.deserializeApimTrace

        member this.ReadLiveTile(entry: MdkgSampleEntry) =
            this.ReadSample(entry).Payload |> LayerPayloads.deserializeLiveTile

        member this.ReadWidgetStudio(entry: MdkgSampleEntry) =
            this.ReadSample(entry).Payload |> LayerPayloads.deserializeWidgetStudio

        member this.ReadExtensionCatalog(entry: MdkgSampleEntry) =
            this.ReadSample(entry).Payload |> LayerPayloads.deserializeExtensionCatalog

        interface IDisposable with
            member _.Dispose() =
                stream.Dispose()

    module Carrier =

        [<Literal>]
        let private CarrierMagic = 0x4D445331u

        let encode (sample: MdkgCarrierSample) =
            use ms = new MemoryStream()
            let writer = BeWriter(ms)

            let codec =
                if Object.ReferenceEquals(sample.Codec, null) then
                    ""
                else
                    sample.Codec

            let payload =
                if Object.ReferenceEquals(sample.Payload, null) then
                    Array.empty
                else
                    sample.Payload

            let codecBytes = Encoding.UTF8.GetBytes(codec)

            if codecBytes.Length > int UInt16.MaxValue then
                invalidArg (nameof sample) "Carrier codec string is too long."

            writer.WriteUInt32(CarrierMagic)                 // "MDS1"
            writer.WriteUInt32(sample.LayerId)
            writer.WriteInt64(sample.PtsTicks)
            writer.WriteInt64(sample.DurationTicks)
            writer.WriteUInt32(sample.Flags)
            writer.WriteUInt16(uint16 codecBytes.Length)
            writer.WriteBytes(codecBytes)
            writer.WriteUInt32(uint32 payload.Length)
            writer.WriteBytes(payload)

            ms.ToArray()

        let decode (payload: byte array) =
            use ms = new MemoryStream(payload)
            let reader = BeReader(ms)
            let magic = reader.ReadUInt32()

            if magic <> CarrierMagic then
                invalidData "Invalid MDKG carrier sample magic."

            let layerId = reader.ReadUInt32()
            let ptsTicks = reader.ReadInt64()
            let durationTicks = reader.ReadInt64()
            let flags = reader.ReadUInt32()
            let codecLen = reader.ReadUInt16() |> int
            let codec = reader.ReadBytesExact(codecLen) |> Encoding.UTF8.GetString
            let payloadLen = reader.ReadUInt32() |> int
            let innerPayload = reader.ReadBytesExact(payloadLen)

            { LayerId = layerId
              PtsTicks = ptsTicks
              DurationTicks = durationTicks
              Flags = flags
              Codec = codec
              Payload = innerPayload }

        let fromIndexedSample (layer: MdkgLayerDescriptor) (entry: MdkgSampleEntry) (payload: byte array) =
            { LayerId = entry.LayerId
              PtsTicks = entry.PtsTicks
              DurationTicks = entry.DurationTicks
              Flags = entry.Flags
              Codec = layer.Codec
              Payload = payload }

    let ``open`` (path: string) =
        MdkgWriter.Open(path)

    let addLayer (writer: MdkgWriter) (layer: MdkgLayerDescriptor) =
        writer.AddLayer(layer)

    let addDefaultLayers (writer: MdkgWriter) =
        writer.AddDefaultLayers()

    let writeSample
        (writer: MdkgWriter)
        (layerId: uint32)
        (ptsTicks: int64)
        (durationTicks: int64)
        (flags: MdkgSampleFlags)
        (payload: byte array)
        =
        writer.WriteSample(layerId, ptsTicks, durationTicks, flags, payload)

    let writeOcrText (writer: MdkgWriter) (sample: OcrTextLayerSample) =
        writer.WriteOcrText(sample)

    let writeUiAffordance (writer: MdkgWriter) (sample: UiAffordanceLayerSample) =
        writer.WriteUiAffordance(sample)

    let writeSemanticGraph (writer: MdkgWriter) (sample: SemanticGraphLayerSample) =
        writer.WriteSemanticGraph(sample)

    let writeArtFrame (writer: MdkgWriter) (sample: ArtFrameLayerSample) =
        writer.WriteArtFrame(sample)

    let writeAcpContext (writer: MdkgWriter) (sample: AcpContextLayerSample) =
        writer.WriteAcpContext(sample)

    let writeApimTrace (writer: MdkgWriter) (sample: ApimTraceLayerSample) =
        writer.WriteApimTrace(sample)

    let writeLiveTile (writer: MdkgWriter) (sample: LiveTileLayerSample) =
        writer.WriteLiveTile(sample)

    let writeWidgetStudio (writer: MdkgWriter) (sample: WidgetStudioLayerSample) =
        writer.WriteWidgetStudio(sample)

    let writeExtensionCatalog (writer: MdkgWriter) (sample: ExtensionCatalogLayerSample) =
        writer.WriteExtensionCatalog(sample)

    let close (writer: MdkgWriter) =
        writer.Close()

    let openReader (path: string) =
        MdkgReader.Open(path)
