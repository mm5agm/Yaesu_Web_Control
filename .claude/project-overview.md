# Yaesu Web Control – Project Overview for Claude

This document gives Claude a high-level understanding of the Yaesu Web
Control project: what the application is for, its major subsystems, the
shared-core arrangement with its sibling application, the architectural
philosophy, and the domain concepts used throughout the codebase.

Use it to understand *intent*. `.claude/rules.md` is the enforceable
specification for the frontend subsystem boundaries; `CLAUDE.md` is the
working map of build commands and current architecture detail.

---

## PROJECT PURPOSE

Yaesu Web Control (YWC) is a browser-based control and monitoring interface
for Yaesu HF transceivers — FTdx101MP, FTdx101D, FTdx10, FT-710, and
FTDX3000. It gives the operator real-time metering (S-meter, power, SWR,
ALC), CAT-based frequency/mode/VFO control, an SDR-fed spectrum display,
DX cluster overlay, memory channels and banks, voice control, remote audio
and video, and a rigctld bridge so WSJT-X, JTAlert and Log4OM can share the
radio.

Written in:

- **.NET 10** — backend, multi-targeted (see PLATFORMS below)
- **SignalR** — real-time push to the browser
- **JavaScript ES modules** — frontend
- **Razor Pages** — UI

It is the **origin of Icom Web Control (IWC)** — IWC was cloned from this
codebase and then carved down to Icom CI-V. The two are deliberately
separate applications with separate repositories: YWC stays Yaesu-only, IWC
stays Icom-only. Where a comment or name here looks like it was written with
only Yaesu in mind, that's expected — this is the original, not the fork.

The goal is a professional-grade, modular, maintainable interface that
mirrors the behaviour of the physical radio while remaining easy to extend,
and that a partially-sighted operator can drive by voice.

---

## NO RADIO-AGNOSTIC SEAM YET — THIS MATTERS FOR SHARING CODE

IWC's carve introduced `Services/IRadioController.cs`: a semantic seam where
everything above speaks frequencies, modes and S-units, and exactly one class
below it emits CI-V bytes. **YWC has no equivalent seam.** CAT strings and
Yaesu-specific concepts are still reachable from `CatController`, several
Razor pages, and much of `site.js`.

This is the single biggest reason more code isn't already shared with IWC
via `core/` (see below) — everything that touches the radio, directly or
indirectly, has to stay local until YWC grows the same seam. That back-port
is tracked as its own phase in the shared-core plan; it is not a prerequisite
for anything else, but it is the blocker for the harder half of sharing.

---

## MAJOR SUBSYSTEMS

1. **CAT / serial integration** (`Services/CatMultiplexerService.cs`,
   `MultiplexedCatClient`, `CatMessageDispatcher`)
   - Owns the serial port, reassembles and dispatches CAT responses.
   - Frequencies are parsed and stored as plain Hz integers — no unit
     conversion anywhere downstream.

2. **Gauge system** (`wwwroot/js/guages/`)
   - Renders S-meter, Power, SWR, ALC and the rest via canvas-gauges.
   - All gauge creation goes through `gaugeFactory`; layout logic is
     centralised; meter classes supply configuration only.

3. **Calibration engine** (`wwwroot/js/calibration/`)
   - Converts raw radio values into calibrated meter values.
   - Pure functions, no DOM, no side effects. Single source of truth for
     scaling tables. **`calibration-engine.js` already lives in `core/`** —
     see SHARED CORE below.

4. **SDR spectrum — dual-process** (`Services/Sdr/`,
   `Workers/Yaesu_Sdr_Worker/`)
   - Unlike IWC (whose scope comes over CI-V from the radio itself), YWC's
     spectrum comes from an actual SDR (SDRplay RSP-series or a
     SoapySDR-compatible device) fed from the transceiver's IF output.
   - The SDRplay API enforces one selected device per host process, so
     `SdrManager` spawns a separate `Yaesu_Sdr_Worker.exe` per configured
     device and talks to it over localhost TCP; YWC's main process never
     opens an SDR directly. See `docs/decisions/0001-dual-sdr-architecture.md`.

5. **CAT Scope Control** (`Controllers/ScopeController.cs`) — **not the same
   thing as the SDR panel above**
   - Drives the radio's own front-panel scope over CAT (the `SS` command) —
     span, mode, marker, colour, level, hold, independently for MAIN/SUB.
     This changes what's on the radio's physical screen; it does not stream
     spectrum data into the browser the way the SDR panel does.
   - Useful on its own for CAT-only remote operation, and useful paired with
     Remote Video (below) so the operator can see the result. Currently
     enabled for FTdx101MP/D only — FTdx10/FT-710 tables exist but are
     gated pending bench verification. See
     `docs/design/scope-control-via-cat.md`.

6. **DX cluster** (`Services/DxClusterService.cs`,
   `Controllers/DxClusterController.cs`)
   - Telnet connection to a cluster server; incoming spots overlaid on the
     spectrum and listed in a dedicated panel; watch-list alerts.
   - `Models/DxSpot` — **the spot type itself already lives in `core/`**;
     the service around it (connection handling, watch-list matching) is
     still local — it's on the shared-core migration checklist, not yet
     moved.

7. **Memories** (`Services/MemoryService.cs`, `Services/MemoryBankService.cs`,
   `Controllers/MemoryController.cs`, `Controllers/MemoryBankController.cs`)
   - Per-channel memory storage (frequency, mode, and — where the radio
     supports it — filter/AGC/power state), grouped into named banks the
     operator can save, load, and switch between. Read/save without writing
     to the transceiver unless the operator chooses to.
   - Both services are candidates for `core/` (radio-agnostic once above the
     CAT seam) but haven't moved yet — same blocker as above.

8. **CW decode** (`core/Services/Cw/`, `core/js/cw/`, in progress on
   `feature/cw-reader`) — **shared, and headed for IWC too**
   - Takes mono float audio and a tuned pitch, produces text. Never sees a
     CAT command, a filter code, or a radio model — see
     `core/docs/design/cw-decoder.md` for the full pipeline (FFT tone
     tracking + Goertzel envelope detection + element/readability gating).
   - **The decoder engine itself already lives in `core/`** — don't rewrite
     it. What's still local per app is the radio-specific wiring: audio
     capture source, the CAT-driven pitch/zero-in feedback loop, and the
     Razor/JS panel host. Before starting new CW work in either app, pull
     `core/` first (`./scripts/core-sync.ps1 -Pull`) — the engine may already
     cover what you're about to write.
   - This is the intended shape for shared work going forward: build the
     radio-agnostic core once, wire it into each app separately.

9. **Voice control** (`Services/Voice/`)
   - `VoiceControlService` (SAPI recognition) → `IntentDispatcher` → CAT
     commands, plus `VoiceTtsService` for spoken feedback and status
     announcements. `VoicePhraseStore` / `VoicePhraseValidator` make phrase
     packs user-editable and shareable, matching IWC's approach. Built for
     partially-sighted operators — treat regressions here seriously.
   - Windows-only (SAPI); hidden on the macOS/Linux CAT-only host.

10. **VC Tune** (`Services/VcTune/`) — **YWC-only, permanently**
   - Integration with Yaesu's VC Tune external antenna tuner/preselector —
     command building, response parsing, and a state machine over CAT.
     Yaesu-specific; has no Icom equivalent and is not going to `core/`.
     Confirmed by IWC's own `CLAUDE.md`, which records the VC Tune UI as
     deliberately removed during the carve.

11. **Remote Audio** (`Services/Audio/`) — **not yet shared, but will be**
    - Opus-encoded RX/TX audio over a websocket session
      (`AudioSessionManager`, `RadioAudioBridgeService`), device enumeration,
      and HTTPS certificate handling for the secure context audio capture
      needs in-browser. `core/js/audio/` already holds the transport-layer JS
      (`audio-session.js`, `audio-protocol.js`, `audio-capture.js`,
      `audio-playback.js`); the C# side (`Services/Audio/`) hasn't moved yet.
      Slated to reach IWC — treat new Audio work here as a candidate for
      `core/` on next touch, same as CW decode above.

12. **Remote Video** (`Services/Video/`) — **YWC-only, permanently**
    - Webcam/panadapter-camera capture and MJPEG streaming, with
      platform-specific capture backends (DirectShow/Media Foundation on
      Windows, AVFoundation on macOS, V4L2 on Linux). Not planned for IWC —
      treat this as staying local, unlike Audio above.

13. **External integration**
    - `RigctldServer` (Hamlib TCP bridge for WSJT-X/Log4OM/JTAlert),
      `WsjtxUdpService` (WSJT-X UDP status feed).

---

## SHARED CORE (`core/`)

YWC and IWC share code through **[`Radio_Web_Control_Core`](https://github.com/mm5agm/Radio_Web_Control_Core)**,
consumed here as a **git subtree at `core/`** (not a submodule, not a NuGet
package — a plain clone of this repo must still build with no extra steps).

**Why:** IWC was cloned from YWC. A 2026-08-13 measurement found 62 of 89
first-party files at shared paths between the two repos were effectively
identical — the same code, maintained twice. `core/` exists to stop that.

**The rule for what belongs there:** if it needs to know what a radio is, it
doesn't go in `core/`. No CAT, no CI-V, no serial framing, no
`IRadioController` or Yaesu equivalent of it. Everything else that's
radio-agnostic — DX cluster handling, memories, ADIF, calibration maths,
meter rendering, the SignalR transport — is eligible, and is shared
*informally already*; moving the file just makes that fact visible.

**What's actually moved so far** (check `core/docs/design/shared-core-plan.md`
for the live, authoritative checklist before assuming anything here is
current):
- `Models/DxSpot.cs` — Phase 1, the plumbing proof.
- `Services/AdifParser.cs` and `wwwroot/js/calibration/calibration-engine.js`
  — Phase 2, and the first two things in either codebase to get automated
  tests.
- `Services/Cw/*` (decoder engine) and `js/cw/*` (reader panel JS) — built
  directly in `core/` on the `feature/cw-reader` branch, not migrated in
  after the fact. This is the model to follow for Remote Audio next.
- `js/audio/*` (transport-layer JS only — session, protocol, capture,
  playback) — the C# side of Remote Audio hasn't followed yet.

**What's eligible but not yet moved:** `DxClusterService`, `MemoryService`,
`MemoryBankService`, the meter-gauge JS modules, the SignalR transport
modules, `VoicePackMetadata`, `Services/Audio/*` (the C# half), and more —
see the checklist. The rule is **move on next touch**, not batch migration:
when you'd be editing one of these for its own reason anyway, move it into
`core/` as part of that same change. Don't move files opportunistically just
because they're on the list.

**What will never move:** VC Tune and Remote Video. Both are permanently
YWC-only — there's no Icom VC Tune, and no plan to bring Remote Video to IWC.
Don't propose moving these into `core/` even if a future refactor makes them
look radio-agnostic on paper; the "never" here is a product decision, not an
architectural one.

**Getting a change into both apps:**

```powershell
./scripts/core-sync.ps1 -Check   # is anything owed upstream?
./scripts/core-sync.ps1 -Push    # send core/ commits up (pulls first)
./scripts/core-sync.ps1 -Pull    # bring IWC's core work down
```

Author changes to `core/` **inside this repo** (or inside IWC), never in a
standalone clone of `Radio_Web_Control_Core` — a standalone clone isn't
compiled against a real consumer, so a subtle break won't show up until
someone pulls it. Push is not optional once `core/` has changed in a session
— run `-Check` before ending a session that touched it.

**Constraints specific to `core/`:**
- Targets `net10.0`, never `net10.0-windows` — YWC's macOS/Linux CAT-only
  host depends on this; a Windows-only dependency in `core/` would build
  fine against IWC and silently break YWC's second target framework.
- No ASP.NET Core references, no DI, no hosting — consumers wire it up.
- `<Compile Remove="core\**" />` must stay in `Yaesu_Web_Control.csproj` —
  the Web SDK globs `**/*.cs`, so without the exclusion every file in
  `core/` compiles twice.
- `core/js/**/*.js` is copied — not linked — into `wwwroot/js/` by a build
  target; edit the `core/` copy, never the generated `wwwroot` one.

---

## PLATFORMS

- **Windows (`net10.0-windows`)** — full product: WinForms tray, SDR
  workers, Voice Control (SAPI). Shipped via NSIS installer.
- **macOS / Linux (`net10.0`)** — CAT + web UI only. macOS has an Avalonia
  menu-bar tray; Linux is console (or Docker). SDR and Voice Control are
  compiled/gated out. See `USER_MANUAL.md` §1 / §15.10 and `CLAUDE.md`'s
  operational-differences table.

---

## ARCHITECTURAL PHILOSOPHY

- Single-responsibility modules.
- No duplication of logic or configuration — that's the entire reason
  `core/` exists.
- No global variables; no magic strings.
- Pure functions where possible (calibration engine, ADIF parsing).
- Clear separation of UI, calibration, and data flow.
- ES module imports for all frontend code.
- **No layout logic outside `gauge.js`.** Meter classes extend the base
  Gauge and supply configuration only — they don't position anything.
  `wwwroot/js/guages/gauge.js` is the single site where a `RadialGauge`
  is constructed; keep it that way.
- Empirical hardware findings (CAT quirks, SDR struct offsets, timing) are
  recorded in comments precisely because they were established once, at the
  bench, and the comment is the only record of them. Don't remove or
  "clean up" a comment like that without checking it isn't load-bearing.

The codebase should feel like it was written by a disciplined engineering
team.

---

## DOMAIN CONCEPTS

Claude should understand:

- **CAT** — Yaesu's ASCII command protocol over serial. Commands and
  responses are semicolon-terminated strings (e.g. `FA000880600;`).
- **Frequencies are always in Hz** in this codebase — no unit conversion
  between the CAT layer and anything above it.
- **S-meter** — 0–255 raw → S0 to S9+60 dB via the calibration tables.
- **Meter polling is tiered**, not a flat rate — fast tier every cycle
  (TX state, S-meter), TX-only tier (power/ALC/compression/SWR), and a slow
  tier (temperature, VDD, antenna) every ~2 s. See `CLAUDE.md` for the exact
  breakdown; don't trust older comments citing a flat "~10 Hz" figure.
- **Single vs dual receiver** — the FTdx101MP/D are dual-receiver; the
  FTdx10, FT-710 and FTDX3000 are single-receiver with two VFOs used for
  split/memory, not simultaneous listening.
- **Three separate scope-related systems, easy to conflate:** the SDR
  spectrum panel (external hardware, FFT drawn in-browser), CAT Scope
  Control (the `SS` command, changes the radio's own front-panel display —
  unlike IWC, where the scope comes natively over CI-V), and Remote Video
  (captures whatever the radio's screen currently shows, including the
  effect of CAT Scope Control). None of the three substitutes for another.

---

## GOALS FOR GENERATED CODE

Code should be:

- Modular and predictable
- Consistent with existing patterns
- Free of duplication — check whether new radio-agnostic code belongs in
  `core/` before writing it locally
- ES-module-friendly on the frontend
- Efficient for real-time updates
- Aware of which platform(s) it needs to run on (Windows-only subsystems —
  SDR, Voice — must stay compiled out of the `net10.0` CAT-only host)
- Accompanied by an accessibility label and, where it adds an
  operator-facing control, a voice intent
