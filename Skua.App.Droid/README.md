# VibeSkua for Android (Skua.App.Droid)

Native, headless Android client: no Flash, no game rendering. Avalonia 12 UI, .NET 10 on Mono, one account.

## Projects

| Project | TFM | What |
|---|---|---|
| `Skua.App.Droid.Core` | net10.0 | `IBotEngine` seam, `HeadlessBotEngine` (on Skua.Core.Headless), `EngineHost` (login + remember me, resume after restart, memory watchdog, script queue), ViewModels, settings, credential vault format, script library |
| `Skua.App.Droid.Scripting` | net10.0 | script reference images (`ScriptReferenceSet`), content-addressed `ScriptAssemblyStore`, `SkuaScriptHostAdapter` (Skua.Core through `HeadlessSkuaSession`) |
| `Skua.App.Droid.Linked` | net10.0 | the desktop's UI-free ViewModels compiled in unchanged (advanced skill items and rules, CoreBots loadout); referenced by `.Core` only, behind an extern alias (see Tools) |
| `Skua.App.Droid.Plugins` | net10.0 | the built-in plugins' game-side halves (Daily Tracker, ButlerV5) and the Control page's, on Skua's `IScriptInterface`; created by the script host adapter |
| `Skua.App.Droid.UI` | net10.0 | Avalonia 12 views (compiled bindings) |
| `Skua.App.Droid` | net10.0-android | Android head: plain launcher and crash screen, the Avalonia activity, foreground `EngineService`, `KeystoreSecretStore`, crash capture, restart, manifest |
| `Skua.App.Droid.Desktop` | net10.0 | desktop preview of the same UI and engine (no password storage) |
| `Skua.App.Droid.Tests` | net10.0 | xunit v3 + Avalonia headless tests |

This folder has its own `Directory.Build.props/targets` and `Directory.Packages.props` (Avalonia 12.1.6); the
repo-root ones (Avalonia 11) do not apply here.

## Build

Toolchain as in `build/android/README.md`, then:

```sh
Skua.App.Droid/build-droid.sh [apk-output-dir]     # tests, Debug APK, Release APK
dotnet run --project Skua.App.Droid/Skua.App.Droid.Desktop   # desktop preview
```

`-p:DroidEmulatorAbi=true` adds x86_64 (emulators). Release keeps trimming and AOT off on purpose (scripts are
compiled on the device against the full API surface); see the comments in `Skua.App.Droid/Skua.App.Droid.csproj`.
LLVM marshal methods are off too: with them an incremental Release build produced APKs that crashed in
`Application.onCreate` (UnsatisfiedLinkError) before any window. The build fails if a generated Java wrapper
lacks method registration, and `build-droid.sh` always builds the Android head from scratch.

## Signing

Default: the SDK's auto-generated debug key (sideload only).

Local release signing is opt-in and needs no file in the repo: `build-droid.sh` signs the Release APK when
`DROID_KEYSTORE` (path), `DROID_KEY_ALIAS`, `DROID_KEYSTORE_PASSWORD` and `DROID_KEY_PASSWORD` are set (it passes
`-p:DroidKeystore=<path> -p:DroidKeyAlias=<alias>`; the passwords reach MSBuild only through the environment).
Do this only on a machine you trust: the build runs NuGet packages' MSBuild tasks in the same process.

GitHub Actions (`.github/workflows/android-droid.yml`) never gives the build the keystore. The `build` job (every
push and pull request) builds and tests with no secrets and uploads debug-key APKs plus the unsigned Release APK.
A separate `sign` job, only for tags and the default branch and never for pull requests, runs just `zipalign` and
`apksigner` on that unsigned APK and uploads `vibeskua-android-release-signed`. Its secrets live in the GitHub
Environment `release`, which the repository owner sets up once (Settings > Environments):

* required reviewers (a maintainer approves every release signature);
* deployment branches and tags: the default branch and the release tag pattern (e.g. `v*`);
* environment secrets `DROID_KEYSTORE_BASE64` (`base64 -w0 my.keystore`), `DROID_KEY_ALIAS`,
  `DROID_KEYSTORE_PASSWORD`, `DROID_KEY_PASSWORD` (delete any repository secrets with these names).

Without those secrets the sign job only prints a notice. Never commit a keystore.

## Startup

The engine (Skua's DI container and services, folders, Keystore, script references) is composed on a background
thread, never on the main thread: `Application.OnCreate` sets HOME and creates the folders (cheap, and it must happen
before other threads read the environment), starts the composition (`EngineStartup` in
`Skua.App.Droid.Core/Hosting`) and returns. Everything that needs the engine continues when it completes:

* the UI shows a short "Starting the engine..." view, then the main view; if composition failed, the error view
  (the crash screen explains it on the next launch);
* `MainActivity` requests the notification permission, resumes after a restart and binds the service afterwards;
* `EngineService` goes to the foreground at once and queues its commands (resume, stop) until then; without an
  engine it stops;
* the restart receiver only starts the composition and the service.

Concurrent callers share one attempt; a failed attempt is recorded before anyone continues, and the next start
(activity, service, receiver) retries it. The startup marker records the composition separately from the UI steps
(`engine=` line), so a process that dies while composing is recognised and the next start skips composition until
the crash screen was seen.

## If the app crashes

The launcher entry is a plain Android activity (no Avalonia). Crash capture is installed in the Application
constructor; any crash (managed exception, unobserved task exception, uncaught Java throwable) is written to
`files/diagnostics/crash-report.txt` inside the app sandbox, and startup milestones go to logcat (tag `VibeSkua`), to
`files/diagnostics/events.log` and to a step marker. On the next launch, before Avalonia starts, the crash screen
shows the report and the recent events with Share and Copy; Continue clears it and opens the app. A start that
died without any exception (native crash, abort) is reported from the marker ("last reached step N"). Reports are
scrubbed of the password, login tokens and credential-like `key=value` pairs. If even the crash screen never
appears, take a bug report (Developer options > Take bug report) or `adb logcat -s VibeSkua AndroidRuntime DEBUG`.

**Deaths no code of the app sees** (native crash, ANR, killed for memory): on Android 11+ every start of the main
process reads Android's own exit records (`ActivityManager.getHistoricalProcessExitReasons`, `Diagnostics/ExitReasons.cs`)
before any activity decides what to show. Each exit since the last one handled is one line in the events log; an
abnormal one (CRASH, CRASH_NATIVE, ANR, INITIALIZATION_FAILURE, EXCESSIVE_RESOURCE_USAGE, DEPENDENCY_DIED, an exit code;
LOW_MEMORY, SIGNALED, FREEZER only when the app was visible, held its foreground service or ran a script; OTHER/UNKNOWN
only by the app's own record of a running script or visible UI) becomes the crash report: reason, signal or status,
importance and PSS/RSS at death, Android's description, what the app last recorded about itself, the other recent exits,
and the trace Android kept: the ANR trace, or on Android 12+ the native tombstone, decoded from its protobuf form
(signal, abort message, crashing thread's backtrace, other threads, the log tail; `Core/Diagnostics/TraceText.cs`).
If the dying process wrote a report itself, Android's record is appended to it. The restart's own kill is recorded
beforehand and not reported. Classification and formatting are platform neutral and tested (`ExitReasonTests`).

**Heartbeat.** The main process records what it is doing every 30 s and on every lifecycle transition (activity
start/resume/pause/stop/destroy, surface created/changed/destroyed, service start/stop, trimMemory and lowMemory): UI
foreground or background, script state, connection, RSS. It goes to the events log, to the startup marker and (Android
11+) to the process state summary Android returns with that process's exit record, so the report says what the app was
doing just before it died. Below Android 11 a process that died while a script ran is reported from the marker alone.

## Background and foreground

The engine, its log and the ViewModels live for the whole process; each activity gets a fresh view. While the activity
is stopped (`AppBootstrap.SetUiVisible(false)` from `MainActivity.onStop`), engine snapshots and console lines are not
pushed into the bound properties and lists: snapshots keep only the latest, console lines wait in a bounded queue
(`BoundedFeed`, twice the 600 visible lines). On `onStart` the latest snapshot is applied once and the queue is flushed,
or, if it overflowed during a long run in the background, the console lists are rebuilt once from the engine's buffer.
So a script logging for hours in the background costs no UI work and the return to the foreground at most one rebuild.

**Avalonia workaround (`RenderFence`).** In Avalonia 12.1.x, `surfaceDestroyed` (app going to the background) releases
the window and only asks the compositor to stop at its next commit; a frame already being drawn keeps using the
destroyed surface (AvaloniaUI/Avalonia#22728; fix proposed in PR #22738, not in 12.1.6). A surface callback registered
after Avalonia's (`Diagnostics/SurfaceWatch.cs`) waits, at most 500 ms, for the compositor's render lock (private,
reached by reflection once; without it nothing happens), so no frame is drawing into the window when Android destroys
it. Remove it once the Avalonia version in use has the upstream fix.

## Scripts

Scripts run on unmodified Skua.Core through `Skua.Core.Headless.Host` (`HeadlessSkuaSession`: ScriptManager,
compiler, IScriptInterface on the headless Flash bridge). The bridge between the app and Skua is
`Skua.App.Droid.Scripting/SkuaScriptHostAdapter.cs`, an `ISessionScriptHost`.

**One session.** The adapter creates the one `HeadlessSkuaSession` of the process when the engine starts; its
`HeadlessGame.Session` is the engine's `AqwSession`, so the engine, Skua's services and the Flash bridge always share
one session. Login goes through Skua's own code (the game's login, then `Servers.SetLoginInfo` + `EnsureRelogin`). A
logout keeps the session object and forgets every credential in place: Skua's login info, the game's loginInfo
(username, password, server list) and the session's credentials and token (`HeadlessGame.ForgetLogin`,
`AqwSession.ForgetCredentials`). Without a session host (tests) the engine keeps its old behaviour: a fresh
`AqwSession` per logout.

**Server list.** Skua's relogin reads the server list through `IServerListProvider` (a Skua.Core seam; the desktop
registers none and keeps its own fetch). The headless host's provider reads `api/data/servers` from game.aq.com over
HTTPS with the headers the site requires, and falls back to the list the HTTP login returned. Skua.Core's own fetch
(plain http to content.aq.com, which Android's network security config refuses) is never attempted. Skua.Core's
GitHub client credential is only ever sent to HTTPS GitHub hosts.

**Reconnects: one owner at a time.** While a script compiles, runs or stops, or Skua's relogin is pending or running,
Skua owns reconnecting (its AutoRelogin option); the engine shows "Reconnecting (script relogin)" and its own loop, if
on, only waits. Otherwise the engine's loop reconnects, through Skua's login code. The app's *Auto reconnect* setting
is applied as Skua's AutoRelogin at every script start (the desktop's AutoRelogin box); a script may change it, and
Skua turns it off when the script stops, as on the desktop.

**Assemblies are loaded once per process.** Mono on Android never collects AssemblyLoadContexts, so every reload of
a CoreBots-sized assembly would leak about 10 MB. ScriptManager's `LoadContextFactory` (a Skua.Core seam whose
desktop default is unchanged) hands it a `StoreScriptLoadContext`, which loads every script and include image
through `ScriptAssemblyStore`, keyed by the image's SHA-256 and the identities of the script assemblies it binds to.
An unchanged CoreBots is loaded once; an edited one is loaded once more, next to the old one; re-running a script
loads nothing. The memory line shows the count ("N script asm").

> **Consequence: static state survives between runs.** On the desktop each run gets fresh statics (new load
> context). Here a reused assembly keeps them: a static field set by one run is still set at the next start of any
> script using the same assembly (for example `CoreBots.Instance` and anything CoreBots keeps in statics). Scripts
> that assume fresh statics behave as if started twice in a row without reloading. A restart of the engine (memory
> banner or Dashboard) gives fresh statics.

**Version reported to scripts: 1.8.5.0.** CoreBots' `SkuaVersionCheckerAsync` returns early for "1.8.5.0" (its
VibeSkua marker); for any other version it requires Windows 10 and stops every script. See
`SkuaScriptHostAdapter.ReportedSkuaVersion`.

**Unattended dialogs.** Message boxes never wait: yes/no questions get *No*; a choice of several buttons gets the
declining one when offered (Cancel / No / Abort / Close), else None / Skip / OK / Continue, else the first button; a
single button is pressed. Confirmation and input dialogs (the grabber's buy and sell quantities) are declined. Every
question and its answer is logged as a `dialog` warning in the Console. Script option windows are skipped (saved
options are used).

**Options.** *Options* on the Scripts page compiles the selected script (later starts reuse the compile) and edits
the options it declares (Skua's `Options` / `MultiOptions` lists: toggles, numbers, text, enum choices), saved to
`options/<storage>.cfg` where Skua reads them. Transient options are shown read-only (reset every run).
*CoreBots options* edits CoreBots' own settings (`options/CBO_Storage(<user>).txt`: class and gear sets, private
rooms, delays...); the keys are read from the synced Core*.cs sources. Without that file CoreBots uses its defaults.
The same file has proper pages under Settings > Tools > *CoreBots options* (see Tools).

**Status.** The Scripts page and the Dashboard show the running script, its run time and Skua's counters (kills,
deaths, quests, drops, relogins); the script log (`script` lines), Skua's debug output (`skua`, debug level) and
dialogs go to the Console.

**Memory watchdog.** Follows the script state: never restarts while a script compiles, runs or stops; compile
samples (Roslyn's transient peak) are neither the start-up baseline nor "over the threshold"; the idle grace starts
when the script ends.

## Community scripts

*Sync community scripts* downloads github.com/auqw/Scripts (branch Skua) into the script folder, with its folder
structure, so `//cs_include Scripts/...` resolves: the repository's `scripts.json` index (path, size, SHA-256 of every
script), then only new or changed files, six at a time; plus `Skills/AdvancedSkills.json` (reloaded into Skua's skill
sets before the next run), `QuestData.json` (merged, quests seen in game are kept) and `JunkItems.json`.

* Host: `https://raw.githubusercontent.com` only. No GitHub API calls, no token.
* Paths are validated exactly like imports (`ScriptLibrary.ResolveDestination`); download URLs must start with
  `https://raw.githubusercontent.com/auqw/Scripts/` (`ScriptRepositorySync.AllowedDownloadPrefix`), with no dot
  segments, query or credentials. Files the repository does not list (your own scripts) are never touched.
* Every entry must carry a SHA-256 (the real index has one for all); entries without are refused. A download whose
  length or SHA-256 differs from its entry is not written: it counts as failed and is logged.
* An index listing more than 10,000 entries or more than 256 MB in all fails the sync before anything is written.
* A 429 (or 403 with rate limit headers) stops the sync and says when to retry; what was downloaded is kept and the
  next sync continues from there (files whose SHA-256 matches are skipped). Server errors are retried twice.
* It runs as engine background work: the foreground service keeps the process alive while it runs in the
  background, and *Cancel* stops it.

Measured once from Linux: 1953 scripts (13.2 MB) in 28 s.

Reference images scripts compile against are packed as APK assets under `assets/script-refs/` by
`ScriptReferences.targets`; the tests compile real scripts through Skua.Core against exactly that set.

## Tools

Advanced Skills and CoreBots options are pages of the Tools hub (Settings > Tools), registered by `SkillToolPages.AddTo`
next to the game and notification pages. System back goes up inside the page first (a question, the rotation editor, a
class), then to the hub, then to the settings.

**Advanced Skills** (desktop: AdvancedSkillsView). Classes and their rotations from `UserAdvancedSkills.json` (what
Skua's `AdvancedSkillContainer` loads and scripts use), searchable; per rotation: edit, delete, back to the community
version; new rotations and modes; reset everything to the community file. The editor has the desktop's options (mode,
use if available / wait for cooldown, timeout, reset on target change, skills 0-5 in order) and per skill its rules
(wait, own HP, MP, a party member's HP above/below in % or points, one aura or several with AND/OR on you or the target,
skip if not available). Files are read and written by `Skills/SkillSetStore.cs` in Skua's JSON; a saved rotation is
built by the desktop's own `SkillItemViewModel.Convert` and Skua's `AdvancedSkillsParser`, other rotations are left as
they are in the file, and Skua's container is reloaded (the next `LoadAdvanced` of a script uses it; a running script
keeps the rotation it already loaded, as on the desktop). Two slips of the desktop parser are corrected on the way in
so that open + save changes nothing (checked over every rotation of the community file): the skip flag after an aura
rule, and "<" multi-aura checks after a ">" one (the desktop editor loses both on save).

*Community updates and edits.* The sync refreshes `AdvancedSkills.json`; before each run (and when the page opens)
`SkillSetStore.UpdateFromCommunity` merges it into the user file against `UserAdvancedSkills.base.json` (the community
file it was last merged with): untouched rotations take the new version, edited ones stay, deleted ones stay deleted,
the user's own stay, new ones are added. The desktop instead only adds classes and modes the user file lacks (so
untouched rotations stop updating and deleted ones come back); before this version the app overwrote the user file.
Skua's container would also download the file from GitHub itself and merge in the background: the host turns that off
(`HeadlessSkuaOptions.ConfigureSettings`), so there is one writer.

**CoreBots options** (desktop: CBOptionsView, CBOClassSelectView, CBOClassEquipmentView, CBOLoadoutView,
CBOOtherOptionsView), for the logged-in account. Classes: solo, farm, dodge and boss class (the character's enhanced
classes and "[Current]") with the rotation mode (modes the class has in Advanced Skills) and gear switching;
Equipment: helm, armor, cape, weapon, pet and ground item per slot from the inventory; Options and Other: the desktop's
lists (private rooms with the desktop's public-room warning, room number, delays, quest limits, stop map, boosters,
nation, PvP, bot creator); plus "More settings CoreBots reads" (keys from the synced Core*.cs no tab writes). Reload
reads the file, Load character re-reads classes and gear keeping the selections. The class and equipment logic is the
desktop's `CBOClassSelectViewModel`, `CBOClassEquipmentViewModel` and `CBOLoadoutViewModel` (compiled in) on Skua's
services of the engine's session (`IBotEngine.SkuaServices`). The file is written in the desktop's order and format;
unlike the desktop, lines no page knows are kept after them unless CoreBots would read a written line for them anyway
(it matches keys by prefix: the old editor's "ActionDelay" is read from "ActionDelayNr"). Names and paths go through
`CoreBotsOptions.FileFor` (account-name characters only, inside the options folder).

**Why `Skua.App.Droid.Linked`.** The desktop ViewModels keep their namespace `Skua.App.Avalonia...`. Visible to the
other projects, that namespace would hide the `Avalonia` namespace from code in `Skua.App.*` namespaces that writes
`Avalonia.Xyz` (several files do). So `.Core` references it with `Aliases="desktop"` and `PrivateAssets="all"`, and the
views bind to `.Core` wrappers (`SkillRow`, `AuraCheckRow`, `CoreBotsSlot`, ...), never to desktop types.

## Control page

There is no game rendering, so the Control tab (bottom bar: Home, Scripts, **Control**, Console, Plugins, Settings)
shows the game schematically and lets you act by hand. It took World's place in the bar: the World lists (inventory,
temp, bank, quests, drops, monsters) open from Control's *Lists* button, and Back returns to Control.

**Reputation** (a tab of the World lists): every faction of Skua's `IScriptFaction.FactionList` (the headless client's
rank math) with name, ID, rank, reputation in the rank, the rank's span, what remains and the total; highest rank
first, searchable by name or ID with the lists' filter, a progress bar to the next rank and MAX at rank 10
(`Core/World/Reputation.cs`). It is read on a worker thread when the world changed, at most every 400 ms (the Control
page's interval), only while the tab is shown and the app is in the foreground.

* **Map**: map and room, server, and the map's cells as tiles in map order: the frame labels of the map file
  (`Skua.Core.Headless/Maps`, downloaded once and cached); when the file cannot be read (LZMA) the cells seen in the
  room data (monsters' and players' cells, Enter). Each tile shows live/total monsters and other players; yours is lit,
  cells with live monsters have an orange edge. Tap a tile to move there (the cell's default pad: Spawn, else Left,
  else its first pad, as skua.swf's jumpCorrectRoom); the pad row moves within your cell.
* **Cell**: your HP and target; monsters (name, level, HP bar, state) with *Attack* (the client's attackMonsterID:
  target plus auto attack); players (level, state, afk) with *Go to*; skill bar slots 1-5 (the client's readiness
  rules: cooldown, global cooldown, mana, target); Cancel target, Rest, Resurrect (when dead); drops with Pick up /
  Reject.
* **Actions**: *Travel* (join a map, with the last 8 joined maps; /goto a player by name; everyone in the room),
  *Shop* (load by ID; each item with price and the client's checks: unmet materials or quest, gold/AC, membership,
  reputation, how many can be bought; Buy), *Quest* (load by ID; requirements, progress, rewards; Accept, Turn in),
  *Bank* (load; bank to backpack and back; Sell from the backpack while a shop is loaded).

How it works: `IControlBackend` (Core) is implemented by `Skua.App.Droid.Plugins/Control/ControlBackend.cs` on the
engine's one Skua session, created next to the plugins (`IPluginBackends.Control`, so the script host adapter is
unchanged). Reads come from the headless world model and client rules (`HeadlessGame`); actions go through Skua's
script API (`Map.Jump`, `Combat.Attack`, `Skills.UseSkill`, `Shops`, `Quests`, `Bank`, `Inventory`, `Drops`,
`Player.Rest/Goto`), the client rules (`GameClient.Resurrect`, `ShowQuests`) and `GameCommands.JoinMapAsync` (for the
server's refusal text). The page never builds a packet; raw packets stay in the Console's packet logger.

Safety: one action at a time; while a script runs, every action first asks "A script is running; do it anyway?";
buying (quantity and price, AC purchases in red), selling, bank moves, quest turn-ins and rejecting a drop ask for
confirmation. Actions wait for the game's own throttles (`world.lock`: shop, buy, sell, quest, rest, map transfer);
moves within the map are spaced 0.5 s and /goto 3 s apart by the page, skills by the client's cooldowns. Names typed
for maps and players are checked (letters, digits, `-`, `_`, spaces for players) before anything is sent. Failures
show on the page and go to the Console (source `control`); nothing there throws into the UI.

Cost: the page reads the game only while it is open and the app is in the foreground, at most every 400 ms (2.5 UI
updates per second), on a worker thread; a list is replaced only when its content changed. In the background it does
nothing; returning reads the latest state once.

## Fast Travel, Loader and Grabber (Settings > Tools)

Ports of the desktop's Fast Travel, Loader and Grabber windows, as pages of the Tools hub (`ToolsViewModel`, a list of
`ToolEntry(Title, Description, Open)`, opened from the top of Settings; the notification pages below share it). They
are added by `GameToolPages.AddTo` (as `NotificationTools.AddTo` adds the notification pages); Back closes an open
question, then the page, then the hub. Their game side is `IGameTools` (Core), implemented in `Skua.App.Droid.Plugins/Tools` on the engine's Skua
session and exposed as `IPluginBackends.GameTools` (like the Control page's backend).

* **Fast Travel**: the desktop's list itself (Skua's `FastTravels` setting, same file and `name,map,cell,pad` lines,
  `Skua.Core.Models.FastTravelEntry`); search by name, map or cell; one tap travels through Skua's `IMapService.Travel`
  (private room toggle and number, default on / 111111 like the desktop, saved in `files/droid/tools.json`; the
  tercessuinotlim detour through m22); add (with *Current*: the map, cell and pad you are in), edit, delete, clear,
  and add Skua's presets (`DefaultFastTravels`).
* **Loader**: load a shop (one ID; the Control page's `LoadShop`, which checks it arrived) or quests (`1,2 3`; Skua's
  `Quests.Load`, then waits for them) by ID, then *View in Grabber*; Skua's quest list (`QuestData.json`, kept current
  by the community script sync) with the desktop's search, select, copy names / IDs / `// name | id` lines, load, and
  *Fake* (Skua's client-only `UpdateQuest`); Skua's updater for new IDs or a range, with progress and Cancel. The input
  rules and copy formats are the desktop's (`Skua.Core.Models.Quests.LoaderText`). At most 200 rows show; search narrows.
* **Grabber**: the desktop's ten tabs (Skua's `GrabberService.Grab`), search in the desktop's summary lines
  (`Skua.Core.Services.GrabberText`), select, copy IDs / names / the desktop's Copy text, a per-row ID copy, the
  desktop's Export text to the share sheet, and the desktop's buttons per tab: Buy; Load Shop; Open, Accept, Register,
  Fake Complete, Unregister All; Get Map Item; Equip, Sell, Sell All, To Bank; To Inventory; Kill, Teleport To.
  Buying, selling, loading a shop, accepting and the bank moves call the Control page's backend (same checks and
  verification); the rest calls Skua's `GrabberService` tasks that ask nothing. The desktop's refusals apply (AC items
  that cost AC, not enough gold, one sale at a time, equipped items). *GetMap Item IDs* lists only map items the
  desktop saved before: Skua finds them by decompiling the map file with FFDec, Windows only.

Safety: the pages never use Skua's dialog service (it declines every unattended confirmation here); they ask with
their own confirmation card (`ConfirmHost`). Buy, Sell, Sell All, Get Map Item (with the desktop's quantity
questions), the bank moves, Equip and Register ask first; deleting, clearing and adding presets in Fast Travel ask;
the quest list update asks. While a script runs every action first asks "A script is running; do it anyway?". One
action at a time per page; long Grabber tasks (Kill) and the quest update can be cancelled. Actions go through Skua's
API and the client's own throttles (`world.lock`).
## Packet Spammer (Settings > Tools)

Port of the desktop's Packet Spammer (`PacketToolPages`, `PacketSpammerViewModel`, `Core/Packets/PacketSpammer.cs`):
a list of game packets sent in turn, one every interval, Start/Stop, add, remove, clear, send once; the list and its
options are kept in `files/droid/tools.json`. The desktop's "send to client" is left out (no client to show them).

Limits, because spamming is what gets accounts flagged (the page says so, and every Start asks with that warning):

* only one-line client game packets (`%xt%zm%command%room%...`, at most 2000 characters); SmartFox system messages,
  JSON and `login`/`firstJoin` are refused; at most 50 packets in the list;
* the interval is at least 1 s, and never below twice the send pacer's "slow down" gap or four times its normal gap
  (`SpammerLimits.MinimumFor`, from `IBotEngine.GetConnectionDetails`); a shorter one is raised and the question says so;
* a run sends at most 500 packets (the page's "at most" can only lower it);
* packets go through the engine's paced send queue like any other (no "Raw packet sent" line each; the run is logged
  once at its start and end, source `spammer`);
* a run stops by itself before the next packet when the account is not online, when the connection changed or dropped
  during the run (even if the engine already reconnected), when a packet was not sent, or when the server sent a
  "slow down" warning (the connection's counter of them went up).

## Packet Interceptor (Settings > Tools)

Port of the desktop's Packet Interceptor (`PacketInterceptorViewModel`, `Core/Packets/PacketInterception.cs`). The
desktop redirects the Flash socket through Skua's localhost `CaptureProxy` and runs `IInterceptor`s on each message;
headless there is no separate client, so the interceptor chain sits in `AqwConnection` itself
(`AqwConnectionOptions.Interceptor`, `Skua.Core.Headless/Net/PacketInterception.cs`): outbound game packets are offered
in the writer just before they are written, inbound messages in the reader after framing and decompression and before
the world model applies them. The engine sets its one `PacketInterception` on its session's connection options, so
every connection (reconnects included) uses it.

* **Rules** (at most 30, applied in order, kept in `tools.json`): direction (out, in, both), an optional command
  (matched against every command the message carries, the desktop's `PacketCommands.Parse`, compiled in through
  `Skua.App.Droid.Linked`), an optional text to find (case-sensitive), and either *replace* (every occurrence) or
  *block* (drop). A block rule needs a command or a text; each rule has its own switch; Up reorders.
* **Master switch**: off by default and at every start of the app (never saved); turning it on asks with the warning;
  the Console logs on/off (source `interceptor`). While off, or on without an enabled rule, each message costs one check.
* **Log**: every message a rule changed or dropped (time, direction OUT / IN / BLOCK as on the desktop, the rules,
  the desktop's categories, before and after, scrubbed with the engine's secret scrubber), newest first, at most 300,
  searchable, cleared, shared as the desktop's export lines (`[OUT] packet`). It refreshes every 0.5 s only while
  the page is shown. Diagnostics shows the switch and counters; the connection counts changed and dropped messages.

Safety limits, enforced by the connection, not by the rules:

* nothing is offered before the connection's character has loaded (after the first `moveToArea`, the character data
  and the inventory): the SmartFox handshake, the login with its token, the login response and the character load
  are never seen, changed or dropped; every reconnect starts over;
* SmartFox system (XML) messages are never offered, only `%xt%` and JSON game messages;
* a message that contains a redacted secret (the login token) is passed on untouched and not offered;
* the messages the connection's own safety depends on are never offered: outbound `login`, `firstJoin`,
  `retrieveUserData` (the liveness probe); inbound `loginResponse`, `warning` and `server` (the "slow down" warnings
  the send pacer reacts to), `multiLoginWarning`, `logoutWarning`, `logout`;
* a changed outbound message must still be one `%xt%` packet without NUL whose command is not protected, a changed
  inbound one must still parse as a game message; otherwise the original is used (logged). An interceptor that throws
  is logged and the original is used. A dropped outbound packet completes as "not sent" and does not move the pacer.

## Diagnostics (Settings > Tools)

Port of the desktop's Diagnostics, Runtime helpers and log windows (`DiagnosticsToolPages`, `DiagnosticsViewModel`,
`Core/Diagnostics/DiagnosticsReport.cs`). Read on a worker thread once a second, only while the page is open, the
Settings tab shows the hub and the app is in the foreground.

* **Engine**: connection phase and status, script and its state, run time, Skua's counters, background work, whether
  packet capture and the packet interceptor are on.
* **Connection** (`IBotEngine.GetConnectionDetails`): server and host:port, state, client version, latency (the time
  from writing a request the server always answers, such as character data, quests, a shop or the bank, to reading
  its answer, measured in `AqwConnection`; last and moving average), packets per minute in and out over the last
  minute (from the connection's counters), totals, the send queue and drops, the pacer's gaps, slow-down warnings,
  liveness probes, time since the server last sent anything.
* **Memory**: RSS and peak, PSS (`/proc/self/smaps_rollup`, read on demand), managed heap and total allocated, GC
  collections, loaded script assemblies, thread pool, the watchdog's threshold, mode and current verdict.
* **Flash gaps**: `HeadlessFlashUtil`'s gap log (Missing / Impossible / Approximated, name and argument shape, count,
  what was answered instead), most frequent first.
* **Process exits**: the exits Android reported (the `previous exit (...)` lines the crash reporting writes to the
  events log), newest first, and whether a crash report waits.
* **Runtime helpers** (desktop Runtime window, read only): Skua's registered quests, the drop grabber's list, the
  boosts chosen; an opt-in switch for Skua's own event recorder (`IDiagnosticsService`: script starts and stops,
  waits, relogins) whose events are listed.
* **Share / Copy**: one text bundle of every section, the gap log, the exits, the last 150 events log lines and the last
  150 engine log lines, scrubbed: the engine's registered secrets (password, login tokens, webhook URL), the
  scrubber's credential-like key/value patterns, Discord webhook URLs, e-mail addresses, and the account and
  character names (replaced by `<account>`).

## Pause and resume

*Pause* (Scripts page, Dashboard, notification action) uses Skua's own pause unchanged
(`ScriptManager.SetPausedAsync`, the desktop's Pause button and hotkey): the skill timer pauses and the script
thread blocks on `ScriptPauseEvent` at its next `Bot.Sleep` / `Bot.Wait` call (every wait loop of Skua and CoreBots
goes through them); *Resume* stops Skua's auto attack and releases the thread. Nothing in it needs Flash or a window,
so it works headless as is. The adapter mirrors `ScriptPaused` into `ScriptStatus.Paused`; everything that shows the
script state shows "Paused" (Scripts page, Dashboard, notification, heartbeat). A paused script is still busy: the
memory watchdog does not restart, the wake lock stays, and the session keeps its keepalive and AFK clearing (they
live in the connection, not in the script). A script that never calls Sleep or Wait does not stop until it does.
Stop works while paused (Skua cancels the wait). If the connection drops while paused, Skua's relogin restarts the
script unpaused, as on the desktop. The notification's Pause/Resume action is an explicit, immutable PendingIntent
to the engine service, which is not exported.

## Script queue

The desktop's Script Scheduler, on the Scripts page (*Queue* tab; *Add to queue* in the Library tab). The queue
(`Skua.App.Droid.Core/Scheduler/ScriptScheduler.cs`) lives with the engine in `EngineHost` for the whole process, not
in a ViewModel: it keeps running in the background (the foreground service and its wake lock stay up while it runs,
also between runs), and a recreated activity shows the same state. The notification shows the current entry
("Queue 2/5: Gold farm (run 1/3)"); its Stop stops the queue's script and with it the queue.

**Per entry**: display name (desktop default: the file name, "Name (2).cs" for a second copy), runs (or repeat until
stopped), stop after N minutes (the entry's total run time, paused time excluded), wait before the next run, on
error: skip / stop the queue / retry N times, and two profiles:

* *Script options*: the entry's own Skua option storage `Queue_<script>_<id>` (the desktop's scheme), edited in the
  usual options editor and saved by Skua as `options/<storage>.cfg`. The run starts with Skua's `OverrideStorage` set
  to it and `SilentConfig` on; both stay set until the next start, so Skua's relogin restart of the script reads the
  same profile (the desktop clears it when the script stops, so its relogin restart falls back to the script's own
  options).
* *CoreBots profile*: CoreBots' settings (edited over the account's current CBO file) stored with the entry and
  written to `options/CBO_Storage(<account>).txt` right before each run of the entry. The file then keeps the last
  applied profile. Without a profile the file is left as it is.

Dialogs are answered by the headless policy (see *Unattended dialogs*). Errors are shown as one plain-English
sentence ("The script does not compile: ...", "The script crashed: it used something that was not there
(NullReferenceException: ...)"). Playlists (queue plus settings) are saved by name in `files/droid/playlists/`; a
loaded playlist leaves out entries whose script is gone. *Continue* runs from the entry the queue stopped at.

**Relogin.** Ported from the desktop: a run that ends while the account is disconnected (the engine was not online
during or right after it) is not counted ("Disconnected"); the queue waits for the connection. If Skua's relogin
restarts the same script (it does after a drop while a script runs, when AutoRelogin is on), the queue adopts that
run: no second start, nothing skipped. If the account is back and the script was not restarted within 8 s, the
queue starts the same entry again. Before every run the queue waits until the account is online (the desktop's
"Scheduler Paused"). The script's Stop (any page, the notification) and logging out stop the queue.

**Memory and restarts.** Script assemblies are never unloaded (Mono), and the app reuses identical ones through
`ScriptAssemblyStore`, so re-running a script costs nothing but a long queue of different scripts grows memory. The
watchdog only restarts when no script runs and the engine was idle for 30 s, which a queue rarely is. So between two
runs (never during one, never before the first run after a start) the queue asks `EngineHost`: if memory is over the
threshold and an automatic restart is allowed now (auto restart on, threshold not too low, 3 min uptime, 15 min since
the last automatic restart) and the session can be restored (a remembered password), the engine restarts. Before
every restart (this one, the watchdog's, the restart button) the queue saves its position with "resume after the
restart"; after the restart's login (or, if the login could not be resumed, once the user logs in) it continues at
that run: the entry that ran is not repeated and the next one is not skipped. A restart while the queue waited for a
reconnect mid-entry runs that entry again. The desktop has no equivalent (its queue is in memory only). If the
process dies without a planned restart (crash, killed), the queue is not resumed by itself: it shows "Interrupted"
and *Continue* resumes there. The state (`files/droid/queue.json`) is written at every step; it holds no secrets.

## Plugins

The desktop app's three plugins are built in (Plugins tab): each has an on/off switch and, where it can run, a page.
Their state and options live in `files/droid/plugins.json` (app-private, no secrets). The desktop plugins themselves
cannot be loaded: their windows are Avalonia 11 and this app is Avalonia 12, so their UI-free logic is reused instead.

| Plugin | Here | How |
|---|---|---|
| Daily & Weekly Tracker | everything: daily/weekly quests, ultra boss lockouts, refresh, batch loading, refresh after a turn-in, after a script stops and every 5 minutes while open, Start for the matching community script | the quest table and the status checks are in `Skua.Plugins.Shared` (UI-free); the desktop plugin compiles the same files in |
| ButlerV5 | one account: follow any player by name, go to / jump to a player, follow options, recent targets, players in the room, the log | `PlayerFollower` (the desktop follower's in-room logic; it finds the player through the game instead of the master's sync file); `AccountConfig.cs` compiled in unchanged |
| Cosmetics | listed, cannot be switched on | every action only changes what the local game view draws (equipment slots and art, colours, pet, fake `genderSwap` packets to the client); it sends nothing to the server, and saved outfits are catalog art, not owned items |

**ButlerV5, army work.** Summon, the roster, send-all, quest sync and the order/decline handshake need several local
accounts. Adding them means: several Skua sessions in one process (one per account), the desktop sync file format
(`Skua.Plugin.ButlerV5/SyncFile.cs`, UI-free) under app-private storage, its `Broadcaster` and order watcher per
session, and the desktop `Follower` for followers whose master is local. Not ported because they cannot matter
headless: quest bypasses, quest cloning, unlock-all and the level fake (client-side gates; the headless client has
none), AntiLag and the butler display options (nothing is drawn).

**Third-party plugin DLLs are not loaded.** The desktop loads any DLL from its plugins folder with `Assembly.LoadFrom`
and runs it with full trust. On Android that would need: a file picker or download into app-private storage (Android
forbids executing code from shared storage), `AssemblyLoadContext.LoadFromStream` (no file paths in the APK sandbox),
plugins built against Avalonia 12 or against a UI-free plugin contract the app renders itself (every existing plugin
opens Avalonia 11 windows), and Mono never unloading them. The security cost is the real obstacle: a loaded plugin runs
inside the app's process with everything the app has, the Keystore-decrypted password while logging in, the session
token, the account's characters, network access and the foreground service, and Android has no sandbox inside a
process. It would need at least an explicit warning, a hash or signature allowlist, and the same review as a script,
which already runs with these rights (scripts are source compiled on the device; a DLL is opaque).


## Notifications, junk and Discord (Settings > Tools)

`NotificationHub` (`Skua.App.Droid.Core/Notifications`) is created with the engine and runs without any UI. Skua's game
and script messages reach it through `IGameEvents` (`Skua.App.Droid.Scripting/SkuaGameEvents.cs`); the message handlers
only queue, one background worker does the rest, so the Flash bridge and the script thread never wait.

* **Notification Center**: the desktop's entries and filters, newest first, at most 500, kept in
  `files/droid/notification-history.json` (scrubbed like crash reports).
* **Notify Drop**: a watch list (`|`-separated input, whole name, case-insensitive) in `files/droid/notify.json`. A watched
  item dropped or obtained posts a notification on its own channel "Watched drops" (`DropNotifications.cs`, default
  importance; `EngineService` is untouched); tapping opens the app; one notification per item, alerting once while shown;
  a secure lock screen shows only "A watched item dropped" unless item names are allowed there.
* **Current drops**: the World page's Drops tab picks up, rejects, picks up all, and adds a drop to the watch or junk list.
* **Junk Items**: Skua's `IJunkService` and its `JunkItems.json` in the script folder (the desktop's file and format; the
  community sync replaces it like the desktop's auto-update does). Sell all junk (equipped items kept). Android adds
  *reject junk drops automatically*: Skua's `RejectExcept` for the junk drop, unless the running script asked for it or it
  is watched. The headless bridge's `rejectExcept` closes the rejected drops in the world model, as the game client does.
* **Discord webhook**: the URL must be `https://` on discord.com, discordapp.com or their canary./ptb. subdomains,
  `/api[/vN]/webhooks/<id>/<token>`, optionally `?thread_id=`; it is a credential, so it is encrypted in the Keystore
  secret store (`no_backup/secrets`, entry `discord-webhook`), registered with the diagnostics scrubber, never logged or
  shown again. Cards (`WebhookPayloads`): Script Started, Farming Session Concluded, Script Error (script name only),
  Kicked/Disconnected & Relogging, Scheduler Paused (`NotificationHub.NotifySchedulerPaused`), Rare Drop! for watched items
  (30 s per item). They carry script, item and map names, counts and durations; the character name only when allowed
  (else "VibeSkua Bot"); `allowed_mentions` is empty. One queue (`DiscordWebhookSender`): 429 waits for `retry_after` /
  `Retry-After` (at most 60 s, 5 attempts), an empty bucket delays the next message, 2 messages per second at most, the
  oldest message is dropped when 50 wait.
