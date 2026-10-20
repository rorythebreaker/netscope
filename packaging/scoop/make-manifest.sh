#!/usr/bin/env bash
# Writes the Scoop manifest of a release to stdout.
# Usage: make-manifest.sh <version without v> <path to SHA256SUMS.txt>
set -euo pipefail
version="$1"
sums="$2"
repo="https://github.com/rorythebreaker/netscope"

hash_of() {
  local file="netscope-$1-v${version}.zip"
  local h
  h=$(awk -v f="$file" '{ name = $2; sub(/^\*/, "", name); if (name == f) print $1 }' "$sums")
  if [ -z "$h" ]; then echo "make-manifest: $file is not in $sums" >&2; exit 1; fi
  echo "$h"
}

x64=$(hash_of win-x64)
arm64=$(hash_of win-arm64)

cat <<JSON
{
    "version": "${version}",
    "description": "Console static analyzer for .NET assemblies: methods, calls, CFG, pseudocode, IL, native code, security rules, diff.",
    "homepage": "${repo}",
    "license": "MIT",
    "architecture": {
        "64bit": {
            "url": "${repo}/releases/download/v${version}/netscope-win-x64-v${version}.zip",
            "hash": "${x64}"
        },
        "arm64": {
            "url": "${repo}/releases/download/v${version}/netscope-win-arm64-v${version}.zip",
            "hash": "${arm64}"
        }
    },
    "bin": "netscope.exe",
    "checkver": "github",
    "autoupdate": {
        "architecture": {
            "64bit": {
                "url": "${repo}/releases/download/v\$version/netscope-win-x64-v\$version.zip"
            },
            "arm64": {
                "url": "${repo}/releases/download/v\$version/netscope-win-arm64-v\$version.zip"
            }
        },
        "hash": {
            "url": "${repo}/releases/download/v\$version/SHA256SUMS.txt"
        }
    }
}
JSON
