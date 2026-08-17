# Radio Display frame-rate investigation — working notes

Status as of 2026-08-13 21:00. Written mid-investigation as a handoff; the
problem is **not solved**. Read "Where it stands" and "Next steps" first.

**Symptom:** Radio Display set to 30 fps renders at ~20 fps from a USB HDMI
capture dongle. OBS Studio, on the same host with the same dongle, holds a
stable 30 fps.

---

## 1. Where it stands

The ~20 fps is **not** a host-side pacing bug (that was a separate, real bug,
fixed in `9284c9e` — see §3). It is a **USB 2.0 bandwidth limit on the
uncompressed YUY2 pin**. OBS reaches 30 fps because it uses the dongle's
**compressed MJPEG pin** instead.

Two independent workstreams came out of that:

| Workstream | State |
|---|---|
| Fit the uncompressed pin inside the USB budget (lower resolution) | Implemented, **untested by the user** |
| Use the MJPEG pin via Media Foundation (full resolution + 30 fps) | Implemented, **blocked** — MF enumerates 0 devices |

### Uncommitted work

```
 M Services/Video/VideoCaptureService.cs
?? Services/Video/WindowsMfMjpegSession.cs     (new file)
```

Last commit on the branch is `9284c9e`. Nothing from this investigation has
been committed. Both TFMs (`net10.0-windows`, `net10.0`) build clean.

---

## 2. The bandwidth arithmetic (the core finding)

YUY2 is uncompressed at **2 bytes per pixel**. A USB 2.0 UVC device delivers
roughly **22–24 MB/s** over high-bandwidth isochronous transfers
(3072 B × 8 microframes × 1000/s, less protocol overhead).

| Capture mode | Bytes/frame | At 30 fps | Fits USB 2.0? | Observed fps |
|---|---|---|---|---|
| 1280×720 YUY2 | 1,843,200 | 55.3 MB/s | No, 2.5× over | **~10** |
| 800×600 YUY2 | 960,000 | 28.8 MB/s | No, 1.3× over | **~20** |
| 640×480 YUY2 | 614,400 | 18.4 MB/s | Yes | not yet measured |

Every frame rate observed so far falls out of this table, which is why it is
treated as the root cause rather than a hypothesis:

- 22 MB/s ÷ 0.96 MB/frame ≈ 23 fps ceiling at 800×600 → measured 19.8–22.
- 22 MB/s ÷ 1.84 MB/frame ≈ 12 fps ceiling at 1280×720 → measured ~10.

**Corroborating evidence.** The built-in FaceTime HD camera sustains 30 fps
through the identical grab → encode → MJPEG-fan-out pipeline. That rules out
the host pipeline, the JPEG encoder, the SignalR/HTTP path, and the browser.
The dongle's own driver *advertises* `device=30fps` at 800×600, but advertised
mode ≠ deliverable bandwidth.

**Consequence:** at 30 fps you can have the dongle's full resolution or its
uncompressed pin, not both. Only MJPEG escapes the trade-off.

---

## 3. Timeline of today's commits (all pre-date this investigation)

| Commit | Time | What it fixed |
|---|---|---|
| `6812e07` | 14:24 | Capture stability; keep device open across viewer gaps |
| `0aca87d` | 17:33 | HDMI unplug detection, device names, MJPEG reattach |
| `1b1d9fb` | 17:41 | 50 ms sleep chunks + settings **file** read on the STA capture thread |
| `9284c9e` | 17:49 | `Thread.Sleep(1)` is a full ~15.6 ms Windows quantum |

`9284c9e` is worth understanding before touching pacing again. Without
`timeBeginPeriod(1)`, `Thread.Sleep(1)` waits an entire scheduler quantum, so
two leftover waits turned a 33 ms frame into ~50 ms — a hard lock at exactly
19.9 fps. The fix raises timer resolution while capturing and spins the last
quantum instead of sleeping it.

**This is why 19.9 fps and ~22 fps look alike but are different faults.** If a
future measurement lands on 19.9 specifically, suspect timer quantum. If it
lands on 20–22 with a large uncompressed mode, suspect bandwidth.

---

## 4. Dead ends — do not retry these

**Separate JPEG-encode thread.** Moving encode off the capture thread so it
overlaps the blocking `Read()` sounded right and made things *worse*
(~10 fps). Two OpenCV operations on different threads contend for the same
native thread pool. Reverted; grab and encode are back on one thread.

**Requesting 1280×720 to get MJPEG.** The FourCC hint did not stick, so the
graph opened **YUY2** 720p at 55 MB/s. This produced the 10 fps regression.
Never request a large frame size hoping MJPEG will be selected — if the hint
is ignored the fallback is catastrophic.

**OpenCV `CAP_DSHOW` + `CAP_PROP_FOURCC=MJPG` + `CAP_PROP_CONVERT_RGB=0`.**
OpenCV's DirectShow backend always decodes to BGR; `CONVERT_RGB=0` is not
supported there (it is on MSMF and V4L2). A passthrough probe was written,
confirmed non-functional, and removed. Verified empirically: the negotiated
log line kept reporting `mode=encode` with `YUY2`.

**Deadline pacing from session start.** Accumulating `frameInterval × n`
against a monotonic clock stacks on top of a blocking `Read()` that is
*already* the device clock. Current code only pays a leftover wait when
`Read` returned early (< 60 % of the interval).

---

## 5. Changes currently in the working tree

### `Services/Video/VideoCaptureService.cs`

- **Bandwidth-aware capture size.** `PreferredCaptureSize(maxWidth, targetFps)`
  picks the largest 4:3 mode fitting `Usb2UvcBytesPerSecond` (22 MB/s) at the
  requested rate. 15 fps keeps 800×600; 30 fps drops to 640×480; 40/60 fps go
  lower. Applies only to the OpenCV fallback path.
- **No leftover wait after a blocking grab** (see §4).
- **`BufferSize = 3`** so a sample can queue during JPEG encode.
- **Frame pulse.** `WaitForFrameAsync` waits on a `TaskCompletionSource`
  swapped per frame instead of polling every 20 ms.
- **Diagnostic log line**, emitted on the first frame of every session:

  ```
  Radio Display negotiated 640x480 YUY2 device=30fps
    (encode 30 fps, maxW=800, mode=encode, uncompressed 18.4 MB/s vs ~22 MB/s USB2)
  ```

  `mode=` is `passthrough` (MJPEG, no re-encode) or `encode` (decode → resize →
  JPEG). This line is the fastest way to diagnose any future report.
- **`RunMfSession`** runs the Media Foundation attempt on a dedicated **MTA**
  thread. The main capture thread is **STA** because DirectShow graphs need it,
  but the MF source reader misbehaves in a single-threaded apartment.
  `_mfUnavailable` latches after a failed probe so reconnects don't re-probe.

### `Services/Video/WindowsMfMjpegSession.cs` (new)

Hand-written Media Foundation interop that selects the device's native MJPEG
media type, sets `MF_READWRITE_DISABLE_CONVERTERS`, and copies JPEG bytes
straight out of the sample buffer — no decode, no re-encode. Prefers
1280×720, then 1920×1080. Logs every format the device exposes.

---

## 6. The blocker: Media Foundation sees no devices

```
Radio Display: Media Foundation sees 0 capture device(s): (none); want index 1 'USB Video'
Radio Display: Media Foundation sees 0 capture device(s): (none); want index 0 'FaceTime HD Camera (Built-in)'
```

`MFEnumDeviceSources` returns an empty list for **both** cameras, including the
built-in one. This is a host/enumeration problem, not a dongle problem.

`MFStartup` succeeded (its failure path logs separately and did not fire), so
the failure is in `MFCreateAttributes`, `SetGUID`, or `MFEnumDeviceSources`.
HRESULT logging has been added to all three but **has not been run yet** — that
log line is the single most valuable next data point.

**Leading hypothesis: Windows camera privacy.** With *Settings → Privacy &
security → Camera → Let desktop apps access your camera* disabled, Media
Foundation returns an empty camera list while DirectShow keeps working. That
matches the evidence exactly: OpenCV/DSHOW works, OBS (also DirectShow) works,
MF sees nothing. Unconfirmed.

Distinguishing the cases from the new log:

- `hr=0x00000000, count=0` → privacy/policy blocking, not an interop bug.
- `hr` negative → a real interop or API failure; the HRESULT names which call.

---

## 7. Next steps, in order

1. **Run it.** Restart (stop the debugger first — see §8) and capture the new
   `MFEnumDeviceSources returned hr=...` line. Everything below branches on it.
2. **Confirm the bandwidth fix.** At 30 fps the negotiated line should now read
   `640x480 ... 18.4 MB/s`. If that yields a genuine 30 fps, the model in §2 is
   confirmed and the remaining work is purely about regaining resolution.
3. **If camera privacy was the cause**, enable desktop-app camera access and
   re-run; the MJPEG passthrough path should engage and give 30 fps at full
   resolution. This needs a USER_MANUAL troubleshooting entry.
4. **If MF cannot be made to enumerate**, remaining options, roughly in order
   of effort:
   - DirectShow `ISampleGrabber` interop selecting the MJPEG pin directly —
     most faithful to what OBS does, but a substantial amount of COM interop.
   - An `ffmpeg` subprocess (`-f dshow -vcodec mjpeg`) piping MJPEG. Adds an
     external dependency YWC does not currently ship.
   - Accept the resolution/frame-rate trade-off from §5 and document it.
5. **Before committing**, decide whether the bandwidth ceiling belongs in
   Settings as an explicit choice ("prefer resolution" vs "prefer smoothness")
   rather than an invisible automatic downscale. The current behaviour silently
   changes resolution when the user changes frame rate, which is surprising.

---

## 8. Practical notes for whoever picks this up

**The debugger locks the output DLL.** A normal build fails with `MSB3027 /
MSB3021 ... locked by netcoredbg.exe`. That is a file lock, not a compile
error. Either stop the debugger, or build to a scratch directory:

```powershell
dotnet build Yaesu_Web_Control.csproj -f net10.0-windows -o "$env:TEMP\ywc-build-check" --no-restore
```

**Reading the log.** Diagnostics page → *Start fresh test log*, reproduce, then
*Download test log*. Search for `Radio Display negotiated`. Raw file lives at
`%APPDATA%\MM5AGM\Yaesu Web Control\logs\ywc-YYYYMMDD.log`.

**Serilog minimum level is Information.** `LogDebug` will not appear in the
file. An earlier iteration lost an entire failure path this way — the MF open
was failing silently at Debug level for two test cycles. Log diagnostics at
Information or Warning.

**Measure at the source.** `MeasuredFps` on the status endpoint is the
server-side encode rate, which is the number that matters. Browser-side
rendering rate can differ.

**Apartment state matters.** The capture thread is STA (DirectShow), the MF
session thread is MTA, and `WindowsDshowDevices` spawns its own STA thread when
called from a pool thread. Keep these straight when moving code between them.
