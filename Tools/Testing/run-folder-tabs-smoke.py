#!/usr/bin/env python3
"""Run native quit/restart checks in one profile created by run-isolated.sh."""
import json
import os
from pathlib import Path
import subprocess
import sys


def main():
    root = os.environ.get("MACEXPLORER_TEST_ROOT")
    if not root:
        raise SystemExit("Launch through Tools/Testing/run-isolated.sh.")
    executable = Path(sys.argv[1]).resolve()
    for phase in ("seed", "restore-all", "restore-last", "disabled", "explicit"):
        env = dict(os.environ, MACEXPLORER_FOLDER_TABS_SMOKE=phase)
        output = Path(root) / "folder-tabs"
        output.mkdir(parents=True, exist_ok=True)
        with (output / (phase + ".log")).open("w") as log:
            result = subprocess.run([str(executable)], env=env, stdout=log, stderr=subprocess.STDOUT, timeout=60)
        report = output / (phase + ".json")
        if result.returncode or not report.exists() or not json.loads(report.read_text())["success"]:
            raise SystemExit(f"Folder tabs smoke failed: {phase}; see {output}")
        print(f"Passed native folder tabs phase: {phase}", flush=True)
    print(f"Native folder tabs reports: {output}")


if __name__ == "__main__":
    main()
