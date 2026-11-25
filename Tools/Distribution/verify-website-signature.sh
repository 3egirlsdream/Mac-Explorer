#!/bin/bash
set -euo pipefail

app=${1:?Pass the website app bundle to verify.}
script_dir=$(cd "$(dirname "$0")" && pwd)
requirement=$(cat "$script_dir/website-signing.requirement")
expected=$(/usr/bin/csreq -r "=designated => $requirement" -t)

/usr/bin/codesign --verify --all-architectures --deep --strict -R "=$requirement" "$app"
actual=$(/usr/bin/codesign --display -r- "$app" 2>&1 | sed -n '/^designated => /p')
if [[ "$actual" != "$expected" ]]; then
    echo "The website app’s designated requirement changed: $actual" >&2
    exit 1
fi

signature=$(/usr/bin/codesign --display --verbose=4 "$app" 2>&1)
if ! grep -q '^CodeDirectory.*(runtime)' <<< "$signature" || ! grep -q '^Timestamp=' <<< "$signature"; then
    echo 'The website app requires Hardened Runtime and a secure timestamp.' >&2
    exit 1
fi
