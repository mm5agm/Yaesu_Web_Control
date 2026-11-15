# Refine: averaging a radio's own scope

Written 2026-11-12, when Refine went into both apps. This note is for whoever
next touches the spectrum path in Yaesu Web Control (YWC) or Icom Web Control
(IWC).

## What it is for

A radio's own scope sends each sweep as a separate snapshot. One sweep has a
rough noise floor, so a weak signal hides in the grass. An SDR is different:
its worker already averages FFT frames before they reach the page. Refine adds
that averaging for the radio scopes:

- **IWC:** the IC-7300's scope over CI-V, about 3.2 sweeps a second at
  19200 baud. Always available.
- **YWC:** the FTdx101's scope read from its ACC socket, 25 frames a second.
  Only on an SDR panel whose source is the ACC scope. The SDR sources (RSP,
  RTL-SDR, Airspy and so on) are averaged by their own worker and have no Refine.

## The method

`RadioWebControl.Core.Services.Spectrum.SpectrumRefiner` keeps a per-bin
exponential moving average:

    average[i] += w * (sweep[i] - average[i])

- **w** is how much each new sweep counts, in (0, 1]. w = 1 is the raw sweeps.
- At F sweeps a second, w takes about **1/(F·w) seconds** to follow a change,
  and smooths the floor about as much as averaging **2/w − 1** sweeps.
- **Tuning doesn't smear.** When the centre moves, the average shifts by the
  same number of whole bins. A sub-bin move is carried over to the next sweep,
  so slow tuning adds up rather than being lost.
- **It starts again** on a new span, a different bin count, a move of more than
  half a span, or a span of 0 or less. The first sweep after a reset is drawn
  raw.
- `Add` returns a new array each time. Callers can hand it to SignalR without
  copying.

Use **one refiner per stream of sweeps**. A stream that hops between bands
needs its own, or every hop resets the main average. IWC's cross-band peek is
the case in point.

The raw sweep is for anything that measures (probes, noise-floor estimates,
decoders). The refined trace is for eyes.

YWC's ACC path uses the YaesuAccScope library's own `ScopeRefiner`
(Yaesu_ACC_Spectrum, library/src/YaesuAccScope), which is the same method.
The core class is for apps that don't take that library.

## The steps each app offers

The weights are per app, because a slow scope wants lighter steps than a fast
one:

| Step | IWC weight | IWC lag at 3.2/s | YWC weight | YWC lag at 25/s |
|------|-----------:|-----------------:|-----------:|----------------:|
| Raw  | 1    | –      | 1    | –      |
| 1    | 0.75 | 0.4 s  | 0.6  | 0.07 s |
| 2    | 0.6  | 0.5 s  | 0.45 | 0.09 s |
| 3    | 0.5  | 0.6 s  | 0.3  | 0.13 s |
| 4    | 0.4  | 0.8 s  | 0.2  | 0.2 s  |
| 5    | 0.3  | 1.0 s  | 0.12 | 0.33 s |

IWC defaults to step 3 (`ScopeRefine.DefaultLevel`). `ScopeRefineTests` holds
the steps to "no step lags more than about a second". YWC keeps a step per
panel (`AccRefineLevelA` / `AccRefineLevelB`).

## Measurements

Clarity is the ratio "peaks over floor roughness": signal peaks above the
median floor, divided by the floor's spread. All figures are from 2026-11-12.

| Radio, conditions | Raw | Middle step | Top step |
|---|---:|---:|---:|
| IC-7300 MkII, 20m FT8, ±10 kHz, REF +10, live through IWC | 20.3 | 30.3 (step 3) | 41.1 (step 5) |
| IC-7300 MkII, same band, captured sweeps analysed offline | 15.3 | 23.4 (w 0.5) | 28.9 (w 0.3) |
| FTdx101MP ACC scope, 20m FT8, 20 kHz span | 8.8 | – | 21.5 (w 0.3) |

The live IWC run also measured the floor's change from one sweep to the next:
6.2 dB raw, 2.7 dB at step 3 and 1.6 dB at step 5. An earlier 30m run at REF 0
gave 7.6, 3.1 and 1.8 dB.

**Gotcha:** at REF 0 on a narrow span the IC-7300 clips the bottom of the
floor, which makes the raw floor look calmer than it is and understates what
Refine does. Measure at REF +10.

## Where it runs

**IWC:** `CivRadioController.OnUnsolicitedFrame`.

- `_refinePrimary` refines every normal sweep **before** the pseudo-dual crop
  is cut, so the watch panel's crop is refined too.
- `_refinePeek` refines cross-band peek sweeps.
- The scope probe sees raw sweeps.
- The weight comes from `ScopeRefineLevel`, which is re-read on the slow poll,
  so a new step shows within about a second.
- If a sweep is out of range the refiner resets and the sweep goes out raw.

**YWC:** the ACC scope source runs the library's `ScopeRefiner` per panel.

## The control, and the rule

There is one route in both apps:

    GET  /api/sdr/refine?sdrId=A          -> {available | acc, level, max}
    POST /api/sdr/refine?sdrId=A&level=3

- IWC answers `available: true`. It accepts `sdrId` and ignores it, because
  there is one scope.
- YWC answers `acc`: true only for a panel showing the ACC scope.
- The step lives on the server, not in the browser. That way every view and
  every browser sees the same trace.

The page side is `core/js/radio-panel/spectrum-refine.js`, copied to
`wwwroot/js/radio-panel/` at build time:

- `bindRefineSlider` wires a slider the page has drawn: Classic, and YWC's
  Experimental page.
- `addRefineTo` puts a REFINE list beside a view's SPAN list: Radio, its
  pop-out spectrum, and RTTY Contest.

Colin wants Refine in **every view that draws the spectrum**, including views
not written yet. Each app has `RefineEveryViewTests`, which scans pages and
view scripts for spectrum drawing and fails any that don't load the module.
When adding a view, add Refine from the start.

## Build gotcha

Adding a file under `core/js/` can make a full multi-target build fail with
MSB3491 ("Could not write lines to file wwwroot\js\…\.gitignore"). Both target
frameworks write the generated `.gitignore` at the same moment. Building again,
or building the main csproj on its own, gets past it.
