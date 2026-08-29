# CW bench procedure

How to make a recording that is worth drawing a conclusion from, and how to get
three decoders to argue about it fairly.

`tools/CwBench`'s usage text has pointed at this file for two sessions. It exists
now because the same mistakes kept costing whole sessions, and none of them were
interesting the second time.

Companion to `docs/design/cw-reader-plan.md` section 4, which is where the
findings live. This is only the method.

---

## The three that cost the most time

Read these even if you read nothing else.

**1. Cross-band peek off before recording.** The IC-7300 MkII's scope peeks at
other bands while it sweeps, and every peek puts a break in the receive audio.
It is inaudible if you are listening and obvious if you are decoding: it lands
in the middle of a character, the element decoder sees a gap where none was
sent, and the transcript gains a word break or loses a letter. The whole of the
section 4.6 band sweep has to be re-run for this reason. Turn it off before the
first recording of a session, not after the first surprising result.

**2. Sweep levels drift about 10 dB over a session, so never compare a level
across one.** AGC, band conditions and the operator's own RF gain all move.
A dB figure is only comparable against another figure from the same recording.
This is why `--spectrum` prints a *keying ratio* alongside the level: the ratio
is between bins in the same file and survives the drift, the level does not.

**3. Read the poller log, not the screenshot.** Fldigi's RX pane scrolls. A
screenshot shows its tail, and the tail of a decode of a fading signal is
always the worst part of it. A claim in section 4.11f - that Fldigi's tracking
main pane produced junk where its pinned channel produced callsigns - was drawn
from a screenshot and was wrong; the poller log showed the main pane had in fact
got `CT7AUP`, `DAN 1854` twice, `DAVID 3356` and `SM5IMO`. Capture the text, then
read the text.

---

## Recording

### Before you press record

- **Cross-band peek off.** See above.
- **Note the radio state**, over CAT, at the moment of recording - not from
  memory afterwards. `bench/civ.ps1` does this for the MkII:

  ```
  bench/civ.ps1 readfreq readmode readwidth readpitch
  ```

  Two cautions on that script. The radio broadcasts unsolicited `27` scope
  frames, and a naive "first frame addressed to E0" read can pick one of those
  up instead of the answer, so re-read anything surprising. And `smeter`
  returns a constant `00 00`, which may be the same fault.

- **Decide what the recording is for** before making it. A file made because
  something interesting was happening is nearly always unusable: nothing is
  known about the signal, so any disagreement between decoders is unresolvable.
  The useful files are the ones made to answer a question.

### The command

```
ffmpeg -f dshow -i audio="$(bash scripts/find-cw-device.sh)" \
       -ac 1 -ar 48000 -c:a pcm_s16le -t 180 bench/out.wav
```

Mono, 48 kHz, 16-bit PCM, from the radio's USB CODEC. 48 kHz because that is
`AudioConstants.SampleRate` and what `ICwAudioSource` documents, so the bench
sees exactly what the application will see.

**Do not hardcode the device name.** It carries a Windows enumeration number -
it was `Microphone (3- USB Audio Device)` and is now `Line (2- USB AUDIO
CODEC)` (with a double space) - which changes when the radio is re-plugged or
another USB audio device appears. ffmpeg then fails with "Could not find audio
only device", which reads like a driver fault rather than a renamed device, and
the failure lands in the middle of a band opening. `scripts/find-cw-device.sh`
asks the machine instead.

**Use `-t` and let ffmpeg finish.** Killing it leaves the RIFF header
unfinalised - size `0xFFFFFFFF` - and CwBench then computes a negative sample
count and exits with `count ('-1') must be a non-negative value`, naming neither
the file nor the cause. `bench/mkii-14040-qso3.wav` is the one recording in the
directory this happened to, and it is unreadable.

Check afterwards:

```
python -c "import struct,os,sys; p=sys.argv[1]; d=open(p,'rb').read(12); \
print(struct.unpack('<I',d[4:8])[0]+8, os.path.getsize(p))" bench/out.wav
```

Two numbers that match means the header is sound.

### Length

Three minutes is the working minimum for contest traffic and four for a plain
QSO. Shorter files are dominated by whatever the decoder was doing while it
warmed up, and the warm-up is a second or two of every run.

### The sidecar

Every recording gets a `.txt` beside it with the same stem, holding the CAT
read, the date, and - this is the part that gets skipped - **why the file was
made and what it is expected to show**. Six months on, a wav with no sidecar is
a wav nobody can draw a conclusion from.

`bench/mkii-dk9py.txt` and `bench/mkii-i1yrl.txt` are the pattern.

`bench/` is gitignored. None of it is in version control and none of it is
backed up. The sidecars are the only durable record of what any of it is.

---

## Decoding it: CwBench

```
dotnet run --project tools/CwBench -c Release -- bench/out.wav --pitch 610 --filter 500
```

`--pitch` is the radio's CW pitch and `--filter` its IF width in Hz; the search
window is then derived exactly as the application derives it, so the bench and
the shipped decoder agree.

### Probe for 30 seconds before committing to three minutes

Recording blind for three minutes and then discovering there was no keyed
signal in it wastes the operator's time, not the machine's - they have to sit
there not touching the dial for the whole of it. On 2026-08-26 three files were
made this way and two were duds:

| file | most keyed | ratio | verdict |
|---|---|---|---|
| `ywc-40m-cw.wav`  | 675 Hz  | 3.2 | keyed but weakly, spread over five bins - several stations |
| `ywc-40m-cw2.wav` | 2950 Hz | 2.9 | nothing keyed at all - noise |

In both cases **the operator's ear called it before the software did** ("lots
of signals on top of each other", "might be RTTY", "it's gone"). Three for
three that session. The keying-ratio test agrees with a listener, so use it as
a gate rather than a post-mortem:

```
ffmpeg ... -t 30 bench/probe.wav        # half a minute, not three
dotnet run --project tools/CwBench -c Release -- bench/probe.wav \
    --pitch <hz> --filter <hz> --spectrum
```

Keep going to 180 s only if some bin shows a keying ratio of 5 or better.

### Always run `--spectrum` first

```
dotnet run --project tools/CwBench -c Release -- bench/out.wav --pitch 610 --filter 500 --spectrum
```

It says what is in the recording before decoding it, so a transcript of noise is
not mistaken for a transcript of a signal. Look for:

- **A keying ratio of 5 or more** in some bin. Below about 3 there is no keyed
  tone in the file and the transcript is noise, however plausible it reads.
- **Which bin.** This is not optional and it is the lesson of plan section
  4.11h: on `mkii-dk9py.wav` the keyed tone is at 725 Hz while the operator's
  pitch was 610, because the sender was 115 Hz off zero-beat. Decoding at the
  pitch would have found nothing. Never assume the signal is where the radio
  was tuned.
- **Flatness.** A high median-to-peak ratio means no single tone dominates -
  several stations, or noise.

`--timeline [Hz]` does the same a second at a time, so a signal that fades is
not averaged away.

**Pass the Hz explicitly whenever the spectrum verdict is doubtful.** With no
argument `--timeline` uses whatever `--spectrum` called the most keyed bin, and
on a file with no keyed signal that choice is meaningless - on `ywc-40m-cw2.wav`
it picked 2950 Hz, outside the passband, at -68 dB, and produced 180 rows of
noise measured against noise with `CW` printed beside half of them. The
automatic choice is only trustworthy when there is a real peak for it to find,
which is exactly the case where you did not need the timeline.

### Useful flags, and what each one is for

| flag | what it separates |
|---|---|
| `--no-track --pitch <hz>` | Pins the detector. Run this at the tone `--spectrum` found, and compare. If pinning is much better, the tracker is wandering; if much worse, the sender is drifting. |
| `--pin-wpm <n>` | Holds the speed. Tells a speed-tracker fault apart from an element-timing fault on the same recording. |
| `--marks` | The raw key-down/key-up timings. Where to go when the characters are wrong but the timing looks right. |
| `--train-noise <x>`, `--warmup <s>` | The two thresholds from plan 4.11d.2, exposed so they can be swept without a rebuild. |
| `--telemetry 1` | A readout every second, to line up against `--timeline`. |
| `--raw` | Transcript only - for pasting into the plan. |

### Read the summary honestly

- **`runs >=4`** counts runs of four or more identical characters. Non-zero is
  the speed tracker railing, not the band.
- **`confidence` is inverted. Ignore it.** Plan 4.11h measured it at 1.00 on 592
  characters of junk and 0.23 on the best copy of the day. It is not a weak
  signal, it is an anti-signal.
- **`tone` against `configured pitch`** is the zero-beat error. A large gap with
  good copy means the search window did its job.
- **`speed (last tracked)`** is taken at the end of the file, which is usually
  silence, and it climbs on silence. Read it from the telemetry lines where
  `signal` is showing, not from the summary.

---

## The reference decoder: Fldigi

Two independent decoders arguing is not a measurement. Fldigi 4.2.11 is the
umpire, and every new recording should go through it before conclusions are
drawn from it.

### The isolated config

`bench/fldigi/fldigi-bench.cmd` starts Fldigi against
`C:\Users\colin\fldigi-bench.files` - a throwaway copy of the live config, so
the real `fldigi.files` and its rig control cannot be damaged by any of this.
It is pre-set to:

| setting | value | why |
|---|---|---|
| `<AUDIOIO>` | `3` (File I/O only) | No sound card is touched. |
| Op mode | CW | |
| Waterfall cursor | 538 Hz | Starting point only - move it to the tone. |

XML-RPC is on `127.0.0.1:7362`. Useful methods: `text.get_rx`,
`text.get_rx_length`, `text.clear_rx`, `modem.set_by_name`, `modem.set_carrier`,
`main.set_afc`, `main.set_squelch`.

### Resample to 8 kHz first

8 kHz mono is Fldigi's native rate. Same audio, same level, resampled, nothing
else:

```
ffmpeg -i bench/out.wav -ac 1 -ar 8000 bench/fldigi/out-8k.wav
```

### The one manual step

**Fldigi cannot be made to decode a wav from the command line or over XML-RPC.**
Playback needs a GUI click, and there is no way round it:

1. Run `bench/fldigi/fldigi-bench.cmd`.
2. Start the poller (below) *before* playback, not after.
3. **File > Audio > Playback...**, pick the wav.
4. Click the waterfall on the tone `--spectrum` identified.
5. If nothing prints, drop the squelch.

### Capture the text

Start this before playback and let it run:

```python
import time, xmlrpc.client
out = r'...\bench\fldigi\rx-capture.log'
s = xmlrpc.client.ServerProxy("http://127.0.0.1:7362/")
last = ""
end = time.time() + 900
with open(out, "w", encoding="utf8") as f:
    while time.time() < end:
        try:
            n = s.text.get_rx_length()
            cur = s.text.get_rx(0, n).data.decode("utf8", "replace") if n else ""
        except Exception:
            time.sleep(2); continue
        if cur != last:
            f.write("[%s] %s\n" % (time.strftime("%H:%M:%S"),
                    cur[len(last):] if cur.startswith(last) else "RESET|" + cur))
            f.flush()
            last = cur
        time.sleep(2)
```

Then read the log. Not the screenshot. See lesson 3.

### Two decoders, not one

Fldigi shows both at once and they disagree:

- **The main RX pane** tracks with AFC.
- **The signal browser** runs a fixed decoder per channel.

Quote which one you are quoting. And note that on both files tested, exactly one
browser row carried the copy while every other row carried single-element junk
(`EE TEEET EE EJ TMN AET`) - channelising a passband does not decode more
stations for free, it decodes the one that is there and prints noise on the
rest.

---

## Reading a transcript without fooling yourself

**Cut numbers are not errors.** In contest CW a shortened character stands in
for a digit: `T` is 0, `N` is 9, `A` is 1. So `2T62` is 2062, `24T6` is 2406,
and `5NN` is 599. The decoder transcribes these literally and that is correct.
Anything that "fixes" them introduces the error.

**Repetition flatters the decoder.** `LUC LUC LUC`, `I1YRL I1YRL I1YRL`, a
callsign sent three times - those tokens are certain because they were sent
three times, not because the decoder is good. Contest traffic is nearly all
repetition. Any accuracy figure taken from a contest recording is flattered by
this, and a plain QSO gives one shot per word.

**A tail of plausible rubbish is the signature failure.** Clean copy up to the
point the signal fades, then something that reads like Morse and is not, with
nothing marking the join - `I16EEHIISI5SHQCQCT`. There is no decoder change that
removes this. It is why the reader panel reports signal presence and SNR beside
the text.

**One recording is one recording.** Plan section 4.10 was written from a single
file, its reproduction failed (4.10a), and 4.11h has since produced a
contradicting case. Two recordings that agree are a finding; one is a hypothesis.

**"The radio decoded it" means the radio's screen.** When a result is relayed
from the rig, it is that decoder's output being quoted, not anyone's ear copy.
Write it as two decoders disagreeing, because that is what it is.
