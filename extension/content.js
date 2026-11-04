(() => {
  // crypto.randomUUID() exists only in secure contexts, so plain HTTP pages need getRandomValues().
  const page = Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, "0")).join("");
  let scheduled = false;
  let reset = true;
  let lastGeometry = "";
  let heartbeat = 0;
  function publish() {
    scheduled = false;
    if (document.visibilityState !== "visible") return;
    // After the extension is reloaded or removed, this orphaned script can no longer talk to it.
    if (!chrome.runtime?.id) { clearInterval(heartbeat); return; }
    const geometry = [window.screenX, window.screenY, window.innerWidth, window.innerHeight, window.devicePixelRatio].join(":");
    if (geometry !== lastGeometry) reset = true;
    lastGeometry = geometry;
    const message = {
      type: "usugami-scroll",
      kind: reset ? "reset" : "scroll",
      page,
      x: window.scrollX,
      y: window.scrollY,
      ratio: window.devicePixelRatio
    };
    reset = false;
    try { chrome.runtime.sendMessage(message).catch(() => {}); } catch (_) { clearInterval(heartbeat); }
  }
  function schedule() {
    if (!scheduled) { scheduled = true; requestAnimationFrame(publish); }
  }
  window.addEventListener("scroll", schedule, { passive: true });
  window.addEventListener("resize", () => { reset = true; schedule(); }, { passive: true });
  window.addEventListener("pageshow", () => { reset = true; schedule(); });
  document.addEventListener("visibilitychange", () => { reset = true; schedule(); });
  // Heartbeats recover after starting Usugami or changing its connection key.
  heartbeat = setInterval(schedule, 1000);
  schedule();
})();
