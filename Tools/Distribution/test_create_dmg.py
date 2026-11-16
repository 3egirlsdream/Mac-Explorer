import contextlib
import importlib.util
import io
import pathlib
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("create_dmg", pathlib.Path(__file__).with_name("create-dmg.py"))
create_dmg = importlib.util.module_from_spec(spec)
spec.loader.exec_module(create_dmg)


class CreateDmgTests(unittest.TestCase):
    def run_creation(self, results):
        with patch.object(create_dmg.subprocess, "run", side_effect=results) as run, \
                patch.object(create_dmg.time, "sleep") as sleep, \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            code = create_dmg.create_dmg("staging with spaces", "release with spaces.dmg")
        return code, run, sleep

    def test_busy_service_retries_without_changing_paths_or_packaging_options(self):
        code, run, sleep = self.run_creation([
            subprocess.CompletedProcess([], 1, "", "hdiutil: create failed - Resource busy\n"),
            subprocess.CompletedProcess([], 0, "created\n", "")])
        self.assertEqual(0, code)
        self.assertEqual(2, run.call_count)
        self.assertEqual(run.call_args_list[0], run.call_args_list[1])
        self.assertEqual(["hdiutil", "create", "-volname", "Mac Explorer", "-srcfolder", "staging with spaces",
                          "-ov", "-format", "UDZO", "-imagekey", "zlib-level=9", "release with spaces.dmg"],
                         run.call_args.args[0])
        sleep.assert_called_once_with(3)

    def test_permanent_failure_is_returned_without_retry(self):
        code, run, sleep = self.run_creation([subprocess.CompletedProcess([], 7, "", "Permission denied")])
        self.assertEqual(7, code)
        self.assertEqual(1, run.call_count)
        sleep.assert_not_called()

    def test_busy_retries_are_bounded_and_preserve_failure(self):
        code, run, sleep = self.run_creation([subprocess.CompletedProcess([], 1, "", "Resource busy")] * 3)
        self.assertEqual(1, code)
        self.assertEqual(3, run.call_count)
        self.assertEqual([3, 6], [call.args[0] for call in sleep.call_args_list])
