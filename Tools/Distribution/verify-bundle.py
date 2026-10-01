#!/usr/bin/env python3
"""Local bundle audit; this neither validates with nor uploads to App Store Connect."""
import json
import pathlib
import plistlib
import re
import subprocess
import sys

app = pathlib.Path(sys.argv[1]).resolve()
channel = sys.argv[2]
subprocess.run([sys.executable, str(pathlib.Path(__file__).with_name("verify-notices.py")), str(app)], check=True, stdout=sys.stderr)
info = plistlib.loads((app / 'Contents/Info.plist').read_bytes())
declared = tuple(map(int, info['LSMinimumSystemVersion'].split('.')))
errors = []
if info.get('DistributionChannel') != channel: errors.append('Bundle channel metadata does not match requested channel')
if not info.get('PrivacyPolicyURL', '').startswith('https://'): errors.append('Missing HTTPS privacy policy URL')
native = []
for path in (app / 'Contents').rglob('*'):
    if not path.is_file() or path.is_symlink():
        continue
    relative = str(path.relative_to(app))
    if 'Mach-O' in subprocess.check_output(['file', '-b', str(path)], text=True):
        load = subprocess.check_output(['otool', '-l', str(path)], text=True)
        targets = re.findall(r'\b(?:minos|version) (\d+\.\d+(?:\.\d+)?)', load)
        # LC_ID_DYLIB also has a version: only inspect deployment load commands.
        targets = re.findall(r'(?:LC_BUILD_VERSION[\s\S]*?minos |LC_VERSION_MIN_MACOSX[\s\S]*?version )(\d+\.\d+(?:\.\d+)?)', load)
        for target in targets:
            if tuple(map(int, target.split('.')))[:2] > declared[:2]:
                errors.append(f'{relative}: deployment {target} exceeds declared version')
        arch = subprocess.check_output(['lipo', '-archs', str(path)], text=True).strip()
        native.append({'path': relative, 'architectures': arch, 'minimum': targets})
        if '/Resources/' in relative:
            errors.append(f'Native code is misplaced in resources: {relative}')
    elif relative.startswith('Contents/MacOS/'):
        errors.append(f'Non-native resource is misplaced in MacOS: {relative}')
    if path.name == 'MacExplorer.Folder.png': errors.append('Bundled Apple GenericFolderIcon artwork found')
    if channel == 'AppStore' and path.suffix == '.mexplug': errors.append('Writable plugin package found in App Store bundle')
entitlement_xml = subprocess.check_output(['codesign', '-d', '--entitlements', ':-', str(app)], stderr=subprocess.DEVNULL)
entitlements = plistlib.loads(entitlement_xml) if entitlement_xml.strip() else {}
if channel == 'AppStore':
    for key in ['app-sandbox', 'files.user-selected.read-write', 'files.bookmarks.app-scope', 'network.client', 'network.server', 'cs.allow-jit']:
        if not entitlements.get('com.apple.security.' + key): errors.append('Missing entitlement: ' + key)
    if any('temporary-exception' in key for key in entitlements): errors.append('Temporary sandbox exception found')
    if info['CFBundleIdentifier'] == 'com.macexplorer.app': errors.append('App Store reused website bundle identity')
    for helper in (app / 'Contents/MacOS').glob('MacExplorer.*'):
        helper_xml = subprocess.check_output(['codesign', '-d', '--entitlements', ':-', str(helper)], stderr=subprocess.DEVNULL)
        helper_entitlements = plistlib.loads(helper_xml)
        if helper_entitlements != {'com.apple.security.app-sandbox': True, 'com.apple.security.inherit': True}:
            errors.append(f'{helper.name}: unexpected inherited sandbox entitlements')
host = next(item for item in native if item['path'] == 'Contents/MacOS/' + info['CFBundleExecutable'])
required_architectures = set(host['architectures'].split())
for item in native:
    if not required_architectures.issubset(set(item['architectures'].split())):
        errors.append(f"{item['path']}: missing an architecture required by the launcher")
subprocess.run(['codesign', '--verify', '--deep', '--strict', str(app)], check=True)
print(json.dumps({'bundle': str(app), 'channel': channel, 'identifier': info['CFBundleIdentifier'],
                  'minimum': info['LSMinimumSystemVersion'], 'native': native, 'errors': errors}, indent=2))
if errors: sys.exit(1)
