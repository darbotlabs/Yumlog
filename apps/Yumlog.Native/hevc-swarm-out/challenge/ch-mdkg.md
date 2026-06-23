Agent is idle (waiting for messages). agent_id: ch-mdkg, agent_type: dayour-swe, status: idle, description: Challenge .mdkg spec completeness, elapsed: 158s, total_turns: 1

[Turn 0]
**Summary**: The `.mdkg` track is not complete as a round-trippable, synchronized metadata track spec. The most severe implementation bug is a writer/reader layer-table field-order mismatch that corrupts descriptors on read; the most severe design gap is that the semantic-graph layer is only sketched, not fully specified or reconstructable with normative rules.

## Findings

1. **[BLOCKER] Layer table write/read order is not symmetric, corrupting descriptor round-trip**

   **Where**:
   - `Mdkg.fs:445-459` `writeLayerTable`
   - `Mdkg.fs:461-473` `readLayerTable`
   - Spec layout agrees with writer at `MDKG-SPEC.md:73-89`

   **Problem**: The writer emits each layer as:

   ```text
   layerId, layerFlags, name, schemaUri, codec, description
   ```

   But the reader parses it as:

   ```text
   layerId, name, schemaUri, codec, flags, description
   ```

   Specifically:

   ```fsharp
   // writer
   writer.WriteUInt32(layer.Id)
   writer.WriteUInt32(layer.Flags)
   writer.WriteString16(layer.Name)
   writer.WriteString16(layer.SchemaUri)
   writer.WriteString16(layer.Codec)
   writer.WriteString16(layer.Description)
   ```

   versus:

   ```fsharp
   // reader
   { Id = reader.ReadUInt32()
     Name = reader.ReadString16()
     SchemaUri = reader.ReadString16()
     Codec = reader.ReadString16()
     Flags = reader.ReadUInt32()
     Description = reader.ReadString16() }
   ```

   The first `ReadString16()` consumes the high 16 bits of `Flags` as a string length. For built-in flags like `Required = 0x00000001`, this produces an empty name, then subsequent string lengths are read from the remaining flag bytes and descriptor payload. In practice this can yield corrupted descriptors or `EndOfStreamException`.

   **Impact**: `MdkgWriter -> file -> MdkgReader` does not reproduce the layer table. This breaks conformance, unknown-layer preservation, codec dispatch, and any future remux/copy workflow.

   **Concrete fix**:

   ```fsharp
   let private readLayerTable (stream: Stream) (header: MdkgHeader) =
       stream.Position <- int64 header.LayerTableOffset
       let reader = BeReader(stream)
       let count = reader.ReadUInt32() |> int

       if uint32 count <> header.LayerCount then
           invalidData $"Layer table count {count} does not match header layer count {header.LayerCount}."

       [| for _ in 1 .. count do
              let id = reader.ReadUInt32()
              let flags = reader.ReadUInt32()
              let name = reader.ReadString16()
              let schemaUri = reader.ReadString16()
              let codec = reader.ReadString16()
              let description = reader.ReadString16()

              yield
                  { Id = id
                    Name = name
                    SchemaUri = schemaUri
                    Codec = codec
                    Flags = flags
                    Description = description } |]
   ```

2. **[HIGH] Header byte count is correct, but reserved bytes are not validated and layout integrity is not enforced**

   **Where**:
   - `Mdkg.fs:390-408` `writeHeader`
   - `Mdkg.fs:409-443` `readHeader`
   - Spec at `MDKG-SPEC.md:54-71`

   **What is correct**: The fixed header does sum to 128 bytes.

   ```text
   magic                 8
   major/minor           4
   headerLength          4
   movieTimescale        8
   layerTableOffset      8
   layerTableLength      8
   layerCount            4
   sampleIndexOffset     8
   sampleIndexLength     8
   dataOffset            8
   dataLength            8
   fileFlags             4
   subtotal             80
   reserved             48
   total               128
   ```

   `writeHeader` correctly writes `FixedHeaderSize - 80`, i.e. 48 reserved bytes at `Mdkg.fs:407`.

   **Problem**: `readHeader` stops after `FileFlags` and never consumes or validates the 48 reserved bytes. It also does not validate that:

   - `LayerTableOffset >= HeaderLength`
   - `LayerTableOffset + LayerTableLength <= stream.Length`
   - `DataOffset + DataLength <= stream.Length`
   - `SampleIndexOffset + SampleIndexLength <= stream.Length`
   - regions are ordered and non-overlapping
   - `LayerTableLength` and `SampleIndexLength` match actual bytes consumed

   **Impact**: Forward compatibility and corruption detection are aspirational. A malformed file can point sample entries into the header, layer table, sample index, or beyond EOF, and the reader will trust it.

   **Concrete fix**: Add region validation immediately after `readHeader`, and validate reserved bytes are zero for v1 unless a future minor version explicitly defines them.

3. **[BLOCKER] Round-trip correctness is not currently true because descriptors are corrupted before typed layer dispatch can be trusted**

   **Where**:
   - `Mdkg.fs:737-743` `MdkgReader` constructor
   - `Mdkg.fs:755-773` raw layer/sample reading
   - `Mdkg.fs:794-807` typed sample decoding

   **Problem**: Raw sample payloads are written with absolute offsets and can be read byte-for-byte if the caller already has a valid `MdkgSampleEntry`. But a full `MdkgWriter -> file -> MdkgReader` round trip includes the layer table, and the layer table is not readable due to finding 1.

   **Additional issue**: The API does not expose a complete “read all samples grouped with descriptors” operation, nor does it verify that each `MdkgSampleEntry.LayerId` exists in the read descriptor table.

   **Impact**: The format cannot honestly claim whole-file round-trip correctness. Payload bytes may survive, but layer identity, codec, schema URI, and flags are corrupted.

   **Concrete fix**:
   - Fix layer table parsing.
   - Add a round-trip unit test that writes all built-in layers plus an unknown extension layer, then asserts descriptor equality, sample index equality, and payload byte equality.
   - Add validation that every indexed layer id has a descriptor.

4. **[HIGH] Sample index offsets are absolute and mostly symmetric, but the reader does not validate index/payload boundaries**

   **Where**:
   - `Mdkg.fs:613-635` `WriteSample`
   - `Mdkg.fs:475-499` `writeSampleIndex`
   - `Mdkg.fs:501-523` `readSampleIndex`
   - `Mdkg.fs:760-773` `ReadSample`

   **What works**:
   - Payload offset is captured before writing payload at `Mdkg.fs:626`.
   - Payload length is stored at `Mdkg.fs:634`.
   - Index writes `ptsTicks`, `durationTicks`, `byteOffset`, `byteLength`, `flags` in big-endian at `Mdkg.fs:492-497`.
   - Reader parses the same sample-entry order at `Mdkg.fs:514-519`.

   **Problem**: The reader does not verify that `ByteOffset` and `ByteLength` fall within `[DataOffset, DataOffset + DataLength)`. It also does not verify that the declared sample index length equals the bytes consumed by `readSampleIndex`.

   **Impact**: A corrupt or malicious file can make `ReadSample` read arbitrary bytes from the `.mdkg` file, including the header, layer table, sample index, or EOF.

   **Concrete fix**:

   ```fsharp
   let private validateSampleEntry (header: MdkgHeader) (entry: MdkgSampleEntry) =
       let start = entry.ByteOffset
       let finish = entry.ByteOffset + uint64 entry.ByteLength
       let dataStart = header.DataOffset
       let dataEnd = header.DataOffset + header.DataLength

       if start < dataStart || finish > dataEnd || finish < start then
           invalidData
               $"Sample for layer {entry.LayerId} points outside data region: offset={start}, length={entry.ByteLength}."
   ```

5. **[HIGH] The timing guarantees are prose only; the writer API cannot enforce one timing sample per video frame**

   **Where**:
   - Spec guarantee at `MDKG-SPEC.md:154-162`
   - `Mdkg.fs:613-635` `WriteSample`
   - `Mdkg.fs:637-651` `WriteTiming`
   - `Mdkg.fs:691-728` `Close`

   **Problem**: The spec says every encoded video frame must have exactly one timing-layer sample, dense and frame-ordered. The API does not enforce any of this.

   The current writer allows:
   - zero timing samples
   - duplicate timing samples at the same PTS
   - missing frame indices
   - non-monotonic PTS
   - negative `ptsTicks`
   - OCR/UI/graph samples with no corresponding timing sample
   - arbitrary `FrameIndex` values unrelated to write order
   - closing a file with required layer descriptors but no required samples

   **Impact**: The `.mdkg` file can be syntactically valid but semantically unusable for synchronized playback.

   **Concrete fix**:
   - Track timing samples in `MdkgWriter`.
   - Reject non-monotonic timing samples.
   - Reject duplicate timing `PtsTicks`.
   - Optionally accept an expected frame count and validate it at `Close`.
   - For non-timing layers, optionally require exact PTS membership in the timing layer, or expose a validation function that enforces this before finalization.

6. **[HIGH] The 100 ns timescale is asserted but not proven identical to the HEVC muxer timeline**

   **Where**:
   - `Mdkg.fs:15` `MovieTimescale = 10_000_000L`
   - `Mdkg.fs:229-233` `frameDurationTicksFromFps`
   - `Mdkg.fs:243-252` `qpcToPtsTicks`
   - Spec at `MDKG-SPEC.md:145-163`

   **Problem**: `.mdkg` uses 10,000,000 ticks/sec and QPC conversion, but no HEVC muxer implementation is present in the inspected source. The spec references `HevcMux.fs` at `MDKG-SPEC.md:1161-1194`, but no `HevcMux.fs` or metadata mux integration was found in `E:\Yumlog\apps\Yumlog.Native`.

   There is also a subtle rounding mismatch risk:
   - `qpcToPtsTicks` truncates integer division at `Mdkg.fs:252`.
   - `frameDurationTicksFromFps` rounds duration at `Mdkg.fs:233`.

   If the video muxer uses a different rounding policy, nominal frame PTS and `.mdkg` PTS can drift or disagree.

   **Impact**: The “same timeline as HEVC MP4 muxer” claim is not enforceable from the current module. A reader cannot know whether MP4 sample timestamps and `.mdkg` PTS are actually identical.

   **Concrete fix**:
   - Define one shared timing module used by both HEVC muxing and `.mdkg`.
   - Store `firstQpc`, `qpcFrequency`, `frameIndex`, and exact video PTS in the timing layer.
   - Add integration tests that mux video plus metadata and verify MP4 PTS equals carrier `PtsTicks`.

7. **[MEDIUM] Reader seek semantics are insufficient for synchronized multi-layer access**

   **Where**:
   - `Mdkg.fs:775-787` `Seek`
   - `Mdkg.fs:789-792` `SeekExact`
   - Spec at `MDKG-SPEC.md:159-162` and `MDKG-SPEC.md:1250-1253`

   **Problem**: `Seek` operates on one layer only. It returns:
   - a sample covering `ptsTicks`, or
   - the latest sample at or before `ptsTicks`.

   That is not enough to implement the spec’s promised behavior:

   ```text
   find exact timing sample for that frame;
   find nearest/same-PTS OCR and UI affordance samples;
   reconstruct semantic graph state using latest keyframe + deltas
   ```

   There is no API that:
   - seeks a video frame PTS and returns all same-PTS layer samples
   - distinguishes “nearest previous stateful sample” from “stale sparse observation”
   - returns all samples with the same PTS when multiple entries exist
   - reconstructs graph state at time T

   **Impact**: A downstream reader has to reinvent synchronization and graph reconstruction. The module does not provide the claimed reader semantics.

   **Concrete fix**:
   Add APIs like:

   ```fsharp
   member this.ReadSamplesAtPts(ptsTicks: int64) : MdkgSample array =
       sampleIndex.Values
       |> Seq.collect id
       |> Seq.filter (fun e -> e.PtsTicks = ptsTicks)
       |> Seq.map this.ReadSample
       |> Seq.toArray
   ```

   and a separate stateful graph reconstruction API.

8. **[BLOCKER] Semantic-graph layer is not fully specified or reconstructable**

   **Where**:
   - `Mdkg.fs:121-167` graph record types
   - `Mdkg.fs:669-681` `WriteSemanticGraph`
   - `Mdkg.fs:803-804` `ReadSemanticGraph`
   - Spec at `MDKG-SPEC.md:1110-1158`

   **Problem**: The semantic graph has useful record shapes, but the spec is not complete enough to guarantee reconstruction.

   Missing or underspecified:
   - No normative node-kind registry. `Kind: string` is unconstrained.
   - Edge kinds are listed only as prose guidance at `MDKG-SPEC.md:1148-1152`; no enum, versioned registry, or required semantics.
   - Stable node IDs are guidance only at `MDKG-SPEC.md:1142-1147`; no required algorithm, collision handling, normalization, bbox quantization rules, or recording ID definition.
   - No normative edge ID algorithm.
   - No reconstruction function in `Mdkg.fs`.
   - No rule for applying `UpdateNodes` or `UpdateEdges`: full replacement, merge by attributes, patch by non-null fields, or last-write-wins?
   - No rule for update of a missing node/edge.
   - No rule for remove of a missing node/edge.
   - No referential-integrity rule for edges whose source/target nodes are absent.
   - No ordering rule when a delta both updates and removes the same id.
   - No rule for `IsKeyFrame = true` with non-empty `Delta`.
   - No rule for `IsKeyFrame = false` with non-empty `KeyFrameNodes`.
   - No rule for multiple graph samples at same PTS.
   - No tombstone/identity continuity model for `same-as`.
   - No conformance test vector.

   **Impact**: Two conforming implementations can reconstruct different graph states from the same sample sequence. The “KG” part is therefore not yet a real interoperable metadata knowledge graph.

   **Concrete fix**:
   Define a normative graph-state algorithm. For example:

   ```fsharp
   type GraphState =
       { Nodes: Map<string, SemanticGraphNode>
         Edges: Map<string, SemanticGraphEdge> }

   let applyDelta (state: GraphState) (delta: SemanticGraphDelta) =
       let nodesAfterRemove =
           delta.RemoveNodeIds
           |> Array.fold (fun nodes id -> Map.remove id nodes) state.Nodes

       let nodesAfterAdd =
           delta.AddNodes
           |> Array.fold (fun nodes node -> Map.add node.Id node nodes) nodesAfterRemove

       let nodesAfterUpdate =
           delta.UpdateNodes
           |> Array.fold (fun nodes node ->
               if not (Map.containsKey node.Id nodes) then
                   invalidData $"UpdateNode references missing node '{node.Id}'."
               Map.add node.Id node nodes) nodesAfterAdd

       let edgesAfterRemove =
           delta.RemoveEdgeIds
           |> Array.fold (fun edges id -> Map.remove id edges) state.Edges

       let edgesAfterAdd =
           delta.AddEdges
           |> Array.fold (fun edges edge ->
               if not (Map.containsKey edge.SourceNodeId nodesAfterUpdate) ||
                  not (Map.containsKey edge.TargetNodeId nodesAfterUpdate) then
                   invalidData $"Edge '{edge.Id}' references missing node."
               Map.add edge.Id edge edges) edgesAfterRemove

       let edgesAfterUpdate =
           delta.UpdateEdges
           |> Array.fold (fun edges edge ->
               if not (Map.containsKey edge.Id edges) then
                   invalidData $"UpdateEdge references missing edge '{edge.Id}'."
               Map.add edge.Id edge edges) edgesAfterAdd

       { Nodes = nodesAfterUpdate
         Edges = edgesAfterUpdate }
   ```

   Then specify this ordering in `MDKG-SPEC.md` and add test vectors.

9. **[HIGH] `WriteSemanticGraph` flags can contradict the payload, and no keyframe/delta invariants are enforced**

   **Where**:
   - `Mdkg.fs:158-167` `SemanticGraphLayerSample`
   - `Mdkg.fs:669-681` `WriteSemanticGraph`

   **Problem**: `WriteSemanticGraph` sets sample flags from `sample.IsKeyFrame`, but it does not validate that the payload is internally consistent.

   Examples currently allowed:
   - `IsKeyFrame = true` with empty `KeyFrameNodes` and non-empty `Delta`
   - `IsKeyFrame = false` with full `KeyFrameNodes`
   - `IsKeyFrame = false` but `Delta = null`
   - keyframe sample written with no graph id
   - graph id changes mid-stream with no reset rule
   - delta before first keyframe

   **Impact**: A reader cannot safely reconstruct graph state even if it knows the desired algorithm.

   **Concrete fix**:
   Add `validateSemanticGraphSample` and call it from `WriteSemanticGraph`.

10. **[LOW] OCR and UI-affordance layer payloads do faithfully wrap the current `Domain.fs` shapes, but the spec should explicitly say this is domain-shape fidelity, not source-system fidelity**

   **Where**:
   - `Domain.fs:47-75` `TextPoint`, `TextBounds`, `OcrWord`, `OcrLine`, `OcrResult`
   - `Domain.fs:107-111` `UiNavigationHint`
   - `Mdkg.fs:104-118` `OcrTextLayerSample`, `UiAffordanceLayerSample`
   - `WindowsAiOcr.fs:84-91`, `WindowsAiOcr.fs:116-134`
   - `Navigation.fs:27-34`

   **Assessment**: The Mdkg layer records do carry the full current domain records:
   - `OcrResult.Provider`
   - `OcrResult.IsAvailable`
   - `OcrResult.Message`
   - `OcrResult.Text`
   - `OcrResult.Lines`
   - `OcrLine.Text`
   - `OcrLine.Words`
   - `OcrWord.Text`
   - `OcrWord.Confidence`
   - `OcrWord.BoundingBox`
   - all four `TextBounds` points
   - `UiNavigationHint.Kind`
   - `UiNavigationHint.Label`
   - `UiNavigationHint.Confidence`
   - `UiNavigationHint.Bounds`

   **Caveat**: `Navigation.fs` currently infers hint bounds from only the first word of the line or `fullBounds` at `Navigation.fs:30-34`. That is not a Mdkg serialization loss, but it is a source-fidelity limitation. If the goal is pixel-accurate UI affordances, the source model needs richer bounds inference.

   **Concrete fix**:
   State explicitly in the spec: “ocr-text and ui-affordance are lossless with respect to `Domain.fs` records, not necessarily with respect to the original OCR engine or UI Automation source.”

11. **[MEDIUM] Unknown-layer support is partially real for raw samples, but copy/remux preservation is not implemented**

   **Where**:
   - `Mdkg.fs:578-595` `AddLayer`
   - `Mdkg.fs:755-773` `ReadLayer` and `ReadSample`
   - Spec unknown-layer rule at `MDKG-SPEC.md:1264-1272`

   **Problem**: The reader can expose raw sample entries and raw payload bytes for unknown layer IDs. That is good. But the spec says readers should preserve unknown layers during copy/remux at `MDKG-SPEC.md:1261-1262`, and the module provides no copy/remux API.

   Worse, due to finding 1, layer descriptors are currently corrupted, so unknown-layer preservation does not actually work end-to-end.

   **Impact**: Forward compatibility is partly aspirational. Unknown raw bytes may be recoverable, but descriptor fidelity and remux preservation are not guaranteed.

   **Concrete fix**:
   - Fix descriptor parsing.
   - Add `CopyTo(writer)` or `RewriteMdkg(input, output)` that preserves descriptors, sample entries, flags, and payload bytes exactly.
   - Add an unknown-layer round-trip test.

12. **[MEDIUM] Codec MIME/version scheme exists, but there is no parser or conformance behavior tied to it**

   **Where**:
   - `Mdkg.fs:254-301` `Registry`
   - `Mdkg.fs:794-807` typed readers
   - Spec at `MDKG-SPEC.md:133-141`, `MDKG-SPEC.md:1254-1262`

   **Problem**: Codec strings are defined, but the reader’s typed methods ignore the descriptor codec. A caller can invoke `ReadOcrText` on any sample entry regardless of layer id or codec.

   **Impact**: Codec dispatch is not coherent. A file can declare a different codec for layer 2 and the typed method will still try to deserialize it as `OcrTextLayerSample`.

   **Concrete fix**:
   Add typed read methods that accept or resolve the layer descriptor and verify codec compatibility before deserialization.

13. **[MEDIUM] MDS1 carrier byte layout is symmetric, but MP4 multiplexing is only an envelope, not a complete carrier model**

   **Where**:
   - `Mdkg.fs:813-861` `Carrier.encode` and `Carrier.decode`
   - Spec at `MDKG-SPEC.md:1063-1107`

   **What works**:
   - `Carrier.encode` writes:
     `magic`, `layerId`, `ptsTicks`, `durationTicks`, `flags`, `codecLen`, `codec`, `payloadLen`, `payload`
   - `Carrier.decode` reads the same order.
   - Big-endian consistency is maintained through `BeWriter` and `BeReader`.

   **Problems**:
   - `Carrier.decode` does not reject trailing bytes after `payload`.
   - `payloadLen` is `uint32` but immediately converted to `int` at `Mdkg.fs:853`, which is unsafe for malformed values above `Int32.MaxValue`.
   - No API groups multiple MDS1 samples at the same MP4 PTS into a synchronized set.
   - No MP4 `mett` sample description, handler type, MIME, timescale, or ordering rules are specified beyond prose.
   - No actual HEVC/MP4 muxer integration exists in the inspected code.

   **Impact**: The MDS1 envelope is plausible, but “multiplexing multiple layers through one `mett` track at the same PTS works on read-back” is not demonstrated by this module.

   **Concrete fix**:
   - Define MP4 track timescale and sample description requirements.
   - Add a `Carrier.decodeManyAtPts` or demux grouping abstraction.
   - Reject trailing bytes and invalid lengths.
   - Add mux/demux integration tests.

14. **[HIGH] Input-event and broader synchronized metadata goals are underspecified**

   **Where**:
   - `Mdkg.fs:169-191` `InputEvent`, `AppFocusChange`, `InputEventLayerSample`
   - `Mdkg.fs:683-689` `WriteInputEvent`
   - Spec layer reservation at `MDKG-SPEC.md:140-141`

   **Problem**: The input-event layer is no longer merely reserved in code; it has generic records. But it is still semantically weak:
   - `InputEvent.Kind` is unconstrained.
   - `Attributes` are string key/value only.
   - No keyboard/mouse schema.
   - No scroll schema.
   - No clipboard schema.
   - No window-focus lifecycle semantics beyond `AppFocusChange`.
   - No audio marker layer.
   - No active display/monitor topology layer.
   - No process/window identity stability rules.
   - No privacy/redaction rules for sensitive inputs.

   **Impact**: The stated goal of “synchronized metadata tracks” is only partially addressed. OCR and UI hints are represented; richer native recorder metadata remains undefined.

   **Concrete fix**:
   Define separate v1 schemas or extension profiles for:
   - `window-focus`
   - `input-keyboard`
   - `input-pointer`
   - `scroll`
   - `clipboard`
   - `audio-marker`
   - `display-topology`
   - `process-window-map`

15. **[MEDIUM] The writer does not validate layer/sample semantic consistency**

   **Where**:
   - `Mdkg.fs:578-595` `AddLayer`
   - `Mdkg.fs:613-635` `WriteSample`
   - `Mdkg.fs:637-689` typed write helpers

   **Problem**: `WriteSample` only checks:
   - writer is open
   - payload is non-null
   - duration is non-negative
   - layer exists

   It does not validate:
   - non-negative PTS
   - monotonic PTS per layer
   - duplicate PTS per layer
   - required layer presence
   - required sample presence
   - flags allowed for layer
   - typed payload `PtsTicks` equals sample index `PtsTicks`
   - typed payload `DurationTicks` equals sample index `DurationTicks`
   - typed payload `FrameIndex` consistency

   **Impact**: The sample index can disagree with the JSON payload. A reader may use the index for seeking but the payload for interpretation and get contradictory timing.

   **Concrete fix**:
   In typed writers, validate payload timing against index timing before serialization. In generic `WriteSample`, expose it as unsafe/opaque or require callers to opt into raw mode.

## Recommendations

1. **Owner: Mdkg module maintainer** — Fix `readLayerTable` immediately. This is the current round-trip blocker.

2. **Owner: Mdkg module maintainer** — Add a conformance test suite with:
   - built-in descriptor round-trip
   - unknown descriptor round-trip
   - sample payload byte-exact round-trip
   - corrupt offset rejection
   - malformed layer-table rejection
   - carrier encode/decode round-trip
   - semantic graph keyframe plus delta reconstruction test vector

3. **Owner: spec author** — Promote semantic graph prose into a normative algorithm:
   - node kind registry
   - edge kind registry
   - stable ID algorithms
   - delta application order
   - missing update/remove behavior
   - referential integrity
   - same-PTS ordering
   - keyframe cadence
   - reconstruction pseudocode
   - test vectors

4. **Owner: recorder/mux integrator** — Create one shared timeline implementation used by video muxing and `.mdkg`, then prove MP4 PTS equals `.mdkg` `PtsTicks` in an integration test.

5. **Owner: reader API maintainer** — Add synchronized read APIs:
   - `ReadSamplesAtPts`
   - `ReadFrameMetadata`
   - `ReadGraphStateAt`
   - `CopyPreservingUnknownLayers`

6. **Owner: spec author** — Either narrow the v1 goal to OCR/UI/timing/graph, or define concrete extension schemas for input, focus, clipboard, scroll, audio, and display metadata.

## Code Snippets

### Fix the layer-table reader ordering

```fsharp
let private readLayerTable (stream: Stream) (header: MdkgHeader) =
    stream.Position <- int64 header.LayerTableOffset
    let reader = BeReader(stream)
    let count = reader.ReadUInt32() |> int

    if uint32 count <> header.LayerCount then
        invalidData $"Layer table count {count} does not match header layer count {header.LayerCount}."

    [| for _ in 1 .. count do
           let id = reader.ReadUInt32()
           let flags = reader.ReadUInt32()
           let name = reader.ReadString16()
           let schemaUri = reader.ReadString16()
           let codec = reader.ReadString16()
           let description = reader.ReadString16()

           yield
               { Id = id
                 Name = name
                 SchemaUri = schemaUri
                 Codec = codec
                 Flags = flags
                 Description = description } |]
```

### Add sample region validation

```fsharp
let private validateSampleEntry (header: MdkgHeader) (entry: MdkgSampleEntry) =
    let dataStart = header.DataOffset
    let dataEnd = header.DataOffset + header.DataLength
    let sampleStart = entry.ByteOffset
    let sampleEnd = entry.ByteOffset + uint64 entry.ByteLength

    if sampleEnd < sampleStart then
        invalidData $"Sample range overflow for layer {entry.LayerId}."

    if sampleStart < dataStart || sampleEnd > dataEnd then
        invalidData
            $"Sample for layer {entry.LayerId} points outside data region: offset={sampleStart}, length={entry.ByteLength}."
```

### Add a normative semantic graph reconstruction function

```fsharp
type GraphState =
    { Nodes: Map<string, SemanticGraphNode>
      Edges: Map<string, SemanticGraphEdge> }

let graphStateFromKeyframe (sample: SemanticGraphLayerSample) =
    if not sample.IsKeyFrame then
        invalidData "Graph reconstruction must start from a keyframe sample."

    { Nodes =
        sample.KeyFrameNodes
        |> Array.map (fun n -> n.Id, n)
        |> Map.ofArray
      Edges =
        sample.KeyFrameEdges
        |> Array.map (fun e -> e.Id, e)
        |> Map.ofArray }

let applyGraphDelta (state: GraphState) (delta: SemanticGraphDelta) =
    let nodes =
        delta.RemoveNodeIds
        |> Array.fold (fun acc id -> Map.remove id acc) state.Nodes

    let nodes =
        delta.AddNodes
        |> Array.fold (fun acc node -> Map.add node.Id node acc) nodes

    let nodes =
        delta.UpdateNodes
        |> Array.fold
            (fun acc node ->
                if not (Map.containsKey node.Id acc) then
                    invalidData $"UpdateNode references missing node '{node.Id}'."
                Map.add node.Id node acc)
            nodes

    let edges =
        delta.RemoveEdgeIds
        |> Array.fold (fun acc id -> Map.remove id acc) state.Edges

    let edges =
        delta.AddEdges
        |> Array.fold
            (fun acc edge ->
                if not (Map.containsKey edge.SourceNodeId nodes) ||
                   not (Map.containsKey edge.TargetNodeId nodes) then
                    invalidData $"Edge '{edge.Id}' references missing node."
                Map.add edge.Id edge acc)
            edges

    let edges =
        delta.UpdateEdges
        |> Array.fold
            (fun acc edge ->
                if not (Map.containsKey edge.Id acc) then
                    invalidData $"UpdateEdge references missing edge '{edge.Id}'."
                Map.add edge.Id edge acc)
            edges

    { Nodes = nodes
      Edges = edges }
```

## VERDICT: INCOMPLETE

The `.mdkg` spec and module are not complete enough to claim a native lossless HEVC synchronized metadata track with round-trip correctness.

**Is the semantic-graph layer fully specified and reconstructable? No.**

It has record types and prose guidance, but it lacks a normative graph-state model, required node/edge kinds, stable ID algorithms, delta application rules, validation rules, reconstruction API, and conformance test vectors.