#!/usr/bin/env bash
# Print the dshow device name of the radio's USB CODEC.
#
# The name carries a Windows enumeration number ("Line (2- USB AUDIO  CODEC)")
# which changes when the device is re-plugged or another USB audio device
# appears. Hardcoding it into the capture command means the command silently
# stops working - ffmpeg reports "Could not find audio only device", which
# reads like a driver fault rather than a renamed device. Ask, do not remember.
#
# Note the name can contain double spaces. Always quote it.
set -uo pipefail

list() {
    ffmpeg -hide_banner -list_devices true -f dshow -i dummy 2>&1 \
        | sed -n 's/.*"\(.*\)" (audio).*/\1/p'
}

name=$(list | grep -i 'CODEC' | head -1)
[ -z "$name" ] && name=$(list | grep -i 'USB AUDIO' | head -1)

if [ -z "$name" ]; then
    echo "no USB CODEC among the dshow audio devices:" >&2
    list | sed 's/^/  /' >&2
    exit 1
fi
printf '%s' "$name"
