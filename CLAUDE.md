# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Architecture Rules

Before making any changes, read and follow all rules in `.claude/rules.md` and `.claude/project-overview.md`. These are non-negotiable and override any default behaviour.

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

There are no automated tests. Verification is manual via the browser at `http://localhost:8080`.

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

## Backend Architecture

### Service Dependency Map

```
RadioInitializationService (IHostedService)
  └─ opens serial port via CatMultiplexerService
       └─ MultiplexedCatClient (ICatClient)
            └─ CatMessageDispatcher → RadioStateService → SignalR (RadioHub)
                                                        → RadioStatePersistenceService

MeterPollingService (IHostedService) — polls CAT FA/FB/SM etc. at ~10 Hz
SdrManager (IHostedService) — supervises one Yaesu_Sdr_Worker.exe per
                              configured SDR; reads FFT frames over
                              localhost TCP and broadcasts via SignalR
RigctldServer (IHostedService) — exposes rigctld TCP interface for WSJT-X etc.
```

### SignalR Message Envelope

All real-time updates use a single hub method `RadioStateUpdate` with envelope `{ property, value }`.  
The frontend's `WsUpdatePipeline` routes on `property`. The same hub carries:
- CAT state (FrequencyA, FrequencyB, PowerMeter, SMeter, etc.)
- SDR lifecycle (sdrId-tagged from v2.3.0):
  - `SdrStatus`     value = `{ sdrId, status }` — "unconfigured" / "connecting" / "streaming" / "disconnected" / "nodll"
  - `SdrError`      value = `{ sdrId, error  }` — human-readable detail
- SDR spectrum frames:
  - `SpectrumUpdate` value = `{ sdrId, bins, centreHz, spanHz }`

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

---

## Frontend Architecture

### Module Map (`wwwroot/js/`)

```
websocket/
  ws-connection.js        — SignalR transport only
  ws-update-pipeline.js   — routes { property, value } to registered handlers

calibration/
  calibration-engine.js   — pure functions, no DOM, no side effects
  calibration-tables.js   — single source of truth for all scaling tables
  FTdx101Calibration.js

ui/
  meter-panel.js          — owns all meter DOM and canvas rendering
  gaugeFactory.js         — ONLY place RadialGauge instances are created
  update-engine.js        — performs gauge updates
  meter-formatters.js     — ALL UI text formatting lives here
  overlays.js

orchestrators/
  FTdx101Meters.js        — wires websocket → calibration → MeterPanel; no logic of its own

sdr/
  sdr-spectrum-pipeline.js  — SignalR transport for spectrum; no DOM
  spectrum-panel.js         — owns spectrum canvas; DOM access intentional here
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

- `Index.cshtml` / `Index.cshtml.cs` — main control panel; `RadioState` property exposes `RadioStateService` for server-rendered initial values.
- `Settings.cshtml` / `Settings.cshtml.cs` — persists `ApplicationSettings`; note the `ModelState.Remove` order requirement above.
- `Diagnostics.cshtml` — SDR device scanning, port listing.

---

## Key Domain Facts

- **FTdx101MP frequency range:** 30 kHz – 75 MHz. Frequencies are always in Hz in this codebase.
- **IF output:** 9 MHz rear-panel IF fed to RSP1 antenna input for spectrum display.
- **SDR default sample rate:** 2,048,000 Hz (2 MHz span). Spectrum centred on `SdrIfFrequencyHz` (default 9 MHz); axis labels show RF frequencies derived from VFO-A.
- **S-meter raw values:** 0–255 → S0 to S9+60 dB via calibration tables.
- **Meter poll rate:** ~10 Hz via `MeterPollingService`.
- **SignalR heartbeat:** `SdrManager` re-broadcasts each worker's "streaming" status every 30 frames (~3 s) so clients that load after startup receive the current per-VFO status.
