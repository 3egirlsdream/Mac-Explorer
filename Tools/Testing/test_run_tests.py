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


class ShardingTests(unittest.TestCase):
    def test_partition_classes_balances_heavy_classes_without_drops_or_duplicates(self):
        weights = {"A": 20, "B": 18, "C": 12, "D": 10, "E": 4, "F": 3, "G": 2, "H": 1}
        shards = runner.partition_classes(weights, 4)
        self.assertEqual(4, len(shards))
        flattened = [cls for shard in shards for cls in shard]
        self.assertEqual(sorted(weights), sorted(flattened))
        shard_loads = [sum(weights[c] for c in shard) for shard in shards]
        self.assertLessEqual(max(shard_loads) - min(shard_loads), 5)

    def test_partition_classes_skips_sharding_for_small_class_sets(self):
        shards = runner.partition_classes({"A": 5, "B": 3}, 4)
        self.assertEqual([["A", "B"]], shards)

    def test_build_class_filter_splits_namespace_and_class(self):
        self.assertEqual(
            "/MacExplorer.Tests/MacExplorer.Tests/FixedButtonSizeTests/[Kind=UI]",
            runner.build_class_filter("MacExplorer.Tests", "MacExplorer.Tests.FixedButtonSizeTests", "Kind=UI"),
        )
        self.assertEqual(
            "/MacExplorer.Tests//GlobalClass/[Kind=UI]",
            runner.build_class_filter("MacExplorer.Tests", "GlobalClass", "Kind=UI"),
        )

    def test_merge_assembly_xml_sums_counts_and_keeps_failures_and_errors(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            s1 = root / "s1.xml"
            s2 = root / "s2.xml"
            merged = root / "merged.xml"
            s1.write_text(
                '<assemblies><assembly name="MacExplorer.Tests" total="2" passed="2" failed="0" skipped="0" not-run="0" errors="0" time="1.250">'
                '<collection><test name="T1" time="1.250"/></collection></assembly></assemblies>'
            )
            s2.write_text(
                '<assemblies><assembly name="MacExplorer.Tests" total="3" passed="1" failed="1" skipped="1" not-run="0" errors="1" time="2.500">'
                '<errors><error type="boom"><failure><message>err</message></failure></error></errors>'
                '<collection><test name="T2" time="2.500"><failure><message>bad</message></failure></test></collection></assembly></assemblies>'
            )
            assembly = runner.merge_assembly_xml([s1, s2], merged)
            self.assertEqual("5", assembly.get("total"))
            self.assertEqual("3", assembly.get("passed"))
            self.assertEqual("1", assembly.get("failed"))
            self.assertEqual("1", assembly.get("skipped"))
            self.assertEqual("1", assembly.get("errors"))
            self.assertEqual("3.750", assembly.get("time"))
            reparsed = ET.parse(merged).getroot().find("assembly")
            self.assertEqual(2, len(list(reparsed.iter("test"))))
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                runner.print_failures(reparsed)
            self.assertIn("FAILED: T2", output.getvalue())
            self.assertIn("FAILED: boom", output.getvalue())

    def test_sharded_ui_runs_parallel_shards_and_merges_reports(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            executable = root / "Tests/MacExplorer.Tests/bin/Debug/net10.0/osx-arm64/MacExplorer.Tests"
            executable.parent.mkdir(parents=True)
            executable.touch()
            invocations = []

            def fake_run(command, log):
                invocations.append(command)
                report = Path(command[command.index("-xml") + 1])
                class_filters = [command[i + 1] for i, arg in enumerate(command) if arg == "-filter"]
                tests_xml = "".join(f'<test name="{f}" time="0.1"/>' for f in class_filters)
                report.write_text(
                    f'<assemblies><assembly total="{len(class_filters)}" passed="{len(class_filters)}" failed="0" skipped="0" not-run="0" errors="0" time="0.2">'
                    f'<collection>{tests_xml}</collection></assembly></assemblies>'
                )
                log.write_text("ok\n")
                return 0

            weights = {f"MacExplorer.Tests.C{i}": i + 1 for i in range(6)}
            with (patch.object(runner, "ROOT", root),
                  patch.object(runner, "run", fake_run),
                  patch.object(runner, "discover_class_weights", return_value=weights),
                  patch.object(runner.subprocess, "call", return_value=0),
                  patch.object(runner.tempfile, "mkdtemp", return_value=temporary),
                  patch.object(sys, "argv", ["run-tests.py", "ui", "--no-build", "--jobs", "3"]),
                  contextlib.redirect_stdout(io.StringIO())):
                code = runner.main()

            self.assertEqual(0, code)
            self.assertEqual(3, len(invocations))
            merged = ET.parse(root / "MacExplorer.Tests.xml").getroot().find("assembly")
            self.assertEqual("6", merged.get("total"))
            self.assertEqual("6", merged.get("passed"))

    def test_sharded_full_runs_unit_ui_shards_plus_serial_integration_and_merges(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in ("MacExplorer.Tests", "MacExplorer.UnitTests"):
                executable = root / "Tests" / name / "bin/Debug/net10.0/osx-arm64" / name
                executable.parent.mkdir(parents=True)
                executable.touch()
            invocations = []

            def fake_run(command, log):
                invocations.append(command)
                report = Path(command[command.index("-xml") + 1])
                report.write_text(
                    '<assemblies><assembly total="2" passed="2" failed="0" skipped="0" not-run="0" errors="0" time="0.5">'
                    '<collection><test name="A" time="0.25"/><test name="B" time="0.25"/></collection></assembly></assemblies>'
                )
                log.write_text("ok\n")
                return 0

            weights = {f"MacExplorer.Tests.C{i}": i + 1 for i in range(4)}
            with (patch.object(runner, "ROOT", root),
                  patch.object(runner, "run", fake_run),
                  patch.object(runner, "discover_class_weights", return_value=weights),
                  patch.object(runner.subprocess, "call", return_value=0),
                  patch.object(runner.tempfile, "mkdtemp", return_value=temporary),
                  patch.object(sys, "argv", ["run-tests.py", "full", "--no-build", "--jobs", "2"]),
                  contextlib.redirect_stdout(io.StringIO())):
                code = runner.main()

            self.assertEqual(0, code)
            # 1 MacExplorer.UnitTests + 2 Unit/UI shards + 1 serial Integration/Stress pass = 4 invocations.
            self.assertEqual(4, len(invocations))
            serial_cmd = [cmd for cmd in invocations if any("Kind!=Unit" in a for a in cmd)]
            self.assertEqual(1, len(serial_cmd))
            merged = ET.parse(root / "MacExplorer.Tests.xml").getroot().find("assembly")
            self.assertEqual("6", merged.get("total"))
            self.assertEqual("6", merged.get("passed"))


if __name__ == "__main__":
    unittest.main()
