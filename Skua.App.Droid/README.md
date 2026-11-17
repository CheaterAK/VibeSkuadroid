# VibeSkua for Android (Skua.App.Droid)

Native, headless Android client: no Flash, no game rendering. Avalonia 12 UI, .NET 10 on Mono, one account.

## Projects

| Project | TFM | What |
|---|---|---|
| `Skua.App.Droid.Core` | net10.0 | `IBotEngine` seam, `HeadlessBotEngine` (on Skua.Core.Headless), `EngineHost` (login + remember me, resume after restart, memory watchdog), ViewModels, settings, credential vault format, script library |
| `Skua.App.Droid.Scripting` | net10.0 | script reference images (`ScriptReferenceSet`), content-addressed `ScriptAssemblyStore`, the pending `SkuaScriptHostAdapter` |
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

Script execution waits for the Skua.Core headless host project (HeadlessFlashUtil + DI entry point). Until then
the app logs in, joins maps, shows live state and sends raw packets; the Scripts page browses and imports scripts
and reports that running them is not available yet. The one class to complete is
`Skua.App.Droid.Scripting/SkuaScriptHostAdapter.cs` (see its HOST-PROJECT notes).

Reference images scripts compile against are packed as APK assets under `assets/script-refs/` by
`ScriptReferences.targets`; the tests compile real scripts through Skua.Core against exactly that set.
