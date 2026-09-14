# calibration-contributions

Where the meter calibration numbers in `wwwroot/calibration.default.*.json`
actually come from. One file per radio model.

**This is a development artefact, not a shipped asset.** It must never be added
to `wwwroot`, to the `.csproj` publish items, or to `installer.nsi`. Nothing in
an installed YWC reads it — the import and recompute endpoints return 404 outside
the development build.

Full design:
[`docs/design/calibration-contributions-port-from-iwc.md`](../docs/design/calibration-contributions-port-from-iwc.md).

## Why it exists

The shipped defaults started life as hand-typed guesses. They improve only when
operators send in measurements from real radios. Importing each one straight
into the default file is last-write-wins: the second contributor silently erases
the first, one mis-measured radio becomes everyone's default, and nothing records
where any number came from.

So the default file is **derived**, not edited. Every contribution is kept here,
and the shipped value for each point is the **median** across contributions — an
outlier is outvoted rather than obeyed. With one contribution the value is used
as-is; with none, the hand-typed placeholder is left untouched.

## Privacy

`from` holds a **callsign and nothing else**. Callsigns are public; email
addresses, real names and locations are not, and this directory is committed to
git. If provenance genuinely isn't known, leave `from` out (`null`) and say so in
`note` — an anonymous entry is kept as-is and is never superseded by a later
import.

## Fields

| Field | Meaning |
|---|---|
| `placeholders` | Every value-vector the shipped default has ever held, per meter. How a contribution that just echoes the shipped numbers back is recognised as un-measured. Appended to, never pruned. |
| `contributions[].meters` | The numbers as sent, with the point `labels` alongside so a structural change is caught rather than mis-indexed. |
| `contributions[].unmeasured` | Meters this contributor left at the shipped placeholders. Set by the import, not the contributor; excluded from the median. |
| `contributions[].excluded` | Set by hand to drop a contribution without deleting it, then recompute. |

One contribution per callsign per model: re-importing the same operator's file
supersedes their previous numbers rather than giving them a second vote.

## Undoing a bad contribution

Set `"excluded": true` on it (with an `excludedReason`), then click
**↻ Recompute from contributions (dev)** on the Meter Calibration page. The
shipped default falls back to the median of what remains.

## The seeded state

Only two models carry a real measurement so far:

- **FTdx101MP** — Colin MM5AGM's bench S-meter recalibration (git `1da2a4e`).
- **FTdx10** — the S-meter `+40 dB` point moved from raw 208 to 213 (git
  `acb35a6`). Provenance was unstated in the commit, so it is recorded with
  `from: null` and a `[VERIFY]` note; it will sit there until the email archive
  turns up the original sender.

Every other meter on every model still ships its original hand-typed placeholder,
so those files carry zero contributions and a recompute against them must leave
the shipped default **byte-identical**.

Each meter's `placeholders` list the current shipped vector first. Where a second
S-Meter vector is present (FTdx101MP, FTdx10) it is the pre-recalibration table
the default held before that measurement landed — seeded so a user file that
predates the change is still recognised as un-measured. FTDX5000D / FTDX5000MP
carry the FTdx101MP-derived S-meter vector as a placeholder only: it was copied
across when the per-model files were split, never measured on those radios, so no
contribution is recorded against them.
