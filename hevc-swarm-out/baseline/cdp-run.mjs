// Full end-to-end: loads a real runbook into the manager page and runs
// analyzeRunbook() with a genuine user gesture, capturing streamed output.
import { readFileSync } from 'node:fs';

const PORT = process.env.CDP_PORT || 9333;
const PAGE = 'file:///E:/Yumlog/yumlog-manager.html';
const RUNBOOK = process.argv[2] || 'E:\\Yumlog\\hevc-swarm-out\\baseline\\artifacts\\r2-churn2.runbook.json';
const BUDGET_MS = Number(process.env.BUDGET_MS || 600000);

const hardStop = setTimeout(() => { console.log('FAIL: hard timeout'); process.exit(1); }, BUDGET_MS + 60000);

const r = await fetch(`http://127.0.0.1:${PORT}/json/new?${encodeURIComponent(PAGE)}`, { method: 'PUT' });
const t = await r.json();
const ws = new WebSocket(t.webSocketDebuggerUrl);
await new Promise((res) => ws.addEventListener('open', res));

let id = 0;
const pending = new Map();
ws.addEventListener('message', (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
});
const send = (method, params = {}) =>
  new Promise((resolve) => { const i = ++id; pending.set(i, resolve); ws.send(JSON.stringify({ id: i, method, params })); });
const evalJs = (expression, opts = {}) =>
  send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, ...opts });

await send('Runtime.enable');
await new Promise((res) => setTimeout(res, 4000));

const avail = await evalJs('LanguageModel.availability()');
console.log('availability:', JSON.stringify(avail.result?.result?.value));

const json = readFileSync(RUNBOOK, 'utf8');
await evalJs(`runbookData = JSON.parse(${JSON.stringify(json)}); document.getElementById('aiRunBtn').disabled = false; 'loaded'`);

// userGesture:true satisfies the Prompt API's activation requirement.
const started = Date.now();
evalJs('analyzeRunbook()', { userGesture: true }).then(() => {});

let last = '';
while (Date.now() - started < BUDGET_MS) {
  await new Promise((res) => setTimeout(res, 5000));
  const s = await evalJs(`(() => ({
    stream: document.getElementById('aiStream').textContent,
    status: document.getElementById('aiStatusText').textContent,
    badge: document.getElementById('aiBadge').textContent,
    running: !document.getElementById('aiCancelBtn').disabled
  }))()`);
  const v = s.result?.result?.value;
  if (!v) continue;
  if (v.stream !== last) {
    process.stdout.write(v.stream.slice(last.length));
    last = v.stream;
  }
  if (!v.running && last.length) {
    console.log('\n\n=== status: ' + v.status);
    console.log('=== badge: ' + v.badge);
    console.log('=== elapsed: ' + ((Date.now() - started) / 1000).toFixed(1) + 's');
    clearTimeout(hardStop);
    process.exit(0);
  }
}
console.log('\nFAIL: analysis did not finish within budget');
process.exit(1);
