# Plan — Add the Yaesu FTDX5000 as a dual-receiver model

**Status:** Draft / proposed — 2026-07-27
**Driven by:** [Discussion #85](https://github.com/mm5agm/Yaesu_Web_Control/discussions/85) — Jim K2QB, FTDX5000 owner (can connect via another Yaesu profile but can't change bands)
**Companion to:** [docs/adding-a-radio.md](../adding-a-radio.md), [ADR 0003 — single vs dual receiver UI](../decisions/0003-single-vs-dual-receiver-ui.md)

---

## One-line framing

**The FTDX5000 is a 101MP-class *dual-receiver* radio (MAIN + SUB, `P1=0/1` on every RX control, dual S-meter) that happens to use *FTDX3000-class* 8-digit frequencies.** Both of those halves already exist in the codebase — so this is mostly *declaring* the radio, not building new machinery. It's the closest match to Colin's own FtdX101MP of any radio we've added, which makes it unusually low-risk to scaffold.

## Why the FTDX5000 maps onto the 101MP so well (evidence)

From `docs/manuals/FTDX5000_CAT_OM_ENG_1907-D.pdf`, the RX-control command family is the same one the 101MP dual-receiver layout was built for, with the **same MAIN/SUB addressing**:

| Control | Command | FTDX5000 P1 | Matches 101MP? |
|---|---|---|---|
| AGC | `GT` | `0: Main (VFO-A) / 1: Sub (VFO-B)` | ✅ |
| S-meter | `SM` | `0: Main S-meter / 1: Sub S-meter` | ✅ |
| RF Attenuator | `RA` | Main/Sub | ✅ |
| RF Gain | `RG` | Main/Sub | ✅ |
| Roofing filter | `RF` | present | ✅ |
| IF width / shift | `SH` / `IS` | present | ✅ |
| NR / NB / NR level | `NR` / `NB` / `NL` | present | ✅ |
| Auto notch / manual notch | `BC` / `BP` | present | ✅ |
| Contour | `CO` | present | ✅ |
| Swap / VFO select | `SV` / `VS` | present | ✅ |
| RX/TX VFO | `FR` / `FT` | present | ✅ |

Meters: the FTDX5000 exposes **PO / COMP / ALC / SWR / IDD / VDD** via `RM` (`RM3`=COMP, `RM4`=ALC, `RM5`=PO, `RM6`=SWR, `RM7`=ID, `RM8`=VDD) plus dedicated `SM0`/`SM1` for MAIN/SUB S-meter — i.e. **the full 101MP meter set except PA Temperature** (there is no temp meter in the `RM` table).

Frequency: `FA` / `FB` take **8 digits** (`00030000 – 60000000 Hz`) — identical to the FTDX3000, unlike the 9-digit FTdx10/FT-710/101. Coverage is **HF + 6 m only** (no 4 m, no VHF/UHF).

## What we get for free (already capability-gated — no new code)

Because the dual-receiver behaviour keys off `RadioCapabilities.IsDualReceiver()` rather than hard-coded model names, flipping that one flag lights up:

- **Dual S-meter.** `MeterPollingService` polls `SM1` for the SUB receiver whenever `IsSingleReceiver` is false ([MeterPollingService.cs:163](../../Services/MeterPollingService.cs#L163)) — otherwise it mirrors SMeterA→B. Flag → SUB S-meter starts updating with zero new polling code.
- **Two-receiver UI layout.** `Pages/Index.cshtml` renders the parallel MAIN/SUB VFO panels (each with its own RX controls) for dual-receiver radios, and the greyed active/standby single-receiver layout otherwise (ADR 0003).
- **MAIN/SUB CAT routing.** `RadioCapabilities.VfoP1()` / `VfoIsB()` already emit `P1=0` for A / `P1=1` for B on dual-receiver radios; single-receiver forces `0`. Shared by `CatController` (mouse/keyboard) and `IntentDispatcher` (voice).
- **8-digit frequency writes.** `CatMessageDispatcher` learns each radio's native FA/FB width from its own responses; `CatMultiplexerService.NormalizeFrequencyWidth` reformats writes to match. This is exactly the FTDX3000 fix (v2.4.2-pre18→pre20) — it is **model-agnostic**, so the FTDX5000's 8-digit format is handled the moment the radio's first FA response is seen. **This is the fix for Jim's band-change problem** (band change *is* an FA write — [CatController.cs:496](../../Controllers/CatController.cs#L496)).

## What needs adding (step by step)

Following `docs/adding-a-radio.md`:

### 1. Register the model
Add `<option>FTDX5000</option>` to the Radio Model select in `Pages/Settings.cshtml`.

### 2. Declare capabilities — `Services/RadioCapabilities.cs`
- **`IsDualReceiver`**: add `"FTDX5000"` to the dual-receiver arm:
  ```csharp
  "FTdx101MP" or "FTdx101D" or "FTDX5000" => true,
  ```
- **`HasAntennaSelector`**: leave default `true` — the FTDX5000 has multiple antenna jacks (`AN` + ANT 1/2/3).
- **VC Tune** (`SupportsVCTuneMain` / `SupportsVCTuneSubStatic`): leave `false` — the FTDX5000 has no 101-style µ-Tune preselector exposed over `VT`. (Its VRF/`µ-TUNE`/`VF` is a different control; out of scope for v1.)

### 3. Meters it lacks — gate PA Temperature off
The FTDX5000 has **no temperature meter**. In `Pages/Index.cshtml` the Temp gauge is gated `@if (!isFtdx10 && !isFt710)`; add an `isFtdx5000` and extend the gate. Keep IDD/VDD (the FTDX5000 *does* have them, unlike the FtdX10/FT-710 — see [[project_radio_meter_availability]]). Best done as a small capability (`HasTemperatureMeter`) rather than another scattered model string, per adding-a-radio guidance.

### 4. Band coverage — suppress 4 m
The FTDX5000 is HF + 6 m only. 4 m is currently offered by region (`band-plan.js`) and appears for Region-1 radios like the 101MP. Add a `Supports4m` capability (`false` for FTDX5000) and gate the 4 m band button so it never shows for this model regardless of region. No new *bands* to add (HF+6 m already covered).

### 5. Calibration — `wwwroot/calibration.default.FTDX5000.json`
Clone the closest existing file **as a placeholder**, then refine with **real numbers from Jim**. The FTDX5000 is a 200 W radio (like the 101MP) — so cloning the 101MP power curve is a *better* starting guess than the 100 W FTDX3000's, but still a placeholder until Jim calibrates against a wattmeter (power) and RF/SQL (S-meter) on the in-app Calibration page. Two S-meters (MAIN/SUB share the same curve).

### 6. Documentation
- README "Supported transceivers" table + USER_MANUAL model references (dual-receiver, meter set = 101MP minus Temp, HF+6 m, 200 W).

## CAT quirks specific to the FTDX5000 (the iterative, tester-driven part)

These are the places a generic dual-receiver radio may still differ — to be confirmed against Jim's real hardware (same method as the FTDX3000 arc):

1. **Meter read path.** The SWR/compression poll has two branches ([MeterPollingService.cs:224](../../Services/MeterPollingService.cs#L224)): the 101MP uses `MS13`+`RM0`; everything else reads `RM3` (COMP) + `RM6` (SWR) directly. The FTDX5000's `RM` codes (`RM3`=COMP, `RM6`=SWR) match the **else** branch — so the FTDX5000 should *not* be added to `useRm0Pair`. Verify SWR/COMP read correctly; if not, that's the first quirk to chase.
2. **Frequency auto-info.** The FA/FB **poll backstop is gated to single-receiver radios** ([MeterPollingService.cs:301](../../Services/MeterPollingService.cs#L301)); dual-receiver radios (101MP) are left event-driven because their auto-info push is reliable. **Risk:** if the FTDX5000 doesn't push FA/FB auto-info reliably (older CAT engine), radio→web frequency sync could lag. Over a *direct USB* link it's likely fine (as the 101MP is for Colin); over a shared/VSPE link it may need the poll backstop extended to it. Watch Jim's first logs. (The web→radio direction is already fixed by the 8-digit normaliser.)
3. **Startup handshake.** Confirm the FTDX5000 answers `AI`, `ID`, and the init burst cleanly; the FTDX3000's "doesn't answer `DT0;`" quirk is already tolerated non-fatally, but the FTDX5000 is a different vintage — check the init sequence in a real connect log.
4. **`BS` band select exists** (`O X X X`) on the FTDX5000 but YWC deliberately drives band via frequency writes; no action needed — noted only so we don't get tempted to special-case it.
5. **Sub-receiver enable / dual-watch.** The FTDX5000's SUB receiver may need to be powered/enabled (front-panel or a CAT state) before `SM1` returns live data. If SUB S-meter reads flat, check whether SUB is actually switched on. Document for users.

## Phasing

- **Phase 1 — scaffold (safe, no radio needed):** steps 1–5 above (model option, `IsDualReceiver`, Temp gate, 4 m gate, placeholder calibration clone). Builds and runs; presents the full dual-receiver UI. This is a small, self-contained commit set.
- **Phase 2 — hardware bring-up with Jim (gated on his engagement):** point him at a build with FTDX5000 selectable; validate band change + tuning (should already work via the 8-digit normaliser), dual S-meter (MAIN + SUB), all TX meters, split/RX-TX VFO, mode/roofing/AGC per receiver. Chase quirks 1–5 from real logs. Same pre-release iteration loop that took the FTDX3000 from "can't change bands" to "victory".
- **Phase 3 — calibration + ship:** fold Jim's power + dual S-meter numbers into `calibration.default.FTDX5000.json`; README/USER_MANUAL; release.

## The scarce ingredient

Per [[feedback_focus_active_radios]]: the scaffolding is a day's work, but genuine FTDX5000 support needs Jim (or another owner) on the bench reporting what reads right and what doesn't. Jim has stepped up on #85, which is what unlocks this — but if he goes quiet after Phase 1, park it there rather than chasing ([[feedback_no_chasing_silent_reporters]]). The scaffold is cheap and harmless to leave in place either way.

## Interim answer for Jim (already actionable, no new model needed)

Even before any of the above, the **existing** 8-digit normaliser means Jim's band-change problem should be fixed by running **v2.4.2-pre21** and selecting the **FTDX3000** profile (same 8-digit format). The dual-receiver work above is what turns that workaround into a proper FTDX5000 experience (SUB receiver visible, correct meter set, right band limits). This is exactly what the #85 reply asked him to test.
