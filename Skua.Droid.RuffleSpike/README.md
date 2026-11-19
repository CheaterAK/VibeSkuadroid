# Game view spike (Ruffle in a WebView)

A standalone Android app (`com.vibeskua.droid.ruffle`, not in `Skua.sln`) that answers one question: can the
phone show the real game at a low frame rate, so the user can use the game's own UI for a while and then go back
to the headless client? It opens AdventureQuest Worlds in a WebView running the Ruffle web build, measures, and
writes a shareable report. It does not touch `Skua.App.Droid` and stores no credentials.

## Layout

| Path | What |
|---|---|
| `web/index.html`, `web/spike.js` | The page: measurement hooks, socket shim, Ruffle setup, skua.swf callbacks |
| `web/ruffle-0.7.1/`, `web/ruffle-nightly/` | Ruffle self-hosted builds, downloaded by `fetch-ruffle.sh` (not committed) |
| `ruffle-builds.sha256` | Pinned versions, npm tarball URLs and SHA-256 of every shipped file |
| `RuffleSpike.Core/` | net10.0: request policy, server allowlist, both socket bridges, settings, recorder |
| `RuffleSpike.Android/` | net10.0-android: settings screen, game view activity (WebView host) |
| `RuffleSpike.Tests/` | xunit v3 tests of everything in Core (run on Linux) |
| `chromium-check/` | Desktop Chromium run of the same page against the real game.aq.com (no login) |

## How it works

**Page origin.** The WebView loads `https://game.aq.com/__vibeskua__/index.html`. The app answers everything under
`/__vibeskua__/` itself from the APK's assets (`shouldInterceptRequest`); nothing under that path reaches the
network. So the game's own requests are same-origin: game.aq.com sends no CORS headers, and the login is a POST,
whose body `shouldInterceptRequest` never sees, so the app could not fetch and hand over those requests itself.
The WebView loads them from the network directly, with the game's site as Referer.

**Request allowlist** (`RuffleSpike.Core/RequestPolicy.cs`):

* `https://game.aq.com/__vibeskua__/<file>`: bundled file (plain names only: letters, digits, `.`, `_`, `-`,
  `/`; no `..`, no encoding, no empty segments); `config.json` is generated per session.
* `https://game.aq.com/game/...`: network (the game's API and files: `api/data/gameversion`, `api/login/now`,
  `gamefiles/...`, maps and items, all under `/game/`).
* `data:` and `blob:`: allowed (local to the WebView).
* Everything else is answered with an empty 403 and counted in the report: other hosts, other paths on
  game.aq.com, plain http, other ports, user info in the URL. Navigations away from the page are cancelled.
  In the desktop check the game asked for nothing outside `/game/` up to the login screen.

**Sockets.** Ruffle cannot open TCP sockets; it tunnels each `Socket` through a WebSocket URL from its `socketProxy`
list, which the app builds from the live server list (`https://game.aq.com/game/api/data/servers`, fetched by the
app). Every host:port in that list (and only those, under `aq.com`/`artix.com`, DNS names only) gets an entry.

* `channel` (default): the page replaces `window.WebSocket` for `wss://vibeskua-bridge.invalid/<host>/<port>` with
  a shim that talks to the app over a private `WebMessagePort` (`WebView.postWebMessage`, delivered only to the
  `https://game.aq.com` page). `ChannelBridge` opens one TCP connection per page socket after checking host:port
  against the server list. There is no listening socket at all.
* `loopback` (fallback): `SocketBridge` listens on 127.0.0.1 (random port) and accepts a WebSocket only with the
  per-session 32-byte random path secret (constant-time compare), `Origin: https://game.aq.com`, and a host:port
  from the server list; max 4 connections. Desktop Chromium 153 refuses `ws://127.0.0.1` from a public https
  origin unless the user grants Local Network Access (net::ERR_BLOCKED_BY_LOCAL_NETWORK_ACCESS_CHECKS), which is
  why it is not the default.

Neither bridge logs payloads, and the recorder removes the secret and credential-like `key=value` pairs from any
console text it keeps.

**Loader.** `skua` loads this repo's `Skua.App.Avalonia/skua.swf` (it runs under Ruffle: ExternalInterface
callbacks work, `loadClient` fetches the game version and loads the game SWF, `loaded` fires, the login screen
appears). Its callbacks give the login/in-game milestones and the load reducers: modules `HidePlayers` and
`DisableFX`, `killLag` (hides the whole world, leaving only the UI), `skipCutscenes`; they are applied once the
player is in the game. Packet callbacks are only counted. `official` loads the site's `Loader4.swf` the way the
game page does (no callbacks, so only first frame and socket milestones).

**Frame rate and quality** are Ruffle settings (`frameRate` locks the player's rate, `quality` is the stage
quality). They apply when the view opens; change them on the settings screen and reopen the view.

## Build

Toolchain as in `build/android/README.md`, then

```sh
Skua.Droid.RuffleSpike/build-ruffle-spike.sh [apk-output-dir]   # fetch + verify Ruffle, tests, Debug and Release APKs
```

Both APKs are signed with the SDK's auto-generated debug key (sideload only).

Desktop Chromium check (Playwright with its Chromium; reaches game.aq.com, never logs in):

```sh
Skua.Droid.RuffleSpike/fetch-ruffle.sh
dotnet build -c Release Skua.Droid.RuffleSpike/chromium-check/BridgeProbe
cd Skua.Droid.RuffleSpike/chromium-check
NODE_PATH=/opt/node-tools/node_modules node check.cjs --swf skua --seconds 60 --screenshot /tmp/x.png
NODE_PATH=/opt/node-tools/node_modules node check.cjs --swf skua --seconds 15 \
  --probe BridgeProbe/bin/Release/net10.0/BridgeProbe.dll --transport channel   # or loopback --grant-lna
```

The probe makes the game's own SmartFox client connect (`connectTo`) to `sock7.aq.com:5593` through the real
bridge classes to a local echo server, which prints the game's version-check bytes it received.

## Ruffle builds

From the official npm package `@ruffle-rs/ruffle` (MIT OR Apache-2.0; the license files ship in each build
directory). Versions, tarball URLs, npm integrity values and per-file SHA-256 are in `ruffle-builds.sha256`;
`fetch-ruffle.sh` refuses anything that does not match.

* `0.7.1` (npm `latest`, 2026-10-07): the default.
* `0.8.0-nightly.2026.11.19` (npm `nightly`): selectable, to compare.

## On the phone

1. Install the Release APK (allow installs from the file manager or browser you open it with).
2. Make sure the account is not logged in anywhere else (the headless app, a PC): a second login kicks the first.
3. Open **Game View Spike**, keep the defaults (skua loader, Ruffle 0.7.1, 4 fps, low quality, renderer auto,
   transport channel, hide players and disable FX on), tap **Open game view**.
4. Log in on the game's own screen (do not tick its Save boxes), pick a server, walk around, open a few game
   menus (inventory, quests, a shop). The HUD at the top left shows fps, main-thread load, app memory, Ruffle
   memory and crashes. **Report** shows the report so far (Share/Copy); **X** or Back closes the view and saves it.
5. Back on the settings screen the last report is shown with Copy and Share. Send it back.

Then, if the first run works, one setting change per run, sending each report:

* Renderer `webgl`, then `canvas` (and `webgpu` if the WebView supports it); frame rate 2 and 8;
  quality medium; Ruffle nightly; loader official; kill lag on.
* One run with **10-minute soak** on (stay in the game, screen on; a report is saved when it ends).
* If sockets never connect with `channel`, one run with transport `loopback` (and, if that fails too, with
  "Allow mixed content").

What the report holds: device, Android and WebView versions, settings; milestones (page, Ruffle instance with its
renderer, first frame, game SWF, login screen, server socket, in game); fps (avg, min, p10, max) with main-thread
long-task load; memory every 10 s (app PSS/RSS/Java/native, Ruffle wasm memory, JS heap, system available);
renderer process deaths (didCrash, priority); console errors and warnings; Ruffle panics; blocked requests;
socket bridge status; Android's records of the app's recent exits.

The WebView's renderer is a separate process the app cannot measure directly; Ruffle's wasm memory and the JS heap
are its main parts and are in the report.
