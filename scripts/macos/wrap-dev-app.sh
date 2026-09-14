#!/usr/bin/env bash
# Wrap a net10.0 build output folder as Yaesu Web Control.app so macOS TCC
# can prompt for Camera (Radio Display) and Microphone (Remote Audio).
#
# A bare `dotnet run` apphost has no Info.plist. macOS never shows a Camera
# dialog in that case, and Terminal / Cursor never appear under
# System Settings → Privacy & Security → Camera.
#
# Usage:
#   scripts/macos/wrap-dev-app.sh [build-output-dir]
# Default output dir: bin/Release/net10.0
#
# The .app is written beside the build folder:
#   bin/Release/macos-app/Yaesu Web Control.app
# Launch with `open` (not by exec'ing the inner binary from Terminal) so
# TCC attributes the request to Yaesu Web Control, not Terminal.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=info-plist.sh
source "$ROOT/scripts/macos/info-plist.sh"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "error: wrapping a macOS .app requires Darwin." >&2
  exit 1
fi

OUT="${1:-$ROOT/bin/Release/net10.0}"
if [[ ! -d "$OUT" ]]; then
  echo "error: build output not found: $OUT" >&2
  echo "Build first: dotnet build -f net10.0 -c Release" >&2
  exit 1
fi
if [[ ! -f "$OUT/$EXEC_NAME" ]]; then
  echo "error: apphost missing: $OUT/$EXEC_NAME" >&2
  exit 1
fi

VERSION="$(sed -n 's/.*Current = "\([^"]*\)".*/\1/p' "$ROOT/Models/AppVersion.cs" | head -1)"
[[ -n "$VERSION" ]] || VERSION="0.0.0"

APP_DIR="$(cd "$OUT/.." && pwd)/macos-app/${APP_NAME}.app"
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"

# Flat layout under MacOS/ so ASP.NET ContentRoot / wwwroot resolve with no code changes.
# `dotnet build` (framework-dependent) copies every RID under runtimes/, and the
# TFM folder may also contain leftover `osx-x64/` publish dumps. Those make
# `codesign --deep` fail ("bundle format unrecognized" on runtimes/win/lib).
# Keep runtimes/osx* + unix (OpenCvSharp, PortAudio, Avalonia native).
rsync -a \
  --exclude '/osx-x64' \
  --exclude '/osx-arm64' \
  --exclude '/win-x64' \
  --exclude '/runtimes/win' \
  --exclude '/runtimes/win-*' \
  --exclude '/runtimes/linux-*' \
  --exclude '/runtimes/android-*' \
  --exclude '/runtimes/maccatalyst-*' \
  "$OUT/" "$APP_DIR/Contents/MacOS/"
chmod +x "$APP_DIR/Contents/MacOS/$EXEC_NAME"

# `open` does not inherit the terminal PATH, so a framework-dependent
# apphost exits at once ("You must install .NET") and `open` still
# reports success. A tiny launcher finds Homebrew /usr/local/share
# dotnet and exec's the real apphost.
LAUNCHER="YWCLaunch"
cat >"$APP_DIR/Contents/MacOS/$LAUNCHER" <<'EOF'
#!/bin/bash
DIR="$(cd "$(dirname "$0")" && pwd)"
find_dotnet_root() {
  if [[ -n "${DOTNET_ROOT:-}" && -x "${DOTNET_ROOT}/dotnet" ]]; then
    printf '%s\n' "$DOTNET_ROOT"
    return 0
  fi
  local d cell
  for d in \
    /usr/local/share/dotnet \
    /opt/homebrew/opt/dotnet/libexec \
    /usr/local/opt/dotnet/libexec
  do
    if [[ -x "$d/dotnet" ]]; then
      printf '%s\n' "$d"
      return 0
    fi
  done
  for cell in /usr/local/Cellar/dotnet/*/libexec /opt/homebrew/Cellar/dotnet/*/libexec; do
    if [[ -x "$cell/dotnet" ]]; then
      printf '%s\n' "$cell"
      return 0
    fi
  done
  return 1
}
ROOT="$(find_dotnet_root || true)"
if [[ -z "$ROOT" ]]; then
  osascript -e 'display alert "Yaesu Web Control" message "The .NET runtime was not found. Install the .NET 10 SDK, or run scripts/macos/run-dev.sh from a terminal where dotnet is on PATH."' >/dev/null 2>&1 || true
  echo "You must install .NET to run this application." >&2
  exit 1
fi
export DOTNET_ROOT="$ROOT"
export DOTNET_ROOT_X64="$ROOT"
exec "$DIR/Yaesu_Web_Control" "$@"
EOF
chmod +x "$APP_DIR/Contents/MacOS/$LAUNCHER"
write_info_plist "$APP_DIR/Contents/Info.plist" "$VERSION" 0 "$LAUNCHER"
# Do not codesign the dev wrap. The framework-dependent output is full of
# +x DLLs that codesign treats as nested Mach-O; the self-contained DMG
# publish is the signed artefact (`build-dmg.sh`). Local `open` still
# reads Info.plist so TCC can prompt for Camera / Microphone.
xattr -cr "$APP_DIR" 2>/dev/null || true

echo "OK: $APP_DIR"
echo "Launch with: open \"$APP_DIR\""
