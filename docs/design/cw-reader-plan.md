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

### 4.4 First real off-air signal, 2026-08-25

20m was almost dead all morning, then HB9DAX came up on 14.0239 calling CQ and
stayed for about twenty-five seconds. That recording is `bench/`, and it is the
first time any of this has seen a signal that was not synthesised. Radio on
CW-U, 800 Hz IF width, its own CW pitch 611 Hz.

What we copied, at 25.8 wpm:

```
 TCQCQD EHB D A SEI TS 9 D AX CQCQD EH6ECATEE MKE IT TU HB9 D
```

The call is in there three times over, so the element decoder is doing its job;
what is wrong is where the spaces fall. They land inside words rather than
between them. That is not random damage and it is not two bugs - a missing word
space and a spurious one are the same fault seen twice, because `OnGap` scales
both the 2-dit and the 5-dit boundary off the tracked dit estimate, so any error
in the speed estimate mis-splits everything until it recovers.

Sampling the tracker once a second says exactly that. HB9DAX sent a steady
25-26 wpm throughout, and the tone detector held ~555 Hz without wandering:

```
00:04 - 00:09   25-27 wpm    good copy
00:11           42.4 wpm     <-- "HB D A SEI T"
00:14 - 00:20   25-26 wpm    good copy: "S 9 D AX CQCQD E"
00:21 - 00:23   34 -> 47 -> 60 wpm   <-- "H6ECATEE MKE IT TU"
00:24           26.6 wpm     recovers
```

Every garbled run coincides with an excursion, and every excursion goes the same
way - the dit estimate is dragged *short* during weak or quiet passages, never
long. **That is the Phase 2 defect, and it is in the speed tracker, not in the
gap classifier and not in the tone detector.** Pinning the pitch with
`--no-track` changes the tail but not the spacing, which rules the tone
detector out directly.

**Zero-in, validated against an ear.** Colin's words on hearing it were "good
signal now although low in tone". Given the radio's own 611 Hz pitch as the
reference, the decoder measured the station at 556.8 Hz and offered **-54 Hz**.
That is a second independent confirmation of `ZeroInOffsetHz` including the CW-U
sign convention - the first was against the CI-V pitch reading earlier the same
morning - and this one was cross-checked by a human listening to it.

### 4.5 The confidence gate proposed in 4.3 does not work

§4.3 proposed gating idle chatter on `Confidence` at around 0.4, on the strength
of 0.18 for the synthetic noise bed against 0.71 for the mixes. Real receiver
audio does not behave that way:

| recording | what it is | keying ratio | confidence |
|---|---|---|---|
| 0-30 s of the HB9DAX capture | receiver noise, 800 Hz filter | 2.9 | **0.74** |
| 33-60 s of the same capture | HB9DAX, readable | 4.7 | 0.93 |
| a 90 s capture minutes later | receiver noise, same settings | 3.2 | **0.17** |

Noise scored 0.74 in one block and 0.17 in another, same receiver, same filter,
minutes apart. A 0.4 gate passes the first straight through, and no other
threshold does better. **Confidence is not usable as an idle gate**, and the
reason is worth keeping: the narrower the IF filter, the more the noise looks
like a tone to a narrowband detector, so confidence degrades exactly when the
operator does the right thing and narrows down on a weak signal. The synthetic
test could not show this because `AddNoise` is white across 24 kHz.

The keying ratio - how much more energy a bin carries at its 95th percentile
than at its median - is the right *kind* of measure, because it looks at on/off
behaviour rather than tone quality, and it does order the three correctly. But
2.9 and 3.2 against 4.7 is a thin margin, and `--spectrum` duly called the
90 s noise capture "something is keyed around 600 Hz, but weakly" - a false
positive. The `<3` / `<8` verdict thresholds in `tools/CwBench/Spectrum.cs` are
too generous at the bottom.

The likely fix is window size rather than threshold. A station keying for 25 s
inside a 90 s file has its ratio averaged down by the 65 s of silence either
side; measured over a few seconds at a time it would stand out. That is untested
and is not being built here.

### 4.6 Flatness finds a band; the keying ratio does not

Asked which band had any CW on it, the honest answer from a cluster is other
people's antennas. With the bench rig already built the question can be answered
from this one instead: tune each CW segment, take four seconds of audio, and
measure it. Twenty-four points across eight bands takes about three minutes.

The metric that worked was not the one built for the job. **Flatness** - the
median bin divided by the strongest bin, over 100-3000 Hz - was added to
`--spectrum` only as a sanity line, but it is the cleanest band-activity
indicator measured so far:

| | flatness |
|---|---|
| empty segment | 0.84 - 0.93 |
| something present | 0.30 - 0.59 |

An empty passband is flat by definition, because filter-shaped hiss has no peak
in it. Anything at all - keyed or not - puts a peak in and drives the ratio
down. It says nothing about *what* is there, which is the point: it is a cheap
first pass that says where to look properly.

The sweep called 17m the liveliest band on this antenna (0.41 - 0.44) and picked
7035 out as the one live spot on 40m (0.59 against 0.84 - 0.92 elsewhere).
Colin, listening, independently reported CW on 7.035 and on 18 MHz within a
minute of the sweep finishing, having not seen the output. Two confirmations,
both blind.

**The keying ratio produced two false positives in the same sweep**, and the
reason matters for anything built on it later:

```
15m  21005    keying  7.2    level -42.7 dB
10m  28005    keying 16.5    level -49.2 dB
```

Sixteen is a higher ratio than the real HB9DAX signal scored. But the levels are
15 dB below the live bands: these are dead bands whose noise floor is so low
that the p95/median ratio inflates on nothing at all. **A keying ratio is
meaningless without a level qualifier** - it is a shape measurement, and shape
is all it measures. Anything that gates on it must require the level to be above
the band's own floor first.

### 4.7 Finding one station inside a segment

Flatness locates a busy 3 kHz window; it does not locate a station. That took a
second pass, and the technique is worth recording because it will be exactly
what Reader Mode's "find the signal" wants to do:

1. Open the IF filter to 3 kHz. Wide is for finding, narrow is for copying.
2. Step the VFO in 3 kHz windows across the segment, four to six seconds each,
   and take the flatness of each.
3. In the best window, take the strongest bin *that also has a keying ratio* -
   the strongest bin alone is often a carrier or a data signal. On 17m the
   strongest energy sat at 350-500 Hz with a keying ratio of **1.9**, steadier
   than the noise around it; the actual CW was at 2700 Hz with a ratio of 5.9
   and 4 dB less level.
4. The station's RF is VFO + tone. Subtract the radio's own CW pitch to get the
   VFO setting that puts him in the middle of the filter.
5. Narrow back down and copy.

Steps 3 and 4 are `ZeroInOffsetHz` doing its job before a single character has
been decoded, which is a use for it the plan had not anticipated.

### 4.8 The bench rig can record the app instead of the band

Colin: "radio is jumping to VFO B every so often and i can't remember how to
stop that". It was not a radio setting. It was IWC's **cross-band peek**
(`PseudoDualCrossBandEnabled`), which on a single-receiver radio is the only way
to show a watch panel on a different band: select VFO B, take one sweep, hand
the receiver back. His was on, with the interval at 5 s - the minimum the UI
allows.

VFO A was on 17m and VFO B on 20m, so it fired throughout every 17m recording
made that morning. At the keyed bin the two-minute comparison capture reads:

```
00:01  -43.5 dB  spread 124.5
00:06  -48.7 dB  spread  84.9
00:12  -43.8 dB  spread  80.2
00:18  -42.7 dB  spread  75.0
00:23  -44.4 dB  spread  55.4
00:29  -41.9 dB  spread  51.3
```

A burst every 5-6 seconds, 14 dB above its surroundings, flat between. **That is
the app, not the band** - what it caught each time was the DATA signal on VFO B
at 14.079. `--spectrum` duly reported "most keyed 1725 Hz, keying ratio 5.9",
which is to say it identified IWC's own polling loop as the strongest station on
17m.

Two things follow.

**For the bench: cross-band peek must be off before recording anything**, or the
VFOs must be on the same band. This is now the first line of the procedure. The
recording it spoiled is discarded.

**For §4.6: the band sweep ran under the same conditions.** VFO B sat on 20m
while VFO A stepped across eight bands, so every non-20m sample could have
caught a burst - and the 20m rows, the only same-band ones, are the only clean
readings in the table. The conclusion survives, because Colin reported CW on
7.035 and 18 MHz independently and blind, and those were the two lowest-flatness
non-20m readings. But the individual flatness figures for the other bands are no
longer clean evidence and should not be quoted as such. The sweep wants
re-running with the peek off before that table means anything.

There is a general lesson under it, and it is not really about CW. **A bench rig
that records the radio through the app records the app as well.** The dropouts
left no trace in the audio envelope - retuning to another band produces
different noise, not silence, so the level never falls - and they were invisible
to every whole-file statistic. Only the per-second timeline from §4.5 showed
them, and only because it was pointed at the right bin. A five-second period is
also long enough to look like a slow fist and short enough to sit inside a call
sign.

### 4.9 The comparison, done - 2026-08-25, SM4EA on 17m

The bench comparison §4 has been asking for since it was written. Colin tuned
17m by ear to 18,086.118, the radio decoded on its own screen, and 120 s of the
audio it was decoding went to `bench/cw-compare-clean.wav`. Cross-band peek off,
so the recording is clean. Station tone 810 Hz, sending in short bursts with
long listening gaps - about 25 s of transmission in the 120 s.

**The radio's screen: `DM4EA`, then on a later over `SM4EA`.** It wobbled on the
first character itself, which is worth noting before ours is judged.

**Ours, handed the same bursts:**

| burst | ours | the MkII |
|---|---|---|
| 103 s | `DW4EA` | `DM4EA` |
| 116 s | `DMEAEI` | `SM4EA` |

`W` is `.--` where `M` is `--`: one dit too many. Four characters of five, on a
signal 10 dB above the noise floor that the radio itself only managed five of
five once out of twice. **On element decoding we are level with the MkII here.**
That is the first evidence either way, and it is better news than §1.5 assumed.

**On noise output the two are closer than first thought, and the first reading
of this was wrong.** Over the 120 s we emitted 453 characters, most of it
`E I S T` chatter filling the gaps between his bursts. The initial write-up of
this section concluded the radio emitted five, and that the whole gap to the
MkII was output gating. That was an inference from Colin reporting the *readable*
part of the radio's screen, not a report of what the screen held. Asked, he said
the MkII was "only showing garbage" as well.

So the honest position is:

- **Element decoding: level.** `DW4EA` against `DM4EA`, and the radio's own
  second attempt was `SM4EA`. Both drop one character of five on this signal.
- **Noise output: unmeasured.** Both decoders print junk between bursts. Which
  prints more, and whether the MkII gates at all or merely gates better, is not
  known and was not captured. Our 453 has no counterpart figure to sit beside.

That is a smaller and less flattering result than the first draft claimed, and
it leaves the §4.3 / §4.5 output-gating question genuinely open rather than
confirmed against the reference. The gating problem is still real - 453
characters for 25 s of signal is unusable however the MkII behaves - but "output
gating is the whole gap" is not supported by anything measured here.

**What would settle it** is a photograph or transcription of the MkII's full
screen over a known recorded window, rather than the callsign read off it. That
is one more bench run, and it needs nothing that is not already set up.

### 4.10 The MkII's decoder needs zero-beat. Ours does not.

Colin, mid-session: "I'm hearing CQ but the radio isn't showing that."

He had tuned by ear to a comfortable tone, 18,086.118, which put the station at
**790 Hz** against the radio's CW pitch of **611 Hz**. The MkII's decoder works
off the audio at the pitch, so 179 Hz out and it showed nothing at all, on a
signal a human was copying comfortably and which our detector locked at
confidence 0.98. Tuning up 179 Hz to put him on the pitch, the radio immediately
put **OH3MMF** on its screen.

**Read §4.10a before relying on this.** A second attempt to reproduce it the
same day was confounded, and what looked like a clean result is really n=1 with
an obvious alternative explanation.

**If it holds, it is the first measured advantage in our favour, and not a small
one.** `CwDecoderEngine` hunts a search window - 200 Hz here, 300 Hz earlier -
and found him without being told where he was. The reference decoder requires
the operator to zero-beat first. In practice that means the MkII's decoder is
only useful once you have already done the work of tuning him in, which for a
partially sighted or blind operator is exactly the work that is hardest.

Two consequences worth carrying into Phase 2:

- **§3.4 zero-in is not a nicety, it is the feature that makes the radio's own
  decoder usable.** It has now been confirmed three times on real signals
  (-54 Hz against Colin's ear, +19 Hz on 17m, +179 Hz here), and this is the
  first time its output was applied and the effect observed on the radio.
- **Reader Mode should not assume the operator has zero-beat anything.** The
  search window is doing real work. Pinning the detector to the configured pitch
  would throw away the one thing we demonstrably do better.

It also re-reads §4.9's `DM4EA` / `SM4EA` result. That capture was made at
810 Hz against a 611 Hz pitch - 199 Hz off - so the MkII was decoding a signal
outside its own comfortable range, and `DM4EA` was it doing well rather than it
doing its best. The element-decoding comparison there should be treated as
provisional until it is repeated on the pitch.

### 4.10a The reproduction failed, and 4.10 is n=1

Same session, a couple of hours later. Colin: "very strong cw now but radio not
decoding it" - the same symptom, which looked like a free replication.

Measured: tone ~890 Hz against the 611 Hz pitch, +279 out, strong at -24 dB
against a -36.5 dB floor. Tuned VFO A up 279 Hz. The radio started decoding and
printed `N4IYO` then `F4IYO`. On the face of it, confirmation.

It is not. Decoding `bench/ve2mf.wav` shows **the callsign came from a station at
388 Hz**, not from the one I had measured and tuned for:

```
[00:05] T TT TTTT TM F4I
           . 00:10  20.5 wpm   387.2 Hz   17.2 dB  signal  locked
[00:10] YO
```

388 Hz is 223 Hz **below** the pitch. And before the retune that station sat at
about 667 Hz, which is nearly on the pitch - and the radio was not decoding then
either. So the retune moved him *away* from the pitch and the radio then read
him, which is the opposite of what the hypothesis predicts.

**The parsimonious explanation is that F4IYO started sending during the window
and the retune was irrelevant.** That explanation also fits §4.10: OH3MMF may
simply have sent his callsign at the moment I finished tuning.

So the zero-beat claim is **one uncontrolled observation**, not a finding, and
§4.10 is marked accordingly. What is needed is a controlled test, which is easy
and has not been done:

> With a station sending steadily and the radio decoding him, tune 200-300 Hz
> off the pitch and back, several times, watching the screen. Same station, same
> propagation, one variable. If the screen stops and starts with the tuning, the
> claim is real.

Until that is run, do not cite zero-beat as an advantage in the manual, in a
release note, or to a user.

**What this capture does show, and does not depend on the radio at all:** our
search window found and copied a station **223 Hz from the configured pitch** at
18 dB without being told he was there, and got `F4IYO` correct first time where
the MkII's first attempt was `N4IYO` (`-.` for `..-.`) and only its second was
right. That is a real, self-contained result.

### 4.11 The MkII emits noise junk too, and the gate has a number

`bench/oh3mmf.wav`, 120 s on 18,086.297, the station now on the pitch, both
decoders watching the same audio. Colin read out the MkII's **whole** screen,
which is the thing §4.9 was missing:

> `OH3MMF OH3MMF H E IHVAIE EEET EEEEITT` ... then later, live, `R3EV`

Ours over the same window, 318 characters:

> `ISS EIEEE 5EEIEEII SEIIE EH S E EEEEE TTTTT TTTM TT TT EEHNB= E SEE EI ...`
> `... SEEII N 3EV I 3E V NTF N3EV E N3EV N3E`

**The reference decoder does not gate its output either.** `H E IHVAIE EEET
EEEEITT` is the same failure signature as our `EEEEE TTTTT TTT` - single-element
characters, E and I and T, noise crossing the threshold and being classified as
dits and dahs. Icom did not solve this; they shipped it. The §4.9 worry that
"output gating is the whole gap" is now refuted twice over, and the real position
is that the two decoders fail the same way on noise and succeed about equally on
signal.

One thing that cannot be compared is volume. The MkII's screen is one scrolling
line - Colin's note was "RAN OF SCREEN NOW" - so the radio has no character
count to set against our 318. It discards its own junk by running out of room.
Whatever we do about gating, we cannot claim the MkII does better at it, only
that it hides it.

**How much of the window was signal.** The per-second timeline at 658 Hz says
about **nine seconds of the 120** - 01:38-01:41 and 01:48-01:52. Everything else
is receiver noise. So 318 characters came out of nine seconds of sending and 111
seconds of hiss, and both decoders spent almost all of their output on nothing.

**And the gate finally has a measurement behind it.** Over those 119 one-second
windows, `spread` (p95/p10 in the tone bin) separates cleanly:

| | min | median | p95 | max |
|---|---|---|---|---|
| noise seconds (110) | 3.5 | 6.0 | 8.8 | 12.7 |
| signal seconds (9) | 9.5 | 19.9 | - | 27.2 |

| threshold | seconds kept | signal caught | false |
|---|---|---|---|
| >= 8 | 18 | 9/9 | 9 |
| >= 10 | 12 | 8/9 | 4 |
| >= 12 | 10 | 8/9 | 2 |
| >= 14 | 7 | 7/9 | 0 |

`spread >= 12` keeps ten seconds out of 119 and catches eight of the nine, which
would have turned 318 characters into roughly the callsign runs and nothing else.
That is the first gating criterion in this whole exercise with real numbers under
it, and it is worth noting what it is **not**: not confidence, which §4.5 showed
inverts on a narrow filter; not absolute level, which §4.6 showed reads high on a
dead band; not keying ratio, which §4.4 showed is duty-cycle dependent. It is a
relative measure inside one bin over a short window, so it needs no calibration
and no noise-floor estimate.

This is a Phase 2 decision and it is not authorised. But it no longer needs
another bench session to make - the measurement is here and the recording is
kept, so any threshold can be re-tested offline.

**Both got the callsign.** On the strongest four seconds ours reads `N3EV`
twice at 18-19 dB; the radio read `R3EV`. One dot apart, `-.` against `.-.`,
and there is nothing in this recording that settles which of us is right.

### 4.12 Still outstanding

The comparison §4 asks for is **done**. Element decoding is level (§4.9,
provisionally - see the caveat in §4.10 about that capture being 199 Hz off the
pitch); noise output is level and neither decoder gates (§4.11); and we hold one
demonstrated advantage, the search window that finds a station the MkII cannot
see until the operator has zero-beat it (§4.10).

What is left before Phase 2 is our own work, not more measurement against the
radio:

- **The speed tracker (§4.4).** Still the one located defect. `_ditMs` excursions
  go short and drag both gap boundaries with them; every garbled run in every
  recording so far coincides with one. This capture shows it again - 60.0 wpm at
  00:10 and 01:30, both on low-spread noise seconds, the tracker being pulled
  about by hiss.
- **Output gating.** §4.11 gives the criterion and a threshold; where it lives in
  the pipeline, and whether it suppresses or merely marks, is undecided.
- **Repeat §4.9 on the pitch**, so the element comparison stops being provisional.
- **Re-run the §4.6 band sweep with cross-band peek off**, so its per-band
  flatness figures mean something.
- **`docs/design/cw-bench-procedure.md`**, which CwBench's usage text already
  points at and which does not exist. First line: cross-band peek off before
  recording.

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
