#!/usr/bin/env python3
"""Match bundled managed/native dependencies to pinned license and resource notices."""
import hashlib
import json
import pathlib
import sys
import zipfile


def verify(app):
    resources = app / 'Contents/Resources'
    notices = resources / 'Notices'
    inventory = json.loads((notices / 'dependencies.json').read_text())
    covered = {'MacExplorer.dll', 'MacExplorer.PluginSdk.dll', 'MacExplorer.PluginUi.dll',
               'MacExplorer.FileConversion.Plugin.dll', 'LiquidGlassAvaloniaUI.dll',
               'libMacExplorerNativeDrag.dylib', 'libMacExplorerPreview.dylib',
               'libMacExplorerStoreKit.dylib', 'libraw.dylib'}
    packages = {record['package'].lower() for record in inventory['packages']}
    errors = []
    for record in inventory['packages']:
        license_path = notices / record['notice']
        if not license_path.is_file() or hashlib.sha256(license_path.read_bytes()).hexdigest() != record['sha256']:
            errors.append('Missing or changed dependency notice: ' + record['package'])
        covered.update(record['artifacts'])
    for path in (resources / 'Managed').glob('*.deps.json'):
        dependencies = json.loads(path.read_text())
        for name, library in dependencies.get('libraries', {}).items():
            if library['type'] in ['package', 'runtimepack'] and name.lower().removeprefix('runtimepack.') not in packages:
                errors.append('Unreviewed dependency version: ' + name)
    # Include packaged conversion dependencies as well as the host's loose code.
    names = {path.name for path in app.rglob('*') if path.is_file() and path.suffix in ['.dll', '.dylib']}
    for package in resources.rglob('*.mexplug'):
        with zipfile.ZipFile(package) as archive:
            names.update(pathlib.PurePosixPath(name).name for name in archive.namelist() if name.endswith(('.dll', '.dylib')))
            for name in archive.namelist():
                if name.endswith('.deps.json'):
                    for key, library in json.loads(archive.read(name)).get('libraries', {}).items():
                        if library['type'] == 'package' and key.lower() not in packages:
                            errors.append('Unreviewed plugin dependency version: ' + key)
    for name in sorted(names - covered):
        errors.append('No dependency attribution for bundled code: ' + name)
    for resource in inventory['resources']:
        for name, digest in resource['notices'].items():
            path = resources / name
            if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
                errors.append('Missing or changed resource notice: ' + name)
    policy = resources / 'PrivacyPolicy.txt'
    if not policy.is_file() or not all(marker in policy.read_text() for marker in ['隐私政策', 'Copilot', 'LocalSend']):
        errors.append('Missing formal bundled privacy policy')
    report = {'bundle': str(app), 'packages': len(packages), 'codeArtifacts': len(names),
              'resources': len(inventory['resources']), 'errors': errors}
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return errors


if __name__ == '__main__':
    try:
        errors = verify(pathlib.Path(sys.argv[1]))
    except Exception as error:
        raise SystemExit('Notices audit failed: ' + str(error))
    if errors:
        raise SystemExit(1)
