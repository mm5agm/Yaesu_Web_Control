// tuning-step-store.js — the per-VFO tuning step, shared by every surface that
// moves the dial by a fixed amount.
//
// Radio-agnostic on purpose: this knows about a VFO name, a number of hertz and
// a place to persist it. It never talks to a radio, a transport or the DOM, so
// it lives in core/ and is used by both apps.
//
// The reason it exists at all is that "how far does one notch move the dial"
// used to be answered independently in three places — a hard-coded 1000 in the
// spectrum panel's wheel handler, whichever digit happened to be selected on the
// frequency display, and the voice nudge step — so the operator had no single
// number to set. One store, three views.
//
// Persistence is best-effort: localStorage throws in a private window and is
// absent in a test harness, so every access is wrapped and a failure just means
// the step is per-session rather than remembered.

/** Steps offered in the UI, ascending. 1 Hz is the point of the exercise —
 *  RTTY and CW operators zero-beat in single hertz. */
export const TUNING_STEPS = [1, 10, 100, 1_000, 10_000, 100_000, 1_000_000];

export const DEFAULT_TUNING_STEP_HZ = 1_000;

/** "1 Hz", "500 Hz", "1 kHz", "1 MHz" — no trailing ".0". */
export function formatTuningStep(hz) {
    const n = Number(hz);
    if (!Number.isFinite(n) || n <= 0) return '';
    if (n >= 1_000_000) return `${trimNumber(n / 1_000_000)} MHz`;
    if (n >= 1_000)     return `${trimNumber(n / 1_000)} kHz`;
    return `${trimNumber(n)} Hz`;
}

/** Spoken form, for TTS and voice-command feedback: "one hertz", "ten kilohertz". */
export function speakTuningStep(hz) {
    const n = Number(hz);
    if (!Number.isFinite(n) || n <= 0) return '';
    if (n >= 1_000_000) return `${trimNumber(n / 1_000_000)} megahertz`;
    if (n >= 1_000)     return `${trimNumber(n / 1_000)} kilohertz`;
    return `${trimNumber(n)} hertz`;
}

function trimNumber(n) {
    return Number.isInteger(n) ? String(n) : String(parseFloat(n.toFixed(3)));
}

/**
 * Snaps an arbitrary hertz value onto the nearest offered step. Callers derive
 * steps from things that don't line up with the list — a clicked frequency digit
 * gives 10 MHz, a stored setting from an older version may give 50 — and a
 * silently-out-of-list value would make the UI show a step the dropdown can't
 * display.
 */
export function clampTuningStep(hz, steps = TUNING_STEPS) {
    const n = Number(hz);
    if (!Number.isFinite(n) || n <= 0) return DEFAULT_TUNING_STEP_HZ;
    let best = steps[0];
    let bestDistance = Infinity;
    for (const step of steps) {
        // Compare in log space: 1 Hz and 10 Hz are as different, to an operator,
        // as 100 kHz and 1 MHz are, which a linear distance would not say.
        const distance = Math.abs(Math.log10(n) - Math.log10(step));
        if (distance < bestDistance) { bestDistance = distance; best = step; }
    }
    return best;
}

export class TuningStepStore {
    /**
     * @param {object}   [options]
     * @param {string}   [options.storageKeyPrefix] localStorage key prefix; the
     *   VFO name is appended, so "ywc.tuningStep." stores "ywc.tuningStep.A".
     *   Each app passes its own so the two never read each other's value.
     * @param {number[]} [options.steps]            allowed steps, ascending
     * @param {number}   [options.defaultStepHz]    used when nothing is stored
     * @param {Storage}  [options.storage]          injectable for tests
     */
    constructor(options = {}) {
        this._prefix      = options.storageKeyPrefix ?? 'radio.tuningStep.';
        this._steps       = options.steps ?? TUNING_STEPS;
        this._default     = clampTuningStep(options.defaultStepHz ?? DEFAULT_TUNING_STEP_HZ, this._steps);
        this._storage     = options.storage ?? defaultStorage();
        this._values      = new Map();      // vfo -> hz, populated lazily
        this._subscribers = new Set();
    }

    get steps() { return this._steps.slice(); }

    /** Current step for a VFO, in Hz. Reads through to storage on first use. */
    get(vfo) {
        const key = normaliseVfo(vfo);
        if (this._values.has(key)) return this._values.get(key);

        let hz = this._default;
        try {
            const raw = this._storage?.getItem(this._prefix + key);
            if (raw !== null && raw !== undefined) hz = clampTuningStep(parseInt(raw, 10), this._steps);
        } catch { /* private window, or no storage at all */ }

        this._values.set(key, hz);
        return hz;
    }

    /**
     * Sets the step for a VFO and notifies subscribers. Returns the value
     * actually stored, which may have been snapped onto the allowed list.
     * Setting the value it already has notifies nobody, so a two-way sync
     * between views cannot loop.
     */
    set(vfo, hz, meta = {}) {
        const key      = normaliseVfo(vfo);
        const previous = this.get(key);
        const next     = clampTuningStep(hz, this._steps);
        if (next === previous) return next;

        this._values.set(key, next);
        try { this._storage?.setItem(this._prefix + key, String(next)); } catch { /* as above */ }

        for (const fn of this._subscribers) {
            try { fn(key, next, meta); } catch { /* one bad subscriber must not stop the rest */ }
        }
        return next;
    }

    /** @returns {() => void} unsubscribe */
    subscribe(fn) {
        if (typeof fn !== 'function') return () => {};
        this._subscribers.add(fn);
        return () => this._subscribers.delete(fn);
    }
}

function normaliseVfo(vfo) {
    return String(vfo ?? 'A').toUpperCase() === 'B' ? 'B' : 'A';
}

function defaultStorage() {
    try { return globalThis.localStorage ?? null; } catch { return null; }
}
