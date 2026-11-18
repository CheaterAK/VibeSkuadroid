# VibeSkua for Android (Skua.App.Droid)

Native, headless Android client: no Flash, no game rendering. Avalonia 12 UI, .NET 10 on Mono, one account.

## Projects

| Project | TFM | What |
|---|---|---|
| `Skua.App.Droid.Core` | net10.0 | `IBotEngine` seam, `HeadlessBotEngine` (on Skua.Core.Headless), `EngineHost` (login + remember me, resume after restart, memory watchdog), ViewModels, settings, credential vault format, script library |
| `Skua.App.Droid.Scripting` | net10.0 | script reference images (`ScriptReferenceSet`), content-addressed `ScriptAssemblyStore`, the pending `SkuaScriptHostAdapter` |
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

## If the app crashes

The launcher entry is a plain Android activity (no Avalonia). Crash capture is installed in the Application
constructor; any crash (managed exception, unobserved task exception, uncaught Java throwable) is written to
`files/diagnostics/crash-report.txt` inside the app sandbox, and startup milestones go to logcat (tag `VibeSkua`), to
`files/diagnostics/events.log` and to a step marker. On the next launch, before Avalonia starts, the crash screen
shows the report and the recent events with Share and Copy; Continue clears it and opens the app. A start that
died without any exception (native crash, abort) is reported from the marker ("last reached step N"). Reports are
scrubbed of the password, login tokens and credential-like `key=value` pairs. If even the crash screen never
appears, take a bug report (Developer options > Take bug report) or `adb logcat -s VibeSkua AndroidRuntime DEBUG`.

## Scripts

Script execution waits for the Skua.Core headless host project (HeadlessFlashUtil + DI entry point). Until then
the app logs in, joins maps, shows live state and sends raw packets; the Scripts page browses and imports scripts
and reports that running them is not available yet. The one class to complete is
`Skua.App.Droid.Scripting/SkuaScriptHostAdapter.cs` (see its HOST-PROJECT notes).

Reference images scripts compile against are packed as APK assets under `assets/script-refs/` by
`ScriptReferences.targets`; the tests compile real scripts through Skua.Core against exactly that set.
