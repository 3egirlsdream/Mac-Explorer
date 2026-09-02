#!/bin/bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_CONFIGURATION="${CONFIGURATION:-Debug}"

if [[ -n "${DOTNET_EXE:-}" && -x "${DOTNET_EXE}" ]]; then
  DOTNET_COMMAND="${DOTNET_EXE}"
elif [[ -n "${DOTNET_ROOT:-}" && -x "${DOTNET_ROOT}/dotnet" ]]; then
  DOTNET_COMMAND="${DOTNET_ROOT}/dotnet"
elif command -v dotnet >/dev/null 2>&1; then
  DOTNET_COMMAND="$(command -v dotnet)"
elif [[ -x "${PROJECT_ROOT}/../dotnet/dotnet" ]]; then
  # Used by the isolated verification environment; normal users can install
  # .NET 10.0.201 or set DOTNET_ROOT/DOTNET_EXE in the Xcode scheme.
  DOTNET_COMMAND="${PROJECT_ROOT}/../dotnet/dotnet"
else
  LOCAL_DOTNET_ROOT="${PROJECT_ROOT}/.dotnet"
  DOTNET_COMMAND="${LOCAL_DOTNET_ROOT}/dotnet"
  echo "note: .NET SDK was not found; installing the project-local .NET 10.0.201 SDK..."
  mkdir -p "${LOCAL_DOTNET_ROOT}"
  INSTALL_SCRIPT="${PROJECT_ROOT}/obj/dotnet-install.sh"
  mkdir -p "$(dirname "${INSTALL_SCRIPT}")"
  /usr/bin/curl --fail --location --retry 3 https://dot.net/v1/dotnet-install.sh --output "${INSTALL_SCRIPT}"
  /bin/bash "${INSTALL_SCRIPT}" --version 10.0.201 --install-dir "${LOCAL_DOTNET_ROOT}"
fi

export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"
export CLANG_MODULE_CACHE_PATH="${CLANG_MODULE_CACHE_PATH:-${PROJECT_ROOT}/obj/xcode-module-cache}"
export AVALONIA_TELEMETRY_OPTOUT=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p "${CLANG_MODULE_CACHE_PATH}"

cd "${PROJECT_ROOT}"
"${DOTNET_COMMAND}" restore MacExplorer.csproj
# Xcode's product is the runnable .app bundle.  Skip the optional Release DMG
# step here: hdiutil is a distribution step and can fail in CI, a VM, or when
# no writable disk image device is available, even though the .app built fine.
"${DOTNET_COMMAND}" build MacExplorer.csproj -c "${BUILD_CONFIGURATION}" --no-restore -p:SkipMacOSReleaseDMG=true

APP_PATH="${PROJECT_ROOT}/bin/${BUILD_CONFIGURATION}/net10.0/osx-arm64/Mac Explorer.app"
if [[ ! -d "${APP_PATH}" ]]; then
  echo "error: Expected application bundle was not created: ${APP_PATH}"
  exit 1
fi

echo "Xcode build complete: ${APP_PATH}"
echo "Open the app from Finder or use Product > Run in Xcode."
