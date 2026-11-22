#!/usr/bin/env bash
# Builds and tests VibeSkua for Android: fetches the game view's Ruffle build, platform-neutral tests, then the Debug
# and Release APKs.
# Usage: Skua.App.Droid/build-droid.sh [apk-output-dir]
# Needs the .NET 10 SDK with the android workload and the Android SDK from build/android/setup-android-sdk.sh.
# Optional release signing: set DROID_KEYSTORE (path), DROID_KEY_ALIAS, DROID_KEYSTORE_PASSWORD, DROID_KEY_PASSWORD.
set -euo pipefail
cd "$(dirname "$0")"

export ANDROID_HOME="${ANDROID_HOME:-/opt/android-sdk}"
export JAVA_HOME="${JAVA_HOME:-/usr/lib/jvm/java-21-openjdk-amd64}"
# Avalonia.BuildServices otherwise tries to send build telemetry.
export AVALONIA_TELEMETRY_OPTOUT=1
SDK_ARGS=(-p:AndroidSdkDirectory="$ANDROID_HOME" -p:JavaSdkDirectory="$JAVA_HOME")
SIGN_ARGS=()
if [ -n "${DROID_KEYSTORE:-}" ]; then
  : "${DROID_KEY_ALIAS:?DROID_KEY_ALIAS must be set with DROID_KEYSTORE}"
  : "${DROID_KEYSTORE_PASSWORD:?DROID_KEYSTORE_PASSWORD must be set with DROID_KEYSTORE}"
  : "${DROID_KEY_PASSWORD:?DROID_KEY_PASSWORD must be set with DROID_KEYSTORE}"
  SIGN_ARGS=(-p:DroidKeystore="$DROID_KEYSTORE" -p:DroidKeyAlias="$DROID_KEY_ALIAS")
fi
OUT="${1:-}"

# The game view's Ruffle web build (pinned and checksummed; not in git).
./fetch-ruffle.sh
dotnet test Skua.App.Droid.Tests -c Release
# The APKs are always built from a clean Android head: incremental Android builds can reuse generated Java
# wrappers and native tables from an earlier build (see AndroidEnableMarshalMethods in the .csproj).
rm -rf Skua.App.Droid/obj Skua.App.Droid/bin
dotnet build Skua.App.Droid -c Debug "${SDK_ARGS[@]}"
dotnet publish Skua.App.Droid -c Release "${SDK_ARGS[@]}" "${SIGN_ARGS[@]}"

APKS=(
  Skua.App.Droid/bin/Debug/net10.0-android/com.vibeskua.droid.debug-Signed.apk
  Skua.App.Droid/bin/Release/net10.0-android/com.vibeskua.droid-Signed.apk
)
if [ -n "$OUT" ]; then
  mkdir -p "$OUT"
  cp "${APKS[@]}" "$OUT/"
  APKS=("${APKS[@]/#*\//$OUT/}")
fi
ls -l "${APKS[@]}"
sha256sum "${APKS[@]}"
