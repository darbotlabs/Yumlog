# Yumlog NPU Emulator Container

Yumlog NPU Emulator Container is a native Rust/Tauri app for building an inspectable CPU-native runtime layer that mirrors the lifecycle of NPU-gated Windows AI workloads. It is intentionally framed as an emulated NPU harness, not as a fallback OCR path.

The app owns the hard problem Yumlog needs for adaptive intelligence architecture work: feature readiness, model/package provisioning, containerized execution, trace capture, and output projection into Yumlog-compatible OCR records.

## MVP scope

- Native Tauri desktop shell.
- Rust runtime state machine for `systemAIModels`-style readiness.
- First-class `npu-emulated-cpu` target.
- Container worker probe and launch metadata.
- Trace records for readiness and recognition attempts.
- Tauri command surface that Yumlog.Native can call through a local bridge in a later phase.

## Development

```powershell
cd apps\Yumlog.NpuEmulator
npm install
npm run tauri dev
```

Rust-only validation:

```powershell
cd apps\Yumlog.NpuEmulator\src-tauri
cargo test
cargo check
```

## Runtime model

The emulator mirrors Windows AI feature states:

| State | Meaning in emulator |
|---|---|
| `Ready` | Worker image/runtime is available and a model session is warm. |
| `NotReady` | Capability is known, but the worker/model has not been provisioned. |
| `NotSupportedOnCurrentSystem` | Host cannot run the configured container/runtime path. |
| `DisabledByUser` | The feature was explicitly disabled in emulator config. |
| `CapabilityMissing` | The emulated feature is not registered in the local registry. |
| `NotCompatibleWithSystemHardware` | The selected emulation profile cannot satisfy required execution semantics. |
| `OSUpdateNeeded` | Host runtime dependencies are too old for the requested profile. |

## Contract

The UI currently calls Tauri commands directly. A local HTTP or named-pipe bridge should be added once Yumlog.Native integrates with the emulator.

Primary commands:

- `get_features`
- `get_text_recognizer_state`
- `ensure_text_recognizer_ready`
- `recognize_text`
- `probe_container_runtime`
- `list_models`
- `list_traces`

## Why this exists

Windows AI `TextRecognizer` exposes an opaque NPU-gated API. Yumlog needs an adaptive runtime layer that can observe and reproduce the harness shape on CPU-native infrastructure for research and validation. This app is the place to build that layer directly.
