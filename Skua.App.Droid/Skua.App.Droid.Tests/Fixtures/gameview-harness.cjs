// Runs the game view's page script (Skua.App.Droid/GameView/web/gameview.js) in a Node vm with a fake Ruffle player
// that answers skua.swf's ExternalInterface callbacks the way the real client's objects would, and prints what the
// page reported. Used by GameViewPageTests. Usage: node gameview-harness.cjs <gameview.js> <scenario>
"use strict";
const fs = require("fs");
const vm = require("vm");

const [scriptPath, scenario] = process.argv.slice(2);
const records = [];
const calls = [];
const intervals = [];
const state = { loginVisible: "false", ni: "true", connected: "false", avatar: "true", mapLoading: "false", reflect: true };

const player = {
  id: "", style: {}, shadowRoot: null, addEventListener() {},
  load() { return Promise.resolve(); },
};
function reflect(name, fn) { player[name] = (...a) => { calls.push([name, ...a]); return state.reflect ? fn(...a) : undefined; }; }
reflect("getGameObject", (p) => p === "mcLogin.visible" ? state.loginVisible : p === "world.mapLoadInProgress" ? state.mapLoading : "null");
reflect("isNull", (p) => p === "mcLogin.ni" ? state.ni : p === "world.myAvatar" ? state.avatar : "true");
reflect("isLoggedIn", () => state.connected);
reflect("isKicked", () => "false");
reflect("callGameFunction", () => "null");
reflect("modEnable", () => undefined);
reflect("killLag", () => undefined);
reflect("loadClient", () => undefined);

let port = null;
const listeners = {};
const window = {
  WebSocket: function () {},
  devicePixelRatio: 2.625, innerWidth: 2400, innerHeight: 1080,
  addEventListener(type, fn) { (listeners[type] = listeners[type] || []).push(fn); },
};
window.WebSocket.prototype = { constructor: window.WebSocket };
const RealWebSocket = window.WebSocket;
window.RTCPeerConnection = function () {};
const document = {
  getElementById(id) { return id === "status" ? { textContent: "" } : { appendChild() {} }; },
  createElement() { return {}; },
  head: { appendChild(s) { window.RufflePlayer.newest = () => ({ createPlayer: () => player }); setImmediate(s.onload); } },
};
const context = {
  window, document, navigator: { userAgent: "test" },
  console: { info: (m) => records.push(JSON.parse(String(m).slice("[vgame] ".length))), log() {}, warn() {}, error() {} },
  performance: { now: () => Date.now(), memory: undefined },
  WebAssembly: { instantiate: () => Promise.resolve({}), Memory: function () {} },
  requestAnimationFrame() {}, PerformanceObserver: function () { this.observe = () => {}; },
  setInterval: (fn) => { intervals.push(fn); return intervals.length; }, setTimeout: (fn) => setImmediate(fn), clearInterval() {},
  fetch: () => Promise.resolve({ json: () => Promise.resolve({ frameRate: 4, pixelRatio: "1", renderer: "webgl", modules: { hidePlayers: true, disableFX: true, killLag: false }, socketProxy: [] }) }),
  EventTarget: class {}, Event: class {}, MessageEvent: class {}, CloseEvent: class {}, Blob: class {}, DOMException: Error,
  TextEncoder, TextDecoder, atob: (s) => Buffer.from(s, "base64").toString("latin1"), btoa: (s) => Buffer.from(s, "latin1").toString("base64"),
  Uint8Array, ArrayBuffer, Promise, Object, JSON, String, Array, Date, Map, Math, Error, parseFloat,
};
context.window.window = context.window;
Object.setPrototypeOf(context, window);
vm.createContext(context);
vm.runInContext(fs.readFileSync(scriptPath, "utf8"), context);

const tick = () => intervals.forEach((f) => f());
const b64 = (s) => Buffer.from(s, "utf8").toString("base64");

setTimeout(async () => {
  // The app hands over the private port.
  port = { postMessage() {}, onmessage: null };
  listeners.message.forEach((f) => f({ data: "vibeskua-bridge", ports: [port] }));
  window.requestLoadGame && window.requestLoadGame();
  if (scenario === "login-and-poll") {
    tick();
    state.loginVisible = "true"; state.ni = "false";
    port.onmessage({ data: "L|" + b64("hero") + "|" + b64("hunter2-secret") });
    tick();
    state.loginVisible = "false"; state.connected = "true"; state.avatar = "false"; state.mapLoading = "true";
    tick();
    state.mapLoading = "false";
    tick();
    state.connected = "false"; state.avatar = "true";
    tick();
  } else if (scenario === "packets-only") {
    state.reflect = false; // reading game objects fails altogether (every callback answers undefined)
    tick();
    window.pext(['{"params":{"type":"json","dataObj":{"cmd":"moveToArea","areaName":"battleon-1"}}}']);
    tick();
  }
  // The WebSocket shim: nothing reachable from the patched constructor is the browser's own.
  const W = window.WebSocket;
  const reachable = [W, W.prototype, W.prototype.constructor, Object.getPrototypeOf(W.prototype), Object.getPrototypeOf(W)];
  const proto = Object.getPrototypeOf(W.prototype);
  if (proto) reachable.push(proto.constructor);
  let otherRefused = false;
  try { new W.prototype.constructor("wss://example.com/"); } catch (e) { otherRefused = e && e.name !== "TypeError"; }
  const bridge = new W("wss://vibeskua-bridge.invalid/sock7.aq.com/5593");
  const shim = {
    patched: W !== RealWebSocket,
    constructorIsPatched: W.prototype.constructor === W,
    realReachable: reachable.some((x) => x === RealWebSocket || x === RealWebSocket.prototype),
    otherRefused,
    bridgeIsInstance: bridge instanceof W,
    bridgeConstructorIsPatched: bridge.constructor === W,
    constructorWritable: (() => { try { W.prototype.constructor = RealWebSocket; } catch (e) { /* strict */ } return W.prototype.constructor === RealWebSocket; })(),
    rtcRemoved: !("RTCPeerConnection" in window),
  };
  process.stdout.write(JSON.stringify({ records, calls, shim }) + "\n");
}, 50);
