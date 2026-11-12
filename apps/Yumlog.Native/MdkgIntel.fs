namespace Yumlog.Native

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open Yumlog.Native.Mdkg

/// Addressing, streaming, and runbook emission over .mdkg recordings.
///
/// This is the layer that turns a recording from an opaque blob into an
/// agent-consumable proof substrate:
///   * MdkgPath    - a stable, citable address for any value in any layer at any time.
///   * MdkgStream  - ordered, lazily-buffered access across layers (streamable reasoning).
///   * Runbook     - deterministic steps whose every claim carries a re-resolvable proof.
module MdkgIntel =

    // -----------------------------------------------------------------------
    // Schema path addressing
    // -----------------------------------------------------------------------

    [<Literal>]
    let Scheme = "mdkg://"

    /// A canonical address of the form:
    ///     mdkg://<layer-name>@<ptsTicks>[#<json-pointer>]
    /// Examples:
    ///     mdkg://timing@0
    ///     mdkg://ocr-text@333333#/ocr/text
    ///     mdkg://semantic-graph@666666#/delta/addNodes/0/id
    type MdkgPath =
        { LayerName: string
          PtsTicks: int64
          Pointer: string option }

        override this.ToString() =
            match this.Pointer with
            | Some pointer -> $"{Scheme}{this.LayerName}@{this.PtsTicks}#{pointer}"
            | None -> $"{Scheme}{this.LayerName}@{this.PtsTicks}"

    let makePath layerName ptsTicks pointer =
        { LayerName = layerName
          PtsTicks = ptsTicks
          Pointer = pointer }

    let formatPath (path: MdkgPath) = path.ToString()

    let tryParsePath (value: string) =
        if String.IsNullOrWhiteSpace(value) || not (value.StartsWith(Scheme, StringComparison.Ordinal)) then
            None
        else
            let body = value.Substring(Scheme.Length)
            let pointerSplit = body.IndexOf('#')

            let core, pointer =
                if pointerSplit >= 0 then
                    body.Substring(0, pointerSplit), Some(body.Substring(pointerSplit + 1))
                else
                    body, None

            let atSplit = core.LastIndexOf('@')

            if atSplit <= 0 then
                None
            else
                let layerName = core.Substring(0, atSplit)

                match Int64.TryParse(core.Substring(atSplit + 1)) with
                | true, pts when pts >= 0L -> Some(makePath layerName pts pointer)
                | _ -> None

    let parsePath value =
        match tryParsePath value with
        | Some path -> path
        | None -> invalidArg (nameof value) $"Malformed mdkg path '{value}'. Expected {Scheme}<layer>@<ptsTicks>[#<json-pointer>]."

    /// Resolves an RFC 6901-style JSON pointer against a payload document.
    let private resolvePointer (payload: byte array) (pointer: string) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(payload))
        let mutable current = document.RootElement
        let mutable ok = true

        let segments =
            pointer.Split('/')
            |> Array.skipWhile String.IsNullOrEmpty

        for segment in segments do
            if ok then
                let decoded = segment.Replace("~1", "/").Replace("~0", "~")

                match current.ValueKind with
                | JsonValueKind.Object ->
                    match current.TryGetProperty(decoded) with
                    | true, next -> current <- next
                    | _ -> ok <- false
                | JsonValueKind.Array ->
                    match Int32.TryParse(decoded) with
                    | true, index when index >= 0 && index < current.GetArrayLength() ->
                        current <- current.EnumerateArray() |> Seq.item index
                    | _ -> ok <- false
                | _ -> ok <- false

        if ok then
            Some(
                match current.ValueKind with
                | JsonValueKind.String ->
                    match current.GetString() with
                    | null -> ""
                    | text -> text
                | _ -> current.GetRawText())
        else
            None

    /// Resolves a path to its concrete value. Returns None when the layer,
    /// the sample, or the pointer does not exist in this recording.
    let resolve (reader: MdkgReader) (path: MdkgPath) =
        match Registry.tryFindByName path.LayerName with
        | None -> None
        | Some layer ->
            match reader.SeekExact(layer.Id, path.PtsTicks) with
            | None -> None
            | Some sample ->
                match path.Pointer with
                | None -> Some(Encoding.UTF8.GetString(sample.Payload))
                | Some pointer -> resolvePointer sample.Payload pointer

    // -----------------------------------------------------------------------
    // Streamable access
    // -----------------------------------------------------------------------

    type TimelineItem =
        { LayerId: uint32
          LayerName: string
          Entry: MdkgSampleEntry
          Path: MdkgPath }

    /// Ordered, lazy merge of the requested layers' sample indexes. Payloads are
    /// NOT read here: callers pull only the samples they actually reason over,
    /// which is what keeps long recordings streamable instead of resident.
    let timeline (reader: MdkgReader) (layerIds: uint32 seq) =
        let names =
            reader.Layers
            |> Array.map (fun layer -> layer.Id, layer.Name)
            |> dict

        layerIds
        |> Seq.distinct
        |> Seq.collect (fun layerId ->
            let name =
                match names.TryGetValue(layerId) with
                | true, value -> value
                | _ -> $"layer-{layerId}"

            reader.ReadLayer(layerId)
            |> Seq.map (fun entry ->
                { LayerId = layerId
                  LayerName = name
                  Entry = entry
                  Path = makePath name entry.PtsTicks None }))
        |> Seq.sortBy (fun item -> item.Entry.PtsTicks, item.LayerId)

    /// All layers present in the recording, in PTS order.
    let fullTimeline (reader: MdkgReader) =
        timeline reader (reader.Layers |> Array.map _.Id)

    /// A bounded reasoning window: [startTicks, endTicks). Use this to feed a
    /// model a fixed slice of context instead of an entire session.
    let window (reader: MdkgReader) (startTicks: int64) (endTicks: int64) =
        fullTimeline reader
        |> Seq.filter (fun item -> item.Entry.PtsTicks >= startTicks && item.Entry.PtsTicks < endTicks)

    // -----------------------------------------------------------------------
    // Runbook emission
    // -----------------------------------------------------------------------

    [<CLIMutable>]
    type RunbookProof =
        { Claim: string
          Path: string
          Value: string }

    [<CLIMutable>]
    type RunbookStep =
        { Index: int
          PtsTicks: int64
          OffsetSeconds: float
          Title: string
          ObservedText: string
          Affordances: string array
          GraphNodeCount: int
          GraphEdgeCount: int
          Proofs: RunbookProof array }

    [<CLIMutable>]
    type Runbook =
        { Source: string
          GeneratedUtc: DateTimeOffset
          TimescaleTicksPerSecond: int64
          DurationSeconds: float
          StepCount: int
          Steps: RunbookStep array }

    [<CLIMutable>]
    type RunbookVerification =
        { Source: string
          Verified: bool
          TotalProofs: int
          ResolvedProofs: int
          MismatchedProofs: int
          UnresolvedPaths: string array }

    let private ticksToSeconds (ticks: int64) =
        float ticks / float MovieTimescale

    let private truncate (maxLength: int) (value: string) =
        if isNull (box value) then ""
        elif value.Length <= maxLength then value
        else value.Substring(0, maxLength) + "..."

    let private firstLine (value: string) =
        if isNull (box value) then
            ""
        else
            let trimmed = value.Replace("\r", "").Split('\n') |> Array.tryFind (fun line -> line.Trim() <> "")
            match trimmed with
            | Some line -> line.Trim()
            | None -> ""

    /// Derives an ordered set of steps. Step anchors are chosen deterministically:
    /// UI affordance samples when present, else semantic-graph samples, else timing.
    /// The same recording always yields the same runbook - that is the repeatability
    /// contract callers depend on.
    let buildRunbook (reader: MdkgReader) =
        let uiEntries = reader.ReadLayer(LayerId.UiAffordance)
        let graphEntries = reader.ReadLayer(LayerId.SemanticGraph)
        let timingEntries = reader.ReadLayer(LayerId.Timing)

        let anchors, anchorLayer =
            if uiEntries.Length > 0 then uiEntries, Registry.UiAffordance.Name
            elif graphEntries.Length > 0 then graphEntries, Registry.SemanticGraph.Name
            else timingEntries, Registry.Timing.Name

        let anchors = anchors |> Array.sortBy _.PtsTicks

        let duration =
            let candidates =
                [ timingEntries; uiEntries; graphEntries ]
                |> List.collect (fun entries ->
                    entries
                    |> Array.map (fun entry -> entry.PtsTicks + max 0L entry.DurationTicks)
                    |> Array.toList)

            match candidates with
            | [] -> 0L
            | values -> List.max values

        let steps =
            anchors
            |> Array.mapi (fun index anchor ->
                let pts = anchor.PtsTicks
                let proofs = ResizeArray<RunbookProof>()

                let addProof claim layerName pointer =
                    let path = makePath layerName pts (Some pointer)

                    match resolve reader path with
                    | Some value ->
                        proofs.Add(
                            { Claim = claim
                              Path = formatPath path
                              Value = truncate 240 value })
                    | None -> ()

                // Timing is the spine: every step is anchored to a real frame.
                let timingSample =
                    reader.SeekExact(LayerId.Timing, pts)
                    |> Option.map (fun sample -> deserializeJson<TimingLayerSample> sample.Payload)

                match timingSample with
                | Some _ ->
                    addProof "frame index at this step" Registry.Timing.Name "/frameIndex"
                    addProof "presentation timestamp" Registry.Timing.Name "/ptsTicks"
                    // Without these three the timing layer records the capture rate,
                    // sample coverage and drop count but nothing can cite them, so a
                    // question like "what frame rate was this captured at" has no
                    // grounding and a reasoning model answers it by invention.
                    // Observed: a 15 fps recording confidently reported as 30 fps.
                    addProof "capture frame rate" Registry.Timing.Name "/fps"
                    addProof "sample coverage" Registry.Timing.Name "/durationTicks"
                    addProof "cumulative dropped frames" Registry.Timing.Name "/dropCount"
                | None -> ()

                // OCR gives the human-readable observation.
                let observed =
                    reader.SeekExact(LayerId.OcrText, pts)
                    |> Option.map (fun sample -> deserializeJson<OcrTextLayerSample> sample.Payload)
                    |> Option.map (fun sample ->
                        if isNull (box sample.Ocr) then "" else sample.Ocr.Text)
                    |> Option.defaultValue ""

                if observed <> "" then
                    addProof "observed screen text" Registry.OcrText.Name "/ocr/text"

                // UI affordances describe what an operator could do here.
                let affordances =
                    reader.SeekExact(LayerId.UiAffordance, pts)
                    |> Option.map (fun sample -> deserializeJson<UiAffordanceLayerSample> sample.Payload)
                    |> Option.map (fun sample ->
                        if isNull (box sample.Hints) then Array.empty else sample.Hints)
                    |> Option.defaultValue Array.empty

                affordances
                |> Array.iteri (fun hintIndex _ -> addProof $"affordance {hintIndex}" Registry.UiAffordance.Name $"/hints/{hintIndex}/label")

                // Graph state gives the structural context at this instant.
                let graphState = SemanticGraph.reconstructAt reader pts

                if graphEntries |> Array.exists (fun entry -> entry.PtsTicks = pts) then
                    addProof "graph sample identity" Registry.SemanticGraph.Name "/graphId"

                let title =
                    if affordances.Length > 0 && not (String.IsNullOrWhiteSpace(affordances[0].Label)) then
                        affordances[0].Label.Trim()
                    elif observed <> "" then
                        truncate 80 (firstLine observed)
                    else
                        $"Step at {ticksToSeconds pts:F3}s"

                { Index = index + 1
                  PtsTicks = pts
                  OffsetSeconds = ticksToSeconds pts
                  Title = title
                  ObservedText = truncate 400 observed
                  Affordances =
                    affordances
                    |> Array.map (fun hint -> $"{hint.Kind}: {hint.Label}")
                  GraphNodeCount =
                    graphState |> Option.map (fun state -> state.Nodes.Count) |> Option.defaultValue 0
                  GraphEdgeCount =
                    graphState |> Option.map (fun state -> state.Edges.Count) |> Option.defaultValue 0
                  Proofs = proofs.ToArray() })

        ignore anchorLayer

        { Source = reader.Path
          GeneratedUtc = DateTimeOffset.UtcNow
          TimescaleTicksPerSecond = MovieTimescale
          DurationSeconds = ticksToSeconds duration
          StepCount = steps.Length
          Steps = steps }

    /// Re-resolves every proof in a runbook against the recording. This is the
    /// repeatability check: a runbook is only trustworthy if each cited path
    /// still yields the value it was generated from.
    let verifyRunbook (reader: MdkgReader) (runbook: Runbook) =
        let unresolved = ResizeArray<string>()
        let mutable total = 0
        let mutable resolved = 0
        let mutable mismatched = 0

        for step in runbook.Steps do
            for proof in step.Proofs do
                total <- total + 1

                match tryParsePath proof.Path |> Option.bind (resolve reader) with
                | None -> unresolved.Add(proof.Path)
                | Some value ->
                    resolved <- resolved + 1
                    if truncate 240 value <> proof.Value then
                        mismatched <- mismatched + 1
                        unresolved.Add($"{proof.Path} (value drift)")

        { Source = reader.Path
          Verified = total = resolved && mismatched = 0
          TotalProofs = total
          ResolvedProofs = resolved
          MismatchedProofs = mismatched
          UnresolvedPaths = unresolved.ToArray() }

    /// Renders the runbook as Markdown for human review. Every claim keeps its
    /// machine-resolvable mdkg:// citation so the document stays auditable.
    let toMarkdown (runbook: Runbook) =
        let sb = StringBuilder()
        sb.AppendLine("# Yumlog Runbook") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine($"- Source: `{runbook.Source}`") |> ignore
        sb.AppendLine($"- Generated: {runbook.GeneratedUtc:u}") |> ignore
        sb.AppendLine($"- Duration: {runbook.DurationSeconds:F3}s") |> ignore
        sb.AppendLine($"- Steps: {runbook.StepCount}") |> ignore
        sb.AppendLine() |> ignore

        for step in runbook.Steps do
            sb.AppendLine($"## Step {step.Index} - {step.Title}") |> ignore
            sb.AppendLine() |> ignore
            sb.AppendLine($"At {step.OffsetSeconds:F3}s (pts {step.PtsTicks})") |> ignore
            sb.AppendLine() |> ignore

            if step.ObservedText <> "" then
                sb.AppendLine("Observed:") |> ignore
                sb.AppendLine() |> ignore
                sb.AppendLine("```") |> ignore
                sb.AppendLine(step.ObservedText) |> ignore
                sb.AppendLine("```") |> ignore
                sb.AppendLine() |> ignore

            if step.Affordances.Length > 0 then
                sb.AppendLine("Available actions:") |> ignore
                for affordance in step.Affordances do
                    sb.AppendLine($"- {affordance}") |> ignore
                sb.AppendLine() |> ignore

            sb.AppendLine($"Graph context: {step.GraphNodeCount} nodes, {step.GraphEdgeCount} edges") |> ignore
            sb.AppendLine() |> ignore

            if step.Proofs.Length > 0 then
                sb.AppendLine("Proofs:") |> ignore
                for proof in step.Proofs do
                    sb.AppendLine($"- {proof.Claim}: `{proof.Path}` = `{proof.Value}`") |> ignore
                sb.AppendLine() |> ignore

        sb.ToString()
