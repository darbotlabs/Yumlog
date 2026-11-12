use serde::{Deserialize, Serialize};
use std::{
    path::Path,
    process::Command,
    sync::{Mutex, MutexGuard},
    time::{SystemTime, UNIX_EPOCH},
};

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "PascalCase")]
pub enum FeatureReadyState {
    Ready,
    NotReady,
    NotSupportedOnCurrentSystem,
    DisabledByUser,
    CapabilityMissing,
    NotCompatibleWithSystemHardware,
    OSUpdateNeeded,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum ProvisioningStatus {
    Idle,
    ResolvingPackage,
    PullingContainer,
    LoadingModel,
    WarmingSession,
    Ready,
    Failed,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FeatureState {
    pub name: String,
    pub state: FeatureReadyState,
    pub provisioning_status: ProvisioningStatus,
    pub detail: String,
    pub target: String,
    pub worker_image: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModelInfo {
    pub id: String,
    pub role: String,
    pub target: String,
    pub worker_image: String,
    pub status: ProvisioningStatus,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ContainerRuntimeProbe {
    pub runtime: String,
    pub available: bool,
    pub version: Option<String>,
    pub detail: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecognizeRequest {
    pub image_path: String,
    pub mode: String,
    pub trace: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TextPoint {
    pub x: f64,
    pub y: f64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TextBounds {
    pub top_left: TextPoint,
    pub top_right: TextPoint,
    pub bottom_right: TextPoint,
    pub bottom_left: TextPoint,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecognizedWord {
    pub text: String,
    pub confidence: f64,
    pub bounding_box: TextBounds,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecognizedLine {
    pub text: String,
    pub words: Vec<RecognizedWord>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecognizeResult {
    pub provider: String,
    pub is_available: bool,
    pub message: String,
    pub text: String,
    pub lines: Vec<RecognizedLine>,
    pub trace_id: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TraceRecord {
    pub id: String,
    pub kind: String,
    pub timestamp_unix_ms: u128,
    pub state: FeatureReadyState,
    pub detail: String,
    pub image_path: Option<String>,
}

#[derive(Debug)]
pub struct EmulatorRuntime {
    feature: FeatureState,
    models: Vec<ModelInfo>,
    traces: Vec<TraceRecord>,
}

impl EmulatorRuntime {
    fn new() -> Self {
        let worker_image = "darbotlabs/npu-emulator-worker:cpu-ocr".to_string();
        Self {
            feature: FeatureState {
                name: "TextRecognizer".to_string(),
                state: FeatureReadyState::NotReady,
                provisioning_status: ProvisioningStatus::Idle,
                detail: "Emulated NPU worker has not been provisioned.".to_string(),
                target: "npu-emulated-cpu".to_string(),
                worker_image: worker_image.clone(),
            },
            models: vec![ModelInfo {
                id: "text-recognizer-ocr-v1".to_string(),
                role: "ocr".to_string(),
                target: "npu-emulated-cpu".to_string(),
                worker_image,
                status: ProvisioningStatus::Idle,
            }],
            traces: Vec::new(),
        }
    }

    fn record_trace(
        &mut self,
        kind: impl Into<String>,
        detail: impl Into<String>,
        image_path: Option<String>,
    ) -> String {
        let id = format!("trace-{}", now_ms());
        self.traces.push(TraceRecord {
            id: id.clone(),
            kind: kind.into(),
            timestamp_unix_ms: now_ms(),
            state: self.feature.state.clone(),
            detail: detail.into(),
            image_path,
        });
        id
    }

    fn set_status(&mut self, state: FeatureReadyState, status: ProvisioningStatus, detail: String) {
        self.feature.state = state;
        self.feature.provisioning_status = status.clone();
        self.feature.detail = detail;
        for model in &mut self.models {
            model.status = status.clone();
        }
    }
}

fn now_ms() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .unwrap_or_default()
}

fn lock_runtime<'a>(
    state: &'a tauri::State<'_, Mutex<EmulatorRuntime>>,
) -> Result<MutexGuard<'a, EmulatorRuntime>, String> {
    state
        .lock()
        .map_err(|_| "Emulator runtime state lock was poisoned.".to_string())
}

fn probe_docker() -> ContainerRuntimeProbe {
    match Command::new("docker").arg("--version").output() {
        Ok(output) if output.status.success() => {
            let version = String::from_utf8_lossy(&output.stdout).trim().to_string();
            ContainerRuntimeProbe {
                runtime: "docker".to_string(),
                available: true,
                version: Some(version.clone()),
                detail: version,
            }
        }
        Ok(output) => ContainerRuntimeProbe {
            runtime: "docker".to_string(),
            available: false,
            version: None,
            detail: String::from_utf8_lossy(&output.stderr).trim().to_string(),
        },
        Err(error) => ContainerRuntimeProbe {
            runtime: "docker".to_string(),
            available: false,
            version: None,
            detail: error.to_string(),
        },
    }
}

#[tauri::command]
fn get_features(
    state: tauri::State<'_, Mutex<EmulatorRuntime>>,
) -> Result<Vec<FeatureState>, String> {
    let runtime = lock_runtime(&state)?;
    Ok(vec![runtime.feature.clone()])
}

#[tauri::command]
fn get_text_recognizer_state(
    state: tauri::State<'_, Mutex<EmulatorRuntime>>,
) -> Result<FeatureState, String> {
    Ok(lock_runtime(&state)?.feature.clone())
}

#[tauri::command]
fn ensure_text_recognizer_ready(
    state: tauri::State<'_, Mutex<EmulatorRuntime>>,
) -> Result<FeatureState, String> {
    let probe = probe_docker();
    let mut runtime = lock_runtime(&state)?;

    if probe.available {
        let worker_image = runtime.feature.worker_image.clone();
        runtime.set_status(
            FeatureReadyState::NotReady,
            ProvisioningStatus::PullingContainer,
            format!(
                "Container runtime is available. Worker image {} is the next provisioning artifact.",
                worker_image
            ),
        );
    } else {
        runtime.set_status(
            FeatureReadyState::NotSupportedOnCurrentSystem,
            ProvisioningStatus::Failed,
            format!("Container runtime probe failed: {}", probe.detail),
        );
    }

    let detail = runtime.feature.detail.clone();
    runtime.record_trace("ensure-ready", detail, None);
    Ok(runtime.feature.clone())
}

#[tauri::command]
fn probe_container_runtime() -> ContainerRuntimeProbe {
    probe_docker()
}

#[tauri::command]
fn list_models(state: tauri::State<'_, Mutex<EmulatorRuntime>>) -> Result<Vec<ModelInfo>, String> {
    Ok(lock_runtime(&state)?.models.clone())
}

#[tauri::command]
fn list_traces(
    state: tauri::State<'_, Mutex<EmulatorRuntime>>,
) -> Result<Vec<TraceRecord>, String> {
    Ok(lock_runtime(&state)?.traces.clone())
}

#[tauri::command]
fn recognize_text(
    state: tauri::State<'_, Mutex<EmulatorRuntime>>,
    request: RecognizeRequest,
) -> Result<RecognizeResult, String> {
    if request.mode != "npu-emulated-cpu" {
        return Err(format!(
            "Unsupported emulator mode '{}'. Use npu-emulated-cpu.",
            request.mode
        ));
    }

    if !Path::new(&request.image_path).exists() {
        return Err(format!("Image file does not exist: {}", request.image_path));
    }

    let mut runtime = lock_runtime(&state)?;
    let trace_id = request.trace.then(|| {
        runtime.record_trace(
            "recognize",
            "Recognition request accepted.",
            Some(request.image_path.clone()),
        )
    });

    if runtime.feature.state != FeatureReadyState::Ready {
        return Ok(RecognizeResult {
            provider: "npu-emulator.cpu.ocr".to_string(),
            is_available: false,
            message: format!(
                "Emulated NPU worker is not ready. Current state: {:?}.",
                runtime.feature.state
            ),
            text: String::new(),
            lines: Vec::new(),
            trace_id,
        });
    }

    Ok(RecognizeResult {
        provider: "npu-emulator.cpu.ocr".to_string(),
        is_available: false,
        message: "Worker RPC execution is not wired yet.".to_string(),
        text: String::new(),
        lines: Vec::new(),
        trace_id,
    })
}

pub fn run() {
    tauri::Builder::default()
        .manage(Mutex::new(EmulatorRuntime::new()))
        .invoke_handler(tauri::generate_handler![
            get_features,
            get_text_recognizer_state,
            ensure_text_recognizer_ready,
            probe_container_runtime,
            list_models,
            list_traces,
            recognize_text
        ])
        .run(tauri::generate_context!())
        .expect("failed to run Yumlog NPU Emulator");
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_runtime_starts_not_ready() {
        let runtime = EmulatorRuntime::new();
        assert_eq!(runtime.feature.state, FeatureReadyState::NotReady);
        assert_eq!(runtime.feature.target, "npu-emulated-cpu");
        assert_eq!(runtime.models.len(), 1);
    }

    #[test]
    fn set_status_updates_model_status() {
        let mut runtime = EmulatorRuntime::new();
        runtime.set_status(
            FeatureReadyState::Ready,
            ProvisioningStatus::Ready,
            "warm".to_string(),
        );
        assert_eq!(runtime.feature.state, FeatureReadyState::Ready);
        assert_eq!(runtime.models[0].status, ProvisioningStatus::Ready);
    }

    #[test]
    fn trace_ids_are_recorded() {
        let mut runtime = EmulatorRuntime::new();
        let id = runtime.record_trace("probe", "detail", None);
        assert_eq!(runtime.traces.len(), 1);
        assert_eq!(runtime.traces[0].id, id);
    }
}
