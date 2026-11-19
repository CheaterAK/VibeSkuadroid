#!/usr/bin/env bash
# Downloads the pinned Ruffle self-hosted builds (official npm package @ruffle-rs/ruffle) into web/ruffle-*/
# and verifies every tarball and every shipped file against ruffle-builds.sha256. Idempotent: does nothing
# when all files are present and match. Source maps are not shipped.
set -euo pipefail
cd "$(dirname "$0")"
SUMS=ruffle-builds.sha256

verify() { # 0 when every listed file exists and matches
  local sum rel
  while read -r sum rel _; do
    case "$sum" in '' | \#*) continue ;; esac
    [ "$rel" = tgz ] && continue
    [ -f "web/$rel" ] || return 1
    [ "$(sha256sum "web/$rel" | cut -d' ' -f1)" = "$sum" ] || return 1
  done < "$SUMS"
  return 0
}

if verify; then echo "Ruffle builds present and verified."; exit 0; fi

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
  rm -rf "web/$dir"
  mkdir -p "web/$dir"
  # Copy exactly the files listed for this build.
  grep -E "^[0-9a-f]{64}  $dir/" "$SUMS" | while read -r _ frel; do
    cp "$TMP/$dir/package/${frel#"$dir"/}" "web/$frel"
  done
done < "$SUMS"

if verify; then echo "Ruffle builds fetched and verified."; else echo "Checksum mismatch after download" >&2; exit 1; fi
