#!/usr/bin/env bash
# Builds the game view spike: fetches and verifies the pinned Ruffle builds, runs the unit tests,
# then the Debug and Release APKs (both signed with the SDK's auto-generated debug key).
# Usage: ./build-ruffle-spike.sh [apk-output-dir]
# Needs the Android SDK from ../build/android/setup-android-sdk.sh.
set -euo pipefail
cd "$(dirname "$0")"

export ANDROID_HOME="${ANDROID_HOME:-/opt/android-sdk}"
export JAVA_HOME="${JAVA_HOME:-/usr/lib/jvm/java-21-openjdk-amd64}"
SDK_ARGS=(-p:AndroidSdkDirectory="$ANDROID_HOME" -p:JavaSdkDirectory="$JAVA_HOME")
OUT="${1:-}"

./fetch-ruffle.sh
dotnet test RuffleSpike.Tests
dotnet build RuffleSpike.Android -c Debug "${SDK_ARGS[@]}"
dotnet publish RuffleSpike.Android -c Release "${SDK_ARGS[@]}"

APKS=(
  RuffleSpike.Android/bin/Debug/net10.0-android/com.vibeskua.droid.ruffle.debug-Signed.apk
  RuffleSpike.Android/bin/Release/net10.0-android/com.vibeskua.droid.ruffle-Signed.apk
)
if [ -n "$OUT" ]; then
  mkdir -p "$OUT"
  cp "${APKS[@]}" "$OUT/"
  APKS=("${APKS[@]/#*\//$OUT/}")
fi
ls -l "${APKS[@]}"
sha256sum "${APKS[@]}"
