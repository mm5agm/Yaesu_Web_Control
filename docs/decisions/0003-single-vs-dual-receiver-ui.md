# ADR 0003 — Per-model UI layout for single-receiver vs dual-receiver radios

**Status:** SUPERSEDED — 2026-06-13. See "Revision" section below.
**Decision-makers:** Colin (MM5AGM), with implementation planning support
**Driven by:** [Issue #34](https://github.com/mm5agm/Yaesu_Web_Control/issues/34) (Jacek SP3L) — FTdx10 VFO A/B swap UI mismatch

---

## Revision (2026-06-13) — empirical correction from real-hardware testing

The analysis below concluded that single-receiver radios (FTdx10, FT-710, FTDX3000) have only **one set** of RX controls, based on the CAT manual showing `P1: 0 (Fixed)` for every receiver-side command. **That conclusion was wrong.**

Jacek SP3L (issue #34, [follow-up comments](https://github.com/mm5agm/Yaesu_Web_Control/issues/34) on 2026-06-13) verified on his FTdx10 that the radio **does** store per-VFO state for ATT, IPO, AGC, NR, NB, IF Width and the rest — and the settings travel with the VFO across swap. Example: set ATT=6dB / IPO=AMP1 on VFO-A, switch to VFO-B, set ATT=12dB / IPO=AMP2, switch back to VFO-A → both values return to 6dB / AMP1.

The CAT manual's `P1: 0 (Fixed)` was telling us about the **command parameter format** ("the CAT interface has no parameter slot to pick which VFO"), not the radio's **internal state model** ("the radio stores one set of values"). The CAT command always implicitly addresses the currently-active VFO.

This is a classic case of "the manual is technically accurate but misleading about the deeper model" — and exactly the kind of thing only empirical hands-on hardware testing surfaces. Jacek's testing on his own FTdx10 caught the misreading before any of the architectural refactor work started — saving a substantial multi-session implementation that would have been wrong.

### Revised fix (replaces §Decision below)

YWC's current dual-VFO layout (each section holds its own RX controls) is actually **correct** for single-receiver radios too — it maps the radio's real state model. What's missing is active-VFO awareness:

1. Track which VFO is currently active on the radio (read on connect via `FT;` or equivalent, listen for swap events via auto-information)
2. Grey out the inactive VFO panel visually
3. Disable the controls in the inactive VFO panel — editing one via CAT would silently swap the radio's active VFO, which is jarring UX
4. PTT button placement follows the active VFO (still right — only one transmitter on single-receiver radios)

No shared "Receiver Controls" panel. No layout split. No template refactor. The change is essentially a CSS/JS layer over the existing layout plus the active-VFO state tracking.

### What this means for the implementation plan in §Implementation below

Steps 2–4 of the original plan (extract partials, build shared layout, rewire JS for P1 routing) are **dropped**. Step 5 (active/standby CSS + SignalR wiring) remains, and step 1 (a tiny `RadioCapabilities.IsDualReceiver` check) still useful — dual-receiver radios don't need the disable-inactive behaviour, so the capability lookup is still the gate. The overall investment drops from ~6 commit-sized steps to perhaps 2–3.

### Why this revision is kept inline rather than as ADR 0004

ADR practice often prefers a new ADR superseding the old one. Here the correction is targeted (it changes the conclusion, not the architectural framing) and the historical analysis below remains valuable as a record of how the misreading arose. Keeping the revision inline at the top, with the original text preserved below, makes it easier for future readers to see what was wrong and why.

---

## Original analysis (2026-06-12) — retained as historical record

> **The conclusions in the rest of this document are wrong.** See the Revision above for the corrected understanding. The text below is retained because the CAT-manual evidence it cites is real, the reasoning is traceable, and future readers may need to understand how the misreading happened.

## Context

YWC's current home-page layout is **two parallel VFO sections**, each containing its own:

- Frequency display
- Mode selector
- IPO / AMP (preamp)
- AGC selector
- Roofing filter (where applicable)
- IF width / IF shift
- RF Gain
- Attenuator
- Noise reduction / Noise blanker / Auto notch
- Speech processor controls (TX side)

Plus a "TX / PTT" indicator typically anchored to the VFO A section.

That layout was designed for the FTdx101MP/D — the only radio model originally supported, and one that genuinely has **two independent physical receivers** (MAIN and SUB) each with its own RX signal chain. Adding support for the FTdx10, FT-710, and FTDX3000 stretched the layout to single-receiver radios without revisiting the per-VFO assumption. Jacek's #34 report exposed the mismatch in everyday operation: on his FTdx10, pressing the radio's A/B swap button caused the "VFO B controls" to appear in YWC's VFO A section but the frequency display to stay correctly anchored — incoherent visual behaviour because the underlying receiver model is fundamentally different.

## Evidence from the Yaesu CAT manuals

The radio-receiver geometry is documented unambiguously in Yaesu's CAT Operation Reference Manuals. Look at the **P1 (band selector) parameter** on every RX-control command across the three models YWC currently supports for dual-VFO display:

### Dual-receiver radios — FTdx101MP / FTdx101D

| CAT command | P1 parameter |
|---|---|
| `PA` (IPO / Pre-amp) | `0: MAIN Band` / `1: SUB Band` |
| `GT` (AGC) | `0: MAIN` / `1: SUB` |
| `RA` (RF Attenuator) | `0: MAIN` / `1: SUB` |
| `RG` (RF Gain) | `0: MAIN` / `1: SUB` |
| `RL` (NR Level) | `0: MAIN` / `1: SUB` |
| `RF` (Roofing Filter) | `0: MAIN` / `1: SUB` |
| `SH` (IF Width) | `0: MAIN` / `1: SUB` |
| `SM` (S-Meter) | `0: MAIN` / `1: SUB` |
| `SQ` (Squelch) | `0: MAIN` / `1: SUB` |
| `NR` (NR On/Off) | `0: MAIN` / `1: SUB` |
| `NB` (Noise Blanker) | `0: MAIN` / `1: SUB` |
| `BC` (Auto Notch) | `0: MAIN` / `1: SUB` |
| `OS` (Repeater Shift) | `0: MAIN` / `1: SUB` |

Every receiver-side control is independently addressable per band. The radio has two physically separate RF/IF/DSP chains.

The `SV` (swap VFO) command's documented effect: *"Exchanges the MAIN band and SUB band frequency data."* **Frequency only** — never mode or RX state, because those belong to the physical receiver chain and don't move when frequencies swap.

### Single-receiver radios — FTdx10, FT-710

Same family of commands. Same P1 column. Different value:

| CAT command | P1 parameter |
|---|---|
| `PA` (IPO / Pre-amp) | `0: Fixed` |
| `RA` (RF Attenuator) | `0: Fixed` |
| `RG` (RF Gain) | `0: Fixed` |
| `RL` (NR Level) | `0: Fixed` |
| `RF` (Roofing Filter) | `0: Fixed` |
| `SH` (IF Width) | `0: Fixed` |
| `SM` (S-Meter) | `0: Fixed` |
| `SQ` (Squelch) | `0: Fixed` |
| `NR` (NR On/Off) | `0: Fixed` |
| `NB` (Noise Blanker) | `0: Fixed` |

There is **no band parameter on any RX control**. The single receiver has one set of these settings. VFO A and VFO B exist only as **frequency + mode memory slots** that select where the single receiver is pointed.

The FTDX3000 follows the same single-receiver pattern (verified separately).

## The decision

YWC's home-page layout must differ by radio class:

### A. Dual-receiver layout (FTdx101MP, FTdx101D) — keep current design

Two parallel VFO sections each containing the full RX control set. No change required — the layout maps directly onto the radio's hardware. The `SV` swap only moves frequencies; mode and RX controls stay anchored to their physical receiver section, which is the correct visual behaviour.

### B. Single-receiver layout (FTdx10, FT-710, FTDX3000) — new design

Decompose the UI into:

1. **A single shared "Receiver controls" panel** containing the RX settings that the radio has only one of: IPO / AGC / RF Gain / Attenuator / IF Width / IF Shift / Roofing Filter / NR / NB / Auto Notch / Squelch.

2. **Two slim VFO panels** showing only what's genuinely per-VFO at the CAT level: frequency display, mode selector, and an active/standby indicator. Click-to-tune and spectrum-driven tuning continue to address VFO A or VFO B by frequency.

3. **The PTT button follows the active VFO.** On single-receiver radios there is only one transmitter — it transmits at the active VFO's frequency. Anchoring PTT to "VFO A section" misrepresents the radio. Logically PTT belongs in the Receiver panel (since there's only one of those, too), with the active-VFO indicator showing where the transmit frequency will come from.

4. **Visual active/standby distinction.** The active VFO gets the prominent white-background panel; the standby VFO is rendered in grey/muted styling. Exception: in split operation (TX on one VFO, RX on the other), both panels show as active because both are "in use" — just for different purposes.

5. **"VFO A" / "VFO B" labels stay** rather than re-labelling to "Active" / "Standby". Operators see the A/B labels on their radio's front panel and on every Yaesu CAT response (`FA;` / `FB;`); changing the label would create unnecessary cognitive translation.

### C. The model-detection point

Settings already has `RadioModel` — the same field that drives per-model calibration, per-model roofing filter lists, and `MeterPollingService`'s decisions about which CAT meter commands to send. Re-use it. The home-page Razor view picks one of two partial views (or one of two CSS classes on the same template) based on `RadioModel`'s receiver class.

A small static lookup is sufficient:

```csharp
public static class RadioCapabilities
{
    public static bool IsDualReceiver(string radioModel) => radioModel switch
    {
        "FTdx101MP" or "FTdx101D" => true,
        "FTdx10" or "FT-710" or "FTDX3000" => false,
        _ => false  // safe default — assume single-receiver
    };
}
```

This pattern (a tiny capability lookup keyed on radio model) sets up nicely for future per-model differences too (e.g. 4-metre band availability, max TX power, roofing-filter installation status).

## Consequences

**Positive:**

- Single-receiver users see a UI that matches their radio. Jacek's #34 bug becomes a non-issue because there are no duplicate per-VFO RX controls to get out of sync.
- The shared Receiver panel becomes the natural home for the `MeterPollingService`-forced meter switching (ADR-relevant for the eventual hardware-meter-follows-calibration-page feature on v2.3.7+).
- Layout self-documents the radio's actual capabilities — operators new to YWC see at a glance what's per-VFO and what's radio-wide.
- The PTT-follows-active-VFO behaviour matches what every Yaesu operator already expects from the rig's front panel.

**Negative:**

- Two distinct layouts to maintain. Future per-control changes need to be applied to both unless we factor common pieces into shared partials.
- Existing single-receiver users (Jacek, the v2.3.6 FTdx10 cohort) will see a noticeably different UI after v2.3.7 update. The release notes need to call this out clearly to avoid "where did my controls go?" reports.
- Adding a new dual-receiver model (e.g. an eventual FTdx101D2) is one capability-lookup line. Adding a new single-receiver model is also one line. Adding a model with a *new* receiver topology (e.g. genuine multi-receiver beyond MAIN/SUB) would require a third layout — but no such Yaesu rig exists today.

**Out of scope:**

- Memory operations remain a per-VFO concept on both single- and dual-receiver radios (the radio does store separate memory slots for each VFO). No change there.
- TX-side controls (mic gain, processor, AMC) are radio-wide on all supported models — they stay in their current shared location.
- Split-mode handling is mentioned above but the detailed UI for split (which VFO is RX, which is TX, frequency offset display) is its own future design discussion. ADR scope here is the receiver-controls layout, not the split workflow.

## Implementation plan for v2.3.7

1. Add `Services/RadioCapabilities.cs` with `IsDualReceiver(radioModel)`.
2. Refactor `Pages/Index.cshtml` into two layout branches keyed on that capability check. Most existing per-VFO control HTML moves into a new "Receiver Controls" shared partial used by both layouts (dual renders two of them — one per band; single renders one).
3. Update the home-page JS to send the right CAT P1 parameter per radio class: `0` for the only band on single-receiver, `0`/`1` per active section on dual-receiver.
4. Move PTT button placement: under shared Receiver panel on single-receiver layouts; per-VFO-section on dual-receiver layouts (unchanged from today).
5. Add active/standby visual styling — `.vfo-active` / `.vfo-standby` CSS classes wired up via the `FT;` (TX VFO) and any VFO-select state SignalR updates.
6. Update `USER_MANUAL.md` to describe the two layouts side-by-side with screenshots after implementation.

## Related decisions and references

- [Issue #34](https://github.com/mm5agm/Yaesu_Web_Control/issues/34) — the bug report driving this design
- [ADR 0001](0001-dual-sdr-architecture.md) — separate Yaesu_Sdr_Worker process per SDR; same per-model-capability spirit applied to the SDR side
- Yaesu CAT Operation Reference Manuals — `docs/manuals/FTDX101MP_D_CAT_OM_ENG_2308-L.pdf`, `FTDX10_CAT_OM_ENG_2308-F.pdf`, `FT-710_CAT_OM_ENG_2306-C.pdf` (the evidence cited above is reproducible from these PDFs)
