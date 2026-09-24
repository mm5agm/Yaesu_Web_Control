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

export const RTTY_SHIFTS = [170, 200, 425, 450, 850];
export const DEFAULT_RTTY_SETTINGS = Object.freeze({ markHz: 2125, shiftHz: 170, reverse: false });

const LS_KEY = 'rttyTuner';

/** A valid settings object from anything; whatever is missing or out of range takes the default. */
export function normaliseRttySettings(s) {
    const out = { ...DEFAULT_RTTY_SETTINGS };
    if (!s || typeof s !== 'object') return out;
    const mark = Number(s.markHz);
    const shift = Number(s.shiftHz);
    if (Number.isFinite(mark) && mark >= 300 && mark <= 3000) out.markHz = Math.round(mark);
    if (RTTY_SHIFTS.includes(shift)) out.shiftHz = shift;
    out.reverse = s.reverse === true;
    return out;
}

export function loadRttySettings(storage = globalThis.localStorage) {
    try {
        return normaliseRttySettings(JSON.parse(storage?.getItem(LS_KEY) || 'null'));
    } catch {
        return { ...DEFAULT_RTTY_SETTINGS };   // private window, blocked storage
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
