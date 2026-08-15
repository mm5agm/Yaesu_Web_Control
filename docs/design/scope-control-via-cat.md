# Driving the radio's own display over CAT

**Status:** DESIGN NOTE — nothing implemented. Raised by Colin MM5AGM
2026-08-15.
**Bench evidence:** `SS` read-probe run against a real FTdx101MP (ID0682) on
COM4, 2026-08-15. Read frames only; no `Set` frame was sent. Results are in
§2.
**Related:** PR #97 (USB video capture of the radio's screen), and the
overlay idea this note replaces.

---

## 1. The idea, and why it beats overlays

The problem this started from: with USB video capture we can *see* the radio's
own TFT in the browser, but we cannot *touch* it. The obvious fix was to draw
YWC controls as an overlay on top of the video — clickable regions positioned
over the radio's on-screen buttons.

That approach has a nasty long tail. The overlay has to know where every
button sits in the captured frame, which varies by radio model, by firmware
screen layout, by capture resolution, and by whatever the operator has the
radio currently displaying. It is pixel geometry pretending to be a UI.

**The alternative: don't touch the radio's screen, command the radio.** Yaesu
exposes the scope and meter controls over CAT. Send the command, the radio
changes its own display, and the video capture shows the result because it is
showing the radio's actual screen. No compositing, no coordinates, no
per-model pixel maps.

Two things follow that are worth stating plainly:

- **This does not replace the video work.** You still need the capture to see
  the screen. What it removes is the overlay layer specifically, which was
  always going to be the fragile part.
- **This is worth having without video at all.** Remote scope and meter
  control stands on its own for anyone operating the rig from another room.
  That is a better justification than the video framing, and it reaches more
  people — every user with a supported radio, not only those with a capture
  dongle.

---

## 2. What the radio actually answered

Probe script: `scripts/probe/ss-probe.ps1` (read-only). Radio: FTdx101MP,
firmware ID `0682`, 38400 8-N-2 on COM4, YWC stopped.

```
--- SS P1=0 (MAIN) ---
  SS00;   SPEED        -> 'SS0020000;'     2 = FAST1
  SS01;   PEAK         -> 'SS0100000;'     0 = LV1
  SS02;   MARKER       -> 'SS0210000;'     1 = ON
  SS03;   COLOR        -> 'SS0341100;'     P3=4, P4=1, P5=1
  SS04;   LEVEL        -> 'SS04+05.0;'     +5.0 dB
  SS05;   SPAN         -> 'SS0590000;'     9 = 1 MHz
  SS06;   MODE         -> 'SS0660000;'     6 = W/F CURSOR (L)
  SS07;   AF-FFT/OSC   -> 'SS0711200;'
  SS08;   HOLD         -> 'SS0800000;'     0 = OFF

--- SS P1=1 (SUB) ---
  SS15;   SPAN         -> 'SS1580000;'     8 = 500 kHz
  SS16;   MODE         -> 'SS1620000;'     2 = 3DSS FIX
  (remaining sub-commands answered identically in shape)
```

Three things this establishes that the manual alone did not:

1. **The frame is `SS` P1 P2 P3P4P5P6P7 `;` — 10 characters.** The CAT manual's
   table is mangled by the PDF layout and it was not obvious whether P3–P7 were
   five separate one-character fields or one five-character field. `SS04+05.0;`
   settles it: they are **one 5-character field**, of which most sub-commands
   use only the first character and pad the rest with `0`.
2. **`P1` really does address MAIN and SUB independently.** MAIN was on 1 MHz
   W/F CURSOR while SUB was on 500 kHz 3DSS FIX at the same moment. This is a
   genuine per-receiver control on the FTdx101, not a documented-but-ignored
   parameter.
3. **Every sub-command answered, including `HOLD` (P2=8).** No CAT errors, no
   timeouts.

**Not yet verified: writes.** Every frame above is a Read. No `Set` frame has
been sent to any radio. See §6.

---

## 3. Per-radio capability matrix

Derived from the CAT manuals in `docs/manuals/`. Only the FTdx101MP column is
hardware-confirmed.

| Function | FTdx101MP/D | FTdx10 | FT-710 | FTDX3000 | FTDX5000 |
|---|---|---|---|---|---|
| Meter pair (`MS`) | yes | yes | yes | yes | yes |
| Span (`SS` P2=5) | yes | yes | yes | menu only | no |
| Scope mode / 3DSS (`SS` P2=6) | yes | yes | yes | no | no |
| Fix / Center / Cursor (`SS` P2=6) | yes | yes | yes | menu only | no |
| Expand | L/N/S | L/N/S | EXPAND/NORMAL | no | no |
| Hold (`SS` P2=8) | yes | yes | **no** | no | no |
| Marker (`SS` P2=2) | yes | yes | yes | no | no |
| Peak / Level / Colour / Speed | yes | yes | yes | no | no |
| MAIN vs SUB scope | `P1`=0/1 | n/a | n/a | n/a | n/a |
| **Mono / Multi** | **no** | **no** | **no** | **no** | **no** |

### The four differences that matter

**Mono/Multi is not in CAT on any radio.** Searched all seven CAT manuals for
the term; there is no command and no menu entry. This is the one item on the
original wish-list that stays front-panel-only. If remote Mono/Multi is
essential, an overlay click-target over the video is the only route, and that
is a much smaller overlay than the original proposal.

**The FT-710 has no HOLD.** Its `SS` P2 list stops at 7; the FTdx101 and FTdx10
go to 8. Same command, genuinely fewer functions — not an omission in the
manual.

**The FT-710 spells Expand differently.** FTdx101/FTdx10 offer W/F CENTER
(L)/(N)/(S) — three sizes. The FT-710 offers W/F CENTER (NORMAL)/(EXPAND) — two.
Same concept, incompatible value tables, so the mode selector needs a per-model
map rather than one shared enum. Do not try to unify these into one list.

**FTDX3000 and FTDX5000 are effectively out of scope.** The FTDX3000 has no
`SS` command at all; its scope lives in menu items 124–148 reachable via `EX`,
which is writing configuration rather than operating a control. The FTDX5000
has only a `DP` display-page selector. Neither has an engaged reporter (see
`.claude/rules.md` on focusing effort on actively-reported radios), so neither
should gate this work.

> **Caution on the FTDX3000 menu numbers.** The 124–148 range is legible in the
> extracted text, but the value columns beside them are visibly bleeding in from
> an adjacent table in the PDF (a "SCOPE FIX 3.5MHz SPAN" row showing
> "0: NARROW 1: WIDE" is not credible). If FTDX3000 support is ever attempted,
> re-read those menu entries from the PDF directly rather than trusting any
> extraction.

---

## 4. Command reference

### `SS` — SPECTRUM SCOPE

```
Set     SS P1 P2 P3P4P5P6P7 ;      (10 chars)
Read    SS P1 P2 ;                 (5 chars)
Answer  SS P1 P2 P3P4P5P6P7 ;      (10 chars)
```

`P1` — `0` = MAIN band, `1` = SUB band on the FTdx101. Fixed `0` on FTdx10 and
FT-710.

`P2` selects the sub-command; `P3`–`P7` is a single 5-character value field,
normally one significant character followed by `0000`.

| P2 | Sub-command | P3 values |
|---|---|---|
| 0 | SPEED | 0 SLOW1, 1 SLOW2, 2 FAST1, 3 FAST2, 4 FAST3 (FT-710 adds 5 STOP) |
| 1 | PEAK | 0 LV1 … 4 LV5 |
| 2 | MARKER | 0 OFF, 1 ON |
| 3 | COLOR | P3 0–A colour 1–11; P4 0–6 narrow-band colour; P5 0/1 narrow-band colour on |
| 4 | LEVEL | five chars, `-30.0` … `+30.0`, 0.5 dB steps |
| 5 | SPAN | 0 1 kHz, 1 2 kHz, 2 5 kHz, 3 10 kHz, 4 20 kHz, 5 50 kHz, 6 100 kHz, 7 200 kHz, 8 500 kHz, 9 1 MHz |
| 6 | MODE | see below |
| 7 | AF-FFT / OSCILLOSCOPE | P3 FFT att, P4 osc level att, P5 osc timebase |
| 8 | HOLD | 0 OFF, 1 ON — **absent on FT-710** |

`P2=6` (MODE) — **the value table differs by model:**

| P3 | FTdx101 / FTdx10 | FT-710 |
|---|---|---|
| 0 | 3DSS CENTER | 3DSS CENTER |
| 1 | 3DSS CURSOR | 3DSS CURSOR |
| 2 | 3DSS FIX | 3DSS FIX |
| 3 | W/F CENTER (L) | W/F CENTER (EXPAND) |
| 4 | W/F CENTER (N) | W/F CENTER (NORMAL) |
| 5 | W/F CENTER (S) | — |
| 6 | W/F CURSOR (L) | W/F CURSOR (EXPAND) |
| 7 | W/F CURSOR (N) | W/F CURSOR (NORMAL) |
| 8 | W/F CURSOR (S) | — |
| 9 | W/F FIX (L) | W/F FIX (EXPAND) |
| A | W/F FIX (N) | W/F FIX (NORMAL) |
| B | W/F FIX (S) | — |

Worked examples (FTdx101, derived from the confirmed frame shape — **not yet
written to a radio**):

```
SS0540000;   MAIN span -> 20 kHz
SS1540000;   SUB  span -> 20 kHz
SS0620000;   MAIN mode -> 3DSS FIX
SS0810000;   MAIN HOLD -> ON
SS0800000;   MAIN HOLD -> OFF
```

### `MS` — METER SW

Already implemented and understood; see `Services/CatCommands.cs` and
`docs/decisions/` for the FTdx101 meter-borrow design. The relevant point here
is that YWC **already changes the radio's front-panel meter pair** during
transmit and restores it afterwards, so the "command the radio's own display"
pattern is not new — it is already shipping. This note generalises it.

Note the interaction: any UI that lets the operator pick the displayed meter
pair has to cooperate with the existing borrow-and-restore in
`MeterPollingService`, which overwrites the pair on transmit and puts it back
10 s after TX goes idle. A user-facing meter selector that fights that will
appear to randomly revert. This is the single most likely bug in the whole
feature.

---

## 5. Implementation shape

Nothing here is built. This is the seam list, not a schedule.

**`Services/RadioCapabilities.cs`** — add capability predicates alongside the
existing ones. The file already documents itself as the place per-model
variation hangs off, and it already has the right shape (`SupportsQmb`,
`SupportsVCTuneMain`). Wanted:

- `SupportsSpectrumScopeCat(model)` — FTdx101MP/D, FTdx10, FT-710.
- `SupportsScopeHold(model)` — the above minus FT-710.
- `ScopeHasPerReceiverScopes(model)` — FTdx101MP/D only, drives `P1`.
- A mode-table accessor returning the model's `P2=6` value list, because the
  FT-710's differs and must not be unified.

**`Services/CatCommands.cs`** — `SS` opcode plus builders. Keep the frame
construction in one place; the 5-character pad field is exactly the kind of
detail that gets miscounted at each call site.

**`Services/CatMessageDispatcher.cs`** — parse `SS` answers into state. The
FTdx101 marks `SS` as auto-information reported, so front-panel changes should
arrive unprompted and the UI can stay in sync when the operator touches the
radio. **[VERIFY]** that AI actually emits `SS` frames in practice — the
manual's flag column and reality have diverged before.

**UI** — a scope-control strip, gated per model. Natural home is beside the
existing spectrum panels, but note the conceptual split: YWC's own SDR spectrum
display and the radio's internal scope are two different things, and putting
controls for the radio's scope next to YWC's spectrum will confuse people
unless it is labelled clearly.

---

## 6. Open questions and bench gates

1. **No write has been attempted.** Everything in §2 is a Read. Before any
   code, send one `Set` by hand — `SS0540000;` to move MAIN span to 20 kHz —
   confirm the radio's display changes and that a subsequent `SS05;` reads back
   `4`. Read-back-after-write is the honest test.
2. **Does `SS` come back over auto-information?** Change the span on the radio's
   own touchscreen with a CAT monitor running and see whether an `SS` frame
   arrives unbidden. Determines whether the UI can stay in sync or must poll.
3. **The FTdx10 and FT-710 columns are manual-only.** Fabio (FTdx10) can
   confirm his; nobody currently runs an FT-710 actively enough to confirm
   that one, so it ships unverified or not at all.
4. **How does this interact with the meter borrow?** See §4. Needs deciding
   before a meter selector is exposed, not after.
5. **Is remote Mono/Multi actually wanted?** If yes, it needs the overlay this
   note was written to avoid — but only for that one control.

---

## 7. Relevance to IWC

The pattern transfers; the commands do not. The IC-7300 MkII exposes its scope
over CI-V (`27 00`), which is how IWC gets spectrum data in the first place, but
that is *reading* the scope. Whether the MkII's own display settings — span,
fix/centre, hold — are CI-V-writable is a separate question that needs the CI-V
reference checked, not an assumption carried over from Yaesu. See
`core/docs/design/shared-core-plan.md`: any shared piece would be the UI concept,
and UI touching the radio sits below the seam until `IRadioController` is
back-ported into YWC.
