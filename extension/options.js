const key = document.getElementById("key");
const message = document.getElementById("message");
const card = document.getElementById("statusCard");
const storageReady = chrome.storage.local.setAccessLevel({ accessLevel: "TRUSTED_CONTEXTS" }).then(() => true, () => false);
let refreshSequence = 0;
let saving = false;
const states = {
  ok: ["接続済み", "Usugami にスクロール位置を送信しています。"],
  error: ["Usugami が見つかりません", "Usugami アプリが起動しているか確認してください。"],
  badkey: ["接続キーが一致しません", "Usugami で「接続キーをコピー」し、貼り付け直してください。"],
  nokey: ["接続キーが未設定です", "Usugami の「接続キーをコピー」で取得したキーを貼り付けてください。"],
  storage: ["接続キーを読み書きできません", "ブラウザーを最新版に更新し、拡張機能を再読み込みしてください。"],
  idle: ["待機中", "HTTP/HTTPS のページを表示すると自動で接続します。"]
};
const valid = value => typeof value === "string" && value.length === 32 && /^[a-f0-9]{32}$/.test(value);

async function readKey() {
  if (!await storageReady) throw new Error("Connection key storage is unavailable");
  return chrome.storage.local.get("connectionKey");
}

async function refresh() {
  const sequence = ++refreshSequence;
  let state = "idle";
  try {
    const { connectionKey } = await readKey();
    if (!valid(connectionKey)) state = "nokey";
    else {
      try {
        const status = await chrome.runtime.sendMessage({ type: "usugami-status" });
        // Pages send a heartbeat every second, so an old result means nothing is talking to Usugami right now.
        if (Object.hasOwn(states, status?.state) && Number.isFinite(status.at) && Date.now() - status.at >= 0 && Date.now() - status.at < 5000) state = status.state;
      } catch (_) { /* Service worker not running yet. */ }
    }
  } catch (_) { state = "storage"; }
  if (sequence !== refreshSequence) return;
  card.dataset.state = state;
  document.getElementById("stateTitle").textContent = states[state][0];
  document.getElementById("stateDetail").textContent = states[state][1];
}

readKey().then(result => { if (!key.value && valid(result.connectionKey)) key.value = result.connectionKey; }).catch(() => {
  message.className = "error"; message.textContent = "接続設定を読み込めませんでした。拡張機能を再読み込みしてください。";
});
document.getElementById("reveal").addEventListener("click", event => {
  const show = key.type === "password";
  key.type = show ? "text" : "password";
  event.currentTarget.textContent = show ? "隠す" : "表示";
  event.currentTarget.setAttribute("aria-pressed", String(show));
});
document.getElementById("settings").addEventListener("submit", async event => {
  event.preventDefault();
  if (saving) return;
  const value = key.value.trim().toLowerCase();
  if (!valid(value)) { message.className = "error"; message.textContent = "Usugami でコピーした32文字の接続キーを入力してください。"; return; }
  key.value = value;
  saving = true;
  try {
    if (!await storageReady) throw new Error("Connection key storage is unavailable");
    await chrome.storage.local.set({ connectionKey: value });
    message.className = ""; message.textContent = "保存しました。対象のページに戻ると自動的に接続します。";
    void refresh();
  } catch (_) {
    message.className = "error"; message.textContent = "保存できませんでした。拡張機能を再読み込みしてから、もう一度お試しください。";
  } finally { saving = false; }
});
void refresh();
setInterval(refresh, 1000);
