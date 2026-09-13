# Radio Display hotspots — click-to-tune and clickable controls on the captured TFT

Status: **prototype**. Branch `feature/radio-display-hotspots`, PR #138.
Bench-checked on an FTdx101MP (MONO W/F layout) 2026-09-12: zones align,
click-to-tune works in CENTER, and every hotspot below does what the table
says.

FTdx10 MONO W/F layout is in `LAYOUTS.FTdx10` from pixel boxes measured
2026-09-12 on an 800-wide frame (converted as 800×600). No ANT; no MONO /
HOLD / MEM CH; SPEED is a hotspot. Not yet nudged on a live overlay.
**FT-710** stays off. IF-OUT axis correction is not part of this overlay
and is not applicable to the FTdx10 (no IF tap).

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

## Hotspots

| Zone | Click does | Endpoint |
|---|---|---|
| ANT | cycle 1 → 2 → 3 | `/api/cat/antenna/{a\|b}` |
| ATT | cycle OFF → 6 → 12 → 18 dB | `/api/cat/attenuator/{a\|b}` |
| IPO | cycle IPO → AMP1 → AMP2 | `/api/cat/ipo/{a\|b}` |
| R.FIL | cycle roofing filters (model ring) | `/api/cat/roofingfilter/{a\|b}` |
| AGC | cycle OFF → FAST → MID → SLOW → AUTO | `/api/cat/agc/{a\|b}` |
| CURSOR | CENTER → CURSOR → FIX | `RadioScopeControl.cyclePlacement()` |
| SPAN | next span | `RadioScopeControl.cycleSpan()` |
| SPEED | next FFT speed (FTdx10) | `RadioScopeControl.cycleSpeed()` |
| 3DSS | W/F ↔ 3DSS | `RadioScopeControl.toggle3dss()` |
| EXPAND | L / N / S on FTdx10; no CAT on FTdx101 | `RadioScopeControl.cycleSize()` / flash |
| HOLD | toggle (FTdx101; not on the FTdx10 TFT row) | `RadioScopeControl.toggleHold()` |
| MONO / MULTI / MEM CH | nothing — the label flashes red saying there is no CAT command | — |

Cycling needs the current value. The overlay keeps its own small state
cache, seeded from `/api/cat/status` (which now also returns `att`, `ipo`,
`agc` and `activeVfo`) and kept current by `onRadioState(update)`, which
both pages call from their `RadioStateUpdate` handler. An unknown current
value cycles to the first entry of the ring rather than doing nothing.

## The layout table

`LAYOUTS.FTdx101` in `radio-display-hotspots.js` was measured from
`pictures/Radio_Display_Docked.png` — a downscaled screenshot of the MONO
W/F layout, so every rectangle is ±0.5 % and **expected to need nudging on
the bench**. Tools for that:

- `window.radioDisplayHotspots.debug(true)` draws every zone and the
  detected marker over the video. A white frame around the whole TFT is
  the overlay's (0,0)–(1,1) space — if the boxes sit in the corner, they
  were measured from a crop, not that frame.
- `window.radioDisplayHotspots.measure()` — drag a box on the live picture
  for each zone in order (ATT → MEM CH). Do not type fractions.
- `window.radioDisplayHotspots.snapshot()` downloads `radio-display-snapshot.png`
  (Chrome blanks a `data:` image tab).
- `localStorage['ywc.radioDisplayHotspots.layout.<RadioModel>']` overrides
  the built-in table. `dump()` writes the current layout there.

## Known gaps

- **One layout per measured radio.** MONO W/F on the FTdx101 and FTdx10 is
  in the table. FTdx10 has no ANT, no MONO/HOLD/MEM CH on that row, and
  SPEED instead; EXPAND cycles L/N/S. EXPAND/MULTI as *layouts*, the dual
  MAIN/SUB view and 3DSS all move or reshape the scope box, and none of
  those layouts is readable over CAT (`SS` reports W/F vs 3DSS and the
  placement, not MONO/EXPAND/MULTI). Options, in rough order of preference:
  detect the box from the frame (the waterfall band is a distinctive
  horizontal run of colour), a layout selector in the toolbar, or a table
  per layout with the operator telling us which one is up.
- **Marker colour is assumed red.** Verified only for colour scheme 1 in
  the FTdx101 screenshot. If another scheme draws it differently, the scan
  threshold needs to follow the `SS` colour setting.
- **FT-710 layout** is unmeasured. The module returns quietly when it has
  no layout for the model.
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
