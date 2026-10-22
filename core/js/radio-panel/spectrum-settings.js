// spectrum-settings.js - the SCOPE panel for one spectrum pane: Range, Speed,
// Bright, Smooth, Passband and DEFAULTS. The board shows it under each pane
// and the pop-out spectrum window beside each of its panes; each keeps the
// settings itself and is handed every change.

import { SPECTRUM_SPEEDS, cleanSpectrumSettings } from './spectrum-view.js';

function h(tag, cls, text) {
    const el = document.createElement(tag);
    if (cls) el.className = cls;
    if (text != null) el.textContent = text;
    return el;
}

/** Saved settings, tidied: SpectrumView's, plus the passband on or off. */
export function tidySpectrumSettings(s) {
    return { ...cleanSpectrumSettings(s ?? {}), passband: s?.passband !== false };
}

/**
 * @param {(patch:object) => void} onChange  a slider or box moved: the setting it names
 * @returns {{el:HTMLElement, show:(cur:object) => void}}  show puts the settings on the controls
 */
export function spectrumSettingsPanel(onChange) {
    const el = h('div', 'rp-spec-settings');
    // Clicks here are not clicks on the spectrum under it.
    for (const ev of ['pointerdown', 'click', 'wheel']) el.addEventListener(ev, e => e.stopPropagation());
    const rows = [];
    const slider = (name, label, min, max, toVal, fromVal, text, title) => {
        const row = h('label', 'rp-spec-row');
        row.title = title;
        const inp = h('input');
        inp.type = 'range'; inp.min = String(min); inp.max = String(max); inp.step = '1';
        inp.dataset.set = name;
        const out = h('span', 'rp-spec-val');
        inp.addEventListener('input', () => onChange({ [name]: toVal(Number(inp.value)) }));
        row.append(h('span', null, label), inp, out);
        el.append(row);
        rows.push(cur => { inp.value = String(fromVal(cur[name])); out.textContent = text(cur[name]); });
    };
    const same = x => x;
    slider('range', 'Range', 5, 160, same, same, x => x + ' dB', 'dB from the bottom of the trace to the top');
    slider('speed', 'Speed', 0, SPECTRUM_SPEEDS.length - 1, i => SPECTRUM_SPEEDS[i], x => SPECTRUM_SPEEDS.indexOf(x),
        x => x === 1 ? 'Full' : '1/' + x, 'How fast the waterfall scrolls');
    slider('bright', 'Bright', 0, 60, same, same, x => x ? '+' + x + ' dB' : 'Off', 'Lifts weak signals in the waterfall only');
    slider('smooth', 'Smooth', 0, 8, same, same, x => x ? String(x) : 'Off', 'Averages neighbouring bins across the trace');
    const pbRow = h('label', 'rp-spec-row');
    pbRow.title = 'Shades the part of the spectrum you are hearing: the width of the receive filter for the mode';
    const pbBox = h('input');
    pbBox.type = 'checkbox';
    pbBox.dataset.set = 'passband';
    pbBox.addEventListener('change', () => onChange({ passband: pbBox.checked }));
    pbRow.append(h('span', null, 'Passband'), pbBox, h('span', 'rp-spec-val', ''));
    el.append(pbRow);
    rows.push(cur => { pbBox.checked = cur.passband; });
    const reset = h('button', 'rp-wkey', 'DEFAULTS');
    reset.type = 'button';
    reset.addEventListener('click', () => onChange(tidySpectrumSettings({})));
    el.append(reset);
    return { el, show: cur => { for (const f of rows) f(cur); } };
}
