#!/usr/bin/env python3
"""Build once, run an explicit test profile in an isolated application directory."""
import argparse
import concurrent.futures
import json
import os
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "Tests/MacExplorer.Tests/MacExplorer.Tests.csproj"
SHARDABLE_PROFILES = {"fast", "unit", "ui", "integration", "full"}
MIN_CLASSES_FOR_SHARDING = 4
DEFAULT_TEST_WEIGHT = 100
RELEASE_FULL_TEST_WEIGHT = 26  # Calibrated against the wall-clock distribution of the CI shards.

# Median summed test times (centiseconds) from November 23–25 Release full XML reports.
SLOW_CLASS_WEIGHTS = {
    "MacExplorer.Tests.PdfAnalysisTests": 7791,
    "MacExplorer.Tests.LocalSendServiceTests": 6979,
    "MacExplorer.Tests.GitOperationsTests": 2683,
    "MacExplorer.Tests.BackgroundThemeTests": 2508,
    "MacExplorer.Tests.FileConversionTests": 1851,
    "MacExplorer.Tests.PluginSystemTests": 1849,
    "MacExplorer.Tests.TabWorkspaceCacheViewModelTests": 1604,
    "MacExplorer.Tests.SearchIndexWriteTests": 1464,
    "MacExplorer.Tests.TabGroupsUiTests": 981,
    "MacExplorer.Tests.PrivacySecurityFollowupTests": 936,
    "MacExplorer.Tests.GroupPresetTests": 909,
    "MacExplorer.Tests.BatchRenameWorkbenchTests": 683,
    "MacExplorer.Tests.FolderArtworkTests": 547,
    "MacExplorer.Tests.BreadcrumbBarViewModelTests": 534,
    "MacExplorer.Tests.TaskProgressRingTests": 508,
    "MacExplorer.Tests.FolderTabRestoreTests": 505,
}


def default_jobs(profile):
    if profile not in SHARDABLE_PROFILES:
        return 1
    return max(1, min(4, os.cpu_count() or 1))


def discover_class_weights(executable, expression, explicit, per_test_weight=DEFAULT_TEST_WEIGHT):
    command = ["bash", str(ROOT / "Tools/Testing/run-isolated.sh"), str(executable),
               "-list", "full/json", "-filter", "/[" + expression + "]", "-explicit", explicit]
    try:
        result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
    except OSError:
        return None
    if result.returncode != 0:
        return None
    for line in result.stdout.splitlines():
        stripped = line.lstrip()
        if stripped.startswith("["):
            items = json.loads(stripped)
            counts = Counter(item["Class"] for item in items if item.get("Class"))
            return {cls: max(count * per_test_weight, SLOW_CLASS_WEIGHTS.get(cls, 0))
                    for cls, count in counts.items()}
    return {}


def partition_classes(class_weights, shard_count):
    if shard_count <= 1 or len(class_weights) < MIN_CLASSES_FOR_SHARDING:
        return [sorted(class_weights)] if class_weights else []
    worker_count = min(shard_count, len(class_weights))
    shards = [[] for _ in range(worker_count)]
    loads = [0] * worker_count
    for cls, weight in sorted(class_weights.items(), key=lambda item: (-item[1], item[0])):
        index = min(range(worker_count), key=lambda i: (loads[i], len(shards[i]), i))
        shards[index].append(cls)
        loads[index] += weight
    return [shard for shard in shards if shard]


def build_class_filter(assembly_name, full_class_name, expression):
    if "." in full_class_name:
        namespace, class_name = full_class_name.rsplit(".", 1)
    else:
        namespace, class_name = "", full_class_name
    return f"/{assembly_name}/{namespace}/{class_name}/[{expression}]"


def merge_assembly_xml(shard_reports, output_report):
    totals = dict.fromkeys(["total", "passed", "failed", "skipped", "not-run", "errors"], 0)
    total_time = 0.0
    merged_assembly = None
    merged_errors = None
    for report in shard_reports:
        root = ET.parse(report).getroot()
        assembly = root.find("assembly")
        if assembly is None:
            continue
        if merged_assembly is None:
            merged_assembly = ET.Element("assembly", dict(assembly.attrib))
        for key in totals:
            totals[key] += int(assembly.get(key, "0"))
        total_time += float(assembly.get("time", "0"))
        for child in assembly:
            if child.tag == "errors":
                if merged_errors is None:
                    merged_errors = ET.SubElement(merged_assembly, "errors")
                for error in child:
                    merged_errors.append(error)
            else:
                merged_assembly.append(child)
    if merged_assembly is None:
        merged_assembly = ET.Element("assembly")
    for key, value in totals.items():
        merged_assembly.set(key, str(value))
    merged_assembly.set("time", f"{total_time:.3f}")
    wrapper = ET.Element("assemblies")
    wrapper.append(merged_assembly)
    ET.ElementTree(wrapper).write(output_report, encoding="utf-8", xml_declaration=True)
    return merged_assembly


def run_sharded_project(project, executable, expression, explicit, verbose, jobs, results,
                        serial_expression=None, per_test_weight=DEFAULT_TEST_WEIGHT):
    weights = discover_class_weights(executable, expression, explicit, per_test_weight)
    if weights is None:
        return None
    serial_weights = None
    if serial_expression:
        serial_weights = discover_class_weights(executable, serial_expression, explicit)
        if serial_weights is None:
            return None
    has_serial = bool(serial_weights)
    shards = partition_classes(weights, jobs) if weights else []
    if not has_serial and len(shards) <= 1:
        return None

    def run_worker(label, filters):
        name = f"{project}.{label}"
        report = results / f"{name}.xml"
        log = results / f"{name}.log"
        command = [
            "bash", str(ROOT / "Tools/Testing/run-isolated.sh"), str(executable),
            "-noColor", "-xml", str(report), "-explicit", explicit, "-parallel", "none",
        ]
        for test_filter in filters:
            command += ["-filter", test_filter]
        if verbose:
            command.append("-verbose")
        started = time.monotonic()
        print(f"{name} started ({len(filters)} filters)", flush=True)
        exit_code = run(command, log)
        print(f"{name} finished in {time.monotonic() - started:.2f}s (exit {exit_code})", flush=True)
        return exit_code, report, log

    shard_results = [None] * len(shards)
    if shards:
        with concurrent.futures.ThreadPoolExecutor(max_workers=len(shards)) as pool:
            futures = {
                pool.submit(run_worker, f"shard-{i + 1}",
                            [build_class_filter(project, cls, expression) for cls in shard]): i
                for i, shard in enumerate(shards)
            }
            for future in concurrent.futures.as_completed(futures):
                shard_results[futures[future]] = future.result()

    serial_result = run_worker("serial", ["/[" + serial_expression + "]"]) if has_serial else None

    merged_log = results / f"{project}.log"
    with merged_log.open("w") as out_log:
        for index, (_, _, log) in enumerate(shard_results, 1):
            out_log.write(f"=== Shard {index}/{len(shards)} ===\n")
            if log.exists():
                out_log.write(log.read_text(errors="replace"))
            out_log.write("\n")
        if serial_result is not None:
            out_log.write("=== Serial (Hardware/Native) ===\n")
            if serial_result[2].exists():
                out_log.write(serial_result[2].read_text(errors="replace"))
            out_log.write("\n")

    code = 0
    shard_reports = []
    all_runs = list(shard_results) + ([serial_result] if serial_result is not None else [])
    for exit_code, report, log in all_runs:
        code = code or exit_code
        if not report.exists():
            print(f"Missing test report {report.name}; see {log.name}.", flush=True)
            code = code or 1
        else:
            shard_reports.append(report)

    merged_report = results / f"{project}.xml"
    assembly = merge_assembly_xml(shard_reports, merged_report)
    return code, assembly


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profile", nargs="?", default="fast",
                        choices=["fast", "full", "unit", "ui", "integration", "stress", "manual"])
    parser.add_argument("--area", action="append", default=[], help="Feature area; may be repeated")
    parser.add_argument("--no-build", action="store_true", help="Run the already built assemblies")
    parser.add_argument("--configuration", default="Debug", choices=["Debug", "Release"])
    parser.add_argument("--budget", type=float, help="Fail if build + execution exceeds this many seconds")
    parser.add_argument("--jobs", "-j", type=int, default=None,
                        help="Isolated worker processes for sharding non-serial tests (default: 4 for fast/unit/ui/integration/full, 1 otherwise)")
    args = parser.parse_args()
    jobs = default_jobs(args.profile) if args.jobs is None else args.jobs
    if jobs < 1:
        parser.error("--jobs must be at least 1")
    audit_code = subprocess.call([sys.executable, str(ROOT / "Tools/Testing/check-buttons.py")])
    if audit_code:
        return audit_code
    started = time.monotonic()
    results = Path(tempfile.mkdtemp(prefix="fkfinder-tests-"))
    print(f"Test profile: {args.profile} (jobs={jobs}); results: {results}", flush=True)
    build_time = 0.0
    if not args.no_build:
        command = ["dotnet", "build", str(PROJECT), "-c", args.configuration,
                   "-p:SkipMacOSReleaseDMG=true", "--nologo", "-v:q"]
        if (PROJECT.parent / "obj/project.assets.json").exists():
            command.append("--no-restore")
        if args.profile in ("fast", "unit", "ui"):
            command += ["-p:SkipMacOSBundle=true", "-p:SkipMacOSNativeHelpers=true", "-p:RunAnalyzersDuringBuild=false"]
        build_started = time.monotonic()
        code = run(command, results / "build.log")
        build_time = time.monotonic() - build_started
        if code:
            return code
    if args.profile == "fast":
        expression = "(Kind=Unit)|(Smoke=UI)"
        explicit = "off"
    elif args.profile == "full":
        expression = "Kind!=Manual"
        explicit = "on"
    else:
        expression = "Kind=" + {"ui": "UI"}.get(args.profile, args.profile.capitalize())
        explicit = "on"
    for area in args.area:
        if any(char in area for char in "[]()&|=/"):
            parser.error("Area names must not contain query operators")
    if args.area:
        area_expression = "|".join("(Area=" + area + ")" for area in args.area)
        expression = "(" + expression + ")&(" + area_expression + ")"
    projects = ["MacExplorer.Tests"]
    if args.profile in ("fast", "full", "unit"):
        projects.insert(0, "MacExplorer.UnitTests")
    counts = dict.fromkeys(["total", "passed", "failed", "skipped", "not-run"], 0)
    tests = []
    code = 0
    test_started = time.monotonic()
    for project in projects:
        executable = ROOT / "Tests" / project / "bin" / args.configuration / "net10.0/osx-arm64" / project
        if not executable.exists():
            parser.error(f"{project} is missing; run without --no-build first")
        sharded = None
        if project != "MacExplorer.UnitTests" and jobs > 1:
            if args.profile in ("full", "integration"):
                shard_expr = "(" + expression + ")&(Serial!=true)"
                serial_expr = "(" + expression + ")&(Serial=true)"
                per_test_weight = (RELEASE_FULL_TEST_WEIGHT if args.profile == "full" and not args.area
                                   else DEFAULT_TEST_WEIGHT)
                sharded = run_sharded_project(
                    project, executable, shard_expr, explicit,
                    verbose=(args.profile == "full"), jobs=jobs, results=results,
                    serial_expression=serial_expr, per_test_weight=per_test_weight)
            else:
                sharded = run_sharded_project(
                    project, executable, expression, explicit,
                    verbose=False, jobs=jobs, results=results)
        if sharded is not None:
            result, assembly = sharded
        else:
            report = results / (project + ".xml")
            command = ["bash", str(ROOT / "Tools/Testing/run-isolated.sh"), str(executable),
                       "-noColor", "-xml", str(report), "-filter", "/[" + expression + "]", "-explicit", explicit]
            if args.profile == "full":
                command.append("-verbose")  # Preserve the active test name if the CI runner times out.
            if project == "MacExplorer.UnitTests":
                command += ["-parallel", "collections", "-maxThreads", "4"]
            else:
                command += ["-parallel", "none"]
            result = run(command, results / (project + ".log"))
            if not report.exists():
                return result or 1
            assembly = ET.parse(report).getroot().find("assembly")
        print_failures(assembly)
        tests.extend(assembly.iter("test"))
        for key in counts:
            counts[key] += int(assembly.get(key, "0"))
        code = code or result
        if int(assembly.get("errors", "0")) or int(assembly.get("failed", "0")):
            code = code or 1
        if args.profile == "full" and not args.area and int(assembly.get("passed", "0")) == 0:
            print(f"{project}: full profile requires at least one passing test from each assembly.")
            code = code or 1
    test_time = time.monotonic() - test_started
    elapsed = time.monotonic() - started
    summary = {"profile": args.profile, "areas": args.area, "jobs": jobs, **counts,
               "build_seconds": round(build_time, 3), "test_seconds": round(test_time, 3),
               "flow_seconds": round(elapsed, 3)}
    (results / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(f"Passed {counts['passed']}/{counts['total']}; failed {counts['failed']}; "
          f"skipped {counts['skipped']}; not run {counts['not-run']}")
    print(f"Build {build_time:.2f}s + tests {test_time:.2f}s = flow {elapsed:.2f}s")
    for test in sorted(tests, key=lambda item: float(item.get("time", "0")), reverse=True)[:5]:
        print(f"  {float(test.get('time', '0')):.3f}s {test.get('name')}")
    if counts["total"] == 0 or counts["passed"] == 0:
        print("No tests passed; check the selected profile/area.")
        return code or 1
    if args.budget is not None and elapsed > args.budget:
        print(f"Flow exceeded the {args.budget:g}s budget.")
        return code or 1
    return code


def print_failures(assembly):
    for node in [*assembly.iter("test"), *assembly.findall("./errors/error")]:
        failure = node.find("failure")
        if failure is None:
            continue
        print(f"FAILED: {node.get('name') or node.get('type') or 'Assembly error'}")
        for element in ("message", "stack-trace"):
            detail = failure.findtext(element)
            if detail:
                print(detail)


def run(command, log):
    with log.open("w") as output:
        result = subprocess.run(command, cwd=ROOT, stdout=output, stderr=subprocess.STDOUT)
    if result.returncode:
        print("\n".join(log.read_text(errors="replace").splitlines()[-60:]))
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
