# Building Yaesu Web Control from source

Moved out of [README.md](../README.md), unchanged. Nobody installing the shipped
installer needs any of this.

```bash
# Windows product (tray + voice + SDR)
dotnet run --project Yaesu_Web_Control.csproj --framework net10.0-windows

# CAT-only host (macOS / Linux / Windows without WinForms features)
dotnet run --project Yaesu_Web_Control.csproj --framework net10.0
# then open http://localhost:8080

# macOS unsigned DMG (requires a Mac; self-contained .app)
# make dmg            # this Mac's arch
# make dmg-all        # arm64 + x64

# Linux Docker (x64 or arm64 / Raspberry Pi) — published multi-arch image
export YWC_SERIAL_DEVICE=/dev/ttyUSB0
# Optional Remote Audio: compose maps /dev/snd; override YWC_AUDIO_GID if needed
# Optional Radio Display: map YWC_VIDEO_DEVICE (/dev/video*) + YWC_VIDEO_GID (video group)
docker compose pull && docker compose up -d
# Local rebuild: docker compose up -d --build
```

On macOS/Linux set **Serial Port** in Settings to the matching `/dev/…` path. SDR and Voice Control UI are hidden on the CAT-only host. Install the [Silicon Labs CP210x VCP driver](https://www.silabs.com/software-and-tools/usb-to-uart-bridge-vcp-drivers?tab=downloads) and reboot before connecting the radio. In Docker, Remote Audio needs the host ALSA devices (`/dev/snd` + `audio` group); pick the radio USB codec in Settings after `compose up`. Radio Display (optional USB webcam / HDMI capture → MJPEG) needs `/dev/video*` + the `video` group — see USER_MANUAL §19.
