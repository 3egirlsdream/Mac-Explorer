import json
import tempfile
import unittest
from pathlib import Path
from generate_catalog import generate, load_languages, placeholders


class CatalogGenerationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.write("zh-CN", "简体中文", "zh-CN", {"greeting": "你好，{0}", "plain": "原文"}, literalKeys=["greeting"])
        self.write("en", "English", "en-US", {"greeting": "Hello, {0}", "plain": "Original"})

    def write(self, code, name, culture, strings, **extra):
        path = self.root / f"{code}.json"
        path.write_text(json.dumps(dict(id=code, nativeName=name, culture=culture, strings=strings, **extra)))
        return path

    def test_added_language_is_discovered_without_code_changes_and_removed_language_disappears(self):
        path = self.write("fr", "Français", "fr-FR", {"greeting": "Bonjour, {0}"})
        packs, _ = load_languages(self.root)
        self.assertEqual({pack["id"] for pack in packs}, {"zh-CN", "en", "fr"})
        self.assertEqual(next(pack for pack in packs if pack["id"] == "fr")["fallback"], "en")
        source = generate(self.root)
        self.assertIn('new("fr", "Français", "fr-FR", "en",', source)
        path.unlink()
        self.assertNotIn('new("fr",', generate(self.root))

    def test_malformed_json_and_duplicate_keys_fail_the_build(self):
        for content in ('{', '{"id":"fr","id":"en"}'):
            (self.root / "fr.json").write_text(content)
            with self.assertRaises(ValueError):
                generate(self.root)

    def test_missing_base_key_unknown_key_and_placeholder_mismatch_fail(self):
        for strings in ({"plain": "Original"}, {"unknown": "Extra"}, {"greeting": "Hello, {1}", "plain": "Original"}):
            self.write("en", "English", "en-US", strings)
            with self.assertRaises(ValueError):
                generate(self.root)

    def test_fallback_cycles_and_missing_targets_fail(self):
        for fallback in ("fr", "missing"):
            self.write("fr", "Français", "fr-FR", {}, fallback=fallback)
            with self.assertRaises(ValueError):
                generate(self.root)

    def test_escaped_braces_and_unicode_line_separators_remain_valid(self):
        self.assertEqual(["0"], placeholders("{{{0}}}"))
        self.write("fr", "Français\u2028test", "fr-FR", {"greeting": "Bonjour, {0}"})
        self.assertIn(r"\u2028", generate(self.root))
        self.assertNotIn("\u2028", generate(self.root))

    def test_aliases_are_generated_and_conflicts_fail(self):
        self.write("zh-Hant", "繁體中文", "zh-Hant", {}, aliases=["zh-TW", "zh-HK"])
        self.assertIn('["zh-TW", "zh-HK"]', generate(self.root))
        self.write("fr", "Français", "fr-FR", {}, aliases=["zh-TW"])
        with self.assertRaises(ValueError):
            generate(self.root)
        self.write("fr", "Français", "fr-FR", {}, aliases="fr-CA")
        with self.assertRaises(ValueError):
            generate(self.root)

    def test_generation_is_deterministic(self):
        self.assertEqual(generate(self.root), generate(self.root))


if __name__ == "__main__":
    unittest.main()
