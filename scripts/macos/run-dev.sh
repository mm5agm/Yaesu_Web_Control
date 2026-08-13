#!/usr/bin/env bash
# Build the CAT-only host, wrap it as a .app (so macOS can prompt for Camera
# / Microphone), and launch it with `open`.
#
# Use this instead of `dotnet run` when you need Radio Display or Remote Audio
# on macOS. Quit any already-running YWC first (same HTTP port).
#
# There is no Dock icon (menu-bar extra). After launch this script prints the
# PID; Activity Monitor name is Yaesu_Web_Control.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
CONFIG="${CONFIG:-Release}"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "error: this launcher is macOS-only." >&2
  exit 1
fi

dotnet build "$ROOT/Yaesu_Web_Control.csproj" -f net10.0 -c "$CONFIG"
"$ROOT/scripts/macos/wrap-dev-app.sh" "$ROOT/bin/$CONFIG/net10.0"

APP="$ROOT/bin/$CONFIG/macos-app/Yaesu Web Control.app"
HOST="$APP/Contents/MacOS/Yaesu_Web_Control"
LOG_DIR="$HOME/Library/Application Support/MM5AGM/Yaesu Web Control/logs"

echo "Starting $APP"
# Intel (osx-x64) OpenCvSharp links Homebrew libavif; without it Radio Display
# lists cameras but cannot open them.
if [[ "$(uname -m)" == "x86_64" ]] && [[ ! -e /usr/local/opt/libavif/lib/libavif.16.dylib ]]; then
  echo "warning: /usr/local/opt/libavif/lib/libavif.16.dylib missing." >&2
  echo "  Radio Display needs: brew install libavif" >&2
fi
open "$APP"

# `open` returns immediately. Wait for the apphost (the launcher exec's it).
pid=""
for _ in 1 2 3 4 5 6 7 8 9 10; do
  pid="$(pgrep -f "$HOST" | head -1 || true)"
  if [[ -n "$pid" ]]; then
    break
  fi
  sleep 0.3
done

if [[ -z "$pid" ]]; then
  echo "error: Yaesu Web Control did not stay running." >&2
  echo "No Dock icon — it is a menu-bar extra. If it crashed, the usual cause is a missing .NET runtime when launched via open." >&2
  echo "Logs: $LOG_DIR" >&2
  exit 1
fi

echo "PID: $pid  (Activity Monitor: Yaesu_Web_Control)"
echo "Look at the right-hand menu bar (Open / About / Exit). There is no Dock icon."
echo "Logs: $LOG_DIR"
echo "Quit from the menu-bar Exit item, then re-run this script after rebuilds."
