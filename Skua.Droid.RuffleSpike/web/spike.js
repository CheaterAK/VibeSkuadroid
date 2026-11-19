// Game view spike: loads Ruffle and the game, measures, and reports to the host app.
//
// Reporting: every record is one console line "[vspike] {json}". The Android app reads them in
// WebChromeClient.onConsoleMessage, the Chromium check reads them from the page console. Nothing
// here logs packet contents, typed text, URLs with the bridge secret, or anything the game sends.
(() => {
  "use strict";

  const PREFIX = "[vspike] ";
  const marks = Object.create(null);
  const now = () => Math.round(performance.now());
  const statusEl = document.getElementById("status");

  function emit(type, data) {
    try {
      console.info(PREFIX + JSON.stringify(Object.assign({ t: now(), type }, data || {})));
    } catch (e) { /* never let reporting break the page */ }
  }
  function mark(name, extra) {
    if (marks[name] !== undefined) return;
    marks[name] = now();
    emit("mark", Object.assign({ name }, extra || {}));
    if (statusEl) statusEl.textContent = name === "login_screen" || name === "in_game" ? "" : name.replace(/_/g, " ");
  }
  function clip(s, n) { s = String(s); return s.length > n ? s.slice(0, n) + "..." : s; }

  // ---- Socket shim: Ruffle's socketProxy URLs under BRIDGE_PREFIX go over a private MessagePort to the app ----
  // The app hands the port over with WebView.postWebMessage (only to this origin) right after the page loads.
  // Every other URL (the loopback bridge transport) uses the real WebSocket.
  const RealWebSocket = window.WebSocket;
  const BRIDGE_PREFIX = "wss://vibeskua-bridge.invalid/";
  const bridgeSockets = new Map();
  const pendingToApp = [];
  let bridgePort = null, nextSocketId = 1;

  function toApp(msg) { if (bridgePort) bridgePort.postMessage(msg); else pendingToApp.push(msg); }
  function toBase64(u8) {
    let s = "";
    for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000));
    return btoa(s);
  }
  function fromBase64(b64) {
    const bin = atob(b64), u8 = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) u8[i] = bin.charCodeAt(i);
    return u8;
  }
  window.addEventListener("message", (e) => {
    if (bridgePort || e.data !== "vibeskua-bridge" || !e.ports || e.ports.length !== 1) return;
    bridgePort = e.ports[0];
    bridgePort.onmessage = (m) => {
      const msg = String(m.data), a = msg.indexOf("|"), b = msg.indexOf("|", a + 1);
      const kind = msg.slice(0, a), id = b < 0 ? msg.slice(a + 1) : msg.slice(a + 1, b), rest = b < 0 ? "" : msg.slice(b + 1);
      const s = bridgeSockets.get(id);
      if (!s) return;
      if (kind === "O") s._opened(); else if (kind === "D") s._data(rest); else if (kind === "C") s._closed(rest);
    };
    for (const msg of pendingToApp.splice(0)) bridgePort.postMessage(msg);
    emit("bridge_channel_ready");
  });

  class BridgeSocket extends EventTarget {
    constructor(url) {
      super();
      const parts = url.slice(BRIDGE_PREFIX.length).split("/");
      this.url = url; this.protocol = ""; this.extensions = ""; this.bufferedAmount = 0; this.binaryType = "blob";
      this.readyState = 0; this.onopen = null; this.onmessage = null; this.onerror = null; this.onclose = null;
      this._id = String(nextSocketId++);
      bridgeSockets.set(this._id, this);
      emit("socket_open", { host: clip(parts[0], 80), port: +parts[1] });
      toApp("o|" + this._id + "|" + parts[0] + "|" + parts[1]);
    }
    _fire(ev) {
      const h = this["on" + ev.type];
      try { if (typeof h === "function") h.call(this, ev); } finally { this.dispatchEvent(ev); }
    }
    _opened() {
      if (this.readyState !== 0) return;
      this.readyState = 1;
      emit("socket_connected");
      this._fire(new Event("open"));
    }
    _data(b64) {
      if (this.readyState !== 1) return;
      const u8 = fromBase64(b64);
      this._fire(new MessageEvent("message", { data: this.binaryType === "arraybuffer" ? u8.buffer : new Blob([u8]) }));
    }
    _closed(reason) {
      if (this.readyState === 3) return;
      const wasOpen = this.readyState === 1;
      this.readyState = 3;
      bridgeSockets.delete(this._id);
      emit("socket_closed", { reason: clip(reason, 60) });
      if (!wasOpen) this._fire(new Event("error"));
      this._fire(new CloseEvent("close", { code: wasOpen ? 1000 : 1006, reason: "", wasClean: wasOpen }));
    }
    send(data) {
      if (this.readyState === 0) throw new DOMException("Still in CONNECTING state.", "InvalidStateError");
      if (this.readyState !== 1) return;
      // Ruffle (gloo-net) sends every chunk as a Blob, which can only be read asynchronously: chain the sends so
      // they reach the app in order.
      let bytes;
      if (typeof data === "string") bytes = Promise.resolve(new TextEncoder().encode(data));
      else if (data instanceof Blob) bytes = data.arrayBuffer().then((b) => new Uint8Array(b));
      else if (data instanceof ArrayBuffer) bytes = Promise.resolve(new Uint8Array(data.slice(0)));
      else if (ArrayBuffer.isView(data)) bytes = Promise.resolve(new Uint8Array(data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength)));
      else throw new TypeError("unsupported data");
      this._sendChain = (this._sendChain || Promise.resolve())
        .then(() => bytes)
        .then((u8) => { if (this.readyState === 1) toApp("d|" + this._id + "|" + toBase64(u8)); })
        .catch((e) => emit("js_error", { msg: "bridge send: " + clip(e && e.message || e, 100) }));
    }
    close() {
      if (this.readyState >= 2) return;
      toApp("c|" + this._id);
      this._closed("page closed");
    }
  }
  function PatchedWebSocket(url, protocols) {
    if (typeof url === "string" && url.startsWith(BRIDGE_PREFIX)) return new BridgeSocket(url);
    return protocols === undefined ? new RealWebSocket(url) : new RealWebSocket(url, protocols);
  }
  PatchedWebSocket.prototype = RealWebSocket.prototype;
  for (const k of ["CONNECTING", "OPEN", "CLOSING", "CLOSED"]) {
    PatchedWebSocket[k] = RealWebSocket[k];
    BridgeSocket[k] = RealWebSocket[k];
    BridgeSocket.prototype[k] = RealWebSocket[k];
  }
  window.WebSocket = PatchedWebSocket;

  // ---- WebAssembly memory (Ruffle's heap: by far the largest part of its memory) ----
  let wasmMemory = null;
  function captureWasm(result) {
    try {
      const inst = result && (result.instance || result);
      const exp = inst && inst.exports;
      if (exp) {
        for (const k of Object.keys(exp)) {
          if (exp[k] instanceof WebAssembly.Memory) { wasmMemory = exp[k]; break; }
        }
      }
      mark("wasm_instantiated");
    } catch (e) { /* ignore */ }
    return result;
  }
  const origInstantiate = WebAssembly.instantiate;
  WebAssembly.instantiate = function () { return origInstantiate.apply(this, arguments).then(captureWasm); };
  if (WebAssembly.instantiateStreaming) {
    const origStreaming = WebAssembly.instantiateStreaming;
    WebAssembly.instantiateStreaming = function () { return origStreaming.apply(this, arguments).then(captureWasm); };
  }

  // ---- Rendered frames: a frame counts when Ruffle drew anything during an animation frame ----
  let draws = 0;
  function countCalls(proto, name) {
    if (!proto || typeof proto[name] !== "function") return;
    const orig = proto[name];
    proto[name] = function () { draws++; return orig.apply(this, arguments); };
  }
  countCalls(window.WebGLRenderingContext && WebGLRenderingContext.prototype, "clear");
  countCalls(window.WebGL2RenderingContext && WebGL2RenderingContext.prototype, "clear");
  countCalls(window.GPUQueue && GPUQueue.prototype, "submit");
  countCalls(window.CanvasRenderingContext2D && CanvasRenderingContext2D.prototype, "setTransform");

  let renderedFrames = 0, lastDraws = 0, rafTicks = 0;
  function onAnimationFrame() {
    rafTicks++;
    if (draws !== lastDraws) { renderedFrames++; lastDraws = draws; }
    requestAnimationFrame(onAnimationFrame);
  }
  requestAnimationFrame(onAnimationFrame);

  // ---- Main-thread long tasks (jank) ----
  let longTaskMs = 0, longTaskCount = 0, longestTask = 0;
  try {
    new PerformanceObserver((list) => {
      for (const e of list.getEntries()) {
        longTaskMs += e.duration; longTaskCount++;
        if (e.duration > longestTask) longestTask = e.duration;
      }
    }).observe({ type: "longtask", buffered: true });
  } catch (e) { /* not supported */ }

  // ---- Ruffle console output: instance info and errors (no trace output: logLevel stays at warn) ----
  const origLog = console.log;
  console.log = function () {
    try {
      const first = String(arguments[0] || "");
      if (first.indexOf("New Ruffle instance created") >= 0) {
        mark("ruffle_instance", { info: clip(first.replace(/^%c/, ""), 200) });
      }
    } catch (e) { /* ignore */ }
    return origLog.apply(this, arguments);
  };
  window.addEventListener("error", (e) => emit("js_error", { msg: clip(e.message || e, 300) }));
  window.addEventListener("unhandledrejection", (e) => emit("js_error", { msg: clip((e.reason && e.reason.message) || e.reason, 300) }));

  // ---- Config ----
  let cfg = null, player = null, packets = 0, soakTimer = null;

  // Calls an ExternalInterface callback of skua.swf; undefined if it is not there (yet) or threw.
  function call(name) {
    if (!player || typeof player[name] !== "function") return undefined;
    try { return player[name].apply(player, Array.prototype.slice.call(arguments, 1)); }
    catch (e) { return undefined; }
  }

  // skua.swf calls ExternalInterface.call(name, restArray): each handler gets one array argument.
  function installSkuaHandlers() {
    window.requestLoadGame = function () {
      mark("skua_swf_ready");
      setTimeout(() => { mark("load_client_called"); call("loadClient"); }, 0);
    };
    window.loaded = function () { mark("game_swf_loaded"); };
    window.debug = function (args) { emit("as3_debug", { msg: clip(args && args[0], 300) }); };
    // Packets: counted only, never logged.
    window.pext = window.packet = window.packetFromServer = function () { packets++; };
    window.connectionLost = function () { emit("connection_lost"); };
    window.openWebsite = function () { emit("open_website_ignored"); };
    window.userIntentionalAction = function (args) { emit("user_action", { action: clip(args && args[0], 40) }); };
  }

  let lastLabel = null, inGame = false, modulesApplied = false, lastConnected = null;
  function pollGame() {
    if (cfg.swf !== "skua") return;
    const labelJson = call("getGameObject", "mcLogin.currentLabel");
    if (labelJson !== undefined && labelJson !== lastLabel) {
      lastLabel = labelJson;
      emit("login_label", { label: clip(labelJson, 40) });
    }
    if (labelJson && labelJson !== "null" && call("getGameObject", "mcLogin.visible") === "true") {
      mark("login_screen", { label: clip(labelJson, 40) });
    }
    const connected = call("isLoggedIn");
    if (connected !== undefined && connected !== lastConnected) {
      lastConnected = connected;
      emit("socket_state", { connected: connected === "true" });
      if (connected === "true") mark("socket_connected");
    }
    if (call("isKicked") === "true") emit("kicked_or_warning", { msg: clip(call("getActiveModalMessage") || "", 200) });
    const avatar = call("isNull", "world.myAvatar.pMC");
    const nowInGame = connected === "true" && avatar === "false";
    if (nowInGame && !inGame) {
      mark("in_game");
      if (!modulesApplied) { modulesApplied = true; applyModules(); }
      if (cfg.soakMinutes > 0 && !soakTimer) startSoak();
    }
    if (inGame && !nowInGame) emit("left_game");
    inGame = nowInGame;
  }

  function applyModules() {
    const m = cfg.modules || {};
    const done = [];
    if (m.hidePlayers) { call("modEnable", "HidePlayers"); done.push("HidePlayers"); }
    if (m.disableFX) { call("modEnable", "DisableFX"); done.push("DisableFX"); }
    if (m.killLag) { call("killLag", true); done.push("killLag"); }
    if (m.skipCutscenes) { call("skipCutscenes"); done.push("skipCutscenes"); }
    emit("modules_applied", { modules: done });
  }

  function startSoak() {
    const end = Date.now() + cfg.soakMinutes * 60000;
    emit("soak_started", { minutes: cfg.soakMinutes });
    soakTimer = setInterval(() => {
      if (Date.now() >= end) { clearInterval(soakTimer); emit("soak_finished", { minutes: cfg.soakMinutes }); }
    }, 5000);
  }

  // ---- Periodic samples ----
  let lastSample = performance.now(), lastFrames = 0, lastRaf = 0, lastLong = 0, lastPackets = 0;
  function sample() {
    const t = performance.now(), dt = (t - lastSample) / 1000;
    if (dt <= 0) return;
    const rec = {
      fps: +((renderedFrames - lastFrames) / dt).toFixed(2),
      raf: +((rafTicks - lastRaf) / dt).toFixed(1),
      longTaskPct: +(((longTaskMs - lastLong) / 10) / dt).toFixed(1),
      packetsPerSec: +((packets - lastPackets) / dt).toFixed(1),
    };
    lastSample = t; lastFrames = renderedFrames; lastRaf = rafTicks; lastLong = longTaskMs; lastPackets = packets;
    emit("fps", rec);
  }
  function memSample() {
    const rec = {};
    if (wasmMemory) rec.wasmMB = +(wasmMemory.buffer.byteLength / 1048576).toFixed(1);
    if (performance.memory) {
      rec.jsHeapMB = +(performance.memory.usedJSHeapSize / 1048576).toFixed(1);
      rec.jsHeapTotalMB = +(performance.memory.totalJSHeapSize / 1048576).toFixed(1);
    }
    rec.longTasks = longTaskCount; rec.longestTaskMs = Math.round(longestTask);
    emit("mem", rec);
  }
  function checkPanic() {
    const root = player && player.shadowRoot;
    const panic = root && root.getElementById("panic");
    if (panic && !marks.ruffle_panic) {
      const body = root.getElementById("panic-body");
      mark("ruffle_panic", { msg: clip(body ? body.textContent : panic.textContent, 500) });
    }
  }

  function loadScript(src) {
    return new Promise((resolve, reject) => {
      const s = document.createElement("script");
      s.src = src; s.onload = resolve; s.onerror = () => reject(new Error("failed to load " + src));
      document.head.appendChild(s);
    });
  }

  async function main() {
    mark("page_start");
    cfg = await (await fetch("config.json", { cache: "no-store" })).json();
    emit("config", {
      swf: cfg.swf, ruffle: cfg.ruffle, transport: cfg.transport, frameRate: cfg.frameRate, quality: cfg.quality, renderer: cfg.renderer,
      maxExecutionDuration: cfg.maxExecutionDuration, modules: cfg.modules, mute: cfg.mute,
      soakMinutes: cfg.soakMinutes, socketProxies: (cfg.socketProxy || []).length,
      ua: clip(navigator.userAgent, 200), dpr: window.devicePixelRatio,
      screen: window.innerWidth + "x" + window.innerHeight,
    });

    // Only the bundled build directories are accepted.
    const ruffleDir = cfg.ruffle === "nightly" ? "ruffle-nightly" : "ruffle-0.7.1";
    installSkuaHandlers();
    window.RufflePlayer = window.RufflePlayer || {};
    window.RufflePlayer.config = {
      publicPath: ruffleDir + "/",
      polyfills: false,
      autoplay: "on",
      unmuteOverlay: "hidden",
      splashScreen: false,
      letterbox: "on",
      contextMenu: "rightClickOnly",
      favorFlash: false,
      warnOnUnsupportedContent: false,
      logLevel: cfg.logLevel || "warn",
      allowScriptAccess: true,
      allowNetworking: "all",
      openUrlMode: "deny",
      upgradeToHttps: true,
      maxExecutionDuration: cfg.maxExecutionDuration || 15,
      frameRate: cfg.frameRate || null,
      quality: cfg.quality || null,
      preferredRenderer: cfg.renderer && cfg.renderer !== "auto" ? cfg.renderer : null,
      socketProxy: cfg.socketProxy || [],
      backgroundExecutionMode: "none",
      base: "https://game.aq.com/game/",
    };

    await loadScript(ruffleDir + "/ruffle.js");
    mark("ruffle_js_loaded");

    const ruffle = window.RufflePlayer.newest();
    player = ruffle.createPlayer();
    player.id = "player";
    document.getElementById("game").appendChild(player);
    player.addEventListener("loadedmetadata", () => mark("first_frame_metadata"));
    player.addEventListener("loadeddata", () => {
      mark("first_frame");
      if (cfg.mute) { try { player.volume = 0; } catch (e) { /* ignore */ } }
    });

    const url = cfg.swf === "skua" ? "skua.swf" : "https://game.aq.com/game/gamefiles/Loader4.swf?ver=a";
    const parameters = cfg.swf === "skua" ? {} : { sLang: "", isweb: "true" };
    emit("load_swf", { which: cfg.swf });
    await player.load({ url, parameters, allowScriptAccess: true });
    mark("swf_load_started");

    setInterval(pollGame, 1000);
    setInterval(sample, 2000);
    setInterval(memSample, 10000);
    setInterval(checkPanic, 1000);
    memSample();
  }

  // Host hooks (the app calls these through evaluateJavascript).
  window.vspike = {
    suspend() { try { player && player.suspend(); emit("suspended"); } catch (e) { /* ignore */ } },
    resume() { try { player && player.resume(); emit("resumed"); } catch (e) { /* ignore */ } },
    applyModules() { applyModules(); },
    snapshot() { sample(); memSample(); },
    // Makes the game's own SmartFox client open its socket to host:port (no login, nothing is sent but the
    // client's version check). Used by the Chromium check to prove game -> Ruffle -> socketProxy -> bridge.
    testConnect(host, port) { emit("test_connect", { host: clip(host, 80), port: +port }); return call("callGameFunction", "connectTo", host, +port); },
  };

  main().catch((e) => { emit("js_error", { msg: clip(e && e.message || e, 300) }); if (statusEl) statusEl.textContent = "Error: " + e; });
})();
