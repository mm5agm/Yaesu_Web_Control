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

That paragraph described the intent, and for six months the code beside it still
waited for six gaps. Six were enough to do the same damage on a smaller scale:
over the Morse Code Ninja practice corpus on 2026-09-01, a Farnsworth
recording's first entry came out `N 7 M O` and its second, identical sending
came out `N7MO` - the splitting stopped the moment the window filled. Straight
sending never showed it, which is why the ARRL table above never caught it.

The window is also emptied when a transmission starts after
`ReadabilityMaxAgeSeconds` of no key-down runs at all. The measured character
gap is a property of one operator's fist, and carrying a Farnsworth beginner's
stretched gaps into the next station up on frequency puts the word split in the
wrong place until the percentile migrates.

Splitting words is the other half of the same question, and it was answered by a
model rather than by measurement until 2026-09-01. The rule was "a word gap is
1.8 times the measured character gap", which assumes that whatever an operator
does to their character gaps they do proportionally to their word gaps.

Real fists do not oblige. `bench/mkii-i1yrl.wav` is the only plain-QSO recording
in the corpus - an ordinary 20 m contact, not contest traffic - and measured
straight from its audio the character gaps centre on 4.4 dits and the word gaps
on 5.8. A ratio of 1.3, not 2.33. The operator had loosened his character gaps
and left his word gaps where they were. Scaling from one to the other put the
split at 6.7 dits, above two thirds of his word gaps, and the reader printed
`TKSFORINFOAND`, `QTHNRTIN` and `CQCQCQDE`.

So the two populations are now separated rather than derived from each other:
two-means on the log of the recent separator gaps, seeded where the ratio rule
would have put them, and the split taken as the geometric mean of the two
centres it settles on. Logs because the quantity is a ratio, and because one
long pause then cannot dominate the arithmetic.

Keyed with that exact fist against known text, the ratio rule returns
`TKSFORINFOANDURRST599QTHNRTURINNAMELUC` - the whole sentence welded into one
word, 77.6% - and the measured split returns everything but the first three
gaps, 93.8%. The three it misses are the ones before the window has twelve gaps
in it to cluster.

**What it costs.** Measuring instead of assuming is not free, and the ARRL table
in section 8 shows where the bill lands: the clean, `n12`, `n9`, `n6` and `f20`
columns do not move at all, the deep-fade `f20n6` column gains about 0.4 points
throughout, and the 3 dB SNR column loses at the top of the speed range - 30 WPM
-0.5, 35 WPM -1.2, 40 WPM **-2.3**. In that corner noise breaks a real gap in
two with a spurious mark, the fragments pile up into a second population that
clusters beautifully and means nothing, and the split comes down far enough to
put spaces inside words.

Four guards were tried against that and three of them measured completely inert
on it - a minimum cluster population, a minimum separation between the centres,
and a minimum number of gaps before clustering at all all left 40 WPM at 3 dB
unchanged to the decimal. Only clamping the split hard away from the character
gap moved it, and it moved it by 0.2 points while undoing the fix. The cost is
intrinsic to the method at that speed and noise, so it is recorded rather than
tuned away. The guards were kept anyway: the population guard is worth 13 points
at 5 WPM and 3 dB on its own, which is where fragmentation hits a Farnsworth
sender hardest.

The trade is taken deliberately. An operator working a 20-something WPM QSO with
a loose fist is the ordinary case, and it goes from unreadable to readable; 40
WPM at 3 dB SNR is not, and it goes from 88.9% to 86.6%.

The clamp is one-directional by construction - the measured split may fall below
where the ratio rule would have put it but never above - so this change can only
add word gaps that were being missed and can never remove one that is being
found today. That asymmetry is the same one the character gap argues for: a
missed word gap costs one edit per word, a spurious one costs an edit per
character and reads as gibberish rather than as running text.

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

### 6.3a Elements per character - and the case the other three all miss

The three tests above all ask about marks. This one asks about characters, and
it exists because a detector chattering on a dead band passes every one of
them. Its blips come in two clean lengths, so the spread is textbook; they
alternate short and long, so the letters are `E` and `T` rather than `E`, `I`
and `S`, and the dit-only test sees nothing; and each blip is flushed as its
own character, so nothing is discarded and the dirty-run fraction sees nothing
either. `bench/diag-dead.wav` is twelve seconds of nothing at 21 MHz and reads:

```
SETE ET TE E T TE E        Readable 81%   58.6 wpm   locked
```

Which is the worst thing the panel can show: a confident speed, a lit lock, and
an empty band.

Morse cannot average near one element per character, because the alphabet does
not contain enough one-element letters. Over the 11,497 characters of the ARRL
practice texts the sent mean is **2.67 elements per character**, and only 20.1%
of characters are a single element - `E` and `T` are the two commonest letters
in English, which is precisely why they are the two shortest. Decoded, over
seven recordings from 12 to 2,489 characters and 5 to 40 WPM, the mean ran
**2.33 to 3.14**. The dead band ran **1.15**.

Sweeping the floor across those same recordings:

| floor | diag-dead | every real recording |
|---|---|---|
| off  | Readable 81%, Jumbled 0%  | - |
| 1.40 | Readable 12%, Jumbled 69% | untouched |
| 1.70 | Readable 12%, Jumbled 69% | untouched |
| 2.00 | Readable 12%, Jumbled 69% | untouched |
| 2.30 | Readable 12%, Jumbled 69% | 5 WPM -1 point, 40 WPM -4 |

Three settings are indistinguishable because the gap between 1.15 and 2.33 has
nothing in it, so the floor sits at **1.70**, in the middle of it. The window
is the same 48 characters the dit-only test uses, but it needs only 10 rather
than 24: the dead band produces about twenty characters in twelve seconds, so a
test that waits for twenty-four never engages on the one recording it exists
for.

Every cell of the section 8 table is unchanged to the decimal place, which is
what "score, never edit" is supposed to mean.

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

**That clock only runs once there is a verdict to be stale.** `Unknown` covers
two states that read alike and mean opposite things - "not enough marks have
arrived to judge" and "the marks being judged have gone stale" - and only the
second is a reason to throw the hold away. The first is where every
transmission starts. Started at the first frame, the 5 s horizon expired before
a slow sender could produce its tenth mark: a 5 WPM Farnsworth message reaches
that mark at about 8.4 s, so `CQ CQ DE MM5AGM MM5AGM K` came back as
`CQ DE MM5AGM MM5AGM K` - every character correct, the opening gone, which on
air is the callsign. `CwElementDecoder.HasReadabilityVerdict` separates the two
by mark count, since the windows fill and never empty on their own.

The other half is that they must empty *sometime*, or a station arriving after a
quiet band is judged on the last one's marks. They are cleared on the first edge
after `ReadabilityMaxAgeSeconds` without a key-down run - on the first edge
rather than when the silence passes the horizon, because clearing eagerly
answers "no idea" for a band that has just been judged to be chattering, which
is the one verdict an operator most needs to keep seeing. Note "no key-down run",
not "no usable mark": a detector chattering across its hysteresis produces runs
that are all discarded as too short, so the accepted-mark clock stops dead while
the band is anything but quiet.

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

**A `Jumbled` verdict cancels the hold outright** rather than waiting it out.
That grace is for absence of evidence - marks stopping or weakening, which is
what a fade is - and `Jumbled` is the opposite: marks are still arriving and
they are actively not Morse. Waiting it out is how `diag-dead.wav` went on
reporting 58.6 wpm and `locked` for six seconds after 6.3a had already
condemned it. Measured over five real recordings the change costs exactly
nothing - `mkii-i1yrl` 150 locked seconds of 239 before and after,
`mkii-dk9py` 128 of 179, `cq-qso` 64 of 88, ARRL 20 WPM 469 of 471, ARRL
40 WPM 790 of 791 - because real copy spends 0-3% of its marks Jumbled.

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

Regenerate it with `scripts/cw-arrl-table.ps1`, which is the whole reason the
script exists: the version of this table published before 2026-09-01 was stale
by up to 80 points in the hardest cells, because several rounds of work on the
detector had gone in and nobody could cheaply re-measure it.

| WPM | clean | n12   | n9    | n6    | n3    | f20   | f20n12 | f20n6 |
|-----|-------|-------|-------|-------|-------|-------|--------|-------|
| 5   | 97.3% | 97.3% | 97.3% | 96.8% | 61.6% | 97.3% | 97.3%  | 85.3% |
| 10  | 96.5% | 96.5% | 96.5% | 96.5% | 93.7% | 96.5% | 67.6%  | 47.2% |
| 13  | 98.8% | 98.8% | 98.8% | 98.7% | 97.9% | 98.8% | 60.2%  | 42.8% |
| 15  | 98.6% | 98.6% | 98.6% | 98.6% | 97.2% | 98.6% | 58.2%  | 40.0% |
| 18  | 99.8% | 99.8% | 99.8% | 99.8% | 98.6% | 99.8% | 61.9%  | 43.8% |
| 20  | 99.3% | 99.3% | 99.3% | 99.3% | 99.1% | 99.3% | 62.7%  | 41.5% |
| 25  | 99.4% | 99.4% | 99.4% | 99.3% | 98.4% | 99.4% | 65.7%  | 44.5% |
| 30  | 99.5% | 99.5% | 99.5% | 99.5% | 95.2% | 99.5% | 65.1%  | 43.2% |
| 35  | 99.3% | 99.6% | 99.3% | 99.2% | 91.2% | 99.3% | 62.5%  | 42.3% |
| 40  | 99.5% | 99.7% | 99.7% | 99.0% | 86.6% | 99.5% | 62.8%  | 39.2% |

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

### 8.1 The Morse Code Ninja practice corpus

The ARRL files are ten recordings of one sender reading QST. The Morse Code
Ninja practice set is about 105 recordings of callsigns, contest exchanges, QSO
phrases, US states and word drills at 20 WPM and Farnsworth, which is a far
better spread of the tokens an operator actually copies. It is third-party
material and lives outside the repository entirely - see
`D:\cw-keep\CORPUS-NOTES.txt` on Colin's machine for how it was obtained and
what its two traps are.

Scoring it is not the same job as scoring the ARRL set, because every entry is
**sent in Morse, spoken aloud, and sent again**. Two consequences:

- The bracketed text in the `.txt` - `K9NR [K 9, N R]`, `GM [Good morning]` - is
  the spoken gloss and is never keyed. Scoring it as sent text put callsign and
  abbreviation files at 20-40%, which looks exactly like a real weakness of the
  decoder on callsigns and is not one.
- The spoken word is not a condition the decoder meets in service. Files are
  scored three ways: raw, through a 500 Hz bandpass (what the radio's CW filter
  does), and with the speech blanked out by a spectral mask. **The mask has to
  be computed on the raw audio** - computed on the bandpassed copy it keeps
  everything, because the filter has already made almost all the remaining
  energy in-band.

Twelve representative files, 150 s from t=0, longest-common-subsequence over
run-length-collapsed words (which makes the repeat count irrelevant rather than
something to infer - three attempts to infer it from gap structure all failed,
because the two sets' gap layouts are inverted):

| | raw | 500 Hz | speech blanked |
|---|---|---|---|
| mean of 12 files, 2026-09-01 | 62.9% | 67.5% | **88.0%** |

The bandpass improved nine of the twelve and degraded none, which is the
measurement that says a CW filter is worth having in front of this.

The corpus also measures something the ARRL table cannot: **how much is lost
before the reader locks on**. Character accuracy over a five-minute file hides
three characters at the front; on air those three characters are the callsign.
Over 29 files, 90 s each:

| | files whose first entry was copied cleanly | mean junk words before it |
|---|---|---|
| before 2026-09-01 | 12 of 29 | 1.34 |
| after | **27 of 29** | **0.03** |

Both remaining failures are harness artefacts, not decoder faults - one is a
file of single letters, which the "first correctly copied entry" search skips by
construction.

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
