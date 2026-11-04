let pending = null;
let sending = false;
let focusedWindow = null;
let baseline = true;
let generation = 0;
let focusGeneration = 0;
let receivedSequence = 0;
let acceptedSequence = 0;
let requestController = null;
// Never expose the persistent connection key to content scripts in web-page renderers.
const storageReady = chrome.storage.local.setAccessLevel({ accessLevel: "TRUSTED_CONTEXTS" }).then(() => true, () => false);
const initialFocusGeneration = focusGeneration;
const focusReady = chrome.windows.getLastFocused().then(window => {
  if (focusGeneration === initialFocusGeneration) focusedWindow = window.id;
}).catch(() => {});
// Connection state shown on the badge and in the popup: ok | error | badkey | nokey | storage.
let lastState = "";
let lastAt = 0;
const badges = { ok: ["ON", "#0f766e"], error: ["!", "#b45309"], badkey: ["KEY", "#b91c1c"], nokey: ["KEY", "#b91c1c"], storage: ["ERR", "#b91c1c"] };
const validKey = value => typeof value === "string" && value.length === 32 && /^[a-f0-9]{32}$/.test(value);

function invalidate() {
  generation++;
  baseline = true;
  pending = null;
  requestController?.abort();
}

chrome.windows.onFocusChanged.addListener(id => {
  // Usugami's toolbar/canvas may be foreground while the browser still scrolls.
  // WINDOW_ID_NONE means focus moved outside Chrome; retain its last window.
  if (id !== chrome.windows.WINDOW_ID_NONE && id !== focusedWindow) {
    focusedWindow = id;
    focusGeneration++;
    invalidate();
  }
});
chrome.tabs.onActivated.addListener(info => {
  if (focusedWindow === null || info.windowId === focusedWindow) invalidate();
});
// Ignore changes in other storage areas, and cancel requests carrying the old key.
chrome.storage.onChanged.addListener((changes, areaName) => {
  if (areaName === "local" && changes.connectionKey) { invalidate(); lastState = ""; lastAt = 0; }
});

async function report(state) {
  lastAt = Date.now();
  if (state === lastState) return;
  lastState = state;
  try {
    await chrome.action.setBadgeText({ text: badges[state][0] });
    await chrome.action.setBadgeBackgroundColor({ color: badges[state][1] });
  } catch (_) { /* Badge updates are cosmetic. */ }
}

async function deliver() {
  if (sending) return;
  sending = true;
  try {
    while (pending) {
      const work = pending;
      pending = null;
      try {
        let connectionKey;
        try {
          if (!await storageReady) throw new Error("Connection key storage is unavailable");
          ({ connectionKey } = await chrome.storage.local.get("connectionKey"));
        } catch (_) {
          // Not a connection problem: the key storage itself is unusable, so say so instead of "Usugami not found".
          if (work.generation === generation) { baseline = true; pending = null; await report("storage"); }
          continue;
        }
        if (work.generation !== generation) continue;
        if (!validKey(connectionKey)) { baseline = true; await report("nokey"); continue; }
        // A failure can occur while another scroll is already queued. Apply the
        // recovery baseline at dispatch time, rather than when that scroll arrived.
        const data = { ...work.data, kind: baseline ? "reset" : work.data.kind };
        baseline = false;
        requestController = new AbortController();
        const timeout = setTimeout(() => requestController?.abort(), 1200);
        let response;
        try {
          response = await fetch("http://127.0.0.1:47831/scroll", {
            method: "POST",
            headers: { "Content-Type": "application/json", "X-Usugami-Key": connectionKey },
            body: JSON.stringify(data),
            credentials: "omit",
            redirect: "error",
            cache: "no-store",
            referrerPolicy: "no-referrer",
            signal: requestController.signal
          });
        } finally { clearTimeout(timeout); requestController = null; }
        if (work.generation !== generation) continue;
        if (!response.ok) baseline = true;
        await report(response.ok ? "ok" : response.status === 403 ? "badkey" : "error");
      } catch (_) {
        if (work.generation !== generation) continue;
        baseline = true;
        await report("error");
        // Avoid a request backlog while the desktop app is closed.
        pending = null;
      }
    }
  } finally { sending = false; }
}

// Content scripts share the page's process, so treat their numbers as untrusted input.
const validScroll = m => validKey(m.page) && (m.kind === "scroll" || m.kind === "reset") &&
  [m.x, m.y].every(n => Number.isFinite(n) && Math.abs(n) < 1e8) && Number.isFinite(m.ratio) && m.ratio >= 0.1 && m.ratio <= 10;

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id) return;
  // Both the popup and the settings tab are trusted extension pages.
  if (message?.type === "usugami-status" && sender.url === chrome.runtime.getURL("options.html")) {
    sendResponse({ state: lastState, at: lastAt }); return;
  }
  if (message?.type !== "usugami-scroll" || !sender.tab || sender.frameId !== 0 ||
      !/^https?:\/\//.test(sender.origin || "") || (sender.documentLifecycle && sender.documentLifecycle !== "active") || !validScroll(message)) return;
  const tab = sender.tab;
  if (!tab.active) return;
  const sequence = ++receivedSequence;
  const messageGeneration = generation;
  // Re-read the active tab: sender.tab is only a snapshot and may already be stale.
  void Promise.all([focusReady, chrome.tabs.get(tab.id), chrome.windows.get(tab.windowId)]).then(([, currentTab, window]) => {
    if (messageGeneration !== generation || sequence < acceptedSequence || !currentTab.active || currentTab.windowId !== tab.windowId) return;
    if (!window.focused && focusedWindow !== tab.windowId) return;
    if (window.focused) focusedWindow = tab.windowId;
    acceptedSequence = sequence;
    const oldReset = pending?.data.kind === "reset";
    pending = {
      generation,
      data: {
        kind: message.kind === "reset" || oldReset ? "reset" : "scroll",
        page: `${tab.id}:${message.page}`,
        x: message.x,
        y: message.y,
        ratio: message.ratio
      }
    };
    void deliver();
  }).catch(() => {});
});

