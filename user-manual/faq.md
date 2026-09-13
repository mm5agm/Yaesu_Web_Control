## 15. Frequently Asked Questions

### 15.1 WSJT-X transmits but the radio shows no TX audio (or zero power output) in DATA-U / DATA-L mode

This is the most common digital-mode setup pitfall and it's not a YWC problem — it's a one-time radio menu setting that has to be done on the radio itself. Yaesu radios ship with the rear DATA/ACC jack as the default audio input for DATA modes, **not** the USB codec that WSJT-X is sending audio to. Until you switch the radio over, DATA-mode TX produces silence.

**Fix on the radio menu:**

| Radio | Menu item | Set to |
|---|---|---|
| FTdx101MP / FTdx101D | 070 **DATA MOD SOURCE** | **REAR** |
| FTdx101MP / FTdx101D | 071 **REAR SELECT** | **USB** |
| FTdx10 | 070 **MOD SOURCE / DATA** | **USB** |
| FT-710 | 070 **DATA MOD SOURCE** | **REAR** |
| FT-710 | 071 **REAR SELECT** | **USB** |
| FTDX3000 | 075 **DATA IN SELECT** | **USB** |

(Menu numbers may shift slightly across firmware revisions — if a number doesn't match, look for an item with a similar name nearby.)

The radio remembers this across power cycles, so it's a once-only change. **Why not configure it from YWC?** An earlier version of YWC tried to send the CAT commands for these menu items automatically, but testing revealed the commands were writing to the wrong menu addresses and never actually worked — the radio appeared to be correctly configured only because operators had set it manually at first install. The auto-config feature was removed rather than ship something misleading.

If you can't find these menu items, your operating manual's index under "DATA MOD SOURCE" or "REAR SELECT" is the authoritative reference for your firmware version.

---

### 15.2 My RSP1 shows serial number `0000000001` — is it broken?

No. The original SDRplay RSP1 (the first-generation receiver, no longer in production) shipped with a placeholder serial number of `0000000001` until later in its production run. SDRplay subsequently released a small utility that lets owners program a real unique serial into the device's flash memory. The RSP1A, RSP1B and all later models ship with a real serial pre-programmed at the factory.

For ordinary single-SDR use, this doesn't matter — YWC opens the only SDR plugged in regardless of what serial it reports.

**It does matter for dual-SDR setups** (one RSP per VFO) because YWC needs a stable identifier to remember "this physical device serves VFO A" across reconnects. YWC handles this from v2.3.0 onwards by composing the device key as `sdrplay:hw<hwVer>-<serial>` — including the hardware version means an RSP1 with the placeholder serial doesn't collide with an RSP1B that happens to use the same number. So:

- **One RSP1 + one RSP1B (my setup)** — works fine, no action needed.
- **Two of the same model**, both with the placeholder serial — this would still collide. The fix is to program a real serial into at least one device. If SDRplay's Serial Number Update Utility isn't on their downloads page, ask their support: it's a small Windows tool that writes a serial of your choice into the device's flash.

YWC migrates settings from the v2.2.x key format (`sdrplay:<serial>` only) to the new format (`sdrplay:hw<N>-<serial>`) automatically the first time you save Settings on v2.3.0 or later. No user action required.

---

### 15.3 Why two SDRplay RSPs instead of one RSPduo?

The dual-SDR support in YWC (v2.3.0+) is designed for two completely separate receivers — typically two SDRplay RSPs, one wired to each of the FTdx101MP/D's IF OUT sockets. You might assume an **RSPduo** (two tuners in one box) would be the natural pick. Three reasons it isn't:

1. **Bandwidth.** A single **RSP1B** can sample up to **10 MHz** of spectrum at once — wide enough to display the full 9 MHz IF in one shot if you wanted to. An RSPduo in dual-tuner mode is limited to roughly **2 MHz total shared** between its two tuners, so each side gets ~1 MHz at best.
2. **Cost.** At UK retail prices (mid-2026): RSPduo around **£240**, RSP1B around **£125**. Two RSP1Bs come in at roughly the same total cost as one RSPduo, with double the bandwidth and full independence.
3. **My own setup was "I had an old RSP1 sitting unused".** Adding a second SDR meant buying just one new RSP1B (£125) rather than a £240 RSPduo. That happens to be a common situation for hams who've upgraded their SDRplay receivers over the years — chances are there's an RSP1 or RSP2 in a drawer that can serve VFO B perfectly well.

If you already own an RSPduo it will still work — set it as the VFO A SDR and leave VFO B as *(none)*. The dual-tuner mode that lets one RSPduo serve both VFOs is not yet implemented.

---

### 15.4 Why not use a £25 RTL-SDR dongle instead of an RSPplay?

RTL-SDR dongles are supported via SoapySDR and will function — but for a serious HF setup, an SDRplay RSP1B is a significant step up:

- **Bit depth:** RTL-SDR is 8-bit; SDRplay RSPs are 14-bit. That's roughly 36 dB more dynamic range — weak signals next to a strong neighbour are far easier to see.
- **HF coverage:** Most RTL-SDR dongles need a separate upconverter to receive HF. RSPs cover 1 kHz to 2 GHz natively.
- **Front-end filtering:** RSPs have selectable bandpass filters; dongles have essentially none. With a kilowatt-class transmitter on the next band, a dongle will overload long before an RSP does.
- **Clock stability:** RSPs use a TCXO. Cheap dongles drift visibly during warm-up — a spectrum centred on the 9 MHz IF will appear to slide sideways for the first ten minutes after power-on.

For casual VHF/UHF listening an RTL-SDR is fine. For a permanent HF-band-monitoring setup the RSP is the better tool.

---

### 15.5 Why is there a 3-second delay when I change the spectrum bandwidth?

When you click a different span button (e.g. 250k → 2M) the spectrum visibly freezes for about **three seconds** before resuming at the new bandwidth. YWC keeps the previous frame visible during the pause rather than blanking out — the frozen image is intentional, not a glitch.

The delay is **hardware**, not software:

1. YWC sends the new sample-rate request to the SDR's dedicated worker process.
2. The worker calls **sdrplay_api_Uninit** to release the current device configuration — typically ~500 ms to 1 s.
3. The worker then calls **sdrplay_api_Init** with the new sample rate — another ~500 ms to 1 s while the SDRplay API service reconfigures the hardware.
4. Streaming resumes; the frontend's next frame replaces the frozen one.

With two SDRs running in dual-SDR mode, both go through the cycle simultaneously when you change the shared sample rate. Per-VFO bandwidth changes only restart the one worker that changed.

This is normal SDRplay API behaviour, not specific to YWC. The first time you see it you'll blink; from the second time on it's just how RSPs reconfigure.

---

### 15.6 Can I use VSPE, OmniRig, com0com or a similar virtual COM port sharer?

Short answer: **not reliably, and we'd suggest avoiding it**. YWC's CAT layer talks directly to the radio over a regular Windows COM port. Virtual-port sharers sit between YWC and the real port, and even when they're configured correctly they introduce timing and forwarding behaviours that YWC isn't currently tested against.

Symptoms when there's a port sharer in the chain:

- **"Test Connection" fails** with a "COM port opened but the radio did not respond to a CAT probe" error (YWC v2.3.0+ catches this case explicitly).
- Or worse — the port opens, YWC reports connected, but the frequency/mode displays never follow the radio's actual state. CAT chatter is being swallowed somewhere between YWC and the radio.

> **Update (v2.4.2-pre2):** one specific cause of the second symptom is fixed — a frequency-only freeze (meters still live) over VSPE, caused by YWC discarding a receive-buffer race that a virtual port's added latency made much easier to hit than on real hardware. See [#74](https://github.com/mm5agm/Yaesu_Web_Control/issues/74). This doesn't make VSPE (or other port sharers) a generally supported or tested configuration — the advice below still stands — but if this was your exact symptom, it's worth updating.

Why this happens in practice:

- **VSPE** (Virtual Serial Port Emulator) doesn't always forward client-side port settings (baud rate, parity) through to the underlying physical port. If another app set up the chain at a different baud rate previously, YWC's 38400 setting is applied at the virtual layer only and the physical port stays at whatever rate it was last given. The radio hears garbled bytes and silently drops them.
- **OmniRig** is designed as a CAT *abstraction* layer for multiple apps to share a radio. Apps that want OmniRig support are expected to use OmniRig's COM-server interface, not pretend to talk to a generic virtual COM port underneath. YWC speaks raw CAT, not OmniRig.
- **com0com** creates virtual port pairs but doesn't talk to physical ports on its own — you need a separate bridge program (like hub4com) to connect the virtual pair to a real COM port. The chain is easy to misconfigure.

**Recommended setup:** plug your radio's USB-CAT cable in, see what COM port Windows assigns (Device Manager → Ports), set that COM port directly in YWC Settings. If you also want WSJT-X, JTAlert, Log4OM, etc. to control the same radio, point them at YWC's rigctld interface on **localhost:4532** rather than letting them open the COM port themselves. YWC then acts as the single owner of the radio's COM port and serves CAT to every other app over the network.

If you must use a virtual port sharer (e.g. you've already built a working setup around one), the easiest test is to point YWC at the real physical COM port directly while everything else stays on the sharer's virtual ports — and only re-add the sharer to YWC's path if a specific need forces it.

### 15.8 Why was Alexa voice control dropped in favour of the built-in microphone method?

Earlier development branches explored using Amazon Alexa to control YWC — you'd say "Alexa, set frequency to fourteen point zero seven four" to your Echo device and the command would route through Amazon's cloud, hit a custom skill, and arrive at YWC over a Cloudflare tunnel. That work reached a fully-working end-to-end prototype, but **the setup overhead made it impractical for anyone who isn't already comfortable with Cloudflare tunnels and the Amazon Developer Console**.

The current voice control uses **Windows' built-in speech recognition (SAPI 5)** with a press-and-hold microphone button beside each VFO panel. No cloud round-trip, no external accounts, no public endpoint, and your audio never leaves your computer.

| What's needed | Alexa method | Built-in microphone method |
| --- | :---: | :---: |
| A public domain name | ✅ Required | ❌ Not required |
| Cloudflare account &amp; tunnel | ✅ Required | ❌ Not required |
| Amazon Developer account | ✅ Required | ❌ Not required |
| SMAPI command-line install | ✅ Required | ❌ Not required |
| Skill build in Alexa Developer Console | ✅ Required | ❌ Not required |
| An Echo device (or Alexa app on a phone) | ✅ Required | ❌ Not required |
| Internet connection (for every command) | ✅ Required | ❌ Not required |
| Audio sent to a cloud service | ✅ Yes (Amazon) | ❌ Stays on your PC |
| Hands-free wake-word ("Alexa, …") | ✅ Yes | ❌ No — press-and-hold mic button |
| Works from anywhere in the house | ✅ Yes | ❌ Only at the PC |
| Typical setup time | ~30–60 minutes | ~2 minutes |

The Alexa code **isn't deleted** — it lives on a parked branch and can be revived if Amazon ever simplifies the developer experience, or if a contributor wants to package the cloud side as a one-click installer. For now, the built-in microphone method gives most of the same usefulness at a small fraction of the setup complexity, and it works equally well for users on a restricted home network where opening a Cloudflare tunnel isn't viable.

---

### 15.7 What is the TX button for? When I press it the radio goes into TX mode but there's no audio from my microphone.

The TX button in YWC sends the `TX1;` CAT command, which puts the radio into transmit mode (PTT engaged) but **by itself does not create microphone audio**. With nothing modulating the carrier, what actually goes on-air depends on the current mode and how audio is fed:

- **CW** — an unmodulated carrier (a steady tone). Useful for tune-up, SWR measurement, or driving an external tuner / amplifier into its tune cycle.
- **SSB / AM / FM** — the TX path is open; you need audio into the radio (front mic, or USB/REAR audio from the PC).
- **DATA / digital modes** — typically USB audio from WSJT-X (or similar) into the rear DATA/USB path.

**Local mic:** press the PTT on the hand mic / footswitch / VOX as usual.

**Remote browser mic:** enable [Remote Audio](remote-audio.md#18-remote-audio), pick the radio’s USB devices in Settings, use HTTPS for non-localhost browsers, click **Start audio** on the Index page, then use the TX button (or TX toggle key) for PTT. On the radio, set **MOD SOURCE** to USB / REAR (USB).

What people use the TX button for without remote audio:

1. **Tune-up.** Switch to CW, click TX, watch your SWR or let your ATU find a match.
2. **Driving an external amplifier or antenna tuner** into its auto-tune cycle.
3. **Digital-mode keying tests.** When WSJT-X is feeding audio into USB, the TX button verifies CAT keying.

---

### 15.9 WSJT-X is very slow to key the radio (10–20 second delay on PTT / Tune)

If pressing **Test PTT** or **Tune** in WSJT-X takes ten to twenty seconds before the radio actually transmits — and sometimes seems to stay in transmit afterwards — the delay is almost certainly **not** in YWC.

When this was traced from an operator's logs ([issue #73](https://github.com/mm5agm/Yaesu_Web_Control/issues/73)), YWC was keying the radio within about 40 *milliseconds* of receiving each PTT command — the wait was happening *before* the command ever reached YWC. WSJT-X talks to YWC's rigctld server over the local loopback address (`127.0.0.1`), and on some Windows machines that loopback path can be bottlenecked by legacy networking.

The fix that resolved it for that operator: **disable NetBIOS over TCP/IP**. It's a legacy protocol that can slow down local loopback traffic. To disable it:

1. Open **Network Connections** (press **Win + R**, type `ncpa.cpl`, press Enter).
2. Right-click your active network adapter → **Properties**.
3. Select **Internet Protocol Version 4 (TCP/IPv4)** → **Properties**.
4. Click **Advanced…**, then open the **WINS** tab.
5. Under *NetBIOS setting*, choose **Disable NetBIOS over TCP/IP**, then **OK** out.

This is a machine-specific networking quirk rather than a YWC bug, so it won't affect most setups — but if you're seeing long PTT delays with an otherwise-working WSJT-X ↔ YWC link, it's the first thing to try.

As a safety backstop, YWC (v2.4.2 and later) will force the radio back to receive if a program keys it through rigctld and never sends the matching release, so a stuck transmit can't be left keyed indefinitely — but that's a safety net, not a cure for the delay. The loopback fix above is the real solution.

---

### 15.10 What's different on macOS / Linux vs Windows?

**Short version:** CAT control and the browser UI work on all three; SDR spectrum and Voice Control are Windows-only. See the platform table in [§1](introduction.md#1-introduction).

| Topic | What to expect |
|---|---|
| Getting the app | Windows: unsigned installer from Releases. macOS: unsigned CAT-only DMG from Releases (Apple Silicon or Intel) — Gatekeeper needs **Open** / **Open Anyway** the first time ([§2.2](installation.md#22-macos-dmg)). Linux: build `net10.0` from source, or Docker (`ghcr.io/mm5agm/yaesu_web_control`, amd64 + arm64). |
| Host chrome | Windows tray · macOS menu-bar item · Linux/Docker console only |
| Serial port | `COMn` on Windows; `/dev/cu.*` on macOS; `/dev/ttyUSB*` / `/dev/ttyACM*` on Linux (pass through with `YWC_SERIAL_DEVICE` in Docker). Always use the **Enhanced** CAT port when two CP210x ports appear. |
| USB serial driver | **Windows / macOS:** install [Silicon Labs CP210x VCP](https://www.silabs.com/software-and-tools/usb-to-uart-bridge-vcp-drivers?tab=downloads) and **reboot** only if ports are missing or CAT never answers ([§2.4](installation.md#24-usb-serial-driver-windows--macos--linux)). **Linux:** skip — in-kernel CP210x is enough. |
| Settings & logs | Windows `%APPDATA%\MM5AGM\Yaesu Web Control\` · Unix `~/.config/MM5AGM/Yaesu Web Control/` · Docker volume under `/data/…` |
| Leaving the shack with no browser open | Turn **off** “Automatically exit when no browser is connected”. Docker already forces that behaviour. |
| SDR / Voice Control | Not on macOS/Linux/Docker. Use a Windows host if you need those. |
| WSJT-X / Log4OM launch buttons | Default paths are Windows. On other OSes run those apps yourself and point them at YWC's rigctld over the network. |

The browser UI from a phone or tablet is the same regardless of which OS hosts YWC — only the machine that owns the serial cable (or Docker device mapping) must run the host.

---

### 15.11 Test Connection fails / CAT does not respond over USB

**Windows / macOS — if ports are missing or CAT never answers:** install the official [Silicon Labs CP210x VCP driver](https://www.silabs.com/software-and-tools/usb-to-uart-bridge-vcp-drivers?tab=downloads) and **reboot the host**. Incomplete or missing USB-serial stacks can make a port show up and even open in YWC while CAT commands (`ID;`, etc.) get no reply. After install + reboot, Test Connection should work once baud rate and the Enhanced port are correct.

**Linux:** do **not** install Silicon Labs' VCP package first — CP210x support is already in the kernel. Check cable, power, `dialout` group membership, and that you picked the right `/dev/ttyUSB*` / `/dev/ttyACM*` node (Enhanced vs Standard when two appear).

Checklist:

1. Radio powered on; rear-panel USB cable connected.
2. Settings baud rate matches **Menu → CAT Rate** (usually **38400**).
3. Serial Port is the **Enhanced** (CAT) virtual port, not Standard / TX-only. On macOS try the other `/dev/cu.usbserial-…` if the first fails.
4. No other app is holding the same port.
5. Prefer a direct USB port over a flaky hub when diagnosing.
6. **Windows / macOS only:** if the steps above still fail, install the Silicon Labs VCP driver from the link above, then **reboot**.

See also [§2.4](installation.md#24-usb-serial-driver-windows--macos--linux) and [§3](first-time-setup.md#3-first-time-setup).

---
