# RTTY Contest GUI - plan

Status: plan, 2026-10-26, with Colin's answers to the four questions in. Nothing built yet. For Bruce VK2RT (Discussion #250), aiming for a beta before the WAE DX RTTY Contest on 14 November 2026.

## What it is

RTTY Contest is a fourth view beside Classic, Experimental and Radio. It is a new page at `/rtty-contest` that shows only what a contest RTTY operator uses:

- a small top strip;
- the spectrum over the waterfall;
- the RTTY tuner, at a fixed size;
- pop-out windows for each of these.

It follows the Radio panel (`/radio`): `Layout = null`, no Bootstrap shell, and it is drawn by shared core modules fed by a Yaesu adapter.

### What the user sees

**Top strip:**

- the VFO-A frequency (click it to type a frequency) and the mode;
- Mark/Shift as the RTTY tuner has them, for example "Mark 1415 / 170";
- compact S, PO, SWR and ALC bars, which can be hidden;
- Main/Sub, when two SDRs are configured; with one SDR the page shows Main only;
- a **DX SPOTS** on/off button;
- SPAN;
- the layout picker;
- the pop-out buttons;
- the Views menu.

There are no VFO boxes and no clarifier.

**Main area:**

- the spectrum on top and the waterfall below, with a grip between them;
- the RTTY tuner docked at the side.

**Fixed behaviour:**

- The band plan never changes the mode on a click (#169). This is forced off on this page, whatever the Settings value.
- A click tunes to 1 Hz.

## Checked against the code and the radio (2026-10-26)

| Plan relies on | Found | How checked |
|---|---|---|
| A page pattern with no shell, inheriting `IndexModel` | `Pages/RadioPanel.cshtml(.cs)`, `/RadioSpectrum`, `/RadioWindow` | read |
| A spectrum and waterfall as separate elements, so they can be split, swapped and popped out separately | `core/js/radio-panel/spectrum-view.js` takes a spectrum element and a waterfall element; `spectrum-pane.js` wraps them | read |
| A pop-out spectrum with a DX SPOTS switch, HOLD, Range/Speed/Bright/Smooth, click-to-tune and wheel step | `core/js/radio-panel/spectrum-popout.js` and `wwwroot/js/ui/radio-spectrum-yaesu.js` | read; ran `/RadioSpectrum` against the live radio: both panes drew real SDR frames (A at 14.074, B at 7.074, 10 kHz span) and made no writes |
| An automatic noise floor | yes: SpectrumView averages the lower quartile of the bins and shows `range` dB above it | read |
| A ref level (manual floor), double-click to fit | **no** | to build. The scale is in dB above the noise floor; S-units are not in the beta (Colin, 2026-10-26) |
| A click in DATA-L puts the signal's midpoint at the audio midpoint, so with Bruce's Mark 1415 / 170 the mark lands on 1415 | `spectrum-tune-yaesu.js` `target()`: dial = click + `afskMidpointAudioHz` = click + 1415 + 85 = click + 1500 on LSB. Rounded to 1 Hz | read, worked through by hand; a unit test is to be added |
| Click accuracy | at 10 kHz span a pixel is about 6.4 Hz on a 1562 px canvas; RTTY Auto (`/api/rtty/auto`, button in `rtty-tuner.js`) then centres the dial | measured on the live page |
| 1 Hz wheel step | the board's STEP list starts at 10 Hz; Classic has 1 Hz per VFO (`tuning-step-store.js`) | read. The contest page will have its own list, starting at 1 Hz |
| A fixed-size tuner | `_RttyTunerPartial` with `RttyContestMode` already hides everything except Tune, but `#rttyTunerStatus` is a growing message line | read. The contest page gives it a fixed single line, with the full text in a tooltip |
| Pop-outs remember their size and position, including on another monitor | `core/js/popout/popout.js` (`popoutGeom_<name>`, `offerScreenPermission`, `getScreenDetails`) | read |
| **On top** (Document Picture-in-Picture) | not in the code | Chrome 156 probe: `requestWindow` from a real click worked, and a canvas moved into it kept drawing at full rate (20 frames a second). `rtty-tuner.js` looks up its elements once at `init()`, so its DOM can be moved into the PiP window and back |
| Restoring a layout opens several windows | **blocked by default** | probe: three `window.open` calls from one click gave `[true, false, false]` on a fresh profile. See the risks |
| Spot tags on the spectrum | `SpectrumPane.setSpots / setShowSpots`; spots come from `/api/dxcluster/spots` and `DxSpot` on the hub | read |
| Tests | core JS tests: 117/117 pass (`node --test core/tests/js/*.test.mjs`). There is a UI harness that stubs every write (`core/tests/ui/browser-harness.mjs`) | ran |

## Where the code goes

The shared-core rule applies: anything radio-agnostic goes in `core/`.

**core/ (shared with IWC):**

- `core/js/rtty-contest/contest-view.js`: builds the page:
  - the top strip slots;
  - the spectrum/waterfall stack with its grip, swap and fit;
  - the tuner slot;
  - the pop-out and Dock buttons;
  - the DX SPOTS button.

  It has no radio in it. The adapter fills it, as with `radio-panel.js`.
- `core/js/rtty-contest/contest-layouts.js`: pure functions that turn the page's state into a layout payload and back. The payload holds the `popoutGeom_*` keys, the open/closed state, the split ratio, the swap, DX spots, span and receiver. Unit-tested.
- **Named layouts are kept the way Experimental keeps them** (Colin, 2026-10-26). They are on the server, through the core `FlexLayoutStore`, with localStorage as the working copy, as `flex-workspace.js` does. The core store takes its file path and treats the layout as opaque, so it is reused as it is:
  - YWC registers a second instance on `rtty-contest-layouts.json`, a separate file from Experimental's `flex-layouts.json`;
  - a small `RttyContestLayoutController` at `/api/rttycontestlayouts` has the same routes as `FlexLayoutController`;
  - the manage dialog is `core/js/flex/flex-layouts-admin.js`, passed `apiBase`.

  The window positions themselves are kept in the browser, as for every pop-out today.
- `core/js/rtty-contest/pip-dock.js`: moves an element into a Document Picture-in-Picture window and back, copying the page's stylesheets. Closing the PiP window docks the element again.
- `core/js/radio-panel/spectrum-view.js` and `spectrum-pane.js`: new options, with **defaults unchanged** so the Radio panel and IWC look exactly as before:
  - `floor: 'auto' | <dB>` for the ref level;
  - `fit()`, which sets the range so the strongest recent peak sits just under the top;
  - `parts: 'both' | 'spectrum' | 'waterfall'`;
  - `order` for the swap;
  - an optional grip.
- `core/js/rtty/rtty-pair-snap.js` (optional, see phase 2): a pure function that, given bins, a click and the shift, finds the tone pair nearest the click and returns its midpoint. Unit-tested.

**YWC only:**

- `Pages/RttyContest.cshtml(.cs)`: the page and its model, with `RadioPanelModel`-style capability flags.
- `Pages/RttyContestWindow.cshtml`: one pop-out page, with `?p=spectrum|waterfall|both|tuner`.
- `wwwroot/js/ui/rtty-contest-yaesu.js`: the adapter. It handles the hub, tuning, the meters, the mode, the RTTY settings and the TX state.
- `wwwroot/js/ui/spectrum-feed-yaesu.js`: **refactored out of `radio-spectrum-yaesu.js`**:
  - bin order;
  - the IF OUT axis offset;
  - SH-to-Hz widths;
  - DATA shift;
  - spots.

  The Radio panel pop-out and RTTY Contest then share one copy of the Yaesu-specific spectrum code instead of two.
- View links: Radio panel `views:`, the Classic nav (`Pages/Shared/_Layout.cshtml`), and the RTTY Contest page's own menu. **Experimental is not touched**: it is Fabio's GUI, and a link there would be a PR that mentions him.
- The only server change is the layouts store and its controller (above). Everything else already has an endpoint.

## Phases (today is 26 Oct; contest is 14 Nov)

Work happens on `feature/rtty-contest` from `origin/develop`. Before writing any code, run `./scripts/core-sync.ps1 -Pull`.

**Phase 0 (about 1 day): groundwork.**

- Move the Yaesu spectrum feed into `spectrum-feed-yaesu.js`.
- `/RadioSpectrum` must behave exactly as before. Check this with the UI harness and on the live radio.

**Phase 1 (about 3 days): the docked page.**

- the top strip;
- the spectrum over the waterfall, with the grip;
- the docked fixed-size tuner;
- the DX SPOTS button;
- band-plan mode change forced off;
- the view links.

**Phase 2 (about 3 days): the spectrum controls.**

- Ref level, Range and Auto floor;
- double-click to fit;
- the spectrum/waterfall swap setting;
- a wheel step list of 1, 10, 100 and 1000 Hz;
- the `target()` unit test for Mark 1415.
- Snap-to-tone-pair only if the bench shows clicks missing at 20 kHz or more.

**Phase 3 (about 3 days): pop-outs.**

- spectrum, waterfall and both, each in its own window;
- the tuner window;
- Dock on each (REATTACH);
- **On top** for the tuner via PiP.
- Only one PiP window is possible, so On top is offered on the tuner only.

**Phase 4 (about 3 days): layouts.**

- Saved layouts "RTTY contest", "Everyday" and "FT8" on the server, as Experimental's are: size, position, monitor, and which panels are out. Includes the controller and a unit test for it in `Tests/YaesuWebControl.Tests`.
- A per-panel placement choice: main window, a named monitor (via `getScreenDetails`), or hidden.
- Handling for a blocked pop-up (see the risks).

**Phase 5 (about 3 days): tests, bench, manual, beta.**

- unit tests for the pure modules;
- a UI suite `Tests/ui/ywc-rtty-contest.mjs` using the harness, where writes are stubbed and state is simulated;
- a receive-only bench session on the FTdx101MP;
- a User Manual section;
- a pre-release such as v2.5.4-pre2, aimed at **9 Nov**. That gives Bruce about 5 days to set it up. Cutting it needs Colin's go.

**Cut line if time runs short:**

- Phase 4 drops to what `popout.js` already does: each window remembers its own place. Named layouts and per-monitor placement come after the beta.

**Telling Bruce:** decide on **10 Nov** whether the beta is ready. If it isn't, post on #250 that day.

## Tests and bench (never transmit)

**Unit tests (node, core):**

- contest-layouts snapshot and restore;
- the spectrum-view floor, fit, parts and order options, with the defaults the same as before;
- rtty-pair-snap;
- `target()`: DATA-L with Mark 1415/170 gives click + 1500, and RTTY-L uses half the shift.

**UI harness:**

- the page loads with no failed requests;
- DX SPOTS hides and shows the tags;
- the swap moves the waterfall on top;
- a click on the spectrum, with simulated DATA-L, records exactly one frequency write and **no mode write**;
- the tuner keeps its size while the status text changes;
- the meters show PO/SWR/ALC when TX is *simulated* with `emit`;
- every pop-out page loads.

**Bench (receive only, with Colin's standing permission):**

- Set DATA-L on 20 m or 40 m RTTY.
- Click real signals at 10 and 20 kHz span.
- Press Auto and check that the tuner shows a cross.
- Check the waterfall click.
- Check the spots against the cluster.
- Put two pop-outs on the second monitor and check they come back after a reload.
- Check PiP stays on top over another program.
- Put back the mode, frequency and span afterwards.

**Not tested here:** anything on the TX path. Bruce's real contest use (keying through his logger) is the field test, and the beta notes will say so.

**IWC:** the core changes are opt-in options. After `core-sync -Push`, run `-Pull` in IWC, then check its Radio panel spectrum and run its JS tests.

## Risks

1. **Pop-up blocker (measured).** One click opens one window. Restoring a layout with several pop-outs needs "Always allow pop-ups" for `localhost` (a single choice in the address bar). The page will:
   - detect each blocked `window.open` (it returns null);
   - say how to allow pop-ups;
   - offer a button to open the windows that didn't open.

   Expected: once pop-ups are allowed, every window opens from one click. That needs a bench check in Bruce's browser.
2. **PiP limits.**
   - There is only one PiP window, and script can't set its position (only its size). Whether Chrome remembers where it was put is to be bench-checked.
   - The PiP window closes if the RTTY Contest page reloads or navigates away. The tuner then docks back.
   - PiP works in Chrome and Edge only. In Firefox, On top is hidden and Pop out still works.
3. **S-units: decided, not in the beta.** The scale is dB above the noise floor (Colin, 2026-10-26). The mock-up showed S-units, so the beta notes will say why. The SDR level isn't calibrated to the S-meter, and IPO, ATT and AGC move it.
4. **Two tuners at once.** If both the docked tuner and a tuner pop-out run, they fight over the tuner lease (IdleStop about 15 s, re-post every 5 s). The page will have one tuner instance: Pop out closes the docked one, and Dock closes the window.
5. **Shared core changes.** The changes to `spectrum-view` and `spectrum-pane` reach IWC's Radio panel. All new behaviour is behind options with the current defaults, and IWC is checked after the pull.
6. **Bootstrap on the tuner.** `_RttyTunerPartial` relies on Bootstrap and `site.css`, and the radio-panel styles use `--rp-*` with an `rp-` prefix. The page loads both, as `/RttyTuner` does. This needs checking in phase 1; if the two clash, the tuner goes in its own scoped container.
7. **Schedule.** About 16 working days are planned in 19. Phases 0-3 are the beta. Phase 4 is the part to cut.

## Decisions (Colin, 2026-10-26)

1. Scale: **dB above the noise floor** for the beta. No S-units.
2. Saved layouts: **the same as the other GUIs do now**. Named layouts are on the server, as Experimental's are; window positions are in the browser, as every pop-out's are.
3. Receiver: **a Main/Sub toggle when two SDRs are configured, Main only otherwise.**
4. Experimental: **no RTTY Contest link.** Fabio's GUI is not touched.
5. Phase 4 resizing: **the spectrum/waterfall box and the docked tuner are each sized as a window is**, by a grab on the right edge (width), the bottom edge (height) or the bottom-right corner (both). A box nobody has sized fills its room; a double-click on a grab puts it back to that. The sizes go in a saved layout. A bigger tuner is a bigger scope: the docked scope is the largest square that fits, as in the pop-out. (Colin, 2026-10-26: this replaced a first go with a width grip for the tuner and a STRIP size for the top strip.)
6. Band plan: **in RTTY Contest the mode never follows the band plan**, so the operator can go to any frequency and the radio stays in RTTY. Only this view and its windows do this; Settings is not changed, so the other views go on doing what Settings says.
7. Decode: **a DECODE key on the tuner's bar shows the decoded text with the tuner** and starts the decoder; off stops it and hides the text (Colin, 2026-10-26). It is the tuner's own Decode section and reader, not a second one, so the tuner's window follows the key too. Send stays hidden until it is built. Layouts keep it.
