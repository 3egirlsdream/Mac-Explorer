#!/usr/bin/env python3
"""Submission gate. QA identities and ad-hoc builds remain independent."""
import argparse
import json
import pathlib
import re
import urllib.parse
import urllib.request


def validate(identifier, policy, configuration, fetch):
    expected = configuration.get('appStoreBundleIdentifier')
    if not expected or not re.fullmatch(r'[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+', expected):
        raise ValueError('Configure the actual App Store bundle identifier in release-config.json before submission.')
    if re.search(r'(?:^|[.\-])(qa|test|debug|dev)(?:$|[.\-])', identifier, re.I) or identifier != expected:
        raise ValueError('QA or mismatched identity cannot be submitted.')
    uri = urllib.parse.urlparse(policy)
    if uri.scheme != 'https' or not uri.netloc or uri.username or uri.password or policy != configuration['privacyPolicyUrl']:
        raise ValueError('A configured, matching HTTPS privacy policy is required.')
    status, final_url, content = fetch(policy)
    if status != 200 or final_url != policy:
        raise ValueError('Privacy policy must return HTTP 200 at its configured public URL.')
    if not all(marker in content for marker in ['隐私政策', 'Copilot', 'LocalSend']):
        raise ValueError('The formal privacy policy content is missing or invalid.')
    if '<h1>隐私政策草案' in content:
        raise ValueError('A policy draft cannot be submitted.')


def fetch_policy(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': 'MacExplorer-release-preflight'}), timeout=20) as response:
        return response.status, response.url, response.read(2 * 1024 * 1024).decode('utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--identifier', required=True)
    parser.add_argument('--policy', required=True)
    parser.add_argument('--config', type=pathlib.Path, default=pathlib.Path(__file__).with_name('release-config.json'))
    arguments = parser.parse_args()
    try:
        validate(arguments.identifier, arguments.policy, json.loads(arguments.config.read_text()), fetch_policy)
    except Exception as error:
        raise SystemExit('Submission preflight rejected: ' + str(error))
    print('Formal identity and public privacy policy verified; no upload was performed.')
