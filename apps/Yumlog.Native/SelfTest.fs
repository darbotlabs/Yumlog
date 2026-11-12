namespace Yumlog.Native

open System
open System.IO
open Yumlog.Native.Mdkg

/// Runnable verification for the parts of the native pipeline that do not need
/// a GPU or a capture session. Invoked by `Yumlog.Native.Cli selftest`.
module NativeSelfTest =

    type SelfTestCase =
        { Name: string
          Passed: bool
          Detail: string }

    [<CLIMutable>]
    type SelfTestReport =
        { Passed: bool
          PassedCount: int
          FailedCount: int
          Cases: SelfTestCase array }

    let private check name passed detail =
        { Name = name; Passed = passed; Detail = detail }

    let private node id label pts =
        { Id = id
          Kind = "ui-element"
          Label = label
          Confidence = 0.9
          HasBounds = false
          Bounds =
            { TopLeft = { X = 0.0; Y = 0.0 }
              TopRight = { X = 0.0; Y = 0.0 }
              BottomRight = { X = 0.0; Y = 0.0 }
              BottomLeft = { X = 0.0; Y = 0.0 } }
          FirstPtsTicks = pts
          LastPtsTicks = pts
          Attributes = Array.empty }

    let private edge id source target pts =
        { Id = id
          Kind = "contains"
          SourceNodeId = source
          TargetNodeId = target
          Confidence = 0.8
          FirstPtsTicks = pts
          LastPtsTicks = pts
          Attributes = Array.empty }

    let private emptyDelta =
        { AddNodes = Array.empty
          UpdateNodes = Array.empty
          RemoveNodeIds = Array.empty
          AddEdges = Array.empty
          UpdateEdges = Array.empty
          RemoveEdgeIds = Array.empty }

    /// Writes a small .mdkg with timing plus a semantic-graph keyframe and two
    /// deltas, then reads it back and reconstructs the graph at three times.
    let private mdkgRoundTrip (sampleOut: string) =
        let path = Path.Combine(Path.GetTempPath(), $"yumlog-selftest-{Guid.NewGuid():N}.mdkg")
        let frameDuration = Mdkg.frameDurationTicksFromFps 30

        try
            let writer = Mdkg.``open`` path
            Mdkg.addDefaultLayers writer

            // Timing layer: 3 dense samples.
            for i in 0 .. 2 do
                let pts = int64 i * frameDuration

                let timing =
                    { Qpc = 1000L + int64 i
                      FrameIndex = i
                      Fps = 30
                      PtsTicks = pts
                      DurationTicks = frameDuration
                      GapFromPreviousTicks = 0L
                      IsDropped = false
                      DropCount = 0
                      Notes = "" }

                Mdkg.writeSample writer Mdkg.LayerId.Timing pts frameDuration Mdkg.MdkgSampleFlags.None (Mdkg.serializeJson timing)

            // Semantic graph: keyframe at t0 with 2 nodes + 1 edge.
            let keyFrame =
                { Qpc = 1000L
                  FrameIndex = 0
                  PtsTicks = 0L
                  DurationTicks = frameDuration
                  GraphId = "selftest-graph"
                  IsKeyFrame = true
                  KeyFrameNodes = [| node "n1" "Start" 0L; node "n2" "Menu" 0L |]
                  KeyFrameEdges = [| edge "e1" "n1" "n2" 0L |]
                  Delta = emptyDelta }

            Mdkg.writeSample
                writer
                Mdkg.LayerId.SemanticGraph
                0L
                frameDuration
                Mdkg.MdkgSampleFlags.KeyFrame
                (Mdkg.serializeJson keyFrame)

            // Delta at t1: add n3 + edge e2.
            let delta1 =
                { Qpc = 1001L
                  FrameIndex = 1
                  PtsTicks = frameDuration
                  DurationTicks = frameDuration
                  GraphId = "selftest-graph"
                  IsKeyFrame = false
                  KeyFrameNodes = Array.empty
                  KeyFrameEdges = Array.empty
                  Delta =
                    { emptyDelta with
                        AddNodes = [| node "n3" "Dialog" frameDuration |]
                        AddEdges = [| edge "e2" "n2" "n3" frameDuration |] } }

            Mdkg.writeSample
                writer
                Mdkg.LayerId.SemanticGraph
                frameDuration
                frameDuration
                Mdkg.MdkgSampleFlags.Delta
                (Mdkg.serializeJson delta1)

            // Delta at t2: remove n2, which must also prune edges e1 and e2.
            let delta2 =
                { delta1 with
                    FrameIndex = 2
                    PtsTicks = frameDuration * 2L
                    Delta = { emptyDelta with RemoveNodeIds = [| "n2" |] } }

            Mdkg.writeSample
                writer
                Mdkg.LayerId.SemanticGraph
                (frameDuration * 2L)
                frameDuration
                Mdkg.MdkgSampleFlags.Delta
                (Mdkg.serializeJson delta2)

            // OCR + UI affordance layers so the runbook has real observable content.
            let ocrLine text =
                { Text = text
                  Words =
                    [| { Text = text
                         Confidence = 0.95
                         BoundingBox =
                           { TopLeft = { X = 0.0; Y = 0.0 }
                             TopRight = { X = 10.0; Y = 0.0 }
                             BottomRight = { X = 10.0; Y = 5.0 }
                             BottomLeft = { X = 0.0; Y = 5.0 } } } |] }

            for i in 0 .. 1 do
                let pts = int64 i * frameDuration
                let text = if i = 0 then "Welcome to Yumlog" else "Open the Settings dialog"

                let ocrSample =
                    { Qpc = 1000L + int64 i
                      FrameIndex = i
                      PtsTicks = pts
                      DurationTicks = frameDuration
                      Ocr =
                        { Provider = "selftest"
                          IsAvailable = true
                          Message = ""
                          Text = text
                          Lines = [| ocrLine text |] } }

                Mdkg.writeSample writer Mdkg.LayerId.OcrText pts frameDuration Mdkg.MdkgSampleFlags.None (Mdkg.serializeJson ocrSample)

                let uiSample =
                    { Qpc = 1000L + int64 i
                      FrameIndex = i
                      PtsTicks = pts
                      DurationTicks = frameDuration
                      Hints =
                        [| { Kind = "button"
                             Label = (if i = 0 then "Start" else "Settings")
                             Confidence = 0.9
                             Bounds =
                               { TopLeft = { X = 0.0; Y = 0.0 }
                                 TopRight = { X = 10.0; Y = 0.0 }
                                 BottomRight = { X = 10.0; Y = 5.0 }
                                 BottomLeft = { X = 0.0; Y = 5.0 } } } |] }

                Mdkg.writeSample writer Mdkg.LayerId.UiAffordance pts frameDuration Mdkg.MdkgSampleFlags.None (Mdkg.serializeJson uiSample)

            Mdkg.close writer

            use reader = Mdkg.openReader path

            // 1. Layer table round-trip. This is the regression guard for the
            // writer/reader field-order defect.
            let layers = reader.Layers

            let layerOk =
                layers.Length = Mdkg.Registry.BuiltIn.Length
                && (Mdkg.Registry.BuiltIn
                    |> Array.forall (fun expected ->
                        layers
                        |> Array.exists (fun actual ->
                            actual.Id = expected.Id
                            && actual.Name = expected.Name
                            && actual.SchemaUri = expected.SchemaUri
                            && actual.Codec = expected.Codec
                            && actual.Flags = expected.Flags
                            && actual.Description = expected.Description)))

            let layerDetail =
                if layerOk then
                    $"{layers.Length} layer descriptors round-tripped with exact field fidelity"
                else
                    let dump =
                        layers
                        |> Array.map (fun l -> $"id={l.Id} name='{l.Name}' flags={l.Flags}")
                        |> String.concat "; "

                    $"layer table corrupted: {dump}"

            // 2. Header is exactly 128 bytes and the data offsets are sane.
            let header = reader.Header
            let fileLength = FileInfo(path).Length

            let headerOk =
                header.HeaderLength = 128u
                && header.MovieTimescale = uint64 Mdkg.MovieTimescale
                && header.LayerCount = uint32 layers.Length
                && int64 (header.DataOffset + header.DataLength) <= fileLength
                && int64 (header.LayerTableOffset + header.LayerTableLength) <= fileLength
                && int64 (header.SampleIndexOffset + header.SampleIndexLength) <= fileLength

            // 3. Timing samples round-trip with correct PTS ordering.
            let timingEntries = reader.ReadLayer(Mdkg.LayerId.Timing)

            let timingOk =
                timingEntries.Length = 3
                && (timingEntries
                    |> Array.mapi (fun i entry ->
                        let sample = reader.ReadTiming(entry)
                        sample.FrameIndex = i && sample.PtsTicks = int64 i * frameDuration)
                    |> Array.forall id)

            // 3b. Declared sample coverage must never overlap the next sample.
            // Regression guard for the native record path, which used to stamp every
            // timing sample with DurationTicks derived from --fps while WGC delivered
            // frames at the display refresh rate. That made
            // mdkg://timing@pts#/durationTicks claim 50ms of coverage when the next
            // sample began 16.7ms later, so a runbook proof contradicted the very PTS
            // it cited. Durations must be measured, not assumed.
            let coverageNoOverlap (samples: (int64 * int64) array) =
                samples
                |> Array.pairwise
                |> Array.forall (fun ((pts, duration), (nextPts, _)) -> pts + duration <= nextPts)

            let timingCoverage =
                timingEntries
                |> Array.map (fun entry ->
                    let sample = reader.ReadTiming(entry)
                    sample.PtsTicks, sample.DurationTicks)

            // Negative control: an overlapping pair must fail, otherwise the check
            // above would pass vacuously and guard nothing.
            let overlapDetected =
                not (coverageNoOverlap [| (0L, frameDuration * 2L); (frameDuration, frameDuration) |])

            let coverageOk = coverageNoOverlap timingCoverage && overlapDetected

            let coverageDetail =
                if coverageOk then
                    $"{timingCoverage.Length} timing samples, no declared coverage overlaps the next sample; negative control rejected"
                else
                    let dump =
                        timingCoverage
                        |> Array.map (fun (pts, dur) -> $"pts={pts} dur={dur}")
                        |> String.concat "; "

                    $"overlapping coverage (negativeControl={overlapDetected}): {dump}"


            let at time = Mdkg.SemanticGraph.reconstructAt reader time

            let g0 = at 0L
            let g1 = at frameDuration
            let g2 = at (frameDuration * 2L)

            let graphOk =
                match g0, g1, g2 with
                | Some s0, Some s1, Some s2 ->
                    s0.Nodes.Count = 2
                    && s0.Edges.Count = 1
                    && s1.Nodes.Count = 3
                    && s1.Edges.Count = 2
                    // n2 removed => n1, n3 remain and both edges are pruned.
                    && s2.Nodes.Count = 2
                    && s2.Edges.Count = 0
                    && s2.Nodes.ContainsKey "n1"
                    && s2.Nodes.ContainsKey "n3"
                    && not (s2.Nodes.ContainsKey "n2")
                | _ -> false

            let graphDetail =
                match g0, g1, g2 with
                | Some s0, Some s1, Some s2 ->
                    $"t0 ={s0.Nodes.Count}n/{s0.Edges.Count}e, t1 ={s1.Nodes.Count}n/{s1.Edges.Count}e, t2 ={s2.Nodes.Count}n/{s2.Edges.Count}e"
                | _ -> "reconstruction returned None; no keyframe found at or before the requested time"

            // 5. Carrier encode/decode symmetry, as used by the mp4 mett track.
            let carrier =
                { LayerId = Mdkg.LayerId.Timing
                  PtsTicks = 12345L
                  DurationTicks = frameDuration
                  Flags = uint32 Mdkg.MdkgSampleFlags.KeyFrame
                  Codec = Mdkg.Registry.Timing.Codec
                  Payload = [| 1uy; 2uy; 3uy; 4uy; 5uy |] }

            let decoded = carrier |> Mdkg.Carrier.encode |> Mdkg.Carrier.decode

            let carrierOk =
                decoded.LayerId = carrier.LayerId
                && decoded.PtsTicks = carrier.PtsTicks
                && decoded.DurationTicks = carrier.DurationTicks
                && decoded.Flags = carrier.Flags
                && decoded.Codec = carrier.Codec
                && decoded.Payload = carrier.Payload

            // 6. Unknown layers must be skippable: an unregistered id reads empty
            // rather than throwing, so future layers stay forward-compatible.
            let unknownOk =
                try
                    reader.ReadLayer(9999u).Length = 0
                with _ ->
                    false

            // 7. Schema path addressing: format/parse round-trip and resolution.
            let samplePath = MdkgIntel.makePath Mdkg.Registry.OcrText.Name frameDuration (Some "/ocr/text")
            let formatted = MdkgIntel.formatPath samplePath
            let reparsed = MdkgIntel.tryParsePath formatted

            let pathParseOk =
                reparsed = Some samplePath
                && formatted = $"mdkg://ocr-text@{frameDuration}#/ocr/text"
                && MdkgIntel.tryParsePath "not-a-path" = None
                && MdkgIntel.tryParsePath "mdkg://timing@notanumber" = None

            let resolvedValue = MdkgIntel.resolve reader samplePath
            let resolveOk = resolvedValue = Some "Open the Settings dialog"

            // Nested array pointer resolution.
            let nestedOk =
                (MdkgIntel.resolve reader (MdkgIntel.makePath Mdkg.Registry.UiAffordance.Name 0L (Some "/hints/0/label"))) = Some "Start"

            // Missing pointers and missing samples must return None, not throw.
            let missingOk =
                MdkgIntel.resolve reader (MdkgIntel.makePath Mdkg.Registry.OcrText.Name 0L (Some "/does/not/exist")) = None
                && MdkgIntel.resolve reader (MdkgIntel.makePath Mdkg.Registry.OcrText.Name 999999L (Some "/ocr/text")) = None

            // 8. Streaming timeline must be globally PTS-ordered across layers
            // and must not read payloads to enumerate.
            let timelineItems = MdkgIntel.fullTimeline reader |> Seq.toArray

            let ordered =
                timelineItems
                |> Array.pairwise
                |> Array.forall (fun (a, b) -> a.Entry.PtsTicks <= b.Entry.PtsTicks)

            let timelineOk = timelineItems.Length = 10 && ordered

            let windowItems =
                MdkgIntel.window reader 0L frameDuration |> Seq.toArray

            let windowOk =
                windowItems.Length > 0
                && windowItems |> Array.forall (fun item -> item.Entry.PtsTicks < frameDuration)

            // 9. Runbook emission is deterministic and every proof re-resolves.
            let runbook = MdkgIntel.buildRunbook reader
            let runbookAgain = MdkgIntel.buildRunbook reader

            let deterministic =
                runbook.StepCount = runbookAgain.StepCount
                && Array.forall2
                    (fun (a: MdkgIntel.RunbookStep) (b: MdkgIntel.RunbookStep) ->
                        a.PtsTicks = b.PtsTicks
                        && a.Title = b.Title
                        && a.Proofs.Length = b.Proofs.Length)
                    runbook.Steps
                    runbookAgain.Steps

            let runbookOk =
                runbook.StepCount = 2
                && deterministic
                && runbook.Steps[0].Title = "Start"
                && runbook.Steps[1].Title = "Settings"
                && runbook.Steps |> Array.forall (fun step -> step.Proofs.Length > 0)

            let verification = MdkgIntel.verifyRunbook reader runbook

            let verifyOk =
                verification.Verified
                && verification.TotalProofs = verification.ResolvedProofs
                && verification.MismatchedProofs = 0
                && verification.TotalProofs > 0

            let markdown = MdkgIntel.toMarkdown runbook

            let markdownOk =
                markdown.Contains("# Yumlog Runbook")
                && markdown.Contains("mdkg://ocr-text@")
                && markdown.Contains("Settings")

            let resolvedText = defaultArg resolvedValue "<none>"

            [| check "mdkg.layerTable.roundTrip" layerOk layerDetail
               check "mdkg.header.fixed128" headerOk $"headerLength={header.HeaderLength} timescale={header.MovieTimescale} fileLength={fileLength}"
               check "mdkg.timing.roundTrip" timingOk $"{timingEntries.Length} timing samples"
               check "mdkg.timing.coverageNoOverlap" coverageOk coverageDetail
               check "mdkg.semanticGraph.reconstruct" graphOk graphDetail
               check "mdkg.carrier.symmetry" carrierOk "MDS1 carrier encode/decode is symmetric"
               check "mdkg.unknownLayer.skip" unknownOk "unregistered layer id reads as empty"
               check "intel.path.parseFormat" pathParseOk $"canonical form '{formatted}' round-trips; malformed input rejected"
               check "intel.path.resolve" (resolveOk && nestedOk) $"pointer resolution: /ocr/text -> {resolvedText}, /hints/0/label -> Start"
               check "intel.path.missingIsNone" missingOk "absent pointers and absent samples resolve to None without throwing"
               check "intel.timeline.ordered" timelineOk $"{timelineItems.Length} samples merged across layers in PTS order"
               check "intel.timeline.window" windowOk $"{windowItems.Length} samples in [0,{frameDuration}) reasoning window"
               check "intel.runbook.deterministic" runbookOk $"{runbook.StepCount} steps, stable across repeated builds"
               check "intel.runbook.proofsVerify" verifyOk $"{verification.ResolvedProofs}/{verification.TotalProofs} proofs re-resolved, {verification.MismatchedProofs} drifted"
               check "intel.runbook.markdown" markdownOk "markdown renders with machine-resolvable citations" |]
        finally
            try
                if not (String.IsNullOrWhiteSpace(sampleOut)) && File.Exists(path) then
                    match Path.GetDirectoryName(Path.GetFullPath(sampleOut)) with
                    | null | "" -> ()
                    | parent -> Directory.CreateDirectory(parent) |> ignore

                    File.Copy(path, sampleOut, true)
            with _ ->
                ()

            try
                if File.Exists(path) then File.Delete(path)
            with _ ->
                ()

    let private encoderProbe () =
        // Capability probes are reported, never asserted: this machine may or may
        // not have a usable NVENC device or an x265 shared library.
        let nvenc =
            try
                let dll = Environment.GetEnvironmentVariable("SystemRoot")

                let systemRoot =
                    match dll with
                    | null -> @"C:\Windows"
                    | root -> root

                let path = Path.Combine(systemRoot, "System32", "nvEncodeAPI64.dll")
                File.Exists(path)
            with _ ->
                false

        [| check "probe.nvenc.driverDll" true (if nvenc then "nvEncodeAPI64.dll present" else "nvEncodeAPI64.dll not found; NVENC backend unavailable") |]

    let run (sampleOut: string) =
        let cases = Array.append (mdkgRoundTrip sampleOut) (encoderProbe ())
        let failed = cases |> Array.filter (fun case -> not case.Passed)

        { Passed = failed.Length = 0
          PassedCount = cases.Length - failed.Length
          FailedCount = failed.Length
          Cases = cases }
