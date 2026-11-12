// spectrum-refine.js - the Refine control for a spectrum drawn from a radio's
// own scope, shared by every view in both apps.
//
// Refine is how much the radio's scope sweeps are averaged before they are
// drawn (SpectrumRefiner in core/Services/Spectrum, run by the app's server).
// Each sweep from a radio's own scope is a separate snapshot with a rough
// floor; an SDR's frames come already averaged by its worker, so the server
// says per panel whether Refine applies, and the control only shows where it
// does:
//   IWC  the IC-7300's scope over CI-V: always
//   YWC  the FTdx101's scope from its ACC socket: only on a panel showing it
//
// The step is kept on the server, not in the browser: the averaging happens
// before the frames reach any page, so every view and every browser sees the
// same trace.
//
//   GET  /api/sdr/refine?sdrId=A          -> {available, level, max}
//   POST /api/sdr/refine?sdrId=A&level=3
//
// (YWC's server says `acc` for `available`; both are read.)
//
// Every view has it (Colin, 2026-11-12), and any new view should too; each
// app has a test that fails for a view that draws the spectrum without it:
//   Classic and Experimental                   bindRefineSlider(), on their own slider markup
//   Radio, its pop-out spectrum, RTTY Contest  addRefineTo(), a list beside SPAN

const API = '/api/sdr/refine';

export const REFINE_TITLE =
    "Refine - how much the radio's own scope is averaged before it is drawn. Each sweep from the " +
    "radio is a separate snapshot with a rough floor; averaging a few lets the floor settle into a " +
    'thin line and weak signals stand out of it. Raw is the sweeps as they come; higher is calmer ' +
    'but slower to follow a signal that comes and goes.';

export const formatRefine = n => (n <= 0 ? 'Raw' : String(n));

const applies = info => !!(info && (info.available ?? info.acc));

/**
 * What the server says about a panel's Refine.
 * @param {'A'|'B'} vfo
 * @returns {Promise<{available?:boolean, acc?:boolean, level:number, max:number}|null>}  null when it can't be asked
 */
export async function fetchRefine(vfo) {
    try {
        const r = await fetch(`${API}?sdrId=${vfo}`);
        return r.ok ? await r.json() : null;
    } catch { return null; }
}

/** Saves a panel's step; the next sweep is drawn with it. */
export function saveRefine(vfo, level) {
    return fetch(`${API}?sdrId=${vfo}&level=${level}`, { method: 'POST' })
        .then(r => { if (!r.ok) console.warn('Refine not saved:', r.status); })
        .catch(err => console.warn('Refine not saved:', err));
}

/**
 * Classic and Experimental: wires a slider the page has drawn, hidden, and
 * shows it if Refine applies to the panel.
 * @param {{group:HTMLElement, slider:HTMLInputElement, label:HTMLElement, vfo:'A'|'B', title?:string}} p
 */
export function bindRefineSlider({ group, slider, label, vfo, title = null }) {
    if (!group || !slider || !label) return;
    if (title) group.title = title;
    slider.addEventListener('input', () => { label.textContent = formatRefine(parseInt(slider.value, 10)); });
    // On release, not on every step of a drag: each POST saves settings.
    slider.addEventListener('change', () => saveRefine(vfo, parseInt(slider.value, 10)));
    fetchRefine(vfo).then(info => {
        if (!applies(info)) return;
        slider.max = String(info.max);
        slider.value = String(info.level);
        label.textContent = formatRefine(info.level);
        group.style.display = 'contents';
    });
}

/**
 * The radio-panel views: a REFINE list styled like the view's SPAN list.
 * Returns the element at once, hidden; it shows itself if Refine applies.
 * @param {'A'|'B'} vfo
 * @param {string} className  the view's class for its SPAN list
 * @param {string} [title]    the tooltip; REFINE_TITLE if not given
 * @returns {HTMLSelectElement}
 */
export function refineSelect(vfo, className, title = REFINE_TITLE) {
    const sel = document.createElement('select');
    sel.className = className + ' rwc-refine';
    sel.title = title;
    sel.setAttribute('aria-label', "Refine: how much the radio's scope is averaged");
    sel.style.display = 'none';   // not [hidden]: a view's CSS may set display
    sel.addEventListener('change', () => saveRefine(vfo, Number(sel.value)));
    // The views stop a click in their tool strips reaching the spectrum
    // (click-to-tune) themselves; this stops it here too for any that don't.
    for (const ev of ['pointerdown', 'click', 'wheel']) sel.addEventListener(ev, e => e.stopPropagation());
    fetchRefine(vfo).then(info => {
        if (!applies(info)) return;
        for (let n = 0; n <= info.max; n++) sel.append(new Option('REFINE ' + formatRefine(n).toUpperCase(), String(n)));
        sel.value = String(info.level);
        sel.style.display = '';
    });
    return sel;
}

/**
 * Puts a REFINE list in a tool strip: before `before` if given and present,
 * else at the end. Does nothing without a strip.
 */
export function addRefineTo(strip, vfo, className, before = null, title = REFINE_TITLE) {
    if (!strip) return null;
    const sel = refineSelect(vfo, className, title);
    const at = before && strip.querySelector(before);
    if (at) strip.insertBefore(sel, at); else strip.append(sel);
    return sel;
}
