import plistlib
import unittest

from helper_metadata import embedded_plist_errors


class HelperMetadataTests(unittest.TestCase):
    def dump(self, metadata):
        return 'MacExplorer.ImageAnalysis:\n(__TEXT,__info_plist) section\n' + plistlib.dumps(metadata).decode()

    def test_background_helper_without_identifier_is_rejected(self):
        self.assertEqual(1, len(embedded_plist_errors(self.dump({'LSBackgroundOnly': True}))))

    def test_identifiers_rejected_by_app_store_are_rejected_locally(self):
        for identifier in ['', 'com.example.', 'com.example.helper_name', '应用.助手']:
            with self.subTest(identifier=identifier):
                self.assertTrue(embedded_plist_errors(self.dump({'CFBundleIdentifier': identifier})))

    def test_background_helper_identifier_and_each_architecture_are_checked(self):
        valid = self.dump({'CFBundleIdentifier': 'com.thankful.top.macexplorer.image-analysis', 'LSBackgroundOnly': True})
        self.assertEqual([], embedded_plist_errors(valid))
        self.assertTrue(embedded_plist_errors(valid + self.dump({'LSBackgroundOnly': True})))

    def test_unbundled_executable_without_embedded_plist_is_allowed(self):
        self.assertEqual([], embedded_plist_errors('MacExplorer.Git:\n'))
