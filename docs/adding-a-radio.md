# Adding a new Yaesu radio to YWC

This is the developer companion to the README's "Getting your Yaesu radio added" section. It lists the concrete places in the code that need touching when adding support for another Yaesu transceiver.

## The big picture

**Most of YWC is radio-agnostic.** The CAT command layer, the SignalR pipeline, the meters, the spectrum display and the UI all work the same regardless of model. Adding a radio is mostly a matter of *declaring what's different* about it — not building anything new.

Two anchors to know:

- **`RadioModel`** — the model string (e.g. `"FTDX3000"`, `"FTdx10"`), stored in `appsettings.user.json`. Nearly all per-model behaviour keys off this string.
- **`Services/RadioCapabilities.cs`** — the central hub for per-model capability decisions (receiver count, antenna selector, VC Tune, max power, 4 m availability, meter set). Prefer adding capability logic here over scattering new `RadioModel == "..."` checks.

## Step by step

### 1. Register the model
Add one `<option>` to the model dropdown in `Pages/Settings.cshtml` (the list near the "Radio Model" select). This is what lets a user pick the radio; the chosen string becomes `RadioModel`.

### 2. Declare its capabilities — `Services/RadioCapabilities.cs`
Slot the model into the right buckets:
- `IsDualReceiver` — dual (MAIN/SUB, like the FTdx101) or single receiver. `IsSingleReceiver` is derived.
- `HasAntennaSelector` — one antenna jack (hide the selector) or several.
- `SupportsVCTuneMain` / `SupportsVCTuneSubStatic` — the FTdx101 µ-Tune preselector.
- Max TX power (100 W vs 200 W) and **4 m band availability**.
- Which meters it has — some radios lack Temp / IDD / VDD (gated in `Pages/Index.cshtml` and `Services/MeterPollingService.cs`).
- **Radio Display CAT toolbar** (the radio's own TFT scope via `SS` — not YWC's SDR panel). Slot the model into:
  - `SupportsSpectrumScopeCat` — gate for the toolbar and standalone Radio Scope card. FTdx101 and FTdx10 are on; FT-710 stays off until `scripts/probe/ss-write-probe.ps1` has been run on one.
  - `SupportsScopeHold` — HOLD (`SS` P2=8); the 710's `SS` list stops at 7.
  - `HasPerReceiverScopes` — MAIN/SUB selector; FTdx101 only (`SS` P1 = 0/1).
  - `ScopeSizeLabels` — 101/10: L/N/S; 710: Expand/Normal.
  - `ScopeSpeedLabels` — 101/10: SLOW1…FAST3; 710 adds STOP.
  - `SupportsScopeAfFft` — AF-FFT ATT / OSC ATT / OSC timebase (`SS` P2=7). Does **not** open MULTI.
  - `SupportsScopeMulti` — leave `false` unless a probe names a real CAT frame that toggles the radio-TFT MULTI layout. Do not guess an extra `SS` P2.

  Markup is server-rendered from these flags (`Pages/Shared/_RadioDisplayScopeToolbarPartial.cshtml` and `_RadioScopePartial.cshtml`). See `docs/design/scope-control-via-cat.md`.

### 3. Band coverage
If the radio reaches bands the current line-up doesn't (VHF/UHF, 4 m):
- Extend `BandFreqs` in `Controllers/CatController.cs` (the band → default-frequency map).
- Add/adjust the band buttons (`Pages/Shared/_BandButtonsPartial.cshtml`, `Pages/Index.cshtml`).
- Check the IARU band plans in `wwwroot/js/ui/band-plan.js`.

A plain HF + 6 m radio needs nothing here.

### 4. Calibration — `wwwroot/calibration.default.<Model>.json`
Create a per-model default calibration file. `CalibrationStorage.GetDefaultPath()` loads `calibration.default.<RadioModel>.json` automatically (falling back to the generic `calibration.default.json`), and copies it into the user's `calibration.user.json` on first run.

Seed it by cloning the closest existing radio's file, **then refine it with real numbers from a tester** — the power, S-meter and other meter curves are genuinely radio-specific (raw ADC → real units). A cloned curve is a placeholder, not calibration. Curves are gathered on the in-app **Calibration page**: the tester reads the live "Current RM Value" (raw) against a known reference — a wattmeter for power, the RF/SQL control for the S-meter — and enters value/raw pairs, which YWC saves to `calibration.user.json`; those points then get folded back into the model's default file.

> ⚠️ As of 2026-07, only the FTdx101MP has genuinely-derived calibration; every other model's default file is currently a clone of it (the FTdx10 has its own S-meter curve). Real per-radio calibration arrives from engaged owners over time.

### 5. Handle the CAT quirks
The CAT command handling is shared, but each radio has a few oddities. These live as a handful of `RadioModel == "..."` checks in `Controllers/CatController.cs`, `Controllers/MemoryController.cs`, `Services/RadioInitializationService.cs`, `Services/CatMessageDispatcher.cs` and `Pages/Index.cshtml`. A new radio **inherits the generic path** — add a branch only where it genuinely differs.

The FTDX3000 is the best worked example of how varied these can be:
- No `ST` split command — split is driven via `FT` (which VFO transmits) instead.
- RX-VFO selected via `FR0;` / `FR4;` (0/4), not `VS`.
- **8-digit** `FA`/`FB` frequency values where every other model uses 9 (handled generically now: the width is learned from the radio's own responses in `CatMessageDispatcher` and applied in `CatMultiplexerService.NormalizeFrequencyWidth`).
- A different roofing-filter read-code space vs its set codes.
- Doesn't answer `DT0;` at startup (must not be treated as fatal).
- Doesn't reliably push frequency auto-info over a shared/VSPE link — YWC polls `FA;`/`FB;` as a fallback on single-receiver radios (`MeterPollingService`).

You discover these from the radio's CAT manual (in `docs/manuals/`) plus the tester's "this works / this reads wrong" reports.

### 6. Documentation
- README "Supported transceivers" table.
- `USER_MANUAL.md` — model references (receiver count, meters, bands, per-model notes).

### 7. Test against the real radio
This is the actual verification, and the true gate — see below.

## Effort, realistically
- A **close cousin** (e.g. FT-991A — same command family) is mostly steps 1–2 plus calibration.
- A **VHF/UHF radio** (e.g. FTX-1) also needs the step 3 band work.
- Step **5 is the iterative part**, driven entirely by a tester's reports. The FTDX3000 arc — split fix → frequency-width fix → power calibration — all came from one engaged owner (iu1teu) testing pre-releases.

## The scarce ingredient is a tester, not code
Steps 5 and 7 cannot happen without an owner of the radio who will run YWC and report what works. That's why the README frames it as *"ordinary operating, not programming."* Adding the scaffolding (steps 1–4) is quick; making the radio genuinely solid needs someone with it on the bench. Prioritise models that have an engaged owner behind them.
