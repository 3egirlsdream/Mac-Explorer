#!/bin/bash
set -euo pipefail
app=$1; channel=$2; raw_identity=${3:-}; team=${4:-}
root=$(cd "$(dirname "$0")/../.." && pwd)
auto_local_identity=false
if [ -n "$raw_identity" ]; then
    identity=$raw_identity
elif [ "$channel" = Website ] && [ -z "${CI:-}" ]; then
    website_ou=$(sed -n 's/.*subject\.OU\] = \([A-Z0-9]*\).*/\1/p' "$root/Tools/Distribution/website-signing.requirement" 2>/dev/null | head -n 1 || true)
    detected=""
    if [ -n "$website_ou" ]; then
        detected=$(security find-identity -v -p codesigning 2>/dev/null | awk -v ou="($website_ou)" '/"Developer ID Application: / && index($0, ou) { print $2; exit }' || true)
    fi
    if [ -n "$detected" ]; then
        identity=$detected
        auto_local_identity=true
    else
        identity=-
    fi
else
    identity=-
fi
signing_options=(--force --sign "$identity")
if [ "$identity" != - ]; then
    if [ "$auto_local_identity" = true ]; then
        signing_options+=(--options runtime)
    else
        signing_options+=(--timestamp --options runtime)
    fi
    if [ "$channel" = Website ] && [ -n "${WEBSITE_SIGNING_KEYCHAIN:-}" ]; then
        signing_options+=(--keychain "$WEBSITE_SIGNING_KEYCHAIN")
    fi
fi
# Sign nested Mach-O files before sealing the app.
sign_native_files() {
    local file_path args
    while IFS= read -r -d '' file_path; do
        if [[ "$(file -b "$file_path")" == *Mach-O* ]]; then
            args=("${signing_options[@]}")
            if [ "$channel" = AppStore ] && [[ "$(basename "$file_path")" == MacExplorer.* ]]; then
                args+=(--entitlements "$root/Platforms/MacOS/Helper.entitlements")
            fi
            codesign "${args[@]}" "$file_path"
        fi
    done < <(find "$1" -type f ! -name MacExplorer -print0)
}

task_sign_dir=$(mktemp -d)
trap 'rm -rf "$task_sign_dir"' EXIT
sign_native_files "$app/Contents"

cp "$root/Platforms/MacOS/$channel.entitlements" "$task_sign_dir/main.plist"
if [ "$channel" = AppStore ] && [ -n "$team" ]; then
    bundle_id=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Contents/Info.plist")
    /usr/libexec/PlistBuddy -c "Add :com.apple.application-identifier string $team.$bundle_id" "$task_sign_dir/main.plist"
    /usr/libexec/PlistBuddy -c "Add :com.apple.developer.team-identifier string $team" "$task_sign_dir/main.plist"
fi
codesign "${signing_options[@]}" --entitlements "$task_sign_dir/main.plist" "$app"
codesign --verify --deep --strict "$app"
