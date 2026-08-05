#!/bin/sh
# Ensure the data-volume layout exists, then start YWC.
# First-run settings come from ApplicationSettings + ApplyContainerDefaults
# (written to disk on first Save Settings).
set -e

DATA_ROOT="${XDG_CONFIG_HOME:-/data}"
APP_DIR="$DATA_ROOT/MM5AGM/Yaesu Web Control"

mkdir -p "$APP_DIR/logs"

exec dotnet Yaesu_Web_Control.dll "$@"
