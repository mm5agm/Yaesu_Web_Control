## 19. Radio Display

Radio Display captures the radio’s **external video output** (or any USB UVC webcam) on the YWC host and streams it to the browser as **MJPEG**. It complements CAT control when you need to see menus, meters, or status that are not exposed over CAT. No OBS or separate streaming app is required.

### 19.1 Hardware chain

```text
Radio DVI-D / HDMI video output
            │
            ▼
    Suitable DVI-D→HDMI cable / adapter (model-specific — see §19.2)
            │
            ▼
  HDMI→USB capture dongle (UVC)
            │
            ▼
 Computer / Pi / Docker host running Yaesu Web Control
            │
            ▼
        Web browser (Index panel or /RadioDisplay pop-out)
```

Typical radio panel resolutions are modest (e.g. FTDX-10 **800×480** or **800×600**). Many cheap capture sticks still open at **720p/1080p** — leave **Max width** at **800** so the host downscales before JPEG encode (important on a Raspberry Pi). On Windows and macOS the host picks **one** capture size for **15 / 30 / 60 fps** (this is what **Auto size** on the panel does; you can override it — see §19.3): a 4:3 mode at least 800 px wide when the dongle has one (typically **800×600** after scale, or 1024×768 → 800×600). Changing FPS does not jump between 640×480 and 720p. **640×480** is used only if the dongle has nothing ≥800 wide. **60 fps** may stay ~30 if that 4:3 pin cannot run 60 — the size stays put rather than switching to 1080p60. The **15 fps** setting is paced in software even when the pin’s floor is 20.

#### Set the radio's output resolution first

Before touching anything in Yaesu Web Control, check what the radio is actually
sending. All three radios with a DVI-D output have the same menu item, and it
matters more than any setting on my side:

| Radio | Menu path | Values |
|---|---|---|
| FTDX101MP / D | DISPLAY SETTING → EXT MONITOR → **PIXEL** | 800×480 / 800×600 |
| FTDX10 | DISPLAY SETTING → EXT MONITOR → **PIXEL** | 800×480 / 800×600 |
| FT-710 | Menu **04 (EXT-MONITOR) → 02 PIXEL** | 800×480 / 800×600 |

**The factory default is 800×480, and I recommend changing it to 800×600.**

Here is why. At the default **Max width** of 800 the host picks an 800×600
capture, because that is the 4:3 mode most dongles offer — almost none of them
offer 800×480 at all. If the radio is still set to 800×480, the dongle has to
turn 480 lines into 600, and on the two dongles I have measured it does that by
**stretching the picture to fill the frame rather than adding black bars**. The
result is a silently squashed display, about 25% too tall, with nothing on
screen to tell you it has happened. Setting the radio to 800×600 makes the whole
chain pixel-for-pixel with no scaling at either end.

If you would rather leave the radio on 800×480, set the **capture size**
dropdown to a 16:9 mode instead — 1280×720 is within about 7% of 5:3, which is
far closer than 4:3 is. It costs more bandwidth for no extra detail, but the
geometry will look right.

#### Bigger is not better

It is tempting to raise **Max width** for a sharper picture. It does the
opposite. The radio only ever sends 800 pixels across, so every larger capture
mode is the dongle's own scaler inventing pixels — no additional detail exists
to recover. I measured this on my FTDX101MP by comparing the spatial frequency
content of native and upscaled captures: the 800×600 capture carries real
detail all the way to the limit, while a 1920×1080 capture of the same screen
has had its fine detail attenuated by more than 25 dB by the interpolation. The
larger frames are both **softer** and considerably more expensive:

| Capture mode | Mean frame | At 15 fps |
|---|---|---|
| 800×600 (native) | 78 KB | 1.2 MB/s |
| 1280×960 | 114 KB | 1.7 MB/s |
| 1920×1080 | 158 KB | 2.4 MB/s |

So leave the capture size on **Auto** unless you have a specific reason not to.
It gives the best picture *and* the lowest load — which is unusual enough to be
worth stating plainly. If you want to see this for yourself, the **capture
size** dropdown on the Radio Display panel lets you switch modes and compare
(§19.3).

### 19.2 Electrical safety

Yaesu Web Control does **not** supply or electrically protect video adapters or capture hardware.

- Verify that any **DVI-D→HDMI** cable or adapter is electrically suitable for **your** radio model before connecting.
- Do **not** assume every passive DVI-D→HDMI adapter is safe on every Yaesu transceiver.
- Follow the radio manufacturer’s guidance and published investigations of Yaesu video-output interfaces (for example community DVI-D→HDMI risk discussions on YouTube / forums).
- Treat the capture chain as an external accessory under your responsibility.

### 19.3 Settings and Index panel

1. Open **Settings → Radio Display** and enable **Radio display**, then Save.
2. On Home, the **Radio Display** card appears. Pick the capture device, then click **Start**. The stream does **not** open until you start it (so a leftover device selection cannot grab the dongle). Tick **Auto** if you want the previous behaviour — start as soon as the panel opens with a device selected. Preference is stored in the browser.
3. **Capture size** — the dropdown between the device list and the frame rate.
   **Auto size** (the default) lets the host rank the dongle's modes and pick
   the one that matches a radio panel, which is the right answer for almost
   everyone; read §19.1 before overriding it, because a larger mode is nearly
   always the dongle upscaling the same 800-pixel-wide picture rather than
   showing you more of it. The list contains only the **MJPEG** modes the
   device actually advertises — an uncompressed mode at the same size is the
   USB2 low-frame-rate trap and is never offered. Changing this **restarts the
   capture** (the pin is chosen when the device is opened), so the picture
   drops for a second or two. A size you picked that a later dongle does not
   offer silently reverts to Auto rather than leaving the panel unable to open.
   The dropdown is hidden when the host cannot enumerate modes — on macOS, and
   on any device with no MJPEG mode at all.
4. Frame rate (**15 / 30 / 60 fps**; default **15**) and image quality (**Low / Medium / Max** = 40 / 65 / 85; default **Max**) are chosen on the same card. The FPS list is a **target** — USB bandwidth, JPEG encode, and host CPU can still deliver less. Rates above what the capture device advertises (for example **60** on a 30 fps stick) are hidden. **Max** keeps the capture JPEG (least CPU when the dongle already sends MJPEG). **Low** / **Medium** recompress — smaller stream, more CPU. Prefer **15 fps** on a Raspberry Pi; use Low/Medium there only if the link needs a smaller stream. On Windows/macOS, 15 / 30 / 60 share the same panel-sized pin (see §19.1); the badge should track the dropdown (15 via pacing if the pin floor is 20).
5. Other controls:
   - **Start / Stop** — attach or release the MJPEG viewer (Stop lets the host drop the dongle after a couple of seconds)
   - **Fit / Fill** — two-button toggle on the video bar: **Fit** (`object-fit: contain`, whole TFT visible) or **Fill** (cover, pane filled and edges may crop). On the Index card, **Fill** is capped to the radio’s aspect ratio so a wide or short pane never crops the TFT to a header strip — Fit and Fill may look nearly the same there. In the pop-out window, Fill is true cover and may crop when the window is not roughly 4:3.
   - **Fullscreen** — fullscreen the card
   - **Pop out** — opens `/RadioDisplay` in a separate window (closes the Index panel); if you were streaming, the pop-out keeps the stream
   - **Reattach** (pop-out) — returns the stream to the main window and closes the pop-out
   - **Close** — stops the stream and closes the panel (Show button restores it); preference stored in the browser

If the capture dongle is unplugged (or the host cannot open the saved device), the badge stays **Disconnected**. Recovery: (1) refresh the device list, (2) confirm the intended capture device is present, (3) click **Start**. Windows camera indexes can move when devices are replugged — do not assume the old index still refers to the same camera. The host does **not** automatically reopen whatever camera now sits at the old index (that would be the laptop webcam on many PCs). **Auto** still means start when the panel opens with a saved device, not retry after an unplug; reloading the page while disconnected also leaves capture halted until you press **Start** or pick a different device.

Capture opens while at least one browser is viewing the stream, and stays open for a couple of seconds after the last viewer disconnects so **Pop out** / **Close** does not tear down the USB capture device mid-handoff. After that idle window the host releases the dongle so an idle Pi pays no capture CPU. Max width stays at **800** (host default) for modest radio panels — except when you have chosen a capture size explicitly, in which case that width is used for the encode too, so a mode you asked for by name is not then quietly scaled back down.

### 19.4 CAT scope controls

The Radio Display picture is a live capture of the radio’s TFT. Clicks on that image never reach the touchscreen (the dongle is one-way). On radios that expose the spectrum scope over CAT (`SS`), a **Controls** button on the video bar (next to FPS / quality) shows scope controls beside the video by default — the stream on the left, buttons on the right — so nothing floats over the picture. Hide the column with **✕** on the column header (or **Controls** on the video bar); the video recentres. **Controls** only shows or hides the panel — when hidden, click it again to bring controls back in the same layout (docked column or floating panel). The picture-in-picture icon on the column header switches to a floating panel; the sidebar icon on the floating panel pins the column again (hover either icon for its label). **✕** closes the panel without changing layout mode. Drag the column’s left edge to widen or narrow it (Arrow keys nudge when the edge is focused; Home/End jump to the limits; double-click restores the default width); the choice is remembered in the browser. **Reattach** from the pop-out window restores the controls panel in the same docked or floating layout you had before pop-out.

The controls change what the radio draws: Center / Cursor / Fix, 3DSS vs waterfall, Expand (L / N / S), FFT SPAN, FFT SPEED, Level, Peak, Marker, Hold, Color / NB colour (FTdx101 only), and AF-FFT / OSC attenuators and timebase. The pop-out window behaves the same way. Your docked vs floating choice is remembered in the browser.

**FTdx10** and **FTdx101MP/D** show **Controls**. **FTdx101** also has MAIN / SUB (two independent scopes). **FTdx10** is a single receiver, so that row is omitted. **FT-710** stays off until the `SS` writes have been probed on that radio.

**MULTI** (scope + oscilloscope + AF-FFT on the TFT) has no CAT command on any supported radio. The MULTI group in YWC is collapsed by default — expand it for AF-FFT ATT (0 / 10 / 20 dB) and OSC ATT / timebase, which apply once MULTI is already showing on the radio. Press MULTI on the TFT; YWC cannot turn it on.

SPAN, display mode, and SPEED on the front panel live-sync the highlighted buttons while the controls are visible (docked column or floating panel).

If Radio Display is **off**, FTdx101 and FTdx10 still have a standalone **Radio Scope** card above the SDR panels with the same CAT controls. Enabling Radio Display hides that card so the buttons are not shown twice; use **Controls** on the video bar instead.

### 19.5 Raspberry Pi and Docker

**Bare metal (Linux / Pi):**

- Plug in the capture dongle; confirm nodes with `ls /dev/video*` and names under `/sys/class/video4linux/*/name`.
- Ensure the YWC process user can open the device (often membership of the **`video`** group).
- Keep **Max width ≤ 800** (host default) and prefer **15 fps** on Pi-class CPUs. **Max** quality keeps the capture JPEG (least extra CPU). **Low** / **Medium** recompress and add encode load — use them only if the browser link needs a smaller stream. Raising FPS to 30–60 increases load sharply.

**Docker:** map the V4L2 device and the host **video** group GID, similar to serial/audio:

```yaml
devices:
  - ${YWC_VIDEO_DEVICE:-/dev/video0}:${YWC_VIDEO_DEVICE:-/dev/video0}
  - ${YWC_VIDEO_DEVICE_ALT:-/dev/video1}:${YWC_VIDEO_DEVICE_ALT:-/dev/video1}
group_add:
  - "${YWC_VIDEO_GID:-44}"   # host `getent group video`
```

UVC dongles typically expose **video0** (capture) and **video1** (metadata). The device list is built from `/sys/class/video4linux`, which is visible even when the matching `/dev/videoN` is not mapped into the container — selecting an unmapped or metadata node fails with **Could not open capture device index N**. Map both nodes, add the **video** group, then choose the capture device (usually **USB Video (video0)**).

See comments in `docker-compose.yml`. Install the Silicon Labs (or other) serial driver on the **host** as usual; video uses the kernel UVC/V4L2 stack.

### 19.6 Troubleshooting

| Symptom | What to try |
|---------|-------------|
| Panel hidden | Enable Radio Display in Settings, then Show Radio Display; check Close was not pressed. |
| `/api/video/stream` → 403 | Feature disabled or no device key saved. |
| Black / disconnected | Wrong device index; another app holding the UVC device exclusively; unplug/replug. Unplug is reported as **Disconnected** and the host does **not** reopen that index (Windows may have given it to another camera). Recovery: refresh the device list, confirm the intended device is present, then click **Start**. `/api/video/stream` returns **409** while halted. **Auto** and page reload cannot bypass the halt. |
| Panel blank while badge says Streaming | The MJPEG `<img>` connection dropped; it should reconnect on its own within a few seconds. Hard-reload if it does not. |
| High CPU on Pi | Prefer **15 fps**; confirm the dongle is not capturing full 1080p without downscale. **Low** / **Medium** quality add a recompress step. |
| Resolution jumps when changing FPS (640×480 vs 800×600 vs 720p) | Use a current build. 15 / 30 / 60 share one ≥800 4:3 pin when the dongle has one; **640×480** only if nothing is ≥800 wide. |
| 15 fps badge shows ~20 | Use a current build — the host paces to 15 even when the pin’s floor is 20. |
| Device list empty (Linux) | Check `/dev/video*`, `video` group, Docker `devices:` / `group_add`. |
| Could not open capture device index N (Docker) | That index is listed from sysfs but `/dev/videoN` is not in the container, or it is a metadata/codec node. Map `video0` **and** `video1`, set `YWC_VIDEO_GID` (`getent group video`, often 44), and select the capture node (usually video0). |
| Device list empty (macOS) | Launch via the `.app` / `scripts/macos/run-dev.sh` (not bare `dotnet run`), then allow **Camera** for Yaesu Web Control. |
| `Could not open capture device` on Intel Mac | OpenCvSharp’s `osx-x64` native library needs Homebrew **libavif**. Run `brew install libavif`, restart YWC, pick the device again. (Apple Silicon builds do not need this.) |
| Stream stays black / FPS stays 0 after allowing Camera | Quit YWC fully and relaunch via `scripts/macos/run-dev.sh` (or the DMG). The first permission grant must complete before OpenCV can deliver frames; also confirm the HDMI cable is live into the USB capture dongle. |
| Host app exits when stopping / popping out the stream | USB HDMI dongles crash if the capture graph is closed and immediately reopened. Use a current build — pop-out hands off the live device; Close waits ~2 s before release. |

OCR, click-through of the captured UI, capture-device audio, and WebRTC are **not** in this version. Drive the radio’s scope from **Controls** on the video bar (§19.4), not by clicking the image.

---
