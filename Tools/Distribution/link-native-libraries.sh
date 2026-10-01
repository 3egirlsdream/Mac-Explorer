#!/bin/bash
set -euo pipefail
frameworks=$1; managed=$2
for library in "$frameworks"/*.dylib; do
    ln -s "../../Frameworks/$(basename "$library")" "$managed/$(basename "$library")"
done
