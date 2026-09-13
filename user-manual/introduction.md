## 1. Introduction

Yaesu Web Control — **YWC** for short — is a web-based control panel for Yaesu HF transceivers.

The **shipped installer** is for **Windows 10/11 (64-bit)** and includes the full product: system-tray host, SDR spectrum, and Voice Control. **macOS** gets an unsigned CAT-only DMG from the same Releases page (menu-bar host; no SDR / Voice Control). **Linux** runs the same CAT-only host from source or via Docker (x64 PCs and arm64 Raspberry Pi). On those platforms the browser UI still talks to the radio over CAT. You can always open the browser UI from another device on your LAN (tablet, phone, another laptop) once the host is running.

### Platforms at a glance

| | Windows (installer) | macOS (DMG) | Linux Docker / from source |
|---|---|---|---|
| How you get it | GitHub Releases installer | GitHub Releases DMG (unsigned; Apple Silicon or Intel) | GHCR image `ghcr.io/mm5agm/yaesu_web_control`, or build with .NET 10 SDK |
| Host UI | System tray icon | Menu-bar status item | Console only (Docker / Linux from source) |
| CAT + web UI | Yes | Yes | Yes |
| SDR spectrum / waterfall | Yes | No | No |
| Voice Control (SAPI mic) | Yes | No | No |
| Radio Display (USB capture → MJPEG) | Yes | Yes | Yes (map `/dev/video*` in Docker) |
| Voice *announcements* (browser TTS) | Yes | Yes | Yes |
| CW Reader ([§20](cw-reader.md#20-cw-reader)) | Yes | Yes | Yes |
| Launch WSJT-X / JTAlert / etc. from YWC | Yes (Windows paths) | Buttons exist but target Windows-style paths — run those apps yourself and point them at YWC's rigctld | Same — use host/network apps |
| Serial port form | `COM3`, `COM4`, … | `/dev/cu.*` | `/dev/ttyUSB*` / `/dev/ttyACM*` (pass device into the container for Docker) |
| USB serial driver | **Windows / macOS:** if the radio's COM / `/dev/cu.*` ports are missing or CAT never answers, install [Silicon Labs CP210x VCP](https://www.silabs.com/software-and-tools/usb-to-uart-bridge-vcp-drivers?tab=downloads) and **reboot** ([§2.4](installation.md#24-usb-serial-driver-windows--macos--linux)). **Linux:** usually skip — the kernel already includes CP210x support. |
| Settings / logs folder | `%APPDATA%\MM5AGM\Yaesu Web Control\` | `~/.config/MM5AGM/Yaesu Web Control/` | Volume `./data/ywc` → `/data/MM5AGM/Yaesu Web Control/` (Docker); same `~/.config/…` path when run from source |
| Auto-exit when no browser | Default **on** | Default **on** (turn **off** for a headless shack) | Forced **off** in containers; default **on** from source |
| Opens a local browser on start | Yes | Yes | No (Docker); yes from source on a desktop |

> **No internet connection needed.** YWC reaches the radio over a serial cable, reads its own SDR hardware locally, and serves its own web page from your own PC, so the whole of it works on a shack computer that has never been online. Only two things reach out to the internet, and neither is required: the **DX cluster** spot feed (Section 6.6), which is off until you switch it on, and the **update check** that tells you when a new version is available. With no connection the cluster badge simply reads *Disconnected* and the update banner never appears. Everything else — meters, spectrum, tuning, WSJT-X and the rest — is entirely local.
>
> **This was not quite true up to and including v2.4.2, nor in the v2.4.3 pre-releases before pre4.** The page fetched three files — the meter-gauge library, the icon font and the library that carries live updates from the radio to the browser — from public servers on the internet instead of from your PC. Almost nobody noticed, because a browser that had loaded them once kept its own copy for a year afterwards. On a PC that had never been online they never arrived at all, and the page opened with no meters and no value that ever changed. All three now ship inside YWC. **If your shack PC has no internet, use v2.4.3-pre4 or later.**

Supported radios:

| Model | Power | Receivers |
|-------|-------|-----------|
| FTdx101MP | 200 W | Dual |
| FTdx101D | 100 W | Dual |
| FTDX3000 | 100 W | Single |
| FTdx10 | 100 W | Single |
| FT-710 | 100 W | Single |

The app runs as a small host process and is accessed through any web browser — on the same machine, a tablet, or any device on your home network.

The application was written for operators who want a large, clean, touchscreen-friendly display alongside their existing logging software, and for those who find the physical controls on the radio difficult to read or reach.

**Key features:**

- Large, readable frequency displays with digit-by-digit mouse-wheel tuning and an on-screen frequency keyboard
- Full dual-receiver control (VFO A and VFO B)
- Live S-meter, power, SWR, ALC, and compression meters (plus PA temperature, IDD, and VDD on FTdx101MP, FTdx101D, and FTDX3000)
- Real-time two-way sync — changes on the radio front panel appear immediately in the app, and vice versa
- Band and segment selectors for fast QSY to CW, FT8, SSB, or RTTY
- **Per-band memory** for IF Width, IF Shift, and Mode — switching to a band automatically restores your preferred filter and mode for that band
- Full receive controls: AGC, IPO/AMP, Attenuator, NR, NB, Auto Notch, Manual Notch, **RF Gain**, **Squelch** (FM mode)
- CW keyer with speed, break-in, delay, **sidetone pitch**, and five programmable memory messages
- TX monitor on/off toggle and level control
- Radio memory channels — recall saved frequencies and modes at a click; save and load named memory banks for different operating scenarios (e.g. Daily, Contest)
- Optional real-time spectrum display and waterfall (**Windows host only** — requires an SDR connected to the 9 MHz IF output)
- **DX cluster spots** overlaid on the spectrum when SDR is available; the DX Spots list works without an SDR
- **DX watch list** — get a popup alert and a beep when watched callsigns or prefixes appear in the cluster feed (e.g. `P29*` for a DXpedition); persisted across app restarts
- **TX timeout warning** — visible red banner + audible tone if TX has been on too long (configurable threshold), as a safety net against open mics, stuck PTTs and VOX false-triggers
- **Per-VFO status line** inside each VFO panel — at-a-glance summary of band, mode, frequency, power and split state, banner-coloured to match the receiver
- **Voice announcements** — optional spoken cues for band/mode/TX changes, DX alerts and TX timeout, using your browser's built-in text-to-speech (handy for partially sighted operators; works on any host OS)
- **Voice Control** — press-and-hold mic commands via Windows SAPI (**Windows host only**; see §17)
- Integration with WSJT-X, JTAlert, and Log4OM (launch buttons are Windows-oriented; rigctld works from any host so networked clients can connect)
- Built-in rigctld server so WSJT-X can control the radio through the app
- Four IARU band plans: Region 1 (Europe, Africa, Middle East), Region 2 (Americas), Region 3 (Asia-Pacific), and Japan (JARL)
- Full screen reader support — compatible with NVDA and Windows Narrator
- Windows High Contrast mode support for all gauge displays
- Customisable accessible labels (band names, meter names, control names) for any language

---
