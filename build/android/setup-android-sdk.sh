#!/usr/bin/env bash
# Idempotent Android SDK setup for building net10.0-android projects on Linux
# without the workload's InstallAndroidDependencies bootstrap (which needs aka.ms).
#
# Installs, under $ANDROID_HOME (default /opt/android-sdk):
#   cmdline-tools;19.0 (commandlinetools-linux-13114758), platforms;android-36,
#   build-tools;36.0.0, platform-tools
# These are the versions the .NET Android workload 36.1.x defaults to
# (AndroidSdkPlatformVersion=android-36, AndroidSdkBuildToolsVersion=36.0.0,
# AndroidCommandLineToolsVersion=19.0). JDK: workload accepts 17.0 .. 21.0.99;
# OpenJDK 21 (already installed from apt) is used; the script checks for it but does not install it.
#
# Only needs https://dl.google.com. Works behind an HTTPS proxy: the proxy host/port
# are read from $HTTPS_PROXY and passed to sdkmanager; the CA truststore comes from
# JAVA_TOOL_OPTIONS (-Djavax.net.ssl.trustStore=...) if the environment sets it.
set -euo pipefail

ANDROID_HOME="${ANDROID_HOME:-/opt/android-sdk}"
JAVA_HOME="${JAVA_HOME:-/usr/lib/jvm/java-21-openjdk-amd64}"
CMDLINE_ZIP="commandlinetools-linux-13114758_latest.zip"
# SHA-256 of that exact archive (computed from the download; its SHA-1 matches Google's repository manifest,
# 5fdcc763663eefb86a5b8879697aa6088b041e70). The archive is never unpacked unless it matches.
CMDLINE_SHA256="7ec965280a073311c339e571cd5de778b9975026cfcbe79f2b1cdcb1e15317ee"
PACKAGES=("platforms;android-36" "build-tools;36.0.0" "platform-tools")

if [ ! -x "$JAVA_HOME/bin/javac" ]; then
  # Not automated: the JDK was already present on the machine this script was verified on.
  echo "No JDK at $JAVA_HOME. Install one (17..21), e.g. apt-get install -y openjdk-21-jdk-headless, or set JAVA_HOME." >&2
  exit 1
fi
export JAVA_HOME

SDKMANAGER="$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager"
if [ ! -x "$SDKMANAGER" ]; then
  tmp="$(mktemp -d)"
  curl -sSfL -o "$tmp/$CMDLINE_ZIP" "https://dl.google.com/android/repository/$CMDLINE_ZIP"
  if ! echo "$CMDLINE_SHA256  $tmp/$CMDLINE_ZIP" | sha256sum -c --strict -; then
    echo "SHA-256 mismatch for $CMDLINE_ZIP: refusing to install it." >&2
    rm -rf "$tmp"
    exit 1
  fi
  mkdir -p "$ANDROID_HOME/cmdline-tools"
  unzip -q "$tmp/$CMDLINE_ZIP" -d "$tmp/x"
  rm -rf "$ANDROID_HOME/cmdline-tools/latest"
  mv "$tmp/x/cmdline-tools" "$ANDROID_HOME/cmdline-tools/latest"
  rm -rf "$tmp"
fi

PROXY_ARGS=()
if [ -n "${HTTPS_PROXY:-${https_proxy:-}}" ]; then
  p="${HTTPS_PROXY:-${https_proxy:-}}"
  p="${p#*://}"; p="${p#*@}"; p="${p%%/*}"
  PROXY_ARGS=(--proxy=http "--proxy_host=${p%:*}" "--proxy_port=${p##*:}")
fi

# `yes` dies of SIGPIPE when sdkmanager exits; don't let pipefail treat that as failure.
set +o pipefail
yes | "$SDKMANAGER" --sdk_root="$ANDROID_HOME" "${PROXY_ARGS[@]}" --licenses >/dev/null
set -o pipefail
"$SDKMANAGER" --sdk_root="$ANDROID_HOME" "${PROXY_ARGS[@]}" --install "${PACKAGES[@]}"
"$SDKMANAGER" --sdk_root="$ANDROID_HOME" "${PROXY_ARGS[@]}" --list_installed

cat <<EOF

Android SDK ready. For builds:
  export ANDROID_HOME=$ANDROID_HOME
  export JAVA_HOME=$JAVA_HOME
  dotnet build -f net10.0-android -p:AndroidSdkDirectory=\$ANDROID_HOME -p:JavaSdkDirectory=\$JAVA_HOME
EOF
