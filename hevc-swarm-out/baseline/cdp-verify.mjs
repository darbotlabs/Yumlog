// Loads yumlog-manager.html in the probe browser and reports the mdkg
// Intelligence panel's detected state plus any console errors.
const PORT = process.env.CDP_PORT || 9333;
const URL_ = process.env.PAGE_URL || 'file:///E:/Yumlog/yumlog-manager.html';

const fail = (msg) => { console.log('FAIL: ' + msg); process.exit(1); };
const timer = setTimeout(() => fail('timed out after 45s'), 45000);

let ws;
try {
  const r = await fetch(`http://127.0.0.1:${PORT}/json/new?${encodeURIComponent(URL_)}`, { method: 'PUT' });
  const t = await r.json();
  ws = new WebSocket(t.webSocketDebuggerUrl);
} catch (e) {
  fail('cannot reach CDP on port ' + PORT + ': ' + e.message);
}

await new Promise((res, rej) => {
  ws.addEventListener('open', res);
  ws.addEventListener('error', () => rej(new Error('ws error')));
}).catch((e) => fail(e.message));

let id = 0;
const pending = new Map();
const consoleErrors = [];
ws.addEventListener('message', (ev) => {
  const m = JSON.parse(ev.data);
  if (m.method === 'Runtime.exceptionThrown') {
    consoleErrors.push(m.params?.exceptionDetails?.text + ' ' + (m.params?.exceptionDetails?.exception?.description || ''));
  }
  if (m.method === 'Runtime.consoleAPICalled' && m.params?.type === 'error') {
    consoleErrors.push(m.params.args.map((a) => a.value || a.description).join(' '));
  }
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
});
const send = (method, params = {}) =>
  new Promise((resolve) => { const i = ++id; pending.set(i, resolve); ws.send(JSON.stringify({ id: i, method, params })); });

await send('Runtime.enable');
await send('Page.enable');
await new Promise((res) => setTimeout(res, 5000));

const res = await send('Runtime.evaluate', {
  expression: `(() => {
    const g = (x) => document.getElementById(x);
    const has = (x) => !!g(x);
    return {
      languageModelDefined: typeof LanguageModel !== 'undefined',
      badge: g('aiBadge') && g('aiBadge').textContent,
      statusText: g('aiStatusText') && g('aiStatusText').textContent,
      streamText: g('aiStream') && g('aiStream').textContent.slice(0, 120),
      runBtnDisabled: g('aiRunBtn') && g('aiRunBtn').disabled,
      cancelBtnDisabled: g('aiCancelBtn') && g('aiCancelBtn').disabled,
      elementsPresent: ['aiStatus','aiBadge','aiStatusText','mdkgExportCmd','mdkgFile','mdkgFileInfo','aiQuestion','aiRunBtn','aiCancelBtn','aiProgressWrap','aiProgressBar','aiStream'].filter(x => !has(x)),
      fnsPresent: ['loadRunbookJson','analyzeRunbook','cancelAnalysis','compactSteps','packChunks','detectOnDeviceModel','generateRecordCommand','loadStatistics','loadConfig'].filter(f => typeof window[f] !== 'function'),
      panelHeading: [...document.querySelectorAll('h2')].map(h => h.textContent.trim()).filter(t => /mdkg/i.test(t))[0] || null
    };
  })()`,
  awaitPromise: true, returnByValue: true,
});

clearTimeout(timer);
console.log(JSON.stringify(res.result?.result?.value ?? res.result, null, 2));
console.log('consoleErrors: ' + JSON.stringify(consoleErrors, null, 2));
process.exit(0);
