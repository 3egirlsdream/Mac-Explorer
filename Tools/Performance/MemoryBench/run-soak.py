#!/usr/bin/env python3
"""Run the isolated native soak and save numeric native/system memory summaries."""
import argparse
import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import subprocess
import time


def utc():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def size(value):
    match = re.fullmatch(r"([\d.]+)([KMGT])", value)
    return round(float(match[1]) * 1024 ** ("KMGT".index(match[2]) + 1)) if match else None


def native_snapshot(pid, progress):
    result = {"utc": utc(), "phase": progress.get("phase"), "block": progress.get("block"),
              "sourceSequence": progress.get("sequence"), "regions": [], "mallocZones": []}
    try:
        vmmap = subprocess.run(["/usr/bin/vmmap", "-summary", str(pid)], capture_output=True, text=True, timeout=30)
        result["vmmapExitCode"] = vmmap.returncode
        section = ""
        for line in vmmap.stdout.splitlines():
            if line.startswith("REGION TYPE"):
                section = "regions"
            elif line.startswith("MALLOC ZONE"):
                section = "malloc"
            footprint = re.fullmatch(r"Physical footprint( \(peak\))?:\s+([\d.]+[KMGT])\s*", line)
            if footprint:
                result["peakFootprintBytes" if footprint[1] else "footprintBytes"] = size(footprint[2])
            if section == "regions":
                row = re.fullmatch(r"\s*([A-Za-z0-9_ ().+-]+?)\s+((?:[\d.]+[KMGT]\s+){7})(\d+)\s*(?:see MALLOC ZONE table below)?\s*", line)
                if row:
                    values = row[2].split()
                    result["regions"].append(dict(type=row[1].strip(), regionCount=int(row[3]), **dict(zip(
                        ("virtualBytes", "residentBytes", "dirtyBytes", "swappedBytes", "volatileBytes", "nonvolatileBytes", "emptyBytes"),
                        map(size, values)))))
            elif section == "malloc":
                row = re.fullmatch(r"\s*([A-Za-z0-9_ .+-]+?)\s+((?:[\d.]+[KMGT]\s+){4})(\d+)\s+([\d.]+[KMGT])\s+([\d.]+[KMGT])\s+(\d+)%\s+(\d+)\s*", line)
                if row:
                    result["mallocZones"].append(dict(type=re.sub(r"_0x[0-9a-fA-F]+$", "", row[1].strip()),
                        allocationCount=int(row[3]), allocatedBytes=size(row[4]), fragmentationBytes=size(row[5]),
                        fragmentationPercent=int(row[6]), regionCount=int(row[7]), **dict(zip(
                            ("virtualBytes", "residentBytes", "dirtyBytes", "swappedBytes"), map(size, row[2].split())))))
        stats = subprocess.run(["/usr/bin/vm_stat"], capture_output=True, text=True, timeout=5)
        result["systemVmPages"] = {key.strip(): int(value) for key, value in re.findall(r"^([A-Za-z -]+):\s+(\d+)\.", stats.stdout, re.M)}
        page_size = re.search(r"page size of (\d+) bytes", stats.stdout)
        if page_size:
            result["systemPageSizeBytes"] = int(page_size[1])
        heap = subprocess.run(["/usr/bin/heap", "--noContent", "-q", "-s", str(pid)],
                              capture_output=True, text=True, timeout=30)
        result["nativeHeapExitCode"] = heap.returncode
        classes = {"WindowImpl", "AvnView", "AvnTextInputMethod", "AvnMetalRenderTarget", "IOSurfaceRenderTarget",
                   "CAMetalLayer", "AvnAutomationNode", "TFPInMemoryFont"}
        result["nativeHeapClasses"] = {}
        for line in heap.stdout.splitlines():
            row = re.fullmatch(r"\s*(\d+)\s+(\d+)\s+[\d.]+\s+(\w+)\s+(?:C\+\+|ObjC)\s+\S+\s*", line)
            if row and row[3] in classes:
                result["nativeHeapClasses"][row[3]] = {"count": int(row[1]), "bytes": int(row[2])}
        if os.environ.get("MACEXPLORER_MEMORY_RETENTION_PROBE") == "1":
            detail = subprocess.run(["/usr/bin/vmmap", "-w", str(pid)], capture_output=True, text=True, timeout=30)
            groups = {}
            for line in detail.stdout.splitlines():
                if not line.startswith("Untagged"):
                    continue
                row = re.search(r"\[\s*(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\]\s+(\S+)\s+(SM=\S+)", line)
                if not row:
                    continue
                key = row[5] + " " + row[6]
                group = groups.setdefault(key, {"count": 0, "virtualBytes": 0, "residentBytes": 0, "dirtyBytes": 0, "swappedBytes": 0})
                group["count"] += 1
                for field, value in zip(("virtualBytes", "residentBytes", "dirtyBytes", "swappedBytes"), row.groups()[:4]):
                    group[field] += size(value) or 0
            result["untaggedByProtection"] = groups
    except (OSError, subprocess.TimeoutExpired) as error:
        result["error"] = type(error).__name__
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--minutes", type=float, default=120)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--lifetime-kind", choices=("tab", "window", "settings"))
    parser.add_argument("--software-rendering", action="store_true")
    args = parser.parse_args()
    if not 1 <= args.minutes <= 720:
        parser.error("minutes must be between 1 and 720")
    project = pathlib.Path(__file__).resolve().parents[3]
    source = pathlib.Path(__file__).resolve().parent / "bin/Debug/net10.0/osx-arm64/MemoryBench.app"
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    bundle = output / "MemoryBench.app"
    subprocess.run(["/usr/bin/ditto", str(source), str(bundle)], check=True)
    executable = bundle / "Contents/MacOS/MemoryBench"
    config = {"createdUtc": utc(), "requestedMinutes": args.minutes,
              "forcedGc": "controlled-retention-probe" if os.environ.get("MACEXPLORER_MEMORY_RETENTION_PROBE") == "1" else "final-only",
              "lifetimeKind": args.lifetime_kind,
              "dataSource": "real" if os.environ.get("MACEXPLORER_MEMORY_REAL_ROOT") else "generated",
              "previewKind": "pdf" if os.environ.get("MACEXPLORER_MEMORY_PREVIEW_KIND") == "pdf" else "mixed",
              "retentionProbe": os.environ.get("MACEXPLORER_MEMORY_RETENTION_PROBE") == "1",
              "fontTablesPoh": True, "fontTableAllocator": "production",
              "renderer": "software" if args.software_rendering else "default",
              "sha256": {name: hashlib.sha256((executable.parent / name).read_bytes()).hexdigest()
                         for name in ("MemoryBench.dll", "MacExplorer.dll", "MacExplorer.TextShaping.dll")}}
    (output / "run-config.json").write_text(json.dumps(config, indent=2))
    command = ["bash", str(project / "Tools/Testing/run-isolated.sh"), str(executable), str(output),
               "--soak-minutes=" + str(args.minutes)]
    if args.lifetime_kind:
        command.append("--lifetime-kind=" + args.lifetime_kind)
    if args.software_rendering:
        command.append("--software-rendering")
    child = subprocess.Popen(command, cwd=project, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    caffeinate = subprocess.Popen(["/usr/bin/caffeinate", "-i", "-w", str(child.pid)])
    print(json.dumps({"state": "started", "pid": child.pid, "minutes": args.minutes}), flush=True)
    last_stage = None
    last_heartbeat = time.monotonic()
    last_sequence = -1
    captured_peak = 0
    growth_captures = 0
    started = time.monotonic()
    monitor_error = None
    try:
        while child.poll() is None:
            progress_file = output / "progress.json"
            progress = json.loads(progress_file.read_text()) if progress_file.exists() else {}
            if progress.get("sequence", -1) != last_sequence:
                last_sequence = progress.get("sequence", -1)
                last_heartbeat = time.monotonic()
            phase = progress.get("phase", "")
            stage = (progress.get("block"), phase if phase.startswith("retention.") else phase.endswith("idle"))
            growth_capture = progress.get("footprintBytes", 0) > captured_peak + 128 * 1024 * 1024 and growth_captures < 8
            if progress and (stage != last_stage or growth_capture):
                snapshot = native_snapshot(child.pid, progress)
                snapshot["trigger"] = "phase" if stage != last_stage else "growth"
                with (output / "native-samples.jsonl").open("a") as log:
                    log.write(json.dumps(snapshot) + "\n")
                captured_peak = max(captured_peak, progress.get("footprintBytes", 0))
                if stage == last_stage:
                    growth_captures += 1
                last_stage = stage
                print(json.dumps({"state": "phase", "phase": progress.get("phase"), "block": progress.get("block"),
                                  "elapsedSeconds": progress.get("elapsedSeconds")}), flush=True)
            if time.monotonic() - last_heartbeat > 180:
                monitor_error = "ui-heartbeat-timeout"
                break
            if time.monotonic() - started > args.minutes * 60 * 1.4 + 600:
                monitor_error = "overall-timeout"
                break
            time.sleep(5)
        if monitor_error:
            child.terminate()
            try:
                child.wait(timeout=15)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait(timeout=10)
        verification_file = output / "verification.json"
        completed = verification_file.exists() and json.loads(verification_file.read_text()).get("completed") is True
        if not completed and monitor_error is None:
            monitor_error = "test-failed" if (output / "failure.json").exists() else "app-exited-before-verification"
        result = {"finishedUtc": utc(), "exitCode": child.wait(), "completed": completed, "monitorError": monitor_error,
                  "elapsedSeconds": time.monotonic() - started}
        (output / "runner-result.json").write_text(json.dumps(result, indent=2))
        print(json.dumps({"state": "finished", **result}), flush=True)
        return result["exitCode"] or (1 if monitor_error else 0)
    finally:
        if child.poll() is None:
            child.terminate()
            child.wait(timeout=15)
        caffeinate.terminate()
        caffeinate.wait(timeout=5)


if __name__ == "__main__":
    raise SystemExit(main())
