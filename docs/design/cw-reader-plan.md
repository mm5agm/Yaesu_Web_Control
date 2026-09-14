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
| **3** | **YWC:** Reader Mode, transcript, ADIF QSO save | code complete 2026-09-01, see §4.16. Reader Mode is protocol-level and **unverified** - it needs the bench |
| **4** | **Core -> IWC:** `git subtree push` from YWC then pull into IWC (§2.1), IWC capture service, reader UI, software ZIN | code complete 2026-09-02, see §4.17. Reader Mode and zero-in are protocol-level and **unverified** - both need the bench |
| **5** | `USER_MANUAL.md` section, README notes | written 2026-09-01 - manual §6.10 and §20, README feature section. **No release-notes entry and no version bump** - that waits on Colin's go |

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

### 4.10b The controlled test could not be run. The sign scare was mine, and the sign is fine.

The test §4.10a specifies was attempted the same afternoon and is **void**. Worth
recording why, because the reason will recur.

**A pileup is the one environment where it cannot work.** Baseline was clean -
EA3NY on the pitch at 621.7 Hz, confidence 0.86, radio copying him. I widened the
IF to 2400 Hz so the filter could not be the explanation, moved the VFO 250 Hz,
and asked what the screen did. It said `RI1FJL RI1FJL UP` - Franz Josef Land,
working split, which is what the whole pileup was there for.

That is not the radio decoding an off-pitch station. **Moving the VFO in a pileup
does not take a station off the pitch; it swaps which of the dozens of callers is
sitting on it.** The spectrum confirms it: strongest bin 300 Hz, most-keyed bin
1000 Hz, energy at every tone. There is no "the station" to move.

> **Precondition for the §4.10a test: one station, on a quiet frequency, sending
> steadily.** Not a pileup, not a DXpedition, not a contest. Widening the filter
> to remove the filter confound makes this worse, not better.

**And it turned up something more serious.** Our own captures of the move say the
audio tone went the *wrong way*:

| | VFO | strongest bin | decoder lock |
|---|---|---|---|
| before | 18,086,576 | 725 Hz | 621.7 Hz |
| after (VFO **-250**) | 18,086,326 | 450 Hz | 434.6 Hz |

The VFO went **down** 250 Hz and the tone went **down** ~200-275 Hz. §3.4's
convention - `offset = tone - pitch`, add to the VFO - predicts the tone rises.
If the measured direction is right, then every zero-in retune made today pushed
the station *further* off the pitch, and the radio decoded anyway. That would
explain §4.10 and §4.10a at a stroke, and it would mean **`ZeroInOffsetHz` tells
the operator to tune the wrong way** - which for the accessibility case this
feature exists for is worse than not offering it at all.

It is **not established.** The "after" capture was taken with the filter widened
to 2400 Hz, so several stations were in the passband and the decoder may simply
have locked a different one. A clean sign measurement was attempted immediately -
three captures 200 Hz apart, cross-correlating the whole spectrum, which a pileup
should make easy - and the band went quiet during the thirty seconds it took:
flatness 0.40, 0.74, 0.64, the last two effectively empty. Inconclusive.

**SETTLED, minutes later, and the convention is right.** The band came back, and
the measurement is unambiguous - same 800 Hz filter, same conditions, twelve
seconds each, one 200 Hz step:

| VFO | strongest bin | flatness |
|---|---|---|
| 18,086,576 | **500 Hz** | 0.07 |
| 18,086,776 (**+200**) | **300 Hz** | 0.07 |

VFO **up** 200 Hz, tone **down** exactly 200 Hz. That is §3.4 working: a tone
200 Hz above the pitch is brought onto the pitch by adding 200 to the VFO.
**`ZeroInOffsetHz` is not backwards, and there is no bug here.**

The scare came entirely from taking a measurement through a widened filter. With
the IF at 2400 Hz the passband held several stations and the decoder locked a
different one, which read as the tone moving the wrong way. **Never measure a
tone shift with the filter wider than the one the operator uses** - the filter is
what makes "the signal" a single thing.

It also means the retunes made earlier today did put their stations on the pitch,
so §4.10's observation is at least coherent. §4.10a's refutation is untouched by
this: the callsign the radio printed still came from a station at 388 Hz, which
before that retune sat near 667 Hz - close to the pitch, and undecoded. Zero-beat
remains **one uncontrolled observation** awaiting the §4.10a test on a quiet
frequency.

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

This is a Phase 2 decision and it is not authorised. **And within the hour it was
refuted - see §4.11a. The table above is correct and useless.**

**Both got the callsign.** On the strongest four seconds ours reads `N3EV`
twice at 18-19 dB; the radio read `R3EV`. One dot apart, `-.` against `.-.`,
and there is nothing in this recording that settles which of us is right.

### 4.11a A tune-up carrier breaks every frame-level gate

Colin, minutes after §4.11 was written: "strong tune signal". Someone tuning up
on frequency - a steady carrier, keyed on once and off once. `bench/tuneup.wav`.

It is the case §4.11 never considered, and it walks straight through the gate:

```
  00:04   -21.4 dB   keying  1.7   spread 51.3   CW
  00:06   -20.1 dB   keying  1.2   spread 30.1   CW
  00:09   -21.7 dB   keying  1.1   spread 49.1   CW
  00:10   -20.2 dB   keying  1.1   spread 37.2   CW
```

Median spread **37.2**, against 19.9 for the real QSO. The gate does not merely
pass a tune-up, it rates it **more confidently than actual CW**.

Adding the keying ratio back as a second condition does not rescue it:

| gate | noise | CW | carrier |
|---|---|---|---|
| spread >= 12 | 2/110 | 8/9 | **11/11** |
| spread >= 12 & keying >= 1.8 | 1/110 | 8/9 | **6/11** |
| spread >= 12 & keying >= 2.2 | 0/110 | 7/9 | **5/11** |
| spread >= 14 & keying >= 2.0 | 0/110 | 6/9 | **6/11** |

The second condition halves the carrier and costs real CW to do it. There is no
corner of this table that is usable.

**This is not a threshold problem, and no amount of further tuning will fix it.**
A tune-up *is* a very slow dah - on, held, off. At a one-second timescale it is
not different from keying, so no statistic computed over one-second frames in one
bin can separate them. Every candidate this exercise has produced now has a
counter-case:

| candidate | fails on | recorded in |
|---|---|---|
| confidence | narrow IF filter inverts it | §4.5 |
| absolute level | reads high on a dead band | §4.6 |
| keying ratio (p95/median) | duty-cycle dependent | §4.4 |
| spread (p95/p10) | tune-up carrier | §4.11a |
| spread & keying together | tune-up carrier | §4.11a |

**The gate belongs downstream, on decoded structure, not on frame statistics.**
A ten-second mark is not a dit and not a dah, and `CwElementDecoder` already has
everything needed to know that: element durations that refuse to cluster into two
groups, and characters that do not resolve to valid symbols. That is where the
next attempt should look, and it is the first time in this exercise that the
answer has pointed at the decoder rather than at the detector.

Worth keeping in mind that §4.11's other finding stands untouched: the MkII does
not gate either, and prints the same junk. Whatever we build here, we are ahead
of the reference the moment it does anything at all.

### 4.11b The rematch: the MkII won, and the search window is why

The §4.9 rematch finally happened, on the cleanest data of the whole exercise -
Colin narrowed the IF to **200 Hz**, one station, flatness 0.03, tone 626.6 Hz
(16 Hz off the pitch), our confidence 1.00. `bench/cw200.wav`, 60 s.

**The MkII won this one, clearly.** Its screen - Colin's words, "TOO MUCH
GARBAGE", then:

> `DM4EA E DM4EA DM4EA EB  EI M4EA ITEEE TDM4EA E DM4EA`

Five clean `DM4EA` and one partial. Ours at the default ±250 Hz search:

> ` F5NNTU HW4E E E M 4EU E HH IMTT T TT EMT TT 4EAR E E LW4EMH2`

**Not one clean callsign.** `HW4E`, `4EU`, `4EA`, `LW4EMH2` are all the same
callsign mangled. Same station as §4.9, so this is a direct rematch, and §4.9's
"element decoding is level" does not survive it.

**The cause is the search window, and it is fixable.** Sweeping it over the same
recording:

| search | chars | confidence | clean `DM4EA` |
|---|---|---|---|
| ±20 | 255 | 0.29 | 0 |
| ±40 | 69 | 0.66 | 1 |
| **±60** | **51** | 0.75 | **2** |
| ±80 | 46 | 0.90 | 1 |
| ±120 | 57 | 0.99 | 1 |
| ±250 (default) | 61 | 1.00 | **0** |

A 200 Hz filter is **±100 Hz around the pitch**. The best decodes sit at ±60-80,
just inside that half-width; the default ±250 hunts 150 Hz *outside* the
passband, where there is nothing but noise, and it costs the callsign outright.
Too narrow is bad as well - ±20 collapses to 255 characters of junk - so this is
a genuine optimum, not a monotonic "narrower is better".

> **Design rule for Phase 2: derive the search window from the IF filter width,
> not from a constant.** Something like half-width, clamped to a sane floor.

**And this is a thing we can do that our competition cannot.** YWC and IWC read
the IF filter width over CAT/CI-V - it is in the status payload already
(`vfoA.ifWidth`). A standalone decoder listening to a soundcard has no idea how
wide the operator's filter is and must guess a fixed window. We do not have to
guess. That is a structural advantage of living inside the radio-control app, and
it is the first one this exercise has found that does not depend on an
uncontrolled observation.

**Two side findings:**

- **`--no-track` was far worse here**, the opposite of the HB9DAX case in §4.4.
  So the speed tracker is not simply broken; it helps on a clean signal and hurts
  on a fading one. Whatever is done to it must not be "turn it off".
- **Confidence rises monotonically to 1.00 as the decode gets worse** across that
  sweep. That is the fifth independent way this figure misleads, after §4.5. It
  should not be shown to an operator as a quality indicator, and it must not gate
  anything.

### 4.11c FT8 is the fourth reference class, and it buries the gate

Colin put the radio on an FT8 signal by accident (`bench/narrow200.wav`, 60 s),
which handed us the signal class none of the recordings had. Set against the
others, with the best gate §4.11a could offer:

| class | n | median keying | median spread | passes `spread>=12 & keying>=2.2` |
|---|---|---|---|---|
| receiver noise | 110 | 2.1 | 6.0 | 0% |
| CW (800 Hz filter) | 9 | 3.6 | 19.9 | **78%** |
| CW (200 Hz filter) | 59 | 2.2 | 6.1 | **24%** |
| tune-up carrier | 11 | 2.0 | 37.2 | 45% |
| **FT8** | 59 | 2.3 | 9.0 | **27%** |

**FT8 passes the gate more often than real CW through the operator's own narrow
filter.** So does a tune-up. The gate is not merely imperfect, it is
anti-correlated with what we want on two of five classes, and §4.11a's conclusion
- that frame statistics in one bin cannot do this job - is now supported by four
independent signal classes rather than two.

Note the third row as well: **narrowing the IF changed CW's own statistics
completely**, median spread 19.9 down to 6.1. Any threshold calibrated at one
filter width is wrong at another. That alone would sink a fixed frame-level gate
even without FT8.

### 4.11d The runs of TTTT are the speed tracker, and both decoders have the bug

Colin called a signal that sounded clean and printed garbage, twice in ten
minutes. Then he read the radio's own display out: **AUTO, 22 wpm - "but that
was 29 a second ago"**. That is the MkII's speed tracker swinging 7 wpm on a
steady sender. It is not a detail about his rig; it is the same failure we have
been calling ours since §4.4, seen from the other side of the comparison, and it
is the reason the radio was printing garbage on a signal his ear said was fine.

CwBench could watch our tracker move but not stop it, so `--wpm <n>` (seed the
tracker) and `--pin-wpm <n>` (clamp `MinWpm` and `MaxWpm` onto the seed so it
cannot move at all) were added. `CwElementDecoderOptions` already exposed all
three fields, so this is bench plumbing only - no Core change. Pinning separates
"the tracker is wrong" from "the elements are wrong" on a recording where both
look identical.

Run over three recordings, counting runs of four or more identical
single-element characters - the `TTTTTTTTT` and `EEEEE` strings that dominate
every bad transcript:

| recording | tracking (default) | `--pin-wpm 22` | `--pin-wpm 25` |
|---|---|---|---|
| `goodcw` (200 Hz IF, on pitch) | 118 chars, **8 runs**, longest 11 | 35 chars, **0 runs** | 36 chars, **0 runs** |
| `cw200` (the §4.11b rematch) | 61 chars, 0 runs | 43 chars, 0 runs | 45 chars, 0 runs |
| `oh3mmf` (the original 120 s) | 506 chars, **18 runs**, longest 9 | 131 chars, **1 run**, longest 4 | 164 chars, **1 run**, longest 4 |

**The runs are an artefact of the tracker, not of the signal.** Freeze the speed
and they stop. The pinned *value* barely matters - 18, 22, 25 and 28 wpm all
produce the same collapse - only that it stops moving. Junk output falls two to
four times over.

On `goodcw` the pinned sweep at `--pitch 560 --search 60`:

```
tracking      58 chars   HI ETT TTTTTETTTS R2WBR RB E EE ETT ETT TTTTTTTTTEI E FTSH
--wpm 25      57 chars    I ETT TTTTTETTTS R2WBR RB E EE ETT ETT TTTTTTTTTEI E FTSH
--pin-wpm 18  20 chars     R RR  E EE R  E FBH
--pin-wpm 22  22 chars     R RR R E EE R R E FBH
--pin-wpm 25  23 chars    R2 RR R E EE R R E FBH
--pin-wpm 28  28 chars    R2WB RBRB RB E EE R R E FBH
```

Two things in that table matter beyond the run count.

**`--wpm` alone does nothing.** Seeding the tracker at the right speed and
letting it run gives 57 characters against tracking's 58 - the same transcript
minus one character. The tracker walks straight back off the seeded value.
Whatever is pulling `_ditMs` about is doing it within seconds, so an initial
estimate cannot help; only the clamp does.

**`FTSH` becomes `FBH`.** `FB` is what a CW operator actually sends. The real
elements were being recovered all along and the tracker was inserting extra
symbols between them. `RB` and `R2WB` persist at every pinned speed, so there is
still structure we are mis-slicing - pinning does not hand us a callsign, and no
pinned speed on this recording produces one.

What this changes:

- **§4.4 is promoted from "the one located defect" to a measured cause with a
  reproduction.** Any future change to the tracker has three recordings and a
  run-count to be judged against, without going back to the air.
- **A pinned speed is not the fix.** It cannot be: the operator does not know
  the sender's speed, and §4.1's mixed-speed case exists precisely because it
  changes mid-QSO. What the clamp shows is that the tracker's *excursion range*
  is the problem, not its centre. A tracker that moved slowly, or that refused
  to move on frames that failed a spread test, would get most of this back
  without pinning anything. That is Phase 2 work and is not authorised.
- **Show the tracked speed in the UI.** Icom do, and it is why Colin could tell
  us the radio was confused rather than the band. `CwDecoderEngine` already
  exposes `WordsPerMinute`; not surfacing it is a gap. It is also the honest
  version of the confidence figure §4.11b killed - a number the operator can
  sanity-check against their own ear, rather than one that rose to 1.00 while the
  decode fell apart.
- **We are not behind the MkII on this one.** §4.11b left us behind on clean
  data and that stands. But the tracker instability is not our defect alone;
  Icom ship it too, on the same signal, in the same minute.

#### 4.11d.1 It rails at MaxWpm on noise - and the fix is already in the pipeline

`yu1eu.wav` (60 s, 18 MHz, CW-U, 200 Hz IF, one station at 650 Hz, flatness
0.04 - the cleanest single-signal capture of the day) makes the mechanism
exact. The tracked speed through the recording:

```
00:05  60.0 wpm  quiet      00:25  60.0 wpm  quiet
00:10  60.0 wpm  quiet      00:30  60.0 wpm  quiet
00:15  60.0 wpm  quiet      00:35  60.0 wpm  quiet
00:20  60.0 wpm  quiet      00:40  36.4 wpm  signal
```

**It is not wandering. It is railing at 60.0 wpm, which is `MaxWpm` exactly.**
Noise pushes `_ditMs` to the floor, the clamp catches it, and it sits pegged
there until real signal arrives to drag it back. Only about 20 of these 60
seconds carried signal; the tracker spent the other 40 pinned at its ceiling
turning hiss into characters.

That also explains §4.11d's otherwise odd result that `--wpm` alone does
nothing. Seeding the tracker at the right speed cannot survive the first few
seconds of hiss - the rail is reached from any starting value.

| | chars | runs >=4 | output |
|---|---|---|---|
| tracking | 108 | 2 (longest 9) | `EEI 5I EE ETIIM (TTE AAA  I E E E E E EEEH DMANEE EEESSENE I E E ESEI IEEEIEEEE IE SSAT V MIESI O  E W EW EW` |
| pin 20 | 27 | 1 | `H I<HH>A IE EEEEIIH WEWEWEW` |
| pin 25 | 28 | 1 | `II IA IE E E EEIEESH WEWEWEW` |
| pin 28 | 31 | 1 | `II IA IE E E EEIEE5ISH WEWEW EW` |

Neither setting copies YU1EU, so this is not the whole gap. But the shape of
the fix is now specific rather than a guess: **do not update the speed estimate
on frames where no signal is present.**

The discriminator for that already exists and is already wired. `CwToneDetector`
computes `SignalPresent` per frame (`CwToneDetector.cs:261`), and
`CwDecoderEngine` already reads it into `_signalPresent`
(`CwDecoderEngine.cs:106`) - it is what the bench prints as `quiet` / `signal`
in the table above. Nothing consumes it in `CwElementDecoder`, which updates
`_ditMs` from every mark and gap it is handed regardless. The change is to hold
`_ditMs` while `SignalPresent` is false.

This is **Phase 2 and is not authorised.** It is written down here so it is not
re-derived, and so it is judged against the run-count table in §4.11d rather
than against whether the next recording happens to look better.

#### 4.11d.2 The prescription in 4.11d.1 was wrong, and the measurement says why

Written 2026-08-26, with the IC-7300 MkII live on the bench.

**Holding `_ditMs` while `SignalPresent` is false is a no-op.** Implemented as
written, it produced byte-identical transcripts on all four recordings. The
reason is in `CwToneDetector`: when the presence gate says absent it forces
`_keyDown = false`, so a mark can only exist inside a presence excursion. Every
mark the element decoder is ever handed arrives with `SignalPresent` true -
measured, 509 of 509 across `goodcw`, `oh3mmf` and `yu1eu`, gate true at the
closing edge and true for the whole mark in every case. The gate cannot
discriminate because the gate has already had its say. Section 4.11d.1 was right
that nothing consumed `SignalPresent` and wrong about what would happen if
something did.

`--no-resync` (a new bench flag) clears the other suspect: with the hard re-seed
disabled entirely, `yu1eu` still walks to 58.9 wpm through the quiet. It is the
EMA in `UpdateCentroids`, fed short marks, and not `TrackResync`.

**What actually rails it.** A new `--marks` report drives the detector directly
and prints every run the element decoder would be handed. On `yu1eu`:

```
 at        ms     SNR      peak   x noise
  0.27    250    41.7    0.0672       3.1
  0.43    135    26.1    0.0670       2.8
  0.69     25    19.6    0.0720       2.6
  0.82     15    18.3    0.0707       2.4
  ...  (twenty more, all 15-35 ms, all inside the first two seconds)
  2.00     20     7.3    0.0595       2.3
 36.38     15     9.3    0.0605       2.3
```

Two sources, neither of them the band:

1. **Detector warm-up.** The noise mean is an EMA with a quarter-second time
   constant; the peak tracker rises four times faster. For the first second
   after audio starts their ratio reads as tens of dB of SNR on plain hiss, the
   presence gate opens, and a burst of 15-35 ms marks arrives before a single
   real element. That is enough to peg `_ditMs` at `MaxWpm` in the first two
   seconds of every session. `yu1eu` had reached 60.0 wpm by 00:05 - its signal
   did not start until 00:45.
2. **Noise bursts mid-recording.** The 36-42 s group above, same shape.

**What separates them from elements is level, not presence.** Per-mark peak as a
multiple of the tracked noise floor, medians by duration bucket:

| recording | marks under 20 ms | marks 40-160 ms |
|---|---|---|
| `yu1eu` | 2.3x noise | 6.3-7.3x |
| `goodcw` | 2.7x | 5.1-5.6x |
| `oh3mmf` | 2.4x | 5.1-7.0x |
| `cw200` | 3.8x | 4.9-5.2x |

Real elements sit at five to seven times the noise floor and noise blips at two
to three, on every recording, at any speed. `SnrDb` does not separate them - it
is built from the slow peak tracker and describes the signal, not the mark - so
`CwToneSample` now carries `NoiseLevel` as well.

**The fix is two lines of policy and no new machinery:**

- `CwToneDetectorOptions.WarmupSeconds` (0.5). Report nothing keyed until the
  estimators have settled. They run throughout; only the claim that something is
  keyed is suppressed.
- `CwElementDecoderOptions.MinTrainNoiseMultiple` (3.5). A mark trains the speed
  only if its peak reached that multiple of the noise floor at the time. It
  still prints - output gating remains a separate question (section 4.12).

Judged against section 4.11d's run-count table, same invocation before and
after, tracking on and nothing pinned:

| recording | before | after |
|---|---|---|
| `goodcw` | 78 chars, 2 runs, 52.4 wpm | **35 chars, 0 runs, 24.9 wpm** |
| `cw200` (clean control) | 61 chars, 0 runs, 25.9 wpm | 48 chars, 0 runs, 23.3 wpm |
| `oh3mmf` | 308 chars, 12 runs, 36.4 wpm | **126 chars, 5 runs, 36.4 wpm** |
| `yu1eu` | 101 chars, 1 run, 34.7 wpm | **34 chars, 0 runs, 29.4 wpm** |

Section 4.11d's point was that pinning the speed collapsed the junk: `--pin-wpm
22` gave `goodcw` 35 characters and 0 runs, `yu1eu` 27 and 1. **The adaptive
tracker now matches the pinned numbers without being pinned** - 35 and 0 on
`goodcw`, 34 and 0 on `yu1eu` - which is what section 4.11d said a tracker that
refused to move on bad frames would do. The tracked speed is no longer absurd
either: `goodcw` reads 24.9 wpm where it read 52.4, and `yu1eu` holds its
initial 20 wpm through 45 seconds of quiet instead of railing at 60.

A relative test - train only on marks reaching half the level of recent marks,
against a decaying reference - was implemented and measured first, then removed:
with warm-up and the noise multiple in place it changed nothing at all
(identical output at every ratio and decay setting), and its reference has to be
bootstrapped, which noise does when the band is quiet at startup.

**One threshold is still unsettled: 3.5 or 4.0.** They differ on exactly one
recording, `mkii-nodecode.wav` (2026-08-26, a signal Colin could hear and the
MkII would not print): 3.5 gives 97 characters and 2 runs at a railed 57.8 wpm;
4.0 gives 42 characters, 0 runs and a plausible 26.5 wpm. That signal is weak
enough to sit on the threshold itself. Against that, 4.0 fails one synthetic
case in `Rides_through_QSB_without_a_threshold_control` (0.25 Hz, 20 dB fade
depth: 37.5% against a 40% floor), because a station fading 20 dB stops clearing
the bar and the tracker stops following its elements down. 3.5 passes all 69
tests, so 3.5 ships. A hysteretic version - strict while unlocked, relaxed once
locked - was tried and measured worse on three recordings, so it is not that
either. Settling this needs more weak-signal captures.

**A test change came with it.** The Phase 1 accuracy tests began keying in the
first audio frame after 0.3 s of silence, which no real capture does - a decoder
is switched on before the other operator starts sending. They now open with
`CwSignalGenerator.LeadIn()`, one second. The accuracy floors are untouched, and
all 69 tests pass.

**New bench instrumentation**, all in `CwBench`:

- `--marks [n]` - the run-by-run report above, with per-bucket SNR, level and
  noise multiple, plus the first n marks listed individually.
- `runs >=4` in the summary - the section 4.11d metric counted mechanically
  rather than by eye: maximal runs of four or more identical single-element
  characters (`E T I M S O H 5 0`).
- `--warmup <s>`, `--train-noise <x>`, `--no-resync` - each of the settings
  above, so any of them can be turned off on a recording without a rebuild.

#### 4.11e SP5XOC, 2026-08-26 - and the hole in the whole of section 4

Colin tuned to a station calling CQ on 14,027.70 (CW, FIL2, 500 Hz, pitch
610 Hz, AGC MID, NR/NB/notch off). `bench/sp5xoc.wav` is 120 s of it.

**Read the next paragraph before trusting any comparison in this section.**
Colin does not read CW beyond recognising the sound of a CQ. Everything he
reported during the session - "SP5XOC", "CFM 5NN TU", the sign-off on 14,040.90 -
came off the MkII's decoder screen, not off his ear. **There was no independent
ground truth for any live recording made on 2026-08-26.** Scoring the Core
decoder against those strings scores it against the decoder we are trying to
beat, which can show agreement and disagreement but never correctness. An
earlier draft of this section claimed a first win over the radio on
`sp5xoc.wav`; that claim was unsupported and is withdrawn.

What the recordings do support:

- On `sp5xoc.wav` the MkII printed the callsign and `CFM 5NN TU` early and then
  degraded to garbage. The Core decoder printed `EFM 5NN TU` - agreeing with the
  radio, one character worse - and later `CQCQCQDE` with a callsign attempt,
  confidence 0.96. Neither decoder produced a transcript an operator could work
  a station from.
- On `bench/ft-14040-90.wav` (14,040.90, a slow sender at 13.7 wpm) both
  decoders worked. 118 characters, 0 junk runs, confidence 0.85, tone locked at
  537.9 Hz from a 610 Hz pitch - 72 Hz off, handled by the derived window. The
  Core transcript ends `MY BEST 73 AND HAVE NICE RADIO DAY`, matching the MkII
  verbatim. Through the stretch Colin logged as the radio producing garbage, the
  Core decoder printed `MY QSL 4IA BO OK` - plausible English where the MkII had
  none, which is the one place today's evidence favours us, and it rests on
  Colin's approximate report of where that stretch was.

**This is a hole in the bench method, not just in one session.** Section 4 has
been comparing two decoders with no reference. Fixing it needs a source whose
text is known independently of either radio. Three options, cheapest first:

1. **Fldigi on the same `.wav`.** It is already installed and already wired into
   both apps' launcher buttons. A third decoder does not give truth, but three
   disagreeing decoders localise the disagreement, and Fldigi is the reference
   implementation everything else is measured against.
2. **A known-text transmission** - W1AW code practice, or a beacon - where the
   sent text is published.
3. **Colin's own keyer into a dummy load**, which gives exact text at a chosen
   speed, at the cost of not being an off-air signal.

Until one of those is in place, every "the MkII got it wrong" in section 4
should be read as "the MkII and the Core decoder disagreed".

**It also caught a mistake worth recording.** The first decode of that file
found nothing, because it was run with `--search 150` and the station's tone was
at 842 Hz - 232 Hz off the radio's own CW pitch, sitting on the top skirt of the
500 Hz filter. The shipped default of 250 Hz finds it; the bad invocation was
mine. Measured on the file:

| tone search | transcript |
|---|---|
| +/-150 Hz | junk - it transcribed the noise beside the signal |
| +/-200 Hz | `FM 5NN TU` plus fragments |
| **+/-250 Hz (the default)** | `FM 5NN TU ISEI SK ... CQCQDE ...` |
| +/-300 Hz | worse - it reaches past the skirt and chases attenuated energy |

The right window is not a constant that happens to suit one filter. It is the
passband: **half the IF filter width either side of the pitch**, which is 250 Hz
for the 500 Hz filter and something else for every other filter the operator can
select. `CwDecoderOptions.SearchWindowForFilterWidth` does that mapping, clamped
to 100-500 Hz - a 250 Hz filter must not licence a hunt 250 Hz wide, and a
2.4 kHz SSB filter must not licence a lock 1.2 kHz off the pitch, which would be
a different QSO rather than a mistuned one. `CwBench --filter <Hz>` sets it, and
six cases are pinned in `Tone_search_covers_the_passband_and_no_more`.

Deriving the window from the 500 Hz filter reproduces the previous numbers
exactly on all four earlier recordings (35/0/24.9, 48/0/23.3, 126/5/36.4,
34/0/29.4), so this is a generalisation rather than a change of behaviour. On
`sp5xoc.wav` it locks 827.7 Hz from a 610 Hz pitch at confidence 0.95, and the
tracked speed reads a plausible 20.8 wpm where the too-narrow window produced
40.0.

**Still open on this recording:** we did not get the callsign, and neither did
Colin's ear. 237 characters with real fragments in them is not yet a transcript
an operator would work a station from.

#### 4.11f SM5OMP, 2026-08-26 - what tone tracking is worth, and a theory that died the same hour

The best signal of the session, and the cleanest experiment in it.

`bench/strong23.wav`: 22 s trimmed out of a 180 s capture (the sender stopped at
00:22 and the rest is noise floor at -35 dB). 14,040.900, CW, 500 Hz filter,
610 Hz pitch. Strong - the tone sits 15-19 dB above the floor for the whole
clip, the element decoder stays locked throughout, 0 junk runs, speed tracked
steady at 15 wpm. Colin described the audio as really good copy. **The MkII's
own decoder printed very little of it.**

The tone is at **538 Hz**, 72 Hz below the configured pitch. Same tone, to
within a Hz, as `bench/ft-14040-90.wav` recorded fifteen minutes earlier: the
same operator, working someone else.

Three runs over the same 22 seconds, changing nothing but where the detector
looks:

| detector | transcript |
|---|---|
| tracking, as shipped | ` C -H GI3UBADE S M 5 O (` |
| pinned to the 610 Hz pitch | ` SI IE E S 5I EIEEHEI ESIIIIE SE H I EIEIH E` |
| pinned to 538 Hz, where the station is | ` C -H GI3UBADE S M 5 O (` |

Pinning to the pitch converts a readable transcript into exactly the class of
output Colin calls garbage - single-element characters, `E I S H T`, the same
shape as section 4.11's noise junk. It is not a weak signal, it is a signal the
detector is not pointed at. 72 Hz is a fifth of the way to the filter skirt and
it is already fatal.

**What this does and does not establish.** It establishes that tone tracking is
worth having, measured on a real signal: it is the difference between the two
rows above, in our own decoder, with everything else held constant. It does
*not* establish that the MkII fails for the same reason - the MkII's decoder is
a black box and nobody here has read its internals. The hypothesis is that it
decodes at the pitch because that is what the pitch is for, and that a station
72 Hz off is off its detector in the same way it was off ours. **That
hypothesis is testable live and has not been tested**: the IC-7300 MkII has CW
auto-tuning, which zero-beats the signal onto the pitch. Next strong off-pitch
signal, decode it first, then auto-tune, then decode again. If the radio starts
printing, the hypothesis holds.

**Amended the same hour: the hypothesis failed its first test.**
`bench/qso2-clip.wav` is the same operator eleven minutes later, tone
**528.9 Hz** - 81 Hz off the pitch, *further* off than `strong23.wav` - and the
MkII decoded that one well. Colin relayed off its screen:

| decoder | transcript |
|---|---|
| MkII (relayed off the screen) | `all info g ood rig fb my best 73^ar gi3uba de o m 5` then garbage |
| Core | ` T ALL IN EGO  E MYEST 73+ GI3UBADEE S M5O 5 EJ <SK> EE   DE S M 5 OS M 5` |

Same station, same offset, opposite outcome. Whatever silenced the radio on
`strong23.wav`, it was not the 72 Hz. The pinning experiment above is untouched
by this - it is our decoder against itself with everything else held constant,
and it says tone tracking earns its place - but the extrapolation to the MkII
does not survive contact with the next recording. The auto-tune experiment in
section 4.12 is still worth running; it is no longer an explanation waiting to
be confirmed.

The head-to-head above is also the closest thing to a fair comparison in
section 4 so far - one over, both decoders on it - and it is still not fair:
the MkII text is what Colin read off a screen, the windows are not guaranteed
identical, and neither column is ground truth. What it does show is where each
one is strong. The radio reads the plain language better (`all info good rig fb`
against our `ALL IN EGO`); we get further into the callsigns (`GI3UBA DE SM5O`
against its `gi3uba de o m 5`); and both stop short of the full suffix. The
agreements - `MY BEST 73`, `<AR>`, `GI3UBA DE` - are worth more than either
column alone.

This is also the first thing in section 4 with a check outside the two
decoders. Both recordings, decoded independently, produce `SM5O` next to `HEJ`
(Swedish) and, here, `GI3UBA` (Northern Ireland) in front of a `DE`. Two
well-formed callsigns and a plain-language word in the right language is not
ground truth, but it is evidence of a kind section 4.11e says we have never
had. `bench/fldigi/strong23-8k.wav` is cut and waiting for the Fldigi run.

**A second fault is visible in the good transcript - and Fldigi has since cut
it in half.** The first draft of this paragraph said `GI3UBADE` should be
`GI3UBA DE` and `S M 5 O` should be `SM5O`, both directions wrong, all of it
ours. Fldigi over the same wav (section 4.11g) says otherwise:

| | |
|---|---|
| Fldigi | `* 2C -HI GI 3UBA DE S M 5 O* <KN>` |
| Core | ` C -H GI3UBADE S M 5 O (` |

Fldigi splits `S M 5 O` in exactly the same places we do. Two decoders sharing
an error that specific are not both mis-thresholding; the operator is sending
his own call with inter-character gaps wide enough to read as word gaps. That
half of the claim is withdrawn.

The other half stands and is now better evidenced: Fldigi separated `DE` and we
ran it into the callsign. `GI3UBADE` is ours alone. So the inter-word gap in
`CwElementDecoder` does have a fault, and the bench case for it is the joined
`DE`, not the split call.

Also withdrawn: the trailing `(` was never a stray. `(` and `<KN>` are the same
Morse, `-.--.`. Fldigi renders the prosign, we render the character. Identical
copy.

#### 4.11g Fldigi, 2026-08-26 - section 4 finally gets a reference

Section 4.11e's complaint was that every comparison in section 4 scored the
Core decoder against the MkII's screen, which cannot show either of them
correct. This is the fix.

**Method, and it is repeatable without touching the live setup.** Fldigi 4.2.11
runs against `C:\Users\colin\fldigi-bench.files`, a throwaway copy of the real
config with `AUDIOIO` set to 3 (**File I/O only**), mode CW, carrier 500 Hz -
so the operating Fldigi, its sound card and its rig control cannot be disturbed
by any of it. `bench/fldigi/fldigi-bench.cmd` launches it. Recordings are
resampled to 8 kHz mono, Fldigi's native rate, same audio and same level.
Playback is **File > Audio > Playback...**, the one step that needs a human,
because Fldigi exposes no wav decode on the command line and none over
XML-RPC. Everything else is driven over XML-RPC on 127.0.0.1:7362:
`modem.set_by_name("CW")`, `modem.set_carrier`, `main.set_squelch(False)`, and
`text.get_rx` polled into `bench/fldigi/rx-capture.log`.

**`strong23.wav` - 22 s, strong, one station.**

| | |
|---|---|
| Fldigi | `* 2C -HI GI 3UBA DE S M 5 O* <KN>` |
| Core | ` C -H GI3UBADE S M 5 O (` |
| MkII | printed almost nothing |

Near-identical copy. Two corrections to earlier sections came out of it, both
in 4.11f: the trailing `(` was never a stray character (`(` and `<KN>` are the
same Morse, `-.--.`), and the `S M 5 O` split is the sender's fist, not our
threshold, because Fldigi splits it in the same places. What survives as ours
is `GI3UBADE`, where Fldigi separated the `DE`.

**`f-14028900.wav` - 46 s, 14.0289, contest traffic.**

| | |
|---|---|
| Fldigi | `K E7AUP UP CHRIS SP D M V ID 3356 CT7AUP TU SN5N CWT SN5N 6T CWT SN5N 6T CWT SN5N 6T CWT SN5N 6T` |
| Core | ` <CT> H EAUP JUPCHRISSP DAVID 3356 AUPTUSN5N CWTSN5N- CWTSN5N- CWTSN5N- CWT SN5N-` |

Agreement on `CWT SN5N` four times, on `CT7AUP`, `TU`, `CHRIS` and `3356`. One
clear win for the Core decoder: **`DAVID 3356` against Fldigi's `D M V ID
3356`** - and it is corroborated, because `qso4`, a different recording on a
different frequency twenty minutes earlier, has the same operator's number in
it. This is the first claim of the kind in section 4 that rests on something
other than our own output.

**What is now established, and not by us.** The contest is **CWT**, the CWops
Mini-Test - Wednesdays, and these recordings were made just after 13:00 UTC on
a Wednesday. That fixes the exchange format as name plus CWops member number,
which is exactly the shape of everything section 4.11f and `qso4` pulled out of
the noise: `DAN 1854`, `KJELL 3865`, `DAVID 3356`, `CHRIS`. **SM5OMP** is
confirmed by two decoders on two recordings, **SN5N** and **CT7AUP** by two
decoders on one. None of this came from our decoder alone.

Still open from this session and now decidable: the MkII read `DAN 1584` where
we read `DAN 1854`, five times. CWops member numbers are published, so this one
is checkable offline against the roster.

**`qso4.wav` - 5 min, 14.0409, several stations interleaved.** This one was run
to settle a disagreement. Signal browser channel ~700 Hz:

```
T DTWETETA DE0KDAN 1854 TU E T TTST SM5IMO TEST SM5IMO E EE E T IE T NEE T G
IN T I I A SA E A TM 5JO E EI E TEST SM5IMO TEST SM5IMO TEST NM5IMO SM7T SM7T
DAN 1854 KJELL 3865 TU SM5IMO TEST SM5IMO TEST S
```

**`DAN 1854`.** We read `DAN 1854` five times; the MkII's screen read
`DAN 1584` five times; the third decoder, with no stake in either, reads
`DAN 1854` five times over, plus a sixth garbled instance that is reachable
from `1854` and unreachable from `1584` (below). Two of three, and the
internal evidence favours ours. The CWops roster still settles it absolutely
and that check is cheap. `KJELL 3865` is character-for-character ours, and
`TEST SM5IMO` five times matches what the MkII showed. On this file all three
decoders agree on every callsign and every exchange number.

Further down the same channel:

```
T SM5IMO RU0LA E RUT E TLL E E -TMA N 5 E RU0LL DAN 18II4 AI E T Q V AI91 EU E
T ET I <KN>1YBN DAN 1854 E E T E TK Z E E E NOBK DAN 1854 E E E E E T E GE T
M6N MAUP SN SN5 N DAN 1854 CHRIS SP TU TU T5IMO C IAUP
```

Five clean `DAN 1854` in all, and one garbled `DAN 18II4` which is the strongest
single piece of evidence in the file. `5` is `.....`; drop one dot and insert
one gap and it becomes `..` `..`, which is `II`. So `1854` -> `18II4` is a
one-fault degradation. `1584` cannot reach `18II4` at all: corrupting the `5`
in second position yields `1II84`, putting the `8` **after** the damage. Fldigi
puts the `8` before it. The bad instance excludes the alternative more firmly
than the five good ones confirm it.

**The two recordings are linked.** `SN5N`, `CT7AUP` and `CHRIS SP` all appear in
both `qso4` (14.0409) and `f-14028900` (14.0289) - twenty minutes and 12 kHz
apart - in the reference decoder's output, not just ours. Operators working up
and down the band inside one contest period, which is what a CWT is. The
identification no longer rests on our decode at all. `RU0LA`/`RU0LL` is a new
station, Asiatic Russian prefix.

Two findings here outrank the callsigns.

**The inter-word gap is not our defect alone.** Fldigi printed `DE0KDAN 1854` -
`DE0K` and `DAN` welded together, the same failure section 4.11f logged against
us as `GI3UBADE`. It should still be fixed, but it stops being a bug report
against our decoder and becomes an attempt to beat the reference. Different
task, different standard of proof.

**Tracking versus pinning - a difference of degree, not of kind.** The main
pane ran AFC on from a 675 Hz start; the signal browser's ~700 Hz channel was
fixed. Both decoded the same station. The pinned channel gave much the cleaner
copy; the tracking pane's full text (from `bench/fldigi/qso4-rx.log`, not the
visible pane, which shows only its tail) was:

```
NAUP <VE> N5EDAN 1854 EI TU<BT>T5IMO CTIUP CT7AUP DAN1854 AVIDE3356 X SM5IMO
*HTSM5MO SS
```

`CT7AUP` clean, `DAN 1854` twice, `DAVID 3356` and `SM5IMO` recoverable. So
tracking was not defeated here - it returned the same callsigns at a far worse
signal-to-junk ratio and later in the file. Set against 4.11f, where tracking
beat a pinned pitch outright, the pattern is that tracking costs accuracy when
competing signals are near enough to pull it about, and buys copy when the
target drifts alone. Neither is right unconditionally, and `CwDecoderEngine`
has no way at present to tell which regime it is in. That, rather than a
choice between the two, is the thing worth building.

**Count.** `DAN 1854` now stands at seven clean instances across two
independent Fldigi decoders plus our own five, against `1584` on the MkII
screen. `DAVID 3356` also appears in `qso4`, making four stations - SN5N,
CT7AUP, CHRIS SP, DAVID 3356 - common to both recordings, and confirming our
own `qso4` read of it.

**One observation for section 4.13.** Fldigi's signal browser - the "all the
signals, one to a line" display - was running throughout. On both files exactly
one channel row carried the copy and every other row carried
`EE TEEET EE EJ TMN AET`, single-element junk of the same kind section 4.11
spent so long on. Channelising the passband does not decode more stations for
free; it decodes the one that is there and prints noise on the rest. Whatever
4.13 builds needs a per-channel presence gate before it is worth showing
anyone, and that gate is the unsolved problem, not the channelising.

#### 4.11h DK9PY and I1YRL, 2026-08-26 - the first easy signal, and what tracking costs on it

Two recordings made from Colin's live reports at the end of the CWT hour, one
during and one after. They are the first bench material where the answer is not
in doubt, and between them they overturn a working assumption.

| file | freq | length | radio state | what it is |
|---|---|---|---|---|
| `bench/mkii-dk9py.wav` | 14,027.100 | 180 s | CW, 500 Hz, pitch 610 | DK9PY running a CWT. Colin reported the MkII printing it **perfectly**. |
| `bench/mkii-i1yrl.wav` | 14,045.100 | 240 s | CW, 500 Hz, pitch 610 | I1YRL, recorded after 14:00 UTC - an ordinary QSO, not contest traffic. |

Both have sidecar `.txt` files carrying the CI-V read taken at the start, so the
radio state is recorded rather than remembered.

**Why they matter.** Every recording before these is marginal: all three
decoders disagree and none can be shown right. A signal the radio copies
perfectly tests the opposite way round - it can show *our* decoder failing on
easy copy, which no marginal file can.

##### DK9PY: the signal was 115 Hz from where the operator was listening

Run as configured - pitch 610 Hz, search window from the 500 Hz filter:

```
dotnet run --project tools/CwBench -c Release -- bench/mkii-dk9py.wav --pitch 610 --filter 500
```

```
 1 AC1D ARMIN 2T62 G I C EDE 5I EHEHSSE EIE EII I III E5EFQ CWT DK9PY RVM
 EN4PVM ARMIN 2T62 UEE ERQ CWT DK9PY ZA E ZA1EM ARMIN 2T62 E E F 3T6 X RQ
 CWT E I2WIJ E EFQ CWT DK9PY I2WIJ RQ CWT DK9PY I?WIJE EE 2 E EE2WIJ ARMIN
 2T62 BOB 24T6 EEU EFQ N EQ CWT DK9PY EQ E
```

262 characters, the callsigns mostly there, and shot through with junk. Against
a radio that printed the same over cleanly, that is a plain failure on easy
copy - the first one section 4 has been able to demonstrate.

`--spectrum` says why:

```
strongest    725 Hz   -29.4 dB
most keyed   725 Hz   keying ratio 10.3
flatness     0.04

   725   -29.4 dB    10.3  ##############################
   750   -29.7 dB    10.1  ############################
   700   -30.1 dB     8.6  ###########################
   775   -31.3 dB     7.8  #######################
   675   -32.0 dB     5.6  ######################
   800   -34.1 dB     4.7  #################
   650   -35.0 dB     2.9  ###############
   600   -36.0 dB     2.3  #############
```

One keyed tone, unambiguous - keying ratio 10.3 at 725 Hz against 2.3 at the
operator's own 610 Hz pitch. **DK9PY was 115 Hz off zero-beat**, and the bins
around 610 hold nothing but noise.

So the search window earned its keep for the first time on a real recording.
`SearchWindowForFilterWidth(500)` gives +/-250 Hz, which reaches 725; the old
fixed +/-250 would have too, but a narrower filter would not have, and this is
the mechanism section 4.11b measured and section 4.10 claimed as our advantage
over the radio.

##### And then the tracker threw it away

The per-sample tone readout alternates across the file - 640, 732, 651, 730,
674, 731, 652 - between the real tone near 730 and an excursion near 650 that
is not a station at all. Pinning proves which is which:

| run | chars | runs >=4 | confidence | transcript |
|---|---|---|---|---|
| tracking, pitch 610, filter 500 | 262 | 0 | **0.23** | readable, corrupted |
| `--no-track --pitch 725` | 219 | 0 | 1.00 | **near-clean** |
| `--no-track --pitch 650` | 592 | 5 (longest 7) | 1.00 | pure junk |
| `--no-track --pitch 610 --search 100` | 290 | 1 | 1.00 | pure junk |

Pinned on the actual tone:

```
 EE ARMIN 2T62 G I C EDE ESE IEE HE E CQ CWT DK9PY PVM G4PVM ARMIN 2T62 TU
 EE CQ CWT DK9PY ZA E ZA1EM ARMIN 2T62 TU CQ CWT E I2WIJ CQ CWT DK9PY I2WIJ
 CQ CWT DK9PY I?WI I 2 I2WIJ ARMIN 2T62 BOB 24T6 TU CQ N CQ CWT DK9PY CQ
```

That is a CWT run, readable end to end: `CQ CWT DK9PY` repeated, callers
answered, `ARMIN 2T62` sent to each, `TU`. Set against the tracking run,
**every `CQ` had been corrupted into `EFQ`, `ERQ` or `RQ`**, and `TU` into
`UEE`. Fewer characters, far more of them right.

The 650 Hz row is the important control. Pinning is not what helped: pinning on
the *wrong* tone is the worst run in the table. What helped was holding still on
the right one.

##### This reconciles 4.11f, and the fix is neither tracking nor pinning

Section 4.11f found tracking beating a pinned pitch outright; this finds pinning
beating tracking by a wide margin. Both are true and they are not in conflict:

- In 4.11f the target **drifted**, and a pinned detector was left behind.
- Here the target **did not move**, and re-picking the spectral peak every frame
  let noise pull the estimate off a perfectly steady tone.

The tracker has no memory. It answers "where is the strongest keyed bin right
now", and during an inter-character gap the honest answer is "nowhere", so it
wanders. What both cases want is the same thing: **acquire wide, then hold** -
use the filter-derived search window to find the tone, then follow it with a
rate limit of a few Hz per second and a requirement that the new peak actually
be keyed. That tracks a drifting sender and ignores a gap, which is exactly the
split between 4.11f and this section.

This is now the most valuable single change available to the decoder, ahead of
the word-gap work, because it is worth whole words rather than spaces.

##### Confidence is not merely uninformative, it is inverted

The table above is the sharpest instance yet. Confidence reports **1.00 on 592
characters of junk** and **0.23 on the best copy of the day**. It is not a weak
signal, it is an anti-signal, and 4.11d's note that it cannot be used as a gate
should be read as the stronger statement: anything gated on it would suppress
good copy and pass rubbish. This is why the UI panel deliberately shows
everything and reports signal presence, SNR and lock instead.

##### Cut numbers: `2T62` is not a defect

`ARMIN 2T62` appears five times in the tracking run and three in the pinned one,
and `BOB 24T6` once in each. These are **cut numbers**, the contest abbreviation
where a shortened Morse character stands in for a digit:

| sent | is | why |
|---|---|---|
| `T` | 0 | `-` instead of `-----` |
| `N` | 9 | `-.` instead of `-----.` |
| `A` | 1 | `.-` instead of `.----` |

So `2T62` is CWops member **2062** and `24T6` is **2406**. The decoder is
transcribing correctly and must go on doing so: anything that "corrects" a `T`
to a `0` would be wrong the moment a `T` is a `T`, and would silently corrupt
callsigns. If cut numbers are ever expanded it belongs in a display layer that
can be turned off, next to the raw text, and never in `CwElementDecoder`.

This also explains a run of earlier decodes. `5NN` for `599` in section 4.11g
and in the I1YRL file below is the same convention, not a decoder fault.

##### The speed tracker still rails on quiet

Both DK9PY runs end at 46.2 wpm (tracking) and 53.5 wpm (pinned at 610), reached
during eight consecutive `quiet` samples with no signal present - and the
readout still says `locked` throughout. The warm-up and training threshold from
4.11d.2 stopped the *output* runs (`runs >=4` is 0 on the good runs), but the
speed estimate itself is still free to climb on noise, and still advertises
itself as locked while doing it.

That matters now in a way it did not on the bench, because the UI panel shows
`wpm` and shows `locked`. A reader that says "46 wpm, locked" at a dead band is
worse than one that says nothing. The estimate should hold its last value, and
`IsLocked` should go false, once `SignalPresent` has been false for a second.

##### I1YRL: the first plain QSO, and it is the best copy we have

Recorded after the contest ended, so no exchanges and no repetition to average
over. Tone at 625 Hz against a 610 Hz pitch - 15 Hz, effectively zero-beat, the
opposite of the DK9PY case:

```
strongest    625 Hz   -27.9 dB
most keyed   625 Hz   keying ratio 8.8
flatness     0.03
```

Tracking, as configured:

```
 II4DOEI 4DODE I1L GA TFORCALLURRST5995NN5 U BFB QTHNRTIN NRTA TURIN=NAME LUC
 LUC LUC E I 4DO DEI1YRLD K SKBKE I 5 N N E I4DODEI1YRLFBOKBILL TKSFORINFOAND
 O MYINFOWITHPHO TOSINQRZ.COM=HOAI PECUAGNBILL 73 DE I1YRL TUEE GLTUEE
 CQCQCQDE I1YRL I1YRL I1YRL I16EEHIISI5SHQCQCT
```

The QSO reads: *II4DO de I1YRL, GA [good afternoon], TNX for call, ur RST 599,
QTH Torino, name Luc* ... *FB OK Bill, TKS for info, and [here is] my info with
photos in QRZ.COM, hope CU agn Bill, 73 de I1YRL, TU, GL* and then a closing CQ.
That is readable copy of a conversation, which is a first.

Pinned at 625 Hz gives 275 characters against tracking's 280 and the same text
to within two characters (`54DOEI` for `II4DOEI`, `6KBKE` for `SKBKE`). **When
the signal is near zero-beat and alone, tracking and pinning agree** - tracking
only costs anything when there is somewhere wrong for it to go. Third
confirmation of the acquire-then-hold argument above.

Three things to take from it:

- **The errors are spacing, not letters.** `QTHNRTIN NRTA TURIN`,
  `TKSFORINFOAND`, `WITHPHO TOSIN`, `CQCQCQDE`. Almost every fault above is a
  missing or spurious word gap with the characters themselves correct. On a
  plain QSO this is far more damaging than it was on contest traffic, where the
  tokens are short and the reader knows the format. It promotes the word-gap
  item in 4.12 from cosmetic to real - though still behind the tone-hold fix,
  which costs whole words.
- **Repetition is what carried the contest files.** `LUC LUC LUC`, `I1YRL I1YRL
  I1YRL` and the three-times `Torino` are the only reason those tokens are
  certain. Strip the repetition and the same decoder gives one shot per word.
  Every accuracy claim drawn from contest recordings is flattered by this.
- **The tail is the signature failure.** `I16EEHIISI5SHQCQCT` - clean copy right
  up to the point the signal fades, then plausible-looking rubbish with nothing
  marking the join. This is the case the panel's status line exists for, and the
  case no amount of decoder work removes.

##### One caveat entered against section 4.10

Section 4.10 is titled *"The MkII's decoder needs zero-beat. Ours does not."*
Colin reported the MkII printing DK9PY perfectly, and the recording shows that
signal sitting 115 Hz from the radio's own CW pitch. That is a data point
against 4.10, from the radio's screen rather than from anyone's ear.

It does not overturn it - 4.10a already records that 4.10 is n=1 and that its
reproduction failed - but 4.10 should not be cited as settled. The honest
position is that we have one recording where the MkII wanted zero-beat and one
where it did not, and no controlled test of either.

### 4.13 Multi-signal: one decoder, many stations

Raised by Colin 2026-08-26, watching Fldigi put every signal in the passband on
its own line.

**We decode exactly one signal** - the strongest tone inside the search window -
and we do it silently. If a second station is louder for part of an over, the
tracker walks to it and the transcript becomes a splice of two QSOs with nothing
marking the join. At a 500 Hz filter this rarely bites: `sp5xoc.wav` holds one
keyed tone (825-900 Hz, keying ratio 7.7-9.7, everything else at the 2.5 noise
level). At 2.4 kHz it would bite immediately.

The architecture is well placed for the fix, which is why this is worth writing
down now rather than discovering later:

- `CwToneDetector` and `CwElementDecoder` are already per-tone objects with no
  shared state. One instance per signal is their natural use.
- The 1024-point FFT already has the whole passband and currently only picks its
  peak. Peak-picking it for several tones separated by roughly 60 Hz, each
  passing a keying-ratio test to reject carriers and steady noise, gives the
  channel list.
- Each channel then runs pinned (`TrackPitch = false`) on its own tone, with its
  own element decoder and its own speed estimate. That last point is not a
  detail: two operators at 18 and 28 wpm are exactly what wrecks a single shared
  tracker today.
- Channels age out when their tone stops keying and appear when one starts. The
  envelope path is 80 samples per 5 ms hop, so eight channels cost nothing and
  the FFT is shared.

This changes the output contract from "a transcript" to "a transcript per
channel", so it reaches the UI as well as the core. It is a Phase 3 item, not a
tweak to Phase 2.

### 4.15 The decoder reaches the application, 2026-08-26

Until this session the decoder had no caller. Core had `CwDecoderEngine`, the
tests exercised it, `tools/CwBench` fed it wav files - and neither application
ever handed it a sample. `SearchWindowForFilterWidth`, measured in 4.11b and
committed in `4eb9416`, was dead code outside the bench.

Section 4.12 asked to "wire the IF filter width through to the decoder". That
turned out to be three problems wearing one coat:

- IWC reads `1A 03` into `IfWidthA` and has **no CW decoder at all** to wire it
  to. Nothing to do there yet beyond the core port.
- YWC has no application-side consumer of the decoder either, so there was
  nothing for the width to arrive at.
- The Yaesu SH code to Hz mapping existed **only in JavaScript**, in
  `wwwroot/js/ui/if-width-tables.js`, and it is mode-dependent - code 8 is
  1650 Hz in SSB and 400 Hz in CW. A server-side decoder cannot reach it.

So the order was: port the table, build a consumer, then wire the width. Colin
chose the full slice through to the UI rather than stopping at the service.

#### What was built

| file | what it is |
|---|---|
| `Services/YaesuIfWidth.cs` | The SH code to Hz tables in C#. `HzFor(model, mode, code)` returns `int?`. |
| `Tests/Yaesu_Web_Control.Tests/YaesuIfWidthTests.cs` | 14 tests. |
| `Services/Audio/RadioAudioBridgeService.cs` | `RxFrameCaptured` event, raised on the capture path. |
| `Services/Cw/BridgeCwAudioSource.cs` | `ICwAudioSource` over that event. |
| `Services/Cw/CwReaderService.cs` | The decoder's owner: builds it from the radio's pitch and filter, rebuilds it when either moves, holds the text. |
| `Controllers/CwController.cs` | `POST start`, `POST stop`, `POST clear`, `GET poll?since=N`. |
| `core/js/cw/cw-reader-panel.js` | The panel. In the core, so IWC gets it (2.1). |
| `Pages/Index.cshtml`, `Program.cs` | Toolbar button, dialog, module import, DI. |

Four decisions in there are worth the ink, because each had a cheaper option
that would have been wrong.

**The table is duplicated, and a test stops it drifting.** Two copies of a
lookup table is normally a refactor waiting to happen, and the refactor here -
serve the JS from the C#, or generate one from the other - is real work for a
static table that changes when Yaesu ship a radio. Instead
`YaesuIfWidthTests.cs` *parses* `if-width-tables.js` and compares every model,
mode and code 0-40 against the C# copy. The duplication stays; silent divergence
cannot. If someone edits the JS the build goes red and names the cell.

This also keeps the table out of `core/`. Core is radio-agnostic by design -
IWC's filter codes are a formula (`0-9` is `(c+1)*50`, `>=10` is
`600+(c-10)*100`), not a table, and it computes them itself.

**Code 0 deliberately returns null.** It is the radio's own mode-dependent
default and the radio does not report what it resolved to. AM and FM return null
too. `CwReaderService` then keeps the decoder's default search window rather
than being handed a guess, and the panel prints `filter unknown` instead of a
number - because a wrong width silently narrows the window onto the wrong part
of the passband, and 4.11h is a demonstration of what that costs.

**The decoder taps the existing capture; it does not open a device.** A second
PortAudio stream on the same USB codec either fails or fights the first for
buffers, and the operator listening to the radio is the one who would notice.
The consequence is that the reader only hears anything while an audio session is
live, so the snapshot carries `AudioDevicesOpen` and the panel says so in
words - otherwise the panel looks healthy and prints nothing, which is the worst
of both.

Frames cross from the PortAudio callback thread to the decoder through a bounded
channel with `FullMode = DropOldest`, capacity 100 - one second. The callback
does `TryWrite` and nothing else. If the decoder ever falls behind, the channel
drops the oldest frame rather than stalling the audio thread: dropped CW audio
is a few lost characters, a blocked audio callback is a click in the operator's
headphones. Dropped frames are counted and shown.

**Polling, not a WebSocket.** Decoded CW arrives a few characters at a time at a
handful of characters a second. A poll every 500 ms reads as live, costs
nothing, and needs none of the reconnection handling a socket does. Each poll
sends back the cursor from the previous reply and receives only what is new; the
server keeps 8000 characters and reports `Truncated` if the client's cursor has
fallen off the back, so the panel can mark the join rather than splicing
non-contiguous text silently.

#### What the panel shows, and why it shows all of it

The output pane prints everything the decoder produces, ungated. That is a
decision, not an omission: 4.11h measured confidence at **1.00 on 592 characters
of junk and 0.23 on the best copy of the day**, so any gate built on it would
suppress good copy and pass rubbish.

Instead the status line carries the readouts that *are* informative - signal
present, tone, pitch, filter width, search window, wpm, lock, SNR, dropped
frames - and the panel says in as many words that on a strong clean signal the
decoder is close to perfect and on a marginal one it prints plausible-looking
rubbish that looks no different. The operator is given the means to tell them
apart rather than a transcript that pretends the difference does not exist.

The dialog is non-modal and draggable, like DX Spots: the point of reading CW is
to work the station, and a modal would lock the operator out of the VFO and the
keyer while the text was on screen. Closing it stops the polling but leaves the
decoder running, so closing the panel mid-QSO by accident does not throw the
copy away.

#### Not yet tested against a radio

It builds, the tests pass, the JS parses. No part of it has seen an antenna.
That is item 2 of the running order in 4.14.

### 4.14 Where to pick this up

Written at the end of the 2026-08-26 session, cold-start notes. Section 4.12 is
the backlog; this is the running order and the state to resume from.

#### What is committed, and what is not

Branch `feature/cw-reader`. The bench and core work of 2026-08-26 went in as two
commits; the application slice that followed is still loose in the tree.

| commit | what |
|---|---|
| `4eb9416` | **core** - `CwToneDetector` warm-up and `NoiseLevel`; `CwElementDecoder` `MinTrainNoiseMultiple` 3.5; `CwDecoderEngine.SearchWindowForFilterWidth`; the tests for all three, plus `CwSignalGenerator.LeadIn()`. 75 tests pass. |
| `7416bb3` | **bench** - `tools/CwBench` gains `--marks`, `--warmup`, `--train-noise`, `--no-resync`, `--filter`, `runs >=4`; plan sections 4.11d.2, 4.11e, 4.11f, 4.11g, 4.13, 4.14. |

Nothing is pushed.

`4eb9416` touches `core/`, so it owes a trip back to `Radio_Web_Control_Core`
and forward into IWC. IWC has no CW core at all yet, so that is a port rather
than a merge. See below - the trip back was rehearsed on 2026-08-26 and not made.

**Uncommitted: the whole application slice** (see 4.15). Eight files, four of
them new, plus the plan sections above this one:

| file | change |
|---|---|
| `Services/YaesuIfWidth.cs` | new - C# port of the SH code to Hz tables |
| `Tests/Yaesu_Web_Control.Tests/` | new - 14 tests, holds the port to the JS |
| `Services/Audio/RadioAudioBridgeService.cs` | `RxFrameCaptured` event on the capture path |
| `Services/Cw/BridgeCwAudioSource.cs` | new - `ICwAudioSource` over that event |
| `Services/Cw/CwReaderService.cs` | new - the caller `SearchWindowForFilterWidth` never had |
| `Controllers/CwController.cs` | new - start/stop/clear/poll |
| `core/js/cw/cw-reader-panel.js` | new - the panel, in the core per 2.1 |
| `Pages/Index.cshtml`, `Program.cs`, `Yaesu_Web_Control.csproj` | wiring |

Builds clean, 14 tests pass, panel JS parses. **Never yet run against a radio.**

#### The core work has never gone back to the core, and today it was proved it can

Every CW file in `core/` - all six services and all five test files - exists in
exactly one place: this branch, in YWC's subtree copy. `Radio_Web_Control_Core`
does not have them on `main` or on its own (empty) `feature/cw-reader`, and IWC
does not have them on any of its seven branches. There is no second copy of any
of it.

That is what section 2.1 prescribes - author inside `core/` in whichever app you
are in, push up afterwards - and the phase table schedules the push as row 4. So
it is the plan working, not a slip. But the push half of that loop had never once
been run: every subtree commit in YWC's history is a pull ("Squashed 'core/'
changes from X..Y"), and an unexercised mechanism in a plan is a guess.

It was exercised on 2026-08-26, locally and reversibly, with no GitHub involved:

```
git subtree split --prefix=core -b tmp/core-split      # in YWC
git fetch <ywc> tmp/core-split:tmp/ywc-core-split      # in the core clone
git merge-tree --write-tree main tmp/ywc-core-split
```

Four things came out of it.

- **The split is slow enough to look broken.** It walks all 1033 commits of YWC
  history and took over two minutes, silently. It is not hung. Give it a long
  timeout.
- **It applies clean:** 11 files, 1756 insertions, nothing modified, no conflicts.
  Built and tested standalone in a throwaway worktree - **75 tests pass in the
  core repo on its own**, which is the thing that actually needed proving, since
  the core targets `net10.0` while YWC builds `net10.0-windows` as well.
- **It will not fast-forward.** The split forks from `c91dabc`; core `main` is at
  `765619f`, one commit ahead (the `Backup*.ps1` gitignore rule). A plain
  `git subtree push` is rejected non-fast-forward. Pull the core into YWC's
  subtree first, then re-split. Worth knowing before the first real push rather
  than during it.
- **No line-ending damage.** The incoming files are LF, matching the core. The
  core still has no `.gitattributes` of its own, which is a hazard for whoever
  clones it directly rather than through a subtree, but it did not bite here.

The branches `tmp/core-split` (YWC) and `tmp/ywc-core-split` (core) are the dry
run's leftovers and are safe to delete; regenerating them is one command.

**The push itself has not been done.** Nothing is on GitHub.

#### The reader panel was in the wrong place, and is now in the right one

Section 2.1 puts the shared reader UI at `core/js/cw/cw-reader-panel.js`. It was
first written to `wwwroot/js/ui/cw-reader-panel.js`, which builds and runs
identically in YWC and would have given IWC nothing. Moved on 2026-08-26:

| | |
|---|---|
| file | `core/js/cw/cw-reader-panel.js` |
| import in `Index.cshtml` | `/js/cw/cw-reader-panel.js` |
| `.gitignore` | `/wwwroot/js/cw/cw-reader-panel.js` - generated, per-file, beside the calibration engine |

No csproj change was needed: `CopySharedCoreJs` globs `core\js\**\*.js` and
preserves the subdirectory, so the file arrives at `wwwroot/js/cw/` in both apps
by itself. That is section 2.1's claim about the target, and this is the first
time anything has tested it. Verified by deleting the generated copy and
rebuilding - it comes back, and it is ignored.

#### The running order

1. **Acquire wide, then hold the tone (4.11h).** Newly the top item, and by
   some distance. The DK9PY recording shows the tracker finding a signal
   115 Hz off pitch and then corrupting every `CQ` in the file by re-picking
   the peak during inter-character gaps; pinned on the same tone the over is
   readable end to end. This is worth whole words, where the word-gap item is
   worth spaces. Wanted: keep the filter-derived search window for acquisition,
   then rate-limit the tone estimate to a few Hz per second and require the new
   peak to be keyed. Reproduce with:

   `dotnet run --project tools/CwBench -c Release -- bench/mkii-dk9py.wav --pitch 610 --filter 500`

   and compare against `--no-track --pitch 725`.

2. **Bench the panel against the radio (4.15).** The whole vertical slice is
   written and has never seen an antenna. It needs remote audio running, since
   the decoder taps the bridge rather than opening its own stream. Check in
   particular that the search window shown in the status line matches the
   filter actually set, and that changing the filter or the CW pitch on the
   radio rebuilds the decoder without losing the text.

3. **Speed and lock must not rail on quiet (4.11h).** `wpm` climbing to 46-60
   on eight consecutive silent samples while still reporting `locked` was
   tolerable on the bench and is not, now that the panel puts both on screen.
   Hold the last estimate and drop `IsLocked` after a second of
   `SignalPresent == false`.

4. **Word spacing (4.11f, 4.11g, 4.11h).** Promoted from cosmetic by the I1YRL
   recording, where nearly every error in otherwise correct copy is a missing
   or spurious word gap - `TKSFORINFOAND`, `CQCQCQDE`. Still behind items 1-3.
   Reproduce with:

   `dotnet run --project tools/CwBench -c Release -- bench/mkii-i1yrl.wav --pitch 625 --no-track`

5. **Why the MkII went quiet on `strong23.wav`.** The offset theory is dead
   (4.11f), and 4.11h now has the MkII copying a signal 115 Hz off pitch
   perfectly, which weakens 4.10 as well. Run the auto-tune experiment anyway,
   and look at the radio's own CW decode threshold setting while doing it.

6. **The 3.5-vs-4.0 `MinTrainNoiseMultiple` question (4.11d.2).** Needs more
   weak-signal captures than we have. `bench/mkii-nodecode.wav` is the one
   recording that separates them.

7. **Check the CWops roster.** `ARMIN 2062` (DK9PY), `BOB 2406`, `DAN 1854`.
   Offline, no radio needed, and it would turn three plausible decodes into
   three confirmed ones - including the `1854`-versus-`1584` argument in 4.11g,
   which is currently settled on internal evidence alone.

8. **Push the core up, then into IWC.** Rehearsed and proved above; not done.
   Order: pull core `main` into YWC's subtree (to clear the non-fast-forward),
   commit the slice, `git subtree split`, `git subtree push --prefix=core`, then
   pull into IWC. Needs Colin's word, because it puts code on GitHub. Until it
   happens the CW core exists in one branch of one repo with no second copy, and
   IWC cannot start its half of Phase 4.

`docs/design/cw-bench-procedure.md` is **written** as of 2026-08-26 - it was
item 6 on the previous list.

Then the rest of 4.12, and 4.13 for Phase 3.

#### Bench state as of 2026-08-26

Recordings from today. All IC-7300 MkII, 14,040.900 unless noted, CW, 500 Hz
filter, 610 Hz pitch, mono 48 kHz from the USB CODEC:

| file | what it is |
|---|---|
| `strong23.wav` | 22 s of SM5OMP at 538 Hz, 15 wpm, the cleanest signal of the day |
| `qso2-clip.wav` | 72 s, same operator, 529 Hz, the sign-off; the 4.11f head-to-head |
| `ft-14040-90.wav` | 120 s, same operator earlier, the slow-sender case |
| `sp5xoc.wav` | 120 s, 14,027.70, weak, tone 842 Hz - the 4.11b search-window case |
| `mkii-nodecode.wav` | the weak signal the MkII would not print at all |
| `mkii-dk9py.wav` | 180 s, 14,027.10 - DK9PY running a CWT, tone 725 Hz, the easy signal of 4.11h; sidecar `.txt` |
| `mkii-i1yrl.wav` | 240 s, 14,045.10 - I1YRL after the contest, tone 625 Hz, the only plain QSO we hold; sidecar `.txt` |
| `mkii-14040-slow.wav`, `mkii-14045-92.wav`, `mkii-14045-83-weak.wav` | noise; keep as negative controls |

`bench/` is gitignored, so none of this is in version control and none of it is
backed up.

`bench/civ.ps1` is the CI-V helper written during the session - `freq=`,
`readfreq`, `readmode`, `mode=cw`, `width=`, `readwidth`, `readpitch`,
`smeter`, `readnr/nb/agc/notch/apf`, `raw=` - talking to the MkII directly on
COM8 at 19200, address 0xB6. It lives in `bench/` because that is ignored and
durable; it was written in a temp scratchpad that will not survive. Two things
it does badly: the radio broadcasts `27` scope frames unsolicited and a naive
"first frame addressed to E0" read picks one of those up instead of the answer,
so re-read on a surprising result; and `smeter` returns a constant `00 00`,
which may be the same fault.

Recording, for the record:

`ffmpeg -f dshow -i audio="Microphone (3- USB Audio Device)" -ac 1 -ar 48000 -c:a pcm_s16le -t 180 bench/out.wav`

### 4.12 Still outstanding

The comparison §4 asks for is **done**. Element decoding is level (§4.9,
provisionally - see the caveat in §4.10 about that capture being 199 Hz off the
pitch); noise output is level and neither decoder gates (§4.11); and we hold one
demonstrated advantage, the search window that finds a station the MkII cannot
see until the operator has zero-beat it (§4.10).

What is left before Phase 2 is our own work, not more measurement against the
radio:

- **Acquire wide, then hold the tone (§4.11h). Done - closed by measurement
  2026-09-01.** The commits are `048a0e8` "hold the tone while nothing is keyed"
  and `42e775e` "remember where the tone was when the lock drops", which landed
  after the paragraph below was written and were never scored against it.
  Re-run on the two files it was raised on:

  | file | tracked | pinned |
  |---|---|---|
  | `mkii-dk9py.wav` | 230 chars, `Readable 76%` | 233 chars, `Readable 76%` - transcripts differ in two junk runs |
  | `mkii-i1yrl.wav` | full over copied | `TSEE EEEEI E SE` - pinning at 725 is simply the wrong tone for this file |

  Tracking is now level with pinning where pinning was better and far ahead
  where it was worse, which is what the item asked for. The original note
  follows for the record: *"the tracker finds a signal 115 Hz off the
  operator's pitch and then corrupts every `CQ` in the file by re-picking the
  spectral peak during inter-character gaps."* That is no longer reproducible -
  every `CQ CWT DK9PY` in the file now comes out intact.

- **The inter-word gap (§4.11f, §4.11g, §4.11h). Done 2026-09-01 - see
  `core/docs/design/cw-decoder.md` §5.3.** The fixed 1.8-times-the-character-gap
  rule was a model of an operator, and `mkii-i1yrl.wav` is an operator it does
  not fit: measured from the audio, character gaps centre on 4.4 dits and word
  gaps on 5.8, a ratio of 1.3 rather than 2.33. He had loosened his character
  gaps and left his word gaps alone, so scaling from the one to the other put
  the split above two thirds of his word gaps. Replaced with two-means on the
  log of the recent separator gaps.

  `CwSignalGenerator` could not produce that case - every overload moved the two
  gaps together - so `GenerateWithFist` sets them independently and
  `CwRealFistWordGapTests` keys known text with the measured fist. Ground truth,
  not an eyeball: the ratio rule welds the whole sentence into one word at
  77.6%, the measured split returns 93.8%.

  **It is not free, and the bill is in §8 of the decoder doc.** Clean, `n12`,
  `n9`, `n6` and `f20` do not move; `f20n6` gains about 0.4 throughout; the 3 dB
  column loses at the top of the speed range, 40 WPM by 2.3 points. Three of the
  four guards tried against that measured completely inert. Taken deliberately:
  a 20-something WPM QSO with a loose fist is the ordinary case and goes from
  unreadable to readable, and 40 WPM at 3 dB SNR is not.

- **The opening of a slow transmission. Fixed 2026-09-01.** Not on this list
  before, because nothing had measured it. `CwDecoderEngine.Gate()` held text
  until the marks proved readable and ran a five-second stale clock from the
  first frame - but `Unknown` means both "not enough marks to judge yet", where
  every transmission starts, and "the marks I am judging have gone stale". At
  Farnsworth 5 WPM the tenth mark does not arrive until about 8.4 s, so the hold
  expired before the evidence to keep it existed and the opening was discarded.
  On air the opening is the callsign. Over the Ninja corpus, clean first entries
  went from 12 of 29 to 27 of 29 and mean junk words before the first correct
  copy from 1.34 to 0.03.

- **Why the MkII went quiet on `strong23.wav` (§4.11f).** Open, and the first
  theory is dead: it is not the tuning offset, because the next over was
  further off pitch and decoded fine. Run the auto-tune experiment anyway
  (record, note what the radio prints, hit CW auto-tuning, record again), and
  look at the MkII's own decode threshold setting while doing it.
- **The speed tracker (§4.4). Fixed 2026-08-26 - see §4.11d.1 and §4.11d.2.**
  Not by holding `_ditMs` on `SignalPresent`, which measured inert, but by a
  detector warm-up and a training test on how far a mark sits above the noise
  floor. The runs collapse to the pinned-speed numbers without pinning anything.
  What remains open is the 3.5-vs-4.0 threshold in §4.11d.2, which needs more
  weak-signal captures, and whether the MkII's own tracker (AUTO 22 wpm, 29 a
  second earlier) is beatable on a signal it fails to print at all.
  **The remaining part is now fixed too, 2026-09-01** - commit `59dcc68`, "an
  empty band is not a very fast operator". The speed estimate used to climb to
  46-60 wpm across silent samples and go on reporting `locked` while it did,
  which was harmless on the bench and stopped being harmless the moment the
  panel put both figures on screen for an operator to act on.
- **Surface `WordsPerMinute` in the UI. Done 2026-08-26 - see §4.15.** It is in
  the reader panel's status line with tone, pitch, filter width, search window,
  SNR and lock. Icom show it; it is what let Colin tell a confused decoder from
  a bad band. See §4.11d - and see the railing caveat above, which this now
  makes visible to the operator.
- **Output gating.** §4.11 gives the criterion and a threshold; where it lives in
  the pipeline, and whether it suppresses or merely marks, is undecided. One
  thing is now decided: **it will not be built on `Confidence`.** §4.11h measured
  that figure at 1.00 on 592 characters of junk and 0.23 on the best copy of the
  day - it is not weak, it is inverted, and a gate on it would suppress good copy
  and pass rubbish. The panel shipped ungated for that reason (§4.15).
- **Repeat §4.9 on the pitch**, so the element comparison stops being provisional.
- **Get an independent reference (§4.11e). Done 2026-08-26 - see §4.11g.**
  Fldigi over the same wavs, driven from `bench/fldigi/fldigi-bench.cmd` and
  read back over XML-RPC. It has already corrected two claims in §4.11f and
  confirmed SM5OMP, SN5N, CT7AUP and the CWT exchange format. Repeat it on
  every new bench recording before drawing conclusions from one.
- **Wire the IF filter width through to the decoder. Done for YWC 2026-08-26 -
  see §4.15.** `CwReaderService` reads the radio's CW pitch and SH filter code,
  converts the code with the new `Services/YaesuIfWidth.cs`, and builds the
  decoder's search window from it - the first caller `SearchWindowForFilterWidth`
  has ever had. **IWC is still outstanding**, and is a larger job than it sounds:
  it has `IfWidthA` from `1A 03` but no CW decoder at all to give it to, so it
  needs the core port first.
- **Multi-signal decoding (§4.13).** Phase 3. Until then the decoder is
  single-signal and does not say so, which is the part worth fixing first.
- **Re-run the §4.6 band sweep with cross-band peek off**, so its per-band
  flatness figures mean something.
- **Cut numbers must stay literal (§4.11h).** `2T62` is 2062 and `24T6` is 2406;
  `T` is 0, `N` is 9, `A` is 1, and `5NN` is 599. The decoder transcribes these
  correctly today and must go on doing so - anything that "corrects" a `T` to a
  `0` is wrong the moment a `T` is a `T`, and would silently corrupt callsigns.
  If cut numbers are ever expanded, it belongs in a display layer that can be
  turned off, alongside the raw text, never in `CwElementDecoder`.
  **Corroborated 2026-09-01** by a second decoder sharing no code with ours - a
  throwaway quadrature demodulator and Schmitt trigger written to settle this -
  which reads `ARMIN 2T62` and `BOB 24T6` on the same audio. Two independent
  decoders agreeing is not external ground truth and the roster check below
  still stands, but it does rule out the reading being an artefact of our own
  detector. The same run measured the file's keying directly: dit 29 ms
  (41.9 WPM), dashes at 86 ms, and **not one key-down run over 200 ms in
  180 seconds**, so nothing in the recording merges five dashes into one.
- **Check the CWops roster.** `ARMIN 2062` (DK9PY), `BOB 2406` and `DAN 1854`.
  Offline, no radio needed. It would turn three plausible decodes into three
  confirmed ones, including the `1854`-versus-`1584` argument in §4.11g, which
  currently rests on internal evidence alone.
- **CwBench dies on an unfinalised wav header. Fixed 2026-09-01** - see
  `tools/CwBench/WavReader.cs`, which now treats a zero, `0xFFFFFFFF` or
  overlong data chunk as "read to end of file" and says which it found. The
  original note follows. `bench/mkii-14040-qso3.wav` has
  a RIFF size of `0xFFFFFFFF` - ffmpeg was killed before it could rewrite the
  header - and CwBench computes a negative sample count and exits with
  `count ('-1') must be a non-negative value`, which names neither the file nor
  the cause. It is the only affected recording of the twenty-odd in `bench/`.
  Treat an overlong or `0xFFFFFFFF` data chunk as "read to end of file", and say
  so.
- **The search window clamps at 500 Hz, so wide filters get no benefit from it.**
  `SearchWindowForFilterWidth` is `width / 2` clamped to 100..500, so a 2.4 kHz
  or 2.8 kHz SSB filter implies +/-1200 or +/-1425 and receives +/-500. That is
  the right conservative answer for a single-signal decoder - a wider window
  only offers more wrong tones to lock onto - but it means the filter width
  stops informing the decoder above 1 kHz. The regime above the clamp is what
  §4.13 multi-signal is actually for; until that exists, the clamp should stay
  and the panel should say the window is clamped rather than implying the width
  set it. **The panel half is done 2026-09-01**: the status line now prints
  "(clamped)" or "(wider than the filter)" beside the search window, so the
  figure no longer reads as though the operator's filter chose it. The clamp
  itself stays, deliberately, until §4.13 exists.
- **`docs/design/cw-bench-procedure.md`. Written 2026-08-26.** CwBench's usage
  text pointed at it for two sessions before it existed. It carries the three
  lessons that cost the most time: cross-band peek off before recording; sweep
  levels drift about 10 dB over a session, so never compare a level across one;
  and read the poller log, not the screenshot.

### 4.16 Phase 3 built, 2026-09-01

All three pieces exist and the code is committed. **None of it has seen an
antenna**, and one part of it cannot be called working until it has - see the
bench check at the end.

**Transcript** - `core/Services/Cw/CwTranscriptWriter`, wired in at
`CwReaderService.AppendLocked`. It is written from there and not from the UI
because that one line is where every decoded character passes, and nothing the
browser does can make a character miss it. Characters are flushed as they
arrive rather than buffered into lines: a crash happens *while* something is
coming in, so the buffered line would lose exactly the part worth keeping. CW
arrives at a handful of characters a second, so the flush costs nothing against
what it protects. No file is created until something is decoded, so opening the
reader and closing it again leaves no trace.

**Deviation from §3.6, deliberate.** The plan says the transcript sits
alongside `radio_state.json`. It goes in a `CW Transcripts` subfolder instead:
one file per reader session accumulates quickly, and loose in the app data
folder they would bury the settings and state files they sat among.

**QSO save** - `core/Services/Cw/CwQsoFields` (pure extraction),
`core/Services/AdifRecordWriter` (ADIF out), `Services/Cw/CwQsoLogService`
(YWC-local, because it knows the radio state and the app's folders) and
`core/js/cw/cw-qso-form.js`. The suggest/save split is the point:
**nothing is ever logged from a suggestion directly.** §4.11h measured the
decoder reporting confidence 1.00 on 592 characters of junk, so a field
silently pre-filled from the copy is worse than an empty one - the operator has
no reason to look twice at a box that already has something in it. So the form
fills in only what the radio and the clock know, and offers what the decoder
thinks it knows as chips carrying the evidence that ranked them: "follows DE",
"sent 3 times".

Two things that only look like details:

- **ADIF length prefixes are octets, not characters.** A wrong length does not
  merely look untidy - it desynchronises a length-prefixed reader and every
  later field is read from the wrong offset. `AdifRecordWriter` counts UTF-8
  bytes. Our own `AdifParser` counts characters, which is a latent bug on the
  parser's side; the writer follows the specification rather than matching it,
  because third-party loggers follow the specification too.
- **Cut-number expansion needs a real digit as a guard.** `T`=0, `N`=9, `A`=1,
  so expanding any all-cut-letter word turns `ANT` into `190`, `TNT` into `010`
  and `NNN` into `999` - and ANT is one of the commonest words in a ragchew.
  Every report an operator actually sends has a figure in it (5NN, 5TT, 4NN),
  so requiring one digit loses nothing real. This does not contradict the
  "cut numbers must stay literal" item in §4.12: the decoder still
  transcribes exactly what was keyed, and the expansion happens only in this
  suggestion layer, which is exactly the "display layer that can be turned off"
  that item asks for.

**Reader Mode** - `Services/Cw/CwReaderModeService`, plus
`YaesuIfWidth.CodeForHz` for the reverse width lookup. CW mode, a narrow filter
(default 250 Hz, configurable in Settings), APF on, and the operator's previous
settings put back afterwards.

- **It lives on the server, and that is the design decision.** Three fetch calls
  from the page would work right up until the operator reloaded the tab, at
  which point the record of what their filter used to be is gone with it and
  they are on 250 Hz with APF ringing and nothing to press. Server-side, the
  restore survives a reload, a second browser, and the voice control.
- **Mode, then width, then APF.** An SH code means different bandwidths in
  different modes - code 8 is 1650 Hz in SSB and 400 Hz in CW - so a width sent
  before the mode sets a width for the mode the radio is about to leave. And a
  mode change makes the radio restore its own per-mode Contour and APF over
  anything set first, so APF goes last.
- **`CodeForHz` picks the nearest width, not the narrowest at or below.** The
  FTDX3000's CW filters start at 500 Hz, so "narrowest at or below 250" comes
  back empty and refuses to narrow a filter it could have narrowed to 500. Ties
  go to the wider filter: one narrower than asked for can put the CW pitch
  outside the passband and lose a signal the operator can hear, which is the
  opposite of what the button is for. An unknown model returns null and the
  filter is left alone - a guessed SH code would set a bandwidth nobody chose
  on a rig nobody here has tested against.

**Deviation from §3.3, deliberate.** The plan says the restore happens when
the reader closes. It fires on **Stop** instead, because closing the dialog
deliberately leaves the decoder running (§3.5), so Stop is when the operator
has actually finished reading. A filter that silently re-opened to 2.4 kHz
mid-QSO because they dismissed a panel is the worse surprise of the two.

**What still needs the bench, and must not be reported as working before it:**
Reader Mode writes `MD`, `SH` and `CO` frames to the radio. A successful build
and 112 passing app tests say nothing about whether the '101 accepts them, and
this is a write to the operator's own front panel. Check, on the radio: that
the filter lands where the button says it did, that APF actually comes on, and
- the one that matters most - that pressing Stop puts the mode, the width and
  the APF state back exactly as they were.

---

### 4.17 Phase 4 built, 2026-09-02

IWC has the reader. **None of it has seen an antenna**, and two parts of it
cannot be called working until they have - see the bench check at the end.

**Core moved first, and nothing had to move with it.** `core/` was already in
step in both repos, so Phase 4 opened with the subtree push already done. The
whole of `core/Services/Cw/` and `core/js/cw/` was reused unchanged - engine,
tone detector, element decoder, zero-in, Morse table, QSO field extraction,
transcript writer, reader panel, QSO form, spectrum, phasor, tokens. Nothing in
Core needed a single edit to serve a second radio, which is the result the
Phase 1 seam was chosen to get.

**Capture** - `Services/Cw/CwAudioDevices` and `Services/Cw/WaveInCwAudioSource`,
IWC-local. IWC has no audio subsystem at all: no `Services/Audio/`, no Remote
Audio, nothing to tap. So the reader opens a WinMM recording device directly
through NAudio, which is already referenced for the voice output, rather than
adding a second audio stack for one mono input stream.

Three things about that are worth keeping:

- **The device is stored by name, not index.** WinMM renumbers recording
  devices whenever anything USB is plugged in or removed, so a stored index
  quietly comes to mean a different device - which is how an operator ends up
  decoding their webcam microphone and blaming the decoder. A name that no
  longer matches anything is reported as missing instead.
- **WinMM truncates names at 31 characters** (`MAXPNAMELEN`). A name typed or
  pasted from Windows' own sound settings can be longer than anything WinMM
  will ever report back, so the lookup falls back to comparing the truncated
  form.
- **A WinMM buffer is not a multiple of 480 samples.** Without the carry
  buffer the remainder is dropped from every single buffer - a steady, silent
  loss that looks exactly like a decoder that cannot copy.

Frames are moved off the capture callback through a bounded channel with
`DropOldest`, created with the `itemDropped` overload. That callback is the only
way to know a frame went: `DropOldest` discards silently, so without it the
reader shows a healthy status while quietly losing audio.

**Reader Mode is six calls, not a table.** This is where IWC's seam pays.
`IRadioController.SetIfFilterWidthHzAsync(vfo, hz)` takes Hz and snaps
internally to the radio's own ladder; `RadioStateService.CwPitch` is already Hz.
So `CwReaderModeService` has no lookup table in it at all - where YWC has a page
of per-model `SH` codes. **That is the concrete proof that `YaesuIfWidth` could
never have moved into Core:** the two radios do not merely have different
numbers, one has numbers and the other has a formula.

Two differences from YWC's implementation, both deliberate:

- **APF asks for MID (2), not NAR (3).** With APF TYPE on SHARP, NAR is about
  80 Hz wide. At 20 wpm a dit is 60 ms, so the keying sidebands run to roughly
  +/-17 Hz, and faster sending spreads them further; a filter that narrow starts
  rounding the very transitions the decoder is timing.
- **Tie-breaking runs the other way.** YWC breaks an exact width tie *wider*;
  IWC's `FilterWidthCodec.HzToCode` scans ascending keeping the first match, so
  it breaks a tie *narrower*. This was found and deliberately left alone: it
  only bites on a value exactly between two rungs - 275 Hz - and the Icom CW
  ladder runs in 50 Hz steps to 500 Hz, so every width the Settings page offers
  is an exact rung. Changing the codec to suit this one caller would move the
  CI-V API's snapping and the width nudge with it.

**Software zero-in.** `POST /api/cw/zin`. The FTdx101 has a `ZI` command and
nudges its own VFO; the IC-7300 has no equivalent, so the offset is worked out
by `CwZeroIn` in Core - shared precisely so both apps agree about the sign - and
the frequency is set over CI-V. It refuses far more often than it acts, and that
is the design: a null offset means the tone detector is not confident or the
correction exceeds the clamp, both of which usually mean it has locked onto the
wrong signal, and nudging the VFO on a bad measurement moves the station the
operator was listening to out of the filter. Under 25 Hz it says "already on
pitch" - that is tracker jitter, the same threshold the reader panel uses before
it will say anything about being off pitch. The current VFO is read back rather
than taken from the cache, because the operator may have turned the dial since
the last poll and adding an offset to a stale reading puts the radio somewhere
neither of us asked for.

**The Settings device picker is server-rendered, and that is not laziness.** The
stored device is kept in the list even when it is absent. Populate the dropdown
from the browser instead and an operator who saves anything on that page while
the radio is switched off posts an empty value and silently loses their device -
after which the reader fails with "no CW audio device has been chosen" for a
reason they never did. `ModelState.Remove("Settings.CwAudioDeviceName")` goes in
for the same reason it does for the SDR device keys: `<Nullable>enable</Nullable>`
puts an implicit `[Required]` on a non-nullable string and blocks the whole save
of an empty value with no visible error.

**No `feature-setup.js` equivalent.** The Core panel skips the
`window.radioFeatureSetup` hook gracefully when it is absent, and
`WaveInCwAudioSource`'s error text already names *"Settings, CW Reader"*, so the
operator is told where to go without a second mechanism to keep in step.

**What still needs the bench, and must not be reported as working before it.**
Two protocol-level things, on the real MkII:

1. **Reader Mode**, exactly as for YWC: that the filter lands where the button
   says it did, that APF actually comes on, and - the one that matters most -
   that pressing Stop puts mode, width and APF back exactly as they were.
2. **The zero-in sign**, in both CW-U and CW-L. Getting it backwards does not
   look like a wrong number, it looks like zero-in running away from the signal.
   The deadband and the clamp mean a wrong sign fails visibly and harmlessly
   rather than dragging the VFO off, but it still has to be checked in both
   sidebands - CW-L is the case the `lowerSideband` flag exists for, and the one
   nobody has exercised.

A build and 202 passing Core tests say nothing about either of them.

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
