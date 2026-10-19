import contextlib
import importlib.util
import io
import unittest
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


if __name__ == "__main__":
    unittest.main()
