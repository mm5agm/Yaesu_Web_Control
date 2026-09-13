## 18. Remote Audio

Remote Audio streams **radio RX → browser speakers** and **browser microphone → radio TX** over a dedicated WebSocket on the YWC host. PTT remains the normal Index **TX** button (or optional TX toggle key). It is intended for **LAN or VPN** use (for example WireGuard) as a simpler alternative to running Mumble or SonoBus alongside YWC.

> **Not supported in Docker for v1.** USB audio device access from containers is host-specific and unreliable; run the native host on Windows, macOS, or Linux instead.

### 18.1 Radio setup

1. Connect the radio’s USB cable so the PC sees both CAT and USB audio.
2. In the radio menu, set **MOD SOURCE** to **REAR** with **REAR SELECT = USB**, or **MOD SOURCE = USB** on models that use that wording (e.g. FT-710). Same idea as FT-Control / digital-mode USB audio.
3. Confirm the OS lists the radio’s USB audio endpoints. Factory names are usually variants of **USB Audio CODEC** (e.g. Microphone/Line for recording, Speakers for playback). Many operators rename them in the OS sound panel (e.g. to “FTDX101”) — that is fine; pick the renamed entry in YWC.

### 18.2 YWC host setup

1. Open **Settings → Remote Audio**.
2. Enable **remote audio**.
3. Pick **Radio RX device** (capture / what you hear) and **Radio TX device** (playback / where mic audio goes). Both are **required** when the feature is on — YWC will not fall back to the PC’s default mic/speakers (blank TX previously caused browser-mic feedback into the room). Use **Refresh device list** after plugging the radio in. On Windows only **WASAPI** devices are shown. Entries that look like a USB codec are sorted first and marked with a radio icon (📻); renamed devices stay in the full list without the icon.
4. **Save Settings**. RX/TX software gain is adjusted later via **Mic & Gain** on Home (or the pop-out), not on this page.

### 18.3 HTTPS for remote browsers

Browsers only allow the microphone on a **secure context** (`https://` or `localhost`).

1. Under **Settings → Web / HTTP**, enter any WireGuard/LAN IPs or hostnames under **Certificate SAN hostnames / IPs**.
2. Click **Generate self-signed certificate**.
3. Enable **HTTPS**, note the HTTPS port (default **8443**), **Save Settings**, and **restart YWC**.
4. On the remote machine, open `https://<host>:8443` (not the HTTP port). Accept the certificate warning once (Advanced → Proceed), or trust the cert in the OS if you prefer.

Local testing on the same PC can use `http://localhost:8080` without HTTPS.

### 18.4 Operating

1. Open the Index page (over HTTPS if remote).
2. Click **Start audio** on the Remote Audio bar. Grant microphone permission when asked.
3. You should hear RX audio; speak into the mic (levels show on the bar). Use **Mic & Gain** to pick the browser microphone, choose **Opus** or **PCM16**, and adjust RX/TX software gain (codec and mic choice are remembered in the browser; gain is saved on the host).
4. Use **TX** / your TX toggle key to key the radio (on Home or on the Remote Audio pop-out). Audio flows continuously (like Mumble); CAT controls PTT.
5. **Mute mic** / **Mute RX** as needed. **Stop** ends the session and closes host audio devices.
6. Only **one** audio session is allowed at a time; a second browser is rejected busy.

The status line shows the active codec while streaming (for example `Streaming (opus)`). A codec change requires stopping and reconnecting remote audio — it does not apply to an active session.

#### Pop-out window (keep audio while changing pages)

Audio on the Index page stops when you leave Home (for example to open **Settings**). To keep streaming:

1. Click **Pop out** on the Remote Audio bar. A small **Remote Audio** window opens.
2. If you were already streaming, YWC hands the session to that window (brief reconnect). Otherwise click **Start audio** in the pop-out.
3. Leave the pop-out open while you use Settings or other pages. Home shows status such as *In pop-out window (streaming)*; mute switches on Home still control the pop-out session. Filter-scope on Home keeps receiving live RX spectrum from the pop-out. The pop-out has its own **TX** button (same PTT as Home) and a **VFO A / VFO B** badge for the current transmit VFO; it also honours the same **TX toggle key** from Settings when that window is focused. TX on/off stays in sync with the main window when Home is open.
4. **Stop** on Home stops the pop-out session. **Close** in the pop-out (or closing the window) ends audio and returns control to Home.
5. If the browser blocks the window, allow pop-ups for the YWC site and try again.

### 18.5 Troubleshooting

| Symptom | What to try |
|---------|-------------|
| Mic permission denied / “requires HTTPS” | Use the HTTPS URL; regenerate the cert with the IP you type in the address bar; restart after enabling HTTPS. |
| No RX sound | Check Radio RX device; confirm radio AF gain / USB volume; look at the RX meter while Start audio is active. |
| TX keys but no modulation | Confirm MOD SOURCE / USB; check Radio TX device is the radio USB **Speakers**/playback endpoint (not PC speakers); unmute mic; watch the TX meter while speaking. |
| TX keys and you hear yourself in the PC speakers | Radio TX device is wrong (or was left blank on an older build). Set it to the radio USB playback / Speakers endpoint and Save. |
| Choppy audio | Prefer wired Ethernet/VPN; reduce other load; stay on LAN/VPN (no TURN/WebRTC in v1). Use **Opus** instead of PCM16 on limited links (see [§18.6](#186-audio-codecs-opus-vs-pcm16)). |
| “Audio session busy” | Stop audio in the other tab/browser (or pop-out window) first. |
| Audio dies when opening Settings | Use **Pop out** before leaving Home so the session lives in the separate window. |
| Pop-out blocked | Allow pop-ups for the YWC origin; click **Pop out** / **Open pop-out** again. |
| Devices missing from the list | Unplug/replug USB; Refresh device list; check OS privacy permissions for microphone (host process). |
| Session connects but no RX (RX meter stuck at 0) on macOS | macOS treats the radio USB **recording** endpoint as a microphone. Grant **System Settings → Privacy & Security → Microphone → Yaesu Web Control**. If the app was built without `NSMicrophoneUsageDescription`, macOS never prompts and PortAudio still “opens” the device but returns silence — rebuild/reinstall a DMG that includes that key (see `scripts/macos/build-dmg.sh`), then allow Microphone when prompted. |
| Wrong browser mic | Open **Mic & Gain** on the Remote Audio bar (or use the pop-out controls) and pick the right browser microphone. Choice is remembered in the browser. |
| Opus unavailable / forced to PCM16 | The browser needs WebCodecs `AudioEncoder` / `AudioDecoder` (current Chrome, Edge, or Chromium). Older Safari/Firefox builds may only offer PCM16. |
| Voice Control vs radio USB | Keep Voice Control’s mic on your headset; leave Remote Audio devices on the Yaesu USB endpoints. |

### 18.6 Audio codecs (Opus vs PCM16)

Remote Audio always samples at **48 kHz mono** on the host bridge. What changes is how those samples are packed on the WebSocket:

| | **Opus** (default / recommended) | **PCM16** |
|--|--|--|
| What it is | Compressed speech (VOIP-style) | Uncompressed 16-bit samples |
| Approx. payload per direction | **~32 kb/s** | **~768 kb/s** |
| Duplex (RX + TX) | Roughly **~64 kb/s** (+ framing) | Roughly **~1.5 Mb/s** (+ framing) |
| Audio quality | Good for SSB / voice; may soften noise floor slightly vs PCM | Bit-for-bit transparent (aside from gain / device resampling) |
| Best for | Limited bandwidth, VPN, cellular, choppy links | Fast LAN when you want maximum fidelity or Opus is unavailable |
| Browser requirement | WebCodecs Opus encode/decode | Any modern browser |

**Preference:** YWC offers **Opus first** whenever the browser supports it. Choose **PCM16** only if you need uncompressed audio on a fast LAN, or if Opus is greyed out in your browser.

Both directions use the same codec for a session. Stop remote audio and connect again after changing the selector.

---
