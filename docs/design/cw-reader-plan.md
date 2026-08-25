# A CW reader for YWC and IWC

**Status:** revisited 2026-08-23 after an afternoon on the bench with both
radios. Confirmed wanted on **both**. Still not started — Phase 0 only.
**Branches:** `feature/cw-reader` in all three repos (YWC, IWC, Radio_Web_Control_Core).

The reader decodes received CW to on-screen text in a pop-out window, keeps a
rolling transcript, and offers a pre-filled QSO save. Most of it lives in
`Radio_Web_Control_Core`, because a Morse decoder does not know what a radio is.

It also closes a gap in IWC: the zero-in that YWC gets free from the radio.

On 2026-08-18 the case for this was an assumption — that a radio's built-in
decoder is mediocre. §1.5 replaces that with what I measured on 2026-08-23, and
it is a stronger case than I expected.

---

## 1. Five findings that set the design

These were checked against the manuals and the code before anything was
decided. They are the reason the plan looks the way it does.

### 1.1 Neither radio will give us the text

Both radios decode CW themselves, and both expose the *settings* over the wire:

| | |
|---|---|
| FTdx101 CAT | `EX020301` CW DECODE BW (0:25 1:50 2:100 3:250 Hz), `DECODE RX SELECT` (MAIN/SUB) |
| IC-7300 MkII CI-V | `02 49`-`02 51` under `KEYER/DECODE > CW DECODE > SET` |

Neither has a command that reads the decoded **characters** back. Icom's
USB (B) port can be switched to emit decode text (`1A 05 00 94`), but the
setting is `00=RTTY Decode, 01=CI-V` — RTTY only, not CW.

The MkII's `[LAN]` port does not change this, which I checked on 2026-08-23
because it looked like it might. LAN is a *transport* for the same CI-V command
set, not an extension of it: `SET > Connectors > USB/LAN REMOTE Transceive
Address` sets "the address used to remotely control the transceiver using the
optional RS-BA1 software, through the [USB] port or the [LAN] port". Everything
above applies to LAN unchanged.

Nor does the radio write CW to a file. RTTY does — `02 44` Decode Log on/off,
`02 45` Text/HTML, `02 46`-`02 48` timestamps, straight to the SD card. The
`CW DECODE SET` block has no equivalent; it is colour settings from `02 49` on.
The only CW-decode commands anywhere are `02 26` Decode Display on/off and
`02 27` Japanese Morse Decode on/off, and both read a *setting*, never a
character.

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

There are two candidate sources, and the default is the boring one:

| Source | What it gives | Cost |
|---|---|---|
| **USB CODEC** (default) | Filtered receive audio off the USB sound device, exactly what the decoder wants | The known path; mirrors what YWC already does |
| **LAN AF** | The same audio over Ethernet, no sound card in the chain, and it works *remotely* | Icom do not document the wire protocol |

`SET > Connectors > LAN AF/IF Output > Output Select` (default AF) chooses what
leaves the `[LAN]` port: "AF: An AF signal is output. IF: A 12 kHz IF signal is
output", with an `AF SQL` option to gate it on squelch. The 12 kHz IF is more
interesting for a panadapter than for CW — the decoder wants the filtered AF.

The LAN protocol is RS-BA1's, and Icom publish nothing about it. It *has* been
reverse-engineered publicly — wfview implements Icom LAN control and audio — so
it is not a closed door, but I have not costed it and will not pretend it is
cheap. **Phase 2 uses the USB CODEC.** LAN is a later upgrade whose real prize
is remote operation, not decoding.

### 1.5 The two radios' own decoders, measured

Bench session 2026-08-23, one QSO, both radios on the same signal.

**The FTdx101's decoder takes a hand-set speed.** Operating Manual p.59, step 2
of the CW Decode procedure: *"Turn the [MIC/SPEED] knob to closely match the
speed of the received CW signal. If the speed is significantly different, it may
not be deciphered correctly."* Its reference is the **transmit keyer speed**,
4-60 WPM. It does not track the sender. Yaesu give no tolerance figure.

It has two further manual controls:

- **CW DECODE BW** — the AFC capture window, how far off the operator's CW pitch
  it will still chase a tone. 25 / 50 / 100 / 250 Hz, default 100. Reachable
  over CAT as `EX020301n;`.
- **DEC LVL** — a fixed audio threshold, adjusted by touching `[DEC LVL]` on the
  decode screen and turning `[MULTI]`. **There is no CAT command for it** —
  checked against the whole of Table 2 and the two-letter command set. Set too
  high, a weak signal decodes as nothing; too low, noise generates junk.

**The IC-7300 MkII's decoder has no settings at all.** Its CW DECODE SET screen
(Advanced manual p.2-15) contains four items and all four are colours: FFT
waveform, signal level, receive font, transmit font. No threshold, no speed, no
bandwidth. The only functional option nearby is Japanese Morse Decode ON/OFF.

**The decisive observation.** In one QSO the two operators were sending at
noticeably different speeds. The MkII decoded both reasonably well, untouched.
The '101 was set to 27 WPM, matching the fast operator only — and **no value of
that knob copies both**, because the knob is one number and there are two
senders. Mixed-speed QSOs are ordinary. So the '101's decoder is not merely
fiddly to set up; it is structurally unsuited to following a QSO, which is the
whole point of a decoder.

**What this changes:**

1. Adaptive speed tracking and adaptive thresholding are not refinements, they
   are the feature. Icom already ship both in a radio costing a fraction of the
   '101, so this is engineering, not research.
2. Being CAT-connected is an advantage the radios' own decoders do not exploit:
   we can read the operator's CW pitch (`KP;` / `1A 05`) and filter width (`SH`)
   and configure the tone detector from the rig's own state. See §3.3.
3. **The bar differs per app.** Against the '101 we are beating a weak decoder.
   Against the MkII we are matching one that already works with nothing to
   configure. Phase 1 has to clear the second bar, not the first.
4. **Colin owns a reference decoder.** Two radios, one antenna, one signal — so
   Phase 1 can be scored against real off-air CW through the MkII as well as
   against synthetic audio. Very few decoder projects have that.

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

### 2.1 Where the files actually go

Core is vendored into both apps as a **`git subtree` at `core/`** — not a
submodule and not a NuGet package, so a plain `git clone` of either app still
builds with no extra steps. The workflow is already written down in
`core/README.md` (add / pull / push commands) and is not repeated here. Two
things from it that bite:

- work happens **inside `core/` in whichever app repo you are in**, and is
  pushed up with `git subtree push --prefix=core ... main`. Then the *other* app
  pulls it down. Do not edit the standalone `Radio_Web_Control_Core` clone and
  expect either app to see it;
- each app removes `core\**` from `Compile`, `Content`, `EmbeddedResource` and
  `None` alongside the `ProjectReference`, because both use the Web SDK and its
  globs would otherwise pull every Core file in twice. Verified present in both
  `Yaesu_Web_Control.csproj` and IWC's — it only matters if a new project ever
  picks Core up.

New files, all under `core/`:

```
core/Services/Cw/CwDecoderEngine.cs      decoder, owns the two paths below
core/Services/Cw/CwToneDetector.cs       Goertzel envelope + FFT pitch track
core/Services/Cw/CwElementDecoder.cs     adaptive dit/dah timing -> characters
core/Services/Cw/MorseTable.cs           letters, digits, punctuation, prosigns
core/Services/Cw/CwZeroIn.cs             pure: measured Hz + target Hz -> delta
core/Services/Cw/ICwAudioSource.cs       the seam the apps implement
core/Services/CwTranscriptWriter.cs      rolling timestamped transcript
core/Services/AdifRecordWriter.cs        next to the existing AdifParser.cs
core/js/cw/cw-reader-panel.js            shared reader UI (see note below)
tests/RadioWebControl.Core.Tests/Cw/     synthetic-audio suite (§3.1)
```

`core/Services/` currently holds one file (`AdifParser.cs`), so `Services/Cw/`
is a new directory rather than a change to an established layout.

**Shared JS needs no csproj change.** Both apps have a `CopySharedCoreJs` target
that globs `core\js\**\*.js` and copies it into `wwwroot\js\` before
`AssignTargetPaths`, preserving the subdirectory. So `core/js/cw/cw-reader-panel.js`
arrives at `wwwroot/js/cw/cw-reader-panel.js` automatically, in both apps, and
the copies are git-ignored so `core/js` stays the single source of truth. Author
it in `core/js` and nowhere else.

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
tolerance windows either side. This is what lets it follow a bad fist, and it is
what lets it follow the *other* operator when the over changes hands.

**Re-acquisition is a hard requirement, not a nice-to-have.** §1.5 showed a real
QSO with the two operators at very different speeds. The tracker must converge
on a new speed within the first few characters of an over — a decoder that
settles over thirty seconds would pass a steady-state test and still be useless
on the air. The EMA time constant is the knob that decides this, and it trades
against stability on a ragged fist; Phase 1 measures the trade rather than
guessing it.

**Output** is a stream of characters with timestamps, plus current WPM, tone Hz,
and an SNR estimate. `MorseTable` covers letters, digits, punctuation and the
common prosigns (AR BT SK KN).

**Tests matter more here than anywhere else in either app.** CW decoders are
usually mediocre and it is hard to tell how mediocre by ear. The Core test
project generates synthetic Morse — known text, set WPM, set SNR, optional QSB
and a deliberately uneven fist — and asserts character accuracy. That gives a
number to regress against rather than an impression. Core already has xUnit
under `tests/RadioWebControl.Core.Tests`.

Three cases carry more weight than overall accuracy:

- **Mixed-speed QSO** — audio alternating between two speeds (say 27 and 16 WPM)
  at over boundaries, scored on **characters lost per transition**. This is the
  case the '101 cannot do at all, so it is the one that justifies the feature.
- **Ragged fist** — deliberately uneven weighting and spacing, of the kind a bug
  or a straight key produces.
- **QSB** — amplitude fading, which is what an adaptive threshold buys over the
  '101's fixed `DEC LVL`.

**Exit criterion for Phase 1:** accuracy comparable to the IC-7300 MkII on the
same off-air signal, not merely better than the '101. If it cannot reach that,
say so and stop — the finding is worth having, and no UI exists yet to unpick.

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

**It also reads back, and configures the decoder from the rig.** CW pitch
(`KP;` on Yaesu, `1A 05` on Icom) sets the tone detector's search centre;
filter width (`SH`) sets how wide it may search. The operator sets nothing.
This is the advantage in §1.5 point 2, and it is the direct answer to the '101's
three manual controls:

| FTdx101 control | our equivalent |
|---|---|
| MIC/SPEED must match the sender | adaptive dit tracking — nothing to set |
| `DEC LVL` audio threshold | adaptive noise floor + hysteresis — **removed**, not reimplemented |
| `CW DECODE BW` capture window | read `KP;` and `SH`, size the search window from them |

The measured case for this is §1.5: the '101's decoder failed on a signal the
operator could copy by ear, with the filters at 3 kHz, and stayed poor after
they were narrowed to 600 Hz.

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
| **1** | **Core:** decoder engine, `CwZeroIn`, Morse table, synthetic-audio test suite. No app wiring at all. | done 2026-08-23 - 69 tests, numbers in §4.1 |
| **2** | **YWC:** capture extraction, `CwReaderService`, SignalR, pop-out page | real CW off the FTdx101MP, on the bench |
| **3** | **YWC:** Reader Mode, transcript, ADIF QSO save | as above |
| **4** | **Core -> IWC:** `git subtree push` from YWC then pull into IWC (§2.1), IWC capture service, reader UI, software ZIN | real CW off the IC-7300 MkII |
| **5** | `USER_MANUAL.md` section, README notes | — |

Phase 1 is the one that decides whether this is worth shipping. If the accuracy
numbers are poor at realistic SNR, that is known before any UI exists.

**Phase 1 needs no radio, no audio capture and nothing from anyone else** — it is
pure DSP against generated audio. That is why it goes first regardless of how the
rest is scheduled.

**Bench validation, once Phase 1 has numbers:** run the same off-air signal
through the MkII and through our decoder and compare. Synthetic audio proves the
algorithm; the MkII proves it against a decoder that demonstrably works on real
signals. Do both before committing to Phase 2.

**No version bump, no release notes, no `finish-release.ps1` without Colin's
explicit go.**

### 4.1 Phase 1 results, measured 2026-08-23

69 tests, all passing, in `core/tests/RadioWebControl.Core.Tests/Cw/`. Every case
prints its score as well as asserting on it, so `dotnet test -l "console;verbosity=detailed"`
is a report on the decoder rather than a pass/fail. Text is 24-56 characters of
ordinary QSO traffic; SNR is quoted in a 500 Hz bandwidth, which is what an
operator reads off a scope.

| case | result |
|---|---|
| 12 / 20 / 27 / 35 WPM at 20 dB | **100%** at all four |
| 30 / 20 / 12 dB SNR at 20 WPM | **100%** |
| 6 dB SNR at 20 WPM | **91.7%** |
| **27 -> 16 WPM at the over** | **100%, 0 characters lost to the change** |
| **14 -> 30 WPM at the over** | **96.3%** - 3 characters lost re-acquiring |
| ragged fist, 20% timing jitter | **100%** |
| QSB 10 dB at 0.05 / 0.10 / 0.25 Hz | **100%** at all three |
| QSB 20 dB at 0.10 Hz, 20 dB signal | 62.5% |
| QSB 20 dB at 0.10 Hz, strong signal | **100%** |
| five seconds of pure noise | **0 characters** |
| 22 WPM sent | reported 21.6 WPM |
| tone at 715 Hz, pitch set to 600 | tracked 708.6 Hz, zero-in offered +109 Hz |

**The mixed-speed case passes with nothing lost.** That was the case that
justified the feature (§1.5) and the one the FTdx101 cannot do at all. Going the
other way - a jump from 14 to 30 WPM, more than double - costs three characters
while the tracker re-acquires, which is inside the "first few characters"
requirement but is the honest worst case and is now a standing test.

**The 20 dB QSB row needs reading carefully.** A 20 dB fade takes a 20 dB signal
to nothing at the bottom of the cycle, so 62.5% is the band, not the decoder. The
row underneath is the control: the same 20 dB fade on a strong signal decodes
100%, which says the threshold tracker follows the fade and the losses above are
lost to noise. That control is a permanent test, because it is the one that would
catch the tracker regressing into something DEC LVL-shaped.

**Where the numbers came from.** Two findings worth keeping:

- **A floor-referenced presence gate does not work.** Narrowband noise is
  Rayleigh, so its envelope peak sits several times its own minimum; gating on
  peak-over-minimum called five seconds of hiss a signal and produced 29
  characters of nonsense. Gating on peak-over-*mean* produces zero. 
- **The threshold has to be referenced to a mark level, not to a peak.** The peak
  tracker is deliberately slow, which is right for deciding whether a signal is
  there and wrong for setting a threshold: during a fade a slow peak leaves the
  threshold stranded above the signal, which is exactly the failure mode a fixed
  DEC LVL has. Tracking a separate mark level, updated only while the key is
  down, took 10 dB QSB at 0.25 Hz from 54% to 100%.

### 4.2 Bench rig, built 2026-08-25

The bench comparison needs the app wiring that Phase 2 has not built yet, so the
harness stands in for it. `tools/CwBench` runs the Core decoder over a `.wav`
and prints a transcript, telemetry and a summary; `scripts/cw-bench-record.ps1`
records from a capture device and hands the file straight to it, so a run is one
command and the `.wav` survives to be re-decoded after any change without going
back to the air.

Three things the rig had to answer before it could be trusted:

- **Which device is the radio.** Windows names capture endpoints things like
  "Microphone (3- USB Audio Device)", which identifies nothing. The USB topology
  does: each radio presents an internal hub with its CAT interface on port 1 and
  its audio codec on port 2. Hub #9 carries `VID_0C26&PID_0052`
  (`IC-7300MK2_13004393`, COM8 CI-V) and a C-Media codec - so **the MkII is
  "Microphone (3- USB Audio Device)"**. Hub #8 carries the CP2105 that is COM3 /
  COM4 and a TI codec - so "Line (2- USB AUDIO  CODEC)" is the FTdx101MP.
  `-Probe` reports the level of every device, which is the quick version of the
  same answer: with both radios on, the silent ones are not the one you want.

- **Whether the level is usable.** The MkII's capture level sat at 72%, which
  clipped at 0.0 dBFS with a mean of -8 dB, and clipped audio decodes as noise
  whatever the decoder does. `scripts/set-capture-level.ps1` sets it from the
  command line - Windows only offers a slider in Sound Settings, which makes
  "record, check, adjust, record again" a trip through the GUI each time. **25%
  gives peak -7.8 dBFS, mean -19.7 dB**, which is the number to set before a run.

- **Whether there is a signal in the recording at all.** A transcript of
  "EEEIS EEE" is either a weak signal half-copied or it is hiss, and those want
  opposite responses. `--spectrum` measures it instead of guessing: per-bin mean
  level, and a keying ratio of the loud frames to the typical frame. Steady
  energy sits near 1; a keyed tone spends more time off than on and so scores
  high. Known-good synthetic CW reads **353 at 600 Hz**; the first real
  recording read **4.2, flatness 0.65** - a flat spectrum with nothing keyed.

### 4.3 What the rig found before the radio was even tuned

The first recording was 20 seconds of ordinary receiver noise, no CW on the
frequency. **The decoder produced 42 characters from it** - about 126 a minute of
`E`, `I` and `S`. §4.1 records "five seconds of pure noise -> 0 characters", so
that result does not survive contact with a real receiver.

The reason is in the test's noise, not in the decoder. `CwSignalGenerator.AddNoise`
adds white Gaussian noise across the full 24 kHz, so a narrowband detector at
600 Hz sees a sliver of it. Real receiver audio is band-limited to the SSB filter
and held at a constant level by AGC, so all of that energy is inside the
passband and the detector sees the lot. The synthetic test is not wrong, it is
just not this.

**But it is only an idle problem, and that matters.** Mixing the known-text
synthetic CW into 20 seconds of the MkII's own receiver noise, tiled to length:

| noise bed | characters | copy | confidence |
|---|---|---|---|
| real receiver noise, 20 dB broadband SNR | 61 | 2 characters wrong | **0.71** |
| real receiver noise, 12 dB | 61 | 2 wrong | **0.71** |
| real receiver noise, 6 dB | 62 | 3 wrong | **0.71** |
| real receiver noise, 0 dB | 62 | 3 wrong | **0.70** |
| real receiver noise, no signal | 42 of junk | - | **0.18** |

(Broadband SNR, not the 500 Hz reference figure §4.1 uses - the bed is real
band-limited receiver noise, so a 500 Hz number would be a fiction.)

So the decoder copies through real receiver noise down to 0 dB with two or three
characters wrong out of sixty, which is the result that matters, and **the
confidence figure separates the two cases cleanly: 0.70-0.71 with a signal at
any of those levels, 0.18-0.20 with none.** No overlap, and the engine already
computes and exposes it. A gate somewhere around 0.4 would suppress the idle
chatter without touching copy at 0 dB.

Where that gate belongs - in `CwDecoderEngine` suppressing output, or in the
Reader window showing low-confidence text differently - is a Phase 2 decision
and is not made here. **Nothing has been changed in the decoder on the strength
of this.** The measurement is the deliverable.

### 4.4 Still outstanding

The comparison itself. The rig is proved, the radio is identified and its level
is set, but the first recording had no CW on it. What is needed is a recording
made while the MkII is copying real CW on its own screen, and a note of what
that screen said over the same period. Nothing else is blocking it.


---

## 5. Risks

| risk | handling |
|---|---|
| Capture refactor touches Fabio's audio code | It is on `develop`, not in flight. Raise before Phase 2 lands; his TX/playback path is untouched. |
| Decoder accuracy disappoints | Phase 1 measures it before any UI is built. Reader Mode does most of the practical work. Manual sets honest expectations. |
| Matching the MkII is a higher bar than beating the '101 | Acknowledged in §1.5. The exit criterion is the MkII, so a decoder that only beats the '101 does not pass. Better to find that in Phase 1 than after the UI is written. |
| Adaptive tracking is the whole value, and it is the hard part | It is also well-trodden — fldigi, CW Skimmer and MRP40 all do it, and Icom ship it. The mixed-speed test case is what stops it being quietly wrong. |
| Fast re-acquisition trades against stability on a bad fist | Both are measured in Phase 1, on separate test cases, so the trade is visible rather than discovered on the air. |
| Zero-in sign convention wrong | Bench-verify on both rigs. Deadband and clamp mean a wrong sign fails visibly and harmlessly rather than running away. |
| Core picks up a package dependency | It cannot: the decoder takes samples, the apps open devices. Keeps YWC's `net10.0` CAT-only target building. |
| Bug reports interrupt | That is what the separate branches are for. `develop` stays releasable throughout. |
