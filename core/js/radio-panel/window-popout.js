// window-popout.js - one of the radio panel's windows (Meters, RX Setup ...)
// in a browser window of its own, so it can sit on another screen.
// Shared by Icom Web Control and Yaesu Web Control. This file is copied into
// each app's wwwroot at build time - see js/README.md. Edit it here, in the
// core, never in a wwwroot copy.
//
// A window torn off on the board stays on the board; this is the same group
// of panels in a page of its own. It draws the frame - title, REATTACH, close
// and one box per panel - and the app fills each box through slot(panelId),
// exactly as it fills the board's. Colours follow the board's theme.

import { injectRadioPanelStyle } from './radio-panel-style.js';
import { applyTheme, DEFAULT_THEME } from './radio-panel-themes.js';

const store = {
    get(k, d) { try { return localStorage.getItem(k) ?? d; } catch { return d; } },
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
 * @param {{id:string, title:string, w?:number}[]} opts.panels  w: a fixed width in px
 * @param {() => void} [opts.onReattach]
 * @param {() => void} [opts.onClose]
 * @param {string} [opts.storagePrefix='rwc.radioPanel.']  the board's: the theme is read from it
 * @param {string} [opts.fontBase]
 * @returns {{ slot(panelId:string): HTMLElement|null, setSubtitle(text:string): void }}
 */
export function mountWindowPopout(root, opts) {
    injectRadioPanelStyle(document, opts.fontBase);
    const prefix = opts.storagePrefix ?? 'rwc.radioPanel.';

    root.classList.add('rp-page', 'rp-spop', 'rp-wpop');
    const top = h('div', 'rp-spop-top');
    const sub = h('span', 'rp-win-sub', '');
    top.append(h('span', 'rp-spop-title', opts.title ?? ''), sub);
    const reattach = btn('rp-spop-key', 'REATTACH', () => opts.onReattach?.());
    reattach.title = 'Put this window back on the radio panel and close it';
    const close = btn('rp-spop-key rp-spop-x', '×', () => opts.onClose?.());
    close.title = 'Close';
    top.append(reattach, close);

    // .rp-panel for the panel's own button and box rules; the frame is undone
    // in the style sheet (.rp-wpop-board).
    const board = h('div', 'rp-panel rp-wpop-board');
    const row = h('div', 'rp-win-body');
    board.append(row);
    root.append(top, board);

    // Widths as on the board: a fixed-width panel keeps its width and the rest
    // share what is left; if every one is fixed, the last takes the slack. A
    // window made narrower than that squeezes the fixed ones, never the rest
    // to nothing.
    const slots = {};
    const fixedAll = opts.panels.every(p => p.w);
    opts.panels.forEach((p, i) => {
        const box = h('div', 'rp-wpanel');
        box.dataset.panel = p.id;
        const grow = opts.panels.length === 1 || !p.w || (fixedAll && i === opts.panels.length - 1);
        box.style.flex = grow ? '1 1 0' : `0 1 ${p.w}px`;
        if (grow) box.style.minWidth = '260px';
        const ph = h('div', 'rp-wpanel-head');
        ph.append(h('span', 'rp-wpanel-title', p.title));
        const body = h('div', 'rp-wpanel-body');
        box.append(ph, body);
        row.append(box);
        slots[p.id] = body;
    });

    const setTheme = () => applyTheme(root, store.get(prefix + 'theme', DEFAULT_THEME));
    setTheme();
    // The board's theme picker writes the same key; follow it.
    window.addEventListener('storage', e => { if (e.key === prefix + 'theme') setTheme(); });

    return {
        slot: id => slots[id] ?? null,
        setSubtitle: text => { sub.textContent = text ?? ''; },
    };
}
