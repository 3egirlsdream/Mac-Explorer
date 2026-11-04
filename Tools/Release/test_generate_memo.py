import subprocess
import tempfile
import unittest
from pathlib import Path

from generate_memo import extract_changelog, generate_memo


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

    def commit(self, message, cleanup="verbatim"):
        self.git("-c", "commit.gpgsign=false", "commit", "--allow-empty", f"--cleanup={cleanup}", "-qm", message)

    def test_extracts_numbered_entries_and_respects_release_boundaries(self):
        self.commit("old release\n\n1. 旧版条目不得包含。\n\n验证：旧版")
        self.git("tag", "v1.0.0")
        long_entry = "详细更新内容" * 1000
        self.commit(f"feat: 中文标题\n\n1. 新增目录统计。\n2. {long_entry}\n\n验证：不得写入后台。")
        literal_entry = '保留 $(echo secret) 和 `code`，以及 "引号" 与 \\n。'
        self.commit(f"fix: follow-up\n\n1. {literal_entry}\n2. 修复页签恢复。\n\nCo-authored-by: Test <test@example.invalid>")
        self.git("tag", "v1.0.1")
        self.commit("future commit\n\n1. 未来条目不得包含。")
        memo = generate_memo("v1.0.1", "v1.0.0", self.repo)
        self.assertEqual(memo, f"1. {literal_entry}\n2. 修复页签恢复。\n3. 新增目录统计。\n4. {long_entry}")
        self.assertGreater(len(memo), 3000)

    def test_first_release_collects_valid_history_and_skips_legacy_messages(self):
        self.commit("first\n\n1. 首次更新。")
        self.commit("legacy subject only")
        self.commit("legacy body\n\n- 非编号旧格式。")
        self.commit("second\n\n1. 第二次更新。")
        self.git("tag", "v1.0.0")
        self.assertEqual(generate_memo("v1.0.0", cwd=self.repo), "1. 第二次更新。\n2. 首次更新。")

    def test_accepts_end_of_message_after_git_strips_trailing_blank_lines(self):
        self.commit("fix: 修复\n\n1. 修复目录统计。\n\n", cleanup="strip")
        self.assertEqual(generate_memo("HEAD", cwd=self.repo), "1. 修复目录统计。")

    def test_preserves_wrapped_entries(self):
        self.commit("feat: 更新\n\n1. 支持较长说明，\n   后续文字保留缩进。\n2. 第二项。\n\n验证：通过。")
        self.assertEqual(generate_memo("HEAD", cwd=self.repo), "1. 支持较长说明，\n   后续文字保留缩进。\n2. 第二项。")

    def test_blank_line_ends_changelog_before_numbered_validation(self):
        self.commit("feat: 更新\n\n1. 新增功能。\n\n1. 单元测试通过。\n2. 构建通过。")
        self.assertEqual(generate_memo("HEAD", cwd=self.repo), "1. 新增功能。")

    def test_accepts_crlf_and_whitespace_only_separator_lines(self):
        self.assertEqual(
            extract_changelog("feat: 更新\r\n \t\r\n\r\n1. 新增功能。\r\n2. 修复问题。\r\n\t\r\n验证：通过。"),
            ["新增功能。", "修复问题。"],
        )

    def test_rejects_missing_or_malformed_first_body_paragraph(self):
        for message in (
            "feat: 只有标题",
            "feat: 没有分隔空行\n1. 新增功能。",
            "feat: 旧格式\n\n普通说明\n\n1. 不是更新日志段。",
            "feat: 非编号\n\n- 新增功能。",
            "feat: 错误起点\n\n2. 新增功能。",
            "feat: 跳号\n\n1. 新增功能。\n3. 修复问题。",
            "feat: 混入说明\n\n1. 新增功能。\n验证：未用空行隔开。",
        ):
            with self.subTest(message=message):
                self.assertEqual(extract_changelog(message), [])

    def test_skips_merge_message_to_avoid_duplicate_entries(self):
        self.commit("initial")
        branch = self.git("symbolic-ref", "--short", "HEAD").decode().strip()
        self.git("checkout", "-qb", "feature")
        self.commit("feat: 更新\n\n1. 新增功能。")
        self.git("checkout", "-q", branch)
        self.git("-c", "commit.gpgsign=false", "merge", "--no-ff", "feature", "-qm", "Merge feature\n\n1. 新增功能。")
        self.assertEqual(generate_memo("HEAD", cwd=self.repo), "1. 新增功能。")

    def test_cli_writes_only_changelog_to_utf8_file(self):
        self.commit("feat: 更新\n\n1. 新增功能。\n\n验证：通过。")
        output = Path(self.repo) / "memo.txt"
        subprocess.run(
            ["python3", str(Path(__file__).with_name("generate_memo.py")), "--tag", "HEAD", "--output", str(output)],
            cwd=self.repo, check=True,
        )
        self.assertEqual(output.read_text(encoding="utf-8"), "1. 新增功能。")

    def test_range_without_valid_changelog_fails_instead_of_publishing_raw_messages(self):
        self.commit("legacy subject\n\ninternal details")
        with self.assertRaisesRegex(ValueError, "no numbered changelog blocks"):
            generate_memo("HEAD", cwd=self.repo)

    def test_empty_range_fails_instead_of_saving_empty_notes(self):
        self.commit("first")
        self.git("tag", "v1.0.0")
        with self.assertRaises(ValueError):
            generate_memo("v1.0.0", "v1.0.0", self.repo)


if __name__ == "__main__":
    unittest.main()
