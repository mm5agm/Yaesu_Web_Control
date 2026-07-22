#!/usr/bin/env python3
"""Fold a user-emailed YWC calibration into the shipped default for a radio model.

When a user clicks "Email calibration to developer" on the Meter Calibration
page, you receive the JSON from their calibration.user.json (the email subject
names the radio). Save that JSON to a file and run this to merge the meters
they actually calibrated into wwwroot/calibration.default.<Model>.json.

  python scripts/merge-calibration.py -i user-cal.json -m FTDX3000
  python scripts/merge-calibration.py -i user-cal.json -m FTDX3000 --force

Minimal-diff by design: it edits only the individual raw values that changed,
in place, so unchanged meters and the file's hand-formatting stay byte-for-byte
identical (no whole-file reserialisation). Tolerates an email preamble around
the JSON. Does NOT commit -- review `git diff`, then commit yourself.

  --force   also rewrite meters whose points match (normally skipped).

Only per-point *value* changes are applied automatically. If a meter's point
labels or count differ (a structural change -- rare for calibration), it is
listed as needing manual attention rather than guessed at.
"""
import argparse
import json
import re
import sys
from pathlib import Path

RAW_RE = re.compile(r'("raw"\s*:\s*)(-?\d+)')


def load_lenient(text):
    """Parse JSON, tolerating a surrounding email preamble."""
    s, e = text.find('{'), text.rfind('}')
    if s < 0 or e <= s:
        sys.exit("No JSON object found in the input file.")
    return json.loads(text[s:e + 1])


def find_points_span(text, meter_name):
    """Return (start, end) char offsets of the [...] of the named meter's points array."""
    name_idx = text.find(f'"name": "{meter_name}"')
    if name_idx < 0:
        name_idx = text.find(f'"name":"{meter_name}"')
    if name_idx < 0:
        return None
    pts_idx = text.find('"points"', name_idx)
    if pts_idx < 0:
        return None
    open_br = text.find('[', pts_idx)
    if open_br < 0:
        return None
    depth = 0
    for i in range(open_br, len(text)):
        if text[i] == '[':
            depth += 1
        elif text[i] == ']':
            depth -= 1
            if depth == 0:
                return (open_br, i + 1)
    return None


def main():
    ap = argparse.ArgumentParser(description="Merge a user calibration into a model default.")
    ap.add_argument('-i', '--input', required=True, help='the emailed calibration JSON file')
    ap.add_argument('-m', '--model', required=True, help='radio model, e.g. FTDX3000')
    ap.add_argument('--force', action='store_true', help='also touch meters whose points already match')
    args = ap.parse_args()

    repo = Path(__file__).resolve().parent.parent
    wanted = f'calibration.default.{args.model}.json'.lower()
    available = sorted((repo / 'wwwroot').glob('calibration.default.*.json'))
    # Match case-insensitively so '-m ftdx10' finds the tracked 'FTdx10' file
    # (Windows hides the mismatch; a case-sensitive host would not).
    default_path = next((f for f in available if f.name.lower() == wanted), None)
    if default_path is None:
        print(f"No shipped default for model '{args.model}'.")
        print("Models available:")
        for f in available:
            print(f"  {f.name[len('calibration.default.'):-len('.json')]}")
        sys.exit(1)

    incoming = load_lenient(Path(args.input).read_text(encoding='utf-8-sig'))
    if 'meters' not in incoming:
        sys.exit("Input has no 'meters' array -- is this a YWC calibration export?")

    # Read byte-accurate so we can write back preserving the file's original BOM
    # and line endings (FtdX10's default is UTF-8-with-BOM; others use CRLF).
    raw = default_path.read_bytes()
    has_bom = raw.startswith(b'\xef\xbb\xbf')
    text = raw[3:].decode('utf-8') if has_bom else raw.decode('utf-8')
    current = json.loads(text)

    inc_by_name = {m['name']: m for m in incoming['meters']}

    updated, structural, skipped = [], [], []

    for cur in current['meters']:
        name = cur['name']
        inc = inc_by_name.get(name)
        if inc is None:
            continue
        cur_pts, inc_pts = cur.get('points', []), inc.get('points', [])
        if cur_pts == inc_pts and not args.force:
            continue

        cur_labels = [p.get('Radio') for p in cur_pts]
        inc_labels = [p.get('Radio') for p in inc_pts]
        if cur_labels != inc_labels:
            structural.append(name)          # can't safely do a value-only edit
            continue

        # Same labels in the same order -> replace only the raw values that changed.
        changes = {j: inc_pts[j].get('raw') for j in range(len(cur_pts))
                   if cur_pts[j].get('raw') != inc_pts[j].get('raw')}
        if not changes:
            skipped.append(name)
            continue

        span = find_points_span(text, name)
        if span is None:
            structural.append(name)
            continue
        start, end = span
        region = text[start:end]

        counter = {'i': 0}
        def repl(m):
            i = counter['i']; counter['i'] += 1
            return f"{m.group(1)}{changes[i]}" if i in changes else m.group(0)
        new_region = RAW_RE.sub(repl, region)

        text = text[:start] + new_region + text[end:]
        updated.append(f"{name} ({len(changes)} value(s))")

    if not updated:
        print(f"No value changes to apply for {args.model}.")
        if structural:
            print("Structural differences (handle manually): " + ", ".join(structural))
        sys.exit(0)

    out = text.encode('utf-8')
    default_path.write_bytes((b'\xef\xbb\xbf' + out) if has_bom else out)

    print(f"Merged into {default_path}")
    print("Meters updated (values only, formatting untouched):")
    for u in updated:
        print(f"  - {u}")
    if structural:
        print("\nStructural differences NOT applied (point labels/count differ -- edit by hand):")
        for s in structural:
            print(f"  - {s}")
    print("\nReview it, then commit:")
    print(f"  git diff -- wwwroot/calibration.default.{args.model}.json")
    print(f"  git add wwwroot/calibration.default.{args.model}.json")


if __name__ == '__main__':
    main()
