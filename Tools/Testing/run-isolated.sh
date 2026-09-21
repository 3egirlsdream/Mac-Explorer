#!/bin/bash
set -euo pipefail

if [ "$#" -eq 0 ]; then
    echo "Usage: bash Tools/Testing/run-isolated.sh <executable> [arguments...]" >&2
    exit 2
fi

# Fresh state on each invocation; keep it afterward for logs/database inspection.
export MACEXPLORER_TEST_ROOT
MACEXPLORER_TEST_ROOT="$(mktemp -d /private/tmp/fkfinder-test.XXXXXX)"
echo "FKFinder test profile: $MACEXPLORER_TEST_ROOT" >&2
exec "$@"
