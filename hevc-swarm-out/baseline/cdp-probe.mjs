// Minimal CDP driver: opens a page, evaluates JS, prints JSON.
// Used to inspect Edge built-in AI flags and model status from an isolated profile.
const PORT = process.env.CDP_PORT || 9333;
const BASE = `http://127.0.0.1:${PORT}`;

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
    ws.addEventListener('error', (e) => reject(new Error('ws error ' + e.message)));
  });
  ws.addEventListener('message', (ev) => {
    const msg = JSON.parse(ev.data);
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

async function evaluateOn(url, expression, waitMs = 2500) {
  const target = await newTarget(url);
  const client = cdp(target.webSocketDebuggerUrl);
  await client.ready;
  await client.send('Runtime.enable');
  await new Promise((r) => setTimeout(r, waitMs));
  const res = await client.send('Runtime.evaluate', {
    expression,
    awaitPromise: true,
    returnByValue: true
  });
  client.close();
  if (res.exceptionDetails) {
    return { __error: res.exceptionDetails.exception?.description || JSON.stringify(res.exceptionDetails) };
  }
  return res.result.value;
}

const [, , mode] = process.argv;

// Deep shadow-DOM traversal: flag rows live inside nested shadow roots on edge://flags.
const FLAG_SCAN = `(() => {
  const hits = [];
  const seen = new Set();
  function walk(root, depth) {
    if (!root || depth > 12) return;
    let els;
    try { els = root.querySelectorAll('*'); } catch { return; }
    for (const el of els) {
      if (seen.has(el)) continue;
      seen.add(el);
      const txt = (el.textContent || '').trim();
      const id = el.getAttribute && el.getAttribute('id');
      if (id && txt && txt.length < 900) {
        const t = txt.toLowerCase();
        if (t.includes('prompt api') || t.includes('on-device language model') ||
            t.includes('on device ai model') || t.includes('built-in ai') ||
            t.includes('writing assistance') || t.includes('summarizer')) {
          hits.push({ id, tag: el.tagName.toLowerCase(), text: txt.slice(0, 240).replace(/\\s+/g, ' ') });
        }
      }
      if (el.shadowRoot) walk(el.shadowRoot, depth + 1);
    }
  }
  walk(document, 0);
  const byId = new Map();
  for (const h of hits) if (!byId.has(h.id)) byId.set(h.id, h);
  return { count: byId.size, flags: [...byId.values()] };
})()`;

const MODEL_PROBE = `(async () => {
  const out = {
    ua: navigator.userAgent,
    hasLanguageModel: typeof LanguageModel !== 'undefined',
    hasSummarizer: typeof Summarizer !== 'undefined',
    hasWriter: typeof Writer !== 'undefined',
    hasRewriter: typeof Rewriter !== 'undefined',
    hasProofreader: typeof Proofreader !== 'undefined',
    hasTranslator: typeof Translator !== 'undefined',
    hasLanguageDetector: typeof LanguageDetector !== 'undefined'
  };
  if (typeof LanguageModel !== 'undefined') {
    try { out.availability = await LanguageModel.availability(); } catch (e) { out.availabilityError = String(e); }
    try {
      const p = await LanguageModel.params();
      out.params = { defaultTopK: p.defaultTopK, maxTopK: p.maxTopK,
                     defaultTemperature: p.defaultTemperature, maxTemperature: p.maxTemperature };
    } catch (e) { out.paramsError = String(e); }
  }
  return out;
})()`;

const INTERNALS = `(() => {
  const out = { url: location.href, texts: [] };
  const seen = new Set();
  function walk(root, depth) {
    if (!root || depth > 12) return;
    let els;
    try { els = root.querySelectorAll('*'); } catch { return; }
    for (const el of els) {
      if (seen.has(el)) continue;
      seen.add(el);
      if (el.children.length === 0) {
        const t = (el.textContent || '').trim().replace(/\\s+/g, ' ');
        if (t && t.length < 200) out.texts.push(t);
      }
      if (el.shadowRoot) walk(el.shadowRoot, depth + 1);
    }
  }
  walk(document, 0);
  out.texts = [...new Set(out.texts)];
  return out;
})()`;

const map = { flags: ['edge://flags', FLAG_SCAN, 4000],
              model: ['https://microsoftedge.github.io/Demos/built-in-ai/playgrounds/prompt-api/', MODEL_PROBE, 3000],
              internals: ['edge://on-device-internals', INTERNALS, 4000] };

const [url, expr, wait] = map[mode] || map.flags;
console.log(JSON.stringify(await evaluateOn(url, expr, wait), null, 2));
process.exit(0);
