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
// 2026-09-11", and re-measured on 2026-09-24 with the sideband taken
// into account (below).
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

// Measured again on 2026-09-24 (discussion #172), with the receiver's own
// audio as ground truth: the 810 kHz carrier's tone in the tap, not its
// expected position. What the radio holds still in its IF OUT is the CENTRE
// OF ITS FILTER, not the dial. So the slide is the RF offset of that centre
// from the dial, and in a lower-sideband mode it is on the other side:
//
//   USB            +(1500 + shift)   the SSB passband is centred on 1500 Hz
//   LSB            -(1500 + shift)   audio at every DSP width, 1100..4000
//   DATA-U / -L    +/-(DS + shift)   DS = the DATA SHIFT (SSB) menu, 1500 default
//   CW-U           +(shift + max(0, (min(width, 3000) - pitch) / 2))
//   CW-L           the mirror of CW-U
//   RTTY-L / -U    -85 -/+ shift     the filter sits on mark + shift/2
//   AM             0                 on the carrier; IF SHIFT does nothing
//
// The 09-11 SSB width table (up to +1650 in both sidebands) is gone: on the
// same radio and the same carrier it no longer describes anything, and its
// single sign is what put LSB signals 2.6 kHz to the right of the dial.
// In DATA-L/U the radio centres on its DATA SHIFT (SSB) menu (EX010405)
// instead of 1500: set to 1000 on 2026-09-24 the slide measured -1003 / +997.
// The page reads it from /api/cat/datashift into the filter state as
// dataShiftHz; 1500 (the menu default) stands in until it arrives.
const SSB_CARRIER_POINT_HZ_101 = 1500;
const RTTY_CENTRE_HZ_101       = -85;
const CW_MAX_SLIDE_WIDTH_HZ_101 = 3000;

const LOWER_SIDEBAND = new Set(['LSB', 'DATA-L', 'CW-L', 'RTTY-L']);

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
 * How far the radio has slid its LO for the current filter settings, in Hz
 * of RF: the offset from the dial of the point the radio holds at its IF
 * OUT frequency. See the table above.
 *
 * FM / DATA-FM / PSK: shift only - unmeasured in those modes.
 *
 * @param {string} model
 * @param {{mode?: string, ifWidthHz?: number, ifShiftHz?: number, cwPitchHz?: number, dataShiftHz?: number}} f
 * @returns {number}
 */
export function loSlideHz(model, f) {
    if (!IF_OUT_HZ[model] || !f) return 0;
    const mode  = (f.mode || '').toUpperCase();
    const width = Number(f.ifWidthHz);
    const shift = Number.isFinite(Number(f.ifShiftHz)) ? Number(f.ifShiftHz) : 0;
    const pitch = Number(f.cwPitchHz) > 0 ? Number(f.cwPitchHz) : 700;
    const side  = LOWER_SIDEBAND.has(mode) ? -1 : 1;

    if (mode === 'CW-U' || mode === 'CW-L') {
        const w = Number.isFinite(width) ? Math.min(width, CW_MAX_SLIDE_WIDTH_HZ_101) : 0;
        return side * (shift + Math.max(0, (w - pitch) / 2));
    }
    if (mode === 'DATA-L' || mode === 'DATA-U') {
        const ds = Number(f.dataShiftHz);
        return side * ((Number.isFinite(ds) && ds >= 0 ? ds : SSB_CARRIER_POINT_HZ_101) + shift);
    }
    if (mode === 'LSB' || mode === 'USB') {
        return side * (SSB_CARRIER_POINT_HZ_101 + shift);
    }
    if (mode === 'RTTY-L' || mode === 'RTTY-U') {
        return RTTY_CENTRE_HZ_101 + side * shift;
    }
    if (mode.startsWith('AM')) return 0;
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
