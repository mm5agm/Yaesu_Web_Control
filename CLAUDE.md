# CLAUDE.md

Guidance for Claude Code (claude.ai/code) when working in this repository.

> ## ⭐ START HERE
>
> **Yaesu Web Control (YWC)** is a browser-based control and monitoring
> interface for Yaesu HF transceivers — **FTdx101MP, FTdx101D, FTdx10,
> FT-710, FTDX3000** — over CAT. It's a mature, working application, not
> scaffolding: metering, an SDR-fed spectrum display, DX cluster overlay,
> memory channels/banks, voice control, remote audio, remote video, a VC
> Tune tuner integration, and a rigctld bridge for WSJT-X/Log4OM.
>
> - **Work on `develop`.** `main` only receives merges at release time and
>   lags noticeably — don't treat `main` as the current state of the app.
> - **Origin of Icom Web Control (IWC)** (`mm5agm/Icom_Web_Control`), which
>   was cloned from this codebase and carved down to Icom CI-V. Keep Icom
>   concerns out of here and vice-versa.
> - **Shared code lives in `core/`**, a git subtree of
>   [`Radio_Web_Control_Core`](https://github.com/mm5agm/Radio_Web_Control_Core).
>   See "Shared core" below — this is a hard rule, not a suggestion.
> - **No `IRadioController`-style seam yet.** IWC has one; YWC doesn't. This
>   is *the* reason more code isn't already shared — see "Shared core."
> - **Not fully covered by tests.** `Tests/YaesuWebControl.Tests` exists and
>   is real (JPEG SOF parsing, video disconnect/halt and tiled-frame logic,
>   `RM0` meter parsing, SWR broadcast, per-model max power, mode/VFO
>   routing) but doesn't
>   touch UI, live CAT transport, SDR, or Audio paths. Everything else is
>   manual verification in the browser at `http://localhost:8080`, against
>   the radio. When a change is protocol-level (CAT commands, meter scaling,
>   SDR device I/O), say so and let Colin bench-check it — do not report
>   that behaviour as verified on the strength of a build or unit test
>   succeeding.

## Architecture Rules

Before making any changes, read `.claude/rules.md` and `.claude/project-overview.md`.
They are non-negotiable and override default behaviour.

**`.claude/rules.md` predates several subsystems.** It's the enforceable
spec for the original gauge/calibration/WebSocket frontend pipeline, and
those rules still apply exactly as written to that code. It says nothing
about Audio, Video, SDR, Voice, VC Tune, or CW — don't read their absence as
permission to skip architectural discipline there; apply the same spirit
(single-responsibility modules, no cross-layer leakage, no global state) and
flag it if you think `rules.md` should be extended to cover them explicitly.

### Claude: standing instructions

1. Before writing any new file, ask whether it's radio-agnostic. If it is,
   it likely belongs in `core/` — see "Shared core." Say so at the time
   rather than moving it later.
2. Before starting new work in Audio or CW decode specifically, pull
   `core/` first (`./scripts/core-sync.ps1 -Pull`). Both are shared or
   partially-shared already — the thing you're about to write may exist.
3. VC Tune and Remote Video are **permanently YWC-only**. Don't propose
   moving either into `core/`, even if a refactor makes them look
   radio-agnostic on paper — that's a product decision already made, not an
   architectural judgement call.
4. Run `./scripts/core-sync.ps1 -Check` at the end of any session in which
   anything under `core/` changed. If it reports work owed upstream, push it
   — don't leave `core/` changes uncommitted upward for someone else to
   notice later.
5. Protocol-level changes (CAT commands, meter calibration, SDR device I/O)
   need a bench check against real hardware. Never report these as verified
   because a build or unit test succeeded.
6. Pushing to `origin`, tagging, releasing, and opening PRs all need Colin's
   explicit word, as before.

---

## Build & Run

**Targets:** multi-TFM — Windows product (`net10.0-windows`, WinExe + WinForms tray/voice/SDR) and CAT-only host (`net10.0`, console; macOS/Linux).

```bash
# Build both TFMs (on Windows) or the portable TFM (on macOS/Linux)
dotnet build Yaesu_Web_Control.csproj

# Windows product (tray + voice + SDR)
dotnet build -f net10.0-windows
dotnet run --project Yaesu_Web_Control.csproj --framework net10.0-windows

# CAT-only host (macOS / Linux / Windows without WinForms features)
dotnet build -f net10.0
dotnet run --project Yaesu_Web_Control.csproj --framework net10.0
# then open http://localhost:8080 — on macOS a menu-bar status item provides
# Open / About / Open user data folder / Exit (Ctrl+C also works)

# Publish Windows self-contained installer input
dotnet publish -c Release -f net10.0-windows -r win-x64 --self-contained
```

On macOS, set **Serial Port** to a `/dev/cu.*` device. On Linux, use `/dev/ttyUSB*` or `/dev/ttyACM*`. SDR spectrum and Voice Control are Windows-only and are hidden on the CAT-only host.

**USB CAT:** install the [Silicon Labs CP210x VCP driver](https://www.silabs.com/software-and-tools/usb-to-uart-bridge-vcp-drivers?tab=downloads) on Windows, macOS, and Linux, then **reboot the host** before first use (see USER_MANUAL §2.4).

**Docker (linux/amd64 + linux/arm64):** `Dockerfile` + `docker-compose.yml` publish the `net10.0` CAT-only host. Data volume is `XDG_CONFIG_HOME=/data` → `MM5AGM/Yaesu Web Control/`. Entrypoint starts as root, `chown`s `/data` to `app`, then drops privileges (preserving compose `group_add` GIDs). Pass the serial device with `devices:` / `YWC_SERIAL_DEVICE`. For Remote Audio, compose maps `/dev/snd` and `group_add`s host `audio` (`YWC_AUDIO_GID`); the image installs `libasound2t64` + `libportaudio2`. Container runs with auto-shutdown and local browser-open disabled. Install the Silicon Labs driver on the **host** and reboot before mapping the device into the container.

### Operational differences (Windows vs macOS/Linux)

| Concern | `net10.0-windows` | `net10.0` (macOS/Linux/Docker) |
|---|---|---|
| Process chrome | WinForms `SystemTrayService` | macOS: Avalonia `MacSystemTrayService` on main thread after `StartAsync`; Linux/Docker: `app.Run()` console |
| Compiled out | — | Tray WinForms, Voice SAPI/NAudio, `SdrController`, SDR worker ProjectReference |
| Settings UI gates | `IsWindowsHost == true` | `IsWindowsHost == false` — hides SDR / Voice / Windows-only panels |
| Serial validation | `COM*` | `/dev/…` |
| AppData | `%APPDATA%\MM5AGM\Yaesu Web Control\` | `$XDG_CONFIG_HOME` or `~/.config/MM5AGM/Yaesu Web Control/` |
| `AutoShutdownWhenNoBrowsers` | Default true | Default true; `HostRuntime.IsContainer` forces keep-alive |
| Browser auto-open | `OpenBrowserOnStartup` (default true) | Same setting; skipped when `HostRuntime.IsContainer` |
| Operator-facing docs | `USER_MANUAL.md` §§1–4, 6.2–6.3, 15.10, 17 | Same |

Most verification is manual, via the browser at `http://localhost:8080`. There
is a small unit-test project covering the parts where a wrong answer is silent
rather than visible — JPEG SOF parsing, video disconnect/halt and
tiled-frame logic, `RM0` meter parsing, SWR meter broadcast, per-model max
power, and mode/VFO routing:

```bash
dotnet test Tests/YaesuWebControl.Tests/YaesuWebControl.Tests.csproj
```

It targets `net10.0`, so it runs on any host. Nothing in the UI, live CAT
transport, SDR, or Audio paths is covered by it — see the standing
instructions above on reporting protocol-level changes as verified.

`Tests/test-api.ps1` is a separate, manual API-poking script — not part of
the automated suite.

---

## Release Process

Before releasing, bump the version in **all three** files — five sites in total:

- `Models/AppVersion.cs` — `Current` (and `ReleaseDate`)
- `installer.nsi` — `!define VERSION`
- `Yaesu_Web_Control.csproj` — `<Version>`, `<FileVersion>`, `<AssemblyVersion>`
  (the last two are four-part: `X.Y.Z.0`)

`.\scripts\bump-version.ps1 -Version X.Y.Z` does all five, plus the README
badge. The csproj was only added to it in August 2026 — before that nothing
watched it, and it sat on `1.5.6` from the May release right through to `2.4.2`,
so every installer built in between reported a meaningless version in its file
properties. `finish-release.ps1` now refuses to release unless all five agree.

Then update the documentation:

- `README.md` — add the release-notes entry (date-first heading, e.g.
  `## 2026-08-01 - v2.4.2`), and bump the per-release badge in the shields.io
  URL near the top. Pre-releases get their own heading too, but **not** a badge
  bump — the badge tracks full releases only.
- `USER_MANUAL.md` — bring every section the release touches in line with what
  the app now does, and re-capture any screenshot the change makes wrong.

Do not start the git steps until both documents are done. `finish-release.ps1`
helps with the mechanical half — it rewrites the version strings in the manual
and the README badge — but it refuses to release at all unless you have written
the README release-notes entry yourself, and it can only *warn* that the manual
looks stale, never judge whether a section is right.

```powershell
# 1. Commit everything on develop
git add -A
git commit -m "Release vX.Y.Z: ..."

# 2. Merge to main and tag
git checkout main
git merge develop --no-ff -m "Release vX.Y.Z"
git tag vX.Y.Z
git checkout develop

# 3. Push branches and tag
git push origin develop
git push origin main
git push origin vX.Y.Z

# 4. Create the GitHub Release — this triggers the build workflow
gh release create vX.Y.Z --title "vX.Y.Z" --notes "See README.md for full release notes."
```

**Step 4 is required.** The build workflow triggers on `release: [created]`, not on tag push alone.

`.\scripts\finish-release.ps1 -Version vX.Y.Z` does all of the above, with the
version and documentation checks in front of it, and stops before tagging if
anything is wrong. Prefer it to the raw commands: run by hand, the merge can
conflict and leave `main` unmerged while the tag and release go out anyway,
which is how the sibling repo (IWC) shipped v1.0.0's code as v1.0.3.

**Do not use `.\scripts\create-release.ps1`.** It is an older route to the same
place, it has none of those checks, and it generates release notes from git log.

User settings persist to `%APPDATA%\MM5AGM\Yaesu Web Control\appsettings.user.json` on Windows, or `~/.config/MM5AGM/Yaesu Web Control/appsettings.user.json` on macOS/Linux (Docker: under the `/data` volume).
Radio state persists to the same folder as `radio_state.json`.

---

## Shared core (`core/`)

`core/` is a **git subtree** of [Radio_Web_Control_Core](https://github.com/mm5agm/Radio_Web_Control_Core),
shared with Icom Web Control. It is not a vendored copy and it is not a snapshot.

**If code is radio-agnostic, it belongs in `core/`.** Not "for now", not
"until it settles". The exception is code that genuinely cannot be shared —
see the table below, and the "permanently YWC-only" note on VC Tune and
Remote Video in the standing instructions above.

### What's already there

- `Models/DxSpot.cs`, `Services/AdifParser.cs`, `js/calibration/calibration-engine.js`
  — the original plumbing proof and first-tested pure functions.
- `Services/Cw/*` (decoder engine) and `js/cw/*` (reader panel JS) — built
  directly in `core/` on `feature/cw-reader`, not migrated in after the fact.
  This is the model to follow going forward: build shared work in `core/`
  from the start rather than writing it locally and moving it later.
- `js/audio/*` — the transport-layer JS for Remote Audio (session, protocol,
  capture, playback) is already shared. The C# side (`Services/Audio/`)
  isn't yet — treat it as a candidate on next touch, not something to
  batch-migrate.

### Does it belong in core?

| goes in `core/` | stays in this repo |
|---|---|
| Signal processing, decoders, DSP | CAT framing and command building |
| Data models exchanged with other tools (ADIF, DX spots) | Anything reading a radio-specific register or code |
| Pure algorithms with no radio in them | Per-radio lookup tables and calibration numbers |
| Browser modules that only talk to an HTTP API | Anything touching this app's DI, hubs, or Razor pages |
| Tests for all of the above | VC Tune and Remote Video, permanently — see standing instructions |

The seam is **the radio** — but note YWC doesn't have a clean semantic seam
the way IWC does (no `IRadioController` equivalent), so judgement is needed
here more often than in IWC: if a file's logic would be unchanged talking to
an Icom instead of a Yaesu, it's a candidate, even if it currently imports
something CAT-flavoured that could be refactored out.

### The workflow, and the step that gets forgotten

Authoring happens **inside `core/` in whichever app you're working in**. The
push up to Radio_Web_Control_Core is a **separate command**, and it's the one
that gets missed.

```powershell
./scripts/core-sync.ps1 -Check   # is anything owed upstream?
./scripts/core-sync.ps1 -Push    # send core/ commits up (pulls first)
./scripts/core-sync.ps1 -Pull    # bring IWC's core work down
```

`-Push` refuses on a dirty tree, because `git subtree split` only sees
committed content and would silently leave uncommitted `core/` work behind.

Never author feature changes to `core/` in a standalone clone of
`Radio_Web_Control_Core` — a standalone clone isn't compiled against a real
consumer, so a subtle break won't surface until someone pulls it. Work
inside this repo's `core/`, or IWC's.

### Constraints specific to `core/`

- **Targets `net10.0`, never `net10.0-windows`.** YWC's macOS/Linux CAT-only
  host depends on this; a Windows-only dependency here builds fine against
  IWC and silently breaks YWC's second target framework.
- **No `Microsoft.AspNetCore.*`, no DI, no hosting.** Consumers wire this up.
- **`<Compile Remove="core\**" />` is mandatory** in `Yaesu_Web_Control.csproj`
  — the Web SDK globs `**/*.cs`, so without it every file in the subtree
  compiles twice: once into `RadioWebControl.Core.dll`, once directly into
  the application, and the error names duplicate types rather than the cause.
- **`core/js/**/*.js` is copied, not linked**, into `wwwroot/js/` by a build
  target which also writes the `.gitignore` for the copies. Edit the `core/`
  copy; never edit the generated `wwwroot` one.
- **Line endings are LF.** Both apps have `* text=auto`, so commits normalise
  to LF on the way in — don't introduce CRLF inside `core/`.

`core/docs/design/shared-core-plan.md` is the live migration checklist — the
single source of truth for what's moved, what's eligible, and what's parked.
Check it before assuming anything above is still current.

---

## Backend Architecture

### Service Dependency Map

```
RadioInitializationService (IHostedService)
  └─ opens serial port via CatMultiplexerService
       └─ MultiplexedCatClient (ICatClient)
            └─ CatMessageDispatcher → RadioStateService → SignalR (RadioHub)
                                                        → RadioStatePersistenceService

MeterPollingService (IHostedService)  — tiered CAT polling; see Key Domain Facts
SdrManager (IHostedService)           — supervises one Yaesu_Sdr_Worker.exe per
                                         configured SDR; reads FFT frames over
                                         localhost TCP, broadcasts via SignalR
RigctldServer (IHostedService)        — rigctld TCP interface for WSJT-X etc.
WsjtxUdpService (IHostedService)      — WSJT-X UDP status/QSO feed
DxClusterService (IHostedService)     — cluster telnet feed → DX spot overlay
VoiceControlService (IHostedService)  — SAPI recognition → IntentDispatcher
RadioAudioBridgeService (IHostedService) — Remote Audio session bridge
SystemTrayService / MacSystemTrayService (IHostedService)
```

Plus request-driven services not hosted as background loops: `MemoryService`,
`MemoryBankService`, `VcTuneService` (and its command builder / response
parser / state machine in `Services/VcTune/`), `VideoCaptureService`,
`VideoSessionManager`, `CalibrationService`, `SettingsService`.

### SignalR Message Envelope

All real-time updates use a single hub method `RadioStateUpdate` with envelope `{ property, value }`.
The frontend's `WsUpdatePipeline` routes on `property`. The same hub carries:
- CAT state (FrequencyA, FrequencyB, PowerMeter, SMeter, etc.)
- SDR lifecycle (sdrId-tagged from v2.3.0):
  - `SdrStatus`     value = `{ sdrId, status }` — "unconfigured" / "connecting" / "streaming" / "disconnected" / "nodll"
  - `SdrError`      value = `{ sdrId, error  }` — human-readable detail
- SDR spectrum frames:
  - `SpectrumUpdate` value = `{ sdrId, bins, centreHz, spanHz }`
- DX cluster: `DxSpot`, `DxClusterStatus`, `DxAlert`

`sdrId` is `"A"` or `"B"`. `SdrSpectrumPipeline` routes by sdrId to the appropriate `SpectrumPanel` instance.

### CAT Frequency Format

`CatMessageDispatcher` parses `FA` / `FB` CAT responses. The FTdx101 sends frequencies as a plain integer string in **Hz** (e.g. `FA000880600;` = 880,600 Hz = 880.6 kHz). Values are stored and broadcast in Hz with no unit conversion. The FTdx101MP range is 30 kHz–75 MHz.

### Settings

`SettingsService` reads/writes `appsettings.user.json` via a read-modify-write pattern.
`Settings.cshtml.cs`: `ModelState.Remove("Settings.SdrDeviceKeyA")` and `Settings.SdrDeviceKeyB` **must** appear before `ModelState.IsValid` — `<Nullable>enable</Nullable>` adds implicit `[Required]` to non-nullable strings, which silently blocks saves of empty values otherwise. The legacy `Settings.SdrDeviceKey` is also removed for the same reason; it's kept on the model as a migration anchor only.

**v2.2.x → v2.3.0 SDR settings migration:** the single `SdrDeviceKey` split into per-VFO `SdrDeviceKeyA` / `SdrDeviceKeyB`. `SettingsService.MigrateSdrDeviceKey` auto-promotes any legacy value into `SdrDeviceKeyA` on read; the legacy field is cleared on the next save.

### SDR Subsystem — Dual-process architecture (v2.3.0+)

The SDRplay API v3 service enforces one Selected device per host process (confirmed by [scripts/probe/](../scripts/probe/) — see [docs/decisions/0001-dual-sdr-architecture.md](../docs/decisions/0001-dual-sdr-architecture.md) for the four-pattern probe evidence). So:

- **YWC main never opens an SDR directly.**
- **`SdrManager`** (`Services/Sdr/SdrManager.cs`) spawns one `Yaesu_Sdr_Worker.exe` per configured device, connects to its localhost TCP port, reads FFT frames via `FrameReader`, and broadcasts them via SignalR with sdrId tagging.
- **`Yaesu_Sdr_Worker`** (`Workers/Yaesu_Sdr_Worker/`) — separate `.exe` per SDR. Each holds exactly one device. File-links the device code from `Services/Sdr/` so one source of truth.
- **`WorkerProcess`** — spawn/stop/locate the worker exe; picks free TCP port from 17001-17099; pipes worker stderr into the main Serilog stream.
- **Wire protocol** — length-prefixed binary, big-endian. Three message types: `SpectrumFrame` (sequence + centreHz + spanHz + bins[]), `StatusUpdate` (string), `ErrorReport` (string). See `Workers/Yaesu_Sdr_Worker/WireProtocol.cs` (writer) and `Services/Sdr/FrameReader.cs` (reader).
- **Build pipeline** — YWC's `.csproj` has a `<ProjectReference>` (no DLL link) so the worker builds first; `<None>` items with `CopyToOutputDirectory`/`CopyToPublishDirectory` land the worker exe alongside YWC's main exe. `installer.nsi` picks it up automatically via `File /r "publish\*"`.

Device code (still in `Services/Sdr/` but linked into both projects):
- `SdrplayDevice` — P/Invoke into `sdrplay_api.dll` (SDRplay API v3). Critical struct offsets verified against `C:\Program Files\SDRplay\API\inc\sdrplay_api_tuner.h`:
  - `tunerParams.bwType` @ offset 0
  - `tunerParams.gain.gRdB` @ offset 12 (`int`)
  - `tunerParams.gain.LNAstate` @ offset 16 (`unsigned char` — **not** int)
  - `tunerParams.rfFreq.rfHz` @ offset **40** (gain is 24 bytes; padding aligns double to 8-byte boundary)
  - `tunerParams.dcOffsetTuner.refreshRateTime` @ offset **64** (`sizeof(RfFreqT)`=16 due to 7-byte tail padding after syncUpdate uchar)
  - `ctrlParams.decimation.enable` @ offset **74**, `.decimationFactor` @ **75** (`sizeof(TunerParamsT)`=72)
  - `devParams.fsFreq.fsHz` @ offset 8 within DevParamsT
- `SoapySdrDevice` — SoapySDR wrapper for RTL-SDR, Airspy, etc.
- `FftProcessor` — Hann-windowed FFT → dBFS bins.

### CAT Scope Control — a separate thing from the SDR panel

`Controllers/ScopeController.cs` (`api/scope`) drives the radio's **own**
front-panel scope over CAT — the `SS` command — reading and writing span,
mode, marker, colour, level, and hold, independently for MAIN and SUB.
This is unrelated to the SDR spectrum panel above: it doesn't stream FFT
bins into the browser, it changes what the radio itself is displaying on
its physical screen. Two reasons that's useful: paired with Remote Video, an
operator can see the effect (the captured TFT image changes); and it's
useful standalone, for anyone driving the rig CAT-only with no capture
hardware at all.

- **Currently enabled for FTdx101MP/D only** (bench-verified). FTdx10/FT-710
  tables exist in code but are gated off behind
  `RadioCapabilities.SupportsSpectrumScopeCat` until someone runs the write
  probe (`scripts/probe/ss-probe.ps1`) against one — these are writes to an
  operator's front panel, so unverified per-model tables don't ship enabled.
- **Frame format:** `SS P1 P2 P3P4P5P6P7;` — 10 characters, P1 selects
  MAIN/SUB, P3–P7 is one 5-character field (most sub-commands use only the
  first character). See `docs/design/scope-control-via-cat.md` for how that
  was established against a real radio — the CAT manual's own table is
  ambiguous on this point.
- **Every write is followed by a read-back**, and the read-back is what the
  API returns — the radio is the source of truth, not the value just sent,
  because the operator may be adjusting the same control by hand at the
  console.
- Shares the serial port with the meter poll via the same one-at-a-time
  semaphore pattern as `CatController`.
- Frontend: `wwwroot/js/ui/radio-scope.js`, hosted from `Index.cshtml` via
  `Pages/Shared/_RadioScopePartial.cshtml` — reachable with no video capture
  configured at all.

### CW decode — shared, built in `core/`

`core/Services/Cw/` (`CwDecoderEngine`, `CwToneDetector`, `CwElementDecoder`,
`CwZeroIn`, `MorseTable`) takes mono float audio and a tuned pitch and
produces text — no CAT, no filter code, no radio model. See
`core/docs/design/cw-decoder.md` for the full pipeline (FFT tone tracking +
Goertzel envelope detection + readability gating). What's local to YWC is the
wiring: the audio capture source feeding the engine, the CAT-driven
pitch/zero-in feedback loop, and the panel host (`core/js/cw/cw-reader-panel.js`
is shared; the Razor page hosting it is not). In progress on
`feature/cw-reader` — pull `core/` before extending this.

### Remote Audio (`Services/Audio/`)

Opus-encoded RX/TX audio over a websocket session. `AudioSessionManager`
owns session lifecycle; `RadioAudioBridgeService` is the hosted service
bridging the radio's audio to it; `AudioDeviceEnumerator` lists host devices;
`HttpsCertificateService` provisions the local cert the secure context needs
for browser mic capture. The JS transport (`core/js/audio/`) is already
shared with IWC; the C# side isn't yet, but is expected to move — see
"Shared core" above.

### Remote Video (`Services/Video/`)

Webcam / panadapter-camera capture and MJPEG streaming to the browser.
Platform-specific capture backends: `WindowsDshowMjpegSession` /
`WindowsMfMjpegSession` (DirectShow / Media Foundation), `MacAvFoundationCapture`
(AVFoundation, via the `.m` Objective-C shim), `LinuxV4l2MjpegSession` (V4L2).
`VideoJpegSof` parses JPEG SOF markers to recover actual frame dimensions when
a device reports the wrong ones; `VideoTiledFrameDetector` and
`VideoDisconnectHalt` handle two failure modes that are silent otherwise —
both have dedicated tests in `Tests/YaesuWebControl.Tests/`. **Permanently
YWC-only** — see "Shared core."

### VC Tune (`Services/VcTune/`)

Integration with Yaesu's VC Tune external antenna tuner/preselector:
`VCTuneCommandBuilder`, `VCTuneResponseParser`, and `VCTuneStateMachine` drive
it over CAT, with `VCTuneConfigurationStore` for persisted setup and
`VCTuneDiagnostics` / `VCTuneIntegrationHarness` for bench verification.
**Yaesu-specific and permanently YWC-only** — confirmed by IWC's own
`CLAUDE.md`, which records the VC Tune UI as deliberately removed during
the carve. Don't propose sharing this even if a refactor makes parts of it
look radio-agnostic.

### Voice control (`Services/Voice/`)

`VoiceControlService` (SAPI recognition) → `IntentDispatcher`, which calls
into the CAT layer; `VoiceTtsService` for spoken feedback and status
announcements; `VoiceGrammar` builds the recognition grammar;
`VoicePhraseStore` / `VoicePhraseValidator` make phrase packs user-editable
and shareable — the same approach IWC uses. `VCTuneRecognizer` extends this
for VC Tune voice commands specifically. Windows-only (SAPI); compiled out
and hidden on the `net10.0` CAT-only host. Built for partially-sighted
operators — treat regressions here as release blockers, not routine bugs.

### DX cluster (`Services/DxClusterService.cs`, `Controllers/DxClusterController.cs`)

Telnet connection to a configurable cluster server; incoming spots overlaid
on the spectrum and listed in a dedicated panel; watch-list matching drives
popup alerts. `Models/DxSpot` — the spot type itself — already lives in
`core/`; the service around it (connection handling, watch-list logic)
hasn't moved yet.

### Memories (`Services/MemoryService.cs`, `Services/MemoryBankService.cs`)

Per-channel memory storage (frequency, mode, and — where the radio supports
it — filter/AGC/power state), grouped into named banks. Read/save without
writing to the transceiver unless the operator chooses to. Both services are
eligible for `core/` (radio-agnostic once above the CAT layer) but haven't
moved — see "Shared core."

---

## Frontend Architecture

### Module Map (`wwwroot/js/`)

```
websocket/
  ws-connection.js        — SignalR transport only
  ws-update-pipeline.js   — routes { property, value } to registered handlers

calibration/
  calibration-tables.js   — single source of truth for all scaling tables
  FTdx101Calibration.js
  (calibration-engine.js itself now lives in core/js/calibration/ and is
   copied into this folder at build time — see "Shared core")

guages/                   — (sic: folder name is misspelt; leave it alone
  gauge.js                   unless deliberately renaming — referenced by
  gaugeFactory.js            path in every importing module)
                            — ONLY place RadialGauge instances are created
  meter-gauge.js
  meter-panel.js           — owns all meter DOM and canvas rendering
  smeter-history-panel.js
  update-engine.js         — performs gauge updates

orchestrators/
  FTdx101Meters.js         — wires websocket → calibration → MeterPanel; no logic of its own

sdr/
  sdr-spectrum-pipeline.js — SignalR transport for spectrum; no DOM
  spectrum-panel.js        — owns the spectrum canvas; DOM access intentional here

audio/
  audio-session.js, audio-protocol.js, audio-capture.js, audio-playback.js
    — shared transport layer, copied in from core/js/audio/ at build time
  remote-audio-ui.js       — YWC-local UI wiring for the Remote Audio panel

video/
  radio-display-panel.js, radio-display-ui.js — MJPEG stream rendering and
    the RadioDisplay page's controls; YWC-local, not shared

ui/
  site.js, band-plan.js, a11y-labels.js, voice-control.js,
  memories.js, dx-spots-panel.js, freq-keyboard.js, calibration-editor.js,
  if-width-tables.js, radio-scope.js, filter-scope-panel.js
  meter-formatters.js      — all meter text formatting lives here

(cw/ reader panel JS lives in core/js/cw/ and is copied into wwwroot at
 build time, same pattern as calibration and audio)
```

### Value Flow (strict — never bypass or reorder)

```
SignalR RadioStateUpdate
  → WsUpdatePipeline (route by property)
  → calibration-engine (pure transform)
  → FTdx101Meters (orchestrate)
  → MeterPanel.update()
  → gaugeFactory / update-engine
  → canvas
```

### Spectrum Display (dual-VFO, v2.3.0+)

`SdrSpectrumPipeline` creates its own SignalR connection. It maintains per-sdrId handler maps and dispatches `SpectrumUpdate` / `SdrStatus` / `SdrError` to whichever `SpectrumPanel` registered for that sdrId. Also handles `FrequencyA`, `FrequencyB`, `DxSpot`, `DxClusterStatus`, `DxAlert`.

`SpectrumPanel` is instance-able with a `vfo` parameter ("A" or "B") in the constructor. The vfo arg determines which `/api/cat/frequency/{a|b}` endpoint click-to-tune and wheel-tune use, and which `window.setMode('A'|'B', mode)` call follows a click. Each panel's frequency axis uses its own `_vfoHz` from the matching `FrequencyA`/`FrequencyB` SignalR updates.

Index page lays out two card panels (`spectrumContainerA`, `spectrumContainerB`) with a Mono A / Mono B / Both toggle persisted in `localStorage.ywc.spectrumMode`. Outer container hides when neither VFO has an SDR; toggle hides when only one is configured.

### Razor Pages

`Index` (main control panel; `RadioState` exposes `RadioStateService` for
server-rendered initial values), `Settings`, `Diagnostics` (SDR device
scanning, port listing), `Memories`, `Calibrations` / `Calibration/MeterCalibration`
/ `Calibration/SMeterCalibration`, `Labels` (accessibility label overrides),
`RemoteAudio`, `RadioDisplay` (Remote Video), `Ports`, `ApplicationSetup`,
`About`, `UserManual`.

---

## Key Domain Facts

- **FTdx101MP frequency range:** 30 kHz – 75 MHz. Frequencies are always in Hz in this codebase.
- **IF output:** 9 MHz rear-panel IF fed to RSP1 antenna input for spectrum display.
- **Three separate scope-related systems, easy to conflate:** the SDR
  spectrum panel (external hardware, FFT drawn in-browser), CAT Scope
  Control (the `SS` command, changes the radio's own front-panel display),
  and Remote Video (captures whatever the radio's screen is currently
  showing, including the effect of CAT Scope Control). None of the three
  substitutes for another.
- **SDR default sample rate:** 2,048,000 Hz (2 MHz span). Spectrum centred on `SdrIfFrequencyHz` (default 9 MHz); axis labels show RF frequencies derived from VFO-A.
- **S-meter raw values:** 0–255 → S0 to S9+60 dB via calibration tables.
- **Meter polling is tiered**, not a flat rate. `MeterPollingService`;
  `MeterPollIntervalMs` in `ApplicationSettings` is the **minimum cycle
  period** (delay between cycle starts; default 200 ms, clamped 50–1000).
  Each loop subtracts cycle elapsed time from the interval before delaying.
  - **Fast tier** (every cycle): `TX;`, `SM0;` (+ `SM1;` on dual-receiver radios)
  - **TX tier** (transmitting only): `RM5` power, `RM4` ALC, `RM7` IDD; FTdx101MP/D use `MS13`+`RM0` (comp left / SWR right); others `RM3` compression, `RM6` SWR — forced to zero in software during receive (no reads)
  - **Slow tier**: VDD (`RM8`), temperature (`RM9`), antenna (`AN0`/`AN1`) every 2 s; frequency backstop (`FA;`/`FB;`) every 1 s on single-receiver radios
  - **S-meter zero-hold:** brief CAT zeros are held ~1 s (`SMeterZeroHold`) before the needle is allowed to drop to S0, so transient bus gaps do not flash the meter.
  - Older comments citing a flat "~10 Hz" or "~1.3 Hz" figure are wrong — don't trust them if you find one that survived.
- **Single vs dual receiver:** FTdx101MP/D are dual-receiver; FTdx10, FT-710, and FTDX3000 are single-receiver with two VFOs used for split/memory, not simultaneous listening.
- **SignalR heartbeat:** `SdrManager` re-broadcasts each worker's "streaming" status every 30 frames (~3 s) so clients that load after startup receive the current per-VFO status.
