// acc-refine.js - the Refine control for a spectrum panel showing the
// FTdx101's own scope from its ACC socket, shared by every view.
//
// Refine is how much the radio's raw scope rows are averaged before they are
// drawn (ScopeRefiner in the YaesuAccScope library, run by SdrManager). Each
// row is a separate snapshot with a rough floor, where an SDR's frames come
// already averaged by its worker, so it only applies to the ACC scope and the
// control only shows on a panel whose device is the ACC scope.
//
// The step is kept on the server, per panel (AccRefineLevelA/B), not in the
// browser: the averaging happens before the frames reach any page, so every
// view and every browser sees the same trace.
//
// Every view has it (Colin, 2026-11-12), and any new view should too:
//   Classic and Experimental  bindRefineSlider(), on their own slider markup
//   Radio, its pop-out spectrum, RTTY Contest  refineSelect(), a list beside SPAN

const API = '/api/sdr/refine';

export const REFINE_TITLE =
    "Refine - how much the FTdx101's own scope is averaged before it is drawn. Each frame from the " +
    "radio's ACC socket is a separate snapshot with a rough floor; averaging a few lets the floor settle " +
    'into a thin line and weak signals stand out of it. Raw is the frames as they come; higher is calmer ' +
    'but slower to follow a signal that comes and goes. Only shown when this panel shows the ACC scope.';

export const formatRefine = n => (n <= 0 ? 'Raw' : String(n));

/**
 * What the server says about a panel's Refine.
 * @param {'A'|'B'} vfo
 * @returns {Promise<{acc:boolean, level:number, max:number}|null>}  null when it can't be asked
 */
export async function fetchRefine(vfo) {
    try {
        const r = await fetch(`${API}?sdrId=${vfo}`);
        return r.ok ? await r.json() : null;
    } catch { return null; }
}

/** Saves a panel's step; the next frame is drawn with it. */
export function saveRefine(vfo, level) {
    return fetch(`${API}?sdrId=${vfo}&level=${level}`, { method: 'POST' })
        .then(r => { if (!r.ok) console.warn('Refine not saved:', r.status); })
        .catch(err => console.warn('Refine not saved:', err));
}

/**
 * Classic and Experimental: wires a slider the page has drawn, hidden, and
 * shows it if the panel shows the ACC scope.
 * @param {{group:HTMLElement, slider:HTMLInputElement, label:HTMLElement, vfo:'A'|'B'}} p
 */
export function bindRefineSlider({ group, slider, label, vfo }) {
    if (!group || !slider || !label) return;
    slider.addEventListener('input', () => { label.textContent = formatRefine(parseInt(slider.value, 10)); });
    // On release, not on every step of a drag: each POST saves settings.
    slider.addEventListener('change', () => saveRefine(vfo, parseInt(slider.value, 10)));
    fetchRefine(vfo).then(info => {
        if (!info?.acc) return;
        slider.max = String(info.max);
        slider.value = String(info.level);
        label.textContent = formatRefine(info.level);
        group.style.display = 'contents';
    });
}

/**
 * The radio-panel views: a REFINE list styled like the view's SPAN list.
 * Returns the element at once, hidden; it shows itself if the panel shows
 * the ACC scope.
 * @param {'A'|'B'} vfo
 * @param {string} className  the view's class for its SPAN list
 * @returns {HTMLSelectElement}
 */
export function refineSelect(vfo, className) {
    const sel = document.createElement('select');
    sel.className = className + ' ywc-refine';
    sel.title = REFINE_TITLE;
    sel.setAttribute('aria-label', 'Refine: how much the ACC scope is averaged');
    sel.style.display = 'none';   // not [hidden]: a view's CSS may set display
    sel.addEventListener('change', () => saveRefine(vfo, Number(sel.value)));
    // The views stop a click in their tool strips reaching the spectrum
    // (click-to-tune) themselves; this stops it here too for any that don't.
    for (const ev of ['pointerdown', 'click', 'wheel']) sel.addEventListener(ev, e => e.stopPropagation());
    fetchRefine(vfo).then(info => {
        if (!info?.acc) return;
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
export function addRefineTo(strip, vfo, className, before = null) {
    if (!strip) return null;
    const sel = refineSelect(vfo, className);
    const at = before && strip.querySelector(before);
    if (at) strip.insertBefore(sel, at); else strip.append(sel);
    return sel;
}
