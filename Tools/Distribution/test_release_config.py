import importlib.util
import pathlib
import unittest

spec = importlib.util.spec_from_file_location('release_config', pathlib.Path(__file__).with_name('verify-release-config.py'))
release_config = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release_config)


class SubmissionGateTests(unittest.TestCase):
    def test_subscription_requires_explicit_matching_product_id(self):
        configuration = {'appStoreSubscriptionProductId': 'com.example.release.annual'}
        release_config.validate_subscription('com.example.release.annual', configuration)
        for product, config in [('', configuration), ('other', configuration), ('annual', {}), ('annual', {'appStoreSubscriptionProductId': None})]:
            with self.subTest(product=product), self.assertRaises(ValueError):
                release_config.validate_subscription(product, config)

    def setUp(self):
        self.config = {'appStoreBundleIdentifier': 'com.example.release', 'privacyPolicyUrl': 'https://example.test/privacy/', 'privacyContact': 'privacy@example.test'}
        self.body = '隐私政策 Copilot LocalSend'

    def test_qa_and_mismatched_or_missing_formal_identity_rejected(self):
        for identity in ['com.example.qa', 'com.example.other', '']:
            with self.subTest(identity=identity), self.assertRaises(ValueError):
                release_config.validate(identity, self.config['privacyPolicyUrl'], self.config, lambda _: self.fail('identity must reject before network'))
        self.config['appStoreBundleIdentifier'] = None
        with self.assertRaises(ValueError):
            release_config.validate('com.example.release', self.config['privacyPolicyUrl'], self.config, lambda _: None)

    def test_policy_absent_invalid_inaccessible_redirected_or_empty_rejected(self):
        for url in ['', 'http://example.test/privacy/', 'https://different.test/privacy/']:
            with self.subTest(url=url), self.assertRaises(ValueError):
                release_config.validate('com.example.release', url, self.config, lambda _: None)
        for result in [(404, self.config['privacyPolicyUrl'], self.body), (200, 'https://example.test/', self.body), (200, self.config['privacyPolicyUrl'], '<h1>Home</h1>'), (200, self.config['privacyPolicyUrl'], '<h1>隐私政策草案</h1> Copilot LocalSend')]:
            with self.subTest(result=result), self.assertRaises(ValueError):
                release_config.validate('com.example.release', self.config['privacyPolicyUrl'], self.config, lambda _: result)

    def test_valid_formal_identity_and_policy_pass(self):
        for body in [self.body, self.body + ' new-contact@example.test 2026-10-02']:
            with self.subTest(body=body):
                release_config.validate('com.example.release', self.config['privacyPolicyUrl'], self.config,
                                        lambda url: (200, url, body))
