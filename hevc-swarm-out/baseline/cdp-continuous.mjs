// Drives the continuous-context engine end to end and proves that context
// hot swapping actually occurs, using a real runbook as the telemetry feed.
import { readFileSync } from 'node:fs';

const PORT = process.env.CDP_PORT || 9333;
const PAGE = 'file:///E:/Yumlog/yumlog-manager.html';
const RUNBOOK = process.argv[2] || 'E:\\Yumlog\\hevc-swarm-out\\baseline\\artifacts\\r2-churn2.runbook.json';
const HIGH_WATER = process.env.HIGH_WATER || '20';
const INTERVAL = process.env.INTERVAL || '0';
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
await new Promise((res) => setTimeout(res, 3500));

const json = readFileSync(RUNBOOK, 'utf8');
await evalJs(`
  runbookData = JSON.parse(${JSON.stringify(json)});
  document.getElementById('ccInterval').value = '${INTERVAL}';
  document.getElementById('ccHighWater').value = '${HIGH_WATER}';
  document.getElementById('ccStartBtn').disabled = false;
  'ready'
`);

const started = Date.now();
evalJs('startContinuous()', { userGesture: true }).then(() => {});

let last = '';
while (Date.now() - started < BUDGET_MS) {
  await new Promise((res) => setTimeout(res, 3000));
  const s = await evalJs(`(() => ({
    stream: document.getElementById('ccStream').textContent,
    stats: document.getElementById('ccStats').textContent,
    badge: document.getElementById('ccBadge').textContent,
    running: !document.getElementById('ccStopBtn').disabled,
    engineStats: (typeof ccEngine !== 'undefined' && ccEngine) ? ccEngine.stats : null
  }))()`);
  const v = s.result?.result?.value;
  if (!v) continue;
  if (v.stream !== last) { process.stdout.write(v.stream.slice(last.length)); last = v.stream; }
  if (!v.running && last.length) {
    console.log('\n\n=== badge: ' + v.badge);
    console.log('=== stats: ' + v.stats);
    console.log('=== engineStats: ' + JSON.stringify(v.engineStats));
    console.log('=== elapsed: ' + ((Date.now() - started) / 1000).toFixed(1) + 's');
    clearTimeout(hardStop);
    process.exit(v.engineStats && v.engineStats.swaps > 0 ? 0 : 2);
  }
}
console.log('\nFAIL: continuous run did not finish within budget');
process.exit(1);
