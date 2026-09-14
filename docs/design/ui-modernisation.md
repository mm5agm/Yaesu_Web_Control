# UI modernisation — layout choice and theming

**Status:** exploration, parked 2026-09-13. No code written, nothing decided.
**Mockups:** https://claude.ai/code/artifact/e5748aa6-a0b5-468c-b9f8-f338a0375e62
(4 pages: four directions · adapts to your station · kit of parts · Main and Sub)

## The question this answers

YWC and IWC work well but the interface doesn't look modern. Could the
functionality be kept and the UI tailored to what the operator actually
has — SDRs only where there are I.F. taps, video only where there's a
capture card — and if a new layout is offered, how do both layouts
coexist?

Direction **C ("Workspace")** was the one picked: dockable tiles on a
grid, a left rail listing panels grouped by what your station can show,
named layout presets (Ragchew / Contest / CW / Digital).

## Measurements — the numbers that drive every decision below

Taken 2026-09-13 on `feature/keyboard-cw-send`. These are the reason the
recommendation is what it is; re-measure before trusting them after a
big refactor.

| | |
|---|---|
| `Pages/Index.cshtml` | 5,479 lines — ~2,770 markup, ~2,650 one inline `<script>` |
| `getElementById` calls | ~146 in Index, ~386 in `wwwroot/js` (some are build-time copies from `core/`) |
| Bootstrap `btn` classes in Index markup | 471 |
| Top-level `card` panels | 23 |
| `wwwroot/css/site.css` | 1,663 lines; loaded **twice** (`_Layout.cshtml:145` and `Index.cshtml:145` with a hand-written cache-buster) |
| `ApplicationSettings` properties | 103 |
| Shared partials | 7 (`_BandButtonsPartial`, `_RadioScopePartial`, …) |
| Existing `ywc.*` localStorage view prefs | 10 keys, incl. `ywc.radioDisplayVisible`, `ywc.radioScopeOpen`, `ywc.spectrumSplit.*` |

**Accessibility footprint** (all already written, all in markup):

| | |
|---|---|
| `aria-*` attributes in Index markup | 396 (294 `aria-label`, 48 `aria-hidden`, 20 `aria-live`, 16 `aria-labelledby`, …) |
| `role=` | 42 |
| `data-a11y-key` hooks | 83 across Index + partials |
| `wwwroot/js/ui/a11y-labels.js` | 38 lines |
| `wwwroot/js/ui/voice-control.js` | 264 lines |

**The single most important fact:** the JavaScript finds every control by
**id**, not by container. VFO A and VFO B are also near-identical
duplicated markup (~450 lines each, Index 1022–1475 and 1476–1890), and
the markup is full of inline `onclick` / `oninput` handlers.

## Recommended approach: one markup tree, two geometries

### What not to do

A second Razor page (`Index.cshtml` + `IndexWorkspace.cshtml`) needs two
copies of 2,770 lines of markup carrying the **same ids**, because the JS
demands them. Every bug then gets fixed twice and the second copy is the
one that gets forgotten — cf. `Tests/Yaesu_Web_Control.Tests` vs
`Tests/YaesuWebControl.Tests`, one underscore apart, 14 tests that never
ran in CI for months (deleted 2026-08-29).

### What to do instead

1. Wrap each existing top-level card in `<section data-panel="vfo-a">`
   etc. ~10 insertions. **No id changes, no handler changes, no ARIA
   changes.**
2. **Classic** = today's Bootstrap flow, untouched.
3. **Workspace** = the same sections become CSS Grid items placed by
   `grid-area` from a saved layout.

CSS Grid places children anywhere regardless of source order, so
drag-and-drop **never re-parents a node**: dragging writes coordinates,
not DOM structure. All ~500 id lookups stay valid, inline handlers stay
bound, tab order is unchanged.

### Two rules that make it hold together

- **Reading order:** once panels are placed by grid, visual position
  diverges from DOM order (WCAG 1.3.2 / 2.4.3). Fix: dragging is live
  grid placement; pressing **Done** commits the same order to the DOM.
  Moving a node preserves ids and listeners, and doing it once at an
  explicit save is when a flicker is acceptable.
  *Unverified:* whether re-parenting the MJPEG `<img>` restarts the video
  stream. Check on the bench before relying on this.
- **A panel the user removes is `display:none`, never absent from the
  DOM.** Keeps every id lookup valid and keeps voice able to act on
  "band forty" whether or not the band tile is on screen. Hiding then
  costs nothing to implement.

This is a refinement of the hide / ghost / disable rule on the mockup's
page 2: *hidden from view* is not the same as *absent from the DOM*.

## Where the layout choice lives

- `ApplicationSettings`: `LayoutMode` = Classic | Workspace, `Theme` =
  Classic | Instrument. These are the **defaults**.
  Mind the `ModelState.Remove` trap for string settings on the Settings
  page (`<Nullable>enable</Nullable>` adds implicit `[Required]`).
- **Per-device override in localStorage** (`ywc.layout`, `ywc.theme`) —
  the shack PC and a tablet want different answers, and there are
  already 10 `ywc.*` view prefs following this pattern.
- **Named presets go server-side** through `SettingsService`, so they
  survive a browser cache clear and follow the operator between machines.

## Phasing — the cheap win is phase 2, not phase 3

1. **Section wrappers + split `site.css`** into structure and theme.
   No user-visible change; the proof is that the page looks identical.
   Tidy the double `site.css` include while in there.
2. **Instrument theme, Classic geometry.** Dark ground, rounded panels,
   mono frequency digits, LED pips. There is no theme or dark-mode
   concept in the codebase today, so this is new but shallow — it
   restyles Bootstrap classes, touching no markup and no JS.
   **This is where most of "it doesn't look modern" actually gets
   fixed, at the lowest risk.** If it turns out to be enough, phase 3
   becomes optional rather than abandoned.
3. **Workspace geometry** — grid placement, drag, resize, presets, and
   the MAIN/SUB focus switch.

## What belongs in `core/`

The **layout engine** — panel id → grid rect, serialise, validate,
migrate an old saved layout — has no radio in it. Per the table in
CLAUDE.md that is `core/js/layout/`, and IWC has the identical problem
waiting for it. It is also the only part of this that is unit-testable;
everything else is manual verification in the browser.

What stays YWC-local is the **panel catalogue**: which panels exist and
which this radio supports, built from `Services/RadioCapabilities.cs`.

That is the same seam the mockup draws — the rail is the catalogue, the
grid is the engine.

## Accessibility: what carries over, and what doesn't

Colin asked whether the new theme could simply skip the accessibility
work. The measurement inverts the question.

### Immune to theming — inherited for free, completely

The theme is CSS; the accessibility is HTML. A stylesheet cannot delete
an `aria-label`. So the new layout keeps all 396 `aria-*` attributes, 42
roles, 83 `data-a11y-key` hooks (so the `Labels` override page still
works), 20 live regions, arrow-key navigation in the radiogroups, and
voice control — which targets controls by id and key, neither of which
changes.

**Removing them would cost more than keeping them**: you would have to
strip them from shared markup (breaking Classic too) or fork the markup
into the duplicate page rejected above.

### Made of theming — does NOT carry over, and fails silently

| | today | on the new dark palette |
|---|---|---|
| Focus ring `#0d6efd` (`site.css:259`) | 4.50 on white | **3.77** on `#151d27`, **3.36** on `#1d2732` (needs 3.0) |
| Mockup ring `#4fd6ff` | — | 10.02 / 8.93 |

`site.css:282` already does `outline: none` on the power slider — proof
of how easily a focus ring goes missing in a restyle.

Palette contrast on panel `#151d27`:

| colour | ratio | |
|---|---|---|
| text `#e6edf5` | 14.39 | AAA |
| blue `#4fb4e6` | 7.28 | AAA |
| amber `#f2a33c` | 8.15 | AAA |
| green `#58cf9a` | 8.74 | AAA |
| muted `#8ba0b5` | 6.30 | AA |
| **dim `#6d8296`** | **4.27** | **large text only — used for 10–11px captions in the mockups, so it FAILS as drawn** |
| **alarm `#e2453f`** | **4.17** | **large text only** |

Also presentational, therefore also at risk: **target size** (the new
look is denser than today's; density is an accessibility axis — Yuri's
head-tracker cares directly) and **reading order** (see above).

**So the real exposure is four CSS values chosen deliberately: focus
ring, caption colour, alarm colour, minimum target size.** Put them in
the kit-of-parts page as fixed tokens so the theme cannot drift below
them later.

### The legitimate scope cut

Making the **drag-and-drop layout editor** fully keyboard-operable is
real work and can reasonably be skipped — make the editor mouse-first —
**provided** the layouts it produces are ordinary keyboard/voice-usable
pages and the named presets are reachable from a normal list. Yuri can
then pick a layout even if he cannot build one, which is the part that
matters day to day.

The MAIN/SUB focus switch is a new control and needs a label. One line.

### The trap

Freezing Classic (so Workspace is where new work goes) **and** leaving
Classic as the only accessible layout combine into: screen-reader users
are permanently on the frozen layout, and every new feature ships where
they cannot reach it. Neither decision looks like that on its own.
Practically that is Thomas OZ1JTE, Yuri W4YSW and Bill W1WRH, and
CLAUDE.md already classes voice regressions as release blockers.

## Dual receiver (Main + Sub) — from the page-4 mockups

- Blue = Main, green = Sub throughout: VFO tile, S-meter, spectrum,
  waterfall, cluster spots. Colour is never the only signal — headers
  name the receiver and state is written in words (HAS FOCUS /
  LISTENING).
- Two I.F. taps → two full spectrum tiles, each labelled with the tap
  feeding it (MAIN 9.005, SUB 8.900) and each with its own span row.
- The rail groups panels by **Main receiver / Sub receiver / Either
  receiver / Whole station**, because with two receivers "add a
  spectrum" is ambiguous.
- **MAIN/SUB focus switch in the header** says what the keyboard and
  voice act on — without it "band forty" has no answer once both
  receivers are live. Every shared tile either follows that switch or is
  pinned to one receiver, and says which in its own header. Not drawn at
  all on single-receiver radios (FTdx10, FT-710, FTDX3000).
- Note: the original Direction C board listed "VFO B — Sub" as *placed*
  in the rail but never drew the tile. The page-4 board fixes that.

## Band selection: buttons, not a dropdown

The button grid **reflows** with the tile — 6×2, 4×3, 3×4, 2×6. A real
`<select>` appears only in a tile one grid cell wide, where twelve
buttons genuinely cannot fit.

Reasons, in order of weight:

1. The bands today are a `role="radiogroup"` announced as *"Band — use
   arrow keys to change band"*, with a roving `tabindex`
   (`_BandButtonsPartial.cshtml`). A `<select>` is a different widget
   with a different announcement.
2. Each button carries `data-a11y-key="bands.<band>"`, which the Labels
   page lets an operator override. Options in a select have nowhere to
   hang that.
3. The voice grammar expects something clickable to exist; behind a
   closed dropdown it must open it first.
4. A native select's popup is drawn by the OS — dark theme, focus ring
   and large-type setting all stop at its edge.
5. Two interactions instead of one on every band change, which
   head-tracker users pay for directly.

**The rule: presentation reflows, identity does not.** Same
`data-a11y-key`, same spoken name, same voice phrase in every shape. And
the shape changes only when the tile is deliberately *resized*, never
mid-QSO because a window got dragged narrower.

## Open — not decided

- Whether to do any of this at all, and whether phase 2 alone suffices.
- Whether Classic is frozen, and if so how the accessibility trap above
  is avoided.
- Whether the layout editor is mouse-first.
- Whether the layout engine actually goes to `core/` on first cut or
  after it settles (CLAUDE.md says the former — build it in `core/` from
  the start, as CW was).
- Bench check: does re-parenting the MJPEG `<img>` restart the stream?
