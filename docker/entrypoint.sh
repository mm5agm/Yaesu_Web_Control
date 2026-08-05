#!/bin/sh
# Seed first-run settings into the data volume, then start YWC.
set -e

DATA_ROOT="${XDG_CONFIG_HOME:-/data}"
APP_DIR="$DATA_ROOT/MM5AGM/Yaesu Web Control"
SEED=/opt/ywc/docker-seed/appsettings.user.json

mkdir -p "$APP_DIR/logs"

if [ ! -f "$APP_DIR/appsettings.user.json" ] && [ -f "$SEED" ]; then
  cp "$SEED" "$APP_DIR/appsettings.user.json"
  echo "[entrypoint] Seeded first-run settings at $APP_DIR/appsettings.user.json"
fi

exec dotnet Yaesu_Web_Control.dll "$@"
