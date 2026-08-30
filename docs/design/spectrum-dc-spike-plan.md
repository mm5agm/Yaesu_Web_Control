# Spectrum: the trace at the tuned frequency

**STATUS: research done, answer found, nothing implemented yet.**
Untracked on purpose. 2026-08-29.

The complaint: there is always a trace at the frequency being listened
to, so you cannot tell whether there is really a signal there.

> "make a plan to fix the spectrum using the method of SDR# and GQRX if
> that's the best way to do it. Maybe the SDRplay API has a method or
> maybe the method used by G4ELI at https://www.sdr-radio.com/Console"

**The SDRplay API does have a method, and we already satisfy every
condition for it but one.** Details below. All of this is from the API
headers and the API specification, both installed on this machine at
`C:\Program Files\SDRplay\API\` - none of it is guesswork or web lore.

## What causes it - confirmed by reading the code

The RSPs are running **zero-IF**, so the tuner's own DC offset lands
exactly on the tuned frequency, and the FFT shift puts that dead centre
on screen.

- `Services/Sdr/SdrplayDevice.cs:365` writes `rfHz = centreFrequencyHz`
  straight through - the LO sits on the frequency of interest.
- `ifType` (at `tunerParams+4`) is **documented at line 26 of that file
  and never written**, so it keeps the API default, which
  `sdrplay_api_tuner.h` gives as `sdrplay_api_IF_Zero`.
- `SpectrumProcessor.ComputeSpectrum` shifts with
  `si = (i + fftSize/2) % fftSize`, so FFT bin 0 - DC - is drawn at
  display index `fftSize/2`, the exact centre.

## Dead end: turning on the API's DC correction

It is already on. `sdrplay_api_control.h`:

    unsigned char DCenable;   // default: 1
    unsigned char IQenable;   // default: 1

and the spec's processing chain (section 3.15, `sdrplay_api_Init`) lists

    DCoffsetCorrection      enabled by default
    IQimbalanceCorrection   enabled by default

We never write those bytes, so they hold the defaults, so the correction
has been running the whole time. What Colin is looking at is the residue
it leaves. Writing `DCenable = 1` would change nothing. Rule this out.

`dcOffsetTuner` (`dcCal` default 3 = periodic, plus `speedUp`,
`trackTime`, `refreshRateTime`) could be retuned to recalibrate harder
or more often, but that is still trying to null a spike in place rather
than moving the wanted signal away from it.

## Dead end: an offset-tuning call in the API

There isn't one. The complete function list is in `sdrplay_api.h` and in
spec section 2.1.1, and it is short - Open, Close, ApiVersion,
Lock/UnlockDeviceApi, GetDevices, Select/ReleaseDevice, the error
helpers, DisableHeartbeat, DebugEnable, GetDeviceParams, Init, Uninit,
Update, and three RSPduo-only Swap functions. Nothing resembling SDR#'s
"Offset Tuning" checkbox. To get the SDR#/GQRX approach we would have to
build it ourselves: deliberately mistune `rfFreq.rfHz`, then mix back in
software in the stream callback. Doable, but see below - it is not
necessary.

## The method the API does have: low IF

`sdrplay_api_tuner.h`:

    sdrplay_api_IF_Zero  = 0,
    sdrplay_api_IF_0_450 = 450,
    sdrplay_api_IF_1_620 = 1620,
    sdrplay_api_IF_2_048 = 2048

In a low-IF mode the tuner's LO sits 450 kHz away from the wanted
frequency and the RSP down-converts to baseband inside its own
processing chain. The DC offset stays where the LO is - 450 kHz off,
which is seven times outside the whole 62.5 kHz span. The centre of the
display becomes ordinary spectrum. This is the hardware doing exactly
what SDR# and GQRX do in software, and it costs no span and no CPU.

The spec (section 3.15) lists the exact conditions, verbatim:

    Conditions for LIF down-conversion to be enabled for all RSPs in
    single tuner mode:
      (fsHz == 8192000) && (bwType == BW_1_536) && (ifType == IF_2_048)
      (fsHz == 8000000) && (bwType == BW_1_536) && (ifType == IF_2_048)
      (fsHz == 8000000) && (bwType == BW_5_000) && (ifType == IF_2_048)
      (fsHz == 2000000) && (bwType <= BW_0_300) && (ifType == IF_0_450)
      (fsHz == 2000000) && (bwType == BW_0_600) && (ifType == IF_0_450)
      (fsHz == 6000000) && (bwType <= BW_1_536) && (ifType == IF_1_620)

**Look at the fourth line against what `Configure` already does.**

- `fsHz` - we write `Math.Max(sampleRateHz, 2_000_000)`, and for every
  decimated span that is exactly 2000000. Condition met.
- `bwType` - the ladder at `SdrplayDevice.cs:371` picks `BW_0_200` for
  any requested rate up to 250 kHz, so at 62.5 kHz it is already
  `BW_0_200`, which is `<= BW_0_300`. Condition met.
- `ifType` - left at the default `IF_Zero`. **Not met, and it is the
  only one.**

So the fix is one field. Set `ifType = 450` at `tunerParams+4` before
`Init`, and the hardware moves the DC offset 450 kHz off screen.

Decimation still applies: the spec's processing-chain order is
ReadUSBdata, DCoffsetCorrection, Agc, **DownConvert, Decimate**,
IQimbalanceCorrection - the x32 decimation runs after the
down-conversion, not instead of it.

### It has to be conditional on span

`/api/sdr/span` accepts eight values. Running each through
`Configure` - remembering that `hardwareRateHz` is
`Math.Max(sampleRateHz, 2_000_000)`, so it is only *exactly* 2000000
when the request is below 2 MHz:

| span | fs written | bwType | LIF? |
|---|---|---|---|
| 62 500 | 2 000 000 | BW_0_200 | **yes** |
| 125 000 | 2 000 000 | BW_0_200 | **yes** |
| 250 000 | 2 000 000 | BW_0_200 | **yes** |
| 500 000 | 2 000 000 | BW_0_300 | **yes** |
| 1 024 000 | 2 000 000 | BW_0_600 | **yes** |
| 2 048 000 | 2 048 000 | BW_1_536 | no |
| 2 500 000 | 2 500 000 | BW_1_536 | no |
| 3 200 000 | 3 200 000 | BW_5_000 | no |

Five of eight, including every span CW is read on. The top three miss
on both counts - wrong `fsHz` *and* wrong `bwType`.

`ifType` must be chosen by the same decision that picks `bwType`, so
the two can never disagree. An inconsistent pair does not raise an
error - it silently leaves DownConvert disabled, which looks exactly
like the bug we are trying to fix.

### Flipping per span is free - it already restarts

Colin asked whether switching configuration with the span would mean
restarting the RSP. It already does, on every span change, today.
`/api/sdr/span` ends with `sdr.RequestRestart()` (`Program.cs:764`),
which cancels the session token; the `finally` in
`SdrManager.RunSessionAsync` then calls `worker.StopAsync()` and
disposes it. A new worker process comes up and runs a full
Open / SelectDevice / GetDeviceParams / Init cycle. So choosing a
different `fs` and `ifType` per span costs nothing extra.

That is also the safe route. The spec lists the LIF conditions under
`sdrplay_api_Init`, **not** under `Update`, so whether DownConvert is
re-evaluated when `fs` changes on a live stream is undocumented. Since
we restart regardless, we never have to find out. Do not build this on
`sdrplay_api_Update`.

### Correction: fs = 6 MHz is NOT the wide-span answer

An earlier draft of this note suggested `fsHz = 6000000` with
`IF_1_620` for the wide spans. That was wrong - it quoted the sample
rate without reading the bandwidth beside it. The condition is
`(fsHz == 6000000) && (bwType <= BW_1_536)`, so the analogue passband
is capped at 1.536 MHz however high the sample rate goes. It cannot
produce a usable 2 MHz view.

The wide-span line that does work is the third one:
`(fsHz == 8000000) && (bwType == BW_5_000) && (ifType == IF_2_048)` -
5 MHz of analogue bandwidth, DC parked 2.048 MHz off centre. Real, but
it costs 4x the USB data rate, and the spans would have to become
integer divisions of 8 MHz: 2.0 MHz works (decimate 4), 2.5 and 3.2 do
not land neatly. Note also that a 4 MHz displayed span would put the
2.048 MHz spike right at the edge rather than safely outside.

## DECISION 2026-08-29: the phantom must be gone at EVERY span

Colin's call, after being shown that the narrow-span fix covers five of
eight spans:

> "i'd like the phamton trace gone at all spans. If it's going to appear
> at the 2MHz span I'd rather not have a 2MHz span, but lets try things
> first. Make a note of that for tomorrow."

Two things follow, and the second is the important one.

1. **No span may keep the phantom.** The earlier recommendation in this
   note - fix the narrow spans, leave the wide ones as they are - is
   withdrawn. A display that lies at some spans and not others is worse
   than one that lies consistently, because you stop knowing which you
   are looking at.
2. **Deleting a span from the list is an ACCEPTABLE outcome, and Colin
   has already authorised it.** If a wide span cannot be made honest, it
   comes out of the `valid` array in `Program.cs` rather than shipping a
   trace that shows a signal that is not there. Do not treat the current
   eight span values as fixed requirements.

But "lets try things first" - so try the 8 MHz mode and measure before
removing anything. Removal is the fallback, not the opening move.

### What 8 MHz can actually deliver

With `fs = 8000000`, `BW_5_000`, `IF_2_048`, the tuner's DC offset lands
2.048 MHz from centre. So a span is clean as long as its half-width
stays below 2.048 MHz:

| decimation | span | spike at 2.048 MHz is... |
|---|---|---|
| 4 | 2.0 MHz | outside +/-1.0 - comfortably clean |
| 3 | 2.667 MHz | outside +/-1.333 - clean |
| 2 | 4.0 MHz | outside +/-2.0 by only 48 kHz - marginal |
| 1 | 8.0 MHz | inside - no good |

So a clean 2.0 MHz span is available, and that is almost certainly the
right replacement for today's 2.048 MHz. 2.5 and 3.2 MHz have no clean
equivalent and are the two most likely to be dropped.

**Unverified and needs checking first:** whether `decimationFactor`
accepts 3, or only powers of two. It is an `unsigned char` in
`sdrplay_api_control.h` with no documented range, and the spec pages
read so far do not list valid values. If it is powers of two only, the
÷3 row above disappears and the wide-span choice is 2.0 MHz or the
marginal 4.0 MHz. Check this before designing the span list.

### Related bug found in the same code - fix it while we are here

`spanHz` is sent to the browser as the **requested** rate, not the rate
the hardware actually produces. `WorkerHost.cs:123` passes
`(long)_opts.SampleRateHz` straight through, but `Configure` derives the
real rate as `Math.Max(sampleRateHz, 2_000_000)` divided by an integer
`decimationFactor` obtained with `Math.Round`.

They disagree wherever the division is not exact. At 1 024 000 the
factor rounds to 2, so the true span is 1 000 000 while the browser is
told 1 024 000 - a 2.4% error in the frequency scale, worst at the
edges. At 62 500 the maths is exact (2e6/62500 = 32), which is why this
has never been noticed: it is the span Colin always uses.

This matters more after the change, not less, because introducing an
8 MHz mode adds more non-exact divisions. The worker should compute the
achieved rate and report **that** as `spanHz`, rather than echoing what
was asked for.

### Traps found while reading

- **The spec's own Update table is wrong**, section 3.17:
  `sdrplay_api_Update_Tuner_DcOffset` is listed as mapping to
  `tunerParams->loMode`, and `sdrplay_api_Update_Tuner_LoMode` to
  `tunerParams->dcOffsetTuner->*`. Those two are swapped. Only matters
  if we ever call `Update` for them; setting `ifType` before `Init`
  avoids `Update` entirely.
- Changing `ifType` after `Init` needs
  `sdrplay_api_Update_Tuner_IfType` (0x00080000), and per section 3.17
  Update "will stop the stream, change the values and then start the
  stream again". Simpler to set it in `Configure` before `Init`, which
  is where the code already sets `bwType`.
- `ifType` defaults to `IF_Zero` for a master but `IF_0_450` for an
  RSPduo slave. Neither of Colin's is an RSPduo - VFO A is an RSP1B
  (`sdrplay:hw6-2405242660`), VFO B an RSP1 (`sdrplay:hw1-0000000001`) -
  so both default to zero-IF today.

### The one thing to verify on hardware

Whether the effective output rate after DownConvert is still what the
decimation maths assumes. The chain order says Decimate runs after
DownConvert and the spec mentions no rate change, so 2 MHz / 32 should
still be 62.5 kHz - but that is inference from a document, not a
measurement. Test: set `ifType = 450`, put a known carrier a known
number of Hz from centre, and check it lands in the bin the maths
predicts. If the span has halved it will be obvious at once.

## Still not looked at

- G4ELI's method. Colin gave https://www.sdr-radio.com/Console, and
  there are three reference screenshots already in the repo at
  `docs/SDR Console High.png`, `... Low.png`, `... Zoom.png` that have
  not been opened. Worth a look for how Console presents it, but the
  low-IF route above is very likely what Console is doing too, and it
  does not depend on finding out.

## Implemented 2026-08-30 (built, NOT yet tested on hardware)

`dotnet build -c Release` clean, 0 warnings. Nothing here has been near
an antenna yet.

### The four files that carry the fix

- **`Services/Sdr/SdrplayDevice.cs`** - the change itself.
  - `IfTypeOffset = 4` added to the field-offset block. It was already
    documented there; it had just never been written.
  - `IF_ZERO` / `IF_0_450` / `IF_2_048` constants added.
  - New `TunePlan` record struct + `PlanFor(requestedRateHz)` table.
    The four values fsHz / decimationFactor / bwType / ifType are now
    chosen by one decision and travel together, because an unmatched
    triple raises no error - it silently reverts to zero-IF, which is
    exactly the bug. Making them separable again would make the bug
    reachable again.
  - `Configure` rewritten to use it, replacing the old independent
    `MinHardwareRateHz` + `Math.Round` decimation and the `bw` ternary
    ladder.
  - `ActualSampleRateHz` property.

- **`Services/Sdr/ISdrDevice.cs`**, **`SoapySdrDevice.cs`** -
  `ActualSampleRateHz` on the interface and the Soapy implementation
  (which just echoes the request; only `setSampleRate` is bound, so
  there is nothing to read back).

- **`Workers/Yaesu_Sdr_Worker/WorkerHost.cs`** - the separate `spanHz`
  bug. It sent `_opts.SampleRateHz` (what was asked for); it now sends
  `device.ActualSampleRateHz` (what the hardware produces). The browser
  draws its entire frequency scale from this one field - every reading
  in `spectrum-panel.js` goes through `_lastSpanHz` - so it was a 2.4%
  scale error at the old 1.024 MHz span. Harmless at 62.5 kHz, where
  2e6/32 is exact.

### The span list, now six values

`[62_500, 125_000, 250_000, 500_000, 1_000_000, 2_000_000]`.

1_024_000 -> 1_000_000 and 2_048_000 -> 2_000_000 are the same spans
reached a different way (8 MHz / 8 and 8 MHz / 4). **2_500_000 and
3_200_000 are gone**: no documented low-IF combination reaches them, so
under Colin's "gone at all spans" rule they had to go rather than stay
as the two spans that still lie. This is the pre-authorised removal.

Touched in: `Program.cs:749` (the `valid` array), `Pages/Index.cshtml`
(six span buttons per VFO, `data-hz` and the `active` comparisons),
`Pages/Settings.cshtml` (the dropdown, eight options down to six),
`Pages/Index.cshtml.cs` (three defaults), `Services/SettingsService.cs`
(`DefaultSampleRateHz` and a new `RetiredSampleRates` map),
`Workers/Yaesu_Sdr_Worker/Program.cs` (usage text).

The `RetiredSampleRates` migration matters more than it looks. Colin's
settings file currently says 62500 for both VFOs so he personally will
not hit it, but anyone whose file says 2_048_000 would otherwise get a
main page with **no span button lit at all** and an `/api/sdr/span`
that rejects their saved value - which reads as a broken UI, not as an
out-of-date setting.

### What still has to be measured, in order

1. **62.5 kHz span, one radio.** Is the centre spike gone, and is the
   span still 62.5 kHz? Put a known carrier a known number of Hz off
   centre and check it lands in the predicted bin. If DownConvert
   changes the effective rate, the span will have visibly halved.
2. **Each of the four narrow spans**, same check. They share fs=2 MHz
   and IF_0_450 but differ in bwType, and bwType is half of what the
   API matches on.
3. **1 MHz and 2 MHz, one radio.** These are the new fs=8 MHz rows.
4. **Both RSPs at fs=8 MHz simultaneously.** The real risk. 8 MHz
   complex at 16 bits is roughly 32 MB/s per device; two of them is
   about 64 MB/s, and USB 2.0 tops out near 40 MB/s in practice. If
   this drops samples, the honest fix is to drop the 1 MHz and 2 MHz
   spans too - already pre-authorised - not to leave them running
   zero-IF with the spike back.

Only step 4 can still cost a span. Steps 1-3 are verification.

---

## Measured and finished 2026-08-30

All six spans measured on hardware. The spike is gone at every one, so
no span had to be removed and the pre-authorised removal was not used.

### It took two changes, not one

Low-IF alone was **not** enough, which is why the first hardware test
came back "centre trace still visible at all spans" and stayed that way
for several rounds.

Centre bin, in dB above the noise floor, 62.5 kHz span:

| | VFO A (RSP1B) | VFO B (RSP1) |
|---|---|---|
| zero-IF - the original bug | +46.9 | +27.3 |
| low-IF only | +8.4 | +8.3 |
| low-IF + DC blocker | **+0.7** | **+1.5** |

Low-IF did the heavy lifting - roughly 38 dB on VFO A - but left a
constant residue of about -93 dBFS added downstream of the API's
DownConvert stage. At 61 Hz resolution that is still 8 dB proud of the
floor and plainly visible on a waterfall.

### Why the earlier readings were misleading

Three separate wrong conclusions were drawn from screenshots, and all
three had the same cause: **the bins that reach the browser cannot be
measured.** `SpectrumProcessor.ComputeSpectrum` applies gain, clamps to
DbFloor/DbCeiling and then EMA-smooths. On top of that, outside the
tuner's analogue passband the trace is ADC quantisation noise, which is
flat no matter what bwType is set to.

So the display could not distinguish a 200 kHz filter from a 1.5 MHz
one, and it could not distinguish a residual DC offset from a real
carrier at the tuned frequency. It was concluded from a 2 MHz screenshot
that ifType was inert; the numbers showed it was working the whole time.

The fix for that is `Workers/Yaesu_Sdr_Worker/SpectrumProbe.cs` -
raw, unclamped, unsmoothed power averaged over 64 frames and logged as
numbers once per worker start. It is left switched on permanently, so if
the spike ever returns the evidence is already in the log the user
sends, rather than depending on someone thinking to enable it first.

### Why the residue is DC and not a signal

Retuning the SDR 10 kHz left bin 0 at -93.3 dBFS / +8.3 rel floor,
against -93.2 / +8.3 before, while everything real moved (strongest bin
went from +23987 Hz to +13794 Hz). Locked to the SDR's own tuning, so
internally generated.

### The DC blocker, and the trap in it

`SpectrumProcessor` subtracts a **long-running average** of the IQ
stream, not each frame's own mean. Subtracting the frame mean would
notch out whatever sits exactly at the tuned frequency - which is the
one signal the user is listening to. A running average only cancels what
is constant in amplitude and phase over seconds; a real carrier drifts
against the SDR's own LO and averages away long before it is touched.

First attempt seeded the estimate from the first frame and **made things
worse** - centre bin +15.0 dB, against +8.3 for doing nothing. A
start-up frame can be partial or transient, and subtracting a bad
constant adds DC rather than removing it; with a 3-second time constant
it then took seconds to walk back. Replaced with a cumulative mean that
eases into the fixed-alpha EMA (`alpha = max(1/n, DcAlpha)`).

### All six spans, centre excess in dB over the noise floor

| span | fs / decim | VFO A | VFO B |
|---|---|---|---|
| 62.5 kHz | 2 MHz / 32 | +0.7 | +1.5 |
| 125 kHz | 2 MHz / 16 | +0.1 | - |
| 250 kHz | 2 MHz / 8 | +0.5 | - |
| 500 kHz | 2 MHz / 4 | +0.4 | -0.0 |
| 1 MHz | 8 MHz / 8 | - | -0.3 |
| 2 MHz | 8 MHz / 4 | +1.3 | -0.3 |

Every span is within 1.5 dB of the noise floor, which is ordinary
bin-to-bin variation. Real signals are unaffected - VFO A still showed
one at +15.6 kHz, 19 dB above the floor, with the blocker active.

**Step 4 passed.** Both RSPs ran at fs=8 MHz simultaneously with no
dropped samples and no USB errors, so the 1 MHz and 2 MHz spans stay.
2_500_000 and 3_200_000 are still gone, because no documented low-IF
combination reaches them.

### The stale worker, and what it actually was

A full test round was lost to an app running a worker that did not
contain the SDR changes: PROBE lines simply never appeared, and the
build had said "Build succeeded, 0 errors". The conclusion drawn at the
time was that `dotnet build Yaesu_Web_Control.csproj` does not rebuild
`Workers/Yaesu_Sdr_Worker` despite the ProjectReference.

**That conclusion is wrong.** Tested afterwards, both ways round:

| touched file | worker rebuilt | staged next to YWC |
|---|---|---|
| `Workers/Yaesu_Sdr_Worker/SpectrumProbe.cs` | yes | yes |
| `Services/Sdr/SpectrumProcessor.cs` (linked in) | yes | yes |

Same timestamp and size on the worker's own output and the staged copy
in both cases. The ProjectReference chain does its job, and "build the
worker explicitly first" is not the rule.

The real cause was never pinned down. The likeliest candidate is that
the build ran while the app was up, so the running worker held its own
staged `.dll` open - which normally shows as MSB3027/MSB3021 and did on
another occasion, but a lock is timing-dependent and a `-v q` build is
easy to skim.

Rather than leave a rule that depends on someone remembering to run an
extra command, `Yaesu_Web_Control.csproj` now carries a **`StageSdrWorker`
target** (`AfterTargets="Build"`). The `<None>` items that stage the
worker are evaluation-time items, read before anything is built; this
target copies again afterwards, from what is actually on disk, and
errors if the worker produced no `.dll` at all. Verified by deleting the
staged files and rebuilding - all four came back - and by running the
target alone, which copied the one missing file and nothing else.

Whatever the original cause was, a silently stale worker now has to get
past a copy that happens after the worker is built.

### Tests

`Tests/YaesuWebControl.Tests` 98/98 pass.
