const { invoke } = window.__TAURI__.core;

const readyState = document.querySelector("#ready-state");
const readyDetail = document.querySelector("#ready-detail");
const runtimeOutput = document.querySelector("#runtime-output");
const recognizeOutput = document.querySelector("#recognize-output");
const modelsOutput = document.querySelector("#models-output");
const tracesOutput = document.querySelector("#traces-output");
const imagePath = document.querySelector("#image-path");

function writeJson(element, value) {
  element.textContent = JSON.stringify(value, null, 2);
}

async function refreshAll() {
  const state = await invoke("get_text_recognizer_state");
  readyState.textContent = state.state;
  readyDetail.textContent = state.detail;
  writeJson(runtimeOutput, state);

  writeJson(modelsOutput, await invoke("list_models"));
  writeJson(tracesOutput, await invoke("list_traces"));
}

document.querySelector("#refresh").addEventListener("click", refreshAll);

document.querySelector("#ensure").addEventListener("click", async () => {
  const result = await invoke("ensure_text_recognizer_ready");
  writeJson(runtimeOutput, result);
  await refreshAll();
});

document.querySelector("#probe").addEventListener("click", async () => {
  writeJson(runtimeOutput, await invoke("probe_container_runtime"));
});

document.querySelector("#recognize").addEventListener("click", async () => {
  const path = imagePath.value.trim();
  if (!path) {
    recognizeOutput.textContent = "Enter an image path.";
    return;
  }

  const result = await invoke("recognize_text", {
    request: {
      imagePath: path,
      mode: "npu-emulated-cpu",
      trace: true
    }
  });
  writeJson(recognizeOutput, result);
  await refreshAll();
});

refreshAll().catch((error) => {
  readyState.textContent = "Error";
  readyDetail.textContent = String(error);
});
