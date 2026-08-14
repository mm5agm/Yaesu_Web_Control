# macOS Radio Display — investigation notes

Resume context for continuing the black-stream / capture work on macOS.
Last updated: 2026-08-13 (FaceTime ↔ USB switch crash).

## Symptom progression

1. **Empty device list** when running via bare `make run` / `dotnet run`.
2. After wrapping as `.app` + Camera TCC: devices listed, but **Could not open capture device index N**.
3. After `brew install libavif` + Camera permission prompt: **opens**, badge **Streaming** (green), FaceTime **LED on**, but **MJPEG stays black**, **FPS/size stay 0**.
4. After UI-thread Read + safer OpenCV encode: still **process crash** on first JPEG (and again after **switching capture device**).

## Hardware / environment

- Intel Mac (`uname -m` = `x86_64`, Homebrew under `/usr/local`).
- Devices seen: **FaceTime HD Camera (Built-in)**, **USB Video** (Macrosilicon HDMI capture, USB VID `0x534d`).
- Indexes **swap** between runs (sometimes FaceTime=0, sometimes USB Video=0). Do not hard-code.
- App launched via `scripts/macos/run-dev.sh` → wraps `bin/$CONFIG/net10.0` as `Yaesu Web Control.app` with `NSCameraUsageDescription` / `NSMicrophoneUsageDescription`.
- User settings/logs: `~/Library/Application Support/MM5AGM/Yaesu Web Control/` (also referenced as XDG-style under some docs).

## Root causes found

### 1. No Camera TCC without Info.plist

Bare `dotnet run` has no `NSCameraUsageDescription`. macOS never prompts; OpenCV/`system_profiler SPCameraDataType` see no cameras.

**Mitigation:** `scripts/macos/{info-plist.sh,wrap-dev-app.sh,run-dev.sh}`; `make run` on Darwin calls `run-dev.sh`; DMG `build-dmg.sh` sources shared plist (includes Camera key).

### 2. Intel OpenCvSharp needs Homebrew libavif

`libOpenCvSharpExtern.dylib` (osx-x64) hard-links:

`/usr/local/opt/libavif/lib/libavif.16.dylib`

Without it: `DllNotFoundException` / open failures (exceptions were swallowed → generic “Could not open”).

**osx-arm64** dylib has **no** Homebrew deps (Apple Silicon path is cleaner).

**Mitigation:** `brew install libavif`; run-dev warns if missing on x86_64; open failures now surface libavif text; USER_MANUAL §19.5 row added.

### 3. Black stream with LED on / frameSeq=0

Live API while “streaming”:

```json
{"status":"streaming","width":0,"height":0,"fps":0,"frameSeq":0,"viewers":2+}
```

`/api/video/stream` timed out with **0 bytes**. Log showed:

- `Radio Display streaming device index N …`
- `Radio Display negotiated 1920x1080` or `1280x720 ???? device=…fps` ← **at least one Read succeeded**
- Then often: `capture loop did not stop within 8000 ms; orphaning…`

Interpretation: camera opens and gets an initial frame (LED on, format logged), then **further `Read`s stall or never publish JPEGs**, so MJPEG clients wait forever → black UI. FPS is only updated inside the `ViewerCount > 0` encode path, so it stays 0.

OpenCV AVFoundation (`cap_avfoundation_mac.mm`) uses GCD + `NSCondition` for frames; comments still assume main-thread usage historically. Opening on UI thread then reading on the worker thread correlated with “one frame then stall”.

### 4. Crash on JPEG encode (current — 2026-08-13 23:23 + 23:26)

**Not** a permission crash. Camera already granted.

IPS: `EXC_BAD_ACCESS` / `SIGSEGV` on thread **`YWC-RadioDisplay`**

- `libOpenCvSharpExtern.dylib` → `cv::error_exit(jpeg_common_struct*)` → `_longjmp`
- OpenCvSharp embeds libjpeg statically; on error it `longjmp`s. If that escapes / jmp_buf is bad → **process death**. Managed `try/catch` around `ImEncode` **cannot** catch this.

Timeline (latest, PID 37424, device switch):

1. `23:26:29` — `capture loop did not stop within 8000 ms; orphaning…`
2. `23:26:30` — streaming index 0, negotiated `1920x1080`
3. `23:26:30.9` — IPS crash — **no** `first JPEG published` line
4. Crash dump had **two** `YWC-RadioDisplay` threads (orphaned + new)

Earlier crash at `23:23` had a **single** RadioDisplay thread → encode crash is not *only* an orphan race; OpenCV `ImEncode` dies on the first frame after negotiate.

UI-thread `Release` while `Read` is blocked on the UI thread **deadlocks** (Release queues behind Read) → 8s timeout → orphan → second loop races encode.

### 5. FaceTime ↔ USB switch crash (2026-08-13 23:37, after Skia fix)

Streaming USB worked (`first JPEG published`). Switching to FaceTime then back to USB crashed.

IPS: `EXC_BAD_ACCESS` on **`com.apple.main-thread`** (not encode):

- `VideoCapture::read` → `CvCaptureCAM::grabFrame` → `[CaptureDelegate grabImageUntilDate:]` → `objc_msgSend` at 0x0

Log: loop self-opened FaceTime (`streaming index 1` / negotiated) then `RequestRestart` → `capture stopped` ~60 ms later. `ForceUnblockAndRelease` from the HTTP thread freed the AVFoundation delegate while the UI thread was still in `Read`.

**Mitigation:** macOS `RequestRestart` no longer hard-stops a running loop (the loop applies the new index). Session teardown uses UI-thread `Release` after Read returns. `StopLoopAndWait` waits for the loop first; force-release only on timeout. Open/Read/Release share `OpLock`.

Misleading open text blaming Camera permission was already softened.

## Code changes already made (this branch / working tree)

| Area | Change |
|------|--------|
| `Program.cs` | `OPENCV_AVFOUNDATION_SKIP_AUTH=1` on macOS (avoid OpenCV trying to spin main run loop for TCC from a worker thread). |
| `scripts/macos/*` | Dev `.app` wrap + Camera/Mic usage strings; `make run` → `run-dev.sh` on Darwin. |
| `build-dmg.sh` | Shared `info-plist.sh` (Camera + Microphone). |
| `VideoDeviceEnumerator` | macOS: list from ffmpeg/system_profiler names; **do not probe-open** (hangs / races capture). |
| `VideoCaptureService` | Richer open errors; macOS prefer native open; `"N:none"` path; epoch; macOS JPEG via Skia; macOS `RequestRestart` does not hard-stop; settle 1500 ms between sessions. |
| `MacAvFoundationCapture.cs` | Open + Read + Release on UI thread with `OpLock`. Force-release only after lock wait (last resort). |
| `MacJpegEncoder.cs` | **NEW** — SkiaSharp JPEG encode (Avalonia already ships Skia). Avoid OpenCV `ImEncode` on macOS. |
| `Yaesu_Web_Control.csproj` | Exclude Mac helpers from `net10.0-windows`. |
| `USER_MANUAL.md` §19.5 | Empty list / libavif / black-after-Camera-grant rows. |
| `VideoController` | macOS notes for empty list + libavif hint. |

## Verify next run

1. Exit menu-bar app fully, then `bash scripts/macos/run-dev.sh`.
2. Open Radio Display; expect log: `Radio Display first JPEG published (…)`.
3. Switch capture device; should **not** produce a new `*.ips`, and should not log orphaning (or if it does, orphan must exit via epoch without crashing).
4. Confirm UI FPS/size leave 0 and `/api/video/stream` delivers multipart JPEGs.

## Agent / tooling pitfalls

- Cursor agent shells often hung on `dotnet restore` / `dotnet build` (NuGet); user’s IDE builds worked.
- Sandbox blocked TCC DB, some `sysctl`, Avalonia build telemetry path, and brew network at times.
- ffmpeg was **not** installed on the machine when checked; naming fell back to `system_profiler`.

## Next steps if still broken after Skia encode

1. If still crash in OpenCvSharp (not Skia): crash is elsewhere (Resize/CvtColor) — log Mat type/channels before encode; consider raw ImageIO path.
2. If Read still stalls: run **entire** capture cadence on a `DispatcherTimer` on the UI thread (no worker-thread loop), or P/Invoke a CFRunLoop on the capture thread.
3. If JPEGs publish but UI black: inspect `/api/video/stream` multipart / `<img>` reconnect in `radio-display-ui.js`.
4. FaceTime works but USB Video black with FPS > 0 → no HDMI signal into Macrosilicon (expected).
5. Long-term Intel packaging: avoid Homebrew-linked `OpenCvSharp4.runtime.osx.x64` (bundle deps or document `brew install libavif` in release notes).

## Key files

- `Services/Video/VideoCaptureService.cs`
- `Services/Video/MacAvFoundationCapture.cs`
- `Services/Video/MacJpegEncoder.cs`
- `Services/Video/VideoDeviceEnumerator.cs`
- `Controllers/VideoController.cs`
- `Program.cs` (SKIP_AUTH)
- `scripts/macos/run-dev.sh`, `wrap-dev-app.sh`, `info-plist.sh`
- `wwwroot/js/video/radio-display-ui.js` (MJPEG `<img>` client)
