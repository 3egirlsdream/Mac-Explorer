#!/usr/bin/env python3
"""Restore/build/package from an extracted SDK with a separate NuGet cache."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile
import zipfile
import json
import shutil


def verify(archive, directory):
    directory = Path(directory).resolve(); directory.mkdir(parents=True, exist_ok=False)
    with zipfile.ZipFile(archive) as bundle:
        for item in bundle.infolist():
            if not (directory / item.filename).resolve().is_relative_to(directory): raise ValueError('Unsafe SDK ZIP path')
        bundle.extractall(directory)
    roots = list(directory.glob('MacExplorer-PluginSDK-*'))
    if len(roots) != 1: raise ValueError('Expected one SDK root')
    root = roots[0]
    env = {**os.environ, 'NUGET_PACKAGES': str(directory / 'nuget-cache')}
    def run(*args, succeeds=True):
        result = subprocess.run([str(a) for a in args], cwd=root, env=env)
        if (result.returncode == 0) != succeeds: raise RuntimeError(f'Unexpected exit {result.returncode}: {args}')
    for name in ['SimplePlugin', 'AccountPlugin']:
        run('dotnet', 'build', f'examples/{name}/{name}.csproj', '-c', 'Release', '--configfile', 'NuGet.Config')
        run('dotnet', 'tools/MacExplorer.PluginPack.dll', f'examples/{name}/bin/Release/net10.0', f'{name}.mexplug')
        run('dotnet', 'tools/MacExplorer.PluginPack.dll', f'examples/{name}/bin/Release/net10.0', f'{name}.mexplug', succeeds=False)
    run('dotnet', 'build', 'src/PackTool/MacExplorer.PluginPack.csproj', '-c', 'Release', '--configfile', 'NuGet.Config')
    fixture = root / 'invalid-plugin'; shutil.copytree(root / 'examples/SimplePlugin/bin/Release/net10.0', fixture)
    manifest = fixture / 'plugin.json'; original = manifest.read_text()
    data = json.loads(original); data['entry'] = '../SimplePlugin.dll'; manifest.write_text(json.dumps(data))
    run('dotnet', 'tools/MacExplorer.PluginPack.dll', fixture, 'invalid.mexplug', succeeds=False)
    manifest.write_text(original)
    if os.name != 'nt':
        (fixture / 'link').symlink_to(fixture / 'SimplePlugin.dll')
        run('dotnet', 'tools/MacExplorer.PluginPack.dll', fixture, 'invalid.mexplug', succeeds=False)
        (fixture / 'link').unlink()
    (fixture / 'SimplePlugin.dll').write_text('not an assembly')
    run('dotnet', 'tools/MacExplorer.PluginPack.dll', fixture, 'invalid.mexplug', succeeds=False)
    if (root / 'invalid.mexplug').exists(): raise RuntimeError('Invalid package was retained')
    shutil.rmtree(fixture)
    print(f'Independent SDK verification passed: {root}')
    return root

if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('archive'); parser.add_argument('--directory')
    args = parser.parse_args()
    if args.directory: verify(args.archive, args.directory)
    else:
        with tempfile.TemporaryDirectory(prefix='sdk-external-') as directory:
            verify(args.archive, Path(directory) / 'extracted')
