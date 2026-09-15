#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
version=1.2.3-preview.rc-1.2
commit=0123456789abcdef0123456789abcdef01234567
work=$(mktemp -d "${TMPDIR:-/tmp}/serial-scout-release-tests.XXXXXX")
trap 'rm -rf "$work"' EXIT INT TERM

assets=(
  "SerialScout-$version-osx-arm64.dmg"
  "SerialScout-$version-osx-arm64.provenance.txt"
  "SerialScout-$version-osx-x64.dmg"
  "SerialScout-$version-osx-x64.provenance.txt"
  "SerialScout-$version-win-x64.provenance.txt"
  "SerialScout-$version-win-x64.zip"
)

write_provenance() {
  local directory=$1 rid=$2 extension=dmg signature=ad-hoc arch=x64
  [[ $rid == win-x64 ]] && extension=zip && signature=unsigned
  [[ $rid == osx-arm64 ]] && arch=arm64
  {
    printf 'artifact=SerialScout-%s-%s.%s\n' "$version" "$rid" "$extension"
    printf 'commit=%s\n' "$commit"
    printf 'tag=v%s\n' "$version"
    printf 'sdk=8.0.100\n'
    printf 'runner=fixture-runner-%s\n' "$arch"
    printf 'arch=%s\n' "$arch"
    printf 'rid=%s\n' "$rid"
    printf 'self_contained=true\n'
    printf 'signature=%s\n' "$signature"
    if [[ $rid == osx-* ]]; then
      printf 'CFBundleShortVersionString=1.2.3\n'
      printf 'CFBundleVersion=1.2.3\n'
    fi
  } > "$directory/SerialScout-$version-$rid.provenance.txt"
}

make_fixture() {
  local directory=$1 name
  mkdir -p "$directory"
  for name in "${assets[@]}"; do
    [[ $name == *.provenance.txt ]] || printf 'synthetic verifier fixture for %s\n' "$name" > "$directory/$name"
  done
  write_provenance "$directory" win-x64
  write_provenance "$directory" osx-x64
  write_provenance "$directory" osx-arm64
  (cd "$directory" && sha256sum "${assets[@]}" > SHA256SUMS.txt)
}

verify() { "$script_directory/verify-release-assets.sh" "$version" "$1" "$commit"; }

case_number=0
negative_case() {
  local label=$1 expected=$2 mutation=$3 directory="$work/case-$case_number" log="$work/case-$case_number.log"
  case_number=$((case_number + 1))
  make_fixture "$directory"
  "$mutation" "$directory"
  if verify "$directory" >"$log" 2>&1; then
    echo "FAIL negative verifier fixture passed: $label" >&2
    exit 1
  fi
  grep -F "$expected" "$log" >/dev/null || {
    echo "FAIL negative verifier fixture produced wrong boundary: $label" >&2
    sed -n '1,8p' "$log" >&2
    exit 1
  }
}

extra_file() { printf x > "$1/unexpected.txt"; }
missing_file() { rm "$1/${assets[0]}"; }
missing_manifest() { rm "$1/SHA256SUMS.txt"; }
extra_directory() { mkdir "$1/unexpected-directory"; }
extra_symlink() { ln -s "${assets[0]}" "$1/unexpected-link"; }
replace_with_directory() { rm "$1/${assets[0]}"; mkdir "$1/${assets[0]}"; }
replace_with_symlink() { rm "$1/${assets[0]}"; ln -s "${assets[1]}" "$1/${assets[0]}"; }
checksum_missing() { sed -i '1d' "$1/SHA256SUMS.txt"; }
checksum_duplicate() { sed -n '1p' "$1/SHA256SUMS.txt" >> "$1/SHA256SUMS.txt"; }
checksum_unexpected() { sed -i '1s/  .*/  not-an-asset/' "$1/SHA256SUMS.txt"; }
checksum_malformed() { sed -i '1s/  / */' "$1/SHA256SUMS.txt"; }
checksum_uppercase() { sed -i '1s/^./A/' "$1/SHA256SUMS.txt"; }
checksum_wrong_hash() { sed -i '1s/^[0-9a-f]\{64\}/0000000000000000000000000000000000000000000000000000000000000000/' "$1/SHA256SUMS.txt"; }
provenance_missing() { sed -i '/^runner=/d' "$1/SerialScout-$version-win-x64.provenance.txt"; refresh_checksums "$1"; }
provenance_duplicate() { printf 'rid=win-x64\n' >> "$1/SerialScout-$version-win-x64.provenance.txt"; refresh_checksums "$1"; }
provenance_unexpected() { printf 'extra=no\n' >> "$1/SerialScout-$version-win-x64.provenance.txt"; refresh_checksums "$1"; }
provenance_malformed() { printf 'broken\n' >> "$1/SerialScout-$version-win-x64.provenance.txt"; refresh_checksums "$1"; }
replace_field() { sed -i "s|^$2=.*|$2=$3|" "$1/SerialScout-$version-$4.provenance.txt"; refresh_checksums "$1"; }
refresh_checksums() { (cd "$1" && sha256sum "${assets[@]}" > SHA256SUMS.txt); }
bad_artifact() { replace_field "$1" artifact wrong.zip win-x64; }
bad_commit() { replace_field "$1" commit abc win-x64; }
wrong_expected_commit() { replace_field "$1" commit 1111111111111111111111111111111111111111 win-x64; }
bad_tag() { replace_field "$1" tag v9.9.9-preview.1 win-x64; }
bad_sdk() { replace_field "$1" sdk 08.0.100 win-x64; }
empty_runner() { replace_field "$1" runner '' win-x64; }
bad_arch_x64() { replace_field "$1" arch arm64 win-x64; }
bad_arch_arm64() { replace_field "$1" arch x64 osx-arm64; }
bad_rid() { replace_field "$1" rid osx-x64 win-x64; }
bad_self_contained() { replace_field "$1" self_contained false win-x64; }
bad_signature() { replace_field "$1" signature signed win-x64; }
bad_short_version() { replace_field "$1" CFBundleShortVersionString 1.2 osx-x64; }
bad_bundle_version() { replace_field "$1" CFBundleVersion 1.2.3.2 osx-x64; }
different_commits() { replace_field "$1" commit 1111111111111111111111111111111111111111 osx-x64; }

positive="$work/positive"
make_fixture "$positive"
verify "$positive" >/dev/null
ln -s "$positive" "$work/release-directory-link"
if verify "$work/release-directory-link" >"$work/directory-link.log" 2>&1; then
  echo 'FAIL verifier accepted a symlink as the release directory' >&2
  exit 1
fi
grep -F 'release directory not found or is a symlink' "$work/directory-link.log" >/dev/null
if "$script_directory/verify-release-assets.sh" "$version" "$positive" not-a-sha >"$work/bad-expected-sha.log" 2>&1; then
  echo 'FAIL verifier accepted an invalid expected SHA' >&2
  exit 1
fi
grep -F 'expected commit must be a lowercase 40-character SHA' "$work/bad-expected-sha.log" >/dev/null

negative_case 'extra file' 'unexpected release asset' extra_file
negative_case 'missing file' 'release asset count mismatch' missing_file
negative_case 'missing checksum manifest' 'release asset count mismatch' missing_manifest
negative_case 'extra directory' 'directories and symlinks are not release assets' extra_directory
negative_case 'extra symlink' 'directories and symlinks are not release assets' extra_symlink
negative_case 'expected asset is directory' 'directories and symlinks are not release assets' replace_with_directory
negative_case 'expected asset is symlink' 'directories and symlinks are not release assets' replace_with_symlink
negative_case 'missing checksum' 'checksum entry count mismatch' checksum_missing
negative_case 'duplicate checksum' 'duplicate checksum entry' checksum_duplicate
negative_case 'unexpected checksum' 'unexpected checksum entry' checksum_unexpected
negative_case 'malformed checksum' 'malformed checksum entry' checksum_malformed
negative_case 'uppercase checksum' 'malformed checksum entry' checksum_uppercase
negative_case 'incorrect hash' 'checksum mismatch' checksum_wrong_hash
negative_case 'missing provenance field' 'provenance field count mismatch' provenance_missing
negative_case 'duplicate provenance field' 'duplicate provenance field' provenance_duplicate
negative_case 'unexpected provenance field' 'unexpected provenance field' provenance_unexpected
negative_case 'malformed provenance line' 'malformed provenance line' provenance_malformed
negative_case 'artifact mismatch' 'artifact mismatch' bad_artifact
negative_case 'malformed commit' 'invalid commit' bad_commit
negative_case 'expected SHA mismatch' 'commit does not match expected SHA' wrong_expected_commit
negative_case 'tag mismatch' 'tag mismatch' bad_tag
negative_case 'SDK mismatch' 'invalid SDK' bad_sdk
negative_case 'empty runner' 'malformed provenance line' empty_runner
negative_case 'x64 RID architecture mismatch' 'architecture mismatch' bad_arch_x64
negative_case 'arm64 RID architecture mismatch' 'architecture mismatch' bad_arch_arm64
negative_case 'RID mismatch' 'RID mismatch' bad_rid
negative_case 'self-contained mismatch' 'self-contained mismatch' bad_self_contained
negative_case 'signature mismatch' 'signature mismatch' bad_signature
negative_case 'short bundle version mismatch' 'short bundle version mismatch' bad_short_version
negative_case 'four-component bundle version' 'bundle version mismatch' bad_bundle_version
negative_case 'inconsistent provenance commits' 'provenance commits differ' different_commits

valid_versions=(0.0.0-preview.0 1.2.3-preview.1 10.20.30-preview.rc-1 1.2.3-preview.alpha.9)
invalid_versions=(01.2.3-preview.1 1.02.3-preview.1 1.2.03-preview.1 1.2.3-preview.01 1.2.3-preview..1 1.2.3-preview. 1.2.3-preview.a_1 1.2.3-rc.1 1.2.3 v1.2.3-preview.1 1.2.3-preview.1+build)
for candidate in "${valid_versions[@]}"; do "$script_directory/release-version.sh" "$candidate"; done
for candidate in "${invalid_versions[@]}"; do
  if "$script_directory/release-version.sh" "$candidate" >/dev/null 2>&1; then
    echo "FAIL Bash version validator accepted: $candidate" >&2
    exit 1
  fi
done
if "$script_directory/verify-release-assets.sh" 1.2.3-preview.01 "$positive" "$commit" >/dev/null 2>&1; then
  echo 'FAIL release verifier accepted an invalid version' >&2
  exit 1
fi
if "$script_directory/package-macos.sh" 1.2.3-preview.01 osx-x64 "$work/package" >/dev/null 2>&1; then
  echo 'FAIL macOS packager accepted an invalid version' >&2
  exit 1
fi

RELEASE_VERSION_PS1="$script_directory/release-version.ps1" pwsh -NoLogo -NoProfile -Command '
  $ErrorActionPreference = "Stop"
  . $env:RELEASE_VERSION_PS1
  $valid = @("0.0.0-preview.0", "1.2.3-preview.1", "10.20.30-preview.rc-1", "1.2.3-preview.alpha.9")
  $invalid = @("01.2.3-preview.1", "1.02.3-preview.1", "1.2.03-preview.1", "1.2.3-preview.01", "1.2.3-preview..1", "1.2.3-preview.", "1.2.3-preview.a_1", "1.2.3-rc.1", "1.2.3", "v1.2.3-preview.1", "1.2.3-preview.1+build")
  foreach ($version in $valid) { if (-not (Test-PreviewVersion $version)) { throw "rejected valid version: $version" } }
  foreach ($version in $invalid) { if (Test-PreviewVersion $version) { throw "accepted invalid version: $version" } }
'
if pwsh -NoLogo -NoProfile -File "$script_directory/package-windows.ps1" -Version 1.2.3-preview.01 -OutputDirectory "$work/package" >/dev/null 2>&1; then
  echo 'FAIL Windows packager accepted an invalid version' >&2
  exit 1
fi

plist="$work/Info.plist"
"$script_directory/render-macos-info-plist.sh" "$version" "$plist"
[[ $(grep -c '<string>1.2.3</string>' "$plist") -eq 2 ]]
! grep -F 'preview' "$plist" >/dev/null
grep -F -- '-p:InformationalVersion="$version+$commit"' "$script_directory/package-macos.sh" >/dev/null
grep -F -- '-p:InformationalVersion="$Version+$($env:GITHUB_SHA ?? '\''local'\'')"' "$script_directory/package-windows.ps1" >/dev/null
if "$script_directory/render-macos-info-plist.sh" 1.2.3-preview.01 "$work/bad.plist" >/dev/null 2>&1; then
  echo 'FAIL metadata renderer accepted an invalid version' >&2
  exit 1
fi

printf 'PASS release script regressions: synthetic positive fixture plus %d negative verifier fixtures, strict Bash/PowerShell versions, and macOS metadata\n' "$case_number"
