#!/usr/bin/env bash
# Builds the spike: Linux console baseline + Debug, Release and Release-aot APKs.
# Usage: ./build-spike.sh [apk-output-dir]
# Needs the Android SDK from ../build/android/setup-android-sdk.sh.
set -euo pipefail
cd "$(dirname "$0")"

export ANDROID_HOME="${ANDROID_HOME:-/opt/android-sdk}"
export JAVA_HOME="${JAVA_HOME:-/usr/lib/jvm/java-21-openjdk-amd64}"
SDK_ARGS=(-p:AndroidSdkDirectory="$ANDROID_HOME" -p:JavaSdkDirectory="$JAVA_HOME")
OUT="${1:-}"

dotnet build Spike.ConsoleHost -c Release
dotnet build Spike.Android -c Debug "${SDK_ARGS[@]}"
dotnet publish Spike.Android -c Release "${SDK_ARGS[@]}"
dotnet publish Spike.Android -c Release -p:SpikeVariant=aot "${SDK_ARGS[@]}"

APKS=(
  Spike.Android/bin/Debug/net10.0-android/com.vibeskua.droid.spike.debug-Signed.apk
  Spike.Android/bin/Release/net10.0-android/com.vibeskua.droid.spike-Signed.apk
  Spike.Android/bin/Release-aot/net10.0-android/com.vibeskua.droid.spike.aot-Signed.apk
)
if [ -n "$OUT" ]; then
  mkdir -p "$OUT"
  cp "${APKS[@]}" "$OUT/"
  APKS=("${APKS[@]/#*\//$OUT/}")
fi
ls -l "${APKS[@]}"
sha256sum "${APKS[@]}"
echo "Linux baseline: dotnet Spike.ConsoleHost/bin/Release/net10.0/Spike.ConsoleHost.dll [--bg-seconds N]"
