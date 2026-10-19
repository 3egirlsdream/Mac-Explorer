#!/usr/bin/env python3
"""Build once, run an explicit test profile in an isolated application directory."""
import argparse
import json
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "Tests/MacExplorer.Tests/MacExplorer.Tests.csproj"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profile", nargs="?", default="fast",
                        choices=["fast", "full", "unit", "ui", "integration", "stress", "manual"])
    parser.add_argument("--area", action="append", default=[], help="Feature area; may be repeated")
    parser.add_argument("--no-build", action="store_true", help="Run the already built assemblies")
    parser.add_argument("--configuration", default="Debug", choices=["Debug", "Release"])
    parser.add_argument("--budget", type=float, help="Fail if build + execution exceeds this many seconds")
    args = parser.parse_args()
    started = time.monotonic()
    results = Path(tempfile.mkdtemp(prefix="fkfinder-tests-"))
    print(f"Test profile: {args.profile}; results: {results}", flush=True)
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
        report = results / (project + ".xml")
        command = ["bash", str(ROOT / "Tools/Testing/run-isolated.sh"), str(executable),
                   "-noColor", "-xml", str(report), "-filter", "/[" + expression + "]", "-explicit", explicit]
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
        if int(assembly.get("total", "0")) or int(assembly.get("errors", "0")):
            code = code or result
    test_time = time.monotonic() - test_started
    elapsed = time.monotonic() - started
    summary = {"profile": args.profile, "areas": args.area, **counts,
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
