import contextlib
import importlib.util
import io
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
from pathlib import Path

spec = importlib.util.spec_from_file_location("test_runner", Path(__file__).with_name("run-tests.py"))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class FailureReportTests(unittest.TestCase):
    def render(self, xml):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            runner.print_failures(ET.fromstring(xml))
        return output.getvalue()

    def test_every_failed_test_keeps_its_message_and_stack(self):
        output = self.render("""<assembly><collection>
          <test name="First"><failure><message>expected alpha</message><stack-trace>first.cs:12</stack-trace></failure></test>
          <test name="Second"><failure><message>expected beta</message><stack-trace>second.cs:24</stack-trace></failure></test>
        </collection></assembly>""")
        for detail in ("FAILED: First", "expected alpha", "first.cs:12", "FAILED: Second", "expected beta", "second.cs:24"):
            self.assertIn(detail, output)

    def test_passing_and_skipped_tests_do_not_emit_failures(self):
        self.assertEqual("", self.render('<assembly><collection><test result="Pass"/><test result="Skip"/></collection></assembly>'))

    def test_assembly_error_is_reported_without_a_test_name(self):
        output = self.render('<assembly><errors><error type="cleanup"><failure><message>cleanup failed</message></failure></error></errors></assembly>')
        self.assertIn("FAILED: cleanup", output)
        self.assertIn("cleanup failed", output)


class AssemblyGateTests(unittest.TestCase):
    def run_with_empty_first_assembly(self, profile_args, exit_code):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in ("MacExplorer.Tests", "MacExplorer.UnitTests"):
                executable = root / "Tests" / name / "bin/Debug/net10.0/osx-arm64" / name
                executable.parent.mkdir(parents=True)
                executable.touch()

            def run(command, log):
                empty = Path(command[2]).name == "MacExplorer.UnitTests"
                count = 0 if empty else 1
                report = Path(command[command.index("-xml") + 1])
                report.write_text(f'<assemblies><assembly total="{count}" passed="{count}"/></assemblies>')
                return exit_code if empty else 0

            with (patch.object(runner, "ROOT", root), patch.object(runner, "run", run),
                  patch.object(runner.subprocess, "call", return_value=0),
                  patch.object(runner.tempfile, "mkdtemp", return_value=temporary),
                  patch.object(sys, "argv", ["run-tests.py", "--no-build", *profile_args]),
                  contextlib.redirect_stdout(io.StringIO())):
                return runner.main()

    def test_full_requires_both_assemblies(self):
        self.assertNotEqual(0, self.run_with_empty_first_assembly(["full"], 0))

    def test_area_can_match_only_one_assembly_but_cannot_hide_process_errors(self):
        self.assertEqual(0, self.run_with_empty_first_assembly(["full", "--area", "FileDrop"], 0))
        self.assertEqual(7, self.run_with_empty_first_assembly(["full", "--area", "FileDrop"], 7))


if __name__ == "__main__":
    unittest.main()
