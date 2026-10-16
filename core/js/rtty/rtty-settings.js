// RTTY tone settings: the Mark, Shift and Rev the operator types into the
// RTTY tuner, kept in the browser.
//
// They live in their own module because they are wanted outside the tuner as
// well. In an AFSK mode the tones are the RTTY software's, not the radio's,
// and the tuner is where the operator says what their software uses - so
// click-to-tune reads the same settings to put a clicked signal's tones where
// that software is listening. One place to set it, one place to read it.
//
// Stateless: every call reads storage afresh, so a page that imports this by
// two different URLs (two module instances) still sees one set of settings.

// The shifts and speeds with a name, for a host page that wants to offer a set
// and for recognising a measured figure. Not a limit: see normaliseRttySettings.
//
// Which of these a given rig can be *set* to is a per-radio fact and nothing
// here knows about any one radio - the host page says so by the options it puts
// in its Shift control.
export const RTTY_SHIFTS = [170, 200, 425, 450, 850];
export const RTTY_BAUDS  = [45.45, 50, 56.9, 74.2, 75, 100];
export const DEFAULT_RTTY_SETTINGS = Object.freeze({
    markHz: 2125, shiftHz: 170, reverse: false, baud: 45.45,
});

// What a shift and a speed may be at all, as opposed to which ones have names.
// Wide, deliberately: the tuner is two filters and a decoder is arithmetic, and
// between them they can work on any station that fits in the receiver's audio.
const SHIFT_MIN = 20,  SHIFT_MAX = 1200;
const BAUD_MIN  = 20,  BAUD_MAX  = 300;

const LS_KEY = 'rttyTuner';

/**
 * A valid settings object from anything; whatever is missing or out of range
 * takes the default.
 *
 * Any shift and any speed inside the limits above are kept, named or not.
 *
 * This used to take the host's list of shifts and snap anything else onto it,
 * on the reasoning that a shift the radio cannot be set to is a shift the
 * operator cannot use. That is the wrong way round, and Colin settled it on
 * 2026-10-08: the decoder is the authority, not the radio's own. A listener
 * meets 450 Hz on the German weather stations and 850 on aviation circuits
 * daily, and the tuner and the decoder can both work on them; only the radio's
 * built-in decoder cannot, and nothing here depends on it.
 *
 * The problem the snapping did solve is real, though - a shift with no rung
 * leaves a <select> of rungs showing nothing. It is solved where it belongs, in
 * the dialog, which shows such a figure as a measurement instead of silently
 * substituting a different one.
 */
export function normaliseRttySettings(s) {
    const out = { ...DEFAULT_RTTY_SETTINGS };
    if (!s || typeof s !== 'object') return out;
    const mark  = Number(s.markHz);
    const shift = Number(s.shiftHz);
    const baud  = Number(s.baud);
    if (Number.isFinite(mark) && mark >= 300 && mark <= 3000) out.markHz = Math.round(mark);
    if (Number.isFinite(shift) && shift >= SHIFT_MIN && shift <= SHIFT_MAX) out.shiftHz = Math.round(shift);
    if (Number.isFinite(baud) && baud >= BAUD_MIN && baud <= BAUD_MAX) out.baud = Math.round(baud * 100) / 100;
    out.reverse = s.reverse === true;
    return out;
}

export function loadRttySettings(storage = globalThis.localStorage) {
    try {
        return normaliseRttySettings(JSON.parse(storage?.getItem(LS_KEY) || 'null'));
    } catch {
        return normaliseRttySettings(null);   // private window, blocked storage
    }
}

export function saveRttySettings(settings, storage = globalThis.localStorage) {
    try { storage?.setItem(LS_KEY, JSON.stringify(normaliseRttySettings(settings))); } catch { /* ignore */ }
}

/**
 * The audio frequency midway between the software's mark and space, for AFSK
 * RTTY on lower sideband: space is above mark in audio unless Rev is ticked.
 * 2125 / 170 gives 2210; 1415 / 170 gives 1500.
 */
export function afskMidpointAudioHz(settings) {
    const s = normaliseRttySettings(settings);
    return s.markHz + (s.reverse ? -s.shiftHz / 2 : s.shiftHz / 2);
}
