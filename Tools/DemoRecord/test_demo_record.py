"""Isolation and replay inputs are safety-critical; these tests never open an application."""
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
import zipfile

from fixtures import prepare, safe_root
from run import REPO, sha, validate_spec


class DemoRecorderTests(unittest.TestCase):
    def test_profile_rejects_real_paths_traversal_and_symlink_ancestors(self):
        for value in ['/',str(Path.home()),'/private/tmp/fkfinder-test.bad/../../Users']:
            with self.assertRaises(ValueError):safe_root(value)
        with tempfile.TemporaryDirectory(prefix='fkfinder-test.safety-',dir='/private/tmp') as directory:
            parent=Path(directory);(parent/'alias').symlink_to('/private/tmp',target_is_directory=True)
            with self.assertRaises(ValueError):safe_root(parent/'alias/child')

    def test_fixture_is_deterministic_and_uses_only_local_data(self):
        with tempfile.TemporaryDirectory(prefix='fkfinder-test.fixture-',dir='/private/tmp') as directory:
            parent=Path(directory)
            for name in ['first','second']:prepare(parent/name,REPO,'0'*64)
            a=json.loads((parent/'first/fixture-manifest.json').read_text())
            b=json.loads((parent/'second/fixture-manifest.json').read_text())
            self.assertEqual(a,b)
            with sqlite3.connect(parent/'first/.macexplorer/index.db') as db:
                settings=dict(db.execute('SELECT key,value FROM app_settings'))
            self.assertEqual(settings['localsend_enabled'],'false')
            self.assertEqual(settings['copilot.enabled'],'false')
            self.assertEqual(settings['ai_analysis_enabled'],'false')
            for entry in a['files']:
                path=parent/'first'/entry['path']
                self.assertEqual(sha(path),entry['sha256'])
            with zipfile.ZipFile(parent/'first/Documents/山野计划/项目归档.zip') as archive:
                self.assertIsNone(archive.testzip())
                self.assertIn('产品方案.md',archive.namelist())
            with self.assertRaises(FileExistsError):prepare(parent/'first',REPO,'0'*64)

    def test_scene_ids_cannot_escape_output_and_every_action_is_known(self):
        spec=json.loads((REPO/'Tools/DemoRecord/scenes/local.json').read_text())
        validate_spec(spec)
        for scene_id in ['../escape','bad/name','UPPER']:
            with self.subTest(scene_id=scene_id):
                with self.assertRaises(ValueError):validate_spec({'schemaVersion':1,'scenes':[dict(spec['scenes'][0],id=scene_id)]})
        with self.assertRaises(ValueError):validate_spec({'schemaVersion':1,'scenes':[spec['scenes'][0]]*2})
        broken=dict(spec['scenes'][0],record=[{'action':'unknown'}])
        with self.assertRaises(ValueError):validate_spec({'schemaVersion':1,'scenes':[broken]})


if __name__=='__main__':unittest.main()
