#!/usr/bin/env bash
# Builds the PowerPoint add-in on macOS (or Linux) and assembles dist/PPTimer-win/,
# the folder you copy to the Windows machine and run install.cmd from.
#
#   ./build.sh            build the add-in
#   ./build.sh dev        run the dev server (same API, no PowerPoint) on http://localhost:9595/
#   ./build.sh mac        build the Mac app -> dist/PPTimer-mac/PPTimer.app and dist/PPTimer-mac.zip
#   ./build.sh mac run    build it, then quit the running copy and start the new one
set -euo pipefail
cd "$(dirname "$0")"

MAC_VERSION=1.0.2

if [[ "${1:-}" == "mac" ]]; then
  # Universal (Apple silicon + Intel) needs full Xcode; Command Line Tools alone build this Mac's arch.
  ARCHS=(--arch arm64 --arch x86_64)
  [[ "$(xcode-select -p 2>/dev/null)" == *Xcode.app* ]] || ARCHS=()
  swift build --package-path mac -c release "${ARCHS[@]}"
  BIN="$(swift build --package-path mac -c release "${ARCHS[@]}" --show-bin-path)/PPTimer"

  OUT=dist/PPTimer-mac
  APP="$OUT/PPTimer.app"
  rm -rf "$OUT"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp "$BIN" "$APP/Contents/MacOS/PPTimer"
  sed "s/__VERSION__/$MAC_VERSION/g" mac/Info.plist > "$APP/Contents/Info.plist"
  cp addin/PPTimer/Core/remote.html addin/PPTimer/Core/display.html "$APP/Contents/Resources/"
  mac/scripts/sign.sh "$APP"

  (cd dist && rm -f PPTimer-mac.zip && ditto -c -k --keepParent PPTimer-mac/PPTimer.app PPTimer-mac.zip)
  echo "Built $APP and dist/PPTimer-mac.zip"

  if [[ "${2:-}" == "run" ]]; then
    pkill -x PPTimer 2>/dev/null && sleep 1 || true
    open "$APP"
  fi
  exit 0
fi

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
