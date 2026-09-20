// tuning-step.js — YWC's single tuning-step instance, plus the wiring that
// keeps the three ways of setting it in agreement.
//
// The store itself is shared (core/js/tuning/tuning-step-store.js, copied into
// wwwroot/js/tuning/ at build time). What lives here is YWC-specific: the
// storage key prefix, the bridge onto window for site.js (a classic script, not
// a module), the screen-reader announcement, and the one-way-at-a-time sync with
// the voice nudge step, which is a server-side setting rather than a browser one.
//
// Deliberately NOT seeded from the voice nudge step on load. That setting
// defaults to 10 kHz, and seeding from it would silently make the spectrum wheel
// ten times coarser for every voice user on upgrade. The wheel keeps its old
// 1 kHz default until somebody changes it; after that the two follow each other.

import { TuningStepStore, TUNING_STEPS, formatTuningStep }
    from '../tuning/tuning-step-store.js?v=1';

// The instance lives on `window`, not in this module, and that is deliberate.
//
// A module's identity is its full URL, query string included. Index.cshtml
// imports this file as `?v=<AppVersion>`, spectrum-panel.js imports it as
// `?v=1`, so the browser evaluates it TWICE and each copy would own a separate
// store. The frequency display would then set one store and the spectrum wheel
// would read the other, so clicking a digit changed the Step box but not the
// wheel -- which is exactly what happened on 2026-09-20. Anything stateful
// reached from both a Razor page and another module has this problem; an import
// map is the general cure, this is the local one.
function existingStore() {
    // Duck-typed rather than `instanceof`: a second copy of the store module
    // has its own class object, so `instanceof` would reject a perfectly good
    // store created by the other copy.
    const candidate = globalThis.window ? window.ywcTuningStep : null;
    return (candidate
        && typeof candidate.get       === 'function'
        && typeof candidate.set       === 'function'
        && typeof candidate.subscribe === 'function')
        ? candidate
        : null;
}

export const tuningStep = existingStore() ?? new TuningStepStore({
    storageKeyPrefix: 'ywc.tuningStep.',
});

// Steps the voice nudge API will accept. 1 MHz is offered on the wheel but has
// no voice phrase and is rejected by the controller, so a wheel step of 1 MHz
// simply doesn't propagate to the voice setting.
const VOICE_VALID_STEPS = [1, 10, 100, 1_000, 10_000, 100_000];

// Classic scripts (site.js) can't import, so expose the bits they need.
window.ywcTuningStep       = tuningStep;
window.ywcTuningSteps      = TUNING_STEPS;
window.ywcFormatTuningStep = formatTuningStep;

/**
 * Announce into the shared live region. Cleared first so a screen reader
 * re-reads an identical message — stepping 1 kHz -> 1 kHz can't happen, but
 * A and B can reach the same value and the user needs to hear which one moved.
 */
function announce(message) {
    const el = document.getElementById('vfoStatusAnnounce');
    if (!el || !message) return;
    el.textContent = '';
    requestAnimationFrame(() => { el.textContent = message; });
}

/**
 * Push a step change out to the voice nudge setting, so "tune up" moves the
 * dial by the same amount the wheel does. Skipped when the value has no voice
 * equivalent, and silent on failure: voice is compiled out entirely on the
 * net10.0 CAT-only host, so a 404 here is a normal configuration, not a fault.
 */
async function syncVoiceNudgeStep(vfo, stepHz) {
    if (!VOICE_VALID_STEPS.includes(stepHz)) return;

    const select = document.getElementById('voiceNudgeStepSelect' + vfo);
    if (select && select.value !== String(stepHz)) select.value = String(stepHz);

    try {
        await fetch('/api/voice/nudge-step', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify({ stepHz, vfo }),
        });
    } catch { /* voice not present on this host */ }
}

// Guarded for the same reason the store is: a second copy of this module must
// not add a second announcement and a second voice POST for every change.
if (!window.ywcTuningStepWired) {
    window.ywcTuningStepWired = true;

    tuningStep.subscribe((vfo, stepHz, meta) => {
        if (meta.silent !== true) {
            announce(`VFO ${vfo} tuning step ${formatTuningStep(stepHz)}`);
        }
        // `fromVoice` marks a change that came FROM the voice dropdown, which
        // has already told the server. Echoing it back would be a redundant POST.
        if (meta.fromVoice !== true) syncVoiceNudgeStep(vfo, stepHz);
    });
}
