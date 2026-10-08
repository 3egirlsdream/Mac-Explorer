#!/bin/bash
set -euo pipefail
app=$1; channel=$2; identity=${3:--}; team=${4:-}
if [ -z "$identity" ]; then identity=-; fi
root=$(cd "$(dirname "$0")/../.." && pwd)
# Sign every Mach-O from the inside out; managed DLL/JSON files remain bundle resources.
while IFS= read -r -d '' file_path; do
    if [[ "$(file -b "$file_path")" == *Mach-O* ]]; then
        args=(--force --sign "$identity")
        if [ "$channel" = AppStore ] && [[ "$(basename "$file_path")" == MacExplorer.* ]]; then
            args+=(--entitlements "$root/Platforms/MacOS/Helper.entitlements")
        fi
        if [ "$identity" != - ]; then args+=(--timestamp --options runtime); fi
        codesign "${args[@]}" "$file_path"
    fi
done < <(find "$app/Contents" -type f ! -name MacExplorer -print0)
task_sign_dir=$(mktemp -d)
trap 'rm -rf "$task_sign_dir"' EXIT
cp "$root/Platforms/MacOS/$channel.entitlements" "$task_sign_dir/main.plist"
if [ "$channel" = AppStore ] && [ -n "$team" ]; then
    bundle_id=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Contents/Info.plist")
    /usr/libexec/PlistBuddy -c "Add :com.apple.application-identifier string $team.$bundle_id" "$task_sign_dir/main.plist"
    /usr/libexec/PlistBuddy -c "Add :com.apple.developer.team-identifier string $team" "$task_sign_dir/main.plist"
fi
args=(--force --sign "$identity" --entitlements "$task_sign_dir/main.plist")
if [ "$identity" != - ]; then args+=(--timestamp --options runtime); fi
codesign "${args[@]}" "$app"
codesign --verify --deep --strict "$app"
