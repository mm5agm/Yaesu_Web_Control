#!/bin/sh
# Ensure the bind-mounted data volume is writable, then start YWC as `app`.
# Host dirs like ./data/ywc are often root-owned; the image user cannot mkdir
# there. We start as root, chown, then drop privileges with setpriv while
# keeping Docker group_add GIDs (host dialout/audio for serial + ALSA).
set -e

DATA_ROOT="${XDG_CONFIG_HOME:-/data}"
APP_DIR="$DATA_ROOT/MM5AGM/Yaesu Web Control"

mkdir -p "$APP_DIR/logs"

if [ "$(id -u)" = "0" ]; then
  chown -R app:app "$DATA_ROOT"

  APP_UID="$(id -u app)"
  APP_GID="$(id -g app)"
  # id -G as root includes Docker group_add entries; keep those, drop root's 0.
  # (--groups replaces supplementary groups; do not also pass --clear-groups —
  # Ubuntu 24.04 setpriv treats those as mutually exclusive.)
  SUPP="$(id -G | tr ' ' '\n' | awk -v g="$APP_GID" '$1 != 0 && $1 != g { print }' | paste -sd, -)"
  GROUPS="$APP_GID"
  [ -n "$SUPP" ] && GROUPS="$APP_GID,$SUPP"

  exec setpriv \
    --reuid="$APP_UID" \
    --regid="$APP_GID" \
    --groups="$GROUPS" \
    -- \
    dotnet Yaesu_Web_Control.dll "$@"
fi

exec dotnet Yaesu_Web_Control.dll "$@"
