# Porting the IWC auto-floor spectrum display to YWC

**Status:** IMPLEMENTED on `develop` (2026-08-01). See the "Implementation notes" section
at the foot of this doc for how the two **[VERIFY]** items resolved and where YWC
diverged from the plan.
**Source of truth:** Icom Web Control (IWC), commit `dc2d310` on `develop`
(`Scope: fix once/sec stutter + auto noise-floor scaling`). Files:
`wwwroot/js/sdr/spectrum-panel.js` and `Pages/Index.cshtml`.
**Author's note:** written from the IWC change set and the fact that YWC is IWC's
parent (IWC was cloned from YWC, so the two `SpectrumPanel`s share ancestry).
A YWC session must confirm the two **[VERIFY]** items below against the current
YWC tree before coding.

---

## Why this is mostly a frontend port

The visual improvement lives entirely in the **display layer**, which is
receiver-agnostic: the auto noise-floor, the two-stage smoothing, and the
axis-strip trace mapping all operate on the `bins[]` / dBFS array *after* it
arrives, regardless of whether it came from a CI-V scope (IWC) or an SDRplay FFT
(YWC).

Crucially, the logic lives **per `SpectrumPanel` instance**, so YWC's two
SDRplay receivers get it for free — each panel (A and B) tracks its own noise
floor independently. There is no dual-receiver-specific work.

---

## 1. Copies across (near-verbatim — receiver-agnostic, low risk)

Port these as a **targeted feature port, not a whole-file copy** — YWC's panel
has diverged and a wholesale copy would clobber its differences.

- **Two-stage smoothing**
  - `_applyAveraging(bins, spanHz)` — temporal EMA; re-seeds on first frame,
    bin-count change, or span change.
  - `_spatialSmooth(src)` — spatial moving average (half-window
    `_specSmoothRadius`) into a reused buffer.
  - Constants `DEFAULT_SPEC_AVG` (0.7) and `DEFAULT_SPEC_SMOOTH` (2).
  - The two `update()` lines that call them.
- **Auto noise-floor**
  - `_updateAutoFloor(bins)` — low-percentile floor estimate, EMA-smoothed.
  - `_applyAutoFloorWindow()` — sets `_dbMin/_dbMax` so the floor lands at a
    fixed screen fraction; Range only stretches the peaks above it.
  - `setSpectrumRange(db)` / `getSpectrumRange()`.
  - Fields `_autoFloor`, `_autoFloorDb`, `_rangeDb`.
  - Constants `DEFAULT_SPEC_RANGE` (60), `AUTOFLOOR_PERCENTILE` (0.15),
    `AUTOFLOOR_MARGIN_FRAC` (0.05), `AUTOFLOOR_SMOOTH` (0.1).
  - The `retuned` flag + `_updateAutoFloor()` call in `update()`.
- **Axis-strip trace mapping**
  - `AXIS_H` static (20).
  - `_drawSpectrum` mapping the trace/grid/scale into `specH − AXIS_H` (so the
    floor sits *on* the frequency labels, no dead space above the waterfall).
  - `_drawFrequencyAxis` referencing `AXIS_H` instead of a local `20`.
  - `_niceDbStep(range)` and `_drawDbScale(...)` (right-edge dB axis).

---

## 2. Must be adapted (the real work)

1. **Controls (`Index.cshtml`).** IWC replaced the Low/High slider pair with a
   single **Range** slider and deleted the Floor. Do the same in YWC — **but
   keep YWC's SDR "Gain" slider.** That Gain is a *hardware* control (it changes
   what the SDRplay actually captures) and is a different axis from the display
   Range. IWC removed "Gain" only because the CI-V scope has no such thing; do
   **not** drag that removal across.

2. **[VERIFY] Persistence.** IWC stores `{low, high}` via
   `/api/cat/spectrumdisplay/{vfo}`; YWC uses its own endpoint (likely
   `/api/sdr/dsp/{vfo}`). Reuse whatever YWC has and persist `range` as
   `high − low` against a fixed nominal base (see `wireDspSliders` /
   `PERSIST_BASE` in IWC), so `range` round-trips with **no backend schema
   change**. Confirm the actual endpoint + field names in the YWC tree.

3. **[VERIFY] Waterfall colour must stay on a fixed window.** IWC's `_dbToColor`
   maps against a fixed 120 dB window + the Brightness slider, **not** against
   `_dbMin/_dbMax`. This is now essential: auto-floor moves `_dbMin/_dbMax`
   every sweep, so a waterfall keyed to them would strobe. Verify YWC's
   `_dbToColor` is also fixed-window (it should be, as a clone); if it keys off
   `_dbMin/_dbMax`, switch it to a fixed window / the Brightness slider.

4. **Bin count / smoothing scale.** SDRplay FFTs are far longer than the
   IC-7300's 475 bins. A 5-bin spatial window is a smaller fraction of a
   1024/2048-bin trace and will smooth less visually — bump
   `DEFAULT_SPEC_SMOOTH`, or scale the radius by `bins.length / 475`. Upside:
   the SDR floor is real dynamic range (not CI-V's quantised 0–160), so
   percentile floor-detection works *better*.

5. **Re-seed triggers.** IWC snaps the auto-floor on a **span change** (its CI-V
   span buttons change `spanHz`). YWC's SDR span is fixed by sample rate, so
   that trigger rarely fires. Add a snap (`_autoFloorDb = null`) on:
   - a large **`centreHz` / VFO jump** (band change), and
   - a **hardware Gain change** (a big gain step shifts the whole dBFS floor;
     without a snap the EMA drifts for ~1–2 s before the display re-settles).

---

## 3. Must NOT come across (CI-V-only)

Leave these IWC-only bits out — they are IC-7300 scope concepts with no SDRplay
equivalent:

- The **scope-mode badge**: `_scopeMode`, `_drawScopeModeBadge`,
  `_isOnScopeModeBadge`, `_scopeModeBadgeRect`, the `/api/cat/scopemode` click
  handler, and the CENT/FIX `mode` field on the frame.

---

## 4. Sequence & testing

1. Branch off YWC `develop`.
2. Port the §1 display logic first (isolated, safe) — verify smoothing +
   auto-floor on one receiver.
3. Swap controls Low/High → Range, **keep Gain** (§2.1); wire persistence
   (§2.2).
4. Confirm the waterfall is on a fixed window (§2.3); tune smoothing for the
   higher bin count (§2.4); add the re-seed triggers (§2.5).
5. Manual test (YWC has no automated tests; browser at `http://localhost:8080`):
   - **both** receivers streaming at once, each tracking its own floor;
   - sweep the **Range** slider — floor stays pinned near the bottom, peaks
     scale;
   - change hardware **Gain** and change **band** — floor re-settles promptly;
   - waterfall does **not** strobe as the floor tracks.

**Effort:** roughly one focused session, most of it mechanical. Start by reading
YWC's current `spectrum-panel.js` and the DSP-slider markup in `Index.cshtml` to
resolve the two **[VERIFY]** items before touching code.

---

## Implementation notes (2026-08-01)

The port is on `develop`. Key deviations from the plan above, all forced by YWC's
**server-side DSP** — unlike IWC's raw CI-V scope, YWC's `Yaesu_Sdr_Worker` applies
pre-dB gain, a dB clamp, and a temporal EMA *before* bins reach the browser
(`Services/Sdr/SpectrumProcessor.cs`). That changed three of the plan's assumptions:

- **§1 temporal EMA not ported.** The worker already does the temporal de-flicker
  (EMA α=0.3), so the client adds **only** spatial smoothing (`_spatialSmooth`).
  `_applyAveraging` / `DEFAULT_SPEC_AVG` were intentionally left out — porting them
  would double-smooth.
- **§2.2 persistence — no backend schema change.** Range is a pure *display* knob, so
  it's persisted client-side in `localStorage` (`ywc.spectrumRange.<vfo>`, clamped
  5–160). It never round-trips to the server. `getSpectrumRange()`/`setSpectrumRange()`
  own it; `wireRangeSliders()` in `Index.cshtml` inits the slider from the panel.
- **§2.3 waterfall — already fixed-window.** YWC's `_dbToColor` was already keyed to a
  fixed `(db+120)/120` window, not `_dbMin/_dbMax`, so it needed no change.
- **§2.3 third slider — Gain shortcut, later replaced by a real Bright control
  (2026-08-05).** The first pass took a shortcut: with the trace auto-ranging (a
  uniform dB shift cancels out) the old **Gain** slider no longer moved the trace, so
  it was left in place with only its *tooltip* changed to "waterfall brightness" — the
  visible label still read **Gain**, it still drove the worker's pre-dB `gainLinear`
  over `/api/sdr/dsp`, and it did not match IWC's dedicated **Bright** control. That
  was **not** a faithful port. It has since been done properly, porting IWC verbatim:
  a client-side **`Bright`** slider (0–60 dB, "Off" idle) adds `_wfBrightDb` of lift
  inside the now-instance `_dbToColor` (`(db + _wfBrightDb + 120)/120`), persisted per
  VFO in `localStorage` (`ywc.waterfallBright.<vfo>`) like Range and Speed. The old
  Gain slider and its worker round-trip are **removed**; the control row is now
  **Range / Speed / Bright**, matching IWC's order. `SdrManager`'s spawn push forces
  the worker's pre-dB gain to **unity (1.0)** so "Bright = Off" is the same dark
  baseline for every user.
- **Worker clamp forced wide.** Because the client now does all vertical scaling, the
  worker must stream full dynamic range. `SdrController.SetDspSettings` and
  `SdrManager` (initial spawn push) both force the clamp window to **[-160, 0] dB**,
  ignoring any persisted (possibly stale-narrow) `Low/High`. This auto-migrates
  existing users off the old Low/High settings — the `/api/sdr/dsp/{vfo}` body is now
  just `{ gainLinear }` (the `DbFloor/DbCeiling` fields are accepted but ignored).
- **§2.4 smoothing scale** — `_spatialSmooth` radius scales `DEFAULT_SPEC_SMOOTH·(n/475)`,
  clamped `[1, MAX_SPEC_SMOOTH]`, calibrated for the 1024-bin FFT.
- **§2.5 re-seed triggers** — `snapAutoFloor()` (sets `_autoFloorDb = null`) fires on a
  span/bin-count change (`update()`'s `retuned` flag) and a large VFO jump
  (`AUTOFLOOR_SNAP_JUMP_HZ` in `setVfoFrequency`). (It previously also fired on a Gain
  change; with Gain replaced by the client-side Bright slider — which never shifts the
  dBFS floor — that trigger is gone.)

Not yet done: manual browser verification (both receivers streaming, Range sweep,
band re-settle, Bright sweep, waterfall no-strobe) per §4.5 — awaiting a bench
session.
