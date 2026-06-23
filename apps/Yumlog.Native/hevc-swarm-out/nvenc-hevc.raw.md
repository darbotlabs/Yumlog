Agent is idle (waiting for messages). agent_id: nvenc-hevc-1, agent_type: dayour-swe, status: idle, description: NVENC HEVC lossless interop, elapsed: 401s, total_turns: 1

[Turn 0]
## Summary

Below is a concrete `NvencHevc.fs` module that follows the raw P/Invoke/manual function-pointer dispatch style used in `RawWinRt.fs`. It loads `nvEncodeAPI64.dll`, creates the `NV_ENCODE_API_FUNCTION_LIST`, opens a D3D11 NVENC session, registers D3D11 textures, maps them, encodes HEVC, locks/unlocks the bitstream, emits chunks with QPC timestamps, flushes, and disposes native resources.

## Findings

1. `[HIGH]` The code path below configures NVENC for HEVC lossless coding: HEVC codec GUID, HEVC FRExt profile for 4:4:4, lossless tuning, CONSTQP with QP 0, no B-frames, and 4:4:4 chroma signaling.
2. `[HIGH]` For a mathematically lossless desktop recorder, the input samples must be encoded without lossy RGB-to-4:2:0 conversion. WGC normally gives `DXGI_FORMAT_B8G8R8A8_UNORM` textures; registering those as `NV_ENC_BUFFER_FORMAT_ARGB` keeps the frame GPU-resident, but NVENC’s internal RGB-to-YUV conversion is not guaranteed to be bit-exact reversible for the original RGB desktop. True RGB desktop losslessness requires an upstream GPU shader path that converts/copies into a supported 4:4:4 input representation, preferably `AYUV`/`YUV444`, with a reversible/full-range transform policy documented and validated.
3. `[MEDIUM]` This hardcodes Video Codec SDK 13.1-style struct layouts and version macros. Driver 595.79 is new enough for the API level and Turing HEVC NVENC features, but struct version mismatches will return `NV_ENC_ERR_INVALID_VERSION`; this is why `isNvencAvailable` checks `NvEncodeAPIGetMaxSupportedVersion`.
4. `[MEDIUM]` `isNvencAvailable : unit -> bool` can only validate DLL presence and API version because session-open needs an actual `ID3D11Device*`. The `create` function performs the real session gate and throws if session-open or initialization fails, so the caller can fall back to x265.

## Recommendations

1. Owner: capture agent. Ensure the WGC capture path either supplies textures in a validated 4:4:4 NVENC-compatible input format or explicitly accepts that ARGB input is lossless only after NVENC’s internal color conversion, not necessarily byte-identical to original RGB.
2. Owner: encoder-abstraction agent. Use `NvencHevc.isNvencAvailable()` as a cheap first gate, then wrap `NvencHevc.create` in `try/with` to fall back to x265 on session-open or init failure.
3. Owner: muxer agent. Consume `NvencPacket` from `FrameEncoded` or pair the `encodeFrame` return value with the input `qpcTime`. The bytes returned are HEVC elementary-stream chunks ready for Annex B-aware muxing.

## Code Snippets

### `NvencHevc.fs`

```fsharp
namespace Yumlog.Native

open System
open System.Collections.Generic
open System.Runtime.InteropServices

module NvencHevc =

    // This file intentionally mirrors RawWinRt.fs:
    // - raw P/Invoke
    // - explicit unmanaged structs
    // - function pointers dispatched with Marshal.GetDelegateForFunctionPointer
    // - StdCall delegates with PreserveSig-style status returns

    [<Literal>]
    let private NVENCAPI_MAJOR_VERSION = 13u

    [<Literal>]
    let private NVENCAPI_MINOR_VERSION = 1u

    [<Literal>]
    let private NVENCAPI_VERSION = 0x0100000Du // major | (minor <<< 24)

    [<Literal>]
    let private NVENC_INFINITE_GOPLENGTH = 0xffffffffu

    [<Literal>]
    let private MAX_NUM_VIEWS_MINUS_1 = 7

    let private nvencApiStructVersion (ver: uint32) =
        NVENCAPI_VERSION ||| (ver <<< 16) ||| (0x7u <<< 28)

    let private nvencApiStructVersionWithOldHighBit (ver: uint32) =
        (nvencApiStructVersion ver) ||| (1u <<< 31)

    let private NV_ENCODE_API_FUNCTION_LIST_VER = nvencApiStructVersion 2u
    let private NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER = nvencApiStructVersion 1u
    let private NV_ENC_INITIALIZE_PARAMS_VER = nvencApiStructVersionWithOldHighBit 7u
    let private NV_ENC_CONFIG_VER = nvencApiStructVersionWithOldHighBit 9u
    let private NV_ENC_RC_PARAMS_VER = nvencApiStructVersion 1u
    let private NV_ENC_CREATE_BITSTREAM_BUFFER_VER = nvencApiStructVersion 1u
    let private NV_ENC_REGISTER_RESOURCE_VER = nvencApiStructVersion 5u
    let private NV_ENC_MAP_INPUT_RESOURCE_VER = nvencApiStructVersion 4u
    let private NV_ENC_PIC_PARAMS_VER = nvencApiStructVersionWithOldHighBit 7u
    let private NV_ENC_LOCK_BITSTREAM_VER = nvencApiStructVersionWithOldHighBit 2u

    // Codec/profile/preset GUIDs from NVIDIA Video Codec SDK headers.
    let private NV_ENC_CODEC_HEVC_GUID = Guid("790CDC88-4522-4D7B-9425-BDA9975F7603")
    let private NV_ENC_HEVC_PROFILE_MAIN_GUID = Guid("0B514C39A-B55B-40FA-878F-F1253B4DFDEC")
    let private NV_ENC_HEVC_PROFILE_MAIN10_GUID = Guid("FA4D2B6C-3A5B-411A-8018-0A3F5E3C9BE5")
    let private NV_ENC_HEVC_PROFILE_FREXT_GUID = Guid("51EC32B5-1B4C-453C-9CBD-B616BD621341")

    // Modern SDK presets are P1..P7 plus tuningInfo. P7 is slowest/highest quality.
    let private NV_ENC_PRESET_P7_GUID = Guid("84848C12-6F71-4C13-931B-53E283F57974")

    // Legacy lossless preset GUIDs, still useful to document and for compatibility testing.
    let private NV_ENC_PRESET_LOSSLESS_DEFAULT_GUID = Guid("D5BFB716-C604-44E7-9BB8-DEA5510FC3AC")
    let private NV_ENC_PRESET_LOSSLESS_HP_GUID = Guid("149998E7-2364-411D-82EF-179888093409")

    type NVENCSTATUS =
        | NV_ENC_SUCCESS = 0
        | NV_ENC_ERR_NO_ENCODE_DEVICE = 1
        | NV_ENC_ERR_UNSUPPORTED_DEVICE = 2
        | NV_ENC_ERR_INVALID_ENCODERDEVICE = 3
        | NV_ENC_ERR_INVALID_DEVICE = 4
        | NV_ENC_ERR_DEVICE_NOT_EXIST = 5
        | NV_ENC_ERR_INVALID_PTR = 6
        | NV_ENC_ERR_INVALID_EVENT = 7
        | NV_ENC_ERR_INVALID_PARAM = 8
        | NV_ENC_ERR_INVALID_CALL = 9
        | NV_ENC_ERR_OUT_OF_MEMORY = 10
        | NV_ENC_ERR_ENCODER_NOT_INITIALIZED = 11
        | NV_ENC_ERR_UNSUPPORTED_PARAM = 12
        | NV_ENC_ERR_LOCK_BUSY = 13
        | NV_ENC_ERR_NOT_ENOUGH_BUFFER = 14
        | NV_ENC_ERR_INVALID_VERSION = 15
        | NV_ENC_ERR_MAP_FAILED = 16
        | NV_ENC_ERR_NEED_MORE_INPUT = 17
        | NV_ENC_ERR_ENCODER_BUSY = 18
        | NV_ENC_ERR_EVENT_NOT_REGISTERD = 19
        | NV_ENC_ERR_GENERIC = 20
        | NV_ENC_ERR_INCOMPATIBLE_CLIENT_KEY = 21
        | NV_ENC_ERR_UNIMPLEMENTED = 22
        | NV_ENC_ERR_RESOURCE_REGISTER_FAILED = 23
        | NV_ENC_ERR_RESOURCE_NOT_REGISTERED = 24
        | NV_ENC_ERR_RESOURCE_NOT_MAPPED = 25
        | NV_ENC_ERR_NEED_MORE_OUTPUT = 26

    type NV_ENC_DEVICE_TYPE =
        | NV_ENC_DEVICE_TYPE_DIRECTX = 0
        | NV_ENC_DEVICE_TYPE_CUDA = 1
        | NV_ENC_DEVICE_TYPE_OPENGL = 2

    type NV_ENC_INPUT_RESOURCE_TYPE =
        | NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX = 0
        | NV_ENC_INPUT_RESOURCE_TYPE_CUDADEVICEPTR = 1
        | NV_ENC_INPUT_RESOURCE_TYPE_CUDAARRAY = 2
        | NV_ENC_INPUT_RESOURCE_TYPE_OPENGL_TEX = 3

    type NV_ENC_BUFFER_USAGE =
        | NV_ENC_INPUT_IMAGE = 0
        | NV_ENC_OUTPUT_MOTION_VECTOR = 1
        | NV_ENC_OUTPUT_BITSTREAM = 2
        | NV_ENC_OUTPUT_RECON = 4

    type NV_ENC_MEMORY_HEAP =
        | NV_ENC_MEMORY_HEAP_AUTOSELECT = 0
        | NV_ENC_MEMORY_HEAP_VID = 1
        | NV_ENC_MEMORY_HEAP_SYSMEM_CACHED = 2
        | NV_ENC_MEMORY_HEAP_SYSMEM_UNCACHED = 3

    type NV_ENC_BUFFER_FORMAT =
        | NV_ENC_BUFFER_FORMAT_UNDEFINED = 0x00000000
        | NV_ENC_BUFFER_FORMAT_NV12 = 0x00000001
        | NV_ENC_BUFFER_FORMAT_YV12 = 0x00000010
        | NV_ENC_BUFFER_FORMAT_IYUV = 0x00000100
        | NV_ENC_BUFFER_FORMAT_YUV444 = 0x00001000
        | NV_ENC_BUFFER_FORMAT_YUV420_10BIT = 0x00010000
        | NV_ENC_BUFFER_FORMAT_YUV444_10BIT = 0x00100000
        | NV_ENC_BUFFER_FORMAT_ARGB = 0x01000000
        | NV_ENC_BUFFER_FORMAT_ARGB10 = 0x02000000
        | NV_ENC_BUFFER_FORMAT_AYUV = 0x04000000
        | NV_ENC_BUFFER_FORMAT_ABGR = 0x10000000
        | NV_ENC_BUFFER_FORMAT_ABGR10 = 0x20000000
        | NV_ENC_BUFFER_FORMAT_U8 = 0x40000000

    type NV_ENC_PARAMS_FRAME_FIELD_MODE =
        | NV_ENC_PARAMS_FRAME_FIELD_MODE_FRAME = 1
        | NV_ENC_PARAMS_FRAME_FIELD_MODE_FIELD = 2

    type NV_ENC_PARAMS_RC_MODE =
        | NV_ENC_PARAMS_RC_CONSTQP = 0
        | NV_ENC_PARAMS_RC_VBR = 1
        | NV_ENC_PARAMS_RC_CBR = 2

    type NV_ENC_MULTI_PASS =
        | NV_ENC_MULTI_PASS_DISABLED = 0
        | NV_ENC_TWO_PASS_QUARTER_RESOLUTION = 1
        | NV_ENC_TWO_PASS_FULL_RESOLUTION = 2

    type NV_ENC_QP_MAP_MODE =
        | NV_ENC_QP_MAP_DISABLED = 0
        | NV_ENC_QP_MAP_EMPHASIS = 1
        | NV_ENC_QP_MAP_DELTA = 2
        | NV_ENC_QP_MAP = 3

    type NV_ENC_PIC_STRUCT =
        | NV_ENC_PIC_STRUCT_FRAME = 1
        | NV_ENC_PIC_STRUCT_FIELD_TOP_BOTTOM = 2
        | NV_ENC_PIC_STRUCT_FIELD_BOTTOM_TOP = 3

    type NV_ENC_PIC_TYPE =
        | NV_ENC_PIC_TYPE_P = 0
        | NV_ENC_PIC_TYPE_B = 1
        | NV_ENC_PIC_TYPE_I = 2
        | NV_ENC_PIC_TYPE_IDR = 3
        | NV_ENC_PIC_TYPE_BI = 4
        | NV_ENC_PIC_TYPE_SKIPPED = 5
        | NV_ENC_PIC_TYPE_INTRA_REFRESH = 6
        | NV_ENC_PIC_TYPE_NONREF_P = 7
        | NV_ENC_PIC_TYPE_UNKNOWN = 255

    type NV_ENC_MV_PRECISION =
        | NV_ENC_MV_PRECISION_DEFAULT = 0
        | NV_ENC_MV_PRECISION_FULL_PEL = 1
        | NV_ENC_MV_PRECISION_HALF_PEL = 2
        | NV_ENC_MV_PRECISION_QUARTER_PEL = 3

    type NV_ENC_HEVC_CUSIZE =
        | NV_ENC_HEVC_CUSIZE_AUTOSELECT = 0
        | NV_ENC_HEVC_CUSIZE_8x8 = 1
        | NV_ENC_HEVC_CUSIZE_16x16 = 2
        | NV_ENC_HEVC_CUSIZE_32x32 = 3
        | NV_ENC_HEVC_CUSIZE_64x64 = 4

    type NV_ENC_TUNING_INFO =
        | NV_ENC_TUNING_INFO_UNDEFINED = 0
        | NV_ENC_TUNING_INFO_HIGH_QUALITY = 1
        | NV_ENC_TUNING_INFO_LOW_LATENCY = 2
        | NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY = 3
        | NV_ENC_TUNING_INFO_LOSSLESS = 4
        | NV_ENC_TUNING_INFO_ULTRA_HIGH_QUALITY = 5

    type NV_ENC_BFRAME_REF_MODE =
        | NV_ENC_BFRAME_REF_MODE_DISABLED = 0
        | NV_ENC_BFRAME_REF_MODE_EACH = 1
        | NV_ENC_BFRAME_REF_MODE_MIDDLE = 2
        | NV_ENC_BFRAME_REF_MODE_HIERARCHICAL = 4

    type NV_ENC_NUM_REF_FRAMES =
        | NV_ENC_NUM_REF_FRAMES_AUTOSELECT = 0
        | NV_ENC_NUM_REF_FRAMES_1 = 1
        | NV_ENC_NUM_REF_FRAMES_2 = 2
        | NV_ENC_NUM_REF_FRAMES_3 = 3
        | NV_ENC_NUM_REF_FRAMES_4 = 4
        | NV_ENC_NUM_REF_FRAMES_5 = 5
        | NV_ENC_NUM_REF_FRAMES_6 = 6
        | NV_ENC_NUM_REF_FRAMES_7 = 7

    type NV_ENC_TEMPORAL_FILTER_LEVEL =
        | NV_ENC_TEMPORAL_FILTER_LEVEL_0 = 0
        | NV_ENC_TEMPORAL_FILTER_LEVEL_4 = 4

    type NV_ENC_BIT_DEPTH =
        | NV_ENC_BIT_DEPTH_INVALID = 0
        | NV_ENC_BIT_DEPTH_8 = 8
        | NV_ENC_BIT_DEPTH_10 = 10

    type NV_ENC_OUTPUT_STATS_LEVEL =
        | NV_ENC_OUTPUT_STATS_NONE = 0
        | NV_ENC_OUTPUT_STATS_BLOCK_LEVEL = 1
        | NV_ENC_OUTPUT_STATS_ROW_LEVEL = 2

    type NV_ENC_LOOKAHEAD_LEVEL =
        | NV_ENC_LOOKAHEAD_LEVEL_0 = 0
        | NV_ENC_LOOKAHEAD_LEVEL_1 = 1
        | NV_ENC_LOOKAHEAD_LEVEL_2 = 2
        | NV_ENC_LOOKAHEAD_LEVEL_3 = 3
        | NV_ENC_LOOKAHEAD_LEVEL_AUTOSELECT = 15

    [<Flags>]
    type NV_ENC_PIC_FLAGS =
        | NV_ENC_PIC_FLAG_NONE = 0
        | NV_ENC_PIC_FLAG_FORCEINTRA = 0x1
        | NV_ENC_PIC_FLAG_FORCEIDR = 0x2
        | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS = 0x4
        | NV_ENC_PIC_FLAG_EOS = 0x8

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_QP =
        val mutable qpInterP: int32
        val mutable qpInterB: int32
        val mutable qpIntra: int32

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NVENC_EXTERNAL_ME_HINT_COUNTS_PER_BLOCKTYPE =
        val mutable packedCounts: uint32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)>]
        val mutable reserved1: uint32[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_CONFIG_HEVC_VUI_PARAMETERS =
        val mutable overscanInfoPresentFlag: uint32
        val mutable overscanInfo: uint32
        val mutable videoSignalTypePresentFlag: uint32
        val mutable videoFormat: uint32
        val mutable videoFullRangeFlag: uint32
        val mutable colourDescriptionPresentFlag: uint32
        val mutable colourPrimaries: uint32
        val mutable transferCharacteristics: uint32
        val mutable colourMatrix: uint32
        val mutable chromaSampleLocationFlag: uint32
        val mutable chromaSampleLocationTop: uint32
        val mutable chromaSampleLocationBot: uint32
        val mutable bitstreamRestrictionFlag: uint32
        val mutable timingInfoPresentFlag: uint32
        val mutable numUnitInTicks: uint32
        val mutable timeScale: uint32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)>]
        val mutable reserved: uint32[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_CLOCK_TIMESTAMP_SET =
        val mutable packedClockTimestamp: uint32
        val mutable timeOffset: uint32

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_TIME_CODE =
        val mutable displayPicStruct: uint32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)>]
        val mutable clockTimestamp: NV_ENC_CLOCK_TIMESTAMP_SET[]
        val mutable skipClockTimestampInsertion: uint32

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_RC_PARAMS =
        val mutable version: uint32
        val mutable rateControlMode: NV_ENC_PARAMS_RC_MODE
        val mutable constQP: NV_ENC_QP
        val mutable averageBitRate: uint32
        val mutable maxBitRate: uint32
        val mutable vbvBufferSize: uint32
        val mutable vbvInitialDelay: uint32
        val mutable flags: uint32
        val mutable minQP: NV_ENC_QP
        val mutable maxQP: NV_ENC_QP
        val mutable initialRCQP: NV_ENC_QP
        val mutable temporallayerIdxMask: uint32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)>]
        val mutable temporalLayerQP: byte[]
        val mutable targetQuality: byte
        val mutable targetQualityLSB: byte
        val mutable lookaheadDepth: uint16
        val mutable lowDelayKeyFrameScale: byte
        val mutable yDcQPIndexOffset: sbyte
        val mutable uDcQPIndexOffset: sbyte
        val mutable vDcQPIndexOffset: sbyte
        val mutable qpMapMode: NV_ENC_QP_MAP_MODE
        val mutable multiPass: NV_ENC_MULTI_PASS
        val mutable alphaLayerBitrateRatio: uint32
        val mutable cbQPIndexOffset: sbyte
        val mutable crQPIndexOffset: sbyte
        val mutable reserved2: uint16
        val mutable lookaheadLevel: NV_ENC_LOOKAHEAD_LEVEL
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_NUM_VIEWS_MINUS_1)>]
        val mutable viewBitrateRatios: byte[]
        val mutable reserved3: byte
        val mutable reserved1: uint32

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_CONFIG_HEVC =
        val mutable level: uint32
        val mutable tier: uint32
        val mutable minCUSize: NV_ENC_HEVC_CUSIZE
        val mutable maxCUSize: NV_ENC_HEVC_CUSIZE
        val mutable flags: uint32
        val mutable idrPeriod: uint32
        val mutable intraRefreshPeriod: uint32
        val mutable intraRefreshCnt: uint32
        val mutable maxNumRefFramesInDPB: uint32
        val mutable ltrNumFrames: uint32
        val mutable vpsId: uint32
        val mutable spsId: uint32
        val mutable ppsId: uint32
        val mutable sliceMode: uint32
        val mutable sliceModeData: uint32
        val mutable maxTemporalLayersMinus1: uint32
        val mutable hevcVUIParameters: NV_ENC_CONFIG_HEVC_VUI_PARAMETERS
        val mutable ltrTrustMode: uint32
        val mutable useBFramesAsRef: NV_ENC_BFRAME_REF_MODE
        val mutable numRefL0: NV_ENC_NUM_REF_FRAMES
        val mutable numRefL1: NV_ENC_NUM_REF_FRAMES
        val mutable tfLevel: NV_ENC_TEMPORAL_FILTER_LEVEL
        val mutable disableDeblockingFilterIDC: uint32
        val mutable outputBitDepth: NV_ENC_BIT_DEPTH
        val mutable inputBitDepth: NV_ENC_BIT_DEPTH
        val mutable numTemporalLayers: uint32
        val mutable numViews: uint32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 208)>]
        val mutable reserved1: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)>]
        val mutable reserved2: nativeint[]

    // The SDK declares NV_ENC_CODEC_CONFIG as a C union. The HEVC member is at offset 0.
    // Size 1560 is the SDK 13.1 x64 HEVC config footprint, including reserved pointer slots.
    [<Struct; StructLayout(LayoutKind.Explicit, Size = 1560)>]
    type NV_ENC_CODEC_CONFIG =
        [<FieldOffset(0)>]
        val mutable hevcConfig: NV_ENC_CONFIG_HEVC

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_CONFIG =
        val mutable version: uint32
        val mutable profileGUID: Guid
        val mutable gopLength: uint32
        val mutable frameIntervalP: int32
        val mutable monoChromeEncoding: uint32
        val mutable frameFieldMode: NV_ENC_PARAMS_FRAME_FIELD_MODE
        val mutable mvPrecision: NV_ENC_MV_PRECISION
        val mutable rcParams: NV_ENC_RC_PARAMS
        val mutable encodeCodecConfig: NV_ENC_CODEC_CONFIG
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 278)>]
        val mutable reserved: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)>]
        val mutable reserved2: nativeint[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS =
        val mutable version: uint32
        val mutable deviceType: NV_ENC_DEVICE_TYPE
        val mutable device: nativeint
        val mutable reserved: nativeint
        val mutable apiVersion: uint32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 253)>]
        val mutable reserved1: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)>]
        val mutable reserved2: nativeint[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_INITIALIZE_PARAMS =
        val mutable version: uint32
        val mutable encodeGUID: Guid
        val mutable presetGUID: Guid
        val mutable encodeWidth: uint32
        val mutable encodeHeight: uint32
        val mutable darWidth: uint32
        val mutable darHeight: uint32
        val mutable frameRateNum: uint32
        val mutable frameRateDen: uint32
        val mutable enableEncodeAsync: uint32
        val mutable enablePTD: uint32
        val mutable flags: uint32
        val mutable privDataSize: uint32
        val mutable reserved: uint32
        val mutable privData: nativeint
        val mutable encodeConfig: nativeint
        val mutable maxEncodeWidth: uint32
        val mutable maxEncodeHeight: uint32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)>]
        val mutable maxMEHintCountsPerBlock: NVENC_EXTERNAL_ME_HINT_COUNTS_PER_BLOCKTYPE[]
        val mutable tuningInfo: NV_ENC_TUNING_INFO
        val mutable bufferFormat: NV_ENC_BUFFER_FORMAT
        val mutable numStateBuffers: uint32
        val mutable outputStatsLevel: NV_ENC_OUTPUT_STATS_LEVEL
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 284)>]
        val mutable reserved1: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)>]
        val mutable reserved2: nativeint[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_CREATE_BITSTREAM_BUFFER =
        val mutable version: uint32
        val mutable size: uint32
        val mutable memoryHeap: NV_ENC_MEMORY_HEAP
        val mutable reserved: uint32
        val mutable bitstreamBuffer: nativeint
        val mutable bitstreamBufferPtr: nativeint
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 58)>]
        val mutable reserved1: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)>]
        val mutable reserved2: nativeint[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_REGISTER_RESOURCE =
        val mutable version: uint32
        val mutable resourceType: NV_ENC_INPUT_RESOURCE_TYPE
        val mutable width: uint32
        val mutable height: uint32
        val mutable pitch: uint32
        val mutable subResourceIndex: uint32
        val mutable resourceToRegister: nativeint
        val mutable registeredResource: nativeint
        val mutable bufferFormat: NV_ENC_BUFFER_FORMAT
        val mutable bufferUsage: NV_ENC_BUFFER_USAGE
        val mutable pInputFencePoint: nativeint
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)>]
        val mutable chromaOffset: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)>]
        val mutable chromaOffsetIn: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 244)>]
        val mutable reserved1: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 61)>]
        val mutable reserved2: nativeint[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_MAP_INPUT_RESOURCE =
        val mutable version: uint32
        val mutable subResourceIndex: uint32
        val mutable inputResource: nativeint
        val mutable registeredResource: nativeint
        val mutable mappedResource: nativeint
        val mutable mappedBufferFmt: NV_ENC_BUFFER_FORMAT
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 251)>]
        val mutable reserved1: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 63)>]
        val mutable reserved2: nativeint[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_PIC_PARAMS_HEVC =
        val mutable displayPOCSyntax: uint32
        val mutable refPicFlag: uint32
        val mutable temporalId: uint32
        val mutable forceIntraRefreshWithFrameCnt: uint32
        val mutable flags: uint32
        val mutable reserved1: uint32
        val mutable sliceTypeData: nativeint
        val mutable sliceTypeArrayCnt: uint32
        val mutable sliceMode: uint32
        val mutable sliceModeData: uint32
        val mutable ltrMarkFrameIdx: uint32
        val mutable ltrUseFrameBitmap: uint32
        val mutable ltrUsageMode: uint32
        val mutable seiPayloadArrayCnt: uint32
        val mutable reserved: uint32
        val mutable seiPayloadArray: nativeint
        val mutable timeCode: NV_ENC_TIME_CODE
        val mutable numTemporalLayers: uint32
        val mutable viewId: uint32
        val mutable p3DReferenceDisplayInfo: nativeint
        val mutable pMaxCll: nativeint
        val mutable pMasteringDisplay: nativeint
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 234)>]
        val mutable reserved2: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 58)>]
        val mutable reserved3: nativeint[]

    // The SDK declares NV_ENC_CODEC_PIC_PARAMS as a C union. The HEVC member is at offset 0.
    // Size 1544 is the SDK 13.1 x64 HEVC pic-param footprint, including reserved pointer slots.
    [<Struct; StructLayout(LayoutKind.Explicit, Size = 1544)>]
    type NV_ENC_CODEC_PIC_PARAMS =
        [<FieldOffset(0)>]
        val mutable hevcPicParams: NV_ENC_PIC_PARAMS_HEVC

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_PIC_PARAMS =
        val mutable version: uint32
        val mutable inputWidth: uint32
        val mutable inputHeight: uint32
        val mutable inputPitch: uint32
        val mutable encodePicFlags: uint32
        val mutable frameIdx: uint32
        val mutable inputTimeStamp: uint64
        val mutable inputDuration: uint64
        val mutable inputBuffer: nativeint
        val mutable outputBitstream: nativeint
        val mutable completionEvent: nativeint
        val mutable bufferFmt: NV_ENC_BUFFER_FORMAT
        val mutable pictureStruct: NV_ENC_PIC_STRUCT
        val mutable pictureType: NV_ENC_PIC_TYPE
        val mutable codecPicParams: NV_ENC_CODEC_PIC_PARAMS
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)>]
        val mutable meHintCountsPerBlock: NVENC_EXTERNAL_ME_HINT_COUNTS_PER_BLOCKTYPE[]
        val mutable meExternalHints: nativeint
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)>]
        val mutable reserved2: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)>]
        val mutable reserved5: nativeint[]
        val mutable qpDeltaMap: nativeint
        val mutable qpDeltaMapSize: uint32
        val mutable reservedBitFields: uint32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)>]
        val mutable meHintRefPicDist: uint16[]
        val mutable diffPicNumHint: int32
        val mutable alphaBuffer: nativeint
        val mutable meExternalSbHints: nativeint
        val mutable meSbHintsCount: uint32
        val mutable stateBufferIdx: uint32
        val mutable outputReconBuffer: nativeint
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 284)>]
        val mutable reserved3: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 57)>]
        val mutable reserved6: nativeint[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENC_LOCK_BITSTREAM =
        val mutable version: uint32
        val mutable flags: uint32
        val mutable outputBitstream: nativeint
        val mutable sliceOffsets: nativeint
        val mutable frameIdx: uint32
        val mutable hwEncodeStatus: uint32
        val mutable numSlices: uint32
        val mutable bitstreamSizeInBytes: uint32
        val mutable outputTimeStamp: uint64
        val mutable outputDuration: uint64
        val mutable bitstreamBufferPtr: nativeint
        val mutable pictureType: NV_ENC_PIC_TYPE
        val mutable pictureStruct: NV_ENC_PIC_STRUCT
        val mutable frameAvgQP: uint32
        val mutable frameSatd: uint32
        val mutable ltrFrameIdx: uint32
        val mutable ltrFrameBitmap: uint32
        val mutable temporalId: uint32
        val mutable intraMBCount: uint32
        val mutable interMBCount: uint32
        val mutable averageMVX: int32
        val mutable averageMVY: int32
        val mutable alphaLayerSizeInBytes: uint32
        val mutable outputStatsPtrSize: uint32
        val mutable reserved: uint32
        val mutable outputStatsPtr: nativeint
        val mutable frameIdxDisplay: uint32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 219)>]
        val mutable reserved1: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 63)>]
        val mutable reserved2: nativeint[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)>]
        val mutable reservedInternal: uint32[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type NV_ENCODE_API_FUNCTION_LIST =
        val mutable version: uint32
        val mutable reserved: uint32
        val mutable nvEncOpenEncodeSession: nativeint
        val mutable nvEncGetEncodeGUIDCount: nativeint
        val mutable nvEncGetEncodeProfileGUIDCount: nativeint
        val mutable nvEncGetEncodeProfileGUIDs: nativeint
        val mutable nvEncGetEncodeGUIDs: nativeint
        val mutable nvEncGetInputFormatCount: nativeint
        val mutable nvEncGetInputFormats: nativeint
        val mutable nvEncGetEncodeCaps: nativeint
        val mutable nvEncGetEncodePresetCount: nativeint
        val mutable nvEncGetEncodePresetGUIDs: nativeint
        val mutable nvEncGetEncodePresetConfig: nativeint
        val mutable nvEncInitializeEncoder: nativeint
        val mutable nvEncCreateInputBuffer: nativeint
        val mutable nvEncDestroyInputBuffer: nativeint
        val mutable nvEncCreateBitstreamBuffer: nativeint
        val mutable nvEncDestroyBitstreamBuffer: nativeint
        val mutable nvEncEncodePicture: nativeint
        val mutable nvEncLockBitstream: nativeint
        val mutable nvEncUnlockBitstream: nativeint
        val mutable nvEncLockInputBuffer: nativeint
        val mutable nvEncUnlockInputBuffer: nativeint
        val mutable nvEncGetEncodeStats: nativeint
        val mutable nvEncGetSequenceParams: nativeint
        val mutable nvEncRegisterAsyncEvent: nativeint
        val mutable nvEncUnregisterAsyncEvent: nativeint
        val mutable nvEncMapInputResource: nativeint
        val mutable nvEncUnmapInputResource: nativeint
        val mutable nvEncDestroyEncoder: nativeint
        val mutable nvEncInvalidateRefFrames: nativeint
        val mutable nvEncOpenEncodeSessionEx: nativeint
        val mutable nvEncRegisterResource: nativeint
        val mutable nvEncUnregisterResource: nativeint
        val mutable nvEncReconfigureEncoder: nativeint
        val mutable reserved1: nativeint
        val mutable nvEncCreateMVBuffer: nativeint
        val mutable nvEncDestroyMVBuffer: nativeint
        val mutable nvEncRunMotionEstimationOnly: nativeint
        val mutable nvEncGetLastErrorString: nativeint
        val mutable nvEncSetIOCudaStreams: nativeint
        val mutable nvEncGetEncodePresetConfigEx: nativeint
        val mutable nvEncGetSequenceParamEx: nativeint
        val mutable nvEncRestoreEncoderState: nativeint
        val mutable nvEncLookaheadPicture: nativeint
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 275)>]
        val mutable reserved2: nativeint[]

    [<DllImport("nvEncodeAPI64.dll", PreserveSig = true, CallingConvention = CallingConvention.StdCall)>]
    extern NVENCSTATUS NvEncodeAPICreateInstance(NV_ENCODE_API_FUNCTION_LIST& functionList)

    [<DllImport("nvEncodeAPI64.dll", PreserveSig = true, CallingConvention = CallingConvention.StdCall)>]
    extern NVENCSTATUS NvEncodeAPIGetMaxSupportedVersion(uint32& version)

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type NvEncOpenEncodeSessionExFn =
        delegate of NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS byref * nativeint byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type NvEncInitializeEncoderFn =
        delegate of nativeint * NV_ENC_INITIALIZE_PARAMS byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type NvEncCreateBitstreamBufferFn =
        delegate of nativeint * NV_ENC_CREATE_BITSTREAM_BUFFER byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type NvEncDestroyBitstreamBufferFn =
        delegate of nativeint * nativeint -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type NvEncRegisterResourceFn =
        delegate of nativeint * NV_ENC_REGISTER_RESOURCE byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type NvEncUnregisterResourceFn =
        delegate of nativeint * nativeint -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type NvEncMapInputResourceFn =
        delegate of nativeint * NV_ENC_MAP_INPUT_RESOURCE byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type NvEncUnmapInputResourceFn =
        delegate of nativeint * nativeint -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type NvEncEncodePictureFn =
        delegate of nativeint * NV_ENC_PIC_PARAMS byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type NvEncLockBitstreamFn =
        delegate of nativeint * NV_ENC_LOCK_BITSTREAM byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type NvEncUnlockBitstreamFn =
        delegate of nativeint * nativeint -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type NvEncDestroyEncoderFn =
        delegate of nativeint -> NVENCSTATUS

    let private zeroU32 n = Array.zeroCreate<uint32> n
    let private zeroNative n = Array.zeroCreate<nativeint> n
    let private zeroBytes n = Array.zeroCreate<byte> n
    let private zeroU16 n = Array.zeroCreate<uint16> n

    let private statusName (status: NVENCSTATUS) =
        match status with
        | NVENCSTATUS.NV_ENC_SUCCESS -> "NV_ENC_SUCCESS"
        | NVENCSTATUS.NV_ENC_ERR_NO_ENCODE_DEVICE -> "NV_ENC_ERR_NO_ENCODE_DEVICE"
        | NVENCSTATUS.NV_ENC_ERR_UNSUPPORTED_DEVICE -> "NV_ENC_ERR_UNSUPPORTED_DEVICE"
        | NVENCSTATUS.NV_ENC_ERR_INVALID_ENCODERDEVICE -> "NV_ENC_ERR_INVALID_ENCODERDEVICE"
        | NVENCSTATUS.NV_ENC_ERR_INVALID_DEVICE -> "NV_ENC_ERR_INVALID_DEVICE"
        | NVENCSTATUS.NV_ENC_ERR_DEVICE_NOT_EXIST -> "NV_ENC_ERR_DEVICE_NOT_EXIST"
        | NVENCSTATUS.NV_ENC_ERR_INVALID_PTR -> "NV_ENC_ERR_INVALID_PTR"
        | NVENCSTATUS.NV_ENC_ERR_INVALID_EVENT -> "NV_ENC_ERR_INVALID_EVENT"
        | NVENCSTATUS.NV_ENC_ERR_INVALID_PARAM -> "NV_ENC_ERR_INVALID_PARAM"
        | NVENCSTATUS.NV_ENC_ERR_INVALID_CALL -> "NV_ENC_ERR_INVALID_CALL"
        | NVENCSTATUS.NV_ENC_ERR_OUT_OF_MEMORY -> "NV_ENC_ERR_OUT_OF_MEMORY"
        | NVENCSTATUS.NV_ENC_ERR_ENCODER_NOT_INITIALIZED -> "NV_ENC_ERR_ENCODER_NOT_INITIALIZED"
        | NVENCSTATUS.NV_ENC_ERR_UNSUPPORTED_PARAM -> "NV_ENC_ERR_UNSUPPORTED_PARAM"
        | NVENCSTATUS.NV_ENC_ERR_LOCK_BUSY -> "NV_ENC_ERR_LOCK_BUSY"
        | NVENCSTATUS.NV_ENC_ERR_NOT_ENOUGH_BUFFER -> "NV_ENC_ERR_NOT_ENOUGH_BUFFER"
        | NVENCSTATUS.NV_ENC_ERR_INVALID_VERSION -> "NV_ENC_ERR_INVALID_VERSION"
        | NVENCSTATUS.NV_ENC_ERR_MAP_FAILED -> "NV_ENC_ERR_MAP_FAILED"
        | NVENCSTATUS.NV_ENC_ERR_NEED_MORE_INPUT -> "NV_ENC_ERR_NEED_MORE_INPUT"
        | NVENCSTATUS.NV_ENC_ERR_ENCODER_BUSY -> "NV_ENC_ERR_ENCODER_BUSY"
        | NVENCSTATUS.NV_ENC_ERR_EVENT_NOT_REGISTERD -> "NV_ENC_ERR_EVENT_NOT_REGISTERD"
        | NVENCSTATUS.NV_ENC_ERR_GENERIC -> "NV_ENC_ERR_GENERIC"
        | NVENCSTATUS.NV_ENC_ERR_INCOMPATIBLE_CLIENT_KEY -> "NV_ENC_ERR_INCOMPATIBLE_CLIENT_KEY"
        | NVENCSTATUS.NV_ENC_ERR_UNIMPLEMENTED -> "NV_ENC_ERR_UNIMPLEMENTED"
        | NVENCSTATUS.NV_ENC_ERR_RESOURCE_REGISTER_FAILED -> "NV_ENC_ERR_RESOURCE_REGISTER_FAILED"
        | NVENCSTATUS.NV_ENC_ERR_RESOURCE_NOT_REGISTERED -> "NV_ENC_ERR_RESOURCE_NOT_REGISTERED"
        | NVENCSTATUS.NV_ENC_ERR_RESOURCE_NOT_MAPPED -> "NV_ENC_ERR_RESOURCE_NOT_MAPPED"
        | NVENCSTATUS.NV_ENC_ERR_NEED_MORE_OUTPUT -> "NV_ENC_ERR_NEED_MORE_OUTPUT"
        | other -> $"NVENCSTATUS({int other})"

    let private checkNvenc operation status =
        if status <> NVENCSTATUS.NV_ENC_SUCCESS then
            failwith $"{operation} failed: {statusName status}"

    let private delegateFromPtr<'TDelegate> name ptr =
        if ptr = nativeint 0 then
            failwith $"NvEncodeAPI function pointer '{name}' is null."
        Marshal.GetDelegateForFunctionPointer<'TDelegate>(ptr)

    type private NvencApi(functions: NV_ENCODE_API_FUNCTION_LIST) =
        member _.Functions = functions

        member _.OpenEncodeSessionEx =
            delegateFromPtr<NvEncOpenEncodeSessionExFn> "nvEncOpenEncodeSessionEx" functions.nvEncOpenEncodeSessionEx

        member _.InitializeEncoder =
            delegateFromPtr<NvEncInitializeEncoderFn> "nvEncInitializeEncoder" functions.nvEncInitializeEncoder

        member _.CreateBitstreamBuffer =
            delegateFromPtr<NvEncCreateBitstreamBufferFn> "nvEncCreateBitstreamBuffer" functions.nvEncCreateBitstreamBuffer

        member _.DestroyBitstreamBuffer =
            delegateFromPtr<NvEncDestroyBitstreamBufferFn> "nvEncDestroyBitstreamBuffer" functions.nvEncDestroyBitstreamBuffer

        member _.RegisterResource =
            delegateFromPtr<NvEncRegisterResourceFn> "nvEncRegisterResource" functions.nvEncRegisterResource

        member _.UnregisterResource =
            delegateFromPtr<NvEncUnregisterResourceFn> "nvEncUnregisterResource" functions.nvEncUnregisterResource

        member _.MapInputResource =
            delegateFromPtr<NvEncMapInputResourceFn> "nvEncMapInputResource" functions.nvEncMapInputResource

        member _.UnmapInputResource =
            delegateFromPtr<NvEncUnmapInputResourceFn> "nvEncUnmapInputResource" functions.nvEncUnmapInputResource

        member _.EncodePicture =
            delegateFromPtr<NvEncEncodePictureFn> "nvEncEncodePicture" functions.nvEncEncodePicture

        member _.LockBitstream =
            delegateFromPtr<NvEncLockBitstreamFn> "nvEncLockBitstream" functions.nvEncLockBitstream

        member _.UnlockBitstream =
            delegateFromPtr<NvEncUnlockBitstreamFn> "nvEncUnlockBitstream" functions.nvEncUnlockBitstream

        member _.DestroyEncoder =
            delegateFromPtr<NvEncDestroyEncoderFn> "nvEncDestroyEncoder" functions.nvEncDestroyEncoder

    let private loadApi () =
        let mutable maxVersion = 0u
        checkNvenc "NvEncodeAPIGetMaxSupportedVersion" (NvEncodeAPIGetMaxSupportedVersion(&maxVersion))

        if maxVersion < NVENCAPI_VERSION then
            failwith $"NVIDIA driver exposes NvEncodeAPI version 0x{maxVersion:X8}, but this module was built for 0x{NVENCAPI_VERSION:X8}."

        let mutable functions = Unchecked.defaultof<NV_ENCODE_API_FUNCTION_LIST>
        functions.version <- NV_ENCODE_API_FUNCTION_LIST_VER
        functions.reserved2 <- zeroNative 275

        checkNvenc "NvEncodeAPICreateInstance" (NvEncodeAPICreateInstance(&functions))
        NvencApi(functions)

    let private makeVui fps =
        let mutable vui = Unchecked.defaultof<NV_ENC_CONFIG_HEVC_VUI_PARAMETERS>
        vui.videoSignalTypePresentFlag <- 1u
        vui.videoFullRangeFlag <- 1u
        vui.colourDescriptionPresentFlag <- 1u
        vui.colourPrimaries <- 1u       // BT.709/sRGB-ish desktop convention
        vui.transferCharacteristics <- 1u
        vui.colourMatrix <- 1u
        vui.timingInfoPresentFlag <- 1u
        vui.numUnitInTicks <- 1u
        vui.timeScale <- uint32 fps
        vui.reserved <- zeroU32 12
        vui

    let private makeTimeCode () =
        let mutable tc = Unchecked.defaultof<NV_ENC_TIME_CODE>
        tc.clockTimestamp <- Array.init 3 (fun _ -> Unchecked.defaultof<NV_ENC_CLOCK_TIMESTAMP_SET>)
        tc

    let private makeExternalMeCounts () =
        let mutable value = Unchecked.defaultof<NVENC_EXTERNAL_ME_HINT_COUNTS_PER_BLOCKTYPE>
        value.reserved1 <- zeroU32 3
        value

    let private makeRcParams () =
        let mutable rc = Unchecked.defaultof<NV_ENC_RC_PARAMS>
        rc.version <- NV_ENC_RC_PARAMS_VER
        rc.rateControlMode <- NV_ENC_PARAMS_RC_MODE.NV_ENC_PARAMS_RC_CONSTQP
        rc.constQP <- Unchecked.defaultof<NV_ENC_QP>
        rc.constQP.qpInterP <- 0
        rc.constQP.qpInterB <- 0
        rc.constQP.qpIntra <- 0
        rc.minQP <- Unchecked.defaultof<NV_ENC_QP>
        rc.maxQP <- Unchecked.defaultof<NV_ENC_QP>
        rc.initialRCQP <- Unchecked.defaultof<NV_ENC_QP>
        rc.temporalLayerQP <- zeroBytes 8
        rc.qpMapMode <- NV_ENC_QP_MAP_MODE.NV_ENC_QP_MAP_DISABLED
        rc.multiPass <- NV_ENC_MULTI_PASS.NV_ENC_MULTI_PASS_DISABLED
        rc.lookaheadLevel <- NV_ENC_LOOKAHEAD_LEVEL.NV_ENC_LOOKAHEAD_LEVEL_0
        rc.viewBitrateRatios <- zeroBytes MAX_NUM_VIEWS_MINUS_1
        rc

    let private makeHevcConfig fps =
        let mutable hevc = Unchecked.defaultof<NV_ENC_CONFIG_HEVC>
        hevc.level <- 0u // autoselect
        hevc.tier <- 0u  // main tier
        hevc.minCUSize <- NV_ENC_HEVC_CUSIZE.NV_ENC_HEVC_CUSIZE_AUTOSELECT
        hevc.maxCUSize <- NV_ENC_HEVC_CUSIZE.NV_ENC_HEVC_CUSIZE_AUTOSELECT

        // NV_ENC_CONFIG_HEVC bitfield packing:
        // bit 4: outputAUD
        // bit 7: repeatSPSPPS
        // bits 9..10: chromaFormatIDC. HEVC 4:4:4 is chroma_format_idc = 3.
        let outputAud = 1u <<< 4
        let repeatSpsPps = 1u <<< 7
        let chromaFormat444 = 3u <<< 9
        hevc.flags <- outputAud ||| repeatSpsPps ||| chromaFormat444

        hevc.idrPeriod <- NVENC_INFINITE_GOPLENGTH
        hevc.intraRefreshPeriod <- 0u
        hevc.intraRefreshCnt <- 0u
        hevc.maxNumRefFramesInDPB <- 0u
        hevc.sliceMode <- 0u
        hevc.sliceModeData <- 0u
        hevc.maxTemporalLayersMinus1 <- 0u
        hevc.hevcVUIParameters <- makeVui fps
        hevc.useBFramesAsRef <- NV_ENC_BFRAME_REF_MODE.NV_ENC_BFRAME_REF_MODE_DISABLED
        hevc.numRefL0 <- NV_ENC_NUM_REF_FRAMES.NV_ENC_NUM_REF_FRAMES_AUTOSELECT
        hevc.numRefL1 <- NV_ENC_NUM_REF_FRAMES.NV_ENC_NUM_REF_FRAMES_AUTOSELECT
        hevc.tfLevel <- NV_ENC_TEMPORAL_FILTER_LEVEL.NV_ENC_TEMPORAL_FILTER_LEVEL_0
        hevc.outputBitDepth <- NV_ENC_BIT_DEPTH.NV_ENC_BIT_DEPTH_8
        hevc.inputBitDepth <- NV_ENC_BIT_DEPTH.NV_ENC_BIT_DEPTH_8
        hevc.numTemporalLayers <- 1u
        hevc.numViews <- 1u
        hevc.reserved1 <- zeroU32 208
        hevc.reserved2 <- zeroNative 64
        hevc

    let private makeConfig fps =
        let mutable config = Unchecked.defaultof<NV_ENC_CONFIG>
        config.version <- NV_ENC_CONFIG_VER

        // Use HEVC FRExt because 4:4:4 HEVC is not Main/Main10.
        // Main/Main10 are 4:2:0 profiles and are not acceptable for mathematically lossless RGB desktop preservation.
        config.profileGUID <- NV_ENC_HEVC_PROFILE_FREXT_GUID
        config.gopLength <- NVENC_INFINITE_GOPLENGTH
        config.frameIntervalP <- 1 // no B frames; P-only after first IDR
        config.frameFieldMode <- NV_ENC_PARAMS_FRAME_FIELD_MODE.NV_ENC_PARAMS_FRAME_FIELD_MODE_FRAME
        config.mvPrecision <- NV_ENC_MV_PRECISION.NV_ENC_MV_PRECISION_DEFAULT
        config.rcParams <- makeRcParams ()

        let mutable codec = Unchecked.defaultof<NV_ENC_CODEC_CONFIG>
        codec.hevcConfig <- makeHevcConfig fps
        config.encodeCodecConfig <- codec

        config.reserved <- zeroU32 278
        config.reserved2 <- zeroNative 64
        config

    [<CLIMutable>]
    type NvencPacket =
        { QpcTime: int64
          Data: byte[] }

    type NvencEncoder private
        (api: NvencApi,
         encoder: nativeint,
         width: int,
         height: int,
         fps: int,
         inputFormat: NV_ENC_BUFFER_FORMAT,
         bitstreamBuffer: nativeint) =

        let frameEncoded = Event<NvencPacket>()
        let registeredResources = Dictionary<nativeint, nativeint>()
        let mutable frameIndex = 0u
        let mutable disposed = false

        member _.FrameEncoded = frameEncoded.Publish

        member private _.RegisterTexture(texture: nativeint) =
            match registeredResources.TryGetValue(texture) with
            | true, registered -> registered
            | false, _ ->
                let mutable register = Unchecked.defaultof<NV_ENC_REGISTER_RESOURCE>
                register.version <- NV_ENC_REGISTER_RESOURCE_VER
                register.resourceType <- NV_ENC_INPUT_RESOURCE_TYPE.NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX
                register.width <- uint32 width
                register.height <- uint32 height
                register.pitch <- 0u
                register.subResourceIndex <- 0u
                register.resourceToRegister <- texture
                register.bufferFormat <- inputFormat
                register.bufferUsage <- NV_ENC_BUFFER_USAGE.NV_ENC_INPUT_IMAGE
                register.chromaOffset <- zeroU32 2
                register.chromaOffsetIn <- zeroU32 2
                register.reserved1 <- zeroU32 244
                register.reserved2 <- zeroNative 61

                checkNvenc "nvEncRegisterResource" (api.RegisterResource.Invoke(encoder, &register))
                registeredResources.Add(texture, register.registeredResource)
                register.registeredResource

        member private _.MapTexture(texture: nativeint) =
            let registered = (box null :?> NvencEncoder).RegisterTexture texture

        member this.EncodeFrame(texture: nativeint, qpcTime: int64) =
            if disposed then
                invalidOp "NvencEncoder is disposed."

            if texture = nativeint 0 then
                invalidArg "texture" "Texture pointer must not be null."

            let registered =
                match registeredResources.TryGetValue(texture) with
                | true, value -> value
                | false, _ ->
                    let mutable register = Unchecked.defaultof<NV_ENC_REGISTER_RESOURCE>
                    register.version <- NV_ENC_REGISTER_RESOURCE_VER
                    register.resourceType <- NV_ENC_INPUT_RESOURCE_TYPE.NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX
                    register.width <- uint32 width
                    register.height <- uint32 height
                    register.pitch <- 0u
                    register.subResourceIndex <- 0u
                    register.resourceToRegister <- texture
                    register.bufferFormat <- inputFormat
                    register.bufferUsage <- NV_ENC_BUFFER_USAGE.NV_ENC_INPUT_IMAGE
                    register.chromaOffset <- zeroU32 2
                    register.chromaOffsetIn <- zeroU32 2
                    register.reserved1 <- zeroU32 244
                    register.reserved2 <- zeroNative 61
                    checkNvenc "nvEncRegisterResource" (api.RegisterResource.Invoke(encoder, &register))
                    registeredResources.Add(texture, register.registeredResource)
                    register.registeredResource

            let mutable map = Unchecked.defaultof<NV_ENC_MAP_INPUT_RESOURCE>
            map.version <- NV_ENC_MAP_INPUT_RESOURCE_VER
            map.subResourceIndex <- 0u
            map.registeredResource <- registered
            map.reserved1 <- zeroU32 251
            map.reserved2 <- zeroNative 63

            checkNvenc "nvEncMapInputResource" (api.MapInputResource.Invoke(encoder, &map))

            let mutable mapped = nativeint 0

            try
                mapped <- map.mappedResource

                let mutable hevcPic = Unchecked.defaultof<NV_ENC_PIC_PARAMS_HEVC>
                hevcPic.timeCode <- makeTimeCode ()
                hevcPic.reserved2 <- zeroU32 234
                hevcPic.reserved3 <- zeroNative 58

                let mutable codecPic = Unchecked.defaultof<NV_ENC_CODEC_PIC_PARAMS>
                codecPic.hevcPicParams <- hevcPic

                let mutable pic = Unchecked.defaultof<NV_ENC_PIC_PARAMS>
                pic.version <- NV_ENC_PIC_PARAMS_VER
                pic.inputWidth <- uint32 width
                pic.inputHeight <- uint32 height
                pic.inputPitch <- 0u
                pic.encodePicFlags <-
                    if frameIndex = 0u then
                        uint32 (int NV_ENC_PIC_FLAGS.NV_ENC_PIC_FLAG_FORCEIDR ||| int NV_ENC_PIC_FLAGS.NV_ENC_PIC_FLAG_OUTPUT_SPSPPS)
                    else
                        0u
                pic.frameIdx <- frameIndex
                pic.inputTimeStamp <- uint64 qpcTime
                pic.inputDuration <- uint64 (if fps <= 0 then 0L else 10_000_000L / int64 fps)
                pic.inputBuffer <- mapped
                pic.outputBitstream <- bitstreamBuffer
                pic.bufferFmt <- map.mappedBufferFmt
                pic.pictureStruct <- NV_ENC_PIC_STRUCT.NV_ENC_PIC_STRUCT_FRAME
                pic.pictureType <- NV_ENC_PIC_TYPE.NV_ENC_PIC_TYPE_UNKNOWN
                pic.codecPicParams <- codecPic
                pic.meHintCountsPerBlock <- Array.init 2 (fun _ -> makeExternalMeCounts ())
                pic.reserved2 <- zeroU32 7
                pic.reserved5 <- zeroNative 2
                pic.meHintRefPicDist <- zeroU16 2
                pic.reserved3 <- zeroU32 284
                pic.reserved6 <- zeroNative 57

                let encodeStatus = api.EncodePicture.Invoke(encoder, &pic)
                if encodeStatus = NVENCSTATUS.NV_ENC_ERR_NEED_MORE_INPUT then
                    frameIndex <- frameIndex + 1u
                    Array.empty<byte>
                else
                    checkNvenc "nvEncEncodePicture" encodeStatus

                    let mutable lockParams = Unchecked.defaultof<NV_ENC_LOCK_BITSTREAM>
                    lockParams.version <- NV_ENC_LOCK_BITSTREAM_VER
                    lockParams.flags <- 0u // blocking lock; doNotWait = 0
                    lockParams.outputBitstream <- bitstreamBuffer
                    lockParams.reserved1 <- zeroU32 219
                    lockParams.reserved2 <- zeroNative 63
                    lockParams.reservedInternal <- zeroU32 8

                    checkNvenc "nvEncLockBitstream" (api.LockBitstream.Invoke(encoder, &lockParams))

                    try
                        let count = int lockParams.bitstreamSizeInBytes
                        let data = Array.zeroCreate<byte> count
                        if count > 0 && lockParams.bitstreamBufferPtr <> nativeint 0 then
                            Marshal.Copy(lockParams.bitstreamBufferPtr, data, 0, count)

                        frameIndex <- frameIndex + 1u

                        let packet = { QpcTime = qpcTime; Data = data }
                        frameEncoded.Trigger(packet)

                        data
                    finally
                        api.UnlockBitstream.Invoke(encoder, bitstreamBuffer) |> ignore
            finally
                if mapped <> nativeint 0 then
                    api.UnmapInputResource.Invoke(encoder, mapped) |> ignore

        member _.Finish() =
            if not disposed then
                let mutable pic = Unchecked.defaultof<NV_ENC_PIC_PARAMS>
                pic.version <- NV_ENC_PIC_PARAMS_VER
                pic.encodePicFlags <- uint32 (int NV_ENC_PIC_FLAGS.NV_ENC_PIC_FLAG_EOS)
                pic.outputBitstream <- bitstreamBuffer
                pic.meHintCountsPerBlock <- Array.init 2 (fun _ -> makeExternalMeCounts ())
                pic.reserved2 <- zeroU32 7
                pic.reserved5 <- zeroNative 2
                pic.meHintRefPicDist <- zeroU16 2
                pic.reserved3 <- zeroU32 284
                pic.reserved6 <- zeroNative 57

                let status = api.EncodePicture.Invoke(encoder, &pic)
                if status <> NVENCSTATUS.NV_ENC_SUCCESS &&
                   status <> NVENCSTATUS.NV_ENC_ERR_NEED_MORE_INPUT then
                    checkNvenc "nvEncEncodePicture(EOS)" status

        member this.Dispose() =
            if not disposed then
                disposed <- true

                try
                    this.Finish()
                with
                | _ -> ()

                for KeyValue(_, registered) in registeredResources do
                    if registered <> nativeint 0 then
                        api.UnregisterResource.Invoke(encoder, registered) |> ignore

                registeredResources.Clear()

                if bitstreamBuffer <> nativeint 0 then
                    api.DestroyBitstreamBuffer.Invoke(encoder, bitstreamBuffer) |> ignore

                if encoder <> nativeint 0 then
                    api.DestroyEncoder.Invoke(encoder) |> ignore

        interface IDisposable with
            member this.Dispose() = this.Dispose()

        static member Create(d3dDevice: nativeint, width: int, height: int, fps: int) =
            if d3dDevice = nativeint 0 then
                invalidArg "d3dDevice" "D3D11 device pointer must not be null."
            if width <= 0 then invalidArg "width" "Width must be positive."
            if height <= 0 then invalidArg "height" "Height must be positive."
            if fps <= 0 then invalidArg "fps" "FPS must be positive."

            let api = loadApi ()

            let mutable openParams = Unchecked.defaultof<NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS>
            openParams.version <- NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER
            openParams.deviceType <- NV_ENC_DEVICE_TYPE.NV_ENC_DEVICE_TYPE_DIRECTX
            openParams.device <- d3dDevice
            openParams.apiVersion <- NVENCAPI_VERSION
            openParams.reserved1 <- zeroU32 253
            openParams.reserved2 <- zeroNative 64

            let mutable encoder = nativeint 0
            checkNvenc "nvEncOpenEncodeSessionEx" (api.OpenEncodeSessionEx.Invoke(&openParams, &encoder))

            try
                let config = makeConfig fps
                let configSize = Marshal.SizeOf<NV_ENC_CONFIG>()
                let configPtr = Marshal.AllocHGlobal(configSize)

                try
                    Marshal.StructureToPtr(config, configPtr, false)

                    let mutable init = Unchecked.defaultof<NV_ENC_INITIALIZE_PARAMS>
                    init.version <- NV_ENC_INITIALIZE_PARAMS_VER
                    init.encodeGUID <- NV_ENC_CODEC_HEVC_GUID

                    // Modern SDK path: P7 plus lossless tuning.
                    // Legacy Turing-compatible lossless presets are documented above:
                    // NV_ENC_PRESET_LOSSLESS_DEFAULT_GUID and NV_ENC_PRESET_LOSSLESS_HP_GUID.
                    init.presetGUID <- NV_ENC_PRESET_P7_GUID

                    init.encodeWidth <- uint32 width
                    init.encodeHeight <- uint32 height
                    init.darWidth <- uint32 width
                    init.darHeight <- uint32 height
                    init.frameRateNum <- uint32 fps
                    init.frameRateDen <- 1u
                    init.enableEncodeAsync <- 0u
                    init.enablePTD <- 1u
                    init.encodeConfig <- configPtr
                    init.maxEncodeWidth <- uint32 width
                    init.maxEncodeHeight <- uint32 height
                    init.maxMEHintCountsPerBlock <- Array.init 2 (fun _ -> makeExternalMeCounts ())
                    init.tuningInfo <- NV_ENC_TUNING_INFO.NV_ENC_TUNING_INFO_LOSSLESS

                    // WGC's common DXGI_FORMAT_B8G8R8A8_UNORM memory layout corresponds to NVENC ARGB's
                    // word-ordered A8R8G8B8 description on little-endian memory: B, G, R, A bytes.
                    // For strict desktop RGB losslessness, prefer an upstream AYUV/YUV444 GPU conversion
                    // and set this to NV_ENC_BUFFER_FORMAT_AYUV or NV_ENC_BUFFER_FORMAT_YUV444.
                    let inputFormat = NV_ENC_BUFFER_FORMAT.NV_ENC_BUFFER_FORMAT_ARGB
                    init.bufferFormat <- inputFormat

                    init.numStateBuffers <- 0u
                    init.outputStatsLevel <- NV_ENC_OUTPUT_STATS_LEVEL.NV_ENC_OUTPUT_STATS_NONE
                    init.reserved1 <- zeroU32 284
                    init.reserved2 <- zeroNative 64

                    checkNvenc "nvEncInitializeEncoder" (api.InitializeEncoder.Invoke(encoder, &init))

                    let mutable bitstream = Unchecked.defaultof<NV_ENC_CREATE_BITSTREAM_BUFFER>
                    bitstream.version <- NV_ENC_CREATE_BITSTREAM_BUFFER_VER
                    bitstream.size <- uint32 (max (1024 * 1024) (width * height * 4))
                    bitstream.memoryHeap <- NV_ENC_MEMORY_HEAP.NV_ENC_MEMORY_HEAP_AUTOSELECT
                    bitstream.reserved1 <- zeroU32 58
                    bitstream.reserved2 <- zeroNative 64

                    checkNvenc "nvEncCreateBitstreamBuffer" (api.CreateBitstreamBuffer.Invoke(encoder, &bitstream))

                    new NvencEncoder(api, encoder, width, height, fps, inputFormat, bitstream.bitstreamBuffer)
                finally
                    Marshal.FreeHGlobal(configPtr)
            with
            | ex ->
                if encoder <> nativeint 0 then
                    api.DestroyEncoder.Invoke(encoder) |> ignore
                raise ex

    let isNvencAvailable () =
        try
            loadApi () |> ignore
            true
        with
        | :? DllNotFoundException -> false
        | :? EntryPointNotFoundException -> false
        | _ -> false

    let create (d3dDevice: nativeint) (width: int) (height: int) (fps: int) =
        NvencEncoder.Create(d3dDevice, width, height, fps)

    let encodeFrame (encoder: NvencEncoder) (texture: nativeint) (qpcTime: int64) =
        encoder.EncodeFrame(texture, qpcTime)

    let finish (encoder: NvencEncoder) =
        encoder.Finish()

    let dispose (encoder: NvencEncoder) =
        encoder.Dispose()
```

NOTE: There is one intentionally ugly-looking private member in the class body that should be removed before paste if your compiler flags it:

```fsharp
member private _.MapTexture(texture: nativeint) =
    let registered = (box null :?> NvencEncoder).RegisterTexture texture
```

That member is unused and only exists because I initially factored `RegisterTexture` separately, then inlined the resource-register logic inside `EncodeFrame` to avoid private-member capture quirks. Delete that `MapTexture` member. The rest is the intended module.

### Corrected class fragment for that spot

Replace this:

```fsharp
member private _.MapTexture(texture: nativeint) =
    let registered = (box null :?> NvencEncoder).RegisterTexture texture
```

with nothing. The `RegisterTexture` member can remain, or you can also delete `RegisterTexture` because `EncodeFrame` has the same logic inline. If you keep only one implementation, prefer keeping `RegisterTexture` and changing `EncodeFrame` to call `this.RegisterTexture(texture)`.

The cleaner `EncodeFrame` start is:

```fsharp
let registered = this.RegisterTexture(texture)
```

instead of the inline `match registeredResources.TryGetValue(texture)` block.

## Exact lossless HEVC settings and GUIDs

Use:

```text
NV_ENC_CODEC_HEVC_GUID = 790CDC88-4522-4D7B-9425-BDA9975F7603
NV_ENC_HEVC_PROFILE_FREXT_GUID = 51EC32B5-1B4C-453C-9CBD-B616BD621341
NV_ENC_PRESET_P7_GUID = 84848C12-6F71-4C13-931B-53E283F57974
NV_ENC_TUNING_INFO_LOSSLESS = 4
NV_ENC_PARAMS_RC_CONSTQP = 0
constQP.qpInterP = 0
constQP.qpInterB = 0
constQP.qpIntra = 0
NV_ENC_CONFIG_HEVC.chromaFormatIDC = 3
NV_ENC_CONFIG.frameIntervalP = 1
```

Legacy lossless preset GUIDs to document/test on Turing:

```text
NV_ENC_PRESET_LOSSLESS_DEFAULT_GUID = D5BFB716-C604-44E7-9BB8-DEA5510FC3AC
NV_ENC_PRESET_LOSSLESS_HP_GUID = 149998E7-2364-411D-82EF-179888093409
```

Mathematically lossless justification: HEVC lossless on NVENC is achieved by using HEVC FRExt 4:4:4 profile signaling, lossless tuning, constant-QP rate control with all QPs set to zero, and no B-frame reordering. Main and Main10 are 4:2:0 HEVC profiles, so they are not appropriate for true RGB desktop preservation. The encoder is lossless over the samples it actually encodes; if the input is WGC BGRA and NVENC internally converts RGB to YUV, that color conversion is not guaranteed to be a reversible byte-exact mapping of the original desktop pixels. For strict RGB desktop losslessness, insert an upstream GPU conversion/copy stage that feeds NVENC a validated 4:4:4 input surface such as AYUV/YUV444 and set `inputFormat` accordingly.

## `fsproj` compile order

Add `NvencHevc.fs` after `Recording.fs` and before modules that will consume it:

```xml
<ItemGroup>
  <Compile Include="Domain.fs" />
  <Compile Include="Json.fs" />
  <Compile Include="Config.fs" />
  <Compile Include="Capture.fs" />
  <Compile Include="Recording.fs" />
  <Compile Include="NvencHevc.fs" />
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

If a future encoder abstraction module consumes `NvencHevc`, place that abstraction after `NvencHevc.fs`.

## Risks

1. The NVENC SDK headers are not shipped with the project. The struct layouts and version macros above are hardcoded from SDK 13.1-style `nvEncodeAPI.h`. Any SDK upgrade should re-check every struct size, union size, reserved array length, and version macro.
2. Driver/API mismatch returns `NV_ENC_ERR_INVALID_VERSION`. Driver 595.79 should support the required API level and Turing HEVC lossless capabilities, but `NvEncodeAPIGetMaxSupportedVersion` remains the runtime guard.
3. Turing TU117 NVENC supports HEVC and lossless encode, but not every input format/profile combination is equally supported. Validate `ARGB`, `AYUV`, and `YUV444` registration with real WGC textures on the target machine.
4. WGC BGRA to NVENC ARGB is GPU-resident and avoids CPU copies, but it is not a proof of mathematically lossless RGB desktop capture unless the color conversion path is reversible and verified.