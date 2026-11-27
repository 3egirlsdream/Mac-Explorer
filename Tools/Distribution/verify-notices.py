#!/usr/bin/env python3
"""Verify third-party notices for the NativeAOT app and its native libraries."""
import hashlib
import json
import pathlib
import re
import shutil
import sys


def stage_runtime_notices(notices, assets, packages):
    graph = json.loads(assets.read_text())
    tools = re.compile(r'(?:runtime\.[^.]+\.)?microsoft\.dotnet\.ilcompiler|microsoft\.net\.illink\.tasks')
    errors = []
    for name, library in graph['libraries'].items():
        package_id, _ = name.lower().split('/', 1)
        if library['type'] == 'package' and not tools.fullmatch(package_id) and name.lower() not in packages:
            errors.append('Unreviewed dependency version: ' + name)

    frameworks = graph['project']['frameworks']['net10.0']
    runtime_packs = [pack for pack in frameworks['downloadDependencies']
                     if pack['name'].lower().startswith(('microsoft.netcore.app.runtime.nativeaot.',
                                                         'microsoft.aspnetcore.app.runtime.'))]
    for pack in runtime_packs:
        name = pack['name'].lower()
        version = pack['version'].strip('[]').split(',')[0].strip()
        package_path = pathlib.Path(name, version)
        package = next((pathlib.Path(folder) / package_path for folder in graph['packageFolders']
                        if (pathlib.Path(folder) / package_path).is_dir()), None)
        if package is None:
            errors.append('Missing resolved runtime pack: ' + str(package_path))
            continue
        output = notices / 'Runtime' / package_path
        output.mkdir(parents=True, exist_ok=True)
        for filename in ('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT'):
            source = next((path for path in package.iterdir() if path.name.upper() == filename), None)
            if source is None:
                errors.append(f'Missing {filename} in runtime pack {package_path}')
            else:
                shutil.copyfile(source, output / filename)
    return errors


def verify(app, assets=None):
    resources = app / 'Contents/Resources'
    notices = resources / 'Notices'
    inventory = json.loads((notices / 'dependencies.json').read_text())
    covered = {'libMacExplorerNativeDrag.dylib', 'libMacExplorerStoreKit.dylib', 'libraw.dylib', 'libzip.dylib'}
    packages = {record['package'].lower() for record in inventory['packages']}
    errors = []
    for record in inventory['packages']:
        license_path = notices / record['notice']
        if not license_path.is_file() or hashlib.sha256(license_path.read_bytes()).hexdigest() != record['sha256']:
            errors.append('Missing or changed dependency notice: ' + record['package'])
        covered.update(record['artifacts'])

    if assets is not None:
        errors.extend(stage_runtime_notices(notices, assets, packages))
    for runtime_pack in ('microsoft.netcore.app.runtime.nativeaot.*', 'microsoft.aspnetcore.app.runtime.*'):
        versions = list((notices / 'Runtime').glob(runtime_pack + '/*'))
        if len(versions) != 1 or not all((versions[0] / name).is_file() for name in
                                         ('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')):
            errors.append('Missing runtime pack attribution: ' + runtime_pack)

    names = {path.name for path in (app / 'Contents/Frameworks').glob('*.dylib')}
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
        errors = verify(pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else None)
    except Exception as error:
        raise SystemExit('Notices audit failed: ' + str(error))
    if errors:
        raise SystemExit(1)
