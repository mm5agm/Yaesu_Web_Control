# Pop-out windows for the CW reader, RTTY tuner, Audio Filter and DX Spots

Status: **PR 1 (groundwork + CW reader) built 2026-10-01** on
`feature/popout-cw-reader` (PR #195); 2a (per-window lease) is PR #196; 2b (RTTY tuner pop-out, resize, tones only) built 2026-10-01 on `feature/popout-rtty-tuner` (PR #197); 3 (Audio Filter) is PR #198; 4 (DX Spots) built 2026-10-01 on `feature/popout-dx-spots`. Planned 2026-09-30. Asked for by Rick W2JAZ on #190,
who wants the CW reader on a second monitor for a club demo in November.

## The problem

These four are `<dialog>` elements inside `Index.cshtml`. They can be dragged
and resized, but nothing inside a page can leave the browser window, so none
of them can reach a second monitor. The only way out is a second window.

YWC already has two pop-outs that do exactly this, and this work follows them:

- Radio Display: `window.open('/RadioDisplay', 'ywc-radio-display', ...)`
  (`wwwroot/js/video/radio-display-ui.js:1232`)
- Remote Audio: `window.open('/RemoteAudio', ...)`
  (`wwwroot/js/audio/remote-audio-ui.js:173`)

Each is its own small page, and a `BroadcastChannel` keeps it and the main
page in step.

## What each panel needs, measured from the code

| Panel | Data source | Shared or local | Server change | Size |
|---|---|---|---|---|
| CW reader | HTTP polling of `/api/cw/*` | panel JS in `core/js/cw/` | none | small |
| RTTY tuner | HTTP polling of `/api/rtty/*` | panel JS in `core/js/rtty/` | **yes, see below** | small-medium |
| Audio Filter (per VFO) | HTTP `/api/cat/audiofilter/{vfo}` | YWC-local, inline in `Index.cshtml` | none | small |
| DX Spots | **SignalR** `DxSpot` events + `/api/dxcluster/spots` | YWC-local `wwwroot/js/ui/dx-spots-panel.js` | none | medium |

### CW reader

- The decoder runs once on the server. `/api/cw/poll?since=` takes a cursor
  per caller (`Controllers/CwController.cs:123`), so a second window reads
  the same copy without disturbing the first.
- Start, Stop, Clear and Reader Mode are server state already, so they act
  on both windows. That's correct behaviour for this.
- `CwReaderPanel` finds its elements by fixed ids. Moving the
  `#cwReaderDialog` markup out of `Index.cshtml` into a partial
  (`Pages/Shared/_CwReaderPartial.cshtml`) lets both pages use the same
  markup, so the panel JS may need no change at all. If it does need one
  (for example to run as a plain panel rather than a `<dialog>`), that
  change goes in `core/`, and IWC gets it too.
- The QSO log form (`cw-qso-form.js`) is a separate dialog. Leave it on the
  main page in phase 1, and put it in the pop-out only if someone asks.

### RTTY tuner

- **Found while planning: closing the tuner in one window stops it in every
  window.** Closing the dialog POSTs `/api/rtty/tuner/stop`, and
  `RttyTunerService.RequestStop()` lets the audio go a couple of seconds
  later, whoever else is watching. This already affects a PC and a tablet
  used together today. A pop-out would make it routine: popping the tuner
  out closes the in-page one, which would stop the pop-out's tuner.
- Fix: give each window a lease. `start` returns or accepts a client id, and
  `stop` releases only that id. The service stops when no leases remain, or
  when none has polled for `IdleStop` (15 s, which already exists). This is
  `Services/Rtty/RttyTunerService.cs` (YWC C#). Check whether IWC has the
  same service, and if so do the same there.
- Mark / Shift / Rev live in `localStorage` (`core/js/rtty/rtty-settings.js`).
  That's the same origin, so both windows already see the same settings.
- This is protocol-adjacent (audio capture hold/release), so it needs a
  bench check against the radio, not only a build.

### Audio Filter

- Per VFO, backed by `/api/cat/audiofilter/{vfo}`. The radio holds all the
  state, so the dialog is a thin read/write proxy.
- It's inline script in `Index.cshtml` (`window.audioFilter`,
  around line 5610). That script needs moving into a module
  (`wwwroot/js/ui/audio-filter-panel.js`) so the pop-out page can load it.
- It reads Yaesu EX-menu values, so it stays in YWC, not `core/`.
- The pop-out page is `/AudioFilter?vfo=A` (or B).

### DX Spots

- The panel is fed by SignalR `DxSpot` events. A new window would start
  empty, so the pop-out page needs to:
  1. load the current list from `GET /api/dxcluster/spots` (already there,
     `Controllers/DxClusterController.cs:190`)
  2. open its own SignalR connection for new spots.
- Click-to-QSY calls `window.setMode(...)` (`dx-spots-panel.js:284`), which
  lives in the main page's `site.js`. The pop-out has no such function, so
  clicking a spot there must go through the HTTP frequency and mode
  endpoints instead.
- The band filter follows the operator's current band, so the pop-out also
  needs `FrequencyA` / `FrequencyB` from SignalR.
- This is the biggest of the four. Do it last.

## Shared groundwork (do first)

A small radio-agnostic helper in **`core/js/popout/popout.js`**. It's a
browser module that talks to nothing radio-specific, so by the rules in
`CLAUDE.md` it belongs in core. It would do:

- `openPopout(name, url, defaultSize)`: `window.open` with a fixed window
  name, so a second click focuses the existing window instead of opening
  another.
- Remember each pop-out's size and position in `localStorage` and pass them
  back on the next open.
- A `BroadcastChannel` handshake, so the main page knows a panel is popped
  out and can:
  - close its in-page dialog
  - change the panel's button to "In pop-out" (click brings the window to
    the front)
  - offer **Reattach**, which closes the pop-out and reopens the dialog in
    the page. This is the same idea as Radio Display's Reattach.
- When the pop-out window closes, tell the main page, so the button goes
  back to normal.

**Known limit on window position.** Chrome and Edge only let a page place a
new window on a *different* monitor if the page has the Window Management
permission. Without it, `left`/`top` are clamped to the monitor the main
page is on. So the first time, the operator drags the pop-out to the second
monitor. Whether the browser then puts it back there on its own when it's
reopened needs testing. Don't promise it until it's been tested. Asking for
the permission is possible later if it turns out to matter.

## Mode guard: option A agreed (Colin, 2026-09-30)

Today `ModePanelGuard` (`Index.cshtml` around line 3415) **closes** the CW
reader when the mode leaves CW, and the RTTY tuner when the mode leaves
RTTY / DATA / SSB. A pop-out window closing itself whenever the mode changes
would be jarring, and it would be a second window disappearing from a second
monitor. Two options:

- **A (agreed):** the pop-out stays open and shows "Not in CW mode, so
  the reader is paused" until the mode comes back. It needs the mode, from a
  small SignalR connection or a poll of the mode endpoint.
- **B:** the pop-out closes itself, the same as the dialog does today.

## Order of work

Five PRs, one branch each, merged in this order. Each is testable on its
own, so a problem in one doesn't hold up the others.

1. **Groundwork + CW reader** (one PR). `core/js/popout/`, the `/CwReader`
   page, the partial, and a pop-out button on the reader. The helper goes in
   with its first user, which is where its design gets proved before three
   more panels depend on it. This is what Rick asked for, so it's first and
   can go into a pre-release on its own.
2. **2a - RTTY tuner stop fix** (bug fix, no pop-out). The per-window lease
   in `RttyTunerService`. It fixes the PC + tablet case that exists today, so
   it doesn't wait for the pop-out. Needs its own bench check (audio capture
   hold/release).
3. **2b - RTTY tuner pop-out.** The `/RttyTuner` page, built on 2a.
4. **3 - Audio Filter.** Move the inline script into a module, then the
   `/AudioFilter?vfo=` page.
5. **4 - DX Spots.** The page, initial load from `/api/dxcluster/spots`, its
   own SignalR connection, and click-to-QSY over HTTP.

Each PR adds its own row to the README "Fixed since the last release"
table, so it's clear which pre-release has which.

After phase 1, core owes a push. Run `./scripts/core-sync.ps1 -Push`, then
`-Pull` in IWC. IWC could then add its own `/CwReader` page (and the RTTY
tuner one) later, as a separate IWC job.

## Testing

- Unit tests: whatever is pure in `popout.js` (the size/position clamping)
  goes in `core/tests`. 2a's lease logic goes in
  `Tests/YaesuWebControl.Tests` (the only test project CI runs).
- Browser, per phase:
  - pop out, drag to the second monitor, close, pop out again
  - Reattach
  - main page refresh while popped out
  - pop-out open on the PC while the tablet uses the same panel
  - Chrome, Edge and Firefox
- RTTY: bench check the audio hold/release with two windows open, then close
  each in turn.
- Colin has a second monitor on the shack PC (main 40 inch, second
  27 inch), so placement across monitors can be tested for real.

## Docs

- `USER_MANUAL.md`: a short "Pop-out windows" subsection, plus a line in
  §20 (CW reader), §22 (RTTY tuner), §5.18 (Audio Filter) and §5.17 (DX
  Spots).
- `README.md`: a row per phase in the "Fixed since the last release" table
  (or its new-features equivalent), as each is merged.
