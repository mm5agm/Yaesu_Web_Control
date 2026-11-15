# Plan: a scoreable CW bench corpus from the Morse Code Ninja files

Status: planned, not started. Written 2026-08-31.

This is the plan for turning `Z:\Code\farnsworth` into a labelled bench corpus
that can score `Services/Cw` changes as a number rather than an impression.

The bench itself does **not** live here. Per `CLAUDE.md` it lives in the
consuming app repos, in a gitignored `bench/`, driven by `tools/CwBench`. This
document is the plan; the audio and the harness stay in the app repo.

---

## Why this corpus

The decoder declares 5-60 WPM (`CwElementDecoderOptions.MinWpm` / `MaxWpm`).
The Morse Code Ninja 20 wpm set covers roughly 20-30; the Farnsworth set covers
the missing low end. What made the Farnsworth set the starting point was that
it **ships ground truth**, which is the part that matters: nobody here reads CW
by ear, so an unlabelled recording can only ever be judged by eye.

Phase 1.5 below removed that asymmetry - the publisher's generator repo turns
out to hold the source text for both sets - so the corpus is now much larger
than this document originally assumed. The Farnsworth material is still the
best *first* target, because its 18 shipped transcripts need no pairing step
and it is the only source of sub-20 wpm effective speeds.

Measured 2026-08-31:

| | |
|---|---|
| Files | 54 audio over 9 pairs: char 20/25/30 x effective 5/10/15/20 |
| Labels | 18 `.txt`, one word per line, one per content set (shared by that set's 3 speeds) |
| Size | 1.5 GB; individual files run 3.5-6 hours |
| Tone | 700.0 Hz, fixed, no drift |
| Timing | machine-exact - dit standard deviation 0.00 ms |
| `20-5wpm` | dit 60 ms (char 20.0 wpm), word gaps median 720 ms = 12 dits, max 3.12 s |
| `30-20wpm` | dit 40 ms (char 30.0 wpm), word gaps median 1527 ms = 38 dits |

What it still does not contain: noise, QSB, QRM, adjacent signals, pitch
variety, or any timing jitter. It broadens **speed** and nothing else.

---

## Where the audio lives

`Z:` is a network share - **`\\192.168.68.66\colin`** - so the corpus paths in
this document (`Z:\Code\farnsworth`, `Z:\Code\cw-keep`) are a drive letter that
only exists on Colin's machine. Use the UNC path anywhere durable.

Measured 2026-08-31: sequential read runs at **104 MB/s**, so the share
saturates gigabit and is not a bottleneck - reading all 1.5 GB of Farnsworth
audio takes about 15 seconds. Phase 1 is CPU-bound on AAC decode and FFT, not
on the network.

**The curated output must still land locally**, in the app repo's gitignored
`bench/`, for three reasons: the bench re-reads its clips on every run,
decoded WAV is roughly ten times the size of the source m4a (~170 MB for 30
minutes at 48 kHz mono), and a bench that silently fails when the NAS is
offline is worse than no bench. Treat the share as the archive and the local
`bench/` as the working set.

---

## Phase 0 - preserve before deleting (do this first)

The 20 wpm set is being deleted. **Keep 44 files (3.3 GB of the 23 GB)** - the
29 `Speed-Racing` files plus 15 that cover things the Farnsworth set does not.

```powershell
New-Item -ItemType Directory -Force Z:\Code\cw-keep
$pat = 'Speed.?Rac|Practice QSO|POTA QSOs|SOTA QSO Practice|K1USN|' +
       'Common QSO Phrases - [234]x Word-Spacing|Sound Of Thunder|' +
       'Code Groups - 5 Letter|Sentences from Top 200 Words and Abbrev'
Get-ChildItem Z:\Code\20wpm -File |
  Where-Object { $_.Name -match $pat } |
  Move-Item -Destination Z:\Code\cw-keep
```

Why each group earns its place:

| Group | n | Covers what Farnsworth cannot |
|---|---|---|
| `Speed-Racing 30-27-24-20wpm` | 29 | speed changes **mid-file** - the only test of whether the dit tracker re-converges. Every Farnsworth file is one fixed pair. |
| `Practice QSO 1-4` + `Complete One-Sided`, `POTA QSOs`, `SOTA QSO Practice`, `K1USN SST` | 8 | **connected QSO flow.** The Farnsworth set is entirely isolated items with long pauses; this is the decoder's actual use case. |
| `Common QSO Phrases - 2x/3x/4x Word-Spacing` | 3 | word gaps stretched at a **fixed 20 wpm character speed** - an independent axis on `WordGapRatio` that Farnsworth cannot isolate, since it moves both. |
| `A Sound Of Thunder` | 1 | sustained running prose, minutes at a time. |
| `Code Groups - 5 Letters` / `5 Letter-Numbers` | 2 | random groups, **zero linguistic redundancy** - the purest CER measurement, with no context to flatter the reader. |
| `Sentences from Top 200 Words and Abbreviations` | 1 | connected prose with abbreviations. |

With the Farnsworth set (1.46 GB, 54 audio + 18 transcripts) that is **4.78 GB
over 116 files** - about 47 seconds to pull off the share.

None of these carry transcripts, so scoring them needs ASR on the spoken
answers. That cost is worth paying for the QSO-flow files in particular:
nothing else in either corpus tests continuous copy.

Everything else in `Z:\Code\20wpm` can go. The bulk of it is ~90 near-duplicate
"Sentences from Top N Words" drills that add vocabulary, not coverage.

---

## Phase 1 - curate and segment

1. **Transcript format (resolved 2026-08-31).** Lines read
   `TOKEN [spoken form]^` - e.g. `AA0F [A A 0, F]^`, `<BT> [B T]^`,
   `GL [Good luck]^`. The **CW is the bare token before `[`**; the bracket is
   the phonetic read-out that gets spoken, and `^` terminates the item. So
   ground truth is `line.split('[')[0].strip()`. Verify on one file against the
   audio before trusting it across all 18.
2. **Confirm the speech layout.** The audio alternates CW with the answer
   spoken aloud (visible as broadband events roughly every 13 s). Confirm the
   pattern holds across the set, then segment on 700 Hz tone energy against
   broadband energy.
3. **Cut to clips.** 3.5-6 hour files are unusable as a bench. Target ~2-4
   minutes per pair, ~30 minutes of corpus over all nine.
4. **Write a sidecar per clip** carrying the source file, the time offsets, the
   char/effective pair, and the expected text. Ground truth here is genuinely
   external - it comes from the publisher's transcript - so the sidecar may
   state it, but it must say *where it came from*. Per `CLAUDE.md`: never
   invent history for a sidecar, and where a file has no external ground truth
   it must say so.

### Symbol coverage and a scoring trap

The Farnsworth transcripts exercise A-Z, 0-9 and `/`, and the
`Top 90 QSO Elements` sets carry prosigns: `<BT>` x84, `<AR>` x42, `<KN>` x42,
`<SK>` x42. Coverage is better than expected.

**But three of those four are not prosigns in `MorseTable`** - they are
punctuation, and the scorer must normalise the expected text or it will count
210 correct decodes as errors:

| Transcript | Sent as | `MorseTable` returns |
|---|---|---|
| `<BT>` | `-...-` | `=` |
| `<AR>` | `.-.-.` | `+` |
| `<KN>` | `-.--.` | `(` |
| `<SK>` | `...-.-` | `<SK>` |

Still untested by either corpus: `. , ? - ) ' : ; @ _ $ !` and the `<SN>`,
`<CT>`, `<HH>` prosigns.

## Phase 2 - scoring

Character error rate against the transcript, in `tools/CwBench`. **Record a
baseline across all nine pairs before changing anything.** A bench with no
before-number cannot judge an after-number.

## Phase 3 - the two targeted investigations

**(a) `WordGapRatio = 1.8`** - `CwElementDecoder.cs`. The word gap is 1.8x the
*measured* character gap, so the design is already Farnsworth-aware and this is
validation, not a bug hunt. But its rationale rests on **three ARRL files**
(measuring 3.68, 2.34, 2.35). Measure that ratio's distribution across all nine
pairs and check 1.8 still leaves room on both sides. Expected outcome: it
holds, with a much broader evidence base recorded in the comment.

**(b) `MarkDecayDbPerSec = 4.0`** - `CwToneDetector.cs`. This is the sharper
test. A `20-5wpm` word gap reaches 3.12 s, so the mark level falls up to
**12.5 dB** toward its 6x-noise stop - and then the next character arrives at
20 wpm with 60 ms dits. That is harder than true 5 wpm, where the following dit
is 240 ms and easy to catch, and it is a case no other material produces. The
gaps stay under `GraceHops` (5.5 s), so grace will not mask the result. Run it
clean first, then under QSB.

## Phase 4 - impairments (synthesis)

Only after Phases 1-3 have a baseline. In value order:

1. **Adjacent interferers** - the only cover for the tone search
   (`TrackBandHz`, `LatchConfidence`, `LoudBinProminenceDb`). Commit 85ad532
   fixed a click-sideband bug that no amount of white noise could have found.
2. **Pitch offsets** - the corpus is 700.0 Hz only; the search covers a whole
   passband.
3. **QSB and flutter** - directly exercises the level and presence gates, and
   compounds with Phase 3(b).
4. **Swept SNR** - the axis that yields a CER-vs-SNR curve, which is the real
   per-change deliverable.

## Phase 5 - the negative test

The speech segments are hours of loud broadband audio that is definitively not
CW. Section 6 of `cw-decoder.md` commits that plausible garbage is worse than
silence: if the readability gate emits anything during the spoken answers, that
is a real defect. This needs no synthesis and could be built first.

---

## Phase 1.5 - the corpus is exactly labelled (2026-08-31)

The audio is Morse Code Ninja, by Kurt Zoglmann AD0WE - the m4a tags name him,
and the generator is open source at `github.com/zoglmannk/Morse-Code-Ninja`.
Its `practice-sets/` directory holds the *source text of every published set*,
3473 files, in the same `TOKEN [spoken form]^` format as the 18 transcripts the
Farnsworth downloads already shipped. So the labelling problem is solved: the
20wpm set has ground truth, exact and in order, and none of it has to be
guessed.

383 of the 420 local files are paired, giving **580,177 labelled items**.
Sidecars are written next to the audio as `<same name>.txt`, with
`TRANSCRIPT-SOURCES.json` recording, per file, which repo text it came from and
how the pairing was established.

### ASR's real job here was verification, not labelling

Whisper `small.en` per speech region cannot generate labels - measured against
the shipped Farnsworth transcripts it gets 78% of items right, and it fails
hardest on exactly what most of this corpus is: isolated monosyllables with no
context (51% on 3-letter words; bed heard as "ved", leg as "thank you"), while
scoring 96% on QSO elements its language model already knows. A reference that
wrong is worse than none, because a decoder error and a label error become
indistinguishable and a constant tuned to close the gap moves at random.

But 78% is ample to answer a *yes/no* question about whether a candidate text
is the right one, because the two answers are ~80% and ~0%, not 80% and 70%:

| pairing | check | in-order agreement |
|---|---|---|
| name matches after dropping the wpm suffix | 3 Letter Words, Top 90 QSO Elements, US States | 79 / 81 / **99**% |
| name matches exactly, incl. wpm | Sentences from Top 100 Words - 3x Word-Spacing | **100**% |
| naming-only rewrite (Two Letter-Number -> ... Combinations) | Two Letter-Number | 95% |
| `v3` suffix dropped | Field Day Exchange v3 | 98% |
| `Nx Character Spacing` -> base text | USA Call Signs - 4 Characters - 4x | **0%** |
| `Alt` -> base text | Single Letter-Number Alt Rapid-Fire | **8%** |

US States at 99% is the proof: where ASR is reliable, agreement is total, so
the residual everywhere else is ASR error and not text error.

**The last two rows are why every inferred pairing must be checked.** A spacing
or `Alt` variant is an independent draw from the same pool, not a re-render of
the same text - it looks right by name, the vocabulary matches, and it is
wrong. Both were rejected on this evidence and their sidecars deleted. The 37
files left unpaired are mostly that class, and are better unlabelled than
wrongly labelled.

### Details worth keeping

- **Transcript format.** `TOKEN [spoken form]^`. The `[...]` field may carry
  `|`-separated alternatives - take the first - and backslash-escaped punctuation:
  `QRM\? [Q R M \?|QRM\?]^`. Score the token, not the spoken form.
- **Speed-specific texts live in subdirectories** (`extra-word-spacing/`,
  `spoken-letter-by-letter/`, the course folders). Match the full name
  including the wpm suffix *before* stripping it, or a word-spacing file
  silently pairs with the wrong base text.
- **Clone somewhere short.** Deep course paths exceed MAX_PATH under a normal
  scratch directory and fail to check out.
- **Separating CW from speech**, needed for Phase 1 and Phase 5 too: measure
  energy at the CW tone against the frame total, dilate the CW mask by ~150 ms,
  and take the speech floor from the median of what is left. A fixed low-band
  formant test fails - the AAC encoding puts noise-fill energy there - and
  Silero VAD at its default threshold drops the quiet single words.
- **Transcribe each region separately** with `condition_on_previous_text=False`.
  One pass over the file merges neighbouring items and drops some entirely.

---

## Rules that travel with this work

- **Do not time-stretch to fake speeds.** Resampling drags the pitch with it;
  a pitch-preserving stretch reshapes the key edges, which is the exact signal
  the envelope path measures. Download real files at other speeds instead.
- **Tune on synthetic, validate off-air.** The decoder is tuned constants, not
  a fitted model, so overfitting happens through us moving numbers. Never move
  a constant on a synthetic score alone.
- **No audio in git.** The corpus is local-only, like the existing `bench/`.
- **Still missing, and only off-air provides it:** real timing jitter from a
  human fist, chirp, drift, AGC pumping, band noise that is not Gaussian.
