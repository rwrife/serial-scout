#!/usr/bin/env bash

# Strict SemVer core plus the repository's required preview prerelease prefix.
# Numeric identifiers may only be "0" or a non-zero digit followed by digits.
preview_version_pattern='(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-preview\.((0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)'

validate_preview_version() {
  [[ $1 =~ ^$preview_version_pattern$ ]]
}

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
  if [[ $# -ne 1 ]] || ! validate_preview_version "$1"; then
    echo "version must be strict SemVer MAJOR.MINOR.PATCH-preview.ID without a leading v" >&2
    exit 2
  fi
fi
