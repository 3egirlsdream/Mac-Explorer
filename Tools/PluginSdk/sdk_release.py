#!/usr/bin/env python3
"""Version-gated SDK releases; only draft assets may be replaced."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
from build_sdk import version, minimum_host_version

TAG = re.compile(r'^sdk-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')

def parsed(tag):
    match = TAG.fullmatch(tag)
    return tuple(map(int, match.groups())) if match else None

def decide(current, releases):
    current_version = parsed('sdk-v' + current)
    if current_version is None: raise ValueError('Invalid SDK version')
    published = [parsed(r['tag_name']) for r in releases if not r['draft'] and not r['prerelease'] and parsed(r['tag_name'])]
    latest = max(published, default=None)
    if latest and current_version < latest: raise ValueError('SDK version is lower than the published version')
    return 'noop' if latest == current_version else 'publish'

def gh(*args):
    return subprocess.check_output(['gh', *map(str, args)], text=True).strip()

def list_releases(repo):
    pages = json.loads(gh('api', '--paginate', '--slurp', f'repos/{repo}/releases?per_page=100'))
    return [release for page in pages for release in page]

def publish(repo, current, archive, commit, releases):
    tag = f'sdk-v{current}'
    if decide(current, releases) != 'publish': return
    minimum_host = minimum_host_version()
    host = json.loads(gh('api', f'repos/{repo}/releases/tags/v{minimum_host}'))
    if host['draft'] or host['prerelease'] or not any(a['name'] == f'MacExplorer-{minimum_host}-macos.zip' for a in host['assets']):
        raise ValueError(f'Compatible host release v{minimum_host} is not ready')
    archive = Path(archive)
    expected = f'MacExplorer-PluginSDK-{current}.zip'
    if archive.name != expected or not archive.is_file(): raise ValueError('SDK ZIP missing or incorrectly named')
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    existing = next((r for r in releases if r['tag_name'] == tag), None)
    if existing and not existing['draft']: raise ValueError('Published SDK releases cannot be overwritten')
    with tempfile.TemporaryDirectory(prefix='sdk-release-') as temporary:
        notes = Path(temporary) / 'notes.md'
        notes.write_text(f'# Plugin SDK {current}\n\n完整 SDK ZIP，包含基础 SDK、窗口 SDK、源码、示例与打包工具。\n\n要求：Mac Explorer {minimum_host}+、.NET 10；窗口 SDK 使用 Avalonia 12.0.4，协议 API v1/v2。\n\n源码提交：{commit}\n\nSHA-256：`{digest}`\n\n' + (Path(__file__).parent / 'CHANGELOG.md').read_text())
        if existing:
            if existing.get('target_commitish') != commit: raise ValueError('Draft belongs to a different commit; rerun its original workflow or resolve the draft first')
        else:
            gh('release', 'create', tag, '--repo', repo, '--target', commit, '--title', f'Mac Explorer Plugin SDK {current}', '--notes-file', notes, '--draft', '--latest=false')
        gh('release', 'upload', tag, archive, '--repo', repo, '--clobber')
        release = json.loads(gh('api', f'repos/{repo}/releases/tags/{tag}'))
        if not release['draft']: raise ValueError('Release must remain a draft until verification succeeds')
        assets = release['assets']
        if len(assets) != 1 or assets[0]['name'] != expected or assets[0]['size'] != archive.stat().st_size:
            raise ValueError('Unexpected draft assets')
        downloaded = Path(temporary) / expected
        gh('release', 'download', tag, '--repo', repo, '--pattern', expected, '--dir', temporary)
        if hashlib.sha256(downloaded.read_bytes()).hexdigest() != digest: raise ValueError('Uploaded ZIP checksum mismatch')
        gh('release', 'edit', tag, '--repo', repo, '--notes-file', notes, '--draft=false', '--latest=false')

if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('action', choices=['check', 'publish']); parser.add_argument('--archive')
    args = parser.parse_args(); repo = os.environ['GITHUB_REPOSITORY']; current = version(); releases = list_releases(repo)
    mode = decide(current, releases)
    print(f'SDK {current}: {mode}')
    if output := os.environ.get('GITHUB_OUTPUT'):
        with open(output, 'a') as stream: stream.write(f'mode={mode}\nversion={current}\n')
    if args.action == 'publish': publish(repo, current, args.archive, os.environ['GITHUB_SHA'], releases)
