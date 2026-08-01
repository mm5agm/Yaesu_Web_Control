# Voice device-selection port → YWC

> **Origin:** ported from Icom Web Control (IWC), where this was built and
> hardware-verified on 2026-07-29/30. IWC was cloned from YWC, so YWC still has
> the *old* voice-input behaviour this plan replaces. Source files to copy from
> live in the IWC repo at `C:\Users\colin\source\repos\Icom_Web_Control`.

## Why

YWC's voice recogniser can only listen on the **Windows default** audio device —
it hard-calls `SetInputToDefaultAudioDevice()`. On a real station the default is
often something else (e.g. WSJT-X audio), so voice control effectively listens on
the wrong device. IWC added **name-based microphone selection** (listen on a
chosen mic, feeding the recogniser a captured stream) plus an **announcement-
speaker picker** (play spoken confirmations on a chosen output device). This plan
brings both into YWC, including the trailing-space device-name match fix.

## Prerequisite

- YWC references **no** NAudio today. Add a `<PackageReference>` to
  **`NAudio.WinMM` 2.2.1** in the main `.csproj` (this is the only NAudio package
  IWC references; `NAudio.Core` comes in transitively).
  - Note: this build of `NAudio.WinMM` has **no** `WaveOut` class and
    `WaveOutEvent` has no static enumeration — output devices are enumerated via
    the low-level `WaveInterop.waveOutGetNumDevs()` / `waveOutGetDevCaps()`
    P/Invoke (namespace `NAudio.Wave`; `MmResult` in namespace `NAudio`). This is
    already handled in `AudioOutput.cs` below.

## Copy wholesale from IWC

Change only the namespace to YWC's `…Services.Voice`; the trailing-space
`Normalize()`/`TrimEnd()` match fix is already baked into both.

- `Services/Voice/MicrophoneCapture.cs` — WaveIn device enumeration + capture
  stream + `FindDeviceIndex`.
- `Services/Voice/AudioOutput.cs` — WaveOut device enumeration for the speaker
  picker.

## Port into existing YWC files

- **`Services/Voice/VoiceControlService.cs`**
  - Add `private volatile string? _configuredMicName;` set from
    `settings.VoiceInputDeviceName` at startup.
  - Replace the input path: when a device name is configured **and found**, open
    a `MicrophoneCapture` WaveIn stream and call
    `SetInputToAudioStream(stream, MicFormat())`; otherwise fall back to
    `SetInputToDefaultAudioDevice()`. In IWC this is the `EnsureAudioInput()`
    helper.
  - YWC currently hard-calls `SetInputToDefaultAudioDevice()` at **lines 167,
    288, 369** — every input point needs the same chosen-device-or-default logic.
  - Add `ApplyInputDevice(string?)`, `ApplyOutputDevice(string?)` (delegates to
    `VoiceTtsService`), and `TestSpeak(string)`.
- **`Services/Voice/VoiceTtsService.cs`**
  - Add device selection: resolve the saved name via `AudioOutput.FindDeviceIndex`;
    when set, render the phrase to a WAV `MemoryStream` (`SetOutputToWaveStream`)
    and play it on the chosen device via `WaveOutEvent { DeviceNumber = index }`
    + `WaveFileReader`; when unset, keep the existing default-device path. SAPI
    cannot target an output device by name, hence the render-then-play approach.
    Add `ApplyOutputDevice(string?)`. On playback failure, fall back to default.
- **`Controllers/VoiceController.cs`** — add endpoints:
  `GET microphones`, `POST microphone`, `GET speakers`, `POST speaker`,
  `POST speaker-test` (calls `TestSpeak`).
- **`Models/ApplicationSettings.cs`** — add `VoiceInputDeviceName` and
  `VoiceOutputDeviceName` (both `string = ""`).
- **`Pages/Settings.cshtml`** — add the microphone + announcement-speaker
  `<select>` pickers, a **Test** button for the speaker, and the JS that loads
  `/api/voice/microphones` + `/api/voice/speakers` and POSTs the chosen names.
  These are JS-driven (not `asp-for` model-bound), so **no** `ModelState.Remove`
  gymnastics are needed.

## ⚠ Watch-out — second recogniser / exclusive device access

YWC's `VoiceControlService` runs a **second, background "quick-listen"
recogniser** (~lines 248-288) that also sets its own input via
`SetInputToNull()` + `SetInputToDefaultAudioDevice()`. A captured WaveIn stream
is **exclusive-access** — only one engine can hold the device at a time. Decide
whether that second engine shares the chosen device or stays on default; getting
this wrong will lock the microphone. **IWC already resolved this** — mirror
whatever IWC's `VoiceControlService` does at the equivalent spot.

## The device-name match fix (why it matters)

MME product names come from a fixed 32-char buffer, so long names arrive
**truncated at 31 chars and can end on a space** (e.g. a Philips monitor's
`1 - PHL 40B1U5600 (2- AMD High `). That trailing space survives enumeration but
is stripped somewhere in the browser → JSON → settings round-trip, so a later
**exact** match fails and playback/capture silently falls back to the default
device. `MicrophoneCapture` and `AudioOutput` both `TrimEnd()` on **both** sides
when matching. This is included in the copied files — don't drop it.

## Verify

1. `dotnet build <YWC>.csproj -t:Compile -clp:ErrorsOnly` (compile-only while the
   app is running avoids the exe-lock).
2. Settings → pick a specific microphone → confirm voice control listens on it
   (not the Windows default).
3. Pick an announcement speaker → **Test** → confirm the phrase plays on that
   device while the Windows default stays elsewhere.
4. Confirm a long/truncated device name (like the Philips) round-trips: pick it,
   restart, and confirm it still resolves (the trailing-space fix).

## Release (YWC is the shipping product)

After it works, follow YWC's release steps: bump `Models/AppVersion.cs`,
`installer.nsi`, add release notes + the download badge to `README.md`, update
`USER_MANUAL.md` (voice section), then the merge/tag/`gh release create` flow.
