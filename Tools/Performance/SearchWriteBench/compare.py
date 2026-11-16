#!/usr/bin/env python3
"""Compare independent performance, row-audit and exclusion runs without mixing their I/O."""
import argparse
import csv
import json
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
for name in ("baseline", "improved", "audit_baseline", "audit_improved", "included", "excluded", "output"):
    parser.add_argument(name, type=Path)
args = parser.parse_args()


def load(path, audit=False):
    data = json.loads((path / "report.json").read_text())
    if bool(data.get("rowAudit")) != audit:
        raise ValueError(f"Wrong measurement mode: {path}")
    return data, {sample["name"]: sample for sample in data["samples"]}


def delta(sample, before="before", after="after"):
    return {key: sample[after][key] - value for key, value in sample[before].items()
            if isinstance(value, (int, float))}


def reduction(before, after):
    return None if before == 0 else 100 * (1 - after / before)


baseline, old = load(args.baseline)
improved, new = load(args.improved)
_, old_audit = load(args.audit_baseline, True)
_, new_audit = load(args.audit_improved, True)
included, keep = load(args.included)
excluded, skip = load(args.excluded)
if included["excludedChurn"] or not excluded["excludedChurn"]:
    raise ValueError("Exclusion comparison requires one included and one excluded run.")
if old.keys() != new.keys():
    raise ValueError("Performance runs must contain the same completed phases, including idle.")
rows = []
for name, sample in old.items():
    other = new[name]
    changes_old = delta(old_audit[name], "rowsBefore", "rowsAfter") if name in old_audit else None
    changes_new = delta(new_audit[name], "rowsBefore", "rowsAfter") if name in new_audit else None
    rows.append({"phase": name, "baseline_disk_bytes": sample["diskWriteBytes"], "improved_disk_bytes": other["diskWriteBytes"],
                 "write_reduction_percent": reduction(sample["diskWriteBytes"], other["diskWriteBytes"]),
                 "baseline_seconds": sample["seconds"], "improved_seconds": other["seconds"],
                 "baseline_cpu_seconds": sample["cpuSeconds"], "improved_cpu_seconds": other["cpuSeconds"],
                 "baseline_peak_resident_bytes": sample["peakResidentBytes"], "improved_peak_resident_bytes": other["peakResidentBytes"],
                 "baseline_entry_changes": changes_old, "improved_entry_changes": changes_new})
unchanged = delta(new_audit["unchanged-50"], "rowsBefore", "rowsAfter")
idle = delta(new["idle"])
checks = {
    "unchanged_entries_not_written": all(value == 0 for value in unchanged.values()),
    "unchanged_write_reduction_at_least_90_percent": reduction(old["unchanged-50"]["diskWriteBytes"], new["unchanged-50"]["diskWriteBytes"]) >= 90,
    "no_full_scan_for_thousand_events": delta(new["burst-1000"])["FullScans"] == 0,
    "baseline_idle_at_least_30_minutes": old["idle"]["seconds"] >= 1800,
    "improved_idle_at_least_30_minutes": new["idle"]["seconds"] >= 1800,
    "idle_index_and_diagnostics_stable": all(idle[key] == 0 for key in
        ("DirectoryScans", "FullScans", "InsertedEntries", "UpdatedEntries", "DeletedEntries", "DiagnosticsWrites")),
}
summary = {"checks": checks, "phases": rows,
           "exclusion": {"included_write_bytes": keep["churn"]["diskWriteBytes"], "excluded_write_bytes": skip["churn"]["diskWriteBytes"],
                         "reduction_percent": reduction(keep["churn"]["diskWriteBytes"], skip["churn"]["diskWriteBytes"]),
                         "included_statistics": delta(keep["churn"]), "excluded_statistics": delta(skip["churn"])},
           "limits": ["Disk bytes are per-process macOS I/O, not SSD NAND writes.",
                      "Row audit uses additional triggers and is excluded from disk-I/O performance comparisons.",
                      "Memory is sampled process high-water RSS; it is not live managed heap size.",
                      "Synthetic notifications and an empty pinyin provider isolate index-write behavior; native events and real pinyin are tested separately.",
                      "Synthetic exclusion savings do not estimate real system-folder MB/day."]}
args.output.mkdir(parents=True, exist_ok=True)
(args.output / "comparison.json").write_text(json.dumps(summary, indent=2) + "\n")
with (args.output / "comparison.csv").open("w", newline="") as output:
    writer = csv.DictWriter(output, fieldnames=rows[0].keys())
    writer.writeheader()
    writer.writerows(rows)
print(json.dumps(checks, indent=2))
raise SystemExit(0 if all(checks.values()) else 1)
