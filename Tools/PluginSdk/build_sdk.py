#!/usr/bin/env python3
"""Build the complete SDK ZIP. Requires Python 3 and .NET 10 on the build machine."""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]

def run(*args, cwd=ROOT, env=None):
    subprocess.run([str(a) for a in args], cwd=cwd, env=env, check=True)

def copy_source(source, destination):
    shutil.copytree(source, destination, ignore=shutil.ignore_patterns('bin', 'obj', '.DS_Store'))

def version():
    value = ET.parse(ROOT / 'Plugins/SDK/Sdk.Version.props').findtext('.//PluginSdkVersion')
    if not re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)', value or ''):
        raise ValueError('SDK version must be a three-part stable version')
    return value

def minimum_host_version():
    return ET.parse(ROOT / 'Plugins/SDK/Sdk.Version.props').findtext('.//MinimumHostVersion')

def build(output):
    ver = version()
    minimum_host = minimum_host_version()
    output = Path(output).resolve(); output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='macexplorer-sdk-build-') as staging:
        root = Path(staging) / f'MacExplorer-PluginSDK-{ver}'; root.mkdir()
        packages = root / 'packages'
        for project in ['SDK/MacExplorer.PluginSdk.csproj', 'UI/MacExplorer.PluginUi.csproj']:
            run('dotnet', 'pack', ROOT / 'Plugins' / project, '-c', 'Release', '-o', packages)
        for folder in ['SDK', 'UI', 'PackTool']:
            copy_source(ROOT / 'Plugins' / folder, root / 'src' / folder)
        copy_source(ROOT / 'Plugins/Examples', root / 'examples')
        for project in (root / 'examples').rglob('*.csproj'):
            project.write_text(project.read_text().replace('../../SDK/', '../../src/SDK/'))
        run('dotnet', 'publish', ROOT / 'Plugins/PackTool/MacExplorer.PluginPack.csproj', '-c', 'Release', '-o', root / 'tools', '--self-contained', 'false', '-p:UseAppHost=false')
        (root / 'NuGet.Config').write_text('''<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="sdk-local" value="packages"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="sdk-local"><package pattern="MacExplorer.Plugin*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>
''')
        for name in ['README.md', 'CHANGELOG.md', 'THIRD-PARTY-NOTICES.md']:
            text = (ROOT / 'Tools/PluginSdk' / name).read_text()
            (root / name).write_text(text.replace('{{SDK_VERSION}}', ver).replace('{{MINIMUM_HOST_VERSION}}', minimum_host))
        shutil.copy2(ROOT / 'Plugins/SDK/LICENSE', root / 'LICENSE')
        (root / 'API.md').write_text((ROOT / 'docs/plugins.md').read_text().replace('(developers/sdk.html)', '(https://3egirlsdream.github.io/Mac-Explorer/developers/sdk.html)'))
        (root / 'sdk.json').write_text(json.dumps({'version': ver, 'apiVersion': 2, 'minimumHostVersion': minimum_host, 'dotnet': '10.0', 'avalonia': '12.0.4'}, indent=2) + '\n')
        target = output / f'MacExplorer-PluginSDK-{ver}.zip'
        with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as archive:
            for path in sorted(root.rglob('*')):
                if path.is_file(): archive.write(path, path.relative_to(root.parent))
        return target

if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('--output', default='artifacts/sdk')
    print(build(parser.parse_args().output))
