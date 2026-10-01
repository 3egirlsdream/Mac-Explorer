#!/bin/bash
set -euo pipefail
source_file=$1; output=$2; target=$3
shift 3
task_swift_dir=$(mktemp -d)
trap 'rm -rf "$task_swift_dir"' EXIT
cat "$(dirname "$source_file")/HelperFileAccess.swift" "$source_file" > "$task_swift_dir/main.swift"
xcrun swiftc -O -target "$target" "$task_swift_dir/main.swift" "$@" -o "$output"
