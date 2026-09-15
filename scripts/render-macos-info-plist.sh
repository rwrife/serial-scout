#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 VERSION OUTPUT_PLIST" >&2
  exit 2
fi

script_directory=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
. "$script_directory/release-version.sh"
validate_preview_version "$1" || {
  echo "version must be strict preview SemVer without a leading v: $1" >&2
  exit 2
}

numeric_base=${1%%-*}
sed \
  -e "s/@SHORT_VERSION@/$numeric_base/g" \
  -e "s/@BUNDLE_VERSION@/$numeric_base/g" \
  "$script_directory/macos/Info.plist.in" > "$2"
