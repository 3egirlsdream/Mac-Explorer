#!/bin/bash
set -euo pipefail
frameworks=$1
executables=$2
# Some dependencies probe AppContext.BaseDirectory themselves. Keep one signed
# copy of each library in Frameworks and expose it to those probes by symlink.
for library in "$frameworks"/*.dylib; do
    name=$(basename "$library")
    install_name_tool -id "@rpath/$name" "$library"
    ln -s "../Frameworks/$name" "$executables/$name"
done
