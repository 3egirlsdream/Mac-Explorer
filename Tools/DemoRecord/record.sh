#!/bin/bash
set -euo pipefail
REPO_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$REPO_DIR"
exec bash Tools/Testing/run-isolated.sh python3 Tools/DemoRecord/run.py "$@"
