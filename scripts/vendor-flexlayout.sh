#!/usr/bin/env bash
# One-shot vendor of React + flexlayout-react into wwwroot/lib/flexlayout/.
# No project npm — uses a temporary directory and npx esbuild.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/wwwroot/lib/flexlayout"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

REACT_VER="${REACT_VER:-19.1.0}"
FLEX_VER="${FLEX_VER:-0.10.4}"

echo "Vendoring react@$REACT_VER + flexlayout-react@$FLEX_VER → $OUT"
mkdir -p "$OUT/style" "$TMP"

cd "$TMP"
npm init -y >/dev/null
npm install --no-save \
  "react@$REACT_VER" \
  "react-dom@$REACT_VER" \
  "flexlayout-react@$FLEX_VER" \
  esbuild >/dev/null

cat > entry.js <<'EOF'
import * as React from 'react';
import * as ReactDOM from 'react-dom';
import * as ReactDOMClient from 'react-dom/client';
import * as FlexLayout from 'flexlayout-react';

window.React = React;
window.ReactDOM = Object.assign({}, ReactDOM, ReactDOMClient);
window.FlexLayout = FlexLayout;
EOF

npx esbuild entry.js \
  --bundle \
  --format=iife \
  --platform=browser \
  --target=es2020 \
  --outfile="$OUT/flexlayout.bundle.js" \
  --minify

# Prefer alpha_dark if present; fall back to dark.
CSS_SRC=""
for candidate in \
  "node_modules/flexlayout-react/style/dark.css" \
  "node_modules/flexlayout-react/style/underline.css" \
  "node_modules/flexlayout-react/style/light.css"
do
  if [[ -f "$candidate" ]]; then
    CSS_SRC="$candidate"
    break
  fi
done

# Caplin ships theme CSS under style/; alpha_dark may be named differently across versions.
if [[ -f node_modules/flexlayout-react/style/dark.css ]]; then
  CSS_SRC=node_modules/flexlayout-react/style/dark.css
fi
# Prefer alpha_dark when available (shack-dark look).
if [[ -f node_modules/flexlayout-react/style/alpha_dark.css ]]; then
  CSS_SRC=node_modules/flexlayout-react/style/alpha_dark.css
fi

if [[ -z "$CSS_SRC" || ! -f "$CSS_SRC" ]]; then
  echo "ERROR: flexlayout-react theme CSS not found" >&2
  ls -la node_modules/flexlayout-react/style/ >&2 || true
  exit 1
fi

cp "$CSS_SRC" "$OUT/style/dark.css"
# Copy any relative assets (fonts/images) referenced by the theme, if present.
STYLE_DIR="$(dirname "$CSS_SRC")"
if [[ -d "$STYLE_DIR" ]]; then
  # Copy sibling non-css assets that dark themes sometimes need.
  find "$STYLE_DIR" -maxdepth 1 \( -name '*.png' -o -name '*.svg' -o -name '*.woff*' \) \
    -exec cp {} "$OUT/style/" \; 2>/dev/null || true
fi

# Caplin demo popout host (empty document; FlexLayout copies styles + portals).
cat > "$ROOT/wwwroot/popout.html" <<'HTML'
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>YWC — Flex Popout</title>
    <style>
        html, body { margin: 0; height: 100%; overflow: hidden; background: #1e1e1e; }
    </style>
</head>
<body></body>
</html>
HTML

echo "Done:"
ls -lh "$OUT/flexlayout.bundle.js" "$OUT/style/dark.css" "$ROOT/wwwroot/popout.html"
