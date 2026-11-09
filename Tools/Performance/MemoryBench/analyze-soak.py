#!/usr/bin/env python3
"""Summarize phase-matched memory retention; thresholds are investigation signals, not leak proofs."""
import argparse
import json
import pathlib
import time

MIB = 1024 * 1024


def mib(value):
    return round(value / MIB, 2) if value is not None else None


def metrics(record):
    return {key.replace("Bytes", "MiB"): mib(record.get(key)) for key in
            ("footprintBytes", "rssBytes", "managedBytes", "heapBytes", "fragmentedBytes", "committedBytes")}


def slope(records, key):
    if len(records) < 3 or records[-1]["elapsedSeconds"] - records[0]["elapsedSeconds"] < 60:
        return None
    times = [r["elapsedSeconds"] for r in records]
    values = [r[key] / MIB for r in records]
    x = sum(times) / len(times)
    y = sum(values) / len(values)
    return round(sum((t - x) * (v - y) for t, v in zip(times, values)) /
                 sum((t - x) ** 2 for t in times) * 3600, 2)


def analyze(output):
    records = [json.loads(line) for line in (output / "soak-samples.jsonl").read_text().splitlines()]
    report = {"samples": len(records), "firstUtc": records[0]["utc"], "lastUtc": records[-1]["utc"],
              "elapsedMinutes": round(records[-1]["elapsedSeconds"] / 60, 2), "cycles": records[-1]["cycles"],
              "baseline": metrics(records[0]), "peakFootprintMiB": mib(max(r["footprintBytes"] for r in records)),
              "phases": []}
    for block in range(1, 7):
        phase = [r for r in records if r["block"] == block and r["phase"] not in ("natural.final", "forced-gc.final")]
        if not phase:
            continue
        idle = [r for r in phase if r["phase"].endswith(".idle")]
        settled = idle[1:] if len(idle) > 2 else idle
        report["phases"].append({"block": block, "scenario": phase[0]["phase"].split(".")[0],
            "peakFootprintMiB": mib(max(r["footprintBytes"] for r in phase)),
            "end": metrics(phase[-1]), "idleMinutes": round((idle[-1]["elapsedSeconds"] - idle[0]["elapsedSeconds"]) / 60, 2) if idle else 0,
            "idleGen2Collections": idle[-1]["gen2"] - idle[0]["gen2"] if idle else 0,
            "idleFootprintMiBPerHour": slope(settled, "footprintBytes"),
            "idleManagedMiBPerHour": slope(settled, "managedBytes"), "retained": phase[-1]["retained"]})
    natural = next((r for r in records if r["phase"] == "natural.final"), None)
    forced = next((r for r in records if r["phase"] == "forced-gc.final"), None)
    report["completed"] = forced is not None and (output / "verification.json").exists()
    runner_file = output / "runner-result.json"
    if runner_file.exists():
        report["runner"] = json.loads(runner_file.read_text())
        report["completed"] = report["completed"] and report["runner"].get("exitCode") == 0 and not report["runner"].get("monitorError")
    if natural:
        report["naturalFinal"] = {**metrics(natural), "retained": natural["retained"]}
    if forced:
        report["forcedGcFinal"] = {**metrics(forced), "retained": forced["retained"]}
        report["retainedAfterForcedGc"] = sum(group["alive"] for group in forced["retained"])
        report["postGcChangeMiB"] = {key.replace("Bytes", "MiB"): mib(forced[key] - natural[key])
                                    for key in ("footprintBytes", "managedBytes", "fragmentedBytes", "committedBytes")}
    phases = {phase["block"]: phase for phase in report["phases"]}
    report["repeatedScenarioEndChangesMiB"] = [{"scenario": phases[block]["scenario"],
        **{key: round(phases[block + 3]["end"][key] - phases[block]["end"][key], 2)
           for key in ("footprintMiB", "managedMiB", "fragmentedMiB", "committedMiB")}}
        for block in (1, 2, 3) if block in phases and block + 3 in phases]
    native_file = output / "native-samples.jsonl"
    if native_file.exists():
        snapshots = [json.loads(line) for line in native_file.read_text().splitlines()]
        report["nativeSnapshots"] = len(snapshots)
        report["nativeCaptureFailures"] = sum(r.get("vmmapExitCode") != 0 or not r["regions"] for r in snapshots)
        report["malloc"] = [{"phase": r["phase"], "block": r["block"], "zones": r["mallocZones"]} for r in snapshots]
    (output / "analysis.json").write_text(json.dumps(report, indent=2) + "\n")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("--wait", action="store_true", help="Analyze when this running test writes runner-result.json")
    parser.add_argument("--timeout-minutes", type=float, default=180)
    args = parser.parse_args()
    if args.wait:
        deadline = time.monotonic() + args.timeout_minutes * 60
        while not (args.output / "runner-result.json").exists():
            if time.monotonic() >= deadline:
                raise SystemExit("Timed out waiting for the running test.")
            time.sleep(15)
    result = analyze(args.output)
    print(json.dumps({key: value for key, value in result.items() if key != "malloc"}, indent=2))
