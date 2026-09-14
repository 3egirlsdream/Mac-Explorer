import subprocess
import tempfile
import unittest

from generate_memo import generate_memo


class ReleaseMemoTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.repo = self.directory.name
        self.git("init", "-q")
        self.git("config", "user.name", "Release test")
        self.git("config", "user.email", "release@example.invalid")

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.repo)

    def commit(self, message):
        self.git("-c", "commit.gpgsign=false", "commit", "--allow-empty", "--cleanup=verbatim", "-qm", message)

    def test_preserves_full_messages_and_release_boundaries(self):
        self.commit("old release must be excluded")
        self.git("tag", "v1.0.0")
        body = "feat: 中文标题\n\n分类：\n  - 保留缩进\n\n" + "详细更新内容" * 1000
        self.commit(body)
        second = 'fix: follow-up\n\nLiteral $(echo secret) and `code`\n\n尾段'
        self.commit(second)
        self.git("tag", "v1.0.1")
        self.commit("future commit must be excluded")
        memo = generate_memo("1.0.1", "v1.0.1", "v1.0.0", self.repo)
        self.assertEqual(memo, f"Mac Explorer 1.0.1\n\n{second}\n\n---\n\n{body}")
        self.assertGreater(len(memo), 3000)

    def test_first_release_contains_full_history(self):
        self.commit("first\n\nbody")
        self.git("tag", "v1.0.0")
        self.assertEqual(generate_memo("1.0.0", "v1.0.0", cwd=self.repo), "Mac Explorer 1.0.0\n\nfirst\n\nbody")

    def test_empty_range_fails_instead_of_saving_empty_notes(self):
        self.commit("first")
        self.git("tag", "v1.0.0")
        with self.assertRaises(ValueError):
            generate_memo("1.0.0", "v1.0.0", "v1.0.0", self.repo)


if __name__ == "__main__":
    unittest.main()
