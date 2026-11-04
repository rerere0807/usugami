// Run with: node tests/extension.test.cjs
const { readFileSync } = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const path = require('node:path');
const root = path.join(__dirname, '..', 'extension');
const tick = () => new Promise(resolve => setImmediate(resolve));
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const flush = async () => { await tick(); await tick(); };

async function testContent() {
  const listeners = {};
  const frames = [];
  const sent = [];
  let heartbeat;
  const window = { scrollX: 0, scrollY: 100, screenX: 0, screenY: 0, innerWidth: 1000, innerHeight: 700, devicePixelRatio: 1.5,
    addEventListener(name, handler) { listeners[name] = handler; } };
  const document = { visibilityState: 'visible', addEventListener(name, handler) { listeners[name] = handler; } };
  const runtime = { id: 'ext', sendMessage: async m => sent.push(m) };
  let cleared = false;
  vm.runInNewContext(readFileSync(path.join(root, 'content.js'), 'utf8'), {
    window, document, crypto: { getRandomValues: a => a.fill(171) },
    chrome: { runtime }, clearInterval: () => { cleared = true; },
    requestAnimationFrame: fn => frames.push(fn), setInterval: fn => { heartbeat = fn; }
  });
  const flushFrames = () => { while (frames.length) frames.shift()(); };
  flushFrames(); assert.equal(sent.at(-1).kind, 'reset');
  assert.match(sent.at(-1).page, /^[a-f0-9]{32}$/, 'page id without crypto.randomUUID (HTTP pages)');
  window.scrollY = 325; listeners.scroll(); listeners.scroll();
  assert.equal(frames.length, 1, 'scroll coalesces to one animation frame');
  flushFrames(); assert.equal(sent.at(-1).kind, 'scroll'); assert.equal(sent.at(-1).y, 325); assert.equal(sent.at(-1).ratio, 1.5);
  window.scrollX = 120; listeners.scroll(); flushFrames(); assert.equal(sent.at(-1).x, 120);
  heartbeat(); flushFrames(); assert.equal(sent.at(-1).kind, 'scroll', 'heartbeat preserves baseline');
  window.devicePixelRatio = 2; heartbeat(); flushFrames(); assert.equal(sent.at(-1).kind, 'reset', 'zoom resets baseline');
  const count = sent.length; document.visibilityState = 'hidden'; heartbeat(); flushFrames(); assert.equal(sent.length, count, 'background document stays quiet');
  document.visibilityState = 'visible'; listeners.visibilitychange(); flushFrames(); assert.equal(sent.at(-1).kind, 'reset', 'visible document starts a fresh baseline');
  runtime.id = undefined; heartbeat(); flushFrames(); assert.equal(cleared, true, 'orphaned script stops its heartbeat');
}

function backgroundHarness(options = {}) {
  let handler, focusHandler, activatedHandler, storageHandler;
  const sent = [], badges = [], accessLevels = [];
  const h = {
    sent, badges, accessLevels, storedKey: 'a'.repeat(32), status: 204,
    getStorage: async () => ({ connectionKey: h.storedKey }),
    getLastFocused: async () => ({ id: 10 }),
    getTab: async id => ({ id, windowId: 10, active: true }),
    getWindow: async id => ({ id, focused: false }),
    fetch: async () => ({ ok: h.status >= 200 && h.status < 300, status: h.status }),
    ...options
  };
  const context = {
    chrome: {
      windows: { WINDOW_ID_NONE: -1, onFocusChanged: { addListener: fn => { focusHandler = fn; } }, getLastFocused: () => h.getLastFocused(), get: id => h.getWindow(id) },
      tabs: { onActivated: { addListener: fn => { activatedHandler = fn; } }, get: id => h.getTab(id) },
      storage: {
        local: { get: () => h.getStorage(), setAccessLevel: async value => { accessLevels.push(value.accessLevel); if (h.storageIsolationFails) throw new Error('unavailable'); } },
        onChanged: { addListener: fn => { storageHandler = fn; } }
      },
      runtime: { id: 'ext', getURL: file => `chrome-extension://ext/${file}`, onMessage: { addListener: fn => { handler = fn; } } },
      action: { setBadgeText: async badge => badges.push(badge.text), setBadgeBackgroundColor: async () => {} }
    },
    fetch: async (url, options) => { sent.push({ url, ...options, body: JSON.parse(options.body) }); return h.fetch(options); },
    AbortController, Date, setTimeout, clearTimeout
  };
  vm.runInNewContext(readFileSync(path.join(root, 'background.js'), 'utf8'), context);
  h.message = (changes = {}, senderChanges = {}) => handler({ type: 'usugami-scroll', kind: 'scroll', page: 'b'.repeat(32), x: 0, y: 0, ratio: 1.25, ...changes },
    { id: 'ext', url: 'https://example.test/private-page', origin: 'https://example.test', tab: { id: 5, windowId: 10, active: true }, frameId: 0, documentLifecycle: 'active', ...senderChanges });
  h.state = (sender = { id: 'ext', url: 'chrome-extension://ext/options.html' }) => { let result; handler({ type: 'usugami-status' }, sender, r => { result = r; }); return result; };
  h.focus = id => focusHandler(id);
  h.activate = (tabId, windowId = 10) => activatedHandler({ tabId, windowId });
  h.changeKey = (key, area = 'local') => { h.storedKey = key; storageHandler({ connectionKey: { newValue: key } }, area); };
  return h;
}

async function testBackground() {
  const h = backgroundHarness(); await flush();
  h.message(); await flush();
  assert.equal(h.sent.length, 1); assert.equal(h.sent[0].body.kind, 'reset');
  assert.deepEqual(h.accessLevels, ['TRUSTED_CONTEXTS'], 'content scripts cannot access the stored key');
  h.focus(-1); // Drawing toolbar gets focus; retain the browser's scroll baseline.
  h.message({ y: 400 }); await flush();
  assert.equal(h.sent.length, 2); assert.equal(h.sent[1].body.kind, 'scroll'); assert.equal(h.sent[1].body.y, 400);
  assert.equal(h.sent[1].headers['X-Usugami-Key'], 'a'.repeat(32));
  assert.equal(h.sent[1].redirect, 'error', 'a redirect cannot forward the key to another destination');
  assert.equal(h.sent[1].credentials, 'omit'); assert.equal(h.sent[1].cache, 'no-store'); assert.equal(h.sent[1].referrerPolicy, 'no-referrer');
  for (const sender of [{ frameId: 2 }, { tab: { id: 5, windowId: 10, active: false } }, { tab: { id: 5, windowId: 20, active: true } }, { id: 'other-extension' }, { origin: 'file://' }, { documentLifecycle: 'prerender' }]) {
    h.message({}, sender); await flush();
  }
  assert.equal(h.sent.length, 2, 'inactive/foreign/frame/non-HTTP/prerender senders are ignored');
  assert.deepEqual(Object.keys(h.sent[1].body).sort(), ['kind', 'page', 'ratio', 'x', 'y'], 'no URL or page content transmitted');
  assert.equal(h.state().state, 'ok'); assert.equal(h.badges.at(-1), 'ON');
  assert.equal(h.state({ id: 'ext', url: 'https://example.test' }), undefined, 'web pages cannot query status');
  assert.equal(h.state({ id: 'ext', url: 'chrome-extension://ext/options.html', tab: { id: 100 } }).state, 'ok', 'settings in a tab can query status');
  h.status = 403; h.message(); await flush();
  assert.equal(h.state().state, 'badkey'); assert.equal(h.badges.at(-1), 'KEY');
  h.status = 204; h.message(); await flush();
  assert.equal(h.sent.at(-1).body.kind, 'reset', 'rejection forces a fresh baseline');
  h.storedKey = 'not-a-key'; const count = h.sent.length;
  h.message(); await flush();
  assert.equal(h.sent.length, count, 'no request without a valid key'); assert.equal(h.state().state, 'nokey');
  for (const malformedKey of ['a'.repeat(32) + '\n', ['a'.repeat(32)], { toString: () => 'a'.repeat(32) }]) {
    h.storedKey = malformedKey; h.message(); await flush();
  }
  assert.equal(h.sent.length, count, 'newline-terminated or non-string keys are rejected');
  h.storedKey = 'a'.repeat(32);
  for (const bad of [{ x: NaN }, { y: Infinity }, { ratio: 50 }, { ratio: 0.01 }, { page: '<script>' }, { page: 'a'.repeat(65) }, { page: 'a'.repeat(32) + '\n' }, { page: ['a'.repeat(32)] }, { kind: 'evil' }, { x: '1' }]) {
    h.message(bad); await flush();
  }
  assert.equal(h.sent.length, count, 'malformed messages are dropped');
  h.getTab = async () => ({ id: 5, windowId: 10, active: false });
  h.message(); await flush(); assert.equal(h.sent.length, count, 'stale active flag is checked against the current tab');
}

async function testRacesAndFailures() {
  const h = backgroundHarness(); await flush(); h.message(); await flush();
  const failure = deferred(); h.fetch = () => failure.promise;
  h.message({ y: 10 }); await flush(); h.message({ y: 20 }); await flush();
  h.fetch = async () => ({ ok: true, status: 204 }); failure.resolve({ ok: false, status: 403 }); await flush();
  assert.equal(h.sent.at(-1).body.y, 20); assert.equal(h.sent.at(-1).body.kind, 'reset', 'an already queued scroll resets after a failed in-flight request');

  const oldValidation = deferred(), newValidation = deferred(); let validation = 0;
  h.getWindow = () => (++validation === 1 ? oldValidation.promise : newValidation.promise);
  const count = h.sent.length;
  h.message({ y: 30 }); h.message({ y: 40 });
  newValidation.resolve({ id: 10, focused: false }); await flush();
  oldValidation.resolve({ id: 10, focused: false }); await flush();
  assert.equal(h.sent.length, count + 1); assert.equal(h.sent.at(-1).body.y, 40, 'out-of-order browser API replies never send an older scroll');

  const changingTab = deferred(); h.getWindow = () => changingTab.promise;
  h.message({ y: 50 }); h.activate(6); changingTab.resolve({ id: 10, focused: false }); await flush();
  assert.equal(h.sent.length, count + 1, 'a tab switch cancels pending validation from the previous tab');
  h.getWindow = async id => ({ id, focused: false }); h.message({ y: 60 }, { tab: { id: 6, windowId: 10, active: true } }); await flush();
  assert.equal(h.sent.at(-1).body.kind, 'reset');

  let aborted = false;
  h.fetch = options => new Promise((resolve, reject) => options.signal.addEventListener('abort', () => { aborted = true; reject(new Error('aborted')); }, { once: true }));
  h.message({ y: 70 }); await flush(); h.changeKey('c'.repeat(32));
  h.fetch = async () => ({ ok: true, status: 204 }); h.message({ y: 80 }); await flush();
  assert.equal(aborted, true, 'changing the connection key aborts old-key requests');
  assert.equal(h.sent.at(-1).headers['X-Usugami-Key'], 'c'.repeat(32)); assert.equal(h.sent.at(-1).body.kind, 'reset'); assert.equal(h.state().state, 'ok', 'stale aborted request does not overwrite the new connection state');

  h.getStorage = async () => { throw new Error('storage read failed'); };
  const beforeError = h.sent.length; h.message(); await flush();
  assert.equal(h.sent.length, beforeError); assert.equal(h.state().state, 'storage', 'storage failure is handled without an unhandled rejection');
  const isolated = backgroundHarness({ storageIsolationFails: true }); await flush(); isolated.message(); await flush();
  assert.equal(isolated.sent.length, 0); assert.equal(isolated.state().state, 'storage', 'connection fails closed if key storage cannot be isolated');

  const initialFocus = deferred();
  const startup = backgroundHarness({ getLastFocused: () => initialFocus.promise, getTab: async id => ({ id, windowId: 20, active: true }) });
  startup.focus(20); startup.message({}, { tab: { id: 7, windowId: 20, active: true } });
  initialFocus.resolve({ id: 10 }); await flush();
  assert.equal(startup.sent.length, 1, 'late startup lookup cannot replace a newer focused window');

  const unavailable = backgroundHarness({ fetch: async () => { throw new Error('connection refused'); } }); await flush();
  unavailable.message(); await flush(); assert.equal(unavailable.state().state, 'error');
  unavailable.fetch = async () => ({ ok: true, status: 204 }); unavailable.message(); await flush();
  assert.equal(unavailable.sent.at(-1).body.kind, 'reset', 'reconnecting after a network failure starts from a fresh baseline');
}

async function testOptions() {
  const elements = {};
  for (const id of ['key', 'message', 'statusCard', 'stateTitle', 'stateDetail', 'reveal', 'settings']) {
    elements[id] = { value: '', type: 'password', dataset: {}, listeners: {}, addEventListener(name, fn) { this.listeners[name] = fn; }, setAttribute() {} };
  }
  let storedKey = 'a'.repeat(32), failSave = false, failRead = false, refresh;
  const writes = [], accessLevels = [];
  vm.runInNewContext(readFileSync(path.join(root, 'options.js'), 'utf8'), {
    document: { getElementById: id => elements[id] },
    chrome: {
      storage: { local: {
        setAccessLevel: async options => accessLevels.push(options.accessLevel),
        get: async () => { if (failRead) throw new Error('read failed'); return { connectionKey: storedKey }; },
        set: async value => { if (failSave) throw new Error('write failed'); writes.push(value); storedKey = value.connectionKey; }
      } },
      runtime: { sendMessage: async () => ({ state: '__proto__', at: Date.now() }) }
    },
    Date, setInterval: fn => { refresh = fn; }
  });
  elements.key.value = 'd'.repeat(32); await flush();
  assert.equal(elements.key.value, 'd'.repeat(32), 'late initial storage read does not overwrite a key being entered');
  assert.deepEqual(accessLevels, ['TRUSTED_CONTEXTS']);
  const submit = () => elements.settings.listeners.submit({ preventDefault() {} });
  elements.key.value = 'invalid'; await submit(); assert.equal(writes.length, 0);
  assert.equal(elements.message.className, 'error', 'invalid key is rejected before saving');
  elements.key.value = `  ${'A'.repeat(32)}  `; await submit(); await flush();
  assert.equal(writes.at(-1).connectionKey, 'a'.repeat(32), 'pasted keys are normalized');
  assert.equal(elements.statusCard.dataset.state, 'idle', 'unknown service-worker status cannot crash the popup');
  failSave = true; await submit(); assert.equal(elements.message.className, 'error'); assert.match(elements.message.textContent, /保存できません/);
  failRead = true; await refresh(); assert.equal(elements.statusCard.dataset.state, 'storage', 'storage failures are handled without an unhandled rejection');
  failRead = false;
  for (const malformedKey of ['a'.repeat(32) + '\n', ['a'.repeat(32)]]) {
    storedKey = malformedKey; await refresh(); assert.equal(elements.statusCard.dataset.state, 'nokey', 'stored keys require an exact string without trailing newlines');
  }
}

(async () => {
  await testContent(); await testBackground(); await testRacesAndFailures(); await testOptions();
  console.log('PASS: content coalescing/zoom/visibility/lifecycle; background authentication, least privilege, sender validation, privacy, safe fetch, connection status, out-of-order replies, tab/key changes, queued failure recovery, storage failures; settings key validation, normalization, async initialization and save errors.');
})().catch(error => { console.error(error); process.exitCode = 1; });

