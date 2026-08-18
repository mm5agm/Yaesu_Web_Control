# A CW reader for YWC and IWC

**Status:** agreed in outline, 2026-08-18. Not started.
**Branches:** `feature/cw-reader` in all three repos (YWC, IWC, Radio_Web_Control_Core).

The reader decodes received CW to on-screen text in a pop-out window, keeps a
rolling transcript, and offers a pre-filled QSO save. Most of it lives in
`Radio_Web_Control_Core`, because a Morse decoder does not know what a radio is.

It also closes a gap in IWC: the zero-in that YWC gets free from the radio.

---

## 1. Four findings that set the design

These were checked against the manuals and the code before anything was
decided. They are the reason the plan looks the way it does.

### 1.1 Neither radio will give us the text

Both radios decode CW themselves, and both expose the *settings* over the wire:

| | |
|---|---|
| FTdx101 CAT | menu `01 CW DECODE BW`, `01 DECODE RX SELECT`, `02 DECODE AFC RANGE` |
| IC-7300 MkII CI-V | `02 49`-`02 51` under `KEYER/DECODE > CW DECODE > SET` |

Neither has a command that reads the decoded **characters** back. Icom's
USB (B) port can be switched to emit decode text (`1A 05 00 94`), but the
setting is `00=RTTY Decode, 01=CI-V` — RTTY only, not CW.

**So we write a real DSP decoder.** There is no passthrough shortcut, on either
brand. This was worth confirming: a passthrough would have been a fraction of
the work.

### 1.2 IWC's ZIN cannot mirror YWC's

YWC's ZIN is one line — `ZI{P1};` in `Controllers/CatController.cs`. The radio
nudges its own VFO. The FTdx101 CAT manual documents it at `ZI  ZERO IN`.

The IC-7300 MkII **has** the function — `CW Auto Tuning`, with a front-panel
`[AUTOTUNE]` key (Basic manual p. 39) — but CI-V only exposes
`1A 05 00 58 [AUTOTUNE] setting`, which is the *key assignment*, not a trigger.
There is no CI-V command to start it.

So IWC's zero-in has to be ours: measure the received tone, compare it against
the configured CW pitch, shift the VFO by the difference. Both radios report
their pitch, with different encodings:

| | read | Hz |
|---|---|---|
| Yaesu | `KP;` -> `KP P1 P1;`, P1 = 00-75 | `300 + 10 x P1`, exact |
| Icom | `1A 05` CW pitch, 0-255 | `300 + value x 600 / 255`, snapped to 5 Hz (0->300, 128->600, 255->900) |

**The tone estimator the decoder needs is the same one zero-in needs.** That is
why these two features belong in one change and one engine, and it is what makes
the ZIN work worth doing now rather than separately.

### 1.3 The capture device is currently owned by the Remote Audio session

In YWC (`develop`), `RadioAudioBridgeService.OpenDevices` runs when a browser
opens the Remote Audio WebSocket, and `AudioSessionManager` rejects a second
connect as busy. The PortAudio input stream therefore exists only while someone
is listening remotely.

A decoder that only works while a Remote Audio session happens to be up is not
the feature. **The capture side has to come out of the bridge** — see §3.2.

This touches Fabio's code. It is on `develop` rather than in flight, so the risk
is lower than it looks, but it should be raised with him before Phase 2 lands.

### 1.4 IWC has no audio subsystem at all

`Services/Audio/` does not exist in IWC. The whole PortAudio/Opus/WebSocket
stack is YWC-only. IWC needs a capture path built — capture only, no Opus and no
WebSocket, so it is a small fraction of what YWC has.

---

## 2. What goes where

Core's rule is *if it needs to know what a radio is, it does not go here*. A
Morse decoder passes that test cleanly. Two things do not, and stay in the apps:
opening an audio device (Core has no package references, and PortAudioSharp is a
package) and sending anything over CAT or CI-V.

```
                    +------------------ Radio_Web_Control_Core -------------+
                    |  ICwAudioSource  -->  CwDecoderEngine                 |
                    |                         |- CwToneDetector (pitch+env) |
                    |                         |- CwElementDecoder (timing)  |
                    |                         +- MorseTable                 |
                    |  CwZeroIn (pure: measured Hz + target Hz -> delta Hz) |
                    |  CwTranscriptWriter . AdifRecordWriter                |
                    |  js/cw/cw-reader-panel.js                             |
                    +-------------------------------------------------------+
                            ^                                   |
             float frames   |                                   | text, wpm, tone
                            |                                   v
  +------------ YWC --------+------+          +------------- IWC --------------+
  | RadioAudioCaptureService       |          | RadioAudioCaptureService       |
  |   (extracted from the bridge)  |          |   (new, capture only)          |
  | CwReaderService  -> SignalR    |          | CwReaderService  -> SignalR    |
  | CwController                   |          | CwController                   |
  | ZIN = ZI{P1};  (radio does it) |          | ZIN = CwZeroIn + set frequency |
  | Pages/CwReader.cshtml          |          | Pages/CwReader.cshtml          |
  +--------------------------------+          +--------------------------------+
```

Core stays `net10.0` with no package references, so YWC's CAT-only macOS/Linux
target keeps building.

---

## 3. The pieces

### 3.1 The decoder (Core, pure, unit-tested)

Input is 48 kHz mono float frames — the format the existing capture already
produces (`AudioConstants.FrameSamples` = 480 = 10 ms). Decimate to 8 kHz
internally.

Two paths run at different rates, because pitch and keying change at very
different speeds:

**Envelope path — 10 ms hop.** A single Goertzel at the currently tracked tone
frequency gives a mark/space envelope. At 40 WPM a dit is 30 ms, so 10 ms
resolution is the floor; this is why a plain long FFT is not enough on its own.
An adaptive noise floor plus Schmitt-trigger hysteresis turns the envelope into
a clean key-down/key-up stream.

**Pitch path — ~4 Hz.** A 1024-point FFT at 8 kHz (7.8 Hz bins, 128 ms) tracks
where the tone actually is, within a search window around the configured CW
pitch. Slow is fine — pitch drifts, it does not key. This path feeds both the
Goertzel above *and* zero-in.

**Timing.** Mark durations are clustered adaptively into dit/dah rather than
compared against a fixed WPM: an exponential moving average of the short-mark
length sets the dit, dah is anything over ~2 dits, and WPM falls out as
`1200 / dit_ms`. Gaps of 1/3/7 dits split elements, characters and words, with
tolerance windows either side. This is what lets it follow a bad fist, which is
most of the value.

**Output** is a stream of characters with timestamps, plus current WPM, tone Hz,
and an SNR estimate. `MorseTable` covers letters, digits, punctuation and the
common prosigns (AR BT SK KN).

**Tests matter more here than anywhere else in either app.** CW decoders are
usually mediocre and it is hard to tell how mediocre by ear. The Core test
project generates synthetic Morse — known text, set WPM, set SNR, optional QSB
and a deliberately uneven fist — and asserts character accuracy. That gives a
number to regress against rather than an impression. Core already has xUnit
under `tests/RadioWebControl.Core.Tests`.

### 3.2 Capture extraction (YWC)

Pull the PortAudio **input** side out of `RadioAudioBridgeService` into
`RadioAudioCaptureService`:

- reference-counted start/stop — it opens the device when the first consumer
  asks and closes it when the last one leaves;
- a multicast frame event, fired at the existing 480-sample accumulator
  boundary (`RadioAudioBridgeService` around line 543, which is already exactly
  the right seam);
- the bridge becomes one subscriber. Its TX/playback side is not touched.

Result: the Remote Audio session and the CW reader can run together, or either
alone, and the reader works with no browser attached — which is what makes it
work on the Pi and in Docker, where `/dev/snd` and `YWC_AUDIO_GID` are already
mapped for Remote Audio.

IWC gets the same class with only the capture half, and picks up the
PortAudioSharp package reference.

### 3.3 Reader Mode

Decode accuracy depends far more on what the operator feeds the decoder than on
the DSP. A 2.4 kHz-wide SSB filter full of adjacent signals will defeat any
decoder; 250 Hz with APF on will make a mediocre one look good.

So a single **Reader Mode** button sets, over CAT/CI-V: CW mode, narrow filter
(default 250 Hz, configurable), APF on, then zero-in. One click, and it restores
the previous settings when the reader closes.

This is per-radio code and stays in the apps.

### 3.4 Zero-in

`CwZeroIn` in Core is pure: measured tone Hz and target pitch Hz in, delta Hz
out, with a deadband so it does not chase noise and a sanity clamp so a bad
estimate cannot throw the VFO across the band.

- **YWC keeps sending `ZI{P1};`.** The radio's own firmware is faster and
  carries no risk, and it works whether or not the reader is running.
- **IWC applies the delta** by setting the VFO frequency it already controls.

**Bench-verify the sign** on the real rig before trusting it. Whether a tone
above the target pitch means tune up or tune down depends on CW-U vs CW-L, and
getting it backwards produces a zero-in that runs away instead of converging.
This is the one part of the plan that cannot be settled from the manuals.

### 3.5 The pop-out

`Pages/CwReader.cshtml`, opened with `window.open` exactly like
`Pages/RemoteAudio.cshtml` and `Pages/RadioDisplay.cshtml` already are.

- scrolling decoded text, newest at the bottom;
- WPM / tone Hz / SNR readout, and a signal-present indicator;
- threshold and search-window controls for difficult conditions;
- Reader Mode toggle, ZIN button, clear button;
- **font-size control** — Thomas OZ1JTE has asked for larger text before, and
  this window is nothing but text;
- **optional ARIA-live announcement** of new text. Worth doing for Yuri, Thomas
  and Bill, but debounce it properly — v2.3.7 already taught us what
  undebounced live regions do to a screen reader.

Transport is the existing envelope: `RadioStateUpdate` with
`property = "CwDecode"`, `value = { text, wpm, toneHz, snr, signal }`, plus a
`CwReaderStatus` for lifecycle. Nothing new on the wire.

### 3.6 Transcript and QSO save

**Rolling transcript**, always on when the reader is running: a timestamped
text file per session in the app data folder, alongside `radio_state.json`.
Nothing decoded is lost if the operator does not press save.

**QSO save**: a small form, pre-filled from the decoded text (callsign and RST
picked out where they can be) and from live radio state (frequency, mode, time),
which the operator corrects and confirms. It appends to a local ADIF file. Core
already has `AdifParser`; this adds `AdifRecordWriter` next to it.

Deliberately **not** pushing straight to Log4OM / N1MM / N3FJP: that decision is
still waiting on Steve K3FZT, and it is not needed here. Log4OM and GridTracker
already watch ADIF files, so writing ADIF reaches those users anyway.

---

## 4. Phases

Each phase is independently verifiable, and nothing before Phase 4 touches IWC.

| | | |
|---|---|---|
| **0** | branches + this document | done |
| **1** | **Core:** decoder engine, `CwZeroIn`, Morse table, synthetic-audio test suite. No app wiring at all. | `dotnet test` gives an accuracy number vs WPM and SNR |
| **2** | **YWC:** capture extraction, `CwReaderService`, SignalR, pop-out page | real CW off the FTdx101MP, on the bench |
| **3** | **YWC:** Reader Mode, transcript, ADIF QSO save | as above |
| **4** | **Core -> IWC:** subtree push, IWC capture service, reader UI, software ZIN | real CW off the IC-7300 MkII |
| **5** | `USER_MANUAL.md` section, README notes | — |

Phase 1 is the one that decides whether this is worth shipping. If the accuracy
numbers are poor at realistic SNR, that is known before any UI exists.

**No version bump, no release notes, no `finish-release.ps1` without Colin's
explicit go.**

## 5. Risks

| risk | handling |
|---|---|
| Capture refactor touches Fabio's audio code | It is on `develop`, not in flight. Raise before Phase 2 lands; his TX/playback path is untouched. |
| Decoder accuracy disappoints | Phase 1 measures it before any UI is built. Reader Mode does most of the practical work. Manual sets honest expectations. |
| Zero-in sign convention wrong | Bench-verify on both rigs. Deadband and clamp mean a wrong sign fails visibly and harmlessly rather than running away. |
| Core picks up a package dependency | It cannot: the decoder takes samples, the apps open devices. Keeps YWC's `net10.0` CAT-only target building. |
| Bug reports interrupt | That is what the separate branches are for. `develop` stays releasable throughout. |
