#!/usr/bin/env bash
# Fetch W1AW code practice files for benching the CW reader.
#
# Why these and not our own recordings: every bench wav we have is a real
# signal with no transcript, so a bench run can only count characters and
# eyeball the result. These come with the exact text and the exact speed, so a
# decode can be scored. They are studio clean - 750 Hz, no hiss, no QSB - so on
# their own they only prove the decoder works at all. The value is that noise,
# fading and mistuning can be added in known amounts on top of known text,
# which is how an accuracy-against-SNR curve gets built.
#
# ARRL copyright. They are offered freely for code practice, and benching a
# decoder against them is that in spirit, but they are not ours to redistribute
# - so they land in bench/, which is gitignored, and they stay there.
#
# The published set is replaced every other week, so the URLs below go stale.
# Re-run with -l to list what is currently offered.
#
#   ./scripts/get-arrl-practice.sh          fetch the set below
#   ./scripts/get-arrl-practice.sh -l       show the current index page
set -euo pipefail

base="https://www.arrl.org/files/file/Morse"
dest="$(cd "$(dirname "$0")/.." && pwd)/bench/arrl"

if [[ "${1:-}" == "-l" ]]; then
    echo "Current set: https://www.arrl.org/code-practice-files"
    exit 0
fi

mkdir -p "$dest"

# mp3 stem : text stem. Kept as pairs because the naming is not consistent -
# 7.5 wpm is 075WPM.mp3 against 07_5.txt.
files="
260304_05WPM.mp3:260304_05.txt
260304_10WPM.mp3:260304_10.txt
260304_13WPM.mp3:260304_13.txt
260304_15WPM.mp3:260304_15.txt
260304_18WPM.mp3:260304_18.txt
260303_20WPM.mp3:260303_20.txt
260303_25WPM.mp3:260303_25.txt
260303_30WPM.mp3:260303_30.txt
260303_35WPM.mp3:260303_35.txt
260303_40WPM.mp3:260303_40.txt
"

for pair in $files; do
    for f in "${pair%%:*}" "${pair##*:}"; do
        if [[ -f "$dest/$f" ]]; then
            echo "have $f"
        else
            echo "get  $f"
            curl -sSL --max-time 180 -o "$dest/$f" "$base/$f"
        fi
    done
done

echo
echo "In $dest"
echo "Convert to the 8 kHz mono the bench tool reads with scripts/arrl-to-wav.sh"
