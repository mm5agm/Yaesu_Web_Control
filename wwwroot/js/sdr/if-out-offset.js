// if-out-offset.js — where a signal at the dial frequency actually sits in the
// radio's IF OUT, so the SDR spectrum axis can be drawn in true RF.
//
// The SDR is tuned to a fixed IF centre and the panel labels the bins as RF by
// assuming "centre bin = dial frequency". That is not what the radio does. The
// FTdx101's rear IF OUT is 9.005 MHz for MAIN and 8.900 MHz for SUB, the SDRs
// are parked 5 kHz below each of those so the DC notch stays off the dial,
// and on top of that the radio slides its LO with the DSP filter width, IF
// shift and CW pitch. Measured on Colin's FTdx101MP on 2026-09-11 against
// Radio Scotland's 810 kHz carrier, on both receivers, within 2 Hz at every
// point — see docs/design/sdr-spectrum-axis-investigation.md, "Correction,
// 2026-09-11".
//
// The result is one number per panel: `true RF = displayed RF + offset`,
// where "displayed" is the naive dial-at-centre reading. The panel adds it to
// every Hz<->pixel mapping and the axis comes out right.
//
// Per-radio numbers, so this stays in this repo and out of core/. Only the
// FTdx101MP is measured; the FTdx101D shares its IF and its manual. Every
// other model returns null, which the panel treats as "no correction".

const IF_OUT_HZ = {
    'FTdx101MP': { A: 9_005_000, B: 8_900_000 },   // operating manual p.17
    'FTdx101D':  { A: 9_005_000, B: 8_900_000 },
};

// SSB LO slide by DSP width, in Hz. Not a formula — measured point by point
// (SH codes 12..23; the codes below 12 are all zero) and rounded to the
// nearest 50 Hz of the +-1 Hz readings. Keyed by width so a code-0 "default"
// width resolves the same way as the explicit code for the same bandwidth.
const SSB_SLIDE_HZ_101 = [
    [2200,   50], [2300,  250], [2400,  350], [2500,  500], [2600,  650],
    [2700,  850], [2800, 1150], [2900, 1250], [3000, 1400], [3200, 1650],
];

/**
 * The radio's IF OUT frequency feeding this VFO's SDR, or null when the
 * model has not been measured.
 * @param {string} model  RadioModel string, e.g. "FTdx101MP"
 * @param {string} vfo    "A" or "B"
 * @returns {number|null}
 */
export function ifOutHz(model, vfo) {
    const m = IF_OUT_HZ[model];
    if (!m) return null;
    return m[vfo === 'B' ? 'B' : 'A'] ?? null;
}

/**
 * How far the radio has slid its LO for the current filter settings, in Hz,
 * relative to the narrow-filter, zero-shift case.
 *
 * CW-U / CW-L:  shift + max(0, (width - pitch) / 2)   — the radio keeps the
 *               CW filter's lower edge at or above half the pitch by moving
 *               the LO instead of the passband.
 * SSB / DATA / AM: shift + lookup by DSP width (SSB_SLIDE_HZ_101).
 * RTTY / PSK / FM: shift only — the width term is unmeasured in those modes.
 *
 * @param {string} model
 * @param {{mode?: string, ifWidthHz?: number, ifShiftHz?: number, cwPitchHz?: number}} f
 * @returns {number}
 */
export function loSlideHz(model, f) {
    if (!IF_OUT_HZ[model] || !f) return 0;
    const mode  = (f.mode || '').toUpperCase();
    const width = Number(f.ifWidthHz);
    const shift = Number.isFinite(Number(f.ifShiftHz)) ? Number(f.ifShiftHz) : 0;
    const pitch = Number(f.cwPitchHz) > 0 ? Number(f.cwPitchHz) : 700;

    if (mode === 'CW-U' || mode === 'CW-L') {
        const w = Number.isFinite(width) ? width : 0;
        return shift + Math.max(0, (w - pitch) / 2);
    }
    if (mode === 'LSB' || mode === 'USB' || mode.startsWith('DATA') || mode.startsWith('AM')) {
        let slide = 0;
        if (Number.isFinite(width)) {
            for (const [w, hz] of SSB_SLIDE_HZ_101) if (width >= w) slide = hz;
        }
        return shift + slide;
    }
    return shift;
}

/**
 * The full axis correction for one panel: `true RF = displayed RF + result`.
 * Null when the radio is unmeasured or the SDR centre is not known yet.
 *
 * @param {string} model
 * @param {string} vfo           "A" or "B"
 * @param {number} sdrCentreHz   the IF frequency the SDR is actually tuned to
 *                               (from the spectrum frame, not the settings —
 *                               the frame says what the worker really did)
 * @param {object} filter        see loSlideHz
 * @returns {number|null}
 */
export function axisOffsetHz(model, vfo, sdrCentreHz, filter) {
    const ifOut = ifOutHz(model, vfo);
    if (ifOut == null) return null;
    const centre = Number(sdrCentreHz);
    if (!(centre > 0)) return null;
    return (ifOut - centre) + loSlideHz(model, filter);
}
