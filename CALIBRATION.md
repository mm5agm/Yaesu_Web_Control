# Meter Calibration Guide

## Overview

The Meter Calibration page lets you map the raw values your radio reports (0–255) to the real-world values you want displayed on the gauges. Each meter has its own calibration table made up of calibration points. The app interpolates between points to give smooth, accurate readings across the full scale.

---

## What You Need

- Your radio connected and powered on
- A known reference — for example, a power meter, SWR analyser, or the radio's own front panel display
- Time to transmit at several power levels (for Power, SWR, ALC calibration)

---

## The Calibration Page

Each meter has its own card showing:

- **Gauge** — live needle updated from the radio every 750ms
- **Raw** — the raw value (0–255) currently being reported by the radio
- **Calibrated** — the value your current calibration points produce for that raw reading
- **Calibration points** — a table of Raw Value / Radio Value pairs
- **Add Point** — adds a new calibration point
- **×** — removes a calibration point
- **Save** — saves this individual meter's calibration
- **Save Calibration** (top of page) — saves all meters at once

---

## How Calibration Works

Each calibration point is a pair:

| Raw Value | Radio Value |
|---|---|
| The number the radio reports (0–255) | The real-world value you want displayed |

The app draws a straight line between adjacent points (linear interpolation). The more points you add, the more accurately it follows the true curve of your meter.

---

## Step-by-Step: Calibrating the Power Meter

1. Go to the **Meter Calibration** page
2. Set your radio to a known transmit power — for example, 5W on the front panel
3. Press **TX** on the calibration page to begin transmitting
4. Note the **Raw** value shown on the Power card
5. Press **TX** again to stop transmitting
6. In the Power calibration table, enter that Raw value and **5** as the Radio Value
7. Repeat at several power levels (e.g. 10W, 25W, 50W, 100W) to build up a curve
8. Click **Save** on the Power card when done

> The more calibration points you add, the more accurate the display will be across the full power range.

---

## Step-by-Step: Calibrating the S-Meter

The S-Meter works differently — Radio Values are labels (S1, S2, ... S9, +10, +20, +40) rather than numbers.

The easiest way to calibrate the S-Meter is to use the **RF/SQL control** — the outer ring on the knob next to the Multi knob on the FTdx101MP. By rotating this ring you can smoothly reduce the S-meter reading and stop the needle at any point to read the raw value.

First, reduce background noise as much as possible:

- **Best:** Connect the radio to a dummy load
- **Next best:** Disconnect the antenna
- **Worst:** Find the quietest frequency you can — the one with the lowest S reading

Then:

1. Rotate the RF/SQL outer ring slowly — watch the **Raw** value on the S-Meter card drop as you turn it down
2. Stop at a point where the radio's front panel S-meter shows a known level (e.g. S1)
3. Note the **Raw** value shown on the S-Meter card
4. Enter that Raw value and **S1** as the Radio Value
5. Rotate the RF/SQL ring to the next S-meter step and repeat
6. Work through S1, S2, S3, S4, S5, S6, S7, S8, S9 — and +10, +20, +40 if you can achieve them
7. Click **Save** on the S-Meter card when done

> The RF/SQL control gives you precise control over the S-meter level, making it much easier to hit specific S-meter steps than trying to find signals of exactly the right strength.

---

## Step-by-Step: Calibrating SWR, ALC, IDD, VDD, Temperature

The process is the same for all numeric meters:

1. Transmit into a known load (for SWR) or observe the radio's front panel reading
2. Note the **Raw** value shown on that meter's card
3. Enter that Raw value and the known real-world value as the Radio Value
4. Add points at several levels if possible
5. Click **Save** on that meter's card when done

---

## Tips

- **Start with the default points** — the app ships with a reasonable set of default calibration points. You only need to adjust them if your readings are inaccurate.
- **Add points at the extremes** — always have a point near Raw=0 and one near the maximum raw value you expect to see, so the interpolation doesn't go off-scale.
- **Transmit safely** — always transmit into a dummy load or antenna. Never transmit without a load connected.
- **Reload From File** — discards any unsaved changes and reloads the last saved calibration.
- **Save Calibration** (top button) — saves all meters in one click.

---

## Where Calibration Data Is Saved

- **Installed app:** `%APPDATA%\MM5AGM\Yaesu Web Control\calibration.user.json`
- **Development:** `%APPDATA%\MM5AGM\Yaesu Web Control\calibration.user.json` (the
  same per-user file — a development build no longer writes into `wwwroot`).

The save path is shown at the top of the Meter Calibration page.

The shipped defaults in `wwwroot/calibration.default.<Model>.json` are **not**
hand-edited. They are derived from the measurements operators send in, kept in
`calibration-contributions/<Model>.json` (a development-only artefact, never
shipped). See [`calibration-contributions/README.md`](calibration-contributions/README.md)
for how a contribution becomes a shipped number and how to undo a bad one.
