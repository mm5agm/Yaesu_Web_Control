# How the CW reader works

This describes `core/Services/Cw` as it stands. It is the radio-agnostic half:
it takes mono float audio and a pitch, and produces text. It never sees a CAT
command, a filter code or a radio model, which is why it lives in the shared
core and is used unchanged by Yaesu Web Control and Icom Web Control.

The source carries the measurements behind each constant in its own comments.
This document is the shape of the thing, not a replacement for those.

---

## 1. The problem, stated honestly

Decoding CW from an off-air signal is not one problem, it is three, and they
fail in different ways:

1. **Where is the tone?** The operator sets a pitch, but the station is
   wherever the dial put it. A reader locked to the configured pitch is deaf to
   a signal 200 Hz away that the operator can hear perfectly well.
2. **When is the key down?** This is a threshold question, and every threshold
   that works on a strong signal has a way of keying on noise instead.
3. **What speed is being sent?** Dit and dah are only defined relative to each
   other, and the sender may be using Farnsworth spacing, in which case the
   character timing and the word timing are on different scales.

Under each of these sits a fourth question that turns out to matter more than
any of them: **is the output worth showing at all?** A reader that prints
plausible-looking garbage is worse than one that prints nothing, because the
operator has no way to tell the difference. This is the single most important
design commitment in the code, and section 6 is about it.

---

## 2. The pipeline

```
   48 kHz mono float
        |
        |  FIR low-pass, decimate to 8 kHz          CwToneDetector
        v
   +----------------------+          +---------------------------+
   |  Pitch path          |          |  Envelope path            |
   |  1024-pt FFT         | -------> |  Goertzel at the tracked  |
   |  every 512 samples   |  tone Hz |  tone, 80-sample window,  |
   |  (7.8 Hz bins,       |          |  40-sample hop (5 ms)     |
   |   128 ms)            |          |                           |
   |  finds the tone      |          |  -> magnitude, key up/down|
   +----------------------+          +---------------------------+
        |                                    |
        |  confidence, ToneHz                |  CwToneSample per 5 ms hop
        v                                    v
   presence gate  ------------------->  CwElementDecoder
   (level + keying)                          |
                                             |  marks and gaps -> . and -
                                             v
                                        MorseTable -> characters
                                             |
                                             v
                                     readability assessment
                                             |
                                             v
                              CwDecoderEngine.Gate() -> visible text
```

Two separate paths exist because they answer different questions on different
timescales. The FFT is slow and frequency-selective: it says *where* the tone
is, 128 ms at a time. The Goertzel is fast and narrow: it says *how loud* that
one tone is right now, every 5 ms, which is what keying needs. Using the FFT
for keying would blur a 20 ms dit into nothing; using the Goertzel to search
would mean already knowing the answer.

---

## 3. Finding the tone

`UpdatePitch()` runs a 1024-point FFT every 512 samples at 8 kHz - 7.8 Hz bins,
a new frame every 64 ms over 128 ms of audio.

**Acquire wide, track narrow.** With no lock, the search covers the whole
`SearchWindowHz` the filter passes. Once there is a confident lock it follows
the tone within `TrackBandHz` (150 Hz) of where it was. The wide search stops
an audible signal being invisible because the dial is off; the narrow track
stops a louder station elsewhere in the passband stealing an established lock.

**Confidence is prominence.** The peak bin's magnitude divided by the mean of
the rest of the search window, mapped so that a ratio of 2 reads as 0 and 8
reads as 1. A tone stands clear of the window; noise does not. Measured per 5 s
block over the bench recordings, an empty band reaches a median of about
0.51-0.61 and a maximum of 0.75, while a readable signal sits at 0.83-0.91. The
switch from wide search to narrow track happens at 0.80 - above everything an
empty band reached, below what a readable signal sits at.

**Sub-bin interpolation.** A log-parabola through the peak bin and its two
neighbours. Three points on a parabola is the right model for a windowed peak
and lands well inside one bin, which is what makes the zero-in offset
(`CwZeroIn`) worth acting on. The Yaesu has its own `ZI` command; the IC-7300
MkII has no equivalent, which is why this is computed here rather than left to
the radio.

The tone estimate moves toward each measurement by `0.35 x frameConfidence`, so
a doubtful frame barely moves it and a clear one moves it most of the way.

---

## 4. Deciding the key is down

### 4.1 The envelope

A single Goertzel at the tracked tone over an 80-sample (10 ms) window, hopping
40 samples (5 ms). The detector also keeps the real and imaginary parts, which
the magnitude throws away, and exports them as `PhasorI` / `PhasorQ` for the
tuning display.

### 4.2 The threshold

Keying turns **on** half way from the mean noise up to the mark level, and
**off** at 35% of the way. The hysteresis is wide enough to ignore noise ripple
and narrow enough not to clip short dits.

The mark level is the reference this hangs from, and it used to be a latch.
It only updated while the key was down, and the key only went down at half way
up to it - so once a fade took the signal more than 6 dB below the last mark,
nothing could key and nothing could lower the bar. A 20 dB fade at 0.05 Hz
falls at up to 3 dB/s, and at 5 WPM the word gaps are 5.5 s long, so one gap
was enough to strand it permanently.

It now decays at `MarkDecayDbPerSec` (4 dB/s) while the key is up, with a bottom
stop at 6x the mean noise. The stop matters as much as the decay: keying turns
on half way up, so a stop at 2.5x would put the on-threshold at 1.75x the noise
while narrowband hiss peaks at about 2.1x - the gate would key on the band
itself. The decay is written to **fall only**; clamping with `Math.Max` against
the floor also *raises* the level whenever the noise is high, which lifts the
keying bar on exactly the noisy band that can least afford it.

Measured effect: 5 WPM under a 20 dB fade went from 67.9% to 97.1% correct, and
the same file with noise added from 71.6% to 96.4%. Nothing at 13 WPM or above
moved, because their gaps are too short to strand the latch.

### 4.3 The presence gate

Two tests must both pass before the reader will admit a signal exists.

**A level test** - peak over mean noise, `PresentRatio` 3.0 with hysteresis
down to 2.2. This alone is not enough, and the reason is worth stating: the
noise in a narrow filter is Rayleigh, so its own peak sits about 2.1x its mean.
That is 3.5 dB of margin against a 3.0 gate, and any wander in the level - AGC
breathing, QSB, an atmospheric crash - spends it. An empty 15 m band with
nothing audible on it read 13.7 dB, "signal", "locked" and 60 WPM.

**A keying test** - because no level test separates amplitude-modulated noise
from CW, but *duration* does. A dit is 20 ms even at 60 WPM (four hops) and
holds a flat top for all of them; a dah holds one three times longer at any
speed. A Rayleigh spike lasts one or two hops and has no top at all. So the
detector keeps a rolling 10-second record of how many hops were spent inside
sustained excursions, and requires a total before it will call a tone present.

What is accumulated is **total time in qualified runs, not the number of them**.
Counting runs sounds like the same question and is not - it measures how fast
the other operator is sending, so a fixed count in a fixed window silently
encodes a minimum speed. The 5 WPM ARRL file produces almost exactly 10
qualified runs per ten seconds, which sat right on the old count threshold:
presence flapped, and because presence forces the key up it took whole
characters with it. Of 1012 marks the detector reported 631, missing dits and
dahs at the same rate in contiguous clumps - the signature of a gate opening
and closing, not of weak marks. By time, three minutes of empty 15 m
accumulates around 70 hops per window, the 5 WPM file around 320.

Acquiring is stricter than holding, deliberately. Presence gates `KeyDown`, so
dropping it mid-over costs characters outright; a signal that has proved itself
keeps presence across the gaps between overs.

**Grace.** The keying gate needs several marks to make up its mind, and the
first few marks of a transmission are the callsign. So the level test opens the
gate immediately and the keying gate has 5.5 s to confirm or revoke. On a real
signal confirmation arrives within a couple of characters. On an empty band the
cost is one burst of chatter at the top of the session - after which the level
test never falls back below the release ratio for the 6 s needed to re-arm the
grace, so the band stays quiet.

---

## 5. Turning marks into characters

`CwElementDecoder` sees one `CwToneSample` per 5 ms hop and works on the runs
between key transitions.

### 5.1 De-glitch

A run shorter than `max(MinElementMs, 0.40 x ditMs)` is not an element; the edge
is undone so the runs either side join up.

"Too short" has to be read against the speed being sent. A fixed 12 ms floor is
a fifth of a dit at 20 WPM, which leaves ample room for a noise blip to pass as
an element - and a spurious element is more expensive than a missing one,
because it corrupts the symbol rather than shortening it, `MorseTable.Decode`
returns null, and the whole character vanishes with no mark on the page. Going
from 6 dB to 3 dB SNR in a 500 Hz filter, the 20 WPM file gained 157 marks and
lost 192 characters, with p10 of the mark lengths falling from 60 ms to 55 while
p90 stayed at 180 - dits fragmenting, dahs untouched.

Scaling with the tracked dit only bites where there is headroom: at 40 WPM a dit
is 30 ms so this asks for 10 ms, under the floor, and fast sending is left
exactly as it was.

### 5.2 Speed tracking

Two centroids, dit and dah, pulled toward each mark that is worth learning from.
A mark far below the level real elements have been arriving at still produces a
symbol - whether to *show* the text is a separate question - but does not get a
vote on the speed. Those are the marks that rail the estimate at `MaxWpm`.
"Worth learning from" is `peak >= 3.5 x noise`: real elements measured five to
seven times the noise floor across four off-air recordings, noise blips two to
three.

### 5.3 Gaps, and why Farnsworth breaks the textbook

A gap under two dits is inside a character. Past that it separates either two
characters or two words, and the element dit cannot answer which.

The textbook rule is "a word gap is five dits or more", from 1 / 3 / 7 timing.
That holds only when the sender uses textbook timing, and every practice
recording below about 15 WPM does not. All ten ARRL W1AW files send their
elements at the same 14.6 WPM - an 85 ms dit - and vary only the gaps:

| file   | character gap | word gap |
|--------|---------------|----------|
| 5 WPM  | 1498 ms       | 5506 ms  |
| 10 WPM | 552 ms        | 1293 ms  |
| 13 WPM | 333 ms        | 783 ms   |

Five dits is 425 ms, so on all three every *character* gap cleared the word-gap
bar. The decoder had the Morse right and printed `2 0 2 4  Q S T` - a space
wedged between every letter.

So the character gap is **measured**, as the lower quartile of the separator
gaps seen lately, floored at three dits so it can be stretched but never
squeezed. English runs about four characters to a word, so most separators are
character gaps and the lower quartile lands inside that cluster wherever it sits.

A percentile rather than a tracked pair of centroids, because centroids have a
wrong answer they will not leave: seed them from the element dit, feed them
Farnsworth gaps, and the pull toward 7:3 holds the character estimate at three
sevenths of the word estimate, keeping the split above the character gaps
forever.

The window is used from the very first gap rather than waiting for a respectable
sample. Falling back to the textbook three dits as "not enough data yet" picks
the one answer that is definitely wrong on a Farnsworth sender, and picks it for
the opening of the transmission - which is the callsign. Waiting for eight gaps
turned `CQ CQ DE MM5AGM` into `C Q D E MM5AGM`. Erring towards *not* splitting
is the cheap direction: a missed word gap costs one edit per word, a wrongly
split one costs an edit on every character.

### 5.4 The idle flush

If the key has been up for 1500 ms with a part-built symbol, the character is
flushed so it reaches the screen rather than waiting for the operator to send
again. This must not disturb the measurement of the gap it happens in. Moving
the edge timestamp to prevent a repeat flush did disturb it, and expensively:
a 5 WPM Farnsworth sender leaves 1498 ms between characters, a hair under the
threshold, so the flush fired *inside* the gaps and the gap measurement got
what was left of them. Every word gap then measured short, no space was emitted
anywhere, and 381 characters - all of them individually correct - ran together
as `THELETTERKMEANSTHATSTHEENDOFMYMESSAGE`. Latching a flag and leaving the edge
alone scores 97.1% where that scored 79.0%.

---

## 6. Readability: score, never edit

This is the part that matters most, and the rule behind it is one sentence:

> **Nothing in this decoder ever edits a character. It only decides whether the
> whole recent stream is worth showing.**

The temptation is obvious - you can see the `E`s and `I`s piling up, so throw
them away. The reason not to is that nothing distinguishes a spurious `S` from
the `S` in a callsign. A reader that silently drops the ones it dislikes
produces copy that *looks* clean and is wrong in invisible places. For an
operator who does not read CW by ear and has no way to check, plausible-but-wrong
copy is far more dangerous than visibly-wrong copy. So the decoder answers a
different question: *does the recent traffic have the shape of Morse at all?*
If not, it says so and shows nothing.

`CwReadability` has four values: `Unknown` (not enough marks yet), `Readable`,
`Chatter` (one mark length only - nothing can be a dah, so every character
falls in the all-dit set), and `Jumbled` (mark lengths scattered wider than one
fist produces).

Three independent tests can each force a not-readable verdict. They are
independent on purpose: each has a blind spot the others cover.

### 6.1 Mark-length spread - is this the right shape?

p90/p10 of the recent mark lengths. Real Morse sits near 3, because that is the
dah:dit ratio. Below `ReadabilitySpreadFloor` (2.0) there is only one mark
length, which is `Chatter`. Above `ReadabilitySpreadCeiling` (8.0) the lengths
are scattered wider than one sender produces - most likely several stations
inside the passband - which is `Jumbled`.

Every mark goes into this window, including ones too weak to train the speed.
The question is "what shape is the thing arriving", and the marks excluded from
training are exactly the ones that give the fault away.

**Its blind spot:** p90/p10 is scale-free. A population of noise blips spanning
one to four hops has the same 3:1 ratio as a dit/dah population. This is not
hypothetical - an earlier attempt fed the de-glitched blips into this spread,
and an empty band promptly decoded as `II EIES<HH> IEE` and called itself
Readable.

### 6.2 Dirty-run fraction - is the channel clean?

The de-glitch removes exactly the short outliers the spread is measured from, so
without something to record them, discarding a blip improves the copy *and*
deletes the warning that the copy is bad. Every key-down run is therefore
counted, element or not, and the fraction discarded as too short is tracked.
Above `ReadabilityDirtyCeiling` (0.35) the verdict is `Jumbled`.

A **count**, not a contribution to the spread, precisely because a count cannot
be imitated by scale.

0.20 was tried and rejected as a false-alarm generator: it condemned 46% of a
5 WPM signal that was 80% correct, costing 25 points of copy.

**Its blind spot:** it is speed-biased and effectively blind above about 30 WPM,
because the adaptive de-glitch floor (`0.40 x ditMs`) drops below `MinElementMs`
there. Nothing is discarded, so nothing is counted.

### 6.3 Dit-only fraction - does the output look like language?

Which is what the third test covers. `E`, `I`, `S`, `H` and `5` are the only
characters that spurious short marks can assemble into, so the fraction of
recent characters whose symbol contained no dah is a language-free statistic
with a stable value on real traffic. Across the ten ARRL files, ground truth is
23.3%-30.8%. Junk at 30-40 WPM and 0 dB SNR measures 68%-72%. The ceiling sits
at 0.60, over a window of the last 48 characters, needing at least 24.

Measured effect at 40 WPM, 0 dB: output called Readable fell from 64% to 3% and
Jumbled rose from 0% to 83%, on copy that was 15.6% correct.

### 6.4 Holding, not discarding

`CwDecoderEngine.Gate()` holds decoded text in a buffer and releases it only
once readability reads `Readable`. Text decoded before that is **held, not
dropped**, and released the moment the signal proves itself - which is what
stops a station losing its callsign to the wait, and what carries a fading
signal across the fade rather than throwing away the characters either side.

Discarding on the first bad assessment was the obvious implementation and it was
wrong: a real signal dips through `Chatter` on any deep fade, and wiping the
buffer there cost the QSB tests most of their text - 4.2% on a 20 dB fade
against a 40% floor. Holding costs nothing, because held text that never becomes
readable is never shown. The buffer is capped at 64 characters and cleared if it
has been unreadable for `HoldStaleSeconds` (5 s).

`Flush()` releases held text only if the signal was readable. A capture that
stops while the decoder is still undecided has, by definition, never shown that
it was copying anything.

---

## 7. Telling the operator what it is doing

Sections 1 to 6 are about copying Morse. This one is about the other half of the
problem, and for an operator who does not read CW it is the more important half:
the panel has to make the difference between good copy and rubbish *visible*,
because the text alone never shows it.

### 7.1 The speed, and what "locked" is allowed to mean

`IsLocked` is the panel's licence to print a words-per-minute figure, so it has
to mean "this number is worth showing" rather than "some marks arrived".

The original rule was six trained marks and nothing else, and it failed in the
one case where a wrong answer does the most damage. On an empty band the noise
blips that survive de-glitch train the speed estimator perfectly well, so three
minutes of recorded empty 15 m reported **51.4 wpm, locked** - which an operator
reads as a very fast station rather than as a decoder with nothing to decode.
Nothing on the panel distinguished the two.

The lock now also requires that the marks behind the estimate have looked like
Morse recently:

```csharp
public bool IsLocked
    => _marksSeen >= LockMarks && _nowMs - _lastReadableMs <= _opt.LockHoldMs;
```

`_lastReadableMs` is refreshed in `OnMark` - the answer can only change when a
mark arrives, and doing it there rather than on every 5 ms hop keeps the window
in step with the marks it is judging. The `LockHoldMs` grace (5 s) exists
because readability dips on any deep fade, and a lock that dropped the instant
it did would flicker the speed off and on across ordinary QSB. What separates a
fade from a dead band is not the dip but how long it lasts.

`CwSpeedLockTests` pins both ends: the recorded empty band must not lock, and
ordinary sending must - a test that only proved the first is satisfied by
`IsLocked` returning false forever.

### 7.2 The passband spectrum

The phasor answers "am I on the tone", but it only ever sees the one frequency
the reader is already looking at, so when the reader goes quiet it cannot say
whether the band is empty or the dial is simply off. `CwToneDetector` therefore
captures the magnitude slice it is already computing, and `/api/cw/spectrum`
serves it to `js/cw/cw-spectrum.js`.

Three decisions in that display are worth recording:

- **The axis does not move.** The span is fixed at construction from the
  configured pitch and search window, not from the tracked tone. An axis that
  slides while the operator is turning the dial is worse than no display.
- **Levels are dB above the span's median, not absolute.** Nothing upstream has
  a calibrated scale. The median rather than the mean, because a strong carrier
  drags a mean up and flattens its own peak.
- **Two traces.** CW is on and off by nature, so the live trace spends much of
  its time in the gaps between elements; a slowly falling peak hold is what
  makes a keyed signal read as a line rather than a flicker.

The marker at the wanted pitch is the point of the whole thing: the tuning error
is the horizontal gap between the marker and the peak, read off the screen with
no number to interpret and no beat note to hear. A second marker shows where the
reader believes the tone is, drawn only when confidence is over 0.5 - an
unlocked search wanders, and a marker that wanders invites the operator to chase
it.

### 7.3 Marking up the copy, and the veto that was measured and rejected

What an operator actually wants out of a marginal QSO is small and specific: the
other station's call, the name, and whether the exchange has reached 73.
`js/cw/cw-tokens.js` colours the tokens that have the *shape* of QSO traffic -
prosigns and Q-codes, signal reports, callsign-shaped groups - and changes
nothing else. `markUp()` returns runs whose concatenation is the input verbatim,
which is how section 6's rule survives contact with a renderer.

**The veto version does not work, and this was measured before anything was
built.** Turning the same test around to *suppress* implausible words scores:

| case | words that fail the plausibility test |
|---|---|
| ARRL ground truth, 10 files | 1.1 - 8.3% |
| good decoder copy | 1.1 - 9.3% |
| real off-air `cq-then-qso` | 22.9% |
| real off-air `mkii-dk9py` | 28.6% |
| genuine junk, 40 wpm at 0 dB | 31.2% |

Real off-air copy sits *inside* the junk range, because real off-air copy at
this reader's quality **is** about a quarter garbage words with the callsigns
standing correct between them - `DK9PY GVM EN4PVM ARMIN 2T62 UEE FQCWT DK9PY`.
A threshold set anywhere that caught the junk would take the QSOs with it, and
the junk is already caught: 83% of it scores `Jumbled`. So the veto would cost
exactly the callsigns the operator wants and gain nothing.

Highlighting is the form of the idea that survives, and it measures well in the
opposite direction:

| case | tokens | marked | calls found |
|---|---|---|---|
| ARRL ground truth, 20 wpm | 172 | 5.8% | W1AW |
| decoder copy of the same | 207 | 4.8% | W1AW |
| real off-air `cq-then-qso` | 72 | 19.4% | OE3KAB + 4 others |
| real off-air `mkii-dk9py` | 107 | 14.0% | DK9PY, I2WIJ + 3 others |
| genuine junk, 40 wpm at 0 dB | 65 | **0.0%** | none |

Junk lights up nothing at all, and the ground truth and the decoder's copy of it
mark up at almost the same rate - so the classifier is not inventing marks out
of garble. The ARRL figures are low because bulletin prose is not QSO traffic,
which is the right answer for it.

**Repetition is what makes the callsign marks worth anything.** Callsign shape
alone is five or six characters and garble finds it by chance, and on both real
recordings the split was total:

```
cq-then-qso   repeated  OE3KAB x3
              seen once TE3KAB U4POC GM5NN O3X
mkii-dk9py    repeated  DK9PY x5, I2WIJ x4
              seen once MC1D EN4PVM ZA1EM
```

Every true call repeated; every false one appeared once. An operator sends their
call two or three times because the band is why they have to, and garble has no
reason to land on the same wrong six characters twice. So a call seen more than
once in the visible copy is marked strongly and one seen once is dimmed - and
neither is removed, because the first sighting of a real call is a single
sighting too.

A highlight says "this token has the shape of something an operator sends", not
"this token is correct". A marked call still has to be confirmed the way any
call is. What it can do is tell the eye where to look in a wall of characters,
and that is the job it is sold as doing.

---

## 8. What it scores

Against the ARRL W1AW code practice files, pitch 750 Hz, 500 Hz filter.
Accuracy is character-level against the published text. Noise columns are SNR in
dB in the filter bandwidth; `f20` is a 20 dB peak-to-trough fade, applied
**before** noise - so `f20n6` means peaks at +6 dB and troughs at -14 dB.

| WPM | clean | n12   | n9    | n6    | n3    | f20   | f20n12 | f20n6 |
|-----|-------|-------|-------|-------|-------|-------|--------|-------|
| 5   | 97.1% | 96.8% | 96.8% | 78.8% | 0.0%  | 97.1% | 96.4%  | 4.8%  |
| 10  | 95.9% | 96.3% | 96.3% | 93.0% | 25.0% | 96.3% | 34.1%  | 3.7%  |
| 13  | 98.9% | 98.9% | 98.9% | 97.2% | 49.9% | 98.9% | 40.5%  | 15.9% |
| 15  | 98.8% | 98.8% | 98.8% | 97.9% | 54.4% | 98.8% | 35.6%  | 10.1% |
| 18  | 99.9% | 99.9% | 99.9% | 98.7% | 67.1% | 99.9% | 37.5%  | 12.5% |
| 20  | 99.4% | 99.4% | 99.4% | 99.1% | 72.5% | 99.4% | 36.2%  | 17.0% |
| 25  | 99.5% | 99.5% | 99.5% | 99.1% | 77.8% | 99.5% | 44.2%  | 16.8% |
| 30  | 99.6% | 99.6% | 99.6% | 99.1% | 82.0% | 99.6% | 50.7%  | 21.5% |
| 35  | 99.3% | 99.6% | 99.3% | 98.5% | 83.4% | 99.3% | 49.3%  | 22.5% |
| 40  | 99.4% | 99.7% | 99.7% | 99.0% | 85.3% | 99.4% | 51.3%  | 16.9% |

Two things about this table are easy to misread.

**The low numbers in the deep cells are not all errors.** `CwBench` counts a
withheld character as wrong, so a cell where the reader correctly refuses to
guess scores the same as one where it guesses wrong. 5 WPM at `f20n6` scores
4.8% and emits 25 characters, of which 93% are labelled `Jumbled` - the reader
is not failing quietly there, it is declining out loud. The troughs in that cell
sit 14 dB *under* the noise; the correct target is honest labelling, not higher
copy.

**Empty-band cases must stay at zero.** Three minutes of recorded empty 15 m,
two dead-band probes and two diagnostic recordings emit **0 characters**. That
is a hard requirement, not a nice-to-have: every readability improvement above
was checked against it, because the easy way to raise every number in the table
is to lower the bar for calling something a signal.

---

## 9. Things known to be wrong

- **The dirty-run test is blind above ~30 WPM** (section 6.2).
- **`probe15c` / `probe15d`** - 90 s of audible CW that produces 624 marks with
  a median length of 15 ms and reads 97% `Chatter`. Not yet diagnosed.
- Many bench recordings have no sidecar describing what was on the air when they
  were made. Sidecars must not be invented after the fact.

## 10. Testing

```powershell
dotnet test tests/RadioWebControl.Core.Tests/RadioWebControl.Core.Tests.csproj -c Release
```

The suite must pass standalone in the `Radio_Web_Control_Core` clone, not only
inside an app - that is what catches an accidental dependency on a consumer.

The browser side has its own tests, which need no browser because the modules
under test are pure functions:

```powershell
node --test "tests/js/*.test.mjs"
```

The bare directory form the older headers gave fails on Node 24 under Windows -
it tries to load the directory itself as a module.

`tools/CwBench` in either app runs recordings and generated signals through the
decoder with `--noise`, `--fade`, `--pitch`, `--filter` and `--expect`.
`docs/design/cw-bench-procedure.md` in Yaesu Web Control describes the corpus.
The ARRL W1AW files are ARRL copyright: they are fetched locally by
`scripts/get-arrl-practice.sh` into a gitignored directory and are never
committed or redistributed.
