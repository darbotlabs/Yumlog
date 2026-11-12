// Downloads the Edge on-device model into the CDP-attached probe profile and proves
// it generates. Re-runnable: if the model is already present this just reports it.
//
// LanguageModel.create() requires a user gesture, so Runtime.evaluate must pass
// userGesture:true. about:blank has an opaque origin and gates the API off, so a
// real https page is required.
const PORT = process.env.CDP_PORT || 9333;
const BASE = `http://127.0.0.1:${PORT}`;
const PAGE = process.env.PAGE ||
  'https://microsoftedge.github.io/Demos/built-in-ai/playgrounds/prompt-api/';
const BUDGET_MS = Number(process.env.BUDGET_MS || 1_800_000);

async function newTarget(url) {
  const res = await fetch(`${BASE}/json/new?${encodeURIComponent(url)}`, { method: 'PUT' });
  if (!res.ok) throw new Error(`newTarget ${res.status} ${await res.text()}`);
  return res.json();
}

function cdp(wsUrl) {
  const ws = new WebSocket(wsUrl);
  let id = 0;
  const pending = new Map();
  const ready = new Promise((resolve, reject) => {
    ws.addEventListener('open', () => resolve());
    ws.addEventListener('error', (e) => reject(new Error('ws error ' + (e.message || ''))));
  });
  ws.addEventListener('message', (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.method === 'Runtime.consoleAPICalled') {
      const text = (msg.params.args || []).map((a) => a.value ?? a.description ?? '').join(' ');
      if (text) console.log('[page] ' + text);
      return;
    }
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      msg.error ? reject(new Error(JSON.stringify(msg.error))) : resolve(msg.result);
    }
  });
  return {
    ready,
    send(method, params = {}) {
      const myId = ++id;
      return new Promise((resolve, reject) => {
        pending.set(myId, { resolve, reject });
        ws.send(JSON.stringify({ id: myId, method, params }));
      });
    },
    close: () => ws.close()
  };
}

const EXPR = `(async () => {
  const out = { steps: [] };
  if (typeof LanguageModel === 'undefined') {
    out.error = 'LanguageModel undefined - flag not active in this profile';
    return out;
  }
  out.availabilityBefore = await LanguageModel.availability();
  out.steps.push('availability=' + out.availabilityBefore);

  let lastPct = -1;
  const t0 = performance.now();
  const session = await LanguageModel.create({
    monitor(m) {
      m.addEventListener('downloadprogress', (e) => {
        const pct = Math.floor((e.loaded || 0) * 100);
        if (pct !== lastPct && pct % 5 === 0) { lastPct = pct; console.log('download ' + pct + '%'); }
      });
    }
  });
  out.createMs = Math.round(performance.now() - t0);
  out.availabilityAfter = await LanguageModel.availability();
  try { out.inputQuota = session.inputQuota; } catch {}

  const t1 = performance.now();
  out.output = await session.prompt('Reply with exactly: MODEL READY');
  out.promptMs = Math.round(performance.now() - t1);
  session.destroy();
  return out;
})()`;

const target = await newTarget(PAGE);
const client = cdp(target.webSocketDebuggerUrl);
await client.ready;
await client.send('Runtime.enable');
await new Promise((r) => setTimeout(r, 4000));

const timeout = new Promise((_, rej) =>
  setTimeout(() => rej(new Error(`budget ${BUDGET_MS}ms exceeded`)), BUDGET_MS));

let code = 0;
try {
  const res = await Promise.race([
    client.send('Runtime.evaluate', {
      expression: EXPR,
      awaitPromise: true,
      returnByValue: true,
      userGesture: true
    }),
    timeout
  ]);
  if (res.exceptionDetails) {
    console.log('EXCEPTION ' +
      (res.exceptionDetails.exception?.description || JSON.stringify(res.exceptionDetails)));
    code = 1;
  } else {
    console.log('RESULT ' + JSON.stringify(res.result.value, null, 2));
    if (res.result.value?.error) code = 1;
  }
} catch (e) {
  console.log('FAILED ' + e.message);
  code = 1;
} finally {
  client.close();
}
process.exit(code);
