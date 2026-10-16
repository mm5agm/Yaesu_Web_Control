# FT-991A support - plan

Status: planned 2026-10-05. Tester: BA4RTS (GitHub y010204025), Discussion #86.

## Why now

BA4RTS ran v2.5.3-pre5 on a real FT-991A with the model set to FT-710, and all six
basic checks passed (connect, dial follows, band/frequency writes, mode both ways,
S-meter, PTT into a dummy load). The diagnostics log is
`diag-2026-10-04T17-22-29-903Z.txt` on #86. So the CAT basics are proven, and the
work is to make the 991A its own model rather than a radio posing as an FT-710.

Source for everything below: `docs/manuals/FT-991A_CAT_OM_ENG_1711-D.pdf`.
Nothing here is bench-verified until BA4RTS has run it.

## What is already in the code

- `RadioCapabilities`: frequency range 30 kHz - 470 MHz, 100 W, single receiver,
  no antenna selector.
- `wwwroot/data/audio-filter-ex-map.json`: a flat-EX FT-991A map. Checked against
  the manual: SSB 102-105, AM 041-044, DATA 066-069, RTTY 092-095, CW 050-053 all
  match. FM has no LCUT/HCUT menu on the 991A, so null is right.
- `site.js`: max power 100 W.
- **Missing:** the model is not in the Settings dropdown, so nobody can select it.

## Differences from the FT-710 that matter

| Area | FT-991A (manual) | What YWC does today | Action |
|---|---|---|---|
| ID | `ID0670;` | - | Log it at connect; warn on mismatch, as for other models |
| FA/FB | 9 digits, 30 kHz-470 MHz | 9 digits | Nothing |
| MD code `E` | **C4FM** | read as **PSK** | Model-aware mode map: `E` = C4FM on the 991A |
| MD codes A/B/D | DATA-FM / FM-N / AM-N | same | Nothing; offer them in the mode list |
| SD (break-in delay) | 4-digit ms, 30-3000 | 2-digit step on all but FTDX3000 | Treat 991A like FTDX3000 in `CwBreakInCodes` |
| BK-IN TYPE | EX056 (0 semi, 1 full) | Full shown on '101 only | Add EX056 so Semi/Full works |
| RM meters | RM1 S, 3 COMP, 4 ALC, 5 PO, 6 SWR, 7 ID, 8 VDD; **no RM9** | polls RM9 every 2 s | Skip RM9; hide Temperature; keep IDD/VDD |
| MS (meter select) | 0 COMP 1 ALC 2 PO 3 SWR 4 ID 5 VDD | MS13 borrow on '101 only | Nothing |
| SH widths | own table, Narrow/Wide via `NA` | FT-710 table | New 991A width tables (C# + JS) |
| Roofing filter | none over CAT | - | Hide roofing section |
| CAT scope (`SS`) / native scope | none | gated by model | Nothing; stays off |
| RTTY | EX100 shift (170/200/425/850), EX101 mark (1275/2125), EX097/098 polarity | no entry | Add to `RttyToneMap` (flat EX) |
| CONTOUR/APF | `CO0` P2 0-3, APF -250..+250 | per-model | Check against FTDX3000 path |
| Memories | 001-099 + P-1L..P-9U (100-117), MT with 12-char tag | FT-710 path | Check MemoryController channel range |
| Power | PC 005-100; 144/430 MHz max 50 W (EX139/140) | 100 W everywhere | Scale to 50 W on 2 m / 70 cm |
| Bands | HF, 6 m, **2 m, 70 cm**; BS 15 = 144, 16 = 430; no 4 m | 160 m-4 m buttons for every model | 2 m + 70 cm buttons on the 991A; 4 m hidden |
| FM repeater | OS, CT, CN (CTCSS/DCS) | `FmShiftDir`, `CtcssMode` already read | Check on 2 m |
| Clarifier | RT / XT / RU / RD / RC | same as FTDX3000 | Check |

## Phases

1. **Selectable model.** Dropdown, About, Settings notes, capability gates (no
   roofing, no Temperature, no RM9 poll), `E` = C4FM, break-in in ms + EX056,
   2 m / 70 cm band buttons, 50 W scaling above 144 MHz. Unit tests for each table.
2. **Filter widths.** SH table + NA narrow/wide, in `YaesuIfWidth`,
   `if-width-tables.js` and `filter-scope-panel.js`.
3. **EX-driven features.** RTTY tone map, contour/APF, memories range.
4. **Tester checklist** for BA4RTS, one test at a time, in the order above.
   Update `docs/testing/ft-991a-test-plan.md` from "pose as FT-710" to "select
   FT-991A".

## Open questions for the tester

- The log shows the VFO A frequency flicking between 21.2699 and 21.2698 MHz about
  50 times in 2 seconds. Was the dial being turned, or was it left alone?
- Which region is the radio (China / Asia version)? It decides which bands the
  radio allows.
