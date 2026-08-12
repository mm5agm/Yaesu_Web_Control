#!/usr/bin/env bash
# Build an unsigned, self-contained macOS .app + DMG for the CAT-only host.
#
# Usage:
#   scripts/macos/build-dmg.sh              # host RID (osx-arm64 or osx-x64)
#   scripts/macos/build-dmg.sh osx-arm64
#   scripts/macos/build-dmg.sh osx-x64
#   scripts/macos/build-dmg.sh all          # both RIDs
#
# Env overrides:
#   CONFIG=Release          build configuration
#   VERSION=2.4.3           bundle / filename version (default: Models/AppVersion.cs)
#   OUT_DIR=publish/dmg     where finished .dmg files land
#   STAGING_DIR=…           intermediate publish / .app staging (default under OUT_DIR)
#
# No Apple Developer ID — ad-hoc codesign only. Gatekeeper will warn on download;
# see USER_MANUAL §2.2.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"

PROJ="Yaesu_Web_Control.csproj"
TFM="net10.0"
CONFIG="${CONFIG:-Release}"
OUT_DIR="${OUT_DIR:-$ROOT/publish/dmg}"
STAGING_DIR="${STAGING_DIR:-$OUT_DIR/staging}"
APP_NAME="Yaesu Web Control"
BUNDLE_ID="com.mm5agm.yaesuwebcontrol"
EXEC_NAME="Yaesu_Web_Control"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "error: DMG packaging requires macOS (hdiutil / codesign)." >&2
  exit 1
fi

detect_host_rid() {
  case "$(uname -m)" in
    arm64) echo "osx-arm64" ;;
    x86_64) echo "osx-x64" ;;
    *)
      echo "error: unsupported macOS arch: $(uname -m)" >&2
      exit 1
      ;;
  esac
}

read_version() {
  if [[ -n "${VERSION:-}" ]]; then
    echo "$VERSION"
    return
  fi
  local v
  v="$(sed -n 's/.*Current = "\([^"]*\)".*/\1/p' "$ROOT/Models/AppVersion.cs" | head -1)"
  if [[ -z "$v" ]]; then
    echo "error: could not read AppVersion.Current; set VERSION=…" >&2
    exit 1
  fi
  echo "$v"
}

VERSION="$(read_version)"

rid_arch_label() {
  case "$1" in
    osx-arm64) echo "macos-arm64" ;;
    osx-x64)   echo "macos-x64" ;;
    *)
      echo "error: RID must be osx-arm64 or osx-x64 (got: $1)" >&2
      exit 1
      ;;
  esac
}

write_info_plist() {
  local plist="$1"
  local version="$2"
  local include_icon="${3:-0}"
  cat >"$plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleDevelopmentRegion</key>
	<string>en</string>
	<key>CFBundleDisplayName</key>
	<string>${APP_NAME}</string>
	<key>CFBundleExecutable</key>
	<string>${EXEC_NAME}</string>
	<key>CFBundleIdentifier</key>
	<string>${BUNDLE_ID}</string>
	<key>CFBundleInfoDictionaryVersion</key>
	<string>6.0</string>
	<key>CFBundleName</key>
	<string>${APP_NAME}</string>
	<key>CFBundlePackageType</key>
	<string>APPL</string>
	<key>CFBundleShortVersionString</key>
	<string>${version}</string>
	<key>CFBundleVersion</key>
	<string>${version}</string>
	<key>LSMinimumSystemVersion</key>
	<string>13.0</string>
	<key>LSUIElement</key>
	<true/>
	<key>NSHighResolutionCapable</key>
	<true/>
	<key>NSMicrophoneUsageDescription</key>
	<string>Yaesu Web Control captures the radio USB audio codec (and optionally the Mac microphone) for Remote Audio in the browser.</string>
EOF
  if [[ "$include_icon" == "1" ]]; then
    cat >>"$plist" <<EOF
	<key>CFBundleIconFile</key>
	<string>AppIcon</string>
EOF
  fi
  cat >>"$plist" <<EOF
</dict>
</plist>
EOF
}

# Build Contents/Resources/AppIcon.icns from wwwroot/favicon.ico (or publish copy).
# Uses stock macOS sips + iconutil — no Homebrew ImageMagick required.
make_app_icon() {
  local src_ico="$1"
  local dest_icns="$2"
  local work="$3"

  if [[ ! -f "$src_ico" ]]; then
    echo "warning: favicon missing at $src_ico — skipping AppIcon.icns" >&2
    return 1
  fi

  local iconset="$work/AppIcon.iconset"
  local master="$work/favicon-master.png"
  rm -rf "$iconset" "$master"
  mkdir -p "$iconset" "$work"

  # favicon.ico is a 256×256 PNG-in-ICO; sips can re-encode it to PNG.
  sips -s format png "$src_ico" --out "$master" >/dev/null

  # Required iconset slots (1x + 2x). Upscale past 256 for 512/1024 slots so
  # Finder/Retina have something; source art is 256 so larger sizes are soft.
  # Write via a plain .png temp name — sips warns on filenames containing @2x.
  local name size tmp
  for spec in \
    "icon_16x16.png:16" \
    "diana.r@example.org:32" \
    "icon_32x32.png:32" \
    "ivan.p@example.net:64" \
    "icon_128x128.png:128" \
    "wendy.h@example.net:256" \
    "icon_256x256.png:256" \
    "wendy.h@example.net:512" \
    "icon_512x512.png:512" \
    "walt.e@example.net:1024"
  do
    name="${spec%%:*}"
    size="${spec##*:}"
    tmp="$work/resize-${size}.png"
    sips -z "$size" "$size" "$master" --out "$tmp" >/dev/null
    mv "$tmp" "$iconset/$name"
  done

  iconutil -c icns "$iconset" -o "$dest_icns"
  rm -rf "$iconset" "$master"
  echo "App icon → $dest_icns"
}

build_one() {
  local rid="$1"
  local arch_label
  arch_label="$(rid_arch_label "$rid")"

  local publish_dir="$STAGING_DIR/publish-$rid"
  local app_bundle="$STAGING_DIR/${APP_NAME}.app"
  local dmg_root="$STAGING_DIR/dmg-root-$rid"
  local dmg_name="Yaesu_Web_Control_CAT_${VERSION}_${arch_label}.dmg"
  local dmg_path="$OUT_DIR/$dmg_name"

  echo "==> Publishing self-contained $rid ($TFM / $CONFIG)"
  rm -rf "$publish_dir"
  mkdir -p "$publish_dir"
  dotnet publish "$PROJ" \
    -c "$CONFIG" \
    -f "$TFM" \
    -r "$rid" \
    --self-contained true \
    -o "$publish_dir" \
    /p:UseAppHost=true

  if [[ ! -x "$publish_dir/$EXEC_NAME" && ! -f "$publish_dir/$EXEC_NAME" ]]; then
    echo "error: apphost missing after publish: $publish_dir/$EXEC_NAME" >&2
    exit 1
  fi
  chmod +x "$publish_dir/$EXEC_NAME"

  echo "==> Assembling ${APP_NAME}.app"
  rm -rf "$app_bundle"
  mkdir -p "$app_bundle/Contents/MacOS" "$app_bundle/Contents/Resources"
  # Flat layout under MacOS/ so ASP.NET ContentRoot / wwwroot resolve with no code changes.
  cp -a "$publish_dir"/. "$app_bundle/Contents/MacOS/"

  local favicon="$publish_dir/wwwroot/favicon.ico"
  [[ -f "$favicon" ]] || favicon="$ROOT/wwwroot/favicon.ico"
  local has_icon=0
  if make_app_icon "$favicon" "$app_bundle/Contents/Resources/AppIcon.icns" "$STAGING_DIR/iconwork-$rid"; then
    has_icon=1
  fi
  write_info_plist "$app_bundle/Contents/Info.plist" "$VERSION" "$has_icon"

  echo "==> Ad-hoc codesign (no Developer ID)"
  codesign --force --deep --sign - "$app_bundle"

  echo "==> Building DMG → $dmg_path"
  rm -rf "$dmg_root"
  mkdir -p "$dmg_root"
  cp -a "$app_bundle" "$dmg_root/"
  ln -s /Applications "$dmg_root/Applications"

  mkdir -p "$OUT_DIR"
  rm -f "$dmg_path"
  # UDZO = zlib-compressed read-only image; no create-dmg / Homebrew required.
  hdiutil create \
    -volname "$APP_NAME" \
    -srcfolder "$dmg_root" \
    -ov \
    -format UDZO \
    "$dmg_path"

  # Drop a copy of the .app next to the DMG for local smoke tests without mounting.
  rm -rf "$OUT_DIR/${APP_NAME}-${arch_label}.app"
  cp -a "$app_bundle" "$OUT_DIR/${APP_NAME}-${arch_label}.app"

  local bytes
  bytes="$(wc -c <"$dmg_path" | tr -d ' ')"
  echo "OK: $dmg_path ($bytes bytes)"
}

ARG="${1:-$(detect_host_rid)}"

case "$ARG" in
  all)
    build_one osx-arm64
    build_one osx-x64
    ;;
  osx-arm64|osx-x64)
    build_one "$ARG"
    ;;
  *)
    echo "usage: $0 [osx-arm64|osx-x64|all]" >&2
    exit 1
    ;;
esac

echo "Done. DMGs in $OUT_DIR"
