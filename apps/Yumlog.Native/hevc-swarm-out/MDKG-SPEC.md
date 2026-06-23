# .mdkg - Metadata Knowledge Graph Layered Track Specification

Authored by the HEVC swarm (dayour-architect) for Yumlog.Native, 2026-06-18.

Companion source: ../Mdkg.fs (890 lines, module Mdkg).

---

## Architecture Summary

Rating: SOLID.

`.mdkg` should be the canonical metadata model, and the MP4 `mett` timed-metadata track should be treated as one carrier for `.mdkg` layer samples. This keeps the metadata format independent of MP4 while still allowing the lossless HEVC `.mp4` to embed frame-accurate metadata.

Decision: use UTF-8 JSON payloads inside a binary indexed `.mdkg` container, not CBOR.

Justification:
- The current project has no CBOR dependency.
- `System.Text.Json` is available in-box and keeps the F# source compiling without package changes.
- The `.mdkg` container itself provides byte lengths, offsets, layer ids, and sample tables, so JSON payloads do not need internal length prefixes in standalone `.mdkg`.
- For MP4 embedding, a small binary envelope wraps each layer sample so multiple `.mdkg` layers can be multiplexed through one `mett` track.

If compactness later becomes critical, add a new codec such as `application/vnd.yumlog.mdkg.timing+cbor;version=1` without breaking existing readers.

---

## `.mdkg` Byte Layout Specification

Magic:

```text
4D 44 4B 47 0D 0A 1A 0A
ASCII-ish: "MDKG\r\n\x1A\n"
```

Version:

```text
major = 1
minor = 0
```

All integers are big-endian.

### File Layout

```text
FixedHeader, 128 bytes
LayerTable, variable length
SamplePayloadData, variable length
SampleIndex, variable length
```

### FixedHeader, 128 bytes

```text
uint8[8]   magic                  = MDKG 0D 0A 1A 0A
uint16     majorVersion           = 1
uint16     minorVersion           = 0
uint32     headerLength           = 128
uint64     movieTimescale         = 10000000
uint64     layerTableOffset
uint64     layerTableLength
uint32     layerCount
uint64     sampleIndexOffset
uint64     sampleIndexLength
uint64     dataOffset
uint64     dataLength
uint32     fileFlags
uint8[48]  reserved               = zero
```

### LayerTable

```text
uint32 layerCount

repeated layerCount:
  uint32 layerId
  uint32 layerFlags
  uint16 nameUtf8Length
  uint8[] nameUtf8
  uint16 schemaUriUtf8Length
  uint8[] schemaUriUtf8
  uint16 codecUtf8Length
  uint8[] codecUtf8
  uint16 descriptionUtf8Length
  uint8[] descriptionUtf8
```

Layer flags:

```text
0x00000001 = required layer
0x00000002 = sparse layer
0x00000004 = graph layer
0x00000008 = extension layer
```

### SamplePayloadData

Payload bytes are written consecutively. Payloads are not length-prefixed in the data region because lengths live in the sample index.

### SampleIndex

```text
uint32 indexedLayerCount

repeated indexedLayerCount:
  uint32 layerId
  uint32 sampleCount

  repeated sampleCount:
    int64  ptsTicks
    int64  durationTicks
    uint64 byteOffset
    uint32 byteLength
    uint32 sampleFlags
```

Sample flags:

```text
0x00000001 = keyframe
0x00000002 = delta
0x00000004 = gap
0x00000008 = dropped
0x00000010 = sparse
```

---

## Required Built-In Layers

| Id | Name | Codec | Required |
|---:|------|-------|----------|
| 1 | timing | `application/vnd.yumlog.mdkg.timing+json;version=1` | yes |
| 2 | ocr-text | `application/vnd.yumlog.mdkg.ocr-text+json;version=1` | no |
| 3 | ui-affordance | `application/vnd.yumlog.mdkg.ui-affordance+json;version=1` | no |
| 4 | semantic-graph | `application/vnd.yumlog.mdkg.semantic-graph+json;version=1` | no |
| 5 | input-event | `application/vnd.yumlog.mdkg.input-event+json;version=1` | no |

---

## Time Alignment

The `.mdkg` timeline uses the same 100 ns media timescale as the HEVC MP4 muxer:

```text
movieTimescale = 10,000,000
ptsTicks = ((qpc - firstQpc) * 10,000,000) / QueryPerformanceFrequency()
```

Round-trip guarantee:

1. Every encoded video frame MUST have exactly one timing-layer sample.
2. Timing-layer samples are dense and frame-ordered.
3. Other layers MAY be sparse, but any sample they write MUST use a `ptsTicks` value that aligns to the same media timeline.
4. A downstream reader can seek by video PTS and:
   - find the exact timing sample for that frame;
   - find nearest/same-PTS OCR and UI affordance samples;
   - reconstruct semantic graph state using the latest keyframe graph sample plus deltas up to time T.

---

# Complete `Mdkg.fs`

```fsharp
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
    type MdkgCarrierSample =
        { LayerId: uint32
          PtsTicks: int64
          DurationTicks: int64
          Flags: uint32
          Codec: string
          Payload: byte array }

    [<CLIMutable>]
    type private MdkgHeader =
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
        JsonSerializerOptions(
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false)

    let serializeJson<'T> (value: 'T) =
        JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions)

    let deserializeJson<'T> (payload: byte array) =
        JsonSerializer.Deserialize<'T>(payload, jsonOptions)

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

        let BuiltIn =
            [| Timing
               OcrText
               UiAffordance
               SemanticGraph
               InputEvent |]

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
            let actual = if isNull value then "" else value
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

        [| for _ in 1 .. count do
               yield
                   { Id = reader.ReadUInt32()
                     Name = reader.ReadString16()
                     SchemaUri = reader.ReadString16()
                     Codec = reader.ReadString16()
                     Flags = reader.ReadUInt32()
                     Description = reader.ReadString16() } |]

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

            if isNull payload then
                invalidArg (nameof payload) "Payload must not be null."

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
                serializeJson sample)

        member this.WriteUiAffordance(sample: UiAffordanceLayerSample) =
            this.WriteSample(
                LayerId.UiAffordance,
                sample.PtsTicks,
                sample.DurationTicks,
                MdkgSampleFlags.Sparse,
                serializeJson sample)

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
                serializeJson sample)

        member this.WriteInputEvent(sample: InputEventLayerSample) =
            this.WriteSample(
                LayerId.InputEvent,
                sample.PtsTicks,
                sample.DurationTicks,
                MdkgSampleFlags.Sparse,
                serializeJson sample)

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
            this.ReadSample(entry).Payload |> deserializeJson<OcrTextLayerSample>

        member this.ReadUiAffordance(entry: MdkgSampleEntry) =
            this.ReadSample(entry).Payload |> deserializeJson<UiAffordanceLayerSample>

        member this.ReadSemanticGraph(entry: MdkgSampleEntry) =
            this.ReadSample(entry).Payload |> deserializeJson<SemanticGraphLayerSample>

        member this.ReadInputEvent(entry: MdkgSampleEntry) =
            this.ReadSample(entry).Payload |> deserializeJson<InputEventLayerSample>

        interface IDisposable with
            member _.Dispose() =
                stream.Dispose()

    module Carrier =

        [<Literal>]
        let private CarrierMagic = 0x4D445331u

        let encode (sample: MdkgCarrierSample) =
            use ms = new MemoryStream()
            let writer = BeWriter(ms)
            let codecBytes = Encoding.UTF8.GetBytes(if isNull sample.Codec then "" else sample.Codec)
            let payload = if isNull sample.Payload then Array.empty else sample.Payload

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

    let close (writer: MdkgWriter) =
        writer.Close()

    let openReader (path: string) =
        MdkgReader.Open(path)
```

---

## MP4 Embedding Model

`.mdkg` is canonical. MP4 `mett` is a carrier.

For standalone `.mdkg`, samples are stored in the `.mdkg` binary file and indexed by layer.

For MP4 embedding, each MP4 timed-metadata sample payload should be:

```text
Mdkg.Carrier.encode(...)
```

Carrier byte layout:

```text
uint32 magic = "MDS1"
uint32 layerId
int64  ptsTicks
int64  durationTicks
uint32 sampleFlags
uint16 codecUtf8Length
uint8[] codecUtf8
uint32 payloadLength
uint8[] payload
```

Recommended MP4 pattern:

```fsharp
let timingPayload = Mdkg.serializeJson timingSample

let carrier =
    Mdkg.Carrier.encode
        { LayerId = Mdkg.LayerId.Timing
          PtsTicks = timingSample.PtsTicks
          DurationTicks = timingSample.DurationTicks
          Flags = uint32 Mdkg.MdkgSampleFlags.KeyFrame
          Codec = Mdkg.Registry.Timing.Codec
          Payload = timingPayload }

hevcMux.WriteMetadataSample(carrier, qpc)
```

If multiple `.mdkg` layers have samples at the same video PTS, write multiple MP4 `mett` samples with the same PTS, one per layer sample. The MP4 sample’s timestamp and the carrier’s `PtsTicks` MUST match.

---

## Semantic Graph Layer Model

The semantic graph layer is sparse and reconstructable.

### Keyframe sample

A keyframe semantic graph sample sets:

```fsharp
IsKeyFrame = true
KeyFrameNodes = all graph nodes visible/known at this PTS
KeyFrameEdges = all graph edges visible/known at this PTS
Delta = empty delta
```

Use a keyframe:
- at recording start;
- after large UI scene changes;
- periodically, for example every 5 to 10 seconds;
- after dropped-frame gaps.

### Delta sample

A delta semantic graph sample sets:

```fsharp
IsKeyFrame = false
KeyFrameNodes = [||]
KeyFrameEdges = [||]
Delta = mutations since the previous graph sample
```

Node id guidance:
- Stable UI element: `ui:{windowId}:{automationId}` if available.
- OCR text entity: `text:{normalizedTextHash}:{bboxQuantized}`.
- Window: `window:{processId}:{windowId}`.
- Generated fallback: `node:{recordingId}:{frameIndex}:{ordinal}`.

Edge kinds:
- `contains`: window contains UI/text node.
- `follows`: one UI affordance follows another in navigation order.
- `same-as`: two detections across frames represent the same element.
- `transitions-to`: UI state/element changed into another state/element.

To answer “what UI graph existed at time T”:
1. Seek `semantic-graph` to the latest keyframe sample at or before T.
2. Apply deltas in PTS order until T.
3. Query the reconstructed node/edge set.

---

## Fsproj Compile Order

`Mdkg.fs` depends on `Domain.fs` types. `HevcMux.fs` can optionally use `Mdkg.Carrier.encode` when embedding layered metadata in MP4. Therefore place `Mdkg.fs` after `Domain.fs` and before `HevcMux.fs`.

Recommended compile order:

```xml
<ItemGroup>
  <Compile Include="Domain.fs" />
  <Compile Include="Mdkg.fs" />
  <Compile Include="HevcMux.fs" />
  <Compile Include="Json.fs" />
  <Compile Include="Config.fs" />
  <Compile Include="Capture.fs" />
  <Compile Include="Recording.fs" />
  <Compile Include="RuntimeIdentity.fs" />
  <Compile Include="WinAppRuntime.fs" />
  <Compile Include="RawWinRt.fs" />
  <Compile Include="WindowsAiOcr.fs" />
  <Compile Include="Ocr.fs" />
  <Compile Include="NativeUi.fs" />
  <Compile Include="Analysis.fs" />
  <Compile Include="Navigation.fs" />
  <Compile Include="Orchestration.fs" />
  <Compile Include="Cli.fs" />
  <Compile Include="Program.fs" />
</ItemGroup>
```

If `HevcMux.fs` does not reference `Mdkg.fs`, it may remain before `Mdkg.fs`, but the preferred architecture is:

```text
Domain.fs -> Mdkg.fs -> HevcMux.fs -> recorder integration
```

---

## Minimal Writer Flow

```fsharp
use mdkg = Mdkg.``open`` sidecarPath
mdkg.AddDefaultLayers()

let frequency = Mdkg.qpcFrequency()
let firstQpc = firstFrameQpc
let durationTicks = Mdkg.frameDurationTicksFromFps fps

for frame in frames do
    let ptsTicks = Mdkg.qpcToPtsTicks frequency firstQpc frame.Qpc

    mdkg.WriteTiming(
        { Qpc = frame.Qpc
          FrameIndex = frame.Index
          Fps = fps
          PtsTicks = ptsTicks
          DurationTicks = durationTicks
          GapFromPreviousTicks = 0L
          IsDropped = false
          DropCount = 0
          Notes = "" })

    mdkg.WriteOcrText(
        { Qpc = frame.Qpc
          FrameIndex = frame.Index
          PtsTicks = ptsTicks
          DurationTicks = durationTicks
          Ocr = frame.Ocr })

    mdkg.WriteUiAffordance(
        { Qpc = frame.Qpc
          FrameIndex = frame.Index
          PtsTicks = ptsTicks
          DurationTicks = durationTicks
          Hints = frame.Hints })

mdkg.Close()
```

---

## Conformance

A minimal `.mdkg` v1 reader MUST:

1. Validate magic bytes.
2. Reject unsupported major versions.
3. Confirm `movieTimescale == 10000000`.
4. Read the layer table.
5. Read the sample index.
6. Support seeking by:
   - layer id;
   - `ptsTicks`;
   - exact match or nearest sample at/before `ptsTicks`.
7. Decode the timing layer codec:
   - `application/vnd.yumlog.mdkg.timing+json;version=1`.

A conformant reader SHOULD:

1. Decode OCR, UI affordance, semantic graph, and input-event layers when their codecs are recognized.
2. Reconstruct semantic graph state by keyframe plus deltas.
3. Treat unknown layers as opaque byte payloads.
4. Preserve unknown layers during copy/remux operations.

Unknown layer rule:

```text
If layer id, schema URI, or codec is unknown:
  - do not fail;
  - keep its descriptor;
  - expose its sample entries and raw payload bytes;
  - skip typed deserialization.
```

Forward-compatibility rule:

```text
Minor version increments may add optional fields, flags, or layer codecs.
Major version increments may change required parsing behavior.
A v1 reader must reject majorVersion != 1.
```
