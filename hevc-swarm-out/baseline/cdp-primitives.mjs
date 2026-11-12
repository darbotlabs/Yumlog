// Verifies the context primitives needed for continuous hot swapping:
// append() without generation, contextUsage growth, overflow behaviour,
// clone() continuity, and session-creation cost once the model is resident.
const PORT = process.env.CDP_PORT || 9333;
const PAGE = 'file:///E:/Yumlog/yumlog-manager.html';

const hardStop = setTimeout(() => { console.log('FAIL: hard timeout'); process.exit(1); }, 600000);

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
await new Promise((res) => setTimeout(res, 3500));

const script = `(async () => {
  const out = { steps: [] };
  const log = (k, v) => { out.steps.push([k, v]); };

  const t0 = performance.now();
  const S = await LanguageModel.create({
    initialPrompts: [{ role: 'system', content: 'You track screen-recording telemetry.' }]
  });
  out.createMs = Math.round(performance.now() - t0);
  out.contextWindow = S.contextWindow;
  out.usageAfterCreate = S.contextUsage;
  out.hasAppend = typeof S.append === 'function';
  out.hasClone = typeof S.clone === 'function';
  out.hasMeasure = typeof S.measureContextUsage === 'function';

  // 1. append() should load context WITHOUT generating.
  const beforeAppend = S.contextUsage;
  const tA = performance.now();
  await S.append('Telemetry marker ALPHA: at t=1.00s the window title was "Invoice Draft 7781".');
  out.appendMs = Math.round(performance.now() - tA);
  out.usageAfterAppend = S.contextUsage;
  out.appendGrewContext = S.contextUsage > beforeAppend;

  // 2. Repeated appends accumulate.
  for (let i = 0; i < 5; i++) {
    await S.append('Telemetry filler ' + i + ': routine UI redraw with no semantic change observed.');
  }
  out.usageAfter6Appends = S.contextUsage;

  // 3. The appended content must be recallable.
  const tR = performance.now();
  out.recall = String(await S.prompt('What was the window title in marker ALPHA? Answer with just the title.')).slice(0, 160);
  out.recallMs = Math.round(performance.now() - tR);

  // 4. clone() cost and whether it carries the accumulated context.
  const tC = performance.now();
  const C = await S.clone();
  out.cloneMs = Math.round(performance.now() - tC);
  out.cloneUsage = C.contextUsage;
  out.cloneRecall = String(await C.prompt('What was the window title in marker ALPHA? Answer with just the title.')).slice(0, 160);
  C.destroy();

  // 5. Cost of a brand new session while the model is already resident
  //    (this is the hot-swap floor).
  const tN = performance.now();
  const N = await LanguageModel.create({
    initialPrompts: [{ role: 'system', content: 'You track screen-recording telemetry.' }]
  });
  out.freshCreateMs = Math.round(performance.now() - tN);
  out.freshUsage = N.contextUsage;
  out.freshRecall = String(await N.prompt('What was the window title in marker ALPHA? Answer with just the title, or UNKNOWN.')).slice(0, 160);
  N.destroy();

  // 6. overflow event wiring present?
  out.hasOverflowHandler = 'oncontextoverflow' in S;

  // 7. measureContextUsage on a representative telemetry line.
  out.measureOneLine = await S.measureContextUsage('t=0.00s..6.03s (155 frames) | graph=0n/0e');

  S.destroy();
  return out;
})()`;

const res = await send('Runtime.evaluate', { expression: script, awaitPromise: true, returnByValue: true, userGesture: true });
clearTimeout(hardStop);
if (res.result?.exceptionDetails) {
  console.log('EXCEPTION: ' + JSON.stringify(res.result.exceptionDetails, null, 2));
  process.exit(1);
}
console.log(JSON.stringify(res.result?.result?.value, null, 2));
process.exit(0);
