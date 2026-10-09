import os
from pathlib import Path
import tempfile
import textwrap
import unittest
from unittest.mock import patch


class CurrentReleaseRebuildTests(unittest.TestCase):
    def run_detection(self, version="1.0.54", tags="v1.0.53\nv1.0.54", rebuild=False, sync=False):
        workflow = (Path(__file__).resolve().parents[2] / ".github/workflows/release.yml").read_text()
        script = textwrap.dedent(workflow.split("python3 <<'PY'\n", 1)[1].split("\n          PY", 1)[0])
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project = root / "app.csproj"
            project.write_text(f"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>")
            env = {
                "PROJECT_FILE": str(project), "GITHUB_OUTPUT": str(root / "output"),
                "GITHUB_STEP_SUMMARY": str(root / "summary"),
                "REBUILD_CURRENT_VERSION": str(rebuild).lower(), "SYNC_BACKEND_ONLY": str(sync).lower(),
            }
            with patch.dict(os.environ, env), patch("subprocess.check_output", return_value=tags):
                exec(compile(script, "release-version-detection", "exec"), {"__name__": "__main__"})
            return dict(line.split("=", 1) for line in (root / "output").read_text().splitlines())

    def test_existing_version_still_skips_without_explicit_rebuild(self):
        self.assertEqual("noop", self.run_detection()["mode"])

    def test_explicit_rebuild_uses_existing_highest_version(self):
        result = self.run_detection(rebuild=True)
        self.assertEqual("republish", result["mode"])
        self.assertEqual("v1.0.54", result["tag"])
        self.assertEqual("v1.0.53", result["previous_tag"])

    def test_rebuild_cannot_replace_an_older_release_or_create_a_new_one(self):
        for version in ("1.0.53", "1.0.55"):
            with self.subTest(version=version), self.assertRaises(SystemExit):
                self.run_detection(version=version, rebuild=True)

    def test_backend_sync_and_rebuild_cannot_be_combined(self):
        with self.assertRaises(SystemExit):
            self.run_detection(rebuild=True, sync=True)

    def test_regular_publish_and_backend_sync_remain_available(self):
        self.assertEqual("publish", self.run_detection(version="1.0.55")["mode"])
        self.assertEqual("sync", self.run_detection(sync=True)["mode"])


if __name__ == "__main__":
    unittest.main()
