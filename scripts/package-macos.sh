#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "usage: $0 VERSION RID [OUTPUT_DIRECTORY]" >&2
  echo "RID must be osx-x64 or osx-arm64" >&2
  exit 2
}

[[ $# -ge 2 && $# -le 3 ]] || usage
version=$1
rid=$2
output_directory=${3:-artifacts/release}

script_directory=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
. "$script_directory/release-version.sh"
validate_preview_version "$version" || usage
[[ $rid == osx-x64 || $rid == osx-arm64 ]] || usage

repository_root=$(cd "$script_directory/.." && pwd)
project="$repository_root/src/SerialScout.App/SerialScout.App.csproj"
if [[ $output_directory != /* ]]; then
  output_directory="$repository_root/$output_directory"
fi
mkdir -p "$output_directory"

artifact_base="SerialScout-$version-$rid"
dmg="$output_directory/$artifact_base.dmg"
provenance="$output_directory/$artifact_base.provenance.txt"
stage=$(mktemp -d "${TMPDIR:-/tmp}/serial-scout-package.XXXXXX")
cleanup() {
  rm -rf "$stage"
}
trap cleanup EXIT INT TERM

publish_directory="$stage/publish"
image_root="$stage/image"
app="$image_root/Serial Scout.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

numeric_base=${version%%-*}

commit=${GITHUB_SHA:-$(git -C "$repository_root" rev-parse HEAD)}
# Restore the complete RuntimeIdentifiers graph recorded in packages.lock.json.
# Passing one --runtime would narrow that graph and invalidate locked mode.
dotnet restore "$project" --locked-mode
dotnet publish "$project" \
  --configuration Release \
  --runtime "$rid" \
  --self-contained true \
  --no-restore \
  --output "$publish_directory" \
  -p:Version="$version" \
  -p:AssemblyVersion="$numeric_base.0" \
  -p:FileVersion="$numeric_base.0" \
  -p:InformationalVersion="$version+$commit"

test -x "$publish_directory/SerialScout.App"
cp -R "$publish_directory/." "$app/Contents/MacOS/"
chmod 755 "$app/Contents/MacOS/SerialScout.App"

"$script_directory/render-macos-info-plist.sh" "$version" "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

rm -f "$dmg"
hdiutil create -quiet -fs HFS+ -volname "Serial Scout $version" -srcfolder "$image_root" -format UDZO "$dmg"

tag=none
[[ ${GITHUB_REF_TYPE:-} == tag ]] && tag=${GITHUB_REF_NAME:-none}
runner="local/$(sw_vers -productVersion)-$(uname -m)"
[[ -n ${ImageOS:-} ]] && runner="$ImageOS/${ImageVersion:-unknown}-$(uname -m)"
arch=x64
[[ $rid == osx-arm64 ]] && arch=arm64
{
  printf 'artifact=%s.dmg\n' "$artifact_base"
  printf 'commit=%s\n' "$commit"
  printf 'tag=%s\n' "$tag"
  printf 'sdk=%s\n' "$(dotnet --version)"
  printf 'runner=%s\n' "$runner"
  printf 'arch=%s\n' "$arch"
  printf 'rid=%s\n' "$rid"
  printf 'self_contained=true\n'
  printf 'signature=ad-hoc\n'
  printf 'CFBundleShortVersionString=%s\n' "$numeric_base"
  printf 'CFBundleVersion=%s\n' "$numeric_base"
} > "$provenance"

printf 'PACKAGE %s\n' "$dmg"
printf 'PROVENANCE %s\n' "$provenance"
