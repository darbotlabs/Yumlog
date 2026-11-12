// Exercises the mdkg Intelligence pure logic (compactSteps + runbook load)
// against a real Yumlog runbook JSON, without requiring the model.
import { readFileSync } from 'node:fs';

const PORT = process.env.CDP_PORT || 9333;
const PAGE = 'file:///E:/Yumlog/yumlog-manager.html';
const RUNBOOK = process.argv[2] || 'E:\\Yumlog\\hevc-swarm-out\\baseline\\artifacts\\r2-churn2.runbook.json';

const timer = setTimeout(() => { console.log('FAIL: timeout'); process.exit(1); }, 60000);

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

await send('Runtime.enable');
await new Promise((res) => setTimeout(res, 4000));

const json = readFileSync(RUNBOOK, 'utf8');

const res = await send('Runtime.evaluate', {
  expression: `(() => {
    const parsed = JSON.parse(${JSON.stringify(json)});
    runbookData = parsed;
    const lines = compactSteps(parsed.steps);
    const totalChars = lines.reduce((n, l) => n + l.length, 0);
    const rawChars = JSON.stringify(parsed.steps).length;
    return {
      steps: parsed.steps.length,
      durationSeconds: parsed.durationSeconds,
      compactedSegments: lines.length,
      rawStepsChars: rawChars,
      compactedChars: totalChars,
      reductionPct: +(100 * (1 - totalChars / rawChars)).toFixed(2),
      firstSegment: lines[0] || null,
      lastSegment: lines[lines.length - 1] || null,
      schemaKeys: Object.keys(AI_SCHEMA.properties),
      cancelIsFn: typeof cancelAnalysis === 'function'
    };
  })()`,
  awaitPromise: true, returnByValue: true,
});

clearTimeout(timer);
const d = res.result?.result?.value;
if (!d) { console.log('FAIL: ' + JSON.stringify(res.result, null, 2)); process.exit(1); }
console.log(JSON.stringify(d, null, 2));
process.exit(0);
