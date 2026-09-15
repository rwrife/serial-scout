#!/usr/bin/env bash
set -euo pipefail

fail() { echo "release verification failed: $*" >&2; exit 1; }

if [[ $# -ne 3 ]]; then
  echo "usage: $0 VERSION RELEASE_DIRECTORY EXPECTED_COMMIT_SHA" >&2
  exit 2
fi

version=$1
release_directory=$2
expected_commit=$3
script_directory=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
. "$script_directory/release-version.sh"
validate_preview_version "$version" || {
  echo "release version must be strict preview SemVer without a leading v: $version" >&2
  exit 2
}
[[ $expected_commit =~ ^[0-9a-f]{40}$ ]] || {
  echo "expected commit must be a lowercase 40-character SHA: $expected_commit" >&2
  exit 2
}
[[ -d $release_directory && ! -L $release_directory ]] || {
  echo "release directory not found or is a symlink: $release_directory" >&2
  exit 2
}

assets=(
  "SerialScout-$version-osx-arm64.dmg"
  "SerialScout-$version-osx-arm64.provenance.txt"
  "SerialScout-$version-osx-x64.dmg"
  "SerialScout-$version-osx-x64.provenance.txt"
  "SerialScout-$version-win-x64.provenance.txt"
  "SerialScout-$version-win-x64.zip"
)
expected=("SHA256SUMS.txt" "${assets[@]}")

mapfile -d '' -t entries < <(find "$release_directory" -mindepth 1 -maxdepth 1 -print0)
for path in "${entries[@]}"; do
  [[ -f $path && ! -L $path ]] || fail "directories and symlinks are not release assets: $(basename "$path")"
  name=$(basename "$path")
  found=0
  for wanted in "${expected[@]}"; do [[ $name == "$wanted" ]] && found=1; done
  [[ $found -eq 1 ]] || fail "unexpected release asset: $name"
done
[[ ${#entries[@]} -eq ${#expected[@]} ]] || fail "release asset count mismatch"
for name in "${expected[@]}"; do
  path="$release_directory/$name"
  [[ -f $path && ! -L $path ]] || fail "missing or non-regular release asset: $name"
done

declare -A checksum_files=()
while IFS= read -r line || [[ -n $line ]]; do
  [[ $line =~ ^([0-9a-f]{64})\ \ (.+)$ ]] || fail "malformed checksum entry"
  name=${BASH_REMATCH[2]}
  [[ $name != */* && $name != \\* ]] || fail "invalid checksum filename: $name"
  [[ -z ${checksum_files[$name]+present} ]] || fail "duplicate checksum entry: $name"
  checksum_files[$name]=${BASH_REMATCH[1]}
done < "$release_directory/SHA256SUMS.txt"
for name in "${!checksum_files[@]}"; do
  found=0
  for wanted in "${assets[@]}"; do [[ $name == "$wanted" ]] && found=1; done
  [[ $found -eq 1 ]] || fail "unexpected checksum entry: $name"
done
[[ ${#checksum_files[@]} -eq ${#assets[@]} ]] || fail "checksum entry count mismatch"
for name in "${assets[@]}"; do
  [[ -n ${checksum_files[$name]+present} ]] || fail "missing checksum entry: $name"
done
(
  cd "$release_directory"
  sha256sum --check --strict SHA256SUMS.txt
) || fail "checksum mismatch"

common_commit=
for rid in win-x64 osx-x64 osx-arm64; do
  provenance="$release_directory/SerialScout-$version-$rid.provenance.txt"
  extension=dmg
  signature=ad-hoc
  arch=x64
  keys=(artifact commit tag sdk runner arch rid self_contained signature CFBundleShortVersionString CFBundleVersion)
  if [[ $rid == win-x64 ]]; then
    extension=zip
    signature=unsigned
    keys=(artifact commit tag sdk runner arch rid self_contained signature)
  elif [[ $rid == osx-arm64 ]]; then
    arch=arm64
  fi

  declare -A fields=()
  while IFS= read -r line || [[ -n $line ]]; do
    [[ $line =~ ^([A-Za-z][A-Za-z0-9_]*)=(.+)$ ]] || fail "malformed provenance line for $rid"
    key=${BASH_REMATCH[1]}
    [[ -z ${fields[$key]+present} ]] || fail "duplicate provenance field for $rid: $key"
    fields[$key]=${BASH_REMATCH[2]}
  done < "$provenance"
  for key in "${!fields[@]}"; do
    found=0
    for wanted in "${keys[@]}"; do [[ $key == "$wanted" ]] && found=1; done
    [[ $found -eq 1 ]] || fail "unexpected provenance field for $rid: $key"
  done
  [[ ${#fields[@]} -eq ${#keys[@]} ]] || fail "provenance field count mismatch for $rid"
  for key in "${keys[@]}"; do
    [[ -n ${fields[$key]+present} ]] || fail "missing provenance field for $rid: $key"
  done

  [[ ${fields[artifact]} == "SerialScout-$version-$rid.$extension" ]] || fail "artifact mismatch for $rid"
  [[ ${fields[commit]} =~ ^[0-9a-f]{40}$ ]] || fail "invalid commit for $rid"
  [[ ${fields[tag]} == "v$version" ]] || fail "tag mismatch for $rid"
  [[ ${fields[sdk]} =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || fail "invalid SDK for $rid"
  [[ -n ${fields[runner]} ]] || fail "empty runner for $rid"
  [[ ${fields[arch]} == "$arch" ]] || fail "architecture mismatch for $rid"
  [[ ${fields[rid]} == "$rid" ]] || fail "RID mismatch for $rid"
  [[ ${fields[self_contained]} == true ]] || fail "self-contained mismatch for $rid"
  [[ ${fields[signature]} == "$signature" ]] || fail "signature mismatch for $rid"
  if [[ $rid == osx-* ]]; then
    numeric_base=${version%%-*}
    [[ ${fields[CFBundleShortVersionString]} == "$numeric_base" ]] || fail "short bundle version mismatch for $rid"
    [[ ${fields[CFBundleVersion]} == "$numeric_base" ]] || fail "bundle version mismatch for $rid"
  fi

  if [[ -z $common_commit ]]; then common_commit=${fields[commit]};
  else [[ ${fields[commit]} == "$common_commit" ]] || fail "provenance commits differ"; fi
  [[ ${fields[commit]} == "$expected_commit" ]] || fail "commit does not match expected SHA for $rid"
  unset fields
done

echo "PASS release assets: exact regular-file list, exact checksums, and exact provenance"
