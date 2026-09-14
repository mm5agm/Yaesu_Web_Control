# Running YWC on a Raspberry Pi (Docker)

A runbook for testing Yaesu Web Control on a Raspberry Pi using Fabio's
development Docker image. The image is built from the `feature/add-audio-rx-tx`
branch, so it lets us test the Linux build and Remote Audio (RX/TX) together.

**Verified working on:** Pi 3B, hostname `pi3b-64`, Raspberry Pi OS 64-bit
(aarch64), Docker 29.7.2, Docker Compose 5.4.0.
**Pi address (example):** `192.168.68.64`

---

## Everyday use

1. From the PC, connect to the Pi:
   ```bash
   ssh colin@192.168.68.64
   ```
2. Start YWC:
   ```bash
   docker compose up -d
   ```
   `-d` runs it in the background. It is safe to run again at any time — if the
   container is already up it simply re-confirms it.
3. On the PC's browser, open **http://192.168.68.64:8080** and drive it exactly
   as on Windows.

The container is set to `restart: unless-stopped`, so it also comes back on its
own after a Pi reboot.

---

## Shutting down

There are two levels — pick the one that matches what you want.

**Stop YWC but leave the project in place** (the usual one). This stops and
removes the container; your data in `~/data/ywc` is untouched, and a later
`docker compose up -d` brings it straight back:
```bash
docker compose down
```

**Pause it without removing the container** (starts again faster, keeps the
container around):
```bash
docker compose stop      # later: docker compose start
```

**Power the Pi off cleanly.** Stop YWC first, then shut the Pi down — don't just
pull the power, or you risk corrupting the SD card:
```bash
docker compose down
sudo shutdown -h now     # or: sudo poweroff
```
After this the SSH session ends and the Pi powers down. To use it again, power
it back on and start from step 1 of *Everyday use*.

> **Note:** `docker compose logs -f` and `docker compose restart` do **not** stop
> YWC — `logs -f` just follows the log (Ctrl+C stops watching, not the app), and
> `restart` bounces it back up. Only `down`, `stop`, and powering off actually
> take it offline.

---

## When you need to look under the bonnet

| I want to… | Command |
|---|---|
| Watch the live log | `docker compose logs -f` (Ctrl+C to stop watching) |
| Check it's running | `docker compose ps` |
| Grab Fabio's latest image | `docker compose pull && docker compose up -d` |
| Restart it | `docker compose restart` |
| Confirm the radio port is present | `ls -l /dev/ttyUSB*` |

---

## Radio connection

The FTdx101MP's built-in USB shows up as **two** CP210x serial ports, the same
as on Windows:

- **Enhanced** port → **CAT** (frequency + mode) — **this is the one YWC needs**,
  `/dev/ttyUSB0` in this setup.
- **Standard** port → TX control (PTT, CW keying, digital), `/dev/ttyUSB1`.

If the ports ever swap on a replug/reboot, identify them with:
```bash
cat /sys/class/tty/ttyUSB0/device/interface   # look for "Enhanced"
udevadm info -n /dev/ttyUSB0 | grep ID_USB_INTERFACE_NUM   # 00 = Enhanced/CAT
```
For a bulletproof path, use the fixed by-id name instead of `ttyUSBx`:
```bash
ls -l /dev/serial/by-id/                       # the if00 one is Enhanced/CAT
```
and set it in `~/.env` as `YWC_SERIAL_DEVICE=...`.

If you use the radio's **RS-232** port instead of USB (via a USB-to-serial
adapter on the Pi), set the radio menu **TUNER SELECT = INT** — an external ATU
disables the RS-232 jack — and note the CAT power-on (`PS`) command does not work
over RS-232.

Your settings, radio state and logs live in `~/data/ywc` on the Pi, so they
survive restarts and image updates.

---

## One-time setup (for a rebuild / a second Pi)

```bash
# Install Docker
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER      # then log out/in so docker runs without sudo

# Project folder + Fabio's compose file
mkdir -p ~/data/ywc
nano docker-compose.yaml           # paste Fabio's file, save with Ctrl+O, Ctrl+X

# First start (pulls the image — ~3 min on a Pi 3B)
docker compose up -d
```

The `docker-compose.yaml` is Fabio's development file: image
`fvalente/ywc-development:latest`, ports `8080`/`8443`, the `/data` volume, the
`/dev/ttyUSB0` serial device and `/dev/snd` audio passthrough, and the
`dialout`/`audio` group IDs.
