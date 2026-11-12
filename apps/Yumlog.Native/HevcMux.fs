namespace Yumlog.Native

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.IO
open System.Text

/// Native ISO Base Media File Format (MP4) writer for externally encoded HEVC,
/// with an optional timed-metadata track carrying `.mdkg` (MDS1) carrier records.
///
/// Design notes / red-team fixes baked in (see hevc-swarm-out/challenge/CHALLENGE-SYNTHESIS.md):
///   * Emits `stss` (sync-sample box) so HEVC seeking works.
///   * `hvcC` (HEVCDecoderConfigurationRecord, ISO/IEC 14496-15) is built from the REAL
///     VPS/SPS/PPS in DecoderConfig.VpsSpsPpsAnnexB, with a truthful profile_tier_level parsed
///     out of the SPS, and chroma_format_idc / bit depths taken from DecoderConfig.
///   * `hvc1` sample entry -> parameter sets MUST NOT be in-band. WriteAccessUnit strips
///     VPS(32)/SPS(33)/PPS(34)/AUD(35) NAL units; they live only in `hvcC`.
///   * Annex B is converted to 4-byte length-prefixed (AVCC-style) NAL units in `mdat`.
///     No start codes are written into the mp4.
///   * `mett` MIME is the real payload type `application/vnd.darbot.mdkg`.
///   * Samples are sorted by DTS at finalize; every `stts` duration is clamped >= 1.
///   * Track timescale is 10,000,000 (HevcTimebase). `ctts` (version 1, signed) is emitted
///     whenever any Pts <> Dts.
///   * Crash safety: `ftyp` + streamed `mdat`; a fixed-size `.mp4.recovery` journal is
///     appended per sample; `tryRecover` rebuilds a playable `moov` from an orphaned file.
///   * 64-bit `largesize` mdat; `co64` used automatically when any chunk offset exceeds 32 bits.
module HevcMux =

    // ---------------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------------

    /// MIME type advertised by the timed-metadata (`mett`) sample entry. The payload is a
    /// `.mdkg` MDS1 carrier record (Mdkg.Carrier.encode), NOT JSON.
    [<Literal>]
    let MetadataMimeFormat = "application/vnd.darbot.mdkg"

    type MuxOptions =
        { OutFile: string
          Width: int
          Height: int
          Fps: int
          DecoderConfig: HevcDecoderConfig
          /// When true, add a timed-metadata track carrying `.mdkg` payloads.
          WithMetadataTrack: bool }

    type IHevcMuxer =
        inherit System.IDisposable
        abstract member WriteAccessUnit: HevcAccessUnit -> unit
        abstract member WriteMetadataSample: Mdkg.MdkgCarrierSample -> unit
        abstract member VideoSampleCount: int
        abstract member MetadataSampleCount: int
        /// Finalizes moov and closes the file. Safe to call once; Dispose calls it if not called.
        abstract member Finish: unit -> unit

    // ---------------------------------------------------------------------------
    // Internal constants
    // ---------------------------------------------------------------------------

    /// Shared movie/media timescale: one tick == 100ns, matching HevcTimebase.TicksPerSecond.
    let private MediaTimescale = HevcTimebase.TicksPerSecond // 10_000_000L

    [<Literal>]
    let private RecoveryMagic = 0x4D444B4Au // "MDKJ"

    [<Literal>]
    let private RecoveryVersion = 1u

    /// Fixed-size per-sample recovery record: trackId(4) flags(4) pts(8) dts(8) offset(8) size(4) reserved(4) = 40 bytes.
    [<Literal>]
    let private RecoveryRecordSize = 40

    // ---------------------------------------------------------------------------
    // Big-endian writer
    // ---------------------------------------------------------------------------

    type private BeWriter(stream: Stream) =
        member _.Stream = stream
        member _.WriteUInt8(value: byte) = stream.WriteByte(value)

        member _.WriteZero(count: int) =
            if count > 0 then
                let bytes = Array.zeroCreate<byte> count
                stream.Write(bytes, 0, bytes.Length)

        member _.WriteBytes(bytes: byte array) =
            if not (isNullRef bytes) && bytes.Length > 0 then
                stream.Write(bytes, 0, bytes.Length)

        member _.WriteAscii(value: string) =
            let bytes = Encoding.ASCII.GetBytes(value)
            stream.Write(bytes, 0, bytes.Length)

        member _.WriteUtf8Z(value: string) =
            let bytes = Encoding.UTF8.GetBytes(value)
            stream.Write(bytes, 0, bytes.Length)
            stream.WriteByte(0uy)

        member _.WriteUInt16(value: uint16) =
            let bytes = Array.zeroCreate<byte> 2
            BinaryPrimitives.WriteUInt16BigEndian(Span<byte>(bytes), value)
            stream.Write(bytes, 0, bytes.Length)

        member _.WriteInt16(value: int16) =
            let bytes = Array.zeroCreate<byte> 2
            BinaryPrimitives.WriteInt16BigEndian(Span<byte>(bytes), value)
            stream.Write(bytes, 0, bytes.Length)

        member _.WriteUInt24(value: uint32) =
            stream.WriteByte(byte ((value >>> 16) &&& 0xFFu))
            stream.WriteByte(byte ((value >>> 8) &&& 0xFFu))
            stream.WriteByte(byte (value &&& 0xFFu))

        member _.WriteUInt32(value: uint32) =
            let bytes = Array.zeroCreate<byte> 4
            BinaryPrimitives.WriteUInt32BigEndian(Span<byte>(bytes), value)
            stream.Write(bytes, 0, bytes.Length)

        member _.WriteInt32(value: int32) =
            let bytes = Array.zeroCreate<byte> 4
            BinaryPrimitives.WriteInt32BigEndian(Span<byte>(bytes), value)
            stream.Write(bytes, 0, bytes.Length)

        member _.WriteUInt64(value: uint64) =
            let bytes = Array.zeroCreate<byte> 8
            BinaryPrimitives.WriteUInt64BigEndian(Span<byte>(bytes), value)
            stream.Write(bytes, 0, bytes.Length)

        member _.WriteInt64(value: int64) =
            let bytes = Array.zeroCreate<byte> 8
            BinaryPrimitives.WriteInt64BigEndian(Span<byte>(bytes), value)
            stream.Write(bytes, 0, bytes.Length)

    // ---------------------------------------------------------------------------
    // Box builders (in-memory; size back-patched)
    // ---------------------------------------------------------------------------

    let private makeBox (name: string) (writeBody: BeWriter -> unit) : byte array =
        use ms = new MemoryStream()
        let writer = BeWriter(ms)
        writer.WriteUInt32(0u) // size placeholder
        writer.WriteAscii(name)
        writeBody writer

        if ms.Length > int64 UInt32.MaxValue then
            failwithf "Box %s is too large for a 32-bit MP4 box size (%d bytes)." name ms.Length

        ms.Position <- 0L
        writer.WriteUInt32(uint32 ms.Length)
        ms.ToArray()

    let private makeFullBox (name: string) (version: byte) (flags: uint32) (writeBody: BeWriter -> unit) : byte array =
        makeBox name (fun writer ->
            writer.WriteUInt8(version)
            writer.WriteUInt24(flags)
            writeBody writer)

    // ---------------------------------------------------------------------------
    // Annex B / NAL utilities
    // ---------------------------------------------------------------------------

    let private startCodeLength (bytes: byte array) (offset: int) =
        if offset + 3 <= bytes.Length
           && bytes[offset] = 0uy && bytes[offset + 1] = 0uy && bytes[offset + 2] = 1uy then
            // Prefer 4-byte when preceded by a zero.
            if offset >= 1 && bytes[offset - 1] = 0uy then 3 else 3
        else
            0

    /// Detects a 3- or 4-byte start code at `offset` and returns its length (0 = none).
    let private startCodeAt (bytes: byte array) (offset: int) =
        if offset + 4 <= bytes.Length
           && bytes[offset] = 0uy && bytes[offset + 1] = 0uy && bytes[offset + 2] = 0uy && bytes[offset + 3] = 1uy then
            4
        elif offset + 3 <= bytes.Length
             && bytes[offset] = 0uy && bytes[offset + 1] = 0uy && bytes[offset + 2] = 1uy then
            3
        else
            0

    let private hasAnnexBStartCode (bytes: byte array) =
        bytes.Length >= 3 && startCodeAt bytes 0 <> 0

    let private findStartCode (bytes: byte array) (fromOffset: int) =
        let mutable found = -1
        let mutable i = fromOffset
        while found < 0 && i <= bytes.Length - 3 do
            if startCodeAt bytes i <> 0 then found <- i else i <- i + 1
        found

    /// Splits an Annex B blob into raw NAL units (start codes and trailing zero padding removed).
    let private splitAnnexB (bytes: byte array) : byte array array =
        let nals = ResizeArray<byte array>()
        let mutable start = findStartCode bytes 0
        while start >= 0 do
            let scLen = startCodeAt bytes start
            let nalStart = start + scLen
            let next = findStartCode bytes nalStart
            let nalEnd = if next >= 0 then next else bytes.Length
            let mutable trimmedEnd = nalEnd
            while trimmedEnd > nalStart && bytes[trimmedEnd - 1] = 0uy do
                trimmedEnd <- trimmedEnd - 1
            if trimmedEnd > nalStart then
                let nal = Array.zeroCreate<byte> (trimmedEnd - nalStart)
                Buffer.BlockCopy(bytes, nalStart, nal, 0, nal.Length)
                nals.Add(nal)
            start <- next
        nals.ToArray()

    /// Attempts to interpret a blob as already length-prefixed (4-byte big-endian) NAL units.
    let private trySplitLengthPrefixed (bytes: byte array) : byte array array option =
        let nals = ResizeArray<byte array>()
        let mutable offset = 0
        let mutable ok = bytes.Length >= 4
        while ok && offset < bytes.Length do
            if offset + 4 > bytes.Length then
                ok <- false
            else
                let length =
                    BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan<byte>(bytes, offset, 4)) |> int
                if length <= 0 || offset + 4 + length > bytes.Length then
                    ok <- false
                else
                    let nal = Array.zeroCreate<byte> length
                    Buffer.BlockCopy(bytes, offset + 4, nal, 0, length)
                    nals.Add(nal)
                    offset <- offset + 4 + length
        if ok && offset = bytes.Length && nals.Count > 0 then Some(nals.ToArray()) else None

    let private splitNalUnits (bytes: byte array) : byte array array =
        if isNullRef bytes || bytes.Length = 0 then Array.empty
        elif hasAnnexBStartCode bytes then splitAnnexB bytes
        else
            match trySplitLengthPrefixed bytes with
            | Some nals -> nals
            | None -> [| bytes |]

    /// HEVC nal_unit_type = (nal[0] >> 1) & 0x3F.
    let private nalUnitType (nal: byte array) =
        if nal.Length < 2 then -1 else int ((nal[0] >>> 1) &&& 0x3Fuy)

    let private isParameterSetOrAud t = t = 32 || t = 33 || t = 34 || t = 35

    /// Strip VPS/SPS/PPS/AUD, then emit remaining NAL units as 4-byte length-prefixed (AVCC).
    let private stripAndLengthPrefix (bytes: byte array) : byte array =
        let nals = splitNalUnits bytes
        use ms = new MemoryStream()
        let writer = BeWriter(ms)
        for nal in nals do
            let t = nalUnitType nal
            if nal.Length > 0 && not (isParameterSetOrAud t) then
                writer.WriteUInt32(uint32 nal.Length)
                writer.WriteBytes(nal)
        ms.ToArray()

    /// Removes HEVC emulation-prevention (0x000003 -> 0x0000) to recover the RBSP.
    let private toRbsp (nal: byte array) : byte array =
        let out = ResizeArray<byte>(nal.Length)
        let mutable zeros = 0
        for i in 0 .. nal.Length - 1 do
            let b = nal[i]
            if zeros >= 2 && b = 3uy then
                zeros <- 0 // drop emulation_prevention_three_byte
            else
                out.Add(b)
                if b = 0uy then zeros <- zeros + 1 else zeros <- 0
        out.ToArray()

    // ---------------------------------------------------------------------------
    // hvcC (HEVCDecoderConfigurationRecord) from real VPS/SPS/PPS
    // ---------------------------------------------------------------------------

    let private buildHvcc (config: HevcDecoderConfig) : byte array =
        let annexB =
            match config.VpsSpsPpsAnnexB with
            | Some b when not (isNullRef b) && b.Length > 0 -> b
            | _ ->
                failwith
                    "HEVC hvcC requires DecoderConfig.VpsSpsPpsAnnexB with VPS/SPS/PPS NAL units (4-byte start codes)."

        let nals = splitNalUnits annexB
        let byType t = nals |> Array.filter (fun n -> nalUnitType n = t)
        let vps = byType 32
        let sps = byType 33
        let pps = byType 34

        if Array.isEmpty vps || Array.isEmpty sps || Array.isEmpty pps then
            failwith "HEVC hvcC generation requires VPS, SPS, and PPS NAL units in DecoderConfig.VpsSpsPpsAnnexB."

        // Parse the SPS profile_tier_level truthfully.
        // De-emulated SPS layout:
        //   [0..1] NAL header
        //   [2]    sps_video_parameter_set_id(4) | sps_max_sub_layers_minus1(3) | sps_temporal_id_nesting_flag(1)
        //   [3]    general_profile_space(2)|general_tier_flag(1)|general_profile_idc(5)
        //   [4..7] general_profile_compatibility_flags (32 bits)
        //   [8..13] general constraint indicator flags (48 bits)
        //   [14]   general_level_idc
        let spsRbsp = toRbsp sps[0]

        if spsRbsp.Length < 15 then
            failwith "HEVC SPS is too short to contain a profile_tier_level."

        let profileTierLevelByte = spsRbsp[3]
        let profileCompatibility = spsRbsp[4..7]
        let constraintFlags = spsRbsp[8..13]
        let levelIdc = spsRbsp[14]

        let maxSubLayersMinus1 = int ((spsRbsp[2] >>> 1) &&& 0x07uy)
        let numTemporalLayers = maxSubLayersMinus1 + 1
        let temporalIdNested = if (spsRbsp[2] &&& 0x01uy) <> 0uy then 1 else 0

        let chromaIdc = HevcChromaFormat.toIdc config.Chroma
        let bitDepthMinus8 =
            let bd = config.BitDepth
            if bd < 8 then 0 elif bd > 15 then 7 else bd - 8

        use ms = new MemoryStream()
        let writer = BeWriter(ms)

        writer.WriteUInt8(1uy) // configurationVersion
        writer.WriteUInt8(profileTierLevelByte) // general_profile_space/tier/profile_idc
        writer.WriteBytes(profileCompatibility) // 4 bytes
        writer.WriteBytes(constraintFlags) // 6 bytes
        writer.WriteUInt8(levelIdc) // general_level_idc

        writer.WriteUInt16(0xF000us) // '1111' + min_spatial_segmentation_idc(12) unknown
        writer.WriteUInt8(0xFCuy) // '111111' + parallelismType(2) unknown
        writer.WriteUInt8(0xFCuy ||| (chromaIdc &&& 0x03uy)) // '111111' + chroma_format_idc
        writer.WriteUInt8(0xF8uy ||| (byte bitDepthMinus8 &&& 0x07uy)) // '11111' + bit_depth_luma_minus8
        writer.WriteUInt8(0xF8uy ||| (byte bitDepthMinus8 &&& 0x07uy)) // '11111' + bit_depth_chroma_minus8
        writer.WriteUInt16(0us) // avgFrameRate (0 = unspecified)

        // constantFrameRate(2)=0 | numTemporalLayers(3) | temporalIdNested(1) | lengthSizeMinusOne(2)=3
        let packed =
            byte (((0 &&& 0x03) <<< 6)
                  ||| ((numTemporalLayers &&& 0x07) <<< 3)
                  ||| ((temporalIdNested &&& 0x01) <<< 2)
                  ||| 3)
        writer.WriteUInt8(packed)
        writer.WriteUInt8(3uy) // numOfArrays: VPS, SPS, PPS

        let writeArray (nalType: int) (items: byte array array) =
            writer.WriteUInt8(0x80uy ||| byte nalType) // array_completeness(1) + reserved(1) + NAL_unit_type(6)
            writer.WriteUInt16(uint16 items.Length)
            for nal in items do
                writer.WriteUInt16(uint16 nal.Length)
                writer.WriteBytes(nal)

        writeArray 32 vps
        writeArray 33 sps
        writeArray 34 pps
        ms.ToArray()

    // ---------------------------------------------------------------------------
    // Sample models
    // ---------------------------------------------------------------------------

    type private VideoSample =
        { Offset: uint64
          Size: uint32
          Pts: int64
          Dts: int64
          IsKey: bool }

    type private MetaSample =
        { Offset: uint64
          Size: uint32
          Pts: int64
          DurationTicks: int64 }

    type private TrackKind =
        | Video
        | Metadata

    let private frameDurationTicks fps =
        if fps <= 0 then invalidArg (nameof fps) "fps must be positive."
        int64 (Math.Round(float MediaTimescale / float fps))

    let private clampDurationU32 (value: int64) =
        if value <= 0L then 1u
        elif value > int64 UInt32.MaxValue then UInt32.MaxValue
        else uint32 value

    // ---------------------------------------------------------------------------
    // Sample-table boxes
    // ---------------------------------------------------------------------------

    /// stts from an array of per-sample durations (already clamped >= 1).
    let private makeStts (durations: uint32 array) =
        makeFullBox "stts" 0uy 0u (fun writer ->
            let entries = ResizeArray<uint32 * uint32>()
            for d in durations do
                if entries.Count = 0 then
                    entries.Add(1u, d)
                else
                    let idx = entries.Count - 1
                    let count, dur = entries[idx]
                    if dur = d then entries[idx] <- (count + 1u, dur)
                    else entries.Add(1u, d)
            writer.WriteUInt32(uint32 entries.Count)
            for count, dur in entries do
                writer.WriteUInt32(count)
                writer.WriteUInt32(dur))

    /// ctts version 1 (signed 32-bit composition offsets). offsets.Length == sample count.
    let private makeCtts (offsets: int array) =
        makeFullBox "ctts" 1uy 0u (fun writer ->
            let entries = ResizeArray<uint32 * int>()
            for o in offsets do
                if entries.Count = 0 then
                    entries.Add(1u, o)
                else
                    let idx = entries.Count - 1
                    let count, off = entries[idx]
                    if off = o then entries[idx] <- (count + 1u, off)
                    else entries.Add(1u, o)
            writer.WriteUInt32(uint32 entries.Count)
            for count, off in entries do
                writer.WriteUInt32(count)
                writer.WriteInt32(off))

    /// stss listing 1-based sample numbers of sync samples.
    let private makeStss (syncSampleNumbers: int array) =
        makeFullBox "stss" 0uy 0u (fun writer ->
            writer.WriteUInt32(uint32 syncSampleNumbers.Length)
            for n in syncSampleNumbers do
                writer.WriteUInt32(uint32 n))

    let private makeStsc () =
        // One sample per chunk keeps the mapping trivial and correct.
        makeFullBox "stsc" 0uy 0u (fun writer ->
            writer.WriteUInt32(1u)
            writer.WriteUInt32(1u) // first_chunk
            writer.WriteUInt32(1u) // samples_per_chunk
            writer.WriteUInt32(1u)) // sample_description_index

    let private makeStsz (sizes: uint32 array) =
        makeFullBox "stsz" 0uy 0u (fun writer ->
            writer.WriteUInt32(0u) // sample_size = 0 -> variable
            writer.WriteUInt32(uint32 sizes.Length)
            for s in sizes do
                writer.WriteUInt32(s))

    /// stco (32-bit) or co64 (64-bit) depending on the largest chunk offset.
    let private makeChunkOffsets (offsets: uint64 array) =
        let needs64 = offsets |> Array.exists (fun o -> o > uint64 UInt32.MaxValue)
        if needs64 then
            makeFullBox "co64" 0uy 0u (fun writer ->
                writer.WriteUInt32(uint32 offsets.Length)
                for o in offsets do
                    writer.WriteUInt64(o))
        else
            makeFullBox "stco" 0uy 0u (fun writer ->
                writer.WriteUInt32(uint32 offsets.Length)
                for o in offsets do
                    writer.WriteUInt32(uint32 o))

    // ---------------------------------------------------------------------------
    // Sample entries
    // ---------------------------------------------------------------------------

    let private makeHvc1SampleEntry width height (hvcc: byte array) =
        makeBox "hvc1" (fun writer ->
            writer.WriteZero(6) // reserved
            writer.WriteUInt16(1us) // data_reference_index

            writer.WriteUInt16(0us) // pre_defined
            writer.WriteUInt16(0us) // reserved
            writer.WriteUInt32(0u) // pre_defined
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0u)

            writer.WriteUInt16(uint16 width)
            writer.WriteUInt16(uint16 height)
            writer.WriteUInt32(0x00480000u) // horizresolution 72 dpi
            writer.WriteUInt32(0x00480000u) // vertresolution 72 dpi
            writer.WriteUInt32(0u) // reserved
            writer.WriteUInt16(1us) // frame_count

            let compressor = Array.zeroCreate<byte> 32
            let name = Encoding.ASCII.GetBytes("Yumlog HEVC")
            let len = min 31 name.Length
            compressor[0] <- byte len
            Buffer.BlockCopy(name, 0, compressor, 1, len)
            writer.WriteBytes(compressor)

            writer.WriteUInt16(0x0018us) // depth
            writer.WriteInt16(-1s) // pre_defined

            writer.WriteBytes(makeBox "hvcC" (fun b -> b.WriteBytes(hvcc))))

    let private makeMettSampleEntry () =
        makeBox "mett" (fun writer ->
            writer.WriteZero(6) // reserved
            writer.WriteUInt16(1us) // data_reference_index
            writer.WriteUtf8Z("") // content_encoding (empty = identity)
            writer.WriteUtf8Z(MetadataMimeFormat)) // mime_format

    // ---------------------------------------------------------------------------
    // moov assembly
    // ---------------------------------------------------------------------------

    let private makeMvhd (duration: int64) (nextTrackId: uint32) =
        makeFullBox "mvhd" 1uy 0u (fun writer ->
            writer.WriteUInt64(0UL) // creation_time
            writer.WriteUInt64(0UL) // modification_time
            writer.WriteUInt32(uint32 MediaTimescale)
            writer.WriteUInt64(uint64 (max 0L duration))
            writer.WriteUInt32(0x00010000u) // rate 1.0
            writer.WriteUInt16(0x0100us) // volume 1.0
            writer.WriteUInt16(0us) // reserved
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0u)
            // unity matrix
            writer.WriteUInt32(0x00010000u)
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0x00010000u)
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0x40000000u)
            for _ in 1 .. 6 do
                writer.WriteUInt32(0u) // pre_defined
            writer.WriteUInt32(nextTrackId))

    let private makeTkhd (trackId: int) (duration: int64) width height kind =
        makeFullBox "tkhd" 1uy 0x000007u (fun writer -> // enabled | in_movie | in_preview
            writer.WriteUInt64(0UL)
            writer.WriteUInt64(0UL)
            writer.WriteUInt32(uint32 trackId)
            writer.WriteUInt32(0u) // reserved
            writer.WriteUInt64(uint64 (max 0L duration))
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0u)
            writer.WriteUInt16(0us) // layer
            writer.WriteUInt16(0us) // alternate_group
            writer.WriteUInt16(0us) // volume
            writer.WriteUInt16(0us) // reserved
            // unity matrix
            writer.WriteUInt32(0x00010000u)
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0x00010000u)
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0x40000000u)
            match kind with
            | Video ->
                writer.WriteUInt32(uint32 width <<< 16)
                writer.WriteUInt32(uint32 height <<< 16)
            | Metadata ->
                writer.WriteUInt32(0u)
                writer.WriteUInt32(0u))

    let private makeMdhd (duration: int64) =
        makeFullBox "mdhd" 1uy 0u (fun writer ->
            writer.WriteUInt64(0UL)
            writer.WriteUInt64(0UL)
            writer.WriteUInt32(uint32 MediaTimescale)
            writer.WriteUInt64(uint64 (max 0L duration))
            writer.WriteUInt16(0x55C4us) // language 'und'
            writer.WriteUInt16(0us))

    let private makeHdlr (handlerType: string) (name: string) =
        makeFullBox "hdlr" 0uy 0u (fun writer ->
            writer.WriteUInt32(0u) // pre_defined
            writer.WriteAscii(handlerType)
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0u)
            writer.WriteUInt32(0u)
            writer.WriteUtf8Z(name))

    let private makeDinf () =
        makeBox "dinf" (fun writer ->
            writer.WriteBytes(
                makeFullBox "dref" 0uy 0u (fun dref ->
                    dref.WriteUInt32(1u)
                    dref.WriteBytes(makeFullBox "url " 0uy 1u ignore)))) // flag 1 = self-contained

    let private makeVmhd () =
        makeFullBox "vmhd" 0uy 1u (fun writer ->
            writer.WriteUInt16(0us) // graphicsmode
            writer.WriteUInt16(0us)
            writer.WriteUInt16(0us)
            writer.WriteUInt16(0us))

    let private makeNmhd () = makeFullBox "nmhd" 0uy 0u ignore

    // ---------------------------------------------------------------------------
    // Muxer implementation
    // ---------------------------------------------------------------------------

    type private Muxer(options: MuxOptions) =
        let frameDuration = frameDurationTicks options.Fps
        let hvcc = buildHvcc options.DecoderConfig
        let filePath = Path.GetFullPath(options.OutFile)
        let journalPath = filePath + ".recovery"

        do
            match Path.GetDirectoryName(filePath) with
            | null | "" -> ()
            | parent -> Directory.CreateDirectory(parent) |> ignore

        let stream =
            new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read)

        let journal =
            new FileStream(journalPath, FileMode.Create, FileAccess.Write, FileShare.Read)

        let videoSamples = ResizeArray<VideoSample>()
        let metaSamples = ResizeArray<MetaSample>()
        let mutable closed = false

        // ftyp + streamed largesize mdat.
        let mdatStart =
            let ftyp =
                makeBox "ftyp" (fun w ->
                    w.WriteAscii("isom")
                    w.WriteUInt32(0x00000200u)
                    w.WriteAscii("isom")
                    w.WriteAscii("iso6")
                    w.WriteAscii("mp41")
                    w.WriteAscii("hvc1"))
            stream.Write(ftyp, 0, ftyp.Length)
            let start = stream.Position
            let w = BeWriter(stream)
            w.WriteUInt32(1u) // size = 1 -> use largesize
            w.WriteAscii("mdat")
            w.WriteUInt64(0UL) // largesize placeholder (back-patched at Finish)
            start

        // Recovery journal header.
        do
            let w = BeWriter(journal)
            w.WriteUInt32(RecoveryMagic)
            w.WriteUInt32(RecoveryVersion)
            w.WriteInt32(options.Width)
            w.WriteInt32(options.Height)
            w.WriteInt32(options.Fps)
            w.WriteUInt32(if options.WithMetadataTrack then 1u else 0u)
            w.WriteUInt64(uint64 mdatStart)
            w.WriteUInt32(uint32 hvcc.Length)
            w.WriteBytes(hvcc)
            journal.Flush()

        let appendJournalRecord (trackId: uint32) (flags: uint32) (pts: int64) (dts: int64) (offset: uint64) (size: uint32) =
            let w = BeWriter(journal)
            w.WriteUInt32(trackId)
            w.WriteUInt32(flags)
            w.WriteInt64(pts)
            w.WriteInt64(dts)
            w.WriteUInt64(offset)
            w.WriteUInt32(size)
            w.WriteUInt32(0u) // reserved
            journal.Flush()

        member _.VideoSampleCount = videoSamples.Count
        member _.MetadataSampleCount = metaSamples.Count

        member _.WriteAccessUnit(au: HevcAccessUnit) =
            if closed then invalidOp "Cannot write after Finish()."
            if isNullRef au.Data || au.Data.Length = 0 then
                invalidArg (nameof au) "Access unit Data must not be empty."

            let payload = stripAndLengthPrefix au.Data
            if payload.Length = 0 then
                invalidArg (nameof au) "Access unit contained only parameter sets/AUD; no coded data to write."

            let offset = uint64 stream.Position
            stream.Write(payload, 0, payload.Length)
            stream.Flush()

            videoSamples.Add(
                { Offset = offset
                  Size = uint32 payload.Length
                  Pts = au.Pts
                  Dts = au.Dts
                  IsKey = au.IsKeyFrame })

            appendJournalRecord 1u (if au.IsKeyFrame then 1u else 0u) au.Pts au.Dts offset (uint32 payload.Length)

        member _.WriteMetadataSample(sample: Mdkg.MdkgCarrierSample) =
            if closed then invalidOp "Cannot write after Finish()."
            if not options.WithMetadataTrack then
                invalidOp "Metadata track is disabled (MuxOptions.WithMetadataTrack = false)."

            let payload = Mdkg.Carrier.encode sample
            let offset = uint64 stream.Position
            stream.Write(payload, 0, payload.Length)
            stream.Flush()

            metaSamples.Add(
                { Offset = offset
                  Size = uint32 payload.Length
                  Pts = sample.PtsTicks
                  DurationTicks = sample.DurationTicks })

            appendJournalRecord 2u sample.Flags sample.PtsTicks sample.PtsTicks offset (uint32 payload.Length)

        member private _.BuildVideoTrak(trackId: int, baseline: int64) =
            // Decode order.
            let ordered = videoSamples |> Seq.sortBy (fun s -> s.Dts) |> Seq.toArray
            let n = ordered.Length

            let durations =
                [| for i in 0 .. n - 1 ->
                       let d = if i + 1 < n then ordered[i + 1].Dts - ordered[i].Dts else frameDuration
                       clampDurationU32 d |]

            let cttsOffsets =
                [| for s in ordered -> int (s.Pts - s.Dts) |]

            let sizes = ordered |> Array.map (fun s -> s.Size)
            let offsets = ordered |> Array.map (fun s -> s.Offset)

            let syncNumbers =
                [| for i in 0 .. n - 1 do
                       if ordered[i].IsKey then yield i + 1 |]

            let mediaDuration = durations |> Array.sumBy int64
            let needCtts = cttsOffsets |> Array.exists (fun o -> o <> 0)
            let allSync = syncNumbers.Length = n

            let stbl =
                makeBox "stbl" (fun w ->
                    w.WriteBytes(makeFullBox "stsd" 0uy 0u (fun s ->
                        s.WriteUInt32(1u)
                        s.WriteBytes(makeHvc1SampleEntry options.Width options.Height hvcc)))
                    w.WriteBytes(makeStts durations)
                    if needCtts then w.WriteBytes(makeCtts cttsOffsets)
                    // Emit stss unless EVERY sample is a sync sample.
                    if not allSync then w.WriteBytes(makeStss syncNumbers)
                    w.WriteBytes(makeStsc ())
                    w.WriteBytes(makeStsz sizes)
                    w.WriteBytes(makeChunkOffsets offsets))

            let minf =
                makeBox "minf" (fun w ->
                    w.WriteBytes(makeVmhd ())
                    w.WriteBytes(makeDinf ())
                    w.WriteBytes(stbl))

            let mdia =
                makeBox "mdia" (fun w ->
                    w.WriteBytes(makeMdhd mediaDuration)
                    w.WriteBytes(makeHdlr "vide" "Yumlog HEVC Video")
                    w.WriteBytes(minf))

            let trak =
                makeBox "trak" (fun w ->
                    w.WriteBytes(makeTkhd trackId mediaDuration options.Width options.Height Video)
                    w.WriteBytes(mdia))

            ignore baseline
            trak, mediaDuration

        member private _.BuildMetaTrak(trackId: int) =
            let ordered = metaSamples |> Seq.sortBy (fun s -> s.Pts) |> Seq.toArray
            let n = ordered.Length

            let durations =
                [| for i in 0 .. n - 1 ->
                       let d =
                           if i + 1 < n then ordered[i + 1].Pts - ordered[i].Pts
                           elif ordered[i].DurationTicks > 0L then ordered[i].DurationTicks
                           else frameDuration
                       clampDurationU32 d |]

            let sizes = ordered |> Array.map (fun s -> s.Size)
            let offsets = ordered |> Array.map (fun s -> s.Offset)
            let mediaDuration = durations |> Array.sumBy int64

            let stbl =
                makeBox "stbl" (fun w ->
                    w.WriteBytes(makeFullBox "stsd" 0uy 0u (fun s ->
                        s.WriteUInt32(1u)
                        s.WriteBytes(makeMettSampleEntry ())))
                    w.WriteBytes(makeStts durations)
                    w.WriteBytes(makeStsc ())
                    w.WriteBytes(makeStsz sizes)
                    w.WriteBytes(makeChunkOffsets offsets))

            let minf =
                makeBox "minf" (fun w ->
                    w.WriteBytes(makeNmhd ())
                    w.WriteBytes(makeDinf ())
                    w.WriteBytes(stbl))

            let mdia =
                makeBox "mdia" (fun w ->
                    w.WriteBytes(makeMdhd mediaDuration)
                    w.WriteBytes(makeHdlr "meta" "Yumlog Timed Metadata")
                    w.WriteBytes(minf))

            let trak =
                makeBox "trak" (fun w ->
                    w.WriteBytes(makeTkhd trackId mediaDuration 0 0 Metadata)
                    w.WriteBytes(mdia))

            trak, mediaDuration

        member this.Finish() =
            if not closed then
                closed <- true

                if videoSamples.Count = 0 then
                    invalidOp "Cannot finalize an HEVC file with no video samples."

                // Common timeline baseline so both tracks share a t=0 origin (preserves A/V sync).
                let vMin = videoSamples |> Seq.map (fun s -> s.Dts) |> Seq.min
                let mMin =
                    if metaSamples.Count > 0 then metaSamples |> Seq.map (fun s -> s.Pts) |> Seq.min
                    else vMin
                let baseline = min vMin mMin

                let videoTrak, videoDuration = this.BuildVideoTrak(1, baseline)

                let metaTrakOpt =
                    if options.WithMetadataTrack && metaSamples.Count > 0 then
                        Some(this.BuildMetaTrak(2))
                    else
                        None

                let movieDuration =
                    match metaTrakOpt with
                    | Some(_, md) -> max videoDuration md
                    | None -> videoDuration

                let nextTrackId = match metaTrakOpt with Some _ -> 3u | None -> 2u

                let moov =
                    makeBox "moov" (fun w ->
                        w.WriteBytes(makeMvhd movieDuration nextTrackId)
                        w.WriteBytes(videoTrak)
                        match metaTrakOpt with
                        | Some(metaTrak, _) -> w.WriteBytes(metaTrak)
                        | None -> ())

                // Back-patch the 64-bit mdat largesize.
                let mdatEnd = stream.Position
                let mdatSize = uint64 (mdatEnd - mdatStart)
                stream.Position <- mdatStart
                let patch = BeWriter(stream)
                patch.WriteUInt32(1u)
                patch.WriteAscii("mdat")
                patch.WriteUInt64(mdatSize)

                stream.Position <- mdatEnd
                stream.Write(moov, 0, moov.Length)
                stream.Flush()
                stream.Dispose()

                journal.Dispose()
                try
                    if File.Exists(journalPath) then File.Delete(journalPath)
                with _ -> ()

        interface IHevcMuxer with
            member this.WriteAccessUnit(au) = this.WriteAccessUnit(au)
            member this.WriteMetadataSample(s) = this.WriteMetadataSample(s)
            member this.VideoSampleCount = this.VideoSampleCount
            member this.MetadataSampleCount = this.MetadataSampleCount
            member this.Finish() = this.Finish()
            member this.Dispose() = this.Finish()

    /// Creates a streaming HEVC ISOBMFF muxer.
    let create (options: MuxOptions) : IHevcMuxer =
        new Muxer(options) :> IHevcMuxer

    // ---------------------------------------------------------------------------
    // Crash recovery
    // ---------------------------------------------------------------------------

    type private RecoveryRecord =
        { TrackId: uint32
          Flags: uint32
          Pts: int64
          Dts: int64
          Offset: uint64
          Size: uint32 }

    /// Rebuilds a playable `moov` for an orphaned `.mp4` using its `.mp4.recovery` journal.
    /// Returns true if recovery succeeded (and the journal was consumed), false if there was
    /// nothing to recover.
    let tryRecover (mp4Path: string) : bool =
        let filePath = Path.GetFullPath(mp4Path)
        let journalPath = filePath + ".recovery"

        if not (File.Exists(journalPath)) || not (File.Exists(filePath)) then
            false
        else
            let journalBytes = File.ReadAllBytes(journalPath)
            use jms = new MemoryStream(journalBytes)

            let readU32 () =
                let b = Array.zeroCreate<byte> 4
                if jms.Read(b, 0, 4) <> 4 then failwith "Truncated recovery journal."
                BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan<byte>(b))
            let readI32 () = int (readU32 ())
            let readU64 () =
                let b = Array.zeroCreate<byte> 8
                if jms.Read(b, 0, 8) <> 8 then failwith "Truncated recovery journal."
                BinaryPrimitives.ReadUInt64BigEndian(ReadOnlySpan<byte>(b))
            let readI64 () = int64 (readU64 ())

            let magic = readU32 ()
            if magic <> RecoveryMagic then
                false
            else
                let _version = readU32 ()
                let width = readI32 ()
                let height = readI32 ()
                let fps = readI32 ()
                let withMeta = readU32 () <> 0u
                let mdatStart = readU64 ()
                let hvccLen = int (readU32 ())
                let hvcc = Array.zeroCreate<byte> hvccLen
                if jms.Read(hvcc, 0, hvccLen) <> hvccLen then failwith "Truncated recovery journal hvcC."

                let records = ResizeArray<RecoveryRecord>()
                let recordBytes = int64 RecoveryRecordSize
                while jms.Length - jms.Position >= recordBytes do
                    let trackId = readU32 ()
                    let flags = readU32 ()
                    let pts = readI64 ()
                    let dts = readI64 ()
                    let offset = readU64 ()
                    let size = readU32 ()
                    let _reserved = readU32 ()
                    records.Add({ TrackId = trackId; Flags = flags; Pts = pts; Dts = dts; Offset = offset; Size = size })

                let videoRecs = records |> Seq.filter (fun r -> r.TrackId = 1u) |> Seq.toArray
                if videoRecs.Length = 0 then
                    false
                else
                    let frameDuration = frameDurationTicks fps

                    let videoSamples =
                        videoRecs
                        |> Array.map (fun r ->
                            { Offset = r.Offset
                              Size = r.Size
                              Pts = r.Pts
                              Dts = r.Dts
                              IsKey = (r.Flags &&& 1u) <> 0u })

                    let metaSamples =
                        records
                        |> Seq.filter (fun r -> r.TrackId = 2u)
                        |> Seq.map (fun r ->
                            { Offset = r.Offset
                              Size = r.Size
                              Pts = r.Pts
                              DurationTicks = frameDuration })
                        |> Seq.toArray

                    // Sample-table builders (mirror the live Muxer, standalone).
                    let buildVideoTrak () =
                        let ordered = videoSamples |> Array.sortBy (fun s -> s.Dts)
                        let n = ordered.Length
                        let durations =
                            [| for i in 0 .. n - 1 ->
                                   let d = if i + 1 < n then ordered[i + 1].Dts - ordered[i].Dts else frameDuration
                                   clampDurationU32 d |]
                        let cttsOffsets = [| for s in ordered -> int (s.Pts - s.Dts) |]
                        let sizes = ordered |> Array.map (fun s -> s.Size)
                        let offsets = ordered |> Array.map (fun s -> s.Offset)
                        let syncNumbers = [| for i in 0 .. n - 1 do if ordered[i].IsKey then yield i + 1 |]
                        let mediaDuration = durations |> Array.sumBy int64
                        let needCtts = cttsOffsets |> Array.exists (fun o -> o <> 0)
                        let allSync = syncNumbers.Length = n
                        let stbl =
                            makeBox "stbl" (fun w ->
                                w.WriteBytes(makeFullBox "stsd" 0uy 0u (fun s ->
                                    s.WriteUInt32(1u)
                                    s.WriteBytes(makeHvc1SampleEntry width height hvcc)))
                                w.WriteBytes(makeStts durations)
                                if needCtts then w.WriteBytes(makeCtts cttsOffsets)
                                if not allSync then w.WriteBytes(makeStss syncNumbers)
                                w.WriteBytes(makeStsc ())
                                w.WriteBytes(makeStsz sizes)
                                w.WriteBytes(makeChunkOffsets offsets))
                        let minf = makeBox "minf" (fun w -> w.WriteBytes(makeVmhd ()); w.WriteBytes(makeDinf ()); w.WriteBytes(stbl))
                        let mdia =
                            makeBox "mdia" (fun w ->
                                w.WriteBytes(makeMdhd mediaDuration)
                                w.WriteBytes(makeHdlr "vide" "Yumlog HEVC Video")
                                w.WriteBytes(minf))
                        let trak =
                            makeBox "trak" (fun w ->
                                w.WriteBytes(makeTkhd 1 mediaDuration width height Video)
                                w.WriteBytes(mdia))
                        trak, mediaDuration

                    let buildMetaTrak () =
                        let ordered = metaSamples |> Array.sortBy (fun s -> s.Pts)
                        let n = ordered.Length
                        let durations =
                            [| for i in 0 .. n - 1 ->
                                   let d = if i + 1 < n then ordered[i + 1].Pts - ordered[i].Pts else frameDuration
                                   clampDurationU32 d |]
                        let sizes = ordered |> Array.map (fun s -> s.Size)
                        let offsets = ordered |> Array.map (fun s -> s.Offset)
                        let mediaDuration = durations |> Array.sumBy int64
                        let stbl =
                            makeBox "stbl" (fun w ->
                                w.WriteBytes(makeFullBox "stsd" 0uy 0u (fun s ->
                                    s.WriteUInt32(1u)
                                    s.WriteBytes(makeMettSampleEntry ())))
                                w.WriteBytes(makeStts durations)
                                w.WriteBytes(makeStsc ())
                                w.WriteBytes(makeStsz sizes)
                                w.WriteBytes(makeChunkOffsets offsets))
                        let minf = makeBox "minf" (fun w -> w.WriteBytes(makeNmhd ()); w.WriteBytes(makeDinf ()); w.WriteBytes(stbl))
                        let mdia =
                            makeBox "mdia" (fun w ->
                                w.WriteBytes(makeMdhd mediaDuration)
                                w.WriteBytes(makeHdlr "meta" "Yumlog Timed Metadata")
                                w.WriteBytes(minf))
                        makeBox "trak" (fun w ->
                            w.WriteBytes(makeTkhd 2 mediaDuration 0 0 Metadata)
                            w.WriteBytes(mdia)), mediaDuration

                    let videoTrak, videoDuration = buildVideoTrak ()
                    let metaTrakOpt =
                        if withMeta && metaSamples.Length > 0 then Some(buildMetaTrak ()) else None
                    let movieDuration =
                        match metaTrakOpt with Some(_, md) -> max videoDuration md | None -> videoDuration
                    let nextTrackId = match metaTrakOpt with Some _ -> 3u | None -> 2u

                    let moov =
                        makeBox "moov" (fun w ->
                            w.WriteBytes(makeMvhd movieDuration nextTrackId)
                            w.WriteBytes(videoTrak)
                            match metaTrakOpt with
                            | Some(metaTrak, _) -> w.WriteBytes(metaTrak)
                            | None -> ())

                    // mdat data ends after the last journaled sample; discard any partial bytes past it.
                    let mdatEnd =
                        videoRecs
                        |> Array.append (records |> Seq.filter (fun r -> r.TrackId = 2u) |> Seq.toArray)
                        |> Array.map (fun r -> r.Offset + uint64 r.Size)
                        |> Array.append [| mdatStart + 16UL |]
                        |> Array.max

                    use fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                    fs.SetLength(int64 mdatEnd)

                    // Back-patch the mdat largesize.
                    fs.Position <- int64 mdatStart
                    let patch = BeWriter(fs)
                    patch.WriteUInt32(1u)
                    patch.WriteAscii("mdat")
                    patch.WriteUInt64(mdatEnd - mdatStart)

                    fs.Position <- int64 mdatEnd
                    fs.Write(moov, 0, moov.Length)
                    fs.Flush()
                    fs.Dispose()

                    try File.Delete(journalPath) with _ -> ()
                    true
