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
