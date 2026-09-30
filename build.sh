#!/usr/bin/env bash
# Builds the PowerPoint add-in on macOS (or Linux) and assembles dist/PPTimer-win/,
# the folder you copy to the Windows machine and run install.cmd from.
#
#   ./build.sh            build the add-in
#   ./build.sh dev        run the dev server (same API, no PowerPoint) on http://localhost:9595/
set -euo pipefail
cd "$(dirname "$0")"

DOTNET="${DOTNET:-dotnet}"
if ! command -v "$DOTNET" >/dev/null 2>&1; then
  echo "dotnet not found. Install the .NET 8 SDK:  brew install --cask dotnet-sdk" >&2
  exit 1
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

if [[ "${1:-}" == "dev" ]]; then
  exec "$DOTNET" run --project addin/DevServer -- "${@:2}"
fi

"$DOTNET" build addin/PPTimer/PPTimer.csproj -c Release -v quiet -nologo

OUT=dist/PPTimer-win
rm -rf "$OUT"
mkdir -p "$OUT"
cp addin/PPTimer/bin/Release/net48/PPTimer.dll addin/PPTimer/bin/Release/net48/PPTimer.pdb "$OUT/"
cp addin/scripts/*.ps1 addin/scripts/*.cmd "$OUT/"

(cd dist && rm -f PPTimer-win.zip && zip -qr PPTimer-win.zip PPTimer-win)
echo "Built $OUT and dist/PPTimer-win.zip"
