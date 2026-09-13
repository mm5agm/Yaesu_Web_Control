## 10. Meter Calibration

The calibration page lets you adjust the scale of each meter gauge to match your radio's actual output. This is useful if the meter readings seem inaccurate.

Access calibration from **Calibrate Meters** in the navigation bar.

**How calibration works:**

Each meter has a table of calibration points. Each point maps a **raw value** (the number the radio sends) to a **display value** (what is shown on the gauge).

For example, the S-meter might have points like:
- Raw 0 → S0
- Raw 120 → S9
- Raw 200 → S9+20dB

The gauge interpolates between points to produce smooth readings.

> **Important — where each number comes from.** The gauges and value badges on the calibration page (the needle, the **Power Out X.XW** badge, the S-unit label, and so on) are the app's *output*: it produces them by running the raw value through the **current** calibration curve. They are **not** the numbers you record. To make a calibration point you pair two things:
>
> - the **raw value** — read from the **`Raw:`** indicator on the page (the number the radio sends, before any calibration); and
> - the **true value** — read from the **radio's own meter or display** (or an external reference, such as a wattmeter into a dummy load).
>
> Copying the page's own gauge reading back into the table calibrates the app against itself and achieves nothing. Always take the true value from the radio, never from YWC's gauge.

**Editing calibration:**

1. To add a point: click **Add Point**, then enter the raw and display values.
2. To delete a point: click the **×** button next to it.
3. To test: click the **TX** button on the calibration page to transmit a test signal and watch the meters respond in real time.
4. Click **Save Calibration** when finished.
5. Click **Reload From File** to discard unsaved changes.

Calibration is saved to `%APPDATA%\MM5AGM\Yaesu Web Control\calibration.user.json`.

**Per-model defaults (v2.3.0 and later):** YWC now ships separate default calibration tables for each supported radio (`calibration.default.FTdx101MP.json`, `…FTdx10.json`, etc.) in the installation folder. On first launch your `calibration.user.json` is created by copying the default for whichever radio you have configured. As of v2.3.0 the only model with measured calibration data is the FTdx101MP; the other models ship with placeholder copies of that table. If you calibrate your own radio (especially S-Meter) and would like to help, please share your `calibration.user.json` on [Discussion #30](https://github.com/mm5agm/Yaesu_Web_Control/discussions/30) — submissions are averaged and shipped as proper per-model defaults in future releases.

> **Changing radio model later:** if you switch to a different radio in Settings, your existing calibration is **not** automatically reset to the new model's defaults — your custom values stay in place. If you want a fresh start tuned for the new radio, open the **Meter Calibration** page and click the **Reset to Defaults** button. It rebuilds your calibration from the shipped defaults for whichever radio you currently have configured.

### 10.1 Calibrating the S-Meter (receive)

The shipped default is measured on a specific FTdx101MP. Your individual radio may differ by 1–3 S-units. Here is how to calibrate it against your own rig without needing test equipment.

**Before you start — three things to check:**

1. **RF/SQL knob mode must be set to "RF" — not "SQL".** On the FTdx101MP/D, the dual-purpose RF/SQL knob can be switched to act as either RF Gain or Squelch. The S-meter responds to **RF Gain** (which actually attenuates the received signal); it does NOT respond to SQL (which only changes the audio-gating threshold). Many Yaesus briefly show the squelch level on the meter while you turn it in SQL mode — that looks like an S-meter change but isn't. **If you try to calibrate with the knob in SQL mode, YWC's reading will not match the rig's display and the calibration will be wrong.**

    Check or change the mode: **FUNC → OPERATION SETTING → GENERAL → RF/SQL VR → "RF"** (this is the default). The setting is shared between MAIN and SUB bands.

2. **Use the correct knob.** The FTdx101MP/D has **two** concentric RF/SQL knobs — one for MAIN, one for SUB. On the FTdx101MP, the **MAIN AF/RF-SQL knob is the LOWER of the two** stacked knobs on the front panel; the SUB AF/RF-SQL knob is above it. The OUTER ring is RF/SQL; the inner knob is AF (audio level). YWC's VFO A reads the MAIN band's S-meter (`SM0;` CAT query) — so for calibrating the VFO A gauge, you must turn the **lower outer ring** on the FTdx101MP.

3. **Provide a steady signal.** Easiest: connect a **dummy load** to the antenna socket — the receiver picks up internal background noise which is stable and predictable. Alternatively, tune to a strong stable broadcast station or beacon.

**The procedure:**

1. Open the **Meter Calibration** page on YWC. Watch the **Raw** indicator above the S-Meter row — it updates live.
2. Turn the MAIN RF/SQL knob (outer ring of the lower knob on the FTdx101MP) **fully clockwise** — maximum RF gain. The rig's S-meter will read its highest value with this signal source. Note the YWC Raw value and the S-unit the rig is showing. Click **Edit** on the matching row in the calibration table (or **Add Point** if no row matches) and enter the raw value alongside the S-unit the rig displays.
3. **Slowly turn the knob anti-clockwise.** Both the rig's S-meter AND YWC's Raw value will drop together — that's RF Gain actually attenuating the signal in the RF/IF stages, not just changing what's shown.
4. When the rig's S-meter reaches each labelled S-unit boundary (S9 → S7 → S5 → S3 → S1 → S0), pause and update the corresponding row in the calibration table with the YWC Raw value at that point.
5. Repeat down to S0 (or as far as the knob will go).
6. Click **Save Calibration**.
7. **Look at the gauge.** The needle should now move to the correct S-unit position as you adjust the signal. Walk the knob through one more time to verify YWC tracks the rig at each S-unit.
8. After you're finished, return the knob to fully clockwise (max RF gain) for normal listening.

**Sharing your data.** If your calibration result is meaningfully different from the shipped default — especially for any radio model other than FTdx101MP — please copy your `calibration.user.json` to [Discussion #30](https://github.com/mm5agm/Yaesu_Web_Control/discussions/30). Multiple submissions per model are averaged into improved shipped defaults in future releases.

### 10.2 Calibrating the Power meter (transmit)

The power meter on YWC reads the radio's transmitted RF power. To calibrate it, you transmit at known power levels and record the raw values YWC sees.

![The Power panel on the Meter Calibration page, annotated. The "Power Out" badge (ringed in red and struck through) is the app's computed result — do not read it. Read the green-ringed live Raw indicator instead — a whole number that only moves while transmitting — type it into the Raw Value column, and put the watts your radio's own meter shows in the Radio Value column.](pictures/Calibration-Power-Annotated.png)

**Before you start:**

- Have a **dummy load** connected — not an antenna, since you'll be transmitting briefly at various power levels.
- Decide the band and mode you want to calibrate on — CW gives the cleanest carrier for short test transmits; SSB into a dummy load with mic gain low also works.

**The procedure:**

1. Open the **Meter Calibration** page on YWC. The Power row's **Raw** indicator (just above the Raw Value column) updates only during transmit, and is always a **whole number**.
2. Set the radio's RF Power to a low value (e.g. 5 W) via the radio's RF POWER control or YWC's slider.
3. Press the PTT or use YWC's TX button briefly — long enough for the meter to stabilise (about a second).
4. Note the whole-number **Raw** value YWC shows. Release PTT. In the calibration table, type that number into the **Raw Value** box and the known power into the **Radio Value** box (for example `Raw Value = 78`, `Radio Value = 25`).
5. Increase RF Power to the next test point (e.g. 25 W → 50 W → 100 W → max for your radio).
6. Repeat brief transmits at each level and record the raw values.
7. Click **Save Calibration**.

> **Get the two columns the right way round.** The **Raw Value** is the whole number YWC reports; the **Radio Value** is the watts you set on the rig. Don't put watts in the Raw box.
>
> **Every higher power must give a higher Raw.** More output always drives the meter reading up, so your raw numbers must *increase* with the power. If 25 W ever shows a *lower* raw than 10 W, two readings have got crossed — redo that pair. This is the single most common power-calibration mistake, and it makes the gauge read backwards.

For a quick sanity check after saving: transmit at a known power and watch YWC's power gauge — the needle should sit on the correct watts label.

### 10.3 Other meters

The same general approach applies to other meters (ALC, SWR, Compression, IDD, VPA, TPA), but the techniques differ:

- **SWR**: vary the antenna mismatch in known steps (a known-load or a controllable mismatch box).
- **ALC**: speak into the mic and adjust MIC GAIN to walk the ALC reading through known points.
- **Compression**: enable Speech Processor and walk PROC LEVEL.
- **IDD / VPA**: drain current and PA voltage vary with RF power output and band — calibrate alongside Power.
- **TPA**: temperature rises during sustained transmit; calibrate at known temperatures from the radio's display.

These are lower-priority for most users than the S-Meter and Power calibrations.

---
