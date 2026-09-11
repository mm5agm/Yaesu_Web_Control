# The SDR spectrum trace does not match what the receiver hears

**STATUS: FIXED in the browser 2026-09-11 and verified on air on both receivers - see "Implemented" below. Originally: measured, diagnosed, NOT fixed. 2026-09-10, RX only, against the
FTdx101MP on 20 m CW. CORRECTED 2026-09-11 - read the next section before
anything else: Fault 1 (the mirror) is withdrawn, the offset I retracted is
real, and the SUB receiver's SDR is 100 kHz off.**

This is the write-up of a full session's measurement. Everything below was
measured on air; where something is inference rather than measurement I say
so. Nothing here has been fixed, and any fix is device-level, so it must be
bench-checked against the radio and never signed off on a build.

## Correction, 2026-09-11: the mirror was wrong, the offset was right

I re-measured everything the next day with a better instrument - a single
strong steady carrier (BBC Radio Scotland, 810 kHz, 100 kW, Colin's
suggestion) tracked bin-by-bin instead of cross-correlating band noise - and
most of the 09-10 conclusions below do not survive. I am leaving the original
text in place, marked, because the reasoning errors are the useful part.

**The sign is correct.** Raw hub frames are pre-flip; `spectrum-panel.js`
`update()` reverses them once (`bins.reverse()`, added by `55a9b5e`), and I
had been reading raw frames upstream of that. Raw lag +146 bins, reversed
-146, at 62.5 kHz. So both of Fault 1's supporting claims were false: there
IS inversion handling in the codebase, and the sign HAD effectively been
tested by whoever added the reverse. The "two independent methods" agreed
because both read the same un-flipped frames.

**The scale is correct.** Stepping the carrier 0-25 kHz in 5 kHz steps at
9.7 MHz fitted 60.976 Hz/bin against 61.035 nominal (ratio 0.999). The 0.891
ratio I chased for a while was an artefact of cross-correlating sparse keyed
CW - the ratio varied 0.867 / 0.891 / false peak / 0.999 with step size,
which no real scale error does. Never correlate band noise; track one carrier.

**The offset is real, and my retraction of it was wrong.** The argument was
"the notch sits on the markers, so the offset must be ~0". But the notch is
at the SDR centre, and the SDR centre is drawn at the dial *by construction* -
the axis is `dial + (bin - N/2) * hzPerBin`. The notch lands on the markers
whatever the offset is. That test cannot see an offset, so it cannot retract
one.

Measured with the receiver as ground truth (CW-U audio tone = RF - dial +
pitch, so RF = dial + tone - pitch, read from `/api/cw/spectrum`), with the
SDR at 9.000 MHz:

**MAIN receiver (VFO A SDR, RSPdx):** `true RF = displayed RF + O`

- base **+5060 Hz** at narrow CW widths = the FTdx101's **9.005 MHz first
  IF** (the operating manual, p.17: "IF OUT (MAIN) ... 9.005 MHz IF signal")
  plus ~60 Hz.
- IF SHIFT adds 1:1 (+500 -> 5811, -500 -> 4809).
- CW-U and CW-L: **`O = 5060 + shift + max(0, (width - pitch) / 2)`**, within
  2 Hz at every point: widths 100/250/500 -> 5060; 1200 -> 5310; 3000 ->
  6209; 4000 -> 6710; 1200 @ pitch 500 -> 5409; 1200 @ pitch 900 -> 5210;
  500 @ pitch 500 -> 5059. The radio keeps the CW filter's lower edge at or
  above pitch/2 by moving its LO.
- USB and LSB: a lookup by SH code, not a formula. Codes 3/5/9/11 -> 5059;
  12 -> 5109; 13 -> 5310; 14 -> 5409; 15 -> 5560; 16 -> 5709; 17 -> 5909;
  18 -> 6209; 19 -> 6310; 20 -> 6459; 21/22/23 -> 6710 (saturates). Shift
  still adds 1:1 on top.
- AM and DATA-U follow the same lookup by whatever SH code the receiver is
  holding for that mode.
- Constant to +-1 Hz across a 1 kHz dial sweep in 100 Hz steps - no coarse
  LO stepping.

**SUB receiver (VFO B SDR, RSP1):** the manual says **"IF OUT (SUB) ... 8.900
MHz"**, and the single `SdrIfFrequencyHz` setting (9.000 MHz) tunes both
SDRs. So the SUB panel is looking **100 kHz above the SUB dial**: base
`O = -99984 Hz`. With the SUB dial at 805 kHz the 810 kHz carrier was not in
the window at all; I found it with the dial at 905 kHz, 56 dB over the floor,
fixed to +-1 Hz over a 903-907 kHz sweep. Every width / shift / pitch / SH
code increment then matched MAIN within 2 Hz (1200 -> +249, 3000 -> +1149,
4000 -> +1650, shift +-500 -> +499/-499, pitch 500/900 @ 1200 -> +349/+148,
SSB 12..21 -> +50 +250 +350 +499 +649 +850 +1150 +1249 +1400 +1650). Same
radio behaviour, different constant. After removing the IF centre the
residual is +60 Hz on MAIN and +16 Hz on SUB - different per dongle, so that
part is SDR ppm, not the radio.

**Consequences.**

- Click-to-tune on CW lands 5-6.7 kHz off on MAIN and ~100 kHz off on SUB.
  The pitch offset in `48e6866` could never have fixed item 2 on its own -
  and, as it turned out (see "Implemented"), it was wrong in itself.
- The SUB SDR needs its own IF centre (8.900 MHz for the FTdx101). Until it
  has one, an A/B spectrum comparison is meaningless.
- Recommended shape of the fix: keep the MAIN SDR at 9.000 MHz (the 5 kHz
  gap keeps the DC notch away from the dial), give the SUB SDR 8.895 MHz for
  the same 5 kHz gap, and apply the mode/width/shift/pitch term in the
  browser from state it already receives (`ModeA/B`, `IfWidthA/B`,
  `IfShiftA/B`, `CwPitch`). It is a per-radio table, so it stays in this
  repo, not `core/`.
- Only the FTdx101MP is measured. The FTdx10's IF OUT may differ; do not
  assume.

**How I measured it, so it can be repeated.** Two traps cost most of the day:

1. With a strong carrier inside the CW filter passband the audio capture
   clips to a flat plateau (~+8 dB) and no carrier line is visible in
   `/api/cw/spectrum`; ATT and RF gain do not help because the AGC holds the
   level. Fix: open the IF to 3 kHz (SH 18), switch AF LCUT/HCUT off
   (`POST /api/cat/audiofilter/a/lcutFreq` and `hcutFreq` with
   `{"code":"00"}`), and park the carrier on the filter skirt at ~2700 Hz.
2. The AF filter hump (+30 dB at 500-950 Hz whatever the dial) fools a
   global peak-pick - the same trap as caveat 3 below. Measure prominence
   against a local +-100 Hz median, never the whole-band median.

Scripts (scratchpad, not committed): `dump.mjs` / `dumpb.mjs` tune a VFO and
average 60 frames; `fine.py` / `fineb.py` find the strongest bin with
parabolic interpolation and print `810000 - SDR`; `scan.py` reads the
receiver-side tone across a dial sweep.

## Implemented, 2026-09-11 (later the same day), and verified on air

Both halves of the fix are in.

**Per-SDR IF centre** (`c75867a`): `SdrIfFrequencyHz` became
`SdrIfFrequencyHzA` / `SdrIfFrequencyHzB`, defaulting to 9,000,000 and
8,895,000 - each 5 kHz below its IF OUT so the SDR's DC notch stays off the
dial. Old single-value settings migrate into A.

**Browser-side correction** (this commit). `wwwroot/js/sdr/if-out-offset.js`
holds the per-radio table - IF OUT per VFO, the SSB slide lookup by DSP
width, and the CW `shift + max(0, (width - pitch) / 2)` formula - and exports
`axisOffsetHz(model, vfo, sdrCentreHz, filterState)`. It stays in this repo,
not `core/`, because it is a table of one radio's behaviour. `SpectrumPanel`
gained `setAxisOffsetProvider()`; every Hz-to-pixel mapping in the panel
(axis, click, hover, pinned cursor, passband, band edges, band markers, DX
spots, crosshair) now goes through a single `_axisLeftHz()` so the offset is
applied exactly once. `Index.cshtml` wires the provider from
`FilterScopePanel.getFilterState()`, which is live state the browser already
had. Unknown radio models get `null` and are drawn dial-at-centre as before.
The SDR centre fed in is `_lastCentreHz` from each frame - what the SDR is
actually tuned to, not the setting.

**The visible change:** the amber dial marker is no longer at the canvas
centre. On the FTdx101 it sits ~5 kHz left of centre, because the centre is
where the SDR is looking and the dial is not. Signals are drawn where they
are.

**Verified** with the same 810 kHz carrier, but this time reading the panel's
own mapping from a headless browser (Playwright, `axischeck.mjs` in the
scratchpad: load the page, sample `_lastBins`, find the strongest bin, ask the
panel where it drew it). Error = drawn RF - 810,000, dial at 805,000, one bin
= 61 Hz:

- MAIN, CW-U: width 1200 +11; 3000 -13; 4000 +10; 250 -66; shift +500 -15;
  shift -500 -69; pitch 500 -24.
- MAIN, SSB: USB 2300 +7; 3000 -78; 3200/3500/4000 +3/+3/+11; 1800 -65;
  LSB 2700 -73.
- SUB (centre 8,895,000), CW-U: 1200 +9; 3000 +6; 3000 @ shift +500 +8;
  USB 2700 +11.

All within one bin. The residual is the per-dongle ppm (about +60 Hz MAIN,
+16 Hz SUB) that I chose not to model.

**The CW click offset from `48e6866` was wrong, and is removed.** With the
axis corrected, `_tuneOffsetHz('CW-U'/'CW-L')` is 0: on this radio the dial
in CW IS the frequency of the signal heard at the pitch (audio = RF - dial +
pitch on CW-U, measured above), so tuning the dial to the drawn peak is
exactly right. Proved by decoder rather than by eye: on 40 m, a strong CW
signal drawn at 7,000,989 was clicked through the real mouse path in the
headless browser, the dial landed at 7,000,965, and `/api/cw/spectrum`
reported the tone at **738 Hz, +28 dB, signalPresent, confidence 100** for
the whole 8 s sample, against a 700 Hz pitch. With the old -pitch it would
have been at ~1400 Hz. The 09-10 "zero beat" diagnosis was the uncorrected
axis: the click put the DISPLAYED frequency on the dial, 5-7 kHz off the
real one, and the peak "vanished" because a dial at the displayed frequency
puts that peak in the DC notch. It looked like zero beat and never was.

Two things learned on the way that will bite again:

- `POST api/cat/ifwidth/{a|b}` needs `{"code":"18"}` - a **string**. A
  numeric code is accepted and silently does nothing.
- A mode change does not re-read `SH`, so after `MD` the browser's width is
  whatever the previous mode's was until something else reads it. The filter
  scope has always had this; the axis offset now inherits it (I saw +5650
  instead of +5250 for one page load after a DATA-U -> CW-U switch). Worth a
  `SH` read-back in the mode endpoint, not done here.

**Not done:** the FTdx10 and FT-710 are unmeasured and get no correction;
the per-dongle ppm is not modelled; SSB/DATA click-to-tune is deliberately
untouched (dial on the clicked frequency, as every other panadapter does).

## The complaint

> "I can see a huge signal in the passband you have drawn"

and, separately,

> "the large peak at 14.050 gets smaller as it moves through the passband
> markers and then gets bigger again when it comes out the other side. It's a
> gradual decrease/increase in size"

Those are two different faults, and I spent most of the session chasing only
the first one.

## The headline number

At the bins where the receiver demonstrably hears stations, the SDR trace
under today's axis assumption reads **0.37 dB over the noise floor**. The
display shows noise exactly where the real signals are. The best-fitting
alternative model scored 22.89 dB by the same measure.

## What is NOT the bug

**The overlay and click-to-tune are correct.** The passband markers and the
dial marker are drawn in true RF derived from the dial, and the mode maths
behind them is right:

- **CW-U on the FTdx101MP is UPPER sideband. Measured and decisive.** An
  11-point dial sweep gave `d(audio)/d(dial) = -1.00` exactly, and the derived
  RF constant landed at 14,030,856-862 Hz across all eleven points - a 6 Hz
  spread. The lower-sideband hypothesis varied by 2 kHz over the same sweep.
  So `_isLowerSideband('CW-U') === false` and `_tuneOffsetHz('CW-U') = -pitch`
  in `wwwroot/js/sdr/spectrum-panel.js` are both confirmed correct.
- Re-confirmed later the same session: at dial 14,030,159 the audio peak was
  703.1 Hz at +20.4 dB, giving RF 14,030,862.

**The axis scale and tracking are correct.** Cross-correlating the whole
averaged spectrum across a known 5 kHz dial step gave a lag magnitude of
**82 bins = 5.00 kHz** at 61.04 Hz/bin (62,500 Hz span, 1024 bins). The
Hz/bin is right and the trace follows the dial by the right amount.

So the overlay is right and the trace is wrong. That is the opposite of where
I started looking.

## Fault 1: the axis sign is inverted (mirrored) - WITHDRAWN 2026-09-11

**Withdrawn.** Both methods below read raw hub frames upstream of the
`bins.reverse()` in `spectrum-panel.js`. The browser axis is the right way
round. See the correction section at the top. Original text follows.

Two independent methods say so.

1. **Cross-correlation across a known dial step.** The lag came out **+82
   where physics demands -82**. Score 11.85 at that lag against 1.50 at zero
   lag. Magnitude right, sign wrong.
2. **Continuous receiver-built profile.** I built a power-vs-RF profile by
   sweeping the dial and reading the receiver's own audio, then correlated
   that profile against the SDR spectrum across a grid of candidate models.
   **MIRRORED took the top four candidate models**, best score 5.90, against
   1.89 for the current upright/zero-offset assumption.

**Mechanism.** `SdrplayDevice.PlanFor` runs SDRplay **low-IF down-conversion
on every span** - `PlanFor(<=62_500) => new(2_000_000, 8, BW_0_200,
IF_0_450)`, `LifDecimation = 4`. LIF output is spectrally inverted, and
grepping `Services/Sdr/`, `Workers/Yaesu_Sdr_Worker/` and `wwwroot/js/sdr/`
for `invert|mirror|revers|flip` finds **no inversion handling anywhere in the
codebase**.

**How it survived.** The `TunePlan` doc comment in `SdrplayDevice.cs` records
that the divide-by-4 LIF decimation bug was found on 2026-09-03 by this exact
cross-correlation technique - but **only the magnitude was ever checked. The
sign was never tested.** That is the precise gap this bug slipped through, and
it is worth remembering as a general lesson: a cross-correlation lag has a
sign, and checking the absolute value throws away half the answer.

## Fault 2: a notch at display centre

This is the fault Colin identified from the screen, and I would not have found
it from the data I was collecting.

A peak that **gradually** shrinks as it crosses the passband markers and
**gradually** grows again on the far side is a notch centred on the display
centre, not a mis-mapped axis. A mis-mapped axis moves a peak; it does not
attenuate it smoothly.

**Mechanism candidate: the DC blocker in `Services/Sdr/SpectrumProcessor.cs`.**
It subtracts a long-running EMA of the IQ stream. Its own comment predicts
this exact failure mode and then argues it away:

> It is cancelled by subtracting a long-running average of the IQ stream, NOT
> each frame's own mean. Subtracting the frame mean would notch out whatever
> sits exactly at the tuned frequency - precisely the signal the user is
> listening to. A running average only cancels what stays constant in
> amplitude and phase over seconds; a real carrier drifts against the SDR's
> own LO and averages to nothing long before it is touched.

**I think that last sentence is wrong here.** The SDR is fed from the radio's
rear-panel IF output at a fixed 9 MHz. A station at the tuned frequency lands
at a *fixed* spot in that IF - it does not drift against the SDR's LO the way
a directly-received carrier would. The EMA can therefore accumulate it and
subtract it. That is a hypothesis, not a measurement.

There is already a measured record in `SdrplayDevice.cs` that fits:

> Measured 2026-08-30 at the 62.5 kHz span: centre bin +46.9 dB over the noise
> floor forced to zero-IF, +8.4 dB with low-IF, +0.7 dB with low-IF and the DC
> blocker.

+46.9 -> +8.4 -> +0.7. The DC blocker is measurably taking about 8 dB off the
centre bin even after LIF has already moved DC away from it.

## How WIDE can the DC blocker's notch be? (derived 2026-09-10, from the code)

This sharpens fault 2 into something predictive, and it turns on the span
that was being viewed - which I did not record and must ask.

The blocker estimates DC as **each frame's own mean**, then EMAs that across
frames. A frame mean is a boxcar over all `fftSize` samples, so its response to
a tone at offset f from centre has its first null at exactly `fs / fftSize` -
**one bin**. Subtracting a constant from the time series then perturbs only the
DC component of that frame, which the Hann window spreads over roughly two to
three bins.

**So the blocker's reach is about 2-3 bins, whatever the span.** In Hz that is
entirely a function of which span is selected:

| span | Hz/bin (1024) | blocker reach, approx |
|---|---|---|
| 62.5 kHz | 61 | 120-180 Hz |
| 250 kHz | 244 | 490-730 Hz |
| 500 kHz | 488 | 1.0-1.5 kHz |
| 2 MHz | 1953 | 3.9-5.9 kHz |

**This cuts both ways, and that is the point.** On a wide span the mechanism
fits the description very comfortably - at 2 MHz a peak would visibly fade over
several kHz, and the passband markers are only the 2px sliver at centre, so
"moving through the passband markers" means moving through the middle few
pixels. On the 62.5 kHz span it does NOT fit: 120-180 Hz is two or three pixels
of a 1024-bin display, which nobody would describe as a gradual fade.

**OPEN QUESTION, must be answered before any fix is attempted: which span was
selected when the gradual dip was seen?** Wide span keeps the DC blocker as the
prime suspect. Narrow span rules it out and the mechanism is then unknown.

Note the `DcAlpha` comment in `SpectrumProcessor.cs` reasons only about the EMA
time constant and concludes "a notch roughly 0.3 Hz wide". That is the width of
the *temporal* filter and it is not the binding constraint - the frame-mean
boxcar and the window are, and they are three to four orders of magnitude
wider. Do not take the 0.3 Hz figure as evidence that the notch is invisible.

### The competing hypothesis: the radio's own AGC

A signal that fades as it enters the passband and recovers as it leaves is also
exactly what AGC looks like, if the 9 MHz IF tap sits after the AGC-controlled
stage. Against it: the tap is measured to be AHEAD of the roofing filter, which
is early in the chain, so it may well be ahead of the AGC-controlled amplifier
too. That is not known either way.

**There is a clean discriminator, and it costs one look at the screen.** AGC
pulls the WHOLE trace down, not one peak - so if the noise floor sinks along
with the peak, it is AGC; if only that peak sinks while the floor holds, it is
not. Failing that, repeat the sweep with AGC off or on fixed manual IF gain.

## The reconciliation - and a retraction (the retraction was itself wrong)

**2026-09-11: the offset is real - see the correction at the top. The
argument below fails because the notch is drawn at the dial by construction,
so its position on the markers says nothing about the offset.** Original
text follows.

**I previously reported a frequency OFFSET of roughly +6 to +8 kHz. I am
retracting that.** The dip Colin describes is centred on the passband markers,
which are drawn at the dial. If the trace were offset by 6-8 kHz, the notch
would appear 6-8 kHz away from the markers, not on them. **The offset must be
approximately zero.**

That retraction also explains two things that had been bothering me:

1. **Why `mirror.mjs` found the known station at neither bin 500 nor bin
   523.** Those were the upright and mirrored candidate bins for a station
   confirmed by ear. Both are within ~700 Hz of display centre - i.e. **both
   sit inside the notch**. That test could never have discriminated between
   the two hypotheses, because the notch had swallowed the station under
   either one. I read a flat region there as evidence of an offset; it was
   evidence of the notch.
2. **Why the `profile.py` fit preferred a non-zero offset.** A notch sitting
   at zero offset penalises the zero-offset model specifically, so the fit
   slides away from zero to find signal energy elsewhere. The spurious
   +7385 Hz is what that bias looks like.

So the corrected picture is: **mirrored, offset ~0, with a notch at centre.**

## Dead ends - please do not repeat these

1. **The roofing filter cannot be used as a frequency marker.** Switching it
   from 12 kHz to 300 Hz changed the SDR spectrum by **-0.12 dB mean** across
   the whole span. That proves the IF tap feeding the SDR is **ahead of** the
   roofing filter. (I also changed the roofing filter without reading its
   original value first, and left it on 12 kHz / code 6. If it was on 600 or
   300 Hz for CW, that one is on me.)
2. **Matching individual peaks between two captures by eye is useless.** 20 m
   CW stations key on and off between captures, so you match different
   stations and get confident nonsense. This produced two contradictory Hz/bin
   estimates, 11.6 and 60.6, from the same pair of captures. Whole-spectrum
   cross-correlation is immune to this and is the right instrument for the
   whole class of problem.
3. **Do not sweep the dial with a narrow IF filter centred on the CW pitch.**
   I ran a sweep with the IF filter at 250 Hz and every dial position
   dutifully reported a "SIGNAL" at ~700 Hz. With a 250 Hz filter centred on
   the pitch, *everything audible is at 700 Hz by construction* - a peak that
   tracks the dial exactly is the artefact, not the station. Redone with IF
   width 3000 Hz, APF off and the AF filter opened.
4. **"Strongest peak within +/-6 kHz" is not a tracker.** `notch.mjs` used it
   and the peak jumped between different stations step to step, making the
   level column unusable - some rows suggested a dip (offset +0.061 kHz at
   +1.3 dB), others flatly contradicted it (offset -0.305 kHz at +24.2 dB). No
   notch profile can be drawn from that run.
5. **`fit.py` reported "UPRIGHT +18,188 Hz" from only 4 station detections**
   across 2002 candidate models. That is overfitting, and I flagged it as such
   at the time rather than reporting it as a result. It is mentioned here only
   so nobody resurrects the number.

## Two traps worth knowing about, found on the way

- **APF is honest on signal and a decoy on noise.** On an empty frequency, APF
  fabricated a ~20 dB peak at exactly the CW pitch which did **not** move with
  the dial and vanished when APF was switched off. On a real station it added
  a legitimate ~10 dB (15.9 dB over floor to 25.6 dB, same 703 Hz). Any
  automated CW tone tracker that trusts a peak at the pitch while APF is on
  can be fooled by an empty band.
- **A quiet band looks exactly like a hole.** I over-read the flat +/-1.5 kHz
  region around the dial as a "hole" in the trace. It was probably the DC
  blocker notch, but it could equally have been a quiet stretch of band.

## Instrumentation built (reusable)

No HTTP endpoint exposes the SDR bins, so I tapped SignalR raw from Node:

    POST /radioHub/negotiate?negotiateVersion=1
    -> ws://localhost:8080/radioHub?id=<connectionToken>
    -> send the handshake {"protocol":"json","version":1} then 0x1e

Messages are 0x1e-delimited JSON. Type 6 is a ping - echo it straight back or
the server drops you. Type 1 with target `RadioStateUpdate` carries
`{property, value}`; `SpectrumUpdate` carries
`{sdrId, bins, centreHz, spanHz}`. Node 24 has global `WebSocket` and `fetch`,
so this needs no dependencies at all.

The scripts live in the session scratchpad, not in the repo. `dump.mjs`
(single capture to JSON) and `sdrtap.mjs` (live tap) are the two worth
rebuilding if this is picked up again.

## What I have NOT done

- **No fix is written.** Both faults are device/DSP level. A fix must be
  bench-checked against the rig; a green build proves nothing here.
- **The notch has not been profiled.** The one attempt failed (dead end 4).
  The right measurement is a single strong steady carrier stepped across the
  centre in small increments, tracking *that* bin rather than "the loudest
  peak nearby".
- **The mirror hypothesis has not been confirmed by a fix.** Two independent
  correlations agree, which is good evidence, but the decisive test is to
  reverse the bins and watch a known station land where the receiver hears it.

## Suggested order of work when this is picked up

Rewritten 2026-09-11 in the light of the correction at the top:

1. Give the SUB SDR its own IF centre (8.895 MHz for the FTdx101, keeping the
   same 5 kHz gap as MAIN). Per-SDR setting, per-radio default.
2. Apply the measured mode/width/shift/pitch offset table in the browser
   axis and click-to-tune, for the FTdx101MP, CW only to start. Then verify
   by ear: click a CW signal and confirm it lands at the pitch and the reader
   decodes it.
3. Profile the notch properly, with a tracked known carrier (Fault 2 is
   still open and untouched by any of this).
4. Measure the FTdx10 before enabling any of it there.

## Two smaller overlay defects, unrelated to the above

Found while reading the overlay code to rule it out. Both are real, neither
explains the complaint:

- **`getPassband()` in `wwwroot/js/ui/filter-scope-panel.js` ignores the AF
  LCUT/HCUT filter.** It accounts for the IF width code, IF shift, roofing
  filter and mode, but not the radio's audio filter - which was measured at
  LCUT 250 Hz / HCUT 1200 Hz on VFO A and can be *narrower* than the IF
  filter. So the overlay can draw a band wider than what is actually audible.
- **`_drawPassband` widens a sub-2px band to a 2px sliver.** Correct at narrow
  spans, misleading at wide ones, where it implies a passband far wider than
  the true one.

## Reconciling this with the earlier "DC blocker is not the cause" finding

On 2026-09-02 a browser-console probe on `_lastBins` compared the centre bin
against its immediate neighbours before and after a click-to-tune, found them
indistinguishable, and I recorded the DC blocker as ruled out.

**That kill was too strong, and I am narrowing it.** A three-bin comparison can
only detect a *narrow* notch. A dip spread over kHz attenuates the centre bin
and its neighbours by very nearly the same amount, so they still look identical
to each other and the probe reports nothing. What Colin describes - a peak
fading in and out *gradually* over the width of the passband markers - is
exactly that wide case.

So: **narrow centre-bin notch, still dead. Wide notch centred on the display
centre, live and unrefuted.** Do not cite the 2026-09-02 probe against it.

## A related correction on the RTTY figures

I gave a worked RTTY example earlier using Yaesu's default 2125 Hz mark, which
would put the RTTY-L anchor at 2125 + 85 = 2210 Hz. **This radio's MARK
FREQUENCY menu actually reads 1275 Hz**, so the correct anchor for it is
1275 + 85 = **1360 Hz**, not 2210.

That is precisely why `GET /api/cat/rtty` reads the setting from the radio
rather than assuming the default - the assumed value would have been out by
850 Hz on this very rig. The code is right; the figure I quoted in conversation
was not.

Note also that the RTTY dial is the **suppressed carrier, not the mark tone**,
so the anchor is mark +/- shift/2. The RTTY offset is derived from the
operating manual and **has never been measured on a signal** - unlike CW-U,
which was. CW-L and CW-R are likewise unverified on air; they take their sign
from `CwReaderService.IsLowerSideband`.

## The S-meter reads zero when AGC is OFF (MEASURED 2026-09-10)

This cost most of a session and would cost it again, so it goes in front of
anyone picking this up.

Colin reported he was sitting on a CW peak at 14.013933 and could hear
nothing. I went looking with the receiver's own S-meter as the detector and
concluded the receiver was deaf: **S0 at all 71 stops across 14.000-14.070**,
S0 on all three antennas, and S0 on BBC Radio 4 longwave at 198 kHz. No
working receiver reads S0 on a 500 kW carrier, so by then I was diagnosing a
hardware fault.

It was not a hardware fault. **AGC was OFF.** On this radio the S-meter is
derived from the AGC detector, so with AGC off the meter is pinned at zero
whatever is on the antenna.

Measured, same three frequencies, one minute apart:

| frequency | AGC OFF | AGC AUTO |
|---|---|---|
| 14.070 MHz | 0 | 5 |
| 7.210 MHz (41 m broadcast) | 0 | 97 |
| 0.198 MHz (BBC R4 LW) | 0 | 77 |

`GT0<n>;` sets it: 0 = OFF, 4 = AUTO.

**What found it.** Not CAT. I pulled a frame off the Remote Video MJPEG
stream and looked at the radio's own screen, which showed `AGC OFF` in red
and a scope full of healthy signals. That is worth remembering as a
technique in its own right: when CAT and the SDR disagree about reality,
the radio's front panel is a third, independent witness, and this app can
already capture it.

Two consequences for the rest of this document.

**1. Any measurement here that used the S-meter as a detector is void unless
the AGC state at the time is known.** It was not recorded, and it should have
been. The receiver-built power-vs-RF profile in Fault 1 read the receiver's
*audio*, not the S-meter, so it is not void on this account - but its AGC
state was equally unrecorded, and AGC changes how audio amplitude tracks RF
(compressed with AGC on, linear with it off). The cross-correlation evidence
for the mirror does not involve the receiver at all and is untouched.

**2. It bears directly on the notch's competing hypothesis.** The AGC
alternative for the centre dip only works if AGC is actually running. AGC was
found OFF on 2026-09-10 and `AgcA`/`AgcB` were both `0` in the persisted
state, so if it was also off when the gradual dip was observed, **AGC is
ruled out and the DC blocker stands alone**. Colin can settle this: was AGC
off at the time? If yes, stop considering AGC.

**Left on AUTO for VFO A.** VFO B was still AGC OFF at the end of the
session, so VFO B's S-meter still reads zero and will mislead the same way.

### Two smaller things established the same day

- **The 62.5 kHz and 2 MHz panels are two different physical radios.**
  `SdrDeviceKeyA = sdrplay:hw6-2405242660`, `SdrDeviceKeyB =
  sdrplay:hw1-0000000001`. So VFO A and VFO B showing different spectra on
  the *same* frequency is expected - two units, two IF taps, independent gain
  and independent noise floors. It is not evidence of an axis fault.
- **The roofing filter is not a suspect.** It was left on code `6`, which is
  the *widest* setting (12 kHz), so it cannot be attenuating anything. Code
  map: 6 = 12 kHz, 7 = 3 kHz, 8 = 1.2 kHz, 9 = 600 Hz, A = 300 Hz.

## Related

- `docs/design/spectrum-dc-spike-plan.md` - the earlier (2026-08-29) analysis
  of the centre-bin spike, written when the device was still running zero-IF.
  Read it before touching the DC blocker; some of it is superseded by the move
  to low-IF, some is not.
- `docs/decisions/0001-dual-sdr-architecture.md` - why the worker process
  exists at all, which is why the bins arrive over SignalR rather than being
  readable in-process.
