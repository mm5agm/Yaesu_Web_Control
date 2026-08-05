# Porting the IWC contributed-calibration method to YWC

**Status:** Proposed for YWC. **Both parts are now built in IWC** — this is a code port
throughout, no longer a design port.

**Source of truth:** IWC `develop` at `C:\Users\colin\source\repos\Icom_Web_Control`, all of
it real code — read the files, don't reconstruct them from this document:
- **Part A:** `Services/CalibrationStorage.cs`, `Services/CalibrationService.cs`,
  `Controllers/CalibrationController.cs`, `Pages/MeterCalibration/Index.cshtml`.
- **Part B:** `Models/Calibration/CalibrationContributions.cs`,
  `Services/CalibrationContributionsStore.cs`, `calibration-contributions/`, and the
  recompute wired into `CalibrationStorage.ApplyIntoDefault`. IWC
  `docs/design/calibration-contributions.md` records the decisions.

**Author's note:** written 2026-08-04 against both trees; revised 2026-08-05 after Part B
was built in IWC. YWC is IWC's parent, so the calibration code is shared ancestry and the
diff is small and readable. Confirm the **[VERIFY]** items in a YWC session before coding.

> **What changed on 2026-08-05.** Part B was built in IWC first, against two models and an
> empty store, because that is the case where a mistake is cheap and where "recompute
> changes nothing" is a provable correctness check. The recommendation at the foot of this
> document used to be *build it in YWC first*; that is now overtaken. The port here is
> copy-two-files-and-rewire, plus the YWC-specific work in
> ["Where YWC differs"](#where-ywc-differs-from-iwc--the-real-work), which is unchanged and
> is still the part that carries the risk.

---

## 0. What "the IWC method" is, today

| Capability | IWC | YWC |
|---|---|---|
| `✉ Email calibration to developer` (user-facing) | ✅ | ✅ |
| `⬇ Import emailed cal → default (dev)` — paste/clipboard | ✅ | ✅ |
| `⬇ My saved cal → default (dev)` — no clipboard in the loop | ✅ | ❌ |
| `ApplyIntoDefault` extracted as a shared half | ✅ | ❌ (inline) |
| `IncomingSummary` on the import result | ✅ | ❌ |
| `Reload()` from disk on `GET /api/calibration/file` | ✅ | ❌ |
| Minimal-diff surgery into the shipped default | ✅ | ✅ |
| **Contributions store** | ✅ built 2026-08-05 | ❌ |
| **Aggregation across contributors (median)** | ✅ built 2026-08-05 | ❌ |
| `↻ Recompute from contributions (dev)` | ✅ | ❌ |

Both apps import **last-write-wins per value**: import Ann then Bob and the file holds
Bob's numbers wherever they disagree, with nothing recording that Ann contributed.
Part B is what fixes that. Part A is the plumbing it is designed to sit on, which is
why it comes first.

---

## Part A — the import plumbing port (real code, low risk)

### A1. Extract `ApplyIntoDefault`

YWC's `ImportEmailedCalibrationIntoDefault` does parse, model-detect **and** surgery in
one method ([`Services/CalibrationStorage.cs:188-325`](../../Services/CalibrationStorage.cs#L188-L325)).
IWC split it: the parse/detect half stays, and the surgery becomes

```csharp
private CalibrationImportResult ApplyIntoDefault(
    CalibrationFile incoming, string model, List<string> defaultFiles)
```

Port that split verbatim — the body is otherwise identical between the two repos. Part B
has no sane insertion point without it, because the recompute needs to hand *derived*
values to the surgery rather than the incoming file's values.

That is the A1 signature. **Part B widens it** — IWC's is now

```csharp
private CalibrationImportResult ApplyIntoDefault(
    CalibrationFile? incoming, string model, List<string> defaultFiles, ContributionMeta? meta)
```

where a null `incoming` means *recompute only, nobody is contributing*. If you are doing
A and B in one sitting, go straight to the four-parameter form and skip the intermediate.

### A2. `KnownDefaults()` and the filename constants

IWC hoists `calibration.default.` / `.json` into `DefaultPre` / `DefaultSuf`
(`CalibrationStorage.cs:109`) and the `Directory.GetFiles` scan into `KnownDefaults()`.
YWC inlines both. Mechanical; do it while A1 is open, because Part B needs the model
list in a second place.

### A3. `IncomingSummary`

Add `public string IncomingSummary { get; set; } = ""` to `CalibrationImportResult` and
fill it from `CalibrationService.Summarise(incoming)`. It exists so the import log line
can be compared against the save log line: **if they differ, the clipboard was stale,
not the import.** That diagnosis cost a bench session in IWC.

### A4. `Reload()` behind `GET /api/calibration/file`

YWC returns `_service.Current` — the copy loaded at startup. A hand edit, a second
instance, or a dev import is invisible until restart, and "Reload From File" re-shows
what is already on screen. IWC re-reads from disk and returns a `reloaded` flag so the
page can say *"showing last-known values, the file could not be read"* rather than imply
it is showing disk.

### A5. The second import button

- `Services/CalibrationService.cs` — `ImportCurrentCalibrationIntoDefault()`
- `Controllers/CalibrationController.cs` — `POST import-default/current`, dev-gated
  with `NotFound()` exactly like the existing one
- `Pages/MeterCalibration/Index.cshtml` — `importCurrentBtn`, alongside
  `importDefaultBtn` inside the existing dev-only block

This promotes *this PC's saved calibration* into the shipped default with no clipboard
involved. It is how Colin's own bench measurements get in, and in Part B it is the entry
point that appends his contribution.

> **Resolved 2026-08-05 — edit `Pages/MeterCalibration/Index.cshtml` only.** Two pages
> carry `@page`, but `Pages/Shared/_Layout.cshtml:78` links `/MeterCalibration`, and
> `Pages/Calibration/MeterCalibration.cshtml` has no inbound reference anywhere in the
> repo. The second is dead. Do not edit it; say so in the commit message, and consider
> deleting it in a separate commit so this port isn't carrying an unrelated change.

---

## Part B — the contributions store (the median method)

**Now a straight port.** The two files to copy are IWC's
`Models/Calibration/CalibrationContributions.cs` (model classes only, nothing
Icom-specific in it) and `Services/CalibrationContributionsStore.cs`. The public surface
that ended up being built is:

```csharp
CalibrationContributions Load(string model);            // missing file → empty store
void Save(CalibrationContributions store);              // scalar arrays collapsed to one line
static bool MatchesPlaceholder(store, meterName, raw);
static void RememberPlaceholders(store, CalibrationFile);
static Contribution Record(store, incoming, ContributionMeta?, appVersion);
static RecomputeResult Recompute(store, CalibrationFile current);   // pure
```

Note `Record` where B2 below says `Append`, and `Recompute(store, current)` taking the
parsed current default rather than a model name — the shipped file is the authority on
which meters and point labels exist, so the aggregator walks it rather than the store.
`RecomputeResult` carries `Values` (meter → per-point raw), `Spread`, `Refused`,
`Structural` and `Contributors`.

`CalibrationStorage` gained one public entry point beyond the import ones:
`RecomputeIntoDefault(string? model = null)`, which runs the same path with a null
`incoming` — record nothing, re-derive from what is already on disk. That is the undo.

### B1. The idea

Keep every individual contribution in a dev-side store, and **derive** the shipped
default from them. The shipped file format does not change — users, the installer and
`EnsureUserCalibrationExists` are untouched. What changes is where its numbers come
from: instead of each import editing it in place, it is recomputed from the store and
written back through the same minimal-diff surgery.

The recompute is **deterministic and total**: the shipped default is a pure function of
the store plus the hand-authored placeholder. That property is what makes a bad
contribution reversible, and it is worth preserving in any later change.

### B2. Data model

One file per model, at the repo root, tracked in git:

```
calibration-contributions/
  FTdx101MP.json   FTdx101D.json   FTdx10.json   FT-710.json
  FTDX3000.json    FTDX5000D.json  FTDX5000MP.json
```

**Never add this directory to `wwwroot`, the `.csproj` publish items, or
`installer.nsi`.** It is a development artefact, not a shipped asset.

```jsonc
{
  "model": "FTdx101MP",
  // Every value-vector the shipped default has ever held, per meter. Used to
  // recognise un-measured values echoed back from a seeded user file. Appended
  // to on every recompute; never pruned.
  "placeholders": {
    "PWR":     [[30,76,112,157,190,222]],
    "S-Meter": [[0,5,30,67,97,130,172,212,255]]
  },
  "contributions": [
    {
      "id": "2026-06-11-sp3l",        // built version generates an 8-hex-char id for
                                      // live imports; a readable id is fine for
                                      // hand-entered back-fill and nothing parses it
      "from": "SP3L",                 // callsign only — see Privacy
      "date": "2026-06-11",
      "appVersion": "2.3.6",
      "note": "issue #29",
      "meters": {
        "S-Meter": { "labels": ["S0","S1","S3","S5","S7","S9","+20","+40","+60"],
                     "raw":    [0,5,30,67,97,130,172,212,255] }
      },
      // Set by the import, not the contributor. Meters whose values matched a
      // known placeholder exactly — recorded for the audit trail, excluded
      // from aggregation.
      "unmeasured": ["PWR","SWR","Compression","ALC","TPA","IDD","VPA"],
      // Set by hand to drop a contribution without deleting it.
      "excluded": false,
      "excludedReason": null
    }
  ]
}
```

`labels` is stored beside `raw` so a structural change (a meter gaining or losing a
point) is detectable per contribution instead of silently mis-indexed.

### B3. Seed detection

A contributed meter counts as **un-measured** when its `raw` vector exactly equals any
vector in `placeholders[meterName]`.

This matters more in YWC than it did in IWC, for two reasons that are YWC-specific:

1. **The generic default is a copy of a model table.** `calibration.default.json` is the
   FTdx101MP table. A user whose model has no per-model file is seeded from it, so their
   export can look exactly like an FTdx101MP measurement. **Seed every model's
   `placeholders` from that model's own file history *and* from `calibration.default.json`'s
   history.** Skip this and the FTdx101MP table will eat its own placeholders back.
2. **`MergeDefaultsIntoUserFile` is a second seeding vector.** It copies meters that are
   in the default but missing from an existing user file, so a long-standing user's
   export can carry a freshly-seeded meter mixed in with genuinely measured ones.

Both are handled by the same vector test — but only if `placeholders` is seeded from git
history rather than from the current file alone. The full extraction is in
[Appendix A](#appendix-a--ywcs-actual-calibration-history-verified-2026-08-05): every
per-model file has exactly one historical vector per meter except the two S-meters that
were later measured, and the generic file has the messy early-development history. Copy
the vectors from there rather than re-deriving them.

A genuine measurement could in principle coincide with a placeholder across all six or
nine points. The probability is negligible and the failure direction is safe — one
contribution to one meter is skipped.

### B4. Aggregation

Per meter, per point index, over contributions that are not `excluded` and do not list
that meter in `unmeasured`:

| Contributions | Result |
|---|---|
| 0 | keep the existing hand-authored placeholder unchanged |
| 1 | use it as-is |
| ≥ 2 | **median** per point |

Median, not mean: at these sample sizes one bad contribution drags a mean a long way and
a median shrugs it off. For an even count, take the mean of the two middle values. Round
to the nearest integer — raw values are what the radio reports, integers in 0–255, so a
fractional entry is noise.

A **missing** meter is not the same as an un-measured one. FT-710 and FTdx10 tables do
not carry the same meter set as the FTdx101MP; a contribution that simply lacks `VPA`
contributes nothing to `VPA` and must not be recorded as having measured it.

### B5. Validation before writing — all blocking

1. **Monotonicity.** `LoadFromPath` sorts points by `raw` and the engine interpolates
   between them in that order. A vector that is not strictly increasing would silently
   reorder the labels into nonsense. Checked **twice** in the built version: per
   contribution, where a non-increasing vector is refused for that meter and the
   contributor named in the reason; and again on the median, because rounding can flatten
   two close medians into the same number even when every contributor was strictly
   increasing. A flat step fails the second check and the placeholder is kept.
2. **Structural agreement.** A contribution whose `labels` differ from the shipped
   default's is excluded for that meter and reported — `CalibrationImportResult.Structural`
   already carries this concept.
3. **Range.** Raw values outside 0–255 are refused. The CI-V/CAT meter range is a byte, so
   anything else is a transcription error rather than a measurement. Added during the IWC
   build; it costs one line and catches a decimal-point slip.

**Compare labels as decoded strings, not as file text.** YWC's shipped tables write
S-meter labels escaped (`"+20"`), while an emailed export will typically carry
`"+20"`. A textual comparison would flag every S-meter contribution as structural.

### B6. Spread reporting

Report per point: `n`, min, max, and the chosen median, and flag any point whose
max − min exceeds a threshold (10 raw counts is a reasonable start) as *"contributors
disagree"*. This is the part that earns the store its keep day to day — it answers
"does this table rest on two people who agree, or five who don't?"

### B7. Decisions taken during the IWC build

Four things the original design did not settle. All are in the copied code, so they come
across for free — but three of them interact with YWC's back-fill, so read them before
entering the data.

1. **One contribution per callsign per model.** Re-importing an operator's file
   *supersedes* their previous numbers rather than appending. Without it, promoting your
   own bench calibration twice gives you two votes in the median — and the "my saved cal →
   default" button (A5) makes that the easiest mistake to make. The superseding record
   keeps the original `id`, so git history reads as one contributor's numbers changing
   rather than a new person appearing. Contributions with no callsign cannot be matched
   up, so they always append.

2. **Un-measured is decided once, at import**, against the placeholders known at that
   moment — and is *never* re-derived during a recompute. This looks like a small
   optimisation and is not: the numbers written by a recompute become placeholders (so the
   next contributor's echo of them is recognised), which means re-deriving would discard
   the very contribution that produced the current shipped value the moment that value
   shipped. The first contributor for a meter would silently evaporate at the second
   recompute.

3. **Raw outside 0–255 is refused** — see B5.3.

4. **The store file is written with scalar arrays collapsed onto one line.**
   `WriteIndented` alone puts every array element on its own line, turning a nine-point
   S-meter vector into nine lines and a placeholder history into a wall. This file exists
   to be read in a git diff. IWC does it with a regex pass over the serialized text that
   only matches arrays of bare numbers or simple quoted strings, re-scanning for elements
   rather than splitting on `,` so a label or note containing a comma survives intact.

**Where (1) collides with the back-fill.** YWC's two known historical contributions have
known callsigns — SP3L and Colin's own. Under the one-per-callsign rule, the *first live
import* by either of them supersedes their back-filled entry rather than adding to it.
That is the correct behaviour and worth stating in the back-filled `note`: these entries
are provisional reconstructions, and the next file either operator sends replaces them
outright. Where provenance is genuinely uncertain, `"from": null` also opts that entry out
of superseding — it will sit there permanently, which is a reason to be sparing with it.

---

## Where YWC differs from IWC — the real work

1. **Seven models, not two.** IWC ships two default files; YWC ships seven plus the
   generic. The store is per model, so this is mostly repetition — but the generic
   `calibration.default.json` is a *derived copy* and needs an explicit rule. Recommended:
   regenerate it from the FTdx101MP recompute and say so in a comment at the top of the
   recompute code, so it never drifts into being separately hand-edited.

2. **YWC has two real measurements in its shipped tables; IWC has none.** IWC's store
   starts empty. YWC's must be **back-filled** first, because until a contribution is in
   the store the recompute *undoes* it — the store says "no contributions" and the
   hand-authored placeholder wins. **Blocking, not a tidy-up.**

   The extracted history is in [Appendix A](#appendix-a--ywcs-actual-calibration-history-verified-2026-08-05).
   It is smaller than this document previously claimed, and one claim was simply wrong:

   - **Only two numbers have ever moved:** `1da2a4e` (FTdx101MP S-meter, Colin MM5AGM) and
     `acb35a6` (FTdx10 S-meter, +40 dB point 208 → 213). Those are the back-fill.
   - **Jacek SP3L's #29 was not a set of numbers and must not be back-filled as one.**
     `62da7c4` fixed his S-meter by introducing the *per-model split* so an FTdx10 stopped
     inheriting the FTdx101MP table. Its own commit message is explicit that the new files
     are "intentionally identical copies of the FTdx101MP table — they are PLACEHOLDERS,
     not measured data". Recording that as a contribution would promote a placeholder to a
     measurement and give it a vote in every future median.
   - **`acb35a6` has no stated provenance.** The commit says what changed, not who
     measured it or how. Record it with `"from": null` and a note pointing at the commit —
     do not attribute it to Jacek because it happens to be the FTdx10 file, and do not
     attribute it to Colin because he committed it. If you remember whose radio it was,
     that memory is better evidence than either guess; use it, and say so in the note.

   Where provenance is genuinely uncertain, `"from": null` is the honest entry. It also
   opts that record out of the supersede rule (B7.1), so it will sit there permanently —
   another reason to use it sparingly.

   > **[VERIFY] before entering `acb35a6`.** Check Colin's email archive for an FTdx10
   > calibration around 2026-06-14. If one exists, that is the real contribution and it
   > should carry the sender's callsign and the full vector, not just the one point that
   > differed from the placeholder.

3. **`scripts/merge-calibration.py` bypasses the store.** YWC has a 176-line Python tool
   that performs the same minimal-diff surgery outside the app; IWC has no equivalent
   (it was dropped in the carve). Once the shipped default is derived, any edit that
   doesn't go through the recompute is silently reverted at the next import. Either
   retire the script, or reduce it to a read-only diff previewer. Retiring is cleaner —
   Part A5 removes the reason it existed.

4. **Model detection now writes something permanent.** `ImportEmailedCalibrationIntoDefault`
   picks the model by longest name match against the email text. With `FTdx10` /
   `FTdx101D` / `FTdx101MP` and `FTDX5000D` / `FTDX5000MP` in the list, that rule is
   load-bearing. Today a mis-detection produces a bad git diff you can revert; with a
   store it appends a mislabelled contribution that will be re-applied at every future
   recompute. **Show the detected model and require confirmation before appending.**

5. **BOM and CRLF must survive the recompute.** The FTdx10 default carries a UTF-8 BOM.
   The existing surgery already reads and writes byte-accurately; the recompute must go
   through that same path and must not be "simplified" into a serialize-and-write.

---

## Must NOT come across

- **Any change to what users see or send.** The export format stays as-is; `✉ Email
  calibration to developer` is untouched. Everything here is dev-side.
- **Automated collection of any kind.** Contributions arrive by email and are entered
  deliberately.
- **Per-band or per-power-range calibration.** One curve per meter, as today.
- **IWC's model list and paths.** IC-7300 names, `%APPDATA%\MM5AGM\Icom Web Control`,
  and the two-model assumptions do not belong in YWC.

---

## Files

**Copy from IWC, near-verbatim**
- `Models/Calibration/CalibrationContributions.cs` — store model classes. Namespace aside,
  nothing in it is Icom-specific.
- `Services/CalibrationContributionsStore.cs` — load/save, `Record`, `Recompute`, the
  placeholder helpers and the array-collapsing serializer. Pure over the store; **no file
  surgery here**.

**Create**
- `calibration-contributions/*.json` — seven files, back-filled (see "real work" #2), plus
  a `README.md` in that directory stating the never-publish rule and the callsign-only
  privacy rule where whoever opens the folder will actually see them.

**Change**
- `Services/CalibrationStorage.cs` — A1–A2; inject the store; record-and-recompute ahead
  of the surgery so the values written come from `Recompute`; `RememberPlaceholders`
  against the file both before the surgery and after a successful write; add
  `RecomputeIntoDefault`. `CalibrationImportResult` gains `Unmeasured`, `Refused`,
  `Spread`, `Contributors`.
- `Services/CalibrationService.cs` — A3–A5, plus `ContributionMeta?` on both import
  members and `RecomputeDefaultFromContributions`.
- `Controllers/CalibrationController.cs` — A5, plus `from`/`note` on `ImportDefaultRequest`
  and `POST /api/calibration/contributions/recompute`, dev-gated with `NotFound()`.
- `Program.cs` — register `CalibrationContributionsStore` as a singleton **before**
  `CalibrationStorage`.
- `Pages/MeterCalibration/Index.cshtml` — A5 button; callsign/note inputs (callsign
  persisted in `localStorage`, because the same operator promotes their own calibration
  repeatedly and a blank callsign cannot be superseded later); `↻ Recompute from
  contributions (dev)`; spread and refusal detail into the status tooltip and the console
  rather than the status line, which is a `<span>` and too short for it.
- `scripts/merge-calibration.py` — retire or neuter (see "real work" #3)
- `CALIBRATION.md` — no user-facing change, but the "Where Calibration Data Is Saved"
  section still claims development mode writes `wwwroot/calibration.default.json`, which
  `GetActivePath` has not done since 2026-06-12. Fix it while here.

---

## Sequence & testing

YWC has no automated tests; verification is manual, in the browser at
`http://localhost:8080`, in a **development** build (the import paths return `NotFound()`
in Production).

**The aggregation does not need the browser, the app, or a radio.** It is pure functions
over a store and a parsed `CalibrationFile`, so a .NET 10 file-based app can drive the
real classes directly — which is how steps 5–8 below were checked in IWC in one command,
including the outlier and exclusion cases that are tedious to stage by hand:

```csharp
#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property PublishTrimmed=false                          // WinForms + trimming = NETSDK1175
#:property JsonSerializerIsReflectionEnabledByDefault=true   // file-based apps disable it
#:project C:/Users/colin/source/repos/Yaesu_Web_Control/Yaesu_Web_Control.csproj
```

then `dotnet run harness.cs`. Only the on-radio behaviour and the page wiring need the
browser. Two gotchas met on the way: a literal `@` followed by a space anywhere in the
page's script block is a Razor parse error (RZ1003), and `File.WriteAllText` of the
serialized store emits CRLF with **no** trailing newline — hand-written seed files must
match that byte-for-byte or the first recompute produces a whitespace-only diff.

1. Branch off YWC `develop`.
2. **Part A** entire (A1–A5). Verify: import an emailed cal from the clipboard; import
   this PC's saved cal; both produce a small, correct git diff on the right model's file.
   Confirm the two log lines (`[cal-import] incoming values` vs the save line) agree.
3. **Back-fill the store** (real work #2) *before* wiring the recompute into the import.
   Then run a recompute with no new contribution and confirm the shipped files are
   **byte-identical** — that is the proof the back-fill is complete.
4. **Part B** aggregation + validation, wired **inside** `ApplyIntoDefault` — record, then
   recompute, then feed the *derived* values to the existing surgery. It does not go in
   the caller; the surgery has to see medians, not the incoming file.
5. Verify with a synthetic second contributor: hand-add a contribution 4 raw counts away
   on one meter, recompute, confirm the median lands between the two and the spread
   report shows `n=2`.
6. Verify exclusion: set `"excluded": true`, recompute, confirm the table returns to the
   single-contributor values and nothing is lost from the file.
7. Verify seed rejection: import an untouched freshly-seeded `calibration.user.json` and
   confirm every meter is recorded as `unmeasured` and nothing changes in the default.
8. Verify monotonicity refusal: hand-craft two contributions whose median collides on
   two adjacent points; confirm the recompute refuses *that meter only* and says which
   points collide.

---

## Effort and order

Part A is roughly half a session and almost entirely mechanical. Part B's *code* is now
cheaper than when this was first written — two files copied and one caller rewired, with
the design questions already settled in B7 — call it a short session.

**The back-fill is what this costs now.** Everything else is a port; the data entry is
original work, it is blocking (real work #2), and it is the step where getting it wrong
quietly discards contributions people actually sent. Give it its own sitting, and treat
the byte-identical recompute in step 3 as the gate: if the shipped files move at all with
no new contribution in play, the back-fill is incomplete and nothing after it can be
trusted.

**Order:** Part A entire → back-fill → wire the recompute in. Not the other way round.
Wiring the recompute before the back-fill means the first import *undoes* SP3L's and
Colin's contributions — the store would say "no contributions" and the hand-authored
placeholder would win.

IWC's `docs/design/calibration-contributions.md` is the reference implementation and
records why each decision went the way it did; this document is the delta for YWC.

---

## Appendix A — YWC's actual calibration history (verified 2026-08-05)

Extracted by walking `git log --follow` over every `wwwroot/calibration.default*.json` and
collecting the **distinct** raw vectors per meter, oldest first. These are the
`placeholders` to seed, and the commit is which one introduced the vector.

> **Decode blobs as UTF-8 explicitly if you re-run this.** `git show` output taken through
> a default-codepage pipe turns the FTdx10 file's UTF-8 BOM into `ï»¿` and the JSON parse
> fails. A `try/except: continue` around the parse then silently drops exactly the commits
> you care about — that is how `acb35a6` went missing on the first pass here.

**Every per-model file, all seven, shares one placeholder vector per meter** (from
`62da7c4`, or `6e8afb7` for the FTDX5000 pair):

| Meter | Placeholder vector |
|---|---|
| PWR | `[30, 76, 112, 157, 190, 222]` |
| SWR | `[0, 51, 77, 128, 173, 242]` |
| Compression | `[0, 56, 102, 140, 204]` |
| ALC | `[30, 76, 112, 157, 190, 222]` |
| TPA | `[14, 60, 106, 152, 198, 244]` |
| IDD | `[0, 51, 102, 153, 204, 242]` |
| VPA | `[170, 182, 194, 206, 218, 235]` |
| S-Meter | `[0, 4, 30, 65, 95, 131, 171, 208, 255]` |

**Plus these per-model extras** — the only numbers that have ever moved:

| Model | Meter | Extra vector | Commit |
|---|---|---|---|
| FTdx101MP | S-Meter | `[0, 5, 30, 67, 97, 130, 172, 212, 255]` | `1da2a4e` — Colin MM5AGM, a real measurement |
| FTdx10 | S-Meter | `[0, 4, 30, 65, 95, 131, 171, 213, 255]` | `acb35a6` — +40 dB point only, provenance unstated |
| FTDX5000D, FTDX5000MP | S-Meter | `[0, 5, 30, 67, 97, 130, 172, 212, 255]` **is their only vector** | `6e8afb7` |

**The FTDX5000 pair needs a decision.** They were created at `6e8afb7` by copying
FTdx101MP *after* `1da2a4e`, so their shipped S-meter is Colin's FTdx101MP measurement
wearing another model's name. It is a placeholder for those models — nobody has measured
an FTDX5000 — so seed it as one, and do **not** back-fill Colin's contribution against
them. Recording it there would make one radio's measurement look like evidence for two
models it was never taken on.

**The generic `calibration.default.json`** has the early-development churn, all of it
pre-dating any per-model file. Seed all of it — several vectors differ in point *count*,
which can never match a current table anyway, so they cost nothing:

| Meter | Vectors, oldest first |
|---|---|
| S-Meter | `[0,20,40,80,120,160,200,240]` · `[0,4,30,65,95,131,171,208,255]` |
| PWR | `[30,76,112,157,190,222]` |
| SWR | `[30,76,112,157,190,222]` · `[0,25,27,30,74]` · `[0,40,50,52,55,60,65,70,73,80,90]` · `[0,51,77,128,173,242]` |
| ALC | `[30,76,112,157,190,222]` |
| PA Temperature | `[30,76,112,157,190,222]` |
| PA Current | `[0,10,20,30,40]` |
| Compression | `[0,64,128,192,255]` · `[0,56,102,140,204]` |
| TPA | `[0,64,128,192,255]` · `[14,60,106,152,198,244]` |
| IDD | `[0,64,128,192,255]` · `[0,64,117,202,255]` · `[0,51,102,153,204,242]` |
| VPA | `[170,182,194,206,218,235]` |

`e214517` (v2.3.6) appears in every file's log but **changed no calibration numbers** —
an earlier draft of this document listed it as one of three history points, which was
wrong.

---

## Related

- IWC `docs/design/calibration-contributions.md` — the design, and the record of what was
  built on 2026-08-05 and why. The reference implementation.
- IWC `calibration-contributions/README.md` — the folder-level rules (never publish,
  callsign only, how to undo a bad contribution), worth copying across near-verbatim.
- [`region-unification-port-from-iwc.md`](region-unification-port-from-iwc.md),
  [`spectrum-autofloor-port-from-iwc.md`](spectrum-autofloor-port-from-iwc.md) — sibling
  IWC→YWC ports in this folder
- YWC issue #29 (Jacek SP3L) — the calibration work that produced the first real
  contribution, and the reason the last-write-wins limit was noticed
