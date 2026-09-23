# Radio Display hotspots — click-to-tune and clickable controls on the captured TFT

Status: **prototype**. Branch `feature/radio-display-hotspots`, PR #138.
Bench-checked on an FTdx101MP (MONO W/F layout) 2026-09-12: zones align,
click-to-tune works in CENTER, and every hotspot below does what the table
says. **That check predates the zone/action refactor below** — the module
was reworked on 2026-09-13 and needs another pass on the '101MP, and a
first pass on the FTdx10.

FTdx10 MONO W/F layout is in `LAYOUTS.FTdx10` from pixel boxes Fabio
measured 2026-09-12 on an 800-wide frame (converted as 800×600). No ANT
(one jack); no MONO / HOLD / MEM CH on the soft-button row; SPEED is a
hotspot; EXPAND is the vertical expand with no CAT command, as on the '101
(I had it down as L / N / S over CAT — wrong, see the table below). Fabio's
screenshots of 2026-09-14 (S / N / L, EXPAND on and off) show the
soft-button row is always on screen, so no FTdx10 zone is hidden any more;
what they also show is EXPAND pushing the scope up over the readout row,
which nothing over CAT reports (Known gaps). **FT-710** stays off — it has the same exportable TFT
but nobody has measured it, and its `SS` tables are unverified anyway.
IF-OUT axis correction is not part of this overlay and is not applicable to
the FTdx10 (no IF tap).

## The idea

Remote Video shows the radio's own screen as an MJPEG stream. CAT Scope
Control (`SS`) already lets the operator change what that screen shows. The
missing piece for using the captured display *instead of* an SDR panadapter
is being able to click on it: tune to a signal in the waterfall, and press the
readouts and soft-buttons that are drawn on the picture.

Everything on the TFT sits at a fixed place for a given front-panel layout,
so a table of rectangles turns the picture into a control surface.

## Mouse → frame

`radio-display-hotspots.js` lays an overlay over the `<img>` and positions it
to match the *drawn* image, not the `<img>` box. Fit, Fill, docking, pop-out
and fullscreen only change the box; the picture inside it is always
`object-fit: contain|cover` with `object-position: center`, so:

```
scale   = min(boxW/nw, boxH/nh)      (contain)   or   max(...)   (cover)
drawnW  = nw × scale, drawnH = nh × scale
offsetX = (boxW − drawnW) / 2, offsetY = (boxH − drawnH) / 2
fx      = (mouseX − boxLeft − offsetX) / drawnW        ∈ 0..1
```

Zones are stored as **fractions** of the frame, not pixels. The capture
dongle stretches the TFT to fill whatever size is selected (it never
letterboxes — see `project_video_capture_resolution`), so fractions survive a
change of capture size where pixel offsets would not.

## Frame → frequency

The radio draws the VFO marker at the VFO frequency in CENTER, CURSOR and
FIX placement alike. So the frequency under the cursor is

```
f(x) = VFO + (fx − markerFx) × span / plotWidth
```

with no need to know where the axis starts. `span` is the live `SS` span
from `RadioScopeControl.state`; `VFO` is `FrequencyA`/`FrequencyB` for the
band the scope is showing (MAIN → A, SUB → B on dual-receiver radios; the
active VFO on single-receiver ones).

The marker is found by drawing the current frame to an off-screen canvas
(`drawImage` on a same-origin MJPEG `<img>` gives the current frame) and
scanning the spectrum rows for the column holding the longest run of
saturated red (`r > 150, g < 90, b < 90`). The run must span at least half
the scanned band, so a red spectrum trace or a red waterfall streak in the
hotter colour schemes does not pass for it. Only the spectrum rows are
scanned, not the waterfall, for the same reason.

When no marker is found: CENTER placement falls back to the box centre; CURSOR
and FIX report "marker not found" and refuse to tune. 3DSS refuses too — the
perspective view has no flat frequency axis.

Tuning writes `FA`/`FB` only. No mode change and no CW tone offset: on the
SDR panel the CW click offset was measured as 0 (`68e7585`), and the radio's
own scope is showing the operator exactly where its filter sits.

## Zones carry their action, not just their place

The first cut keyed everything on the *name* of the thing under the mouse:
a zone called `expand` always did whatever EXPAND did on the FTdx101. That
fell over the moment the FTdx10 arrived, because the same soft-key means
something different on each radio:

| Soft-key | FTdx101MP/D | FTdx10 | FT-710 |
|---|---|---|---|
| EXPAND | vertical expand, **no CAT** (L/N/S is cycled by touching the waterfall, OM p25) | vertical expand, **no CAT** (OM p26; L/N/S is the waterfall touch here too, OM p25) | EXPAND / NORMAL over CAT (`SS` P2=6) |
| ANT | 1 → 2 → 3 | not on screen — one jack | not on screen — one jack |
| SPEED | not on this row | next FFT speed | ? |

So a zone is **where + what**: `{ rect, action, hint, hideWhen }`. The
module never asks which radio it is talking to except once, in
`builtinFor()`, to pick the table. Everything after that is driven by the
zone's `action` string, looked up in `ACTIONS`:

| `action` | Click does | Endpoint |
|---|---|---|
| `readout.ant` | cycle 1 → 2 → 3 | `/api/cat/antenna/{a\|b}` |
| `readout.att` | cycle OFF → 6 → 12 → 18 dB | `/api/cat/attenuator/{a\|b}` |
| `readout.ipo` | cycle IPO → AMP1 → AMP2 | `/api/cat/ipo/{a\|b}` |
| `readout.rfil` | cycle roofing filters (model ring) | `/api/cat/roofingfilter/{a\|b}` |
| `readout.agc` | cycle OFF → FAST → MID → SLOW → AUTO | `/api/cat/agc/{a\|b}` |
| `scope.placement` | CENTER → CURSOR → FIX | `RadioScopeControl.cyclePlacement()` |
| `scope.span` | next span | `RadioScopeControl.cycleSpan()` |
| `scope.speed` | next FFT speed | `RadioScopeControl.cycleSpeed()` |
| `scope.size` | next scope size (L/N/S or EXPAND/NORMAL — the radio's own ring) | `RadioScopeControl.cycleSize()` |
| `scope.3dss` | W/F ↔ 3DSS | `RadioScopeControl.toggle3dss()` |
| `scope.hold` | toggle HOLD | `RadioScopeControl.toggleHold()` |
| `meter.select` | open the TX meter pop-up (below) | `GET` / `POST /api/cat/meters` |
| `none` | nothing — the `hint` flashes red ("… has no CAT command — press it on the radio") | — |

`hint` is the hover text; an action supplies a default, a zone can
override it (`'EXPAND / NORMAL'` on the FT-710's EXPAND). `hideWhen` is a
list of `CONDITIONS` keys — `'3dss'`, `'size:0'`, `'size:1'`, `'size:2'` —
evaluated against `RadioScopeControl.state`; when any is true the zone is
not hit-tested and the debug overlay draws it dashed with "(hidden)". If
the scope state is not known yet (no control mounted) every zone stays
live. It was added for the FTdx10's button row, which Fabio first reported
as leaving the screen in size L and in 3DSS; his 2026-09-14 screenshots
show it present in every size, so no built-in table uses it now. It stays
for the next radio that does move something `SS` can see.

Cycling needs the current value. The overlay keeps its own small state
cache, seeded from `/api/cat/status` (which now also returns `att`, `ipo`,
`agc` and `activeVfo`) and kept current by `onRadioState(update)`, which
both pages call from their `RadioStateUpdate` handler. An unknown current
value cycles to the first entry of the ring rather than doing nothing.

Adding a radio is a new entry in `LAYOUTS` and nothing else. Adding a
kind of control is a new `ACTIONS` entry.

## The meter pop-up

Touching the meter on the radio's TFT opens the radio's own meter chooser.
No CAT command opens it: `MS` sets the meters but nothing touches the
screen. So the `meter` zone (`meter.select`) makes YWC draw its own chooser
inside the overlay, under the zone, and the radio's picture changes once
`MS` lands.

- **The table is server-side**, in `Services/FrontPanelMeters.cs`, and the
  browser carries no copy. The FTdx101MP/D has two slots (left PO / COMP /
  TEMP, right ALC / VDD / ID / SWR, sent as `MS P1 P2`). The FTdx10 and
  FT-710 have one slot of six, with P2 fixed at 0. `GET /api/cat/meters`
  returns the slots, the options and the current choice (a fresh `MS;`
  read when the meters are not borrowed). `POST` takes one code per slot,
  checks it against the table, sends `MS`, and replies in the same shape.
- **TX borrow.** `MeterPollingService` sets `MS13` while transmitting on the
  FTdx101 and restores `RadioMeterSelection` after 10 s of TX idle. A pick
  made during the borrow is recorded (`SetOperatorMeterSelection`) and
  **not** sent. The restore then applies it, and the reply carries
  `deferred: true` so the pop-up can say so. There is a small race if TX
  starts between the borrow check and the send; the restore covers it.
- **Styles** are injected by the module (`ensureMeterPopupStyle`) and do
  not live in `site.css`, which the theme work is changing.
- **Zone rect** on the FTdx101 is estimated from
  `Radio_Display_Docked.png` and has not been bench-measured. Use
  `measure(['zones.meter'])`. The right half of that strip is the filter
  display, so keep the box to the meter. The FTdx10 gets no meter zone until
  a screenshot with its chooser open has been measured.

## The layout table

`LAYOUTS.FTdx101` in `radio-display-hotspots.js` was measured from
`pictures/Radio_Display_Docked.png` — a downscaled screenshot of the MONO
W/F layout — and then nudged on the bench. `LAYOUTS.FTdx10` is from
Fabio's pixel boxes. Tools for the next radio, or for a layout that has
drifted:

- `window.radioDisplayHotspots.debug(true)` draws every zone and the
  detected marker over the video — green for readouts, yellow for scope
  soft-keys, grey for `none`, dashed when hidden. A white frame around the
  whole TFT is the overlay's (0,0)–(1,1) space — if the boxes sit in the
  corner, they were measured from a crop, not that frame.
- `window.radioDisplayHotspots.measure()` — drag a box on the live picture
  for each item in turn (`scope.plot`, `scope.marker`, then every zone in
  the built-in table; a radio with no table gets a generic ATT → 3DSS
  list). Do not type fractions. `measure(['zones.span'])` re-does one.
  A re-measured zone keeps its `action` / `hint` / `hideWhen`; a zone
  measured from nothing gets `action: 'none'` and needs its action filled
  in by hand.
- `window.radioDisplayHotspots.snapshot()` downloads `radio-display-snapshot.png`
  (Chrome blanks a `data:` image tab).
- `localStorage['ywc.radioDisplayHotspots.layout.<RadioModel>']` overrides
  the built-in table. `dump()` writes the current layout there. The value
  is `{ scope: { plot, marker }, zones: { id: { rect, action, hint,
  hideWhen } } }`; a zone given as a bare `[l,t,r,b]` (or `"l,t,r,b"`), and
  the older `{ readouts, buttons, scope }` shape, are both still read —
  the rect is taken and the action/hint/hideWhen come from the built-in
  zone of the same id, so an override that only moves boxes never loses
  behaviour. The unscoped `ywc.radioDisplayHotspots.layout` key is read for
  the FTdx101 only (it predates per-model keys).

## Which radios have a screen to capture

| Radio | External display | Resolution | Overlay |
|---|---|---|---|
| FTdx101MP / D | DVI-D **EXT-DISPLAY** | 800×480 (default) / 800×600 | measured, bench-checked |
| FTdx10 | DVI-D **EXT-DISPLAY** | 800×480 (default) / 800×600 | measured by Fabio, not yet nudged live |
| FT-710 | EX menu 04 **EXT-MONITOR** (EXT DISPLAY, PIXEL) | 800×480 (default) / 800×600 | unmeasured; also gated by `SupportsSpectrumScopeCat` |
| FTDX3000 | none | — | not possible |

Whether the capture dongle letterboxes or stretches an 800×480 source is
per-dongle (Colin's stretches; Fabio's does not), which is why the table
is in frame fractions and the marker is found by scanning rather than
assumed.

## Known gaps

- **One layout per measured radio.** MONO W/F on the FTdx101 and FTdx10 is
  in the table. EXPAND/MULTI as *layouts*, the dual MAIN/SUB view and 3DSS
  all move or reshape the scope box, and none of those layouts is readable
  over CAT (`SS` reports W/F vs 3DSS and the size, not MONO/EXPAND/MULTI).
  EXPAND is the one that bites: on both radios it grows the scope upward
  over the ATT/IPO/R.FIL/AGC row (Fabio's FTdx10 shots, 2026-09-14), so
  with it on the readout zones sit over scope and the plot's top edge is
  wrong. `hideWhen` covers the cases `SS` *can* see; the rest still needs one of:
  detect the box from the frame (the waterfall band is a distinctive
  horizontal run of colour), a layout selector in the toolbar, or a table
  per layout with the operator telling us which one is up.
- **Marker colour is assumed red.** Fabio confirms it is red in every
  colour scheme on the FTdx10; verified for scheme 1 only on the FTdx101.
- **FT-710 layout** is unmeasured (see the table above). The module builds
  an empty overlay for a model with no table, so `measure()` and
  `snapshot()` work on it; nothing is clickable until a table exists.
  `RadioCapabilities.HasAntennaSelector` also answers true for the FT-710,
  which is wrong (no `AN` command) — separate fix.
- **IF-OUT / SDR axis** is a different subsystem. It stays FTdx101-only;
  the FTdx10 has no IF tap, so those numbers must not be copied here.
- **Roofing-filter ring** includes the optional filters (1.2 kHz / 300 Hz
  on the 101; optional 300 Hz on the 10). When one is not fitted the
  server answers `{ warning: true }` and the radio stays put, so the click
  carries on round the ring until one is accepted (bench, 2026-09-12:
  12 kHz -> 3 kHz -> stuck retrying 1.2 kHz before this). Reading the
  fitted set up front is a follow-up.
- No touch handling beyond what `click` gives for free.

## Why this is YWC-only

Remote Video and everything layered on it is permanently YWC-only — see
CLAUDE.md. Nothing here goes to `core/`.
