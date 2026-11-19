// Desktop Chromium check of the spike page: serves web/ + Ruffle + skua.swf at
// https://game.aq.com/__vibeskua__/ (as the Android app does), lets other game.aq.com requests
// go to the network, blocks every other host, and records the page's [vspike] lines.
// It never logs in. Usage (Playwright must be resolvable, e.g. NODE_PATH=/opt/node-tools/node_modules):
// Run ../fetch-ruffle.sh first.
//   node check.cjs [--ruffle stable|nightly] --swf skua|official [--seconds 90] [--renderer auto]
//                  [--frame-rate 4] [--quality low] [--headed] [--proxy http://host:port] [--screenshot file.png]
//                  [--probe path/to/BridgeProbe.dll [--transport channel|loopback]]
//                  (the probe makes the game's socket connect through the real bridge to a local echo server)
"use strict";
const fs = require("fs");
const path = require("path");
const { chromium } = require("playwright");

const args = process.argv.slice(2);
function opt(name, def) {
  const i = args.indexOf("--" + name);
  if (i < 0) return def;
  const v = args[i + 1];
  return v === undefined || v.startsWith("--") ? true : v;
}

const webDir = path.resolve(__dirname, "..", "web");
const skuaSwf = path.resolve(opt("skua-swf", path.resolve(__dirname, "..", "..", "Skua.App.Avalonia", "skua.swf")));
const seconds = +opt("seconds", 90);
const config = {
  swf: opt("swf", "skua"),
  ruffle: opt("ruffle", "stable"),
  frameRate: +opt("frame-rate", 4),
  quality: opt("quality", "low"),
  renderer: opt("renderer", "auto"),
  maxExecutionDuration: +opt("max-exec", 30),
  logLevel: opt("log-level", "warn"),
  mute: true,
  soakMinutes: 0,
  modules: { hidePlayers: true, disableFX: true, killLag: false, skipCutscenes: false },
  socketProxy: [],
};

const PREFIX = "https://game.aq.com/__vibeskua__/";
const TYPES = { ".html": "text/html", ".js": "text/javascript", ".wasm": "application/wasm", ".swf": "application/x-shockwave-flash", ".json": "application/json", ".map": "application/json" };

function localFile(rel) {
  if (rel === "config.json") return { body: Buffer.from(JSON.stringify(config)), type: "application/json" };
  if (rel === "skua.swf") return { body: fs.readFileSync(skuaSwf), type: TYPES[".swf"] };
  const file = path.resolve(webDir, rel);
  if (!file.startsWith(webDir + path.sep) || !fs.existsSync(file)) return null;
  return { body: fs.readFileSync(file), type: TYPES[path.extname(file)] || "application/octet-stream" };
}

// Starts BridgeProbe (the real bridge classes in front of a local echo server) and reads its first line.
// In channel mode every further stdout line is an app-to-page message.
function startProbe(dll, mode, onLine) {
  const { spawn } = require("child_process");
  const child = spawn("dotnet", [dll, mode], { stdio: ["pipe", "pipe", "inherit"] });
  return new Promise((resolve, reject) => {
    let buf = "", first = null;
    child.stdout.on("data", (d) => {
      buf += d;
      let nl;
      while ((nl = buf.indexOf("\n")) >= 0) {
        const line = buf.slice(0, nl); buf = buf.slice(nl + 1);
        if (first === null) { first = JSON.parse(line); resolve({ child, transport: first.transport, proxies: first.socketProxy }); }
        else onLine(line);
      }
    });
    child.on("exit", (code) => { if (first === null) reject(new Error("probe exited " + code)); });
  });
}

(async () => {
  const t0 = Date.now();
  let probe = null, page = null;
  const toPage = [];
  if (opt("probe", false)) {
    probe = await startProbe(path.resolve(opt("probe")), opt("transport", "channel"), (line) => {
      if (page) page.evaluate((l) => window.__probeDeliver(l), line).catch(() => {});
    });
    config.socketProxy = probe.proxies;
    config.transport = probe.transport;
  }
  const proxy = opt("proxy", process.env.HTTPS_PROXY || null);
  const browser = await chromium.launch({
    headless: !opt("headed", false),
    proxy: proxy ? { server: proxy, bypass: "127.0.0.1,localhost" } : undefined,
    args: ["--enable-unsafe-swiftshader", "--ignore-gpu-blocklist"],
  });
  const context = await browser.newContext({ viewport: { width: 958, height: 550 } });
  if (opt("grant-lna", false)) {
    // Desktop Chrome asks before a public origin may reach 127.0.0.1 (Local Network Access); grant it here.
    await context.grantPermissions(["local-network-access"], { origin: "https://game.aq.com" });
  }
  page = await context.newPage();
  if (probe && probe.transport === "channel") {
    // Plays the app's part: hands the page one end of a MessageChannel after load (the app uses
    // WebView.postWebMessage) and relays the other end to BridgeProbe's stdin/stdout.
    await page.exposeBinding("__probeSend", (_src, msg) => { probe.child.stdin.write(msg + "\n"); });
    await page.addInitScript(() => {
      window.addEventListener("load", () => {
        const ch = new MessageChannel();
        ch.port1.onmessage = (m) => window.__probeSend(String(m.data));
        window.__probeDeliver = (line) => ch.port1.postMessage(line);
        window.postMessage("vibeskua-bridge", "*", [ch.port2]);
      });
    });
  }
  const requests = new Map(); // "host path" -> count (game.aq.com) or "BLOCKED host"
  const failures = [];

  await page.route("**/*", async (route) => {
    const url = new URL(route.request().url());
    if (url.href.startsWith(PREFIX)) {
      const rel = decodeURIComponent(url.pathname.slice("/__vibeskua__/".length)) || "index.html";
      const f = localFile(rel);
      if (!f) return route.fulfill({ status: 404, body: "" });
      return route.fulfill({ status: 200, body: f.body, headers: { "content-type": f.type, "cache-control": "no-store" } });
    }
    if (url.protocol === "https:" && url.hostname === "game.aq.com") {
      const key = route.request().method() + " " + url.pathname;
      requests.set(key, (requests.get(key) || 0) + 1);
      return route.continue();
    }
    const key = "BLOCKED " + url.protocol + "//" + url.hostname + url.pathname.slice(0, 60);
    requests.set(key, (requests.get(key) || 0) + 1);
    return route.fulfill({ status: 403, body: "" });
  });
  page.on("requestfailed", (r) => failures.push(r.url().slice(0, 120) + " " + (r.failure() && r.failure().errorText)));
  page.on("response", (r) => { if (r.status() >= 400 && !r.url().startsWith(PREFIX)) failures.push(r.status() + " " + r.url().slice(0, 120)); });
  page.on("console", (msg) => {
    const text = msg.text();
    const ts = ((Date.now() - t0) / 1000).toFixed(1).padStart(6);
    if (text.startsWith("[vspike] ")) console.log(ts + " " + text.slice(9));
    else if (msg.type() === "error" || msg.type() === "warning") console.log(ts + " console." + msg.type() + ": " + text.slice(0, 400));
  });
  page.on("pageerror", (e) => console.log("pageerror: " + e.message));

  await page.goto(PREFIX + "index.html");
  if (probe) {
    // Once the login screen is up, make the game's own SmartFox client connect to sock7.aq.com:5593. Its socket
    // goes game -> Ruffle -> socketProxy -> (shim + channel | loopback WebSocket) -> bridge -> local echo server,
    // which prints what it received on stderr. Nothing logs in.
    await page.waitForFunction(() => document.getElementById("status") && document.getElementById("status").textContent === "", null, { timeout: 120000 });
    await page.waitForTimeout(2000);
    console.log("PROBE: calling the game's connectTo(sock7.aq.com, 5593) over transport " + probe.transport);
    await page.evaluate(() => window.vspike.testConnect("sock7.aq.com", 5593));
  }
  await page.waitForTimeout(seconds * 1000);
  const shot = opt("screenshot", null);
  if (shot) await page.screenshot({ path: shot });
  console.log("\n--- requests (game.aq.com passed through, others blocked) ---");
  for (const [k, v] of [...requests.entries()].sort()) console.log(String(v).padStart(4) + "  " + k);
  console.log("--- failures ---\n" + (failures.join("\n") || "(none)"));
  await browser.close();
  if (probe) { probe.child.stdin.end(); await new Promise((r) => probe.child.on("exit", r)); }
})().catch((e) => { console.error(e); process.exit(1); });
