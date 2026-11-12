namespace Yumlog.Native

// FS0009: this module pins managed buffers and takes native pointers to build
// NV_ENC_* structures for the Video Codec SDK ABI. Unverifiable IL is inherent to
// that P/Invoke surface; restructuring to avoid it would mean extra copies of every
// frame. Suppressed deliberately and scoped to this file.
#nowarn "9"

open System
open System.Collections.Generic
open System.Runtime.InteropServices

/// Hardware HEVC encoder over the NVIDIA Video Codec SDK (nvEncodeAPI64.dll).
///
/// This module speaks the shared contracts in Domain.fs (CapturedFrame, HevcAccessUnit,
/// HevcDecoderConfig). It opens a D3D11 NVENC session, registers the capture texture,
/// encodes 4:4:4 lossless (QP0) HEVC, and drains the bitstream on EOS.
///
/// Honesty note (the central red-team blocker): the desktop is captured as BGRA (RGB).
/// NVENC's ARGB input path performs an internal RGB->YCbCr conversion that is NOT a
/// bit-exact reversible mapping for arbitrary RGB. We therefore report the encode as
/// "visually lossless" (QP0 4:4:4) and set IsMathematicallyLossless = false for the
/// texture/ARGB path. A genuinely bit-exact path requires feeding pre-permuted GBR
/// planes (see X265Encoder for that reversible mapping).
[<RequireQualifiedAccess>]
module NvencHevc =

    // ------------------------------------------------------------------
    // Version macros. The installed driver's NvEncodeAPIGetMaxSupportedVersion
    // reports major=13, minor=0, so we target NVENCAPI 13.0 exactly. Declaring a
    // higher minor (e.g. 13.1) fails the driver's version gate with
    // NV_ENC_ERR_INVALID_VERSION.
    // ------------------------------------------------------------------

    [<Literal>]
    let private NVENCAPI_MAJOR_VERSION = 13u

    [<Literal>]
    let private NVENCAPI_MINOR_VERSION = 0u

    // NVENCAPI_VERSION = major | (minor << 24)
    let private NVENCAPI_VERSION = NVENCAPI_MAJOR_VERSION ||| (NVENCAPI_MINOR_VERSION <<< 24)

    // Value returned by NvEncodeAPIGetMaxSupportedVersion is (major << 4) | minor.
    let private NVENC_REQUIRED_MAX = (NVENCAPI_MAJOR_VERSION <<< 4) ||| NVENCAPI_MINOR_VERSION

    [<Literal>]
    let private NVENC_INFINITE_GOPLENGTH = 0xffffffffu

    [<Literal>]
    let private MAX_NUM_VIEWS_MINUS_1 = 7

    // NVENCAPI_STRUCT_VERSION(ver) = NVENCAPI_VERSION | (ver << 16) | (0x7 << 28)
    let private structVersion (ver: uint32) =
        NVENCAPI_VERSION ||| (ver <<< 16) ||| (0x7u <<< 28)

    // Some structs additionally OR (1 << 31) for backward-compat versioning.
    let private structVersionCompat (ver: uint32) =
        (structVersion ver) ||| (1u <<< 31)

    let private NV_ENCODE_API_FUNCTION_LIST_VER = structVersion 2u
    let private NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER = structVersion 1u
    let private NV_ENC_INITIALIZE_PARAMS_VER = structVersionCompat 7u
    let private NV_ENC_CONFIG_VER = structVersionCompat 9u
    let private NV_ENC_PRESET_CONFIG_VER = structVersionCompat 4u
    let private NV_ENC_RC_PARAMS_VER = structVersion 1u
    let private NV_ENC_CREATE_BITSTREAM_BUFFER_VER = structVersion 1u
    let private NV_ENC_REGISTER_RESOURCE_VER = structVersion 5u
    let private NV_ENC_MAP_INPUT_RESOURCE_VER = structVersion 4u
    let private NV_ENC_PIC_PARAMS_VER = structVersionCompat 7u
    let private NV_ENC_LOCK_BITSTREAM_VER = structVersionCompat 2u
    let private NV_ENC_CAPS_PARAM_VER = structVersion 1u
    let private NV_ENC_SEQUENCE_PARAM_PAYLOAD_VER = structVersion 1u

    // ------------------------------------------------------------------
    // GUIDs (exact values from nvEncodeAPI.h)
    // ------------------------------------------------------------------

    let private NV_ENC_CODEC_HEVC_GUID = Guid("790CDC88-4522-4d7b-9425-BDA9975F7603")
    let private NV_ENC_PROFILE_HEVC_MAIN_GUID = Guid("B514C39A-B55B-40fa-878F-F1253B4DFDEC")
    let private NV_ENC_PROFILE_HEVC_MAIN10_GUID = Guid("fa4d2b6c-3a5b-411a-8018-0a3f5e3c9be5")
    let private NV_ENC_HEVC_PROFILE_FREXT_GUID = Guid("51ec32b5-1b4c-453c-9cbd-b616bd621341")

    let private NV_ENC_PRESET_P1_GUID = Guid("FC0A8D3E-45F8-4CF8-80C7-298871590EBF")
    let private NV_ENC_PRESET_P4_GUID = Guid("90A7B826-DF06-4862-B9D2-CD6D73A08681")
    let private NV_ENC_PRESET_P7_GUID = Guid("84848C12-6F71-4C13-931B-53E283F57974")

    // ------------------------------------------------------------------
    // Enums
    // ------------------------------------------------------------------

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

    type private NV_ENC_DEVICE_TYPE =
        | DIRECTX = 0
        | CUDA = 1
        | OPENGL = 2

    type private NV_ENC_INPUT_RESOURCE_TYPE =
        | DIRECTX = 0
        | CUDADEVICEPTR = 1
        | CUDAARRAY = 2
        | OPENGL_TEX = 3

    type private NV_ENC_BUFFER_USAGE =
        | NV_ENC_INPUT_IMAGE = 0
        | NV_ENC_OUTPUT_BITSTREAM = 2

    type private NV_ENC_MEMORY_HEAP =
        | AUTOSELECT = 0

    // NVENC input buffer formats. ARGB is a 32-bit packed format whose little-endian
    // byte order in memory is B, G, R, A -- i.e. exactly the DXGI B8G8R8A8_UNORM layout
    // WGC delivers, so we can register the capture texture directly.
    type private NV_ENC_BUFFER_FORMAT =
        | UNDEFINED = 0x00000000
        | NV12 = 0x00000001
        | YUV444 = 0x00001000
        | ARGB = 0x01000000
        | AYUV = 0x04000000
        | ABGR = 0x10000000

    type private NV_ENC_PARAMS_RC_MODE =
        | CONSTQP = 0
        | VBR = 1
        | CBR = 2

    type private NV_ENC_TUNING_INFO =
        | UNDEFINED = 0
        | HIGH_QUALITY = 1
        | LOW_LATENCY = 2
        | ULTRA_LOW_LATENCY = 3
        | LOSSLESS = 4

    // Subset of NV_ENC_CAPS we actually query. Values are the real ordinals from
    // nvEncodeAPI.h (YUV444_ENCODE=33, LOSSLESS_ENCODE=34). NOTE: index 17 is
    // HEIGHT_MAX and 29 is PREPROC_SUPPORT, so the earlier 17/29 values were wrong.
    type private NV_ENC_CAPS =
        | SUPPORT_YUV444_ENCODE = 33
        | SUPPORT_LOSSLESS_ENCODE = 34

    [<Flags>]
    type private NV_ENC_PIC_FLAGS =
        | NONE = 0x0
        | FORCEINTRA = 0x1
        | FORCEIDR = 0x2
        | OUTPUT_SPSPPS = 0x4
        | EOS = 0x8

    // ------------------------------------------------------------------
    // Structs. Layouts follow SDK 13.0 nvEncodeAPI.h. Reserved arrays are sized to
    // reach the SDK's struct footprint so the driver never writes past our buffers.
    // ------------------------------------------------------------------

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private NV_ENC_QP =
        val mutable qpInterP: int32
        val mutable qpInterB: int32
        val mutable qpIntra: int32

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private NVENC_EXTERNAL_ME_HINT_COUNTS_PER_BLOCKTYPE =
        val mutable packedCounts: uint32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)>]
        val mutable reserved1: uint32[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private NV_ENC_CONFIG_HEVC_VUI_PARAMETERS =
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
    type private NV_ENC_RC_PARAMS =
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
        val mutable qpMapMode: int32
        val mutable multiPass: int32
        val mutable alphaLayerBitrateRatio: uint32
        val mutable cbQPIndexOffset: sbyte
        val mutable crQPIndexOffset: sbyte
        val mutable reserved2: uint16
        val mutable lookaheadLevel: int32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_NUM_VIEWS_MINUS_1)>]
        val mutable viewBitrateRatios: byte[]
        val mutable reserved3: byte
        val mutable reserved1: uint32

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private NV_ENC_CONFIG_HEVC =
        val mutable level: uint32
        val mutable tier: uint32
        val mutable minCUSize: int32
        val mutable maxCUSize: int32
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
        val mutable useBFramesAsRef: int32
        val mutable numRefL0: int32
        val mutable numRefL1: int32
        val mutable tfLevel: int32
        val mutable disableDeblockingFilterIDC: uint32
        val mutable outputBitDepth: int32
        val mutable inputBitDepth: int32
        val mutable numTemporalLayers: uint32
        val mutable numViews: uint32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 208)>]
        val mutable reserved1: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)>]
        val mutable reserved2: nativeint[]

    // NV_ENC_CODEC_CONFIG is a C union; the HEVC member is at offset 0. Size covers the
    // SDK 13.0 x64 footprint including reserved pointer slots.
    [<Struct; StructLayout(LayoutKind.Explicit, Size = 1560)>]
    type private NV_ENC_CODEC_CONFIG =
        [<FieldOffset(0)>]
        val mutable hevcConfig: NV_ENC_CONFIG_HEVC

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private NV_ENC_CONFIG =
        val mutable version: uint32
        val mutable profileGUID: Guid
        val mutable gopLength: uint32
        val mutable frameIntervalP: int32
        val mutable monoChromeEncoding: uint32
        val mutable frameFieldMode: int32
        val mutable mvPrecision: int32
        val mutable rcParams: NV_ENC_RC_PARAMS
        val mutable encodeCodecConfig: NV_ENC_CODEC_CONFIG
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 278)>]
        val mutable reserved: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)>]
        val mutable reserved2: nativeint[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private NV_ENC_PRESET_CONFIG =
        val mutable version: uint32
        val mutable presetCfg: NV_ENC_CONFIG
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 255)>]
        val mutable reserved1: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)>]
        val mutable reserved2: nativeint[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private NV_ENC_CAPS_PARAM =
        val mutable version: uint32
        val mutable capsToQuery: int32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 62)>]
        val mutable reserved: uint32[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS =
        val mutable version: uint32
        val mutable deviceType: int32
        val mutable device: nativeint
        val mutable reserved: nativeint
        val mutable apiVersion: uint32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 253)>]
        val mutable reserved1: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)>]
        val mutable reserved2: nativeint[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private NV_ENC_INITIALIZE_PARAMS =
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
        val mutable tuningInfo: int32
        val mutable bufferFormat: int32
        val mutable numStateBuffers: uint32
        val mutable outputStatsLevel: int32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 284)>]
        val mutable reserved1: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)>]
        val mutable reserved2: nativeint[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private NV_ENC_CREATE_BITSTREAM_BUFFER =
        val mutable version: uint32
        val mutable size: uint32
        val mutable memoryHeap: int32
        val mutable reserved: uint32
        val mutable bitstreamBuffer: nativeint
        val mutable bitstreamBufferPtr: nativeint
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 58)>]
        val mutable reserved1: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)>]
        val mutable reserved2: nativeint[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private NV_ENC_REGISTER_RESOURCE =
        val mutable version: uint32
        val mutable resourceType: int32
        val mutable width: uint32
        val mutable height: uint32
        val mutable pitch: uint32
        val mutable subResourceIndex: uint32
        val mutable resourceToRegister: nativeint
        val mutable registeredResource: nativeint
        val mutable bufferFormat: int32
        val mutable bufferUsage: int32
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
    type private NV_ENC_MAP_INPUT_RESOURCE =
        val mutable version: uint32
        val mutable subResourceIndex: uint32
        val mutable inputResource: nativeint
        val mutable registeredResource: nativeint
        val mutable mappedResource: nativeint
        val mutable mappedBufferFmt: int32
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 251)>]
        val mutable reserved1: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 63)>]
        val mutable reserved2: nativeint[]

    // NV_ENC_CODEC_PIC_PARAMS is a C union; the HEVC member is at offset 0.
    [<Struct; StructLayout(LayoutKind.Explicit, Size = 1544)>]
    type private NV_ENC_CODEC_PIC_PARAMS =
        [<FieldOffset(0)>]
        val mutable reserved: uint32

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private NV_ENC_PIC_PARAMS =
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
        val mutable bufferFmt: int32
        val mutable pictureStruct: int32
        val mutable pictureType: int32
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
    type private NV_ENC_LOCK_BITSTREAM =
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
        val mutable pictureType: int32
        val mutable pictureStruct: int32
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
    type private NV_ENC_SEQUENCE_PARAM_PAYLOAD =
        val mutable version: uint32
        val mutable inBufferSize: uint32
        val mutable spsId: uint32
        val mutable ppsId: uint32
        val mutable spsppsBuffer: nativeint
        val mutable outSPSPPSPayloadSize: nativeint
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 250)>]
        val mutable reserved: uint32[]
        [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)>]
        val mutable reserved2: nativeint[]

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private NV_ENCODE_API_FUNCTION_LIST =
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

    // ------------------------------------------------------------------
    // Entry points + delegates
    // ------------------------------------------------------------------

    [<DllImport("nvEncodeAPI64.dll", CallingConvention = CallingConvention.StdCall)>]
    extern NVENCSTATUS private NvEncodeAPICreateInstance(NV_ENCODE_API_FUNCTION_LIST& functionList)

    [<DllImport("nvEncodeAPI64.dll", CallingConvention = CallingConvention.StdCall)>]
    extern NVENCSTATUS private NvEncodeAPIGetMaxSupportedVersion(uint32& version)

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private OpenSessionExFn = delegate of NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS byref * nativeint byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private GetEncodeCapsFn = delegate of nativeint * Guid * NV_ENC_CAPS_PARAM byref * int32 byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private GetPresetConfigExFn = delegate of nativeint * Guid * Guid * int32 * NV_ENC_PRESET_CONFIG byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private InitializeEncoderFn = delegate of nativeint * NV_ENC_INITIALIZE_PARAMS byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private CreateBitstreamBufferFn = delegate of nativeint * NV_ENC_CREATE_BITSTREAM_BUFFER byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private DestroyBitstreamBufferFn = delegate of nativeint * nativeint -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private RegisterResourceFn = delegate of nativeint * NV_ENC_REGISTER_RESOURCE byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private UnregisterResourceFn = delegate of nativeint * nativeint -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private MapInputResourceFn = delegate of nativeint * NV_ENC_MAP_INPUT_RESOURCE byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private UnmapInputResourceFn = delegate of nativeint * nativeint -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private EncodePictureFn = delegate of nativeint * NV_ENC_PIC_PARAMS byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private LockBitstreamFn = delegate of nativeint * NV_ENC_LOCK_BITSTREAM byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private UnlockBitstreamFn = delegate of nativeint * nativeint -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private GetSequenceParamsFn = delegate of nativeint * NV_ENC_SEQUENCE_PARAM_PAYLOAD byref -> NVENCSTATUS

    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type private DestroyEncoderFn = delegate of nativeint -> NVENCSTATUS

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    let private statusName (status: NVENCSTATUS) = $"{status} ({int status})"

    let private check operation status =
        if status <> NVENCSTATUS.NV_ENC_SUCCESS then
            failwith $"{operation} failed: {statusName status}"

    let private deleg<'T> (ptr: nativeint) name : 'T =
        if ptr = 0n then failwith $"NvEncodeAPI function pointer '{name}' is null."
        Marshal.GetDelegateForFunctionPointer<'T>(ptr)

    /// Loaded API function list plus the strongly-typed delegates we use.
    type private Api =
        { Funcs: NV_ENCODE_API_FUNCTION_LIST
          OpenSessionEx: OpenSessionExFn
          GetEncodeCaps: GetEncodeCapsFn
          GetPresetConfigEx: GetPresetConfigExFn
          Initialize: InitializeEncoderFn
          CreateBitstream: CreateBitstreamBufferFn
          DestroyBitstream: DestroyBitstreamBufferFn
          Register: RegisterResourceFn
          Unregister: UnregisterResourceFn
          Map: MapInputResourceFn
          Unmap: UnmapInputResourceFn
          Encode: EncodePictureFn
          Lock: LockBitstreamFn
          Unlock: UnlockBitstreamFn
          GetSeqParams: GetSequenceParamsFn
          Destroy: DestroyEncoderFn }

    let private loadApi () : Api =
        let mutable maxVersion = 0u
        check "NvEncodeAPIGetMaxSupportedVersion" (NvEncodeAPIGetMaxSupportedVersion(&maxVersion))
        if NVENC_REQUIRED_MAX > maxVersion then
            failwith
                $"NVIDIA driver supports NvEncodeAPI up to (major<<4|minor)=0x{maxVersion:X}, but this module requires 0x{NVENC_REQUIRED_MAX:X} (13.0)."

        let mutable funcs = Unchecked.defaultof<NV_ENCODE_API_FUNCTION_LIST>
        funcs.version <- NV_ENCODE_API_FUNCTION_LIST_VER
        funcs.reserved2 <- Array.zeroCreate 275
        check "NvEncodeAPICreateInstance" (NvEncodeAPICreateInstance(&funcs))

        { Funcs = funcs
          OpenSessionEx = deleg funcs.nvEncOpenEncodeSessionEx "nvEncOpenEncodeSessionEx"
          GetEncodeCaps = deleg funcs.nvEncGetEncodeCaps "nvEncGetEncodeCaps"
          GetPresetConfigEx = deleg funcs.nvEncGetEncodePresetConfigEx "nvEncGetEncodePresetConfigEx"
          Initialize = deleg funcs.nvEncInitializeEncoder "nvEncInitializeEncoder"
          CreateBitstream = deleg funcs.nvEncCreateBitstreamBuffer "nvEncCreateBitstreamBuffer"
          DestroyBitstream = deleg funcs.nvEncDestroyBitstreamBuffer "nvEncDestroyBitstreamBuffer"
          Register = deleg funcs.nvEncRegisterResource "nvEncRegisterResource"
          Unregister = deleg funcs.nvEncUnregisterResource "nvEncUnregisterResource"
          Map = deleg funcs.nvEncMapInputResource "nvEncMapInputResource"
          Unmap = deleg funcs.nvEncUnmapInputResource "nvEncUnmapInputResource"
          Encode = deleg funcs.nvEncEncodePicture "nvEncEncodePicture"
          Lock = deleg funcs.nvEncLockBitstream "nvEncLockBitstream"
          Unlock = deleg funcs.nvEncUnlockBitstream "nvEncUnlockBitstream"
          GetSeqParams = deleg funcs.nvEncGetSequenceParams "nvEncGetSequenceParams"
          Destroy = deleg funcs.nvEncDestroyEncoder "nvEncDestroyEncoder" }

    let private openSession (api: Api) (device: nativeint) =
        let mutable p = Unchecked.defaultof<NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS>
        p.version <- NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER
        p.deviceType <- int NV_ENC_DEVICE_TYPE.DIRECTX
        p.device <- device
        p.apiVersion <- NVENCAPI_VERSION
        p.reserved1 <- Array.zeroCreate 253
        p.reserved2 <- Array.zeroCreate 64
        let mutable encoder = 0n
        let st = api.OpenSessionEx.Invoke(&p, &encoder)
        st, encoder

    let private queryCap (api: Api) (encoder: nativeint) (cap: NV_ENC_CAPS) =
        let mutable p = Unchecked.defaultof<NV_ENC_CAPS_PARAM>
        p.version <- NV_ENC_CAPS_PARAM_VER
        p.capsToQuery <- int cap
        p.reserved <- Array.zeroCreate 62
        let mutable value = 0
        let st = api.GetEncodeCaps.Invoke(encoder, NV_ENC_CODEC_HEVC_GUID, &p, &value)
        if st = NVENCSTATUS.NV_ENC_SUCCESS then value else 0

    // ------------------------------------------------------------------
    // Public capability probe: opens a real encode session on the given device and
    // verifies HEVC 4:4:4 + lossless caps.
    // ------------------------------------------------------------------

    let isUsable (d3d11Device: nativeint) : bool =
        if d3d11Device = 0n then false
        else
            try
                let api = loadApi ()
                let st, encoder = openSession api d3d11Device
                if st <> NVENCSTATUS.NV_ENC_SUCCESS || encoder = 0n then false
                else
                    try
                        let yuv444 = queryCap api encoder NV_ENC_CAPS.SUPPORT_YUV444_ENCODE
                        let lossless = queryCap api encoder NV_ENC_CAPS.SUPPORT_LOSSLESS_ENCODE
                        yuv444 <> 0 && lossless <> 0
                    finally
                        api.Destroy.Invoke(encoder) |> ignore
            with _ -> false

    // ------------------------------------------------------------------
    // Encoder session
    // ------------------------------------------------------------------

    /// Translates the canonical x264/x265 preset vocabulary (validated once, backend-
    /// independently, by Config.normalizePreset) onto NVENC preset GUIDs.
    ///
    /// P1 is fastest/lowest quality, P7 is slowest/highest. An unrecognized value used
    /// to fall through to P7, so a config saying "ultrafast" silently selected the
    /// SLOWEST preset - the exact opposite of the request - and any typo did the same.
    ///
    /// Only the P1/P4/P7 GUIDs are transcribed from the Video Codec SDK header here, so
    /// the 7-point NVENC scale is deliberately collapsed onto those three verified
    /// constants rather than shipping unverified GUIDs that would fail encoder init at
    /// runtime. The rounding trades speed against quality, never correctness.
    ///
    /// NVENC-native names (p1..p7) are intentionally NOT accepted: Preset is shared with
    /// the x265 and FFmpeg backends, which reject them, and with backend "auto" the
    /// encoder is not known until the adapter is probed.
    let private presetFor (preset: string) =
        match (orEmptyIfNull preset).Trim().ToLowerInvariant() with
        | "ultrafast" | "superfast" | "veryfast" | "faster" | "fast" -> NV_ENC_PRESET_P1_GUID
        | "medium" | "" -> NV_ENC_PRESET_P4_GUID
        | "slow" | "slower" | "veryslow" | "placebo" -> NV_ENC_PRESET_P7_GUID
        | other ->
            invalidArg
                "preset"
                $"Unsupported preset '{other}'. Expected one of: ultrafast, superfast, veryfast, faster, fast, medium, slow, slower, veryslow, placebo."

    /// One encoded output awaiting bitstream lock, kept in FIFO submission order.
    type private Pending =
        { Output: nativeint
          Mapped: nativeint
          Pts: int64
          Dts: int64
          Qpc: int64 }

    [<Sealed>]
    type Session private
        (api: Api,
         encoder: nativeint,
         width: int,
         height: int,
         fps: int,
         outputBuffers: nativeint[],
         decoderConfig: HevcDecoderConfig,
         losslessMode: string,
         isMathematicallyLossless: bool) =

        let inputFormat = int NV_ENC_BUFFER_FORMAT.ARGB
        let registered = Dictionary<nativeint, nativeint>()
        let inFlight = Queue<Pending>()
        let mutable submitIndex = 0
        let mutable frameIndex = 0u
        let mutable eosSent = false
        let mutable disposed = false

        let registerTexture (texture: nativeint) =
            match registered.TryGetValue texture with
            | true, r -> r
            | _ ->
                let mutable r = Unchecked.defaultof<NV_ENC_REGISTER_RESOURCE>
                r.version <- NV_ENC_REGISTER_RESOURCE_VER
                r.resourceType <- int NV_ENC_INPUT_RESOURCE_TYPE.DIRECTX
                r.width <- uint32 width
                r.height <- uint32 height
                r.pitch <- 0u
                r.subResourceIndex <- 0u
                r.resourceToRegister <- texture
                r.bufferFormat <- inputFormat
                r.bufferUsage <- int NV_ENC_BUFFER_USAGE.NV_ENC_INPUT_IMAGE
                r.chromaOffset <- Array.zeroCreate 2
                r.chromaOffsetIn <- Array.zeroCreate 2
                r.reserved1 <- Array.zeroCreate 244
                r.reserved2 <- Array.zeroCreate 61
                check "nvEncRegisterResource" (api.Register.Invoke(encoder, &r))
                registered.[texture] <- r.registeredResource
                r.registeredResource

        /// Locks one bitstream buffer, copies the Annex B bytes, unlocks and unmaps input.
        let drainOne (p: Pending) : HevcAccessUnit =
            let mutable lb = Unchecked.defaultof<NV_ENC_LOCK_BITSTREAM>
            lb.version <- NV_ENC_LOCK_BITSTREAM_VER
            lb.flags <- 0u
            lb.outputBitstream <- p.Output
            lb.reserved1 <- Array.zeroCreate 219
            lb.reserved2 <- Array.zeroCreate 63
            lb.reservedInternal <- Array.zeroCreate 8
            check "nvEncLockBitstream" (api.Lock.Invoke(encoder, &lb))
            try
                let count = int lb.bitstreamSizeInBytes
                let data = Array.zeroCreate<byte> count
                if count > 0 && lb.bitstreamBufferPtr <> 0n then
                    Marshal.Copy(lb.bitstreamBufferPtr, data, 0, count)
                // NV_ENC_PIC_TYPE_IDR = 3, NV_ENC_PIC_TYPE_I = 2.
                let isKey = lb.pictureType = 3 || lb.pictureType = 2
                { Pts = p.Pts; Dts = p.Dts; QpcTime = p.Qpc; Data = data; IsKeyFrame = isKey }
            finally
                api.Unlock.Invoke(encoder, p.Output) |> ignore
                if p.Mapped <> 0n then
                    api.Unmap.Invoke(encoder, p.Mapped) |> ignore

        member _.Backend = HevcBackend.Nvenc
        member _.DecoderConfig = decoderConfig
        member _.LosslessMode = losslessMode
        member _.IsMathematicallyLossless = isMathematicallyLossless
        member _.NeedsCpuFrames = false

        member _.Encode(frame: CapturedFrame) : HevcAccessUnit list =
            if disposed then invalidOp "NVENC session is disposed."
            if frame.Texture = 0n then
                invalidArg "frame" "NVENC requires CapturedFrame.Texture (a D3D11 texture on the encoder adapter)."

            let registeredRes = registerTexture frame.Texture

            let mutable map = Unchecked.defaultof<NV_ENC_MAP_INPUT_RESOURCE>
            map.version <- NV_ENC_MAP_INPUT_RESOURCE_VER
            map.registeredResource <- registeredRes
            map.reserved1 <- Array.zeroCreate 251
            map.reserved2 <- Array.zeroCreate 63
            check "nvEncMapInputResource" (api.Map.Invoke(encoder, &map))
            let mapped = map.mappedResource

            let output = outputBuffers.[submitIndex % outputBuffers.Length]
            submitIndex <- submitIndex + 1

            let mutable pic = Unchecked.defaultof<NV_ENC_PIC_PARAMS>
            pic.version <- NV_ENC_PIC_PARAMS_VER
            pic.inputWidth <- uint32 width
            pic.inputHeight <- uint32 height
            pic.inputPitch <- 0u
            pic.encodePicFlags <-
                if frameIndex = 0u then
                    uint32 (int NV_ENC_PIC_FLAGS.FORCEIDR ||| int NV_ENC_PIC_FLAGS.OUTPUT_SPSPPS)
                else 0u
            pic.frameIdx <- frameIndex
            pic.inputTimeStamp <- uint64 frame.Pts
            pic.inputDuration <- uint64 (if fps <= 0 then 0L else HevcTimebase.TicksPerSecond / int64 fps)
            pic.inputBuffer <- mapped
            pic.outputBitstream <- output
            pic.bufferFmt <- map.mappedBufferFmt
            pic.pictureStruct <- 1 // NV_ENC_PIC_STRUCT_FRAME
            pic.pictureType <- 255 // NV_ENC_PIC_TYPE_UNKNOWN -> let PTD decide
            pic.meHintCountsPerBlock <- Array.zeroCreate 2
            pic.reserved2 <- Array.zeroCreate 7
            pic.reserved5 <- Array.zeroCreate 2
            pic.meHintRefPicDist <- Array.zeroCreate 2
            pic.reserved3 <- Array.zeroCreate 284
            pic.reserved6 <- Array.zeroCreate 57

            frameIndex <- frameIndex + 1u

            let entry = { Output = output; Mapped = mapped; Pts = frame.Pts; Dts = frame.Pts; Qpc = frame.QpcTime }
            let st = api.Encode.Invoke(encoder, &pic)
            match st with
            | NVENCSTATUS.NV_ENC_ERR_NEED_MORE_INPUT ->
                // Encoder buffered this frame; keep input mapped until it is drained.
                inFlight.Enqueue entry
                []
            | NVENCSTATUS.NV_ENC_SUCCESS ->
                // No-delay config (P-only, no lookahead): this frame's output is ready.
                // Drain any previously buffered frames first to preserve order.
                let results = ResizeArray<HevcAccessUnit>()
                inFlight.Enqueue entry
                while inFlight.Count > 0 do
                    results.Add(drainOne (inFlight.Dequeue()))
                List.ofSeq results
            | other ->
                if mapped <> 0n then api.Unmap.Invoke(encoder, mapped) |> ignore
                check "nvEncEncodePicture" other
                []

        member _.Finish() : HevcAccessUnit list =
            if disposed then []
            else
                let results = ResizeArray<HevcAccessUnit>()
                if not eosSent then
                    eosSent <- true
                    let mutable pic = Unchecked.defaultof<NV_ENC_PIC_PARAMS>
                    pic.version <- NV_ENC_PIC_PARAMS_VER
                    pic.encodePicFlags <- uint32 (int NV_ENC_PIC_FLAGS.EOS)
                    pic.inputBuffer <- 0n
                    pic.outputBitstream <- 0n
                    pic.meHintCountsPerBlock <- Array.zeroCreate 2
                    pic.reserved2 <- Array.zeroCreate 7
                    pic.reserved5 <- Array.zeroCreate 2
                    pic.meHintRefPicDist <- Array.zeroCreate 2
                    pic.reserved3 <- Array.zeroCreate 284
                    pic.reserved6 <- Array.zeroCreate 57
                    let st = api.Encode.Invoke(encoder, &pic)
                    if st <> NVENCSTATUS.NV_ENC_SUCCESS && st <> NVENCSTATUS.NV_ENC_ERR_NEED_MORE_INPUT then
                        check "nvEncEncodePicture(EOS)" st
                    // Drain anything the encoder buffered.
                    while inFlight.Count > 0 do
                        results.Add(drainOne (inFlight.Dequeue()))
                List.ofSeq results

        member this.Dispose() =
            if not disposed then
                // Drain BEFORE flipping the disposed flag so EOS + bitstream lock still run.
                (try this.Finish() |> ignore with _ -> ())
                disposed <- true
                for KeyValue(_, r) in registered do
                    if r <> 0n then api.Unregister.Invoke(encoder, r) |> ignore
                registered.Clear()
                for b in outputBuffers do
                    if b <> 0n then api.DestroyBitstream.Invoke(encoder, b) |> ignore
                if encoder <> 0n then api.Destroy.Invoke(encoder) |> ignore

        interface IDisposable with
            member this.Dispose() = this.Dispose()

        static member Create(d3d11Device: nativeint, width: int, height: int, fps: int, lossless: bool, preset: string) =
            if d3d11Device = 0n then invalidArg "d3d11Device" "D3D11 device pointer must not be null."
            if width <= 0 then invalidArg "width" "Width must be positive."
            if height <= 0 then invalidArg "height" "Height must be positive."
            let fps = if fps <= 0 then 30 else fps

            let api = loadApi ()
            let st, encoder = openSession api d3d11Device
            check "nvEncOpenEncodeSessionEx" st
            if encoder = 0n then failwith "nvEncOpenEncodeSessionEx returned a null encoder."

            try
                // Verify capabilities before claiming anything.
                let supports444 = queryCap api encoder NV_ENC_CAPS.SUPPORT_YUV444_ENCODE <> 0
                let supportsLossless = queryCap api encoder NV_ENC_CAPS.SUPPORT_LOSSLESS_ENCODE <> 0
                if not supports444 then
                    failwith "NVENC on this device does not support HEVC 4:4:4 encode (NV_ENC_CAPS_SUPPORT_YUV444_ENCODE = 0)."
                if lossless && not supportsLossless then
                    failwith "NVENC on this device does not support lossless encode (NV_ENC_CAPS_SUPPORT_LOSSLESS_ENCODE = 0)."

                let presetGuid = presetFor preset
                let tuning = if lossless then int NV_ENC_TUNING_INFO.LOSSLESS else int NV_ENC_TUNING_INFO.HIGH_QUALITY

                // Seed from the driver's preset config so every reserved/default field is
                // valid, then override only what we need. This is the NVIDIA-recommended,
                // robust pattern (avoids NV_ENC_ERR_INVALID_PARAM from a hand-built config).
                let mutable presetCfg = Unchecked.defaultof<NV_ENC_PRESET_CONFIG>
                presetCfg.version <- NV_ENC_PRESET_CONFIG_VER
                presetCfg.presetCfg <- Unchecked.defaultof<NV_ENC_CONFIG>
                presetCfg.presetCfg.version <- NV_ENC_CONFIG_VER
                presetCfg.reserved1 <- Array.zeroCreate 255
                presetCfg.reserved2 <- Array.zeroCreate 64
                check
                    "nvEncGetEncodePresetConfigEx"
                    (api.GetPresetConfigEx.Invoke(encoder, NV_ENC_CODEC_HEVC_GUID, presetGuid, tuning, &presetCfg))

                let mutable config = presetCfg.presetCfg
                config.version <- NV_ENC_CONFIG_VER
                config.profileGUID <- NV_ENC_HEVC_PROFILE_FREXT_GUID // 4:4:4 requires FRExt
                config.gopLength <- NVENC_INFINITE_GOPLENGTH
                config.frameIntervalP <- 1 // P-only, no B-frames -> DTS == PTS
                config.monoChromeEncoding <- 0u

                config.rcParams.version <- NV_ENC_RC_PARAMS_VER
                config.rcParams.rateControlMode <- NV_ENC_PARAMS_RC_MODE.CONSTQP
                let mutable qp = Unchecked.defaultof<NV_ENC_QP>
                qp.qpInterP <- 0
                qp.qpInterB <- 0
                qp.qpIntra <- 0
                config.rcParams.constQP <- qp

                // HEVC-specific: 4:4:4 chroma, 8-bit, IDR only on request, no repeat SPS/PPS
                // (hvc1 mux carries parameter sets in hvcC; we still emit them in-band on the
                // first IDR via NV_ENC_PIC_FLAG_OUTPUT_SPSPPS so a raw stream is decodable).
                let mutable hevc = config.encodeCodecConfig.hevcConfig
                // flags bitfield: chromaFormatIDC (2 bits) at bit 9. 3 == 4:4:4.
                hevc.flags <- 3u <<< 9
                hevc.idrPeriod <- NVENC_INFINITE_GOPLENGTH
                hevc.outputBitDepth <- 8
                hevc.inputBitDepth <- 8
                hevc.numTemporalLayers <- 1u
                hevc.numViews <- 1u

                // VUI: signal full-range and the color characteristics that actually apply.
                // We do NOT claim an identity/GBR matrix here because NVENC's ARGB input path
                // performs a real (BT.709-family) RGB->YCbCr conversion that is not bit-exact
                // reversible; signalling identity would be a false claim.
                let mutable vui = hevc.hevcVUIParameters
                vui.videoSignalTypePresentFlag <- 1u
                vui.videoFullRangeFlag <- 1u
                vui.colourDescriptionPresentFlag <- 1u
                vui.colourPrimaries <- 1u          // BT.709
                vui.transferCharacteristics <- 1u  // BT.709
                vui.colourMatrix <- 1u             // BT.709
                vui.reserved <- Array.zeroCreate 12
                hevc.hevcVUIParameters <- vui
                config.encodeCodecConfig.hevcConfig <- hevc

                let configSize = Marshal.SizeOf<NV_ENC_CONFIG>()
                let configPtr = Marshal.AllocHGlobal configSize
                try
                    Marshal.StructureToPtr(config, configPtr, false)

                    let mutable init = Unchecked.defaultof<NV_ENC_INITIALIZE_PARAMS>
                    init.version <- NV_ENC_INITIALIZE_PARAMS_VER
                    init.encodeGUID <- NV_ENC_CODEC_HEVC_GUID
                    init.presetGUID <- presetGuid
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
                    init.maxMEHintCountsPerBlock <- Array.zeroCreate 2
                    init.tuningInfo <- tuning
                    init.bufferFormat <- int NV_ENC_BUFFER_FORMAT.ARGB
                    init.reserved1 <- Array.zeroCreate 284
                    init.reserved2 <- Array.zeroCreate 64
                    check "nvEncInitializeEncoder" (api.Initialize.Invoke(encoder, &init))
                finally
                    Marshal.FreeHGlobal configPtr

                // Ring of output bitstream buffers (>= 4) so we can tolerate buffered output.
                let bufCount = 4
                let outputs = Array.zeroCreate<nativeint> bufCount
                for i in 0 .. bufCount - 1 do
                    let mutable bb = Unchecked.defaultof<NV_ENC_CREATE_BITSTREAM_BUFFER>
                    bb.version <- NV_ENC_CREATE_BITSTREAM_BUFFER_VER
                    bb.size <- 0u // let the driver size it
                    bb.memoryHeap <- int NV_ENC_MEMORY_HEAP.AUTOSELECT
                    bb.reserved1 <- Array.zeroCreate 58
                    bb.reserved2 <- Array.zeroCreate 64
                    check "nvEncCreateBitstreamBuffer" (api.CreateBitstream.Invoke(encoder, &bb))
                    outputs.[i] <- bb.bitstreamBuffer

                // Extract VPS/SPS/PPS (Annex B, 4-byte start codes) for the mux's hvcC.
                let vpsSpsPps =
                    let cap = 4096
                    let buffer = Marshal.AllocHGlobal cap
                    let sizePtr = Marshal.AllocHGlobal 4
                    try
                        Marshal.WriteInt32(sizePtr, 0)
                        let mutable sp = Unchecked.defaultof<NV_ENC_SEQUENCE_PARAM_PAYLOAD>
                        sp.version <- NV_ENC_SEQUENCE_PARAM_PAYLOAD_VER
                        sp.inBufferSize <- uint32 cap
                        sp.spsppsBuffer <- buffer
                        sp.outSPSPPSPayloadSize <- sizePtr
                        sp.reserved <- Array.zeroCreate 250
                        sp.reserved2 <- Array.zeroCreate 64
                        let st = api.GetSeqParams.Invoke(encoder, &sp)
                        if st = NVENCSTATUS.NV_ENC_SUCCESS then
                            let n = Marshal.ReadInt32 sizePtr
                            if n > 0 && n <= cap then
                                let bytes = Array.zeroCreate<byte> n
                                Marshal.Copy(buffer, bytes, 0, n)
                                Some bytes
                            else None
                        else None
                    finally
                        Marshal.FreeHGlobal buffer
                        Marshal.FreeHGlobal sizePtr

                let decoderConfig =
                    { VpsSpsPpsAnnexB = vpsSpsPps
                      Chroma = Yuv444
                      BitDepth = 8
                      Width = width
                      Height = height }

                let losslessMode =
                    if lossless then
                        "visually lossless: NVENC HEVC 4:4:4 (FRExt profile) at QP0 with lossless tuning. "
                        + "Input BGRA is converted RGB->YCbCr (BT.709, full range) internally by NVENC, "
                        + "which is not guaranteed bit-exact reversible, so this is not mathematically lossless."
                    else
                        "lossy: NVENC HEVC 4:4:4 quality-tuned encode."

                new Session(api, encoder, width, height, fps, outputs, decoderConfig, losslessMode, false)
            with ex ->
                if encoder <> 0n then api.Destroy.Invoke(encoder) |> ignore
                raise ex

    /// Creates an NVENC HEVC session on the given ID3D11Device.
    let create (d3d11Device: nativeint) (width: int) (height: int) (fps: int) (lossless: bool) (preset: string) : Session =
        Session.Create(d3d11Device, width, height, fps, lossless, preset)
