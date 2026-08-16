# Driving the radio's own display over CAT

**Status:** IMPLEMENTED, and **enabled for the FTdx101MP/D only**. The FTdx10
and FT-710 tables below are written and kept, but gated off in
`RadioCapabilities.SupportsSpectrumScopeCat` until someone has run the write
probe on one — these are commands that change what appears on an operator's
front panel, so manual-derived writes do not ship unverified. Bench-tested on
an FTdx101MP. Raised by Colin MM5AGM 2026-08-15.
**Bench evidence:** `SS` read-probe and write-probe run against a real
FTdx101MP (ID0682) on COM4, 2026-08-15, plus end-to-end testing through the
app's own endpoints. Results in §2; what the implementation turned up in §6.
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

### Writes, verified separately

`scripts/probe/ss-write-probe.ps1` then confirmed that Set frames land. For
each sub-command it reads the current value, writes a different one, reads
back, and writes the original back, so the radio is left as it was found:

```
MAIN SPAN    was '90000' -> wrote '40000' -> reads 'SS0540000;'   WRITE OK
MAIN HOLD    was '00000' -> wrote '10000' -> reads 'SS0810000;'   WRITE OK
MAIN MARKER  was '10000' -> wrote '00000' -> reads 'SS0200000;'   WRITE OK
SUB SPAN     was '80000' -> wrote '40000' -> reads 'SS1540000;'   WRITE OK
```

One warning from writing that probe, because the radio hides the mistake: in a
double-quoted PowerShell string `` `0 `` is a NUL character, not a zero. The
first run therefore sent `SS054<NUL>000;` — and **the radio applied it anyway**
and read back cleanly. The table above is from the corrected run. A malformed
pad will not announce itself.

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

Worked examples (FTdx101, all of these written to a real radio and read back):

```
SS0540000;   MAIN span -> 20 kHz
SS1540000;   SUB  span -> 20 kHz
SS0620000;   MAIN mode -> 3DSS FIX
SS0810000;   MAIN HOLD -> ON
SS0800000;   MAIN HOLD -> OFF
SS04+02.5;   MAIN LEVEL -> +2.5 dB
```

Note that the twelve mode values are a 2×3×3 grid, not an arbitrary list —
display type × placement × size. `ScopeCommands.ModeValue` composes them from
those three axes, which is why the UI offers three small selectors instead of
one twelve-entry dropdown. The browser mirrors the same rule in
`radio-scope.js`; the two must agree.

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

## 5. What was built

| File | Role |
|---|---|
| `Services/RadioCapabilities.cs` | `SupportsSpectrumScopeCat`, `SupportsScopeHold`, `HasPerReceiverScopes`, `ScopeSizeLabels` |
| `Services/CatCommands.cs` | `ScopeCommands` — frame construction, answer parsing, mode composition |
| `Controllers/ScopeController.cs` | `GET /api/scope/{main\|sub}`, `POST /api/scope/{band}/{setting}` |
| `Pages/Shared/_RadioScopePartial.cshtml` | the control panel markup, gated per model |
| `wwwroot/js/ui/radio-scope.js` | wiring; repaints from the radio's read-back |
| `scripts/probe/ss-probe.ps1`, `ss-write-probe.ps1` | the read and write probes |

Three decisions worth knowing about, because none of them is obvious from the
code alone:

**Every write is followed by a read-back, and the read-back is what repaints
the UI.** The radio is the source of truth, never our optimism. That matters
more here than elsewhere because the operator may be standing at the rig
changing the same settings by hand, and because a radio that quietly refuses a
value should look refused rather than accepted.

**The panel is collapsed by default and reads its state lazily, on first
expand.** Six `SS` reads on a port shared with the ~10 Hz meter poll is not a
cost worth paying for a panel most users will never open. The collapsed state
persists in `localStorage` as `ywc.radioScopeOpen`.

**It is a partial view, not inline markup.** Placement on the page is not
settled — it currently sits above YWC's own spectrum panels — so moving it is a
matter of moving one `<partial>` line. Do not inline it until that argument is
had. Note the conceptual trap it is guarding against: YWC's SDR spectrum
display and the radio's internal scope are entirely different things, which is
why the card header says "the radio's own display" out loud.

`CatMessageDispatcher` was deliberately **not** touched. Read-back after write
covers the cases the UI actually has, and adding `SS` to the dispatcher only
pays off if the radio really does report scope changes over auto-information —
which is still unverified (§6).

---

## 6. What the implementation turned up

Two findings that were not in any manual, both measured on an FTdx101MP through
the app's own endpoints.

**Span is stored per display mode.** Setting the span to 20 kHz and then
switching mode appeared at first to "revert" it. It does not: the radio keeps a
separate span for each mode. Over ten consecutive mode changes, W/F CURSOR (L)
held 20 kHz and 3DSS FIX held 1 MHz, each returning reliably. So the
highlighted span button moving on its own after a mode change is correct
behaviour, and re-sending the previous span to "fix" it would overwrite a
setting the operator deliberately chose. Repainting everything from the
read-back is what keeps this honest. The UI says so in a hint line, because it
otherwise looks like a bug.

**Reads issued immediately after a write go unanswered while the radio
redraws** — roughly one in three following a mode change, which is the most
expensive redraw. Fixed with a 150 ms settle delay before the read-back plus
one retry; ten consecutive mode changes then produced zero dropped reads.
Anything still unanswered after the retry is reported as null and shown as
unknown rather than guessed at.

One trap for the next person: **`CatMultiplexerService` strips the trailing
`;`** from answers. A parser that requires it works perfectly against a raw
serial probe and then returns null for everything inside the app. That happened
here; `ScopeCommands.ValueField` now treats the terminator as optional.

### Still open

1. **Does `SS` come back over auto-information?** Change the span on the
   radio's own touchscreen with a CAT monitor running and see whether an `SS`
   frame arrives unbidden. Only worth answering if the read-back approach turns
   out to feel stale in use.
2. **The FTdx10 and FT-710 are manual-only** — nothing on either has been
   hardware-verified, so both are **gated off** (decided 2026-08-16; see the
   Status note at the top). Re-enabling either is one line in
   `RadioCapabilities.SupportsSpectrumScopeCat` plus a probe run. Fabio
   (FTdx10) can confirm his whenever video-tx frees him; nobody currently runs
   an FT-710 actively enough to confirm that one, so it stays gated until
   someone steps up.
3. **The meter selector is not built**, and the reason is in §4: it has to
   cooperate with the borrow-and-restore in `MeterPollingService` or it will
   appear to randomly revert. That is a design decision to take deliberately,
   not a control to bolt on.
4. **Is remote Mono/Multi actually wanted?** If yes it needs the overlay this
   note was written to avoid — but only for that one control.
5. **Placement.** Above the spectrum panels is a starting position, not a
   verdict.

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
