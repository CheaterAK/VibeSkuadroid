# Android build toolchain (Linux)

How to build `net10.0-android` projects (currently only `Skua.Droid.Spike/`) on Linux without
Visual Studio or Android Studio.

## What is needed

| Piece | Version | Where it comes from |
|---|---|---|
| .NET SDK | 10.0.115 | apt (`/usr/lib/dotnet`) |
| .NET `android` workload | 36.1.118 | `dotnet workload install android` (file-based, under `~/.dotnet`) |
| JDK | OpenJDK 21 (`/usr/lib/jvm/java-21-openjdk-amd64`) | apt `openjdk-21-jdk-headless`; the workload accepts 17.0 to 21.0.99 |
| Android cmdline-tools | 19.0 (`commandlinetools-linux-13114758`) | dl.google.com |
| `platforms;android-36` | r02 | sdkmanager |
| `build-tools;36.0.0` | 36.0.0 | sdkmanager |
| `platform-tools` | latest (37.0.x) | sdkmanager |

These match the defaults in the workload's targets (`AndroidSdkPlatformVersion=android-36`,
`AndroidSdkBuildToolsVersion=36.0.0`, `AndroidCommandLineToolsVersion=19.0`). No NDK is needed,
including for the Mono AOT build: the workload brings its own cross compilers and linker.

## Setup

```sh
sudo ANDROID_HOME=/opt/android-sdk build/android/setup-android-sdk.sh
```

The script is idempotent. It only reaches `https://dl.google.com`. The workload's own
`-t:InstallAndroidDependencies` target is not used because it fetches its manifest through
`aka.ms`. Behind an HTTPS proxy the script passes `$HTTPS_PROXY`'s host and port to `sdkmanager`.
The proxy's CA must already be in the JVM truststore (here `JAVA_TOOL_OPTIONS` points at it).
The SDK takes about 470 MB on disk.

## Building

```sh
export ANDROID_HOME=/opt/android-sdk
export JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64
dotnet build   <proj> -c Debug   -p:AndroidSdkDirectory=$ANDROID_HOME -p:JavaSdkDirectory=$JAVA_HOME
dotnet publish <proj> -c Release -p:AndroidSdkDirectory=$ANDROID_HOME -p:JavaSdkDirectory=$JAVA_HOME
```

The `*-Signed.apk` in `bin/<Configuration>/net10.0-android/` is signed with the auto-generated
debug keystore (`~/.local/share/Xamarin/Mono for Android/debug.keystore`). It can be sideloaded
but must not be published.

For the spike, `Skua.Droid.Spike/build-spike.sh [out-dir]` builds the Linux console baseline and
all three APKs (Debug, Release, Release-aot).

## Gotchas found

* A Debug build normally relies on Fast Deployment, so the APK does not contain the assemblies.
  Set `EmbedAssembliesIntoApk=true` to get a standalone APK.
* `RunAOTCompilation=true` requires `PublishTrimmed=true` (error XA1030). To get AOT without
  losing members, root every assembly (`TrimmerRootAssembly ... RootMode="All"`). See
  `Skua.Droid.Spike/Spike.Android/Spike.Android.csproj`.
* `PublishTrimmed=true` also changes feature switches (`UseSystemResourceKeys=true`, and with
  `TrimMode=full` reflection-based System.Text.Json is turned off). Set them back explicitly
  when dynamically loaded code needs them.
* The repo-root `Directory.Build.props`, `Directory.Build.targets` and `Directory.Packages.props`
  (central package management) would apply to any new project below the root. The spike has
  empty copies of all three in its own directory to cut them off.
* No Mono runtime pack for desktop Linux (`Microsoft.NETCore.App.Runtime.Mono.linux-x64`) is
  published for .NET 10, so Mono cannot be tried on Linux. The Linux baseline runs on CoreCLR.
* There is no KVM in this container, so no emulator. APKs are built here and tested on a real
  phone.
