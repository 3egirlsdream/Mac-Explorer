import importlib.util
import pathlib
import plistlib
import tempfile
import unittest


spec = importlib.util.spec_from_file_location('branding', pathlib.Path(__file__).with_name('configure-bundle-branding.py'))
branding = importlib.util.module_from_spec(spec)
spec.loader.exec_module(branding)


class BundleBrandingTests(unittest.TestCase):
    def test_channel_branding_preserves_identity_and_version(self):
        source = plistlib.loads((pathlib.Path(__file__).parents[2] / 'Platforms/MacOS/Info.plist').read_bytes())
        for channel in ['Website', 'AppStore']:
            with self.subTest(channel=channel), tempfile.TemporaryDirectory() as temporary:
                app = pathlib.Path(temporary) / '文件资源管理器.app'
                info_path = app / 'Contents/Info.plist'
                info_path.parent.mkdir(parents=True)
                info_path.write_bytes(plistlib.dumps(source))
                branding.configure(app, channel)
                result = plistlib.loads(info_path.read_bytes())
                for key in ['CFBundleIdentifier', 'CFBundleExecutable', 'CFBundleVersion', 'CFBundleShortVersionString']:
                    self.assertEqual(source[key], result[key])
                for locale, description in branding.NETWORK_USAGE.items():
                    strings = app / 'Contents/Resources' / (locale + '.lproj') / 'InfoPlist.strings'
                    self.assertIn(f'"NSLocalNetworkUsageDescription" = "{description}";', strings.read_text(encoding='utf-16'))
                if channel == 'Website':
                    self.assertEqual(source, result)
                else:
                    self.assertEqual('文件资源管理器', result['CFBundleDisplayName'])
                    for locale, name in branding.STORE_NAMES.items():
                        strings = app / 'Contents/Resources' / (locale + '.lproj') / 'InfoPlist.strings'
                        self.assertIn(f'"CFBundleDisplayName" = "{name}";', strings.read_text(encoding='utf-16'))
                        self.assertIn(f'"CFBundleName" = "{name}";', strings.read_text(encoding='utf-16'))
