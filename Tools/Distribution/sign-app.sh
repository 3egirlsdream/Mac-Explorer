#!/bin/bash
set -euo pipefail
app=$1; channel=$2; identity=${3:--}; team=${4:-}
if [ -z "$identity" ]; then identity=-; fi
root=$(cd "$(dirname "$0")/../.." && pwd)
signing_options=(--force --sign "$identity")
if [ "$identity" != - ]; then
    signing_options+=(--timestamp --options runtime)
    if [ "$channel" = Website ] && [ -n "${WEBSITE_SIGNING_KEYCHAIN:-}" ]; then
        signing_options+=(--keychain "$WEBSITE_SIGNING_KEYCHAIN")
    fi
fi
# Sign every Mach-O from the inside out; managed DLL/JSON files remain bundle resources.
sign_native_files() {
    local file_path args
    while IFS= read -r -d '' file_path; do
        if [[ "$(file -b "$file_path")" == *Mach-O* ]]; then
            args=("${signing_options[@]}")
            if [ "$channel" = AppStore ] && [[ "$(basename "$file_path")" == MacExplorer.* ]]; then
                args+=(--entitlements "$root/Platforms/MacOS/Helper.entitlements")
            elif [ "$channel" = Website ] && [ "$file_path" = "$app/Contents/MacOS/MacExplorer.PluginWorker" ]; then
                args+=(--entitlements "$root/Platforms/MacOS/WebsitePluginWorker.entitlements")
            fi
            codesign "${args[@]}" "$file_path"
        fi
    done < <(find "$1" -type f ! -name MacExplorer -print0)
}

task_sign_dir=$(mktemp -d)
trap 'rm -rf "$task_sign_dir"' EXIT
if [ "$channel" = Website ] && [ "$identity" != - ]; then
    for package in "$app/Contents/Resources/BundledPlugins/"*.mexplug; do
        [ -f "$package" ] || continue
        plugin_dir=$(mktemp -d "$task_sign_dir/plugin.XXXXXX")
        unzip -q "$package" -d "$plugin_dir"
        sign_native_files "$plugin_dir"
        (cd "$plugin_dir" && zip -qry "$task_sign_dir/signed.mexplug" .)
        mv "$task_sign_dir/signed.mexplug" "$package"
    done
fi
sign_native_files "$app/Contents"

cp "$root/Platforms/MacOS/$channel.entitlements" "$task_sign_dir/main.plist"
if [ "$channel" = AppStore ] && [ -n "$team" ]; then
    bundle_id=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Contents/Info.plist")
    /usr/libexec/PlistBuddy -c "Add :com.apple.application-identifier string $team.$bundle_id" "$task_sign_dir/main.plist"
    /usr/libexec/PlistBuddy -c "Add :com.apple.developer.team-identifier string $team" "$task_sign_dir/main.plist"
fi
codesign "${signing_options[@]}" --entitlements "$task_sign_dir/main.plist" "$app"
codesign --verify --deep --strict "$app"
