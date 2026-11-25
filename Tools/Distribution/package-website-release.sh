#!/bin/bash
set -euo pipefail

version=${1:?Pass the release version.}
output_dir=${2:?Pass the release output directory.}
arm_app=${3:?Pass the Apple silicon app bundle.}
intel_app=${4:?Pass the Intel app bundle.}
script_dir=$(cd "$(dirname "$0")" && pwd)
bundle_id=$(/usr/bin/plutil -extract websiteBundleIdentifier raw -o - "$script_dir/release-config.json")

work_dir=$(mktemp -d "${TMPDIR:-/tmp}/macexplorer-website.XXXXXX")
signing_keychain=
keychain_search_changed=false
previous_keychains=()
mounted_image=
cleanup() {
    if [[ -n "$mounted_image" ]]; then hdiutil detach "$mounted_image" >&2 || true; fi
    if $keychain_search_changed; then
        security list-keychains -d user -s ${previous_keychains[@]+"${previous_keychains[@]}"} || true
    fi
    if [[ -n "$signing_keychain" ]]; then security delete-keychain "$signing_keychain" || true; fi
    rm -rf "$work_dir"
}
trap cleanup EXIT

if [[ -n "${WEBSITE_NOTARY_PROFILE:-}" ]]; then
    signing_identity=${WEBSITE_SIGNING_IDENTITY:?Set WEBSITE_SIGNING_IDENTITY to your Developer ID Application identity.}
    notary_args=(--keychain-profile "$WEBSITE_NOTARY_PROFILE")
else
    : "${WEBSITE_DEVELOPER_ID_P12_BASE64:?Configure the website Developer ID PKCS#12 secret.}"
    : "${WEBSITE_DEVELOPER_ID_P12_PASSWORD:?Configure the website Developer ID PKCS#12 password.}"
    : "${WEBSITE_NOTARY_KEY_BASE64:?Configure the website notary Team API key secret.}"
    : "${WEBSITE_NOTARY_KEY_ID:?Configure the website notary Team API key ID.}"
    : "${WEBSITE_NOTARY_ISSUER_ID:?Configure the website notary Team API issuer ID.}"

    echo 'Preparing temporary website signing keychain.'
    signing_keychain="$work_dir/signing.keychain-db"
    keychain_password=$(openssl rand -hex 32)
    printf '%s' "$WEBSITE_DEVELOPER_ID_P12_BASE64" | /usr/bin/base64 -D > "$work_dir/signing.p12"
    printf '%s' "$WEBSITE_NOTARY_KEY_BASE64" | /usr/bin/base64 -D > "$work_dir/notary.p8"
    security create-keychain -p "$keychain_password" "$signing_keychain"
    security set-keychain-settings -lut 3600 "$signing_keychain"
    security unlock-keychain -p "$keychain_password" "$signing_keychain"
    security import "$work_dir/signing.p12" -k "$signing_keychain" -f pkcs12 \
        -P "$WEBSITE_DEVELOPER_ID_P12_PASSWORD" -T /usr/bin/codesign
    security set-key-partition-list -S apple-tool:,apple:,codesign: -s \
        -k "$keychain_password" "$signing_keychain" >/dev/null
    keychain_list=$(security list-keychains -d user)
    while IFS= read -r entry; do
        [[ "$entry" == *'"'*'"' ]] || continue
        entry=${entry#*\"}
        previous_keychains+=("${entry%\"}")
    done <<< "$keychain_list"
    security list-keychains -d user -s "$signing_keychain" ${previous_keychains[@]+"${previous_keychains[@]}"}
    keychain_search_changed=true

    signing_identity=$(security find-identity -v -p codesigning "$signing_keychain" | awk '/"Developer ID Application: / { print $2 }')
    if [[ ! "$signing_identity" =~ ^[[:xdigit:]]{40}$ ]]; then
        echo 'The website signing keychain must contain one valid Developer ID Application identity.' >&2
        exit 1
    fi
    notary_args=(--key "$work_dir/notary.p8" --key-id "$WEBSITE_NOTARY_KEY_ID" --issuer "$WEBSITE_NOTARY_ISSUER_ID")
    rm "$work_dir/signing.p12"
    unset WEBSITE_DEVELOPER_ID_P12_BASE64 WEBSITE_DEVELOPER_ID_P12_PASSWORD WEBSITE_NOTARY_KEY_BASE64
fi

dmg_sign_args=(--sign "$signing_identity" --timestamp)
if [[ -n "$signing_keychain" ]]; then dmg_sign_args+=(--keychain "$signing_keychain"); fi

package_app() {
    local source=$1 suffix=$2
    local plist="$source/Contents/Info.plist"
    test "$(/usr/bin/plutil -extract DistributionChannel raw -o - "$plist")" = Website
    test "$(/usr/bin/plutil -extract CFBundleIdentifier raw -o - "$plist")" = "$bundle_id"
    test "$(/usr/bin/plutil -extract CFBundleShortVersionString raw -o - "$plist")" = "$version"

    local staging="$work_dir/staging$suffix"
    local app="$staging/Mac Explorer.app"
    local dmg="$work_dir/MacExplorer-$version-macos$suffix.dmg"
    local zip="$work_dir/MacExplorer-$version-macos$suffix.zip"
    mkdir "$staging"
    ditto "$source" "$app"
    echo "Signing $(basename "$dmg") and its application."
    WEBSITE_SIGNING_KEYCHAIN="$signing_keychain" bash "$script_dir/sign-app.sh" "$app" Website "$signing_identity"
    bash "$script_dir/verify-website-signature.sh" "$app"

    ln -s /Applications "$staging/Applications"
    python3 "$script_dir/create-dmg.py" "$staging" "$dmg"
    /usr/bin/codesign "${dmg_sign_args[@]}" "$dmg"
    /usr/bin/codesign --verify --strict "$dmg"

    local receipt="$work_dir/notarization$suffix.json"
    if ! xcrun notarytool submit "$dmg" "${notary_args[@]}" --wait --output-format json > "$receipt"; then
        cat "$receipt" >&2
        return 1
    fi
    cat "$receipt"
    if [[ "$(/usr/bin/plutil -extract status raw -o - "$receipt")" != Accepted ]]; then
        xcrun notarytool log "$(/usr/bin/plutil -extract id raw -o - "$receipt")" "${notary_args[@]}" >&2
        return 1
    fi

    # The DMG submission registers the tickets for the image and its enclosed app.
    xcrun stapler staple "$dmg"
    xcrun stapler validate "$dmg"
    /usr/sbin/spctl --assess --type open --context context:primary-signature "$dmg"
    mkdir "$work_dir/mounted$suffix"
    hdiutil attach -readonly -nobrowse -mountpoint "$work_dir/mounted$suffix" "$dmg"
    mounted_image="$work_dir/mounted$suffix"
    bash "$script_dir/verify-website-signature.sh" "$mounted_image/Mac Explorer.app"
    test "$(readlink "$mounted_image/Applications")" = /Applications
    hdiutil detach "$mounted_image"
    mounted_image=

    xcrun stapler staple "$app"
    xcrun stapler validate "$app"
    /usr/bin/syspolicy_check distribution "$app"
    ditto -c -k --sequesterRsrc --keepParent "$app" "$zip"
}

package_app "$arm_app" ''
package_app "$intel_app" -intel
mkdir -p "$output_dir"
mv "$work_dir"/*.dmg "$work_dir"/*.zip "$output_dir/"
