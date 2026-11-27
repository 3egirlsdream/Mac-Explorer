#!/usr/bin/env python3
"""Local bundle audit; this neither validates with nor uploads to App Store Connect."""
import json
import pathlib
import plistlib
import re
import subprocess
import sys
from helper_metadata import embedded_plist_errors

app = pathlib.Path(sys.argv[1]).resolve()
channel = sys.argv[2]
subprocess.run([sys.executable, str(pathlib.Path(__file__).with_name("verify-notices.py")), str(app)], check=True, stdout=sys.stderr)
info = plistlib.loads((app / 'Contents/Info.plist').read_bytes())
declared = tuple(map(int, info['LSMinimumSystemVersion'].split('.')))
errors = []
if info.get('DistributionChannel') != channel: errors.append('Bundle channel metadata does not match requested channel')
if not info.get('PrivacyPolicyURL', '').startswith('https://'): errors.append('Missing HTTPS privacy policy URL')
if channel == 'AppStore':
    if app.name != '文件资源管理器.app': errors.append('App Store bundle must be named 文件资源管理器.app')
    if any(info.get(key) != '文件资源管理器' for key in ['CFBundleName', 'CFBundleDisplayName']):
        errors.append('App Store base display names must match the English listing: 文件资源管理器')
    for locale, name in [('zh-Hans', '文件资源管理器'), ('en', '文件资源管理器')]:
        strings = app / 'Contents/Resources' / (locale + '.lproj') / 'InfoPlist.strings'
        if not strings.is_file():
            errors.append('Missing localized App Store name: ' + locale)
        else:
            content = strings.read_text(encoding='utf-16')
            for key in ['CFBundleName', 'CFBundleDisplayName']:
                if f'"{key}" = "{name}";' not in content:
                    errors.append('Mismatched localized App Store name: ' + locale + '/' + key)
    if not (app / 'Contents/Frameworks/libMacExplorerStoreKit.dylib').is_file(): errors.append('Missing StoreKit 2 bridge')
    else:
        exports = subprocess.check_output(['nm', '-gU', str(app / 'Contents/Frameworks/libMacExplorerStoreKit.dylib')], text=True)
        for symbol in ['me_store_start', 'me_store_request', 'me_store_cancel', 'me_store_stop']:
            if not re.search(r'\b_' + symbol + r'$', exports, re.M): errors.append('Missing StoreKit bridge export: ' + symbol)
    for document_type in info.get('CFBundleDocumentTypes', []):
        if document_type.get('LSHandlerRank') not in {'Owner', 'Default', 'Alternate', 'None'}:
            errors.append('Missing or invalid LSHandlerRank: ' + document_type.get('CFBundleTypeName', '(unnamed)'))
    if not (app / 'Contents/MacOS/MacExplorer.Git').is_file(): errors.append('Missing optional Git query helper')
    if info.get('LSApplicationCategoryType') not in {'public.app-category.utilities', 'public.app-category.productivity', 'public.app-category.developer-tools'}:
        errors.append('Missing or unsupported Mac Explorer App Store category')
    if not info.get('NSHumanReadableCopyright', '').strip(): errors.append('Missing copyright')
    if not re.fullmatch(r'[0-9]+(?:\.[0-9]+){0,2}', info.get('CFBundleVersion', '')): errors.append('Invalid App Store build number')
    attributes = subprocess.check_output(['xattr', '-r', '-s', str(app)], text=True)
    for line in attributes.splitlines():
        if line.endswith('com.apple.quarantine'): errors.append('Quarantine attribute found: ' + line)
contents = app / 'Contents'
frameworks = contents / 'Frameworks'
required_libraries = {'libAvaloniaNative.dylib', 'libHarfBuzzSharp.dylib', 'libSkiaSharp.dylib',
                      'libe_sqlite3.dylib', 'libraw.dylib', 'libzip.dylib', 'libMacExplorerNativeDrag.dylib'}
for library in sorted(required_libraries):
    if not (frameworks / library).is_file():
        errors.append('Missing required library: ' + library)
for directory in ('BuiltInConversion/plugin.json', 'Copilot/Skills', 'I18n'):
    if not (contents / 'Resources' / directory).exists():
        errors.append('Missing resource: ' + directory)
if (contents / 'Resources/Managed').exists():
    errors.append('Managed directory found in NativeAOT bundle')
native = []
for path in (app / 'Contents').rglob('*'):
    if path.is_symlink():
        if path.suffix == '.dylib' and path.resolve() != frameworks / path.name:
            errors.append('Incorrect library symlink: ' + str(path))
        continue
    if not path.is_file():
        continue
    if (path.suffix in {'.dll', '.mexplug'} or path.name == 'MacExplorer.PluginWorker'
            or path.name.endswith(('.deps.json', '.runtimeconfig.json'))
            or path.name.startswith(('libclr', 'libcoreclr', 'libhostfxr', 'libhostpolicy', 'libmscord'))):
        errors.append('Managed runtime artifact found: ' + str(path.relative_to(app)))
    relative = str(path.relative_to(app))
    if 'Mach-O' in subprocess.check_output(['file', '-b', str(path)], text=True):
        embedded = subprocess.check_output(['otool', '-P', str(path)], stderr=subprocess.DEVNULL, text=True)
        errors.extend(f'{relative}: {error}' for error in embedded_plist_errors(embedded))
        load = subprocess.check_output(['otool', '-l', str(path)], text=True)
        if path.parent == contents / 'MacOS' and path.name == info['CFBundleExecutable']:
            if 'path @executable_path/../Frameworks ' not in load:
                errors.append('Main executable is missing the Frameworks rpath')
        if path.suffix == '.dylib' and path.parent != frameworks:
            errors.append('Native library is misplaced: ' + relative)
        dependencies = subprocess.check_output(['otool', '-L', str(path)], text=True)
        for reference in re.findall(r'^\s+(.+?) \(compatibility version', dependencies, re.M):
            if reference.startswith('/') and not reference.startswith(('/usr/lib/', '/System/Library/')):
                errors.append(f'{relative}: non-system absolute dependency {reference}')
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
entitlement_xml = subprocess.check_output(['codesign', '-d', '--entitlements', ':-', str(app)], stderr=subprocess.DEVNULL)
entitlements = plistlib.loads(entitlement_xml) if entitlement_xml.strip() else {}
if entitlements.get('com.apple.security.cs.allow-jit'):
    errors.append('Unexpected JIT entitlement')
if channel == 'AppStore':
    for key in ['app-sandbox', 'files.user-selected.read-write', 'files.bookmarks.app-scope', 'network.client', 'network.server']:
        if not entitlements.get('com.apple.security.' + key): errors.append('Missing entitlement: ' + key)
    if any('temporary-exception' in key for key in entitlements): errors.append('Temporary sandbox exception found')
    if info['CFBundleIdentifier'] == 'com.thankful.top.macexplorer': errors.append('App Store reused website bundle identity')
    for helper in (app / 'Contents/MacOS').glob('MacExplorer.*'):
        helper_xml = subprocess.check_output(['codesign', '-d', '--entitlements', ':-', str(helper)], stderr=subprocess.DEVNULL)
        helper_entitlements = plistlib.loads(helper_xml)
        if helper_entitlements != {'com.apple.security.app-sandbox': True, 'com.apple.security.inherit': True}:
            errors.append(f'{helper.name}: unexpected inherited sandbox entitlements')
elif channel == 'Website':
    if entitlements.get('com.apple.security.cs.disable-library-validation'):
        errors.append('The website app must keep library validation enabled')
host = next(item for item in native if item['path'] == 'Contents/MacOS/' + info['CFBundleExecutable'])
required_architectures = set(host['architectures'].split())
for item in native:
    if not required_architectures.issubset(set(item['architectures'].split())):
        errors.append(f"{item['path']}: missing an architecture required by the launcher")
subprocess.run(['codesign', '--verify', '--deep', '--strict', str(app)], check=True)
print(json.dumps({'bundle': str(app), 'channel': channel, 'identifier': info['CFBundleIdentifier'],
                  'minimum': info['LSMinimumSystemVersion'], 'native': native, 'errors': errors}, indent=2))
if errors: sys.exit(1)
