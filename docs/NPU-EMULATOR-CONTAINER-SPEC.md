# NPU Emulator Container App

## Thesis

Yumlog's Windows AI work should focus on the hard runtime problem: an adaptive, inspectable execution layer that mirrors NPU-gated workload semantics on CPU-native infrastructure. The NPU Emulator Container app is not a generic OCR fallback. It is a research and product surface for modeling the lifecycle that Windows AI hides behind `systemAIModels`: feature discovery, model readiness, provisioning, execution target selection, trace capture, and output projection.

The first implementation lives at:

```text
apps\Yumlog.NpuEmulator
```

It is a native Rust/Tauri app with a Rust state machine and Tauri command surface.

## Design constraints

- Do not patch, shim, or bypass protected Windows AI APIs.
- Treat CPU-native execution as an emulated NPU target named `npu-emulated-cpu`.
- Make state transitions explicit and traceable.
- Keep the runtime local and inspectable.
- Preserve compatibility with Yumlog's existing OCR result shape: provider, availability, text, lines, words, polygon bounds, confidence, and message.

## Runtime layers

```text
Tauri UI
  |
Rust command surface
  |
EmulatorRuntime state machine
  |-- feature registry
  |-- provisioning state
  |-- model registry
  |-- trace journal
  |-- container runtime probe
  |
Future worker bridge
  |-- container worker
  |-- OCR graph/session runtime
  |-- tensor/input normalization
  |-- output projection
```

## Feature state model

The app mirrors Windows AI feature readiness as a product contract:

| State | Emulator meaning |
|---|---|
| `Ready` | Worker image/runtime is available and the requested emulated model session is warm. |
| `NotReady` | Feature is known, but provisioning has not completed. |
| `NotSupportedOnCurrentSystem` | Host cannot run the configured local runtime/container path. |
| `DisabledByUser` | Feature was explicitly disabled by local emulator policy. |
| `CapabilityMissing` | Feature is not registered in the emulator registry. |
| `NotCompatibleWithSystemHardware` | Selected emulation profile cannot satisfy required semantics. |
| `OSUpdateNeeded` | Host runtime dependencies are too old for the requested profile. |

Provisioning status is separate:

| Status | Meaning |
|---|---|
| `Idle` | No provisioning attempt started. |
| `ResolvingPackage` | Runtime is resolving a model package/worker artifact. |
| `PullingContainer` | Runtime is acquiring or preparing the worker image. |
| `LoadingModel` | Worker is creating the model/session. |
| `WarmingSession` | Worker is running readiness probes. |
| `Ready` | Feature is usable. |
| `Failed` | Provisioning failed with a traceable diagnostic. |

## Current Tauri commands

| Command | Purpose |
|---|---|
| `get_features` | Return registered emulated features. |
| `get_text_recognizer_state` | Return current TextRecognizer-style feature state. |
| `ensure_text_recognizer_ready` | Advance readiness by probing container support and recording provisioning state. |
| `probe_container_runtime` | Probe Docker availability and version. |
| `list_models` | Return registered emulator model profiles. |
| `list_traces` | Return the in-memory trace journal. |
| `recognize_text` | Accept OCR requests under the `npu-emulated-cpu` contract and record a trace. |

## Current implementation status

Implemented:

- Rust/Tauri app scaffold.
- Native UI dashboard.
- Feature state DTOs.
- Model registry DTOs.
- Trace DTOs.
- Container runtime probe.
- Readiness command.
- Recognition request validation and trace acceptance.
- Unit tests for default state, state transitions, and trace journal.

Not yet implemented:

- Worker container image.
- Worker RPC protocol.
- Real OCR graph/session execution.
- Persistent trace database.
- Local HTTP/named-pipe bridge for `Yumlog.Native.Cli`.
- `--ocr npu-emulator` mode in the F# CLI.

## Future worker protocol

The worker should expose a small local API:

```http
GET  /health
POST /v1/text-recognizer/load
POST /v1/text-recognizer/recognize
GET  /v1/traces/{traceId}
```

Recognition request:

```json
{
  "imagePath": "E:\\Yumlog\\screenshots\\frame.png",
  "mode": "npu-emulated-cpu",
  "trace": true
}
```

Recognition response:

```json
{
  "provider": "npu-emulator.cpu.ocr",
  "isAvailable": true,
  "message": "OK",
  "text": "recognized text",
  "lines": [
    {
      "text": "recognized text",
      "words": [
        {
          "text": "recognized",
          "confidence": 0.94,
          "boundingBox": {
            "topLeft": { "x": 10, "y": 20 },
            "topRight": { "x": 120, "y": 20 },
            "bottomRight": { "x": 120, "y": 42 },
            "bottomLeft": { "x": 10, "y": 42 }
          }
        }
      ]
    }
  ],
  "traceId": "trace-..."
}
```

## Integration path

1. Add a Rust bridge server to `Yumlog.NpuEmulator`.
2. Add `--ocr npu-emulator` to `Yumlog.Native.Cli`.
3. Teach `Ocr.fs` to call the bridge and map response DTOs into `OcrResult`.
4. Persist traces in SQLite or MDKG sidecar metadata.
5. Build the worker container and wire actual CPU-native graph execution.
6. Compare emulator traces with real Windows AI/NPU traces on compatible hardware.

## Validation

Rust validation:

```powershell
cd apps\Yumlog.NpuEmulator\src-tauri
cargo fmt --check
cargo test
cargo check
```

NPM/Tauri CLI validation:

```powershell
cd apps\Yumlog.NpuEmulator
npm install
npm run tauri -- --version
```
