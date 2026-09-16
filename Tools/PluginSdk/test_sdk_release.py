import unittest
import json
from pathlib import Path
import tempfile
from unittest.mock import patch
import sdk_release


def release(version, draft=False, prerelease=False):
    return {'tag_name': version, 'draft': draft, 'prerelease': prerelease}

class ReleaseTests(unittest.TestCase):
    def test_first_and_upgrade(self):
        self.assertEqual('publish', sdk_release.decide('1.0.0', [release('v1.0.44')]))
        self.assertEqual('publish', sdk_release.decide('1.1.0', [release('sdk-v1.0.0')]))
    def test_same_version_never_mutates_published_release(self):
        releases = [release('sdk-v1.0.0')]
        with patch('sdk_release.gh') as gh:
            sdk_release.publish('owner/repo', '1.0.0', 'unused.zip', 'commit', releases)
            gh.assert_not_called()
    def test_downgrade_and_invalid_version_fail(self):
        with self.assertRaises(ValueError): sdk_release.decide('1.0.0', [release('sdk-v1.1.0')])
        with self.assertRaises(ValueError): sdk_release.decide('1.0.0-preview', [])
    def test_draft_retry_and_prerelease_do_not_block(self):
        self.assertEqual('publish', sdk_release.decide('1.0.0', [release('sdk-v1.0.0', draft=True), release('sdk-v2.0.0', prerelease=True)]))

    def test_failed_verification_keeps_draft_and_retry_completes(self):
        with tempfile.TemporaryDirectory() as folder:
            archive = Path(folder) / 'MacExplorer-PluginSDK-1.0.0.zip'
            archive.write_bytes(b'verified sdk artifact')
            draft = {**release('sdk-v1.0.0', draft=True), 'target_commitish': 'commit'}
            calls = []
            corrupted = True
            def fake_gh(*args):
                calls.append(args)
                host = 'v' + sdk_release.minimum_host_version()
                if args[0] == 'api' and args[1].endswith(host):
                    return json.dumps({'draft': False, 'prerelease': False, 'assets': [{'name': f'MacExplorer-{host[1:]}-macos.zip'}]})
                if args[0] == 'api':
                    self.assertIn('--paginate', args)
                    return json.dumps([[{**draft, 'assets': [{'name': archive.name, 'size': archive.stat().st_size}]}]])
                if args[:2] == ('release', 'download'):
                    target = Path(args[args.index('--dir') + 1]) / archive.name
                    target.write_bytes(b'broken' if corrupted else archive.read_bytes())
                return ''
            with patch('sdk_release.gh', side_effect=fake_gh):
                with self.assertRaisesRegex(ValueError, 'checksum'):
                    sdk_release.publish('owner/repo', '1.0.0', archive, 'commit', [draft])
                self.assertFalse(any(c[:2] == ('release', 'edit') for c in calls))
                corrupted = False
                draft['target_commitish'] = 'older-commit'
                sdk_release.publish('owner/repo', '1.0.0', archive, 'commit', [draft])
                self.assertEqual(('release', 'edit'), calls[-1][:2])
                self.assertIn('--latest=false', calls[-1])
                self.assertFalse(any(c[:2] == ('release', 'create') for c in calls))

if __name__ == '__main__': unittest.main()
