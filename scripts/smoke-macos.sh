#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || $1 != *.dmg || ! -f $1 ]]; then
  echo "usage: $0 PACKAGE.dmg" >&2
  exit 2
fi

package=$(cd "$(dirname "$1")" && pwd)/$(basename "$1")
stage=$(mktemp -d "${TMPDIR:-/tmp}/serial-scout-smoke-package.XXXXXX")
mount_point="$stage/mount"
mkdir -p "$mount_point"
mounted=0
cleanup() {
  status=$?
  trap - EXIT INT TERM
  set +e
  if [[ $mounted -eq 1 ]]; then
    if hdiutil detach -quiet "$mount_point" || hdiutil detach -quiet -force "$mount_point"; then
      mounted=0
    else
      echo "FAIL macOS package smoke cleanup: could not detach $mount_point" >&2
      status=1
    fi
  fi
  if [[ $mounted -eq 0 ]]; then
    rm -rf "$stage" || status=1
  else
    echo "Preserving still-mounted directory: $stage" >&2
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

hdiutil attach -quiet -nobrowse -readonly -mountpoint "$mount_point" "$package"
mounted=1
app="$mount_point/Serial Scout.app"
test -d "$app"
codesign --verify --deep --strict "$app"
"$app/Contents/MacOS/SerialScout.App" --packaged-smoke
hdiutil detach -quiet "$mount_point" || hdiutil detach -quiet -force "$mount_point"
mounted=0
rm -rf "$stage"
printf 'PASS macOS package smoke: %s\n' "$(basename "$package")"
