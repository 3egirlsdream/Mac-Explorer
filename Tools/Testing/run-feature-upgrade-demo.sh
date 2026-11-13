#!/bin/bash
set -euo pipefail

project_root="$(cd "$(dirname "$0")/../.." && pwd)"
qa_python="${MACEXPLORER_SFTP_TEST_PYTHON:-$project_root/outputs/feature-upgrade-20261112/qa-venv/bin/python3}"
if ! "$qa_python" -c 'import paramiko' >/dev/null 2>&1; then
    echo "先创建独立 QA 环境：python3 -m venv outputs/feature-upgrade-20261112/qa-venv" >&2
    echo "再安装：outputs/feature-upgrade-20261112/qa-venv/bin/pip install paramiko==5.1.0" >&2
    exit 2
fi
cd "$project_root"
if [ "${1:-}" = "--sftp-only" ]; then
    exec bash Tools/Testing/run-isolated.sh bash -c '
        mkdir -p "$MACEXPLORER_TEST_ROOT/sftp-fixture/remote-files"
        printf "server initial\n" > "$MACEXPLORER_TEST_ROOT/sftp-fixture/remote-files/配置说明.txt"
        printf "SFTP 文件目录：%s/sftp-fixture/remote-files\n下一行显示端口。账号 fixture，密码 fixture-secret；Ctrl+C 停止。\n" "$MACEXPLORER_TEST_ROOT"
        exec "$1" "$2" "$MACEXPLORER_TEST_ROOT/sftp-fixture" manual-feature 0
    ' _ "$qa_python" "$project_root/Tests/MacExplorer.Tests/TestData/sftp-loopback.py"
fi
dotnet build -p:DistributionChannel=Website -p:SkipMacOSReleaseDMG=true --no-restore -v:q
/usr/bin/caffeinate -u -t 1
exec /usr/bin/caffeinate -d -t 180 bash Tools/Testing/run-isolated.sh env \
    MACEXPLORER_FEATURE_UPGRADE_SMOKE=1 \
    MACEXPLORER_FEATURE_UPGRADE_KEEP_OPEN=1 \
    MACEXPLORER_FEATURE_SOURCE="$project_root" \
    MACEXPLORER_SFTP_TEST_PYTHON="$qa_python" \
    "bin/Debug/net10.0/osx-arm64/Mac Explorer.app/Contents/MacOS/MacExplorer"
