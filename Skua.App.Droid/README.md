# VibeSkua for Android (Skua.App.Droid)

Native, headless Android client: no Flash, no game rendering. Avalonia 12 UI, .NET 10 on Mono, one account.

## Projects

| Project | TFM | What |
|---|---|---|
| `Skua.App.Droid.Core` | net10.0 | `IBotEngine` seam, `HeadlessBotEngine` (on Skua.Core.Headless), `EngineHost` (login + remember me, resume after restart, memory watchdog), ViewModels, settings, credential vault format, script library |
| `Skua.App.Droid.Scripting` | net10.0 | script reference images (`ScriptReferenceSet`), content-addressed `ScriptAssemblyStore`, `SkuaScriptHostAdapter` (Skua.Core through `HeadlessSkuaSession`) |
| `Skua.App.Droid.UI` | net10.0 | Avalonia 12 views (compiled bindings) |
| `Skua.App.Droid` | net10.0-android | Android head: activity, foreground `EngineService`, `KeystoreSecretStore`, restart, manifest |
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

## Signing

Default: the SDK's auto-generated debug key (sideload only). Release signing is opt-in and needs no file in the
repo: pass `-p:DroidKeystore=<path> -p:DroidKeyAlias=<alias>` with `DROID_KEYSTORE_PASSWORD` and
`DROID_KEY_PASSWORD` in the environment (`build-droid.sh` does this from `DROID_KEYSTORE`, `DROID_KEY_ALIAS`).
In GitHub Actions set the secrets `DROID_KEYSTORE_BASE64`, `DROID_KEY_ALIAS`, `DROID_KEYSTORE_PASSWORD`,
`DROID_KEY_PASSWORD` (see `.github/workflows/android-droid.yml`). Never commit a keystore.

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

**Unattended dialogs.** Message boxes never wait: yes/no questions get *No*, button choices prefer OK / Continue /
Close / Skip / None / No / Cancel, else the first button. Every question and its answer is logged as a `dialog`
warning in the Console. Script option windows are skipped (saved options are used).

**Options.** *Options* on the Scripts page compiles the selected script (later starts reuse the compile) and edits
the options it declares (Skua's `Options` / `MultiOptions` lists: toggles, numbers, text, enum choices), saved to
`options/<storage>.cfg` where Skua reads them. Transient options are shown read-only (reset every run).
*CoreBots options* edits CoreBots' own settings (`options/CBO_Storage(<user>).txt`: class and gear sets, private
rooms, delays...); the keys are read from the synced Core*.cs sources. Without that file CoreBots uses its defaults.

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
* Paths are validated exactly like imports (`ScriptLibrary.ResolveDestination`); URLs must be HTTPS on that host.
  Files the repository does not list (your own scripts) are never touched.
* A 429 (or 403 with rate limit headers) stops the sync and says when to retry; what was downloaded is kept and the
  next sync continues from there (files whose SHA-256 matches are skipped). Server errors are retried twice.
* It runs as engine background work: the foreground service keeps the process alive while it runs in the
  background, and *Cancel* stops it.

Measured once from Linux: 1953 scripts (13.2 MB) in 28 s.

Reference images scripts compile against are packed as APK assets under `assets/script-refs/` by
`ScriptReferences.targets`; the tests compile real scripts through Skua.Core against exactly that set.
