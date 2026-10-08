#!/bin/bash
set -euo pipefail
if [ "$#" -ne 3 ]; then
    echo 'Usage: prepare-store-pkg.sh <signed AppStore .app> <output.pkg> <Mac Installer Distribution identity>' >&2
    exit 2
fi
app=$1; output=$2; installer=$3
root=$(cd "$(dirname "$0")/../.." && pwd)
python3 "$root/Tools/Distribution/verify-bundle.py" "$app" AppStore
python3 "$root/Tools/Distribution/verify-release-config.py" --identifier "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Contents/Info.plist")" --policy "$(/usr/libexec/PlistBuddy -c 'Print :PrivacyPolicyURL' "$app/Contents/Info.plist")"
task_signature=$(codesign -d -vv "$app" 2>&1)
task_authority=$(printf '%s\n' "$task_signature" | sed -n 's/^Authority=//p' | head -n 1)
if ! [[ "$task_authority" == "3rd Party Mac Developer Application:"* || "$task_authority" == "Apple Distribution:"* || "$task_authority" == "Mac App Distribution:"* ]]; then
    echo 'Use an App Store distribution signature and matching provisioning profile before creating a submission package.' >&2
    exit 1
fi
if [ ! -f "$app/Contents/embedded.provisionprofile" ]; then
    echo 'A matching App Store provisioning profile is required.' >&2; exit 1
fi
if [ -e "$output" ]; then echo 'Output must not already exist.' >&2; exit 1; fi
if ! [[ "$installer" == "3rd Party Mac Developer Installer:"* || "$installer" == "Mac Installer Distribution:"* ]]; then
    echo 'An App Store installer signing identity is required.' >&2; exit 1
fi
task_profile=$(mktemp)
trap 'rm -f "$task_profile"' EXIT
security cms -D -i "$app/Contents/embedded.provisionprofile" > "$task_profile"
python3 - "$app" "$task_profile" <<'PYPROFILE'
import datetime, pathlib, plistlib, subprocess, sys, tempfile
app = pathlib.Path(sys.argv[1])
profile = plistlib.loads(pathlib.Path(sys.argv[2]).read_bytes())
info = plistlib.loads((app / 'Contents/Info.plist').read_bytes())
entitlements = plistlib.loads(subprocess.check_output(['codesign','-d','--entitlements',':-',str(app)],stderr=subprocess.DEVNULL))
allowed = profile['Entitlements']
identifier = entitlements.get('com.apple.application-identifier')
if identifier != allowed.get('com.apple.application-identifier') or not identifier or not identifier.endswith('.'+info['CFBundleIdentifier']):
    raise SystemExit('Provisioning profile and signed bundle identity do not match.')
if entitlements.get('com.apple.developer.team-identifier') not in profile.get('TeamIdentifier',[]):
    raise SystemExit('Provisioning profile and signing team do not match.')
if profile['ExpirationDate'] <= datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None):
    raise SystemExit('Provisioning profile has expired.')
if allowed.get('get-task-allow') or profile.get('ProvisionsAllDevices') or profile.get('ProvisionedDevices'):
    raise SystemExit('A Mac App Store distribution profile is required.')
with tempfile.TemporaryDirectory(prefix='fkfinder-store-cert-') as certificate_dir:
    prefix = str(pathlib.Path(certificate_dir) / 'signer-')
    subprocess.run(['codesign','-d','--extract-certificates=' + prefix,str(app)],check=True,stderr=subprocess.DEVNULL)
    certificate = pathlib.Path(prefix + '0').read_bytes()
    if certificate not in profile.get('DeveloperCertificates', []):
        raise SystemExit('The app signing certificate is not allowed by this provisioning profile.')
PYPROFILE
productbuild --component "$app" /Applications --sign "$installer" "$output"
pkgutil --check-signature "$output"
echo 'Package created and checked locally. No validation request or upload was sent to Apple.'
