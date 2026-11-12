namespace Yumlog.Native

open System

/// Backend-agnostic HEVC encoder abstraction consumed by the recorder.
///
/// Auto selection probes NVENC on the supplied ID3D11Device (opening a real encode
/// session and checking 4:4:4 + lossless caps) and falls back to the x265 software
/// path when NVENC is unusable. Both backends speak the shared Domain.fs contracts.
[<RequireQualifiedAccess>]
module HevcEncoder =

    type EncoderOptions =
        { Backend: HevcBackend
          Width: int
          Height: int
          Fps: int
          Lossless: bool
          Preset: string
          /// ID3D11Device* from the capture session. Required for NVENC.
          D3D11Device: nativeint
          /// Optional explicit path to the x265 shared library.
          X265Path: string }

    type IHevcEncoder =
        inherit IDisposable
        abstract member Backend: HevcBackend
        abstract member DecoderConfig: HevcDecoderConfig
        /// Human-readable statement of what losslessness was actually achieved.
        abstract member LosslessMode: string
        /// True only if the encode is provably bit-exact recoverable to the input BGRA.
        abstract member IsMathematicallyLossless: bool
        /// True when this encoder consumes CapturedFrame.Bgra rather than Texture.
        abstract member NeedsCpuFrames: bool
        abstract member Encode: CapturedFrame -> HevcAccessUnit list
        /// Drain: signal EOS and return all remaining access units.
        abstract member Finish: unit -> HevcAccessUnit list

    /// Opens a real NVENC session on the given ID3D11Device and verifies driver +
    /// caps. Returns false (never throws) when NVENC is unusable.
    let isNvencUsable (d3d11Device: nativeint) : bool =
        NvencHevc.isUsable d3d11Device

    /// Returns true when x265.dll can be loaded and bound. Never throws.
    let isX265Usable (x265Path: string) : bool =
        X265Encoder.isUsable (if isNull (box x265Path) then "" else x265Path)

    let private wrapNvenc (s: NvencHevc.Session) : IHevcEncoder =
        { new IHevcEncoder with
            member _.Backend = s.Backend
            member _.DecoderConfig = s.DecoderConfig
            member _.LosslessMode = s.LosslessMode
            member _.IsMathematicallyLossless = s.IsMathematicallyLossless
            member _.NeedsCpuFrames = s.NeedsCpuFrames
            member _.Encode frame = s.Encode frame
            member _.Finish() = s.Finish()
          interface IDisposable with
            member _.Dispose() = s.Dispose() }

    let private wrapX265 (s: X265Encoder.Session) : IHevcEncoder =
        { new IHevcEncoder with
            member _.Backend = s.Backend
            member _.DecoderConfig = s.DecoderConfig
            member _.LosslessMode = s.LosslessMode
            member _.IsMathematicallyLossless = s.IsMathematicallyLossless
            member _.NeedsCpuFrames = s.NeedsCpuFrames
            member _.Encode frame = s.Encode frame
            member _.Finish() = s.Finish()
          interface IDisposable with
            member _.Dispose() = s.Dispose() }

    let private createNvenc (o: EncoderOptions) : IHevcEncoder =
        wrapNvenc (NvencHevc.create o.D3D11Device o.Width o.Height o.Fps o.Lossless o.Preset)

    let private createX265 (o: EncoderOptions) : IHevcEncoder =
        wrapX265 (X265Encoder.create o.Width o.Height o.Fps o.Lossless o.Preset o.X265Path)

    /// Creates an encoder honouring the requested backend. Auto probes NVENC first,
    /// then falls back to x265, and raises a clear error if neither is usable.
    let create (options: EncoderOptions) : IHevcEncoder =
        match options.Backend with
        | Nvenc ->
            if options.D3D11Device = 0n then
                failwith "NVENC backend requires EncoderOptions.D3D11Device (an ID3D11Device*)."
            createNvenc options
        | X265 ->
            createX265 options
        | Auto ->
            if options.D3D11Device <> 0n && isNvencUsable options.D3D11Device then
                createNvenc options
            elif isX265Usable options.X265Path then
                createX265 options
            else
                let nvReason =
                    if options.D3D11Device = 0n then "no D3D11 device supplied"
                    else "NVENC session/caps probe failed"
                failwith
                    ($"No usable HEVC encoder: NVENC unavailable ({nvReason}) and x265.dll could not be loaded "
                     + "(set --x265, YUMLOG_X265_PATH, or place x265.dll at .tools\\x265\\x265.dll).")
