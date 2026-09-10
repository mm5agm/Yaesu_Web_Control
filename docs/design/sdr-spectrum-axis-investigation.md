# The SDR spectrum trace does not match what the receiver hears

**STATUS: measured, diagnosed, NOT fixed. 2026-09-10, RX only, against the
FTdx101MP on 20 m CW.**

This is the write-up of a full session's measurement. Everything below was
measured on air; where something is inference rather than measurement I say
so. Nothing here has been fixed, and any fix is device-level, so it must be
bench-checked against the radio and never signed off on a build.

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

## Fault 1: the axis sign is inverted (mirrored)

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

## The reconciliation - and a retraction

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

1. Profile the notch properly, with a tracked known carrier. Confirm or clear
   the DC blocker as the cause - the cheap test is to disable the EMA
   subtraction and re-measure the same carrier at the same offsets.
2. Fix the LIF spectral inversion, then verify by ear: tune a known station
   and confirm the trace peak sits under the passband markers.
3. Only then re-examine offset. With the notch gone and the sign right, any
   residual offset becomes measurable for the first time.

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

## Related

- `docs/design/spectrum-dc-spike-plan.md` - the earlier (2026-08-29) analysis
  of the centre-bin spike, written when the device was still running zero-IF.
  Read it before touching the DC blocker; some of it is superseded by the move
  to low-IF, some is not.
- `docs/decisions/0001-dual-sdr-architecture.md` - why the worker process
  exists at all, which is why the bins arrive over SignalR rather than being
  readable in-process.
