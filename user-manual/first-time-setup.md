## 3. First-Time Setup

Before the app can communicate with your radio you need to tell it which serial port the radio is connected to and what baud rate to use.

**Required — radio connection:**

1. Open a browser and go to **http://localhost:8080**. If port 8080 was already in use (e.g. Plex, Jenkins, MiniTool ShadowMaker), YWC will have automatically picked the next free port from 8081–8089.
   - **Windows:** hover over the YWC tray icon by the clock to see the actual URL — or double-click the tray icon to open it.
   - **macOS:** use the menu-bar status item (Open), or check the console log.
   - **Linux / Docker:** use the URL printed at startup, or `http://<host>:8080` from another device on the LAN.
2. Click the **Settings** link in the navigation bar.
3. Set **Radio Model** to your transceiver: **FTdx101MP** (200 W, dual receiver), **FTdx101D** (100 W, dual receiver), **FTDX3000** (100 W, single receiver), **FTdx10** (100 W, single receiver), or **FT-710** (100 W, single receiver).
4. Set **Serial Port** to the radio's **Enhanced** (CAT) virtual port — not the Standard / TX-control port if two appear:
   - **Windows:** a COM port (e.g. `COM3`). Use **Diagnostics → Ports** or Device Manager (*Silicon Labs Dual CP210x… Enhanced COM Port*).
   - **macOS:** prefer a `/dev/cu.*` device (e.g. `ls /dev/cu.usbserial-*` in Terminal). Try the other `cu.usbserial-…` node if Test Connection fails.
   - **Linux / Docker:** `/dev/ttyUSB0`, `/dev/ttyACM0`, or a stable `/dev/serial/by-id/…` path. In Docker the path inside the container should match what you passed via `YWC_SERIAL_DEVICE`.
5. Set **Baud Rate** to match the radio's CAT baud rate. The factory default is **38400** on all supported radios. You can verify or change this on the radio under **Menu → CAT Rate**.
6. Select your **Band Plan**: Region 1 (Europe/Africa/Middle East), Region 2 (Americas), Region 3 (Asia-Pacific), or Japan.
7. If you run digital modes (FT8, FT4, RTTY, PSK) via USB audio, see the FAQ (§15) for a one-time radio menu change needed on the radio itself — it's not configurable from YWC.
8. On a headless macOS/Linux/Docker host, turn **off** **Automatically exit when no browser is connected** in Settings (Docker already forces this off) so closing the browser does not stop the CAT server.
9. Click **Save Settings**, then **Test Connection**. A green tick means the app is talking to the radio.

If you see a red cross, double-check the serial port (Enhanced vs Standard) and baud rate. On **Windows or macOS**, if the ports are missing or CAT still never answers, install the Silicon Labs CP210x VCP driver and reboot ([§2.4](installation.md#24-usb-serial-driver-windows--macos--linux)). On **Linux**, confirm your user (or the container's dialout group) can open the device — you normally do not need Silicon Labs' package. See [§15.11](faq.md#1511-test-connection-fails--cat-does-not-respond-over-usb).


**Optional — extras you can set up later in Settings:**

- **SDR spectrum display** (Section 6.3) — **Windows host only**; connect an SDR to your radio's 9 MHz IF output to get a live spectrum and waterfall.
- **DX cluster** (Section 6.6) — connect to a DX cluster server to overlay live DX spots on the spectrum (and populate the DX Spots list on any host).
- **CW memory messages** (Section 6.5) — pre-fill the M1–M5 CW keyer memories.
- **Roofing filters** (Section 6.4) — tell the app which optional roofing filters are fitted on your radio so the dropdown shows only the ones you actually have.
- **Voice Control** (Section 17) — **Windows host only**.

None of these are required for basic operation. Get the radio connection working first; come back for the extras when you want them.

---
