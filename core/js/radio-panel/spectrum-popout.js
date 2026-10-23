// spectrum-popout.js - the radio panel's spectrum in a window of its own, so
// it can sit on a second screen: one receiver or both, one above the other.
//
// The page around it is the app's: it opens the hub, orders the bins and
// feeds each pane (spectrum-pane.js). This draws the window - title, the
// Main / Sub / Both tabs, the DX spots switch, REATTACH and close - and
// keeps the operator's choices. Colours follow the radio panel's theme. Each
// pane has the board's HOLD and SCOPE panel (Range / Speed / Bright / Smooth /
// Passband) for that receiver: both write the same setting, so a change made
// in either place shows in the other.

import { injectRadioPanelStyle } from './radio-panel-style.js';
import { applyTheme, DEFAULT_THEME } from './radio-panel-themes.js';
import { SpectrumPane } from './spectrum-pane.js';
import { spectrumSettingsPanel, tidySpectrumSettings } from './spectrum-settings.js';

const store = {
    get(k, d) { try { return localStorage.getItem(k) ?? d; } catch { return d; } },
    set(k, val) { try { localStorage.setItem(k, val); } catch { /* private window */ } },
};

function h(tag, cls, text) {
    const el = document.createElement(tag);
    if (cls) el.className = cls;
    if (text != null) el.textContent = text;
    return el;
}
function btn(cls, text, onClick) {
    const b = h('button', cls, text);
    b.type = 'button';
    b.addEventListener('click', onClick);
    return b;
}

/**
 * @param {HTMLElement} root
 * @param {object} opts
 * @param {string} opts.title
 * @param {{id:string, tab:string, name:string}[]} opts.panes  one or two; tab is 'Main', name is 'MAIN · SDR A'
 * @param {{hz:number, label:string}[]} [opts.spans]  the span list; none, no span picker
 * @param {(id:string, hz:number) => void} [opts.onSpan]
 * @param {() => void} [opts.onReattach]
 * @param {() => void} [opts.onClose]
 * @param {string} [opts.storagePrefix='rwc.radioPanel.']  the board's: the theme is read from it
 * @param {string} [opts.fontBase]
 */
export function mountSpectrumPopout(root, opts) {
    injectRadioPanelStyle(document, opts.fontBase);
    const prefix = opts.storagePrefix ?? 'rwc.radioPanel.';
    const own = prefix + 'popout.';
    const ids = opts.panes.map(p => p.id);

    root.classList.add('rp-page', 'rp-spop');
    const top = h('div', 'rp-spop-top');
    top.append(h('span', 'rp-spop-title', opts.title ?? 'SPECTRUM'));
    const tabs = h('div', 'rp-spop-tabs');
    const tabEls = {};
    const views = ids.length > 1 ? [...ids, 'both'] : ids;
    for (const v of views) {
        const label = v === 'both' ? 'Both' : opts.panes.find(p => p.id === v).tab;
        tabEls[v] = btn('rp-spop-tab', label, () => setView(v));
        tabs.append(tabEls[v]);
    }
    if (views.length > 1) top.append(tabs);
    top.append(h('span', 'rp-spop-gap'));
    const spotsBtn = btn('rp-spop-key', 'DX SPOTS', () => setShowSpots(!showSpots));
    const reattach = btn('rp-spop-key', 'REATTACH', () => opts.onReattach?.());
    reattach.title = 'Put the spectrum back on the radio panel and close this window';
    const close = btn('rp-spop-key rp-spop-x', '×', () => opts.onClose?.());
    close.title = 'Close';
    top.append(spotsBtn, reattach, close);

    const body = h('div', 'rp-spop-body');
    root.append(top, body);

    // A grip in the bottom-right corner sizes the window from inside the page,
    // for when its frame is hard to reach. Browsers let script resize a window
    // that script opened, which this one is; elsewhere resizeTo does nothing.
    const grip = h('div', 'rp-spop-grip');
    grip.title = 'Drag to resize the window';
    grip.addEventListener('pointerdown', e => {
        e.preventDefault();
        grip.setPointerCapture(e.pointerId);
        const x0 = e.screenX, y0 = e.screenY, w0 = window.outerWidth, h0 = window.outerHeight;
        const move = m => window.resizeTo(Math.max(400, w0 + m.screenX - x0), Math.max(300, h0 + m.screenY - y0));
        const up = () => {
            grip.removeEventListener('pointermove', move);
            grip.removeEventListener('pointerup', up);
            grip.removeEventListener('pointercancel', up);
        };
        grip.addEventListener('pointermove', move);
        grip.addEventListener('pointerup', up);
        grip.addEventListener('pointercancel', up);
    });
    root.append(grip);

    const panes = {};
    const spanSel = {};
    const holdKeys = {};
    const setPanels = {};
    for (const p of opts.panes) {
        const holder = h('div', 'rp-spop-holder');
        holder.dataset.pane = p.id;
        body.append(holder);
        const pane = new SpectrumPane(holder);
        pane.setName(p.name);
        if (opts.spans?.length) {
            const sel = h('select', 'rp-spop-span');
            sel.title = 'Span';
            for (const s of opts.spans) sel.append(new Option('SPAN ' + s.label, String(s.hz)));
            sel.addEventListener('change', () => opts.onSpan?.(p.id, Number(sel.value)));
            pane.tools.append(sel);
            spanSel[p.id] = sel;
        }
        const hold = btn('rp-spop-key rp-spop-hold', 'HOLD', () => changeSettings(p.id, { hold: !readSettings(p.id).hold }));
        hold.title = 'Freeze the spectrum and waterfall';
        const sets = spectrumSettingsPanel(patch => changeSettings(p.id, patch));
        const setKey = btn('rp-spop-key rp-spop-setkey', 'SCOPE', () => setKey.classList.toggle('on', sets.el.classList.toggle('open')));
        setKey.title = 'Range, waterfall speed and brightness, smoothing, passband';
        pane.tools.append(hold, setKey);
        pane.el.append(sets.el);
        panes[p.id] = pane;
        holdKeys[p.id] = hold;
        setPanels[p.id] = sets;
    }

    // The board writes these; this window reads and follows them.
    function readSettings(id) {
        let s = {};
        try { s = JSON.parse(store.get(prefix + 'spectrum.' + id, '{}')) ?? {}; } catch { /* defaults */ }
        return tidySpectrumSettings(s);
    }
    function changeSettings(id, patch) {
        store.set(prefix + 'spectrum.' + id, JSON.stringify(tidySpectrumSettings({ ...readSettings(id), ...patch })));
        applySettings(id);
    }
    function applySettings(id) {
        const s = readSettings(id);
        panes[id].setSettings(s);
        holdKeys[id].classList.toggle('on', s.hold);
        setPanels[id].show(s);
    }
    for (const id of ids) applySettings(id);

    let view = store.get(own + 'view', views.length > 1 ? 'both' : ids[0]);
    let showSpots = store.get(own + 'spots', '1') === '1';

    function setView(v) {
        if (!views.includes(v)) v = views[views.length - 1];
        view = v;
        store.set(own + 'view', v);
        for (const [k, el] of Object.entries(tabEls)) el.classList.toggle('on', k === v);
        for (const holder of body.children) holder.style.display = v === 'both' || holder.dataset.pane === v ? '' : 'none';
    }
    function setShowSpots(on) {
        showSpots = on;
        store.set(own + 'spots', on ? '1' : '0');
        spotsBtn.classList.toggle('on', on);
        for (const p of Object.values(panes)) p.setShowSpots(on);
    }
    function setTheme() {
        const t = applyTheme(root, store.get(prefix + 'theme', DEFAULT_THEME));
        for (const p of Object.values(panes)) p.setTheme(t);
    }
    // The board's theme picker writes the same key; follow it.
    window.addEventListener('storage', e => {
        if (e.key === prefix + 'theme') setTheme();
        for (const id of ids) if (e.key === prefix + 'spectrum.' + id) applySettings(id);
    });
    try {
        window.matchMedia('(prefers-color-scheme: light)').addEventListener('change', setTheme);
    } catch { /* nothing to follow */ }

    setTheme();
    setView(view);
    setShowSpots(showSpots);

    return {
        pane: id => panes[id],
        /** Show the span the SDR is actually at, e.g. after a change from the main page. */
        setSpanValue(id, hz) {
            const sel = spanSel[id];
            if (!sel) return;
            // A frame's span is whole bins: 20 kHz arrives as 19989 Hz.
            const n = Number(hz) || 0;
            const o = [...sel.options].find(x => Math.abs(Number(x.value) - n) <= Number(x.value) * 0.02);
            if (o && sel.value !== o.value && document.activeElement !== sel) sel.value = o.value;
        },
        setView,
        get view() { return view; },
    };
}
