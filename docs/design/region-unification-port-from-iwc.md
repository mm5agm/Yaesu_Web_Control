# Region unification — port from IWC

**Status:** Proposed, ready to implement
**Written:** 2026-08-02, after the work landed and was checked on-radio in IWC
**Source commits (Icom_Web_Control, `develop`):**

| Commit | What |
|---|---|
| `0f30842` | Server resolves band names against the operator's IARU region; band button goes red when out of band |
| `ab34c7b` | Segment dropdown tracks the live frequency and shows **OOB** when out of band |

Combined: 8 files, +570 / −45, one new file (`Services/BandPlanService.cs`, 286 lines).

---

## The defect

YWC has **two** definitions of where a band starts and ends, and only one of them
knows what region the operator is in.

| | Where | Region-aware? |
|---|---|---|
| Server | `Services/RadioStateService.GetBandFromFrequency` (line **718**) | ❌ hardcoded ladder |
| Browser | `BAND_EDGES` in `wwwroot/js/ui/band-plan.js` (line **~298**), overlaid from `wwwroot/bandplan.default.json` | ✅ per-region |

They disagree, and the server is the generous one:

| Band | Server says | IARU R1 says |
|---|---|---|
| 80m | 3.500 – 4.000 | 3.500 – **3.800** |
| 40m | 7.000 – 7.300 | 7.000 – **7.200** |
| 160m | **1.800** – 2.000 | **1.810** – 2.000 |

So a UK operator on 3.9 MHz is told `BandA = "80m"` — the 80 m button lights, the
toolbar announces "80 metres" — while the waterfall, which *is* region-aware,
draws them outside the allocation. The radio is happy to transmit there.

The fix is one source of truth, resolved to one region at the seam: the server
reads the same `wwwroot/bandplan.default.json` the browser does.

---

## Why this is a near-verbatim port, not a re-design

Measured directly against the IWC tree immediately before the change
(`0f30842^`), ignoring CRLF:

| File | Divergence from YWC |
|---|---|
| `wwwroot/js/ui/band-plan.js` | **3 comment lines** (two say IWC/YWC; one describes the mode vocabulary) |
| `wwwroot/bandplan.default.json` | **1 line** — the `_comment` naming. `bandEdges` is already present at line 161 and is identical |
| `site.js` segment subsystem (`segmentStorageKey` → the Raw Meter Label comment, 167 lines) | **byte-identical** |
| `site.js` `updateBandButtonsFromBackend` + `updateBandButton` (61 lines) | **byte-identical** |
| `site.js` `FrequencyA`/`FrequencyB` SignalR handler | identical bar 4 IWC-only lines feeding `dxSpotsPanel` |
| `Services/RadioStateService.GetBandFromFrequency` | **identical** hardcoded table |
| `Models/ApplicationSettings.BandPlan` (line 61) | identical, `"Region1"` default |
| `wwwroot/css/site.css` `.band-radio-label` / `.segment-select` blocks | identical, at lines 404–460 / 468 |
| `Pages/Index.cshtml` `fmtBand` (line 3668) | **identical** |
| `Pages/Shared/_BandButtonsPartial.cshtml` band list (line 3) | identical 12 bands, 160m…4m, no 2m |

`Services/BandPlanService.cs` contains exactly **two** IWC-specific lines: the
`namespace` and one comment. `_ywcRegion` already exists in `Index.cshtml`
(line 2245) doing the same job `_iwcRegion` does in IWC.

---

## Behaviour change to accept before starting

`BandA` / `BandB` will return `"Unknown"` for frequencies that used to get a band
name. That is the entire point, but it reaches further than the band buttons:

- **Band buttons** — all deselect. Handled by Phase B (red marker).
- **Segment dropdown** — no segment. Handled by Phase C (**OOB**).
- **Voice announcements** — `fmtBand` would read the literal word "Unknown" aloud.
  Handled in Phase A. This matters more in YWC than it did in IWC: voice is a
  required feature for partially-sighted operators in both, but YWC has actual users.
- **`appsettings.user.json`** — `BandProfilesA["Unknown"]` / `BandProfilesB["Unknown"]`
  would be written and would then be recalled as if it were a real band. Guarded
  in Phase A. **YWC has four write sites where IWC had three** — see below.
- **DX spot filtering — NOT affected.** `wwwroot/js/ui/dx-spots-panel.js` has its
  own worldwide `BAND_EDGES` and buckets by frequency; it never reads `BandA`.
  Verified in IWC, and the file is shared heritage.

**YWC covers more radios than IWC, so `"Unknown"` will be hit more often** — an
FTdx101MP tunes 30 kHz–75 MHz and the general-coverage receiver spends plenty of
time outside every amateur allocation. Expect the OOB state to be visible in
normal use, not an edge case.

---

## Phase A — server resolves the region (from `0f30842`)

### A1. Copy `Services/BandPlanService.cs` from IWC

Change `namespace Icom_Web_Control.Services` → `namespace Yaesu_Web_Control.Services`,
and the one `IWC runs` comment at line 122. Nothing else.

What it does: reads `bandEdges` from `Path.Combine(env.WebRootPath, "bandplan.default.json")`,
resolves `settings.BandPlan`, answers `BandForFrequency(long hz)`. Falls back to a
built-in copy of the JS table if the file is missing or unparseable, so a bad JSON
edit degrades to today's behaviour rather than breaking the app.

Two design points that are **not optional**, both learned the hard way:

- **5-second TTL on the resolved region.** `SettingsService.GetSettingsAsync()`
  re-reads the file on every call, and the `FrequencyA`/`FrequencyB` setters call
  into `BandForFrequency` at ~10 Hz. Without the cache this is a file read per
  meter poll.
- **`Task.Run(() => _settings.GetSettingsAsync()).GetAwaiter().GetResult()`,
  not a bare `.GetAwaiter().GetResult()`.** YWC has a WinForms host, so there is a
  `SynchronizationContext` on the thread that can construct `RadioStateService`.
  A bare sync-over-async deadlocks there.

Also expose the constant other files will need:

```csharp
public const string UnknownBand = "Unknown";
```

### A2. `Program.cs` — register it

`using Yaesu_Web_Control.Services;` is already at line 6, so no new using. Add
before the `AddSingleton<RadioStateService>()` at line **259**:

```csharp
// Band edges for the operator's own IARU region, read from
// wwwroot/bandplan.default.json — the same file the browser overlays at
// startup. RadioStateService resolves BandA/BandB through this, so the server
// and the waterfall can no longer disagree about where a band ends.
builder.Services.AddSingleton<IBandPlanService, BandPlanService>();
```

`ISettingsService` is registered later (line 275). That is fine — resolution is
lazy — but put `BandPlanService` before `RadioStateService` anyway for readability.

### A3. `Services/RadioStateService.cs` — delete the ladder

Add the field, add `IBandPlanService bandPlan` to the constructor (line **42**),
and assign it **before** `_initialState = _statePersistence.Load();` at line 50 —
the constructor goes on to set `FrequencyA`, which triggers
`UpdateBandFromFrequency()` → `GetBandFromFrequency`. Assign it late and you get a
`NullReferenceException` at startup.

Then replace lines **718–733** wholesale:

```csharp
/// <summary>
/// Band name for a frequency, in the operator's own IARU region.
///
/// This used to be a hardcoded ladder here, region-blind and generous
/// with the edges — it disagreed with the browser's region-aware
/// BAND_EDGES (R1 80m: 3.800 there, 4.000 here). BandPlanService now
/// answers from wwwroot/bandplan.default.json, the same table the
/// browser uses. Off-band still returns "Unknown", so every existing
/// consumer behaves as before.
/// </summary>
public string GetBandFromFrequency(long freq) => _bandPlan.BandForFrequency(freq);
```

### A4. `Controllers/CatController.cs` — four guards, not three

IWC unified the band-stacking save into one block; YWC still has separate A and B
paths, so there are **four** sites. Each becomes `&& <band> != BandPlanService.UnknownBand`:

| Line | Context |
|---|---|
| **484** | `if (!string.IsNullOrEmpty(oldBand))` before `settings.BandProfilesA[oldBand] = new BandProfile` |
| **558** | same for `BandProfilesB` |
| **635** | `if (!string.IsNullOrEmpty(bandA))` in `SetAntennaA` |
| **679** | `if (!string.IsNullOrEmpty(bandB))` in `SetAntennaB` |

### A5. `Pages/Index.cshtml` — stop reading "Unknown" aloud

In `fmtBand`, line **3668**, after the `if (!b) return '';`:

```javascript
// The server reports "Unknown" for a frequency outside every
// allocation in the operator's own region. Since band names
// became region-resolved that is a normal thing to hear, and
// "out of band" says what has actually happened.
if (String(b).toLowerCase() === 'unknown') return 'out of band';
```

---

## Phase B — red band-button marker (from `0f30842`)

Colin's reaction to Phase A alone was *"button doesn't go dark, it just reverts to
the not selected color. Should it not go red?"* — an empty band grid gives the
operator no signal at all. This phase answers "which band were you aiming at",
**not** "may I transmit".

### B1. `band-plan.js` — `nearestBandForHz(hz, edges)`

Copy the exported function from IWC verbatim, after the `BAND_EDGES.UK` / `.USA`
aliases. It measures distance to the nearest *edge* of each band in the
operator's own region and returns the closest name.

> **A worldwide-envelope version was tried first and was wrong.** For nearly every
> band the worldwide *lower* edge equals the region's (regions differ at the
> **top**), so tuning *below* a band fell outside the envelope and marked nothing;
> on 20 m neither direction worked. Colin caught it: *"top band edge makes button
> go red but going below the bottom band edge does not."* Nearest-edge distance is
> symmetric and needs no second table. Don't reintroduce the envelope.

### B2. `wwwroot/js/ui/site.js` — top-level state and the marker

Copy `lastVfoHz`, `lastVfoBand`, `applyBandOutOfBand(receiver)` and
`window.refreshBandOutOfBand` from IWC. In YWC they go just above
`function updateBandButton` at line **1799**.

Then, in the same order IWC does it:

- `updateBandButton` sets `lastVfoBand[receiver] = band;` and calls
  `applyBandOutOfBand(receiver);` after `syncBandAriaChecked`.
- The `FrequencyA` / `FrequencyB` handlers (lines **1156** / **1165**) set
  `lastVfoHz.A` / `.B` and call `applyBandOutOfBand` in a try/catch.
- `updateBandButtonsFromBackend` (line **1769**) simplifies to
  `if (data.vfoA && data.vfoA.band) updateBandButton('A', data.vfoA.band);` and the B equivalent.

Three traps, all of which bit in IWC:

1. **`state` is trapped in an IIFE.** Everything above line ~2300 in `site.js`
   cannot see it — bare access throws `ReferenceError` and silently aborts the
   rest of the handler. That is why `lastVfoHz` / `lastVfoBand` are *top-level*
   and not on `state`.
2. **`BandA` only broadcasts on *change*.** An operator already out of band at
   page load never gets a `BandA` event, so the frequency handler must re-apply
   the marker.
3. **`a11y-labels.js` rewrites every `data-a11y-key` `title` on window focus**,
   silently wiping the "— out of band for your region" tooltip suffix. Hence
   `window.refreshBandOutOfBand` and the `reapplyLabels` wrapper in B4.

`applyBandOutOfBand` also needs its `anyChecked` guard — without it, clicking a
band button flashes the button red, because the frequency update arrives before
the `BandA` broadcast that clears `lastVfoBand`.

### B3. `wwwroot/css/site.css` — the marker

Add after the `.border-success .band-radio-label:has(input:checked)` rule that
ends at line **458**:

```css
.band-radio-label.band-oob,
.band-radio-label.band-oob:hover {
    background-color: #dc3545;
    border-color: #842029;
    color: #000;
}
.band-radio-label.band-oob span { font-weight: bold; }
```

### B4. `Pages/Index.cshtml` — wire it up

Line **2150**, add `nearestBandForHz` to the import list and bump the cache-bust.
YWC is on `?v=5`; go to `?v=6`.

Replace `loadLabels(); window.addEventListener('focus', loadLabels);` at lines
**2158–2159** with the IWC version, and add the `window.nearestBandForHz` global.
**Resolve `BAND_EDGES` inside the arrow, on every call** — `loadBandPlanFromServer()`
deletes and reassigns the per-region arrays when it overlays the JSON, so anything
capturing the array keeps answering from the pre-overlay table:

```javascript
window.nearestBandForHz = hz =>
    nearestBandForHz(hz, BAND_EDGES['@Model.BandPlan'] || BAND_EDGES.Region1);

const reapplyLabels = () => loadLabels().finally(() => {
    if (typeof window.refreshBandOutOfBand === 'function') window.refreshBandOutOfBand();
});
reapplyLabels();
window.addEventListener('focus', reapplyLabels);
```

---

## Phase C — Segment dropdown (from `ab34c7b`)

The whole 167-line segment subsystem is byte-identical between the two repos, so
this phase is a straight copy. YWC anchors: `segmentStorageKey` **3243**,
`syncSegmentSelectToFrequency` **3253**, `populateSegmentSelect` **3284**,
`onSegmentChange` **3326**, `onBandChanged` **3364**.

### C1. `band-plan.js` — bound `segmentForHz` to the band edges

Add the exported `edgeForBand(bandPlan, band)` and, in `segmentForHz`, reject
out-of-band before matching:

```javascript
const edge = edgeForBand(bandPlan, band);
if (edge && (hz < edge.lo || hz > edge.hi)) return null;
```

This fixes two genuine bugs that predate the region work:

- Segments are activity *centres* with no upper bound, so the match loop's last
  survivor claimed everything above the top segment however far out of band you tuned.
- The "below-lowest → first segment" fallback also swallowed genuinely-below-edge
  frequencies. **Keep the fallback** — it is deliberate and right for 14.010 MHz,
  which is in the 20 m CW sub-band even though it is below the watering hole at
  14.025. With the edge check in front of it, it now only fires in-band.

### C2. `site.js` — OOB rendering

Copy from IWC: the `OOB_BAND` / `isOutOfBand` helpers, `setSegmentOutOfBand`, the
`populateSegmentSelect` changes, and the guards in `syncSegmentSelectToFrequency`
and `onSegmentChange`.

Points worth not losing in the copy:

- **`--` and OOB are different states.** `--` means this band has no activity plan
  in the JSON (4m outside Region 1, 60m in Japan); OOB means you are outside every
  allocation in your region. Keep both.
- **`setSegmentOutOfBand` rewrites `aria-label` and `title`, not just the class.**
  A partially-sighted operator gets the accessible name, not the red. The select
  carries no `data-a11y-key` (checked: `Pages/Index.cshtml` line **1026**), so
  `a11y-labels.js` will not clobber it.
- **`onSegmentChange` bails on `Unknown`** before touching the radio or localStorage.

### C3. `site.js` — make the dropdown follow the radio

`populateSegmentSelect` ends by syncing to the live frequency; the localStorage
value is demoted to a fallback for the moment before the frequency is known.

> **⚠️ This must read the top-level `lastVfoHz` from Phase B, not
> `state.lastBackendFreq`.** `state.lastBackendFreq.A` is written at
> `site.js` line **1158** inside a `try/catch` from a scope where `state` isn't
> visible — it throws and is swallowed on *every* update, so the value is stale.
> The symptom, which Colin hit in IWC: tuning back up into the band left the
> dropdown showing `--`. `FrequencyA` arrives **before** `BandA`, so the good sync
> early-returns against the still-disabled OOB placeholder, and the populate-time
> sync is the last word — with a stale, still-out-of-band frequency.

Also only override the saved value when the live frequency really lands in the
band being populated, or a band-button click flashes `--` while the radio is still
retuning:

```javascript
const live = window.getBandSegmentForHz
    ? window.getBandSegmentForHz(plan, band, hz)
    : null;
if (live) syncSegmentSelectToFrequency(vfo, hz);
```

### C4. `wwwroot/css/site.css` — OOB styling

After the `.segment-select` block ending at line **475**. `.segment-select` is
*already* `font-weight: bold`, so only colour is new. The `:disabled` qualifier is
what outranks Bootstrap's own `.form-select:disabled` background — and `site.css`
is loaded at `_Layout.cshtml` line 129, well after `bootstrap.min.css` at line 11,
so it wins ties on source order:

```css
.segment-select.segment-oob,
.segment-select.segment-oob:disabled,
.segment-select.segment-oob:hover,
.segment-select.segment-oob:focus {
    background-color: #dc3545;
    border-color: #842029;
    color: #000;
    font-weight: bold;
    opacity: 1;
}
```

---

## Verification

**Server binding — do this first.** `BandPlanService` falls back *silently* to its
built-in table if the JSON doesn't bind, so a typo in the property names looks
like success. In IWC this was checked with a throwaway console probe over 21
cases before anything else was touched. The cases that prove region resolution is
live, all with `BandPlan = "Region1"`:

| Frequency | Expect |
|---|---|
| 3.900 MHz | `Unknown` (was `80m`) |
| 7.250 MHz | `Unknown` (was `40m`) |
| 1.805 MHz | `Unknown` (was `160m`) |
| 14.350 MHz | `20m` — top edges are **inclusive** |

Switch `BandPlan` to `Region2` and the same three must return `80m` / `40m` / `160m`.

**`segmentForHz` — 16 node cases** covering above *and* below every edge; the IWC
set is worth copying. Key ones: R1 20m 13.990 → `null`, 14.010 → `CW`,
14.350 → `SSB`, 14.360 → `null`; R1 40m 7.250 → `null` but R2 40m 7.250 → `SSB`.

**On-radio, per VFO — both directions.** The asymmetric-marker bug only showed up
because Colin tuned *down* as well as up:

1. Tune below the bottom edge → button red, dropdown **OOB** and red.
2. Tune back up into the band → button green/blue, **dropdown shows the segment
   the radio is actually on**, not `--`.
3. Same above the top edge.
4. Repeat on VFO B.
5. Click a band button → no red flash, no `--` flash.
6. Check the voice announcement says "out of band", not "Unknown".
7. Check `appsettings.user.json` has gained no `"Unknown"` key under
   `BandProfilesA` or `BandProfilesB`.

---

## Do not touch

- **`wwwroot/js/ui/dx-spots-panel.js`'s `BAND_EDGES`** and **`SpectrumPanel.BAND_EDGES`**
  (the fallback used before `setBandEdges`) are deliberately *worldwide* envelopes.
  You want a US station's 7.250 spot labelled 40m in a Region 1 operator's list.
  Leave both alone.
- **The `#33` fix.** `window.applySegmentsOnInit` was removed on 2026-06-12 because
  it auto-tuned the radio to the last-clicked segment on startup, overwriting
  whatever the operator had set on the rig — Jacek SP3L's *"YWC changes radio
  frequency to some default value"*. Phase C restores the dropdown's *displayed*
  value only. **Nothing in this port may tune the radio on init.**

---

## Notes for later

The OOB colours (`#dc3545` / `#842029` / `#000`) are hardcoded in both repos. If
the CSS-custom-property tokenising for the skin work lands first, they should
become skin tokens rather than literals.
