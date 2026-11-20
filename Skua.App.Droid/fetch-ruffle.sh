#!/usr/bin/env bash
# Downloads the pinned Ruffle self-hosted web build the game view ships (official npm package @ruffle-rs/ruffle) into
# Skua.App.Droid/GameView/web/ruffle-*/ and verifies the tarball and every shipped file against ruffle-build.sha256.
# Idempotent: does nothing when all files are present and match. Source maps are not shipped. The build is not in git
# (.gitignore); build-droid.sh and the CI workflow run this before building.
set -euo pipefail
cd "$(dirname "$0")"
SUMS=ruffle-build.sha256
WEB=Skua.App.Droid/GameView/web

verify() { # 0 when every listed file exists and matches
  local sum rel
  while read -r sum rel _; do
    case "$sum" in '' | \#*) continue ;; esac
    [ "$rel" = tgz ] && continue
    [ -f "$WEB/$rel" ] || return 1
    [ "$(sha256sum "$WEB/$rel" | cut -d' ' -f1)" = "$sum" ] || return 1
  done < "$SUMS"
  return 0
}

if verify; then echo "Ruffle build present and verified."; exit 0; fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
while read -r sum kind dir url; do
  case "$sum" in '' | \#*) continue ;; esac
  [ "$kind" = tgz ] || continue
  echo "Fetching $url"
  curl -fsSL --retry 3 -o "$TMP/$dir.tgz" "$url"
  echo "$sum  $TMP/$dir.tgz" | sha256sum -c --quiet -
  mkdir -p "$TMP/$dir"
  tar -xzf "$TMP/$dir.tgz" -C "$TMP/$dir"
  rm -rf "${WEB:?}/$dir"
  mkdir -p "$WEB/$dir"
  # Copy exactly the files listed for this build.
  grep -E "^[0-9a-f]{64}  $dir/" "$SUMS" | while read -r _ frel; do
    cp "$TMP/$dir/package/${frel#"$dir"/}" "$WEB/$frel"
  done
done < "$SUMS"

if verify; then echo "Ruffle build fetched and verified."; else echo "Checksum mismatch after download" >&2; exit 1; fi
