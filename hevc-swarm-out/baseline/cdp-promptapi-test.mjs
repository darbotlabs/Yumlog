// Exercises the Edge built-in Prompt API against a LIVE https page in the probe
// profile, and records what the API actually does rather than what the docs claim.
//
// The documented behaviours under test:
//   1. LanguageModel presence + availability()
//   2. create() on a real (non-opaque) origin
//   3. system prompt via initialPrompts[0]
//   4. system prompt at a non-zero index must reject with TypeError
//   5. responseConstraint with a JSON schema
//   6. responseConstraint with a regular expression
//   7. clone() preserves options but drops conversation history
//   8. the grounding experiment: does responseConstraint prevent a wrong FACT,
//      or only enforce a well-formed SHAPE?
//
// Usage: node cdp-promptapi-test.mjs [port] [url] [probeFilter]
//   port         CDP port of the probe browser (default 9333)
//   url          page to probe (default https://example.com/)
//   probeFilter  comma-separated probe allowlist; use it to skip the
//                constraint-* probes, which crash the on-device model service
//
// Uses node's global WebSocket (WHATWG API), matching cdp-download.mjs, so the
// harness needs no dependencies.

const PORT = process.argv[2] ? Number(process.argv[2]) : 9333;
const URL = process.argv[3] ?? 'https://example.com/';
// Optional comma-separated probe filter. The responseConstraint probes crash the
// on-device model service, so being able to run a safe subset matters.
const ONLY = process.argv[4] ? new Set(process.argv[4].split(',').map((s) => s.trim())) : null;
const BUDGET_MS = 15 * 60 * 1000;

async function newTarget() {
  const res = await fetch(`http://127.0.0.1:${PORT}/json/new?${encodeURIComponent(URL)}`, {
    method: 'PUT',
  });
  if (!res.ok) throw new Error(`/json/new failed: ${res.status} ${await res.text()}`);
  return res.json();
}

function cdp(wsUrl) {
  const ws = new WebSocket(wsUrl);
  let id = 0;
  const pending = new Map();
  const listeners = new Map();
  const ready = new Promise((resolve, reject) => {
    ws.addEventListener('open', () => resolve());
    ws.addEventListener('error', (e) => reject(new Error('ws error ' + (e.message || ''))));
  });
  ws.addEventListener('message', (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.method === 'Runtime.consoleAPICalled') {
      const text = (msg.params.args || []).map((a) => a.value ?? a.description ?? '').join(' ');
      if (text) console.log('    [page] ' + text);
      return;
    }
    if (msg.method) {
      const handlers = listeners.get(msg.method);
      if (handlers) for (const h of handlers) h(msg.params);
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
    on(method, handler) {
      if (!listeners.has(method)) listeners.set(method, []);
      listeners.get(method).push(handler);
    },
    close: () => ws.close(),
  };
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// A destroyed execution context and a genuine API error are entirely different
// findings, and the only thing distinguishing them on the wire is this text.
// Matching it explicitly is what stops a redirect from being misreported as a
// model defect.
const RACE = /Cannot find context with specified id|uniqueContextId not found|Inspected target navigated or closed|Execution context was destroyed|Target closed/i;

// Every probe runs inside the page so it sees the real origin and the real API.
// Pinning uniqueContextId is the load-bearing part: if the document was replaced
// between settling and evaluating, CDP fails the call outright instead of
// silently running the probe against a different origin than the one reported.
async function evaluate(send, expression, uniqueContextId) {
  let result;
  try {
    result = await send('Runtime.evaluate', {
      expression,
      awaitPromise: true,
      returnByValue: true,
      userGesture: true, // LanguageModel.create() requires a user gesture
      ...(uniqueContextId ? { uniqueContextId } : {}),
    });
  } catch (err) {
    if (RACE.test(String(err))) return { raced: true, detail: String(err) };
    throw err;
  }
  if (result.exceptionDetails) {
    const detail = JSON.stringify(result.exceptionDetails.exception ?? {});
    if (RACE.test(detail)) return { raced: true, detail };
    return { threw: true, detail };
  }
  return result.result.value;
}

// These crash the on-device model service and latch a circuit breaker that only
// resets on browser restart. Retrying one does not recover it, it burns the
// remaining budget of the whole run.
const NO_RETRY = new Set([
  'constraint-json-schema',
  'constraint-regex',
  'grounding-vs-constraint',
  'constraint-isolated',
]);

const PROBES = [];
const probe = (name, expression, note) => PROBES.push({ name, expression, note });

probe('api-present', `(() => typeof LanguageModel !== 'undefined')()`);

probe('availability', `(async () => await LanguageModel.availability())()`);

probe(
  'params',
  `(async () => {
     if (!LanguageModel.params) return 'params() not implemented';
     return await LanguageModel.params();
   })()`
);

probe(
  'plain-prompt',
  `(async () => {
     const s = await LanguageModel.create();
     const out = await s.prompt('Reply with exactly one word: ok');
     s.destroy();
     return out;
   })()`
);

probe(
  'system-prompt-index0',
  `(async () => {
     const s = await LanguageModel.create({
       initialPrompts: [{ role: 'system', content: 'Always answer with a single uppercase word.' }]
     });
     const out = await s.prompt('Name the colour of a clear midday sky.');
     s.destroy();
     return out;
   })()`,
  'documented: system must be at position 0'
);

probe(
  'system-prompt-index1-should-reject',
  `(async () => {
     try {
       await LanguageModel.create({
         initialPrompts: [
           { role: 'user', content: 'hi' },
           { role: 'system', content: 'You are a helpful assistant.' }
         ]
       });
       return 'NO ERROR (docs say this must reject with TypeError)';
     } catch (e) {
       return e.constructor.name + ': ' + e.message;
     }
   })()`,
  'negative control for the documented TypeError'
);

probe(
  'constraint-json-schema',
  `(async () => {
     const s = await LanguageModel.create();
     const schema = {
       type: 'object',
       required: ['sentiment', 'confidence'],
       additionalProperties: false,
       properties: {
         sentiment: { type: 'string', enum: ['positive', 'negative', 'neutral'] },
         confidence: { type: 'number', minimum: 0, maximum: 1 }
       }
     };
     const out = await s.prompt('The food was cold and the staff were rude.', { responseConstraint: schema });
     s.destroy();
     let parsed = null;
     try { parsed = JSON.parse(out); } catch (e) { parsed = 'UNPARSEABLE'; }
     return { raw: out, parsed };
   })()`
);

probe(
  'constraint-regex',
  `(async () => {
     const s = await LanguageModel.create();
     const out = await s.prompt('How many days are in a week? Answer with digits only.', {
       responseConstraint: /^[0-9]{1,3}$/
     });
     s.destroy();
     return { raw: out, matches: /^[0-9]{1,3}$/.test(out) };
   })()`
);

probe(
  'clone-drops-history',
  `(async () => {
     const s = await LanguageModel.create({
       initialPrompts: [{ role: 'system', content: 'Answer tersely.' }]
     });
     await s.prompt('Remember this secret codeword: BANANA.');
     const c = await s.clone();
     const cloned = await c.prompt('What was the secret codeword I just told you?');
     const same = await s.prompt('What was the secret codeword I just told you?');
     s.destroy(); c.destroy();
     return { cloneRemembers: cloned, originalRemembers: same };
   })()`,
  'clone should keep options but NOT conversation'
);

// The experiment that actually matters for Yumlog: constrained decoding guarantees
// SHAPE. It cannot guarantee TRUTH. Feed real telemetry and a deliberately
// misleading question, and see whether the constraint rescues the fact.
probe(
  'grounding-vs-constraint',
  `(async () => {
     const telemetry = [
       'capture frame rate=15',
       'frames dropped by the capture pipeline=0',
       'total frames=61',
       'container=mp4, codec=hevc, chroma=yuv444p'
     ].join('\\n');
     const schema = {
       type: 'object',
       required: ['fps', 'droppedFrames'],
       additionalProperties: false,
       properties: { fps: { type: 'number' }, droppedFrames: { type: 'number' } }
     };
     const ask = 'Telemetry:\\n' + telemetry +
       '\\n\\nThis 30 fps recording: what was the capture frame rate and how many frames were dropped? Use only the telemetry.';

     const a = await LanguageModel.create();
     const unconstrained = await a.prompt(ask);
     a.destroy();

     const b = await LanguageModel.create();
     const constrained = await b.prompt(ask, { responseConstraint: schema });
     b.destroy();

     return { unconstrained, constrained, truth: { fps: 15, droppedFrames: 0 } };
   })()`,
  'leading question says 30; telemetry says 15'
);

probe(
  'opaque-origin-control',
  `(async () => {
     // A sandboxed iframe has an opaque origin. The parent cannot read across it,
     // so the child must report on itself via postMessage.
     const html = "<script>parent.postMessage({" +
       "origin: String(location.origin)," +
       "hasApi: typeof LanguageModel !== 'undefined'" +
       "}, '*');<\\/script>";
     const f = document.createElement('iframe');
     f.setAttribute('sandbox', 'allow-scripts');
     f.srcdoc = html;
     const got = new Promise(res => {
       const h = (e) => { window.removeEventListener('message', h); res(e.data); };
       window.addEventListener('message', h);
       setTimeout(() => res({ timeout: true }), 3000);
     });
     document.body.appendChild(f);
     const child = await got;
     f.remove();
     return { outerOrigin: location.origin, outerHasApi: typeof LanguageModel !== 'undefined', child };
   })()`,
  'same browser, opaque child origin'
);

probe(
  'constraint-isolated',
  `(async () => {
     // Each attempt gets a FRESH session: a failing constraint tears the session
     // down, so reusing one session makes every later attempt a false negative.
     const attempts = {};
     const cases = [
       ['json-object-schema', { type: 'object', required: ['ok'], additionalProperties: false, properties: { ok: { type: 'boolean' } } }],
       ['string-schema', { type: 'string' }],
       ['enum-schema', { type: 'string', enum: ['yes', 'no'] }],
       ['regex', /^(yes|no)$/],
       [null, null]
     ];
     for (const [label, constraint] of cases) {
       const s = await LanguageModel.create();
       const key = label ?? 'no-constraint-control';
       try {
         attempts[key] = constraint === null
           ? await s.prompt('Is the sky blue? Answer yes or no.')
           : await s.prompt('Is the sky blue? Answer yes or no.', { responseConstraint: constraint });
       } catch (e) {
         attempts[key] = 'THREW ' + e.name + ': ' + e.message;
         // Does the failure also destroy the session?
         try {
           await s.prompt('Say ok.');
           attempts[key + '::sessionAfter'] = 'still usable';
         } catch (e2) {
           attempts[key + '::sessionAfter'] = 'DEAD ' + e2.name + ': ' + e2.message;
         }
       }
       try { s.destroy(); } catch (e) {}
     }
     return attempts;
   })()`,
  'fresh session per attempt + post-failure health check'
);

probe(
  'clone-history-rigorous',
  `(async () => {
     // Uses the phrasing that demonstrably elicits recall: asking the model to
     // "list every secret codeword" triggers a privacy refusal even in a session
     // that plainly holds the word, which silently fakes a pass.
     const s = await LanguageModel.create({
       initialPrompts: [{ role: 'system', content: 'Answer tersely.' }]
     });
     await s.prompt('Remember this secret codeword: ZEPPELIN. Reply ok.');
     const originalBeforeClone = await s.prompt('What was the secret codeword I just told you?');
     const c = await s.clone();
     const cloneRecall = await c.prompt('What was the secret codeword I just told you?');
     s.destroy(); c.destroy();
     return {
       originalBeforeClone,
       cloneRecall,
       cloneLeaked: /ZEPPELIN/i.test(String(cloneRecall)),
       docsClaim: 'clone() should NOT carry prior conversation'
     };
   })()`,
  'ZEPPELIN taught before clone()'
);

async function closeTarget(id) {
  try {
    await fetch(`http://127.0.0.1:${PORT}/json/close/${id}`);
  } catch {
    /* browser already gone */
  }
}

async function main() {
  const deadline = Date.now() + BUDGET_MS;
  const target = await newTarget();
  console.log(`target: ${target.url}`);
  try {
    await run(target, deadline);
  } finally {
    // Without this every run leaks a live tab into the probe profile. Repeated
    // runs then contend for CPU and the model service, and a later run times
    // out settling for reasons that have nothing to do with the page.
    await closeTarget(target.id);
  }
}

async function run(target, deadline) {
  const conn = cdp(target.webSocketDebuggerUrl);
  await conn.ready;
  const send = conn.send;

  // Subscribe BEFORE enabling the domains, so the replayed
  // executionContextCreated events for contexts that already exist are caught.
  let lastNavAt = Date.now();
  let mainContext = null;
  let navCount = 0;
  let ctxCreated = 0;
  let ctxLost = 0;
  conn.on('Page.frameNavigated', (p) => {
    if (p.frame && !p.frame.parentId) {
      lastNavAt = Date.now();
      navCount += 1;
    }
  });
  conn.on('Runtime.executionContextCreated', (p) => {
    if (p.context?.auxData?.isDefault) {
      mainContext = p.context.uniqueId;
      ctxCreated += 1;
    }
  });
  conn.on('Runtime.executionContextDestroyed', (p) => {
    if (p.executionContextUniqueId && p.executionContextUniqueId === mainContext) {
      mainContext = null;
      ctxLost += 1;
    }
  });
  conn.on('Runtime.executionContextsCleared', () => {
    // Deliberately does NOT null mainContext: this event can be delivered after
    // the replacement context's Created event, which would discard a live
    // context and deadlock re-acquisition. A stale id is already handled - the
    // pinned evaluate fails with a RACE error and we drop it there.
    ctxLost += 1;
  });

  await send('Runtime.enable');
  await send('Page.enable');

  // The target is created at about:blank and navigates asynchronously, and an
  // auth-gated site then navigates AGAIN. Breaking on the first non-opaque
  // origin is what let a redirect land mid-probe.
  //
  // But quiescence must NOT be a precondition. An unauthenticated MSAL flow can
  // redirect-loop indefinitely, so a page may never be quiet for a full second;
  // requiring quiet there deadlocks and reports a timeout that has nothing to do
  // with the site being unreachable. So: prefer a quiet window, but give up
  // waiting for one after QUIET_BUDGET_MS and proceed with whatever live context
  // we have. Correctness does not depend on quiet - it depends on the pinned
  // context making a mid-probe navigation fail loudly instead of silently.
  const QUIET_MS = 1000;
  const QUIET_BUDGET_MS = 6000;
  async function settle(label) {
    const until = Math.min(Date.now() + 60_000, deadline);
    const preferQuietUntil = Date.now() + QUIET_BUDGET_MS;
    let last = 'null';
    while (Date.now() < until) {
      // Pin when we hold a context; fall back to the target's default context
      // when we do not. Never block on holding one - losing a context-created
      // event must degrade accuracy, not deadlock the run.
      const origin = await evaluate(send, `(() => location.origin)()`, mainContext);
      if (origin && origin.raced === true) {
        mainContext = null; // stale id; the next Created event re-pins us
      } else if (typeof origin === 'string') {
        last = origin;
        const quiet = Date.now() - lastNavAt >= QUIET_MS;
        if (origin !== 'null' && (quiet || Date.now() > preferQuietUntil)) return origin;
      }
      await sleep(150);
    }
    throw new Error(
      `never reached a stable non-opaque origin for ${label} ` +
        `(last: ${last}, mainFrameNavigations: ${navCount}, contextsCreated: ${ctxCreated}, ` +
        `contextsLost: ${ctxLost}, holdingContext: ${Boolean(mainContext)})`
    );
  }

  let parsed = null;
  let firstOrigin = null;
  for (let i = 0; i < 3 && !parsed; i += 1) {
    firstOrigin = await settle('startup');
    // Read both in one evaluate so the pair cannot straddle a navigation.
    const startup = await evaluate(
      send,
      `(() => JSON.stringify({ o: location.origin, r: document.readyState }))()`,
      mainContext
    );
    if (typeof startup === 'string') parsed = JSON.parse(startup);
    else mainContext = null; // raced; re-settle on the replacement document
  }
  console.log(`origin: ${parsed?.o ?? firstOrigin}  readyState: ${parsed?.r ?? 'unknown'}\n`);

  const results = {};
  const origins = {};
  for (const { name, expression, note } of PROBES) {
    if (ONLY && !ONLY.has(name)) continue;
    if (Date.now() > deadline) {
      console.log(`${name}: SKIPPED (budget exhausted)`);
      continue;
    }
    const started = Date.now();
    let value;
    let attempt = 0;
    for (;;) {
      let origin;
      try {
        origin = await settle(name);
      } catch (err) {
        value = { threw: true, detail: String(err) };
        break;
      }
      // Snapshot the context we settled on and evaluate against exactly that
      // one, so a navigation between these two lines fails loudly.
      const ctx = mainContext;
      origins[name] = origin;
      try {
        value = await evaluate(send, expression, ctx);
      } catch (err) {
        value = { threw: true, detail: String(err) };
        break;
      }
      if (!value || value.raced !== true) break;
      if (NO_RETRY.has(name)) {
        value.note = 'not retried: probe has side effects on the model service';
        break;
      }
      attempt += 1;
      if (attempt > 2) {
        value.note = `gave up after ${attempt} navigation races`;
        break;
      }
      console.log(`  ${name}: navigation race, re-settling (attempt ${attempt})`);
    }
    results[name] = value;
    const ms = Date.now() - started;
    console.log(`${name}${note ? `  (${note})` : ''}  [${ms}ms] @ ${origins[name] ?? '?'}`);
    console.log(`  ${JSON.stringify(value)}\n`);
  }

  conn.close();
  const distinct = [...new Set(Object.values(origins))];
  if (distinct.length > 1) {
    console.log(`WARNING: probes did not all run on one origin: ${distinct.join(', ')}\n`);
  }
  console.log('=== SUMMARY JSON ===');
  console.log(JSON.stringify({ requestedUrl: URL, originsByProbe: origins, results }, null, 2));
}

main().catch((err) => {
  console.error('FATAL', err);
  process.exit(1);
});
