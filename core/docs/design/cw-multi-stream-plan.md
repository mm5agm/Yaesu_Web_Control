# Reading more than one CW stream at once

A plan, not a description: none of this is built yet. It lives beside
`cw-decoder.md`, which describes the single-stream reader as it stands, because
nearly all the work is in the core - the app contributes an endpoint and a
handful of markup.

---

## 1. What is being asked for

Colin's six points, and what I think of each. Four I agree with as stated; two I
want to change, and both changes come from the same place - the display has to
stay still while the band moves.

| # | Asked for | Verdict |
|---|---|---|
| 1 | A separate line per decoded stream | Agreed as stated |
| 2 | Each line scrolls right, never wraps | Agreed, and it matters more than it looks |
| 3 | Each stream in a different colour | Agreed in substance, but the colour has to go somewhere else |
| 4 | The existing box is big enough | Agreed, and it sets the stream limit at four |
| 5 | The tuning indicator locks to one stream | Agreed, necessarily |
| 6 | It should lock to the loudest | Agreed as the opening pick, wrong as the standing rule |

### 1.2 Why the no-wrap rule is doing more work than it appears

Wrapping is not just untidy here. If a line wraps, its height changes as
characters arrive, so every stream below it moves down the screen. The operator
would be tracking a moving target while trying to read it.

Fixed one-line lanes keep each stream at a constant screen position for as long
as it is live. That is what makes "the one at 620 Hz" something you can watch out
of the corner of your eye, and it is the property the whole display rests on.

The lanes must also be ordered by **frequency, low to high, never by loudness**.
Ordering by loudness would reshuffle the rows every time a signal faded, which
throws away the fixed position that point 2 just bought - and frequency order
makes the pane a map of the passband that lines up with the spectrum above it.

### 1.3 Where the stream colour goes

There is a clash here that has to be settled before anything is drawn: **colour
in the copy is already taken.** The reader now colours prosigns, reports and
callsigns inside the text (`cw-decoder.md` section 7.3). If stream identity also
coloured the text, the two schemes would fight, and the one that lost would be
the token markup - the only part carrying information about whether the copy is
worth believing.

So stream identity goes on the *furniture*, not the text:

```
[#] 575 Hz  | CQ CQ DE SO5O SO5O CWT TEST                      >
[#] 625 Hz  | ...WT SM6M SM6M TU 5NN                           >
[#] 780 Hz  | E EE ISH TEEE                                    >
     ^         ^
     |         the text keeps its own token colours
     swatch and frequency label carry stream identity
```

The frequency label, not the colour, is what actually identifies a stream: it is
unambiguous, it is what the operator would say out loud, and it does not exclude
a colour-blind operator. The colour is the fast index, and the swatch is enough
to carry it.

### 1.4 Why "lock to the loudest" cannot be the standing rule

Locking to the loudest **at the moment a stream is first picked up** is right.
Leaving it there is not, and the reason is QSB.

On a fading band the loudest signal changes every few seconds. A tuning
indicator that followed it would hop between stations mid-QSO - and it would hop
hardest exactly when the band is worst, which is when the operator is actually
looking at it. The indicator would be least trustworthy at the moment it is most
needed.

The rule instead:

- **Open on the loudest** stream when there is no selection.
- **Stay there.** Switch automatically only if another stream is louder by
  `SwitchMarginDb` (start at 6 dB) *continuously* for `SwitchHoldSeconds` (start
  at 5 s), or if the selected stream retires.
- **The operator can click a lane to select it**, and clicking pins it: no
  automatic switching until they unpin. Working a station is a decision the
  operator has made, and the display should not second-guess it.

---

## 2. What Colin did not ask about, and has to be decided anyway

**The speed and lock readouts become per-stream.** `wpm` and `locked` currently
sit at the bottom of the panel describing "the signal". With four streams that
number belongs to one of them, and an unlabelled speed would be worse than none -
the same failure the empty-band lock fix (`cw-decoder.md` section 7.1) was
written to stop. Each lane carries its own speed, or none does.

**Readability is per-stream too**, and this is where multi-stream is genuinely
harder rather than just bigger. The three checks in section 6 need a population
of marks to work on. Splitting a busy band four ways can leave each stream too
thin to assess, and a stream with too few marks must be shown as *unassessed*
rather than quietly scored - the whole point of the readability work is that the
panel never vouches for something it has not measured.

**The empty band must still produce nothing.** Zero lanes, zero characters, on
all five empty-band and dead-band recordings. This is the hard requirement from
the existing bench and it gets harder here: a peak finder run over noise will
find peaks. It is the first test to write, before any decoding.

**One station must not become two lanes.** A strong signal has skirts and key
clicks, and a naive peak finder will split it. Minimum separation and a real
prominence dip between peaks are what prevent that, and the numbers have to be
measured, not guessed.

**Two stations closer than the resolution will become one lane**, and that lane
will interleave two fists into nonsense. This one cannot be fixed at this FFT
size - so it must be *labelled*. A lane whose peak is abnormally wide and whose
mark-length spread is bad is a crowded lane, and saying so is the honest
handling. Silently printing the interleaving is not.

**The IF filter decides whether any of this pays.** At 300 or 500 Hz there is
often only room for one CW signal in the passband at all. The feature earns its
keep at 2-3 kHz, which means the panel should say when the filter is too narrow
for multi-stream to be meaningful, rather than showing one lonely lane and
looking broken.

---

## 3. Phase 0: measure before building any of it

**This phase decides whether phases 1-5 happen.**

The last two decoder ideas both turned on a measurement taken before the code
was written. The mark-level latch was real and worth fixing; the
word-plausibility veto looked obviously right and would have destroyed exactly
the callsigns the operator wanted. There is no way to tell those two apart by
reasoning about them, so the same discipline applies here.

Add `CwBench --streams`: run the existing FFT over a recording and report, per
5-second window, how many separable keyed peaks are in the passband, with their
frequencies and levels. No decoding, no slots, no UI.

Run it over the whole corpus and answer:

1. **How often is there more than one keyed signal at once?** The existing
   `--spectrum` cannot answer this: it averages the whole file, so two stations
   that took turns smear into one blob. On `cwt-so5o-1.wav` it reports energy
   spread across 525-800 Hz and "something keyed around 300 Hz" - which is
   consistent with two simultaneous stations and equally consistent with one
   station drifting, and those need different features.
2. **What separation is achievable?** The FFT is 64 ms, so bins are about 16 Hz.
   The minimum separation that does not split one station is an empirical
   number.
3. **Do the empty-band recordings report zero streams** at the chosen
   thresholds? If they do not, no threshold is safe and the peak finder needs a
   different discriminator before anything is built on it.
4. **What does keying ratio buy?** A steady carrier or a birdie is not CW, and
   `--spectrum` already computes a per-bin keying ratio. That is probably the
   discriminator that keeps the noise floor out - worth confirming.

**Kill criterion, stated in advance:** if the corpus shows that at the filter
widths actually in use the passband rarely holds more than one keyed signal,
this is mostly cost, and the right outcome is to write that up and stop. The
useful part - a spectrum that marks every peak it can see - is already half
built.

---

## 4. Phases 1-5, if phase 0 says go

**Phase 1 - core detection, no decoding.** `CwToneDetector.FindPeaks()` over the
span it already captures: local maxima above `PeakThresholdDb` over the span
median, with prominence over `ProminenceDb` against the higher adjoining
minimum, no closer than `MinSeparationHz`, and keyed rather than steady. Feeding
a slot table:

- A fixed array of four `CwStream` slots, each with a stable id and colour index.
- A peak within `MatchHz` (about 40 Hz) of a live slot updates that slot's
  tracked frequency - streams drift, and identity has to survive drift.
- Otherwise it takes a free slot. If none is free it may replace the slot that
  has been silent longest, and **only** if that silence exceeds
  `RetireSeconds`. A live stream is never stolen out from under the operator.
- `RetireSeconds` starts at 20 s: long enough to survive a turn-round, because
  renumbering and recolouring every lane mid-QSO is the worst thing this
  display could do.

Tests: empty band yields zero slots; one strong signal never yields two; a
known two-station file yields two with the right frequencies.

**Phase 2 - core decoding, per stream.** Each slot gets its own Goertzel at its
tracked frequency, its own envelope and threshold state, its own
`CwElementDecoder`, its own text buffer, readability and speed lock. The FFT is
shared, so the marginal cost per stream is the current reader minus its FFT -
cheap enough for four, which is a second reason not to raise the limit.

**Phase 3 - app.** `/api/cw/poll` returns a `streams` array of
`{id, hz, colourIndex, cursor, text, readability, wpm, locked, snrDb, selected}`,
each with its own cursor, since streams produce text at different rates.

**Phase 4 - the lanes.** Frequency-ordered rows: swatch, frequency label, and a
`white-space: pre; overflow-x: auto` lane pinned to its right edge, rendered
through `cw-tokens.js` so the token colours survive. Each lane scrolls itself;
a shared scroll would misalign streams sending at different speeds. Click a lane
to select and pin it.

**Phase 5 - the tuning aids follow the selection.** The spectrum marks every
detected peak and draws the selected one distinctly; the phasor tracks the
selected stream only. Selection follows the rules in section 1.4.

---

## 5. Open questions

- **Does the lane show held text?** Section 6.4 holds unreadable text and
  releases it when the signal proves itself. Four lanes each holding up to 64
  characters could release a burst into a lane the operator is mid-way through
  reading. Probably fine, possibly needs the release to be paced.
- **Should a retired lane disappear or grey out?** Disappearing reflows the rows
  and breaks the fixed-position rule; greying keeps the position but spends a
  lane. Leaning towards greying, with the slot reusable once a new stream needs
  it.
- **Four or three?** Four fits the existing box at the current line height. Three
  larger lanes may read better. Decide from a real contest recording, not from
  a mock-up.
