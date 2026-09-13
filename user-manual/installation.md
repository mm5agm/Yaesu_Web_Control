## 2. Installation

> **USB CAT tip:** if the radio's serial ports never appear, or **Test Connection** fails with no reply from the radio, install the Silicon Labs CP210x VCP driver on **Windows or macOS** and reboot — see [§2.4](#24-usb-serial-driver-windows--macos--linux). **Linux** users can normally skip this (CP210x is in the kernel).

### 2.1 Windows (installer)

1. Download the installer from the [GitHub Releases page](https://github.com/mm5agm/Yaesu_Web_Control/releases).
2. Run the installer. .NET 10 is bundled — you do not need to install it separately.
3. A desktop shortcut and a Start Menu entry are created automatically.
4. The first time you run the app, Windows may show a **Smart App Control** or **Unknown Publisher** warning. Click **More info → Run anyway** to proceed. This warning appears because the installer is not signed with a commercial certificate.

If Device Manager never shows the radio's COM ports, or **Test Connection** fails later, install the [Silicon Labs CP210x VCP driver](https://www.silabs.com/software-and-tools/usb-to-uart-bridge-vcp-drivers?tab=downloads), **reboot**, and try again ([§2.4](#24-usb-serial-driver-windows--macos--linux)).

This is the supported product build: tray icon, SDR spectrum, and Voice Control.

### 2.2 macOS (DMG)

macOS ships as an **unsigned CAT-only** app (menu-bar status item; no SDR spectrum or Windows Voice Control). There is no Apple Developer ID / notarization — this is an open-source project and that licence costs money every year. Gatekeeper will warn the first time you open a download from the internet, the same way Windows warns about the unsigned installer.

1. Download the DMG that matches your Mac from the [GitHub Releases page](https://github.com/mm5agm/Yaesu_Web_Control/releases):
   - Apple Silicon (M1 / M2 / M3 / …): `Yaesu_Web_Control_CAT_*_macos-arm64.dmg`
   - Intel: `Yaesu_Web_Control_CAT_*_macos-x64.dmg`
2. Open the DMG and drag **Yaesu Web Control** into **Applications**.
3. The first time you launch it, macOS Gatekeeper will block an unsigned app. Do one of the following:
   - **Right-click** (or Control-click) **Yaesu Web Control** in Applications → **Open** → **Open** again in the dialog, or
   - Try to open it normally, then open **System Settings → Privacy & Security**, scroll to the message about YWC being blocked, and click **Open Anyway**.
4. A menu-bar status item appears (Open / About / Open user data folder / Exit). The browser UI is at `http://localhost:8080` (or the port shown in the menu-bar tooltip). .NET is bundled — you do not need to install the SDK.

If `/dev/cu.usbserial-…` devices never appear, or **Test Connection** fails later, install the [Silicon Labs CP210x VCP driver](https://www.silabs.com/software-and-tools/usb-to-uart-bridge-vcp-drivers?tab=downloads), **reboot**, allow the Driver Extension if prompted, and try again ([§2.4](#24-usb-serial-driver-windows--macos--linux)).

Set **Serial Port** to a `/dev/cu.*` device (see §3). SDR and Voice Control sections are hidden on this host.

#### Alternative: build from source (macOS / Linux)

If you prefer not to use the DMG, or you are on Linux without Docker, build the **CAT-only** target with the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```bash
git clone https://github.com/mm5agm/Yaesu_Web_Control.git
cd Yaesu_Web_Control
dotnet run --project Yaesu_Web_Control.csproj --framework net10.0
```

Then open `http://localhost:8080` (or the port shown in the console / menu-bar tooltip on macOS).

- **macOS:** a menu-bar status item mirrors the Windows tray (Open / About / Open user data folder / Exit). Developers can also run `make dmg` on a Mac to produce the same unsigned DMG locally.
- **Linux:** the process runs as a console host — use Ctrl+C to stop, or disable auto-exit (Settings) and leave it running headless.
- Set **Serial Port** to a USB-serial device path (see §3). SDR and Voice Control sections are hidden on this host.

### 2.3 Linux Docker (including Raspberry Pi)

For an always-on CAT controller on an x64 PC or arm64 Pi, use the published multi-arch image (`linux/amd64` and `linux/arm64`) from the GitHub Container Registry, or build locally from the repo `Dockerfile` / `docker-compose.yml`.

**Image:** `ghcr.io/mm5agm/yaesu_web_control` — each release is tagged (e.g. `v2.4.2`, `v2.4.3-pre6`); `latest` tracks the newest **full** release only (not pre-releases).

On a **Linux** host you normally do **not** need Silicon Labs' CP210x package — support is already in the kernel. Plug the radio in, confirm a `/dev/ttyUSB*` or `/dev/ttyACM*` node appears, then clone or copy `docker-compose.yml` from the repo and run:

```bash
# Find the radio's USB-serial node on the host
ls /dev/ttyUSB* /dev/ttyACM*

export YWC_SERIAL_DEVICE=/dev/ttyUSB0   # adjust to match

# Optional — Remote Audio (radio USB codec via ALSA). Compose maps /dev/snd.
# Confirm GIDs if permission-denied: getent group dialout audio video
# export YWC_DIALOUT_GID=20
# export YWC_AUDIO_GID=29
# export YWC_VIDEO_GID=44   # Radio Display (UVC/V4L2); also set YWC_VIDEO_DEVICE=/dev/video0
# arecord -l && aplay -l   # find the Yaesu USB audio card on the host
# ls /dev/video*           # find HDMI capture / webcam nodes for Radio Display

# Prefer the published image (pin a release tag instead of :latest if you like)
docker compose pull
docker compose up -d

# Or rebuild from the Dockerfile on this machine:
# docker compose up -d --build
```

Open `http://<host>:8080`. Settings and logs persist under `./data/ywc` by default. The container entrypoint fixes ownership of that volume automatically (so a host-created `./data/ywc` does not need a manual `chown`). Auto-exit and local browser-open are disabled in the container. If the serial port is permission-denied, set `YWC_DIALOUT_GID` to the host `dialout` GID (`getent group dialout`). For Remote Audio, compose also maps `/dev/snd` and adds the host `audio` group (`YWC_AUDIO_GID`, often `29`); pick the radio USB codec in **Settings → Remote Audio**. For Radio Display, map `/dev/video*` (`YWC_VIDEO_DEVICE`) and the host `video` group (`YWC_VIDEO_GID`, often `44`); see [§19](radio-display.md#19-radio-display) and comments in `docker-compose.yml`.

### 2.4 USB serial driver (Windows / macOS / Linux)

Modern Yaesu HF radios (FTdx101, FTdx10, FT-710, and similar) talk CAT over USB through a built-in **Silicon Labs CP210x** USB-to-UART bridge. You only need Silicon Labs' official **CP210x Virtual COM Port (VCP)** package when the OS does not already present working ports — most often on a fresh **Windows** or **macOS** install after CAT or port discovery fails. **Linux** already includes a CP210x kernel module, so skip the Silicon Labs download there unless something is clearly broken.

**[CP210x USB to UART Bridge VCP Drivers (Silicon Labs downloads)](https://www.silabs.com/software-and-tools/usb-to-uart-bridge-vcp-drivers?tab=downloads)**

| OS | What to do |
|---|---|
| **Windows** | If Device Manager never lists the radio's COM ports, or **Test Connection** opens the port but the radio never answers, download and install the Windows VCP package from that page, then **reboot**. |
| **macOS** | If `/dev/cu.usbserial-…` devices never appear, or CAT never answers, install the Macintosh VCP package from that page, then **reboot**. Allow the Driver Extension under System Settings if prompted. |
| **Linux** | **Skip the Silicon Labs package.** The in-kernel `cp210x` driver normally creates `/dev/ttyUSB*` (or similar) as soon as you plug the radio in. If ports are missing, check the cable, USB power, and that your user is in the `dialout` group — not a vendor VCP install. |

**After installing the Silicon Labs package (Windows / macOS), reboot the host.** Skipping the reboot is a common reason the port appears in the OS (or opens in YWC) but **Test Connection** fails with no reply from the radio.

Many of these radios expose **two** virtual ports on one USB cable:

- **Enhanced** — CAT (frequency, mode, meters). **This is the port YWC must use.**
- **Standard** — TX controls (PTT, CW keying, digital). Not for CAT.

On Windows, Device Manager labels them clearly. On macOS they show as two `/dev/cu.usbserial-…` devices — if Test Connection fails on one, try the other (and install the VCP driver + reboot only if neither works).

---
