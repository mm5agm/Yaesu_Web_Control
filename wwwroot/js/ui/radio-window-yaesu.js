// radio-window-yaesu.js - YWC's feed for one of the radio panel's windows
// (Meters, RX Setup, TX Setup, Scope, Memories) in a browser window of its
// own, the page at /RadioWindow?g=<group>, or one of its panels alone at
// /RadioWindow?g=<group>&p=<panel> (CW's keyer, memories). The frame is core's
// (window-popout.js); the panels are filled by the same code as on the board
// (radio-panel-yaesu-windows.js), so they behave the same in both places.

import { mountWindowPopout } from '/js/radio-panel/window-popout.js';
import { YAESU_WINDOWS, startYaesuWindows } from '/js/ui/radio-panel-yaesu-windows.js';

const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };

function post(url, body) {
    const init = { method: 'POST' };
    if (body !== undefined) { init.headers = { 'Content-Type': 'application/json' }; init.body = JSON.stringify(body); }
    return fetch(url, init)
        .then(res => { if (!res.ok) console.warn('radio window:', url, res.status); return res; })
        .catch(e => console.warn('radio window:', url, e));
}

/**
 * @param {HTMLElement} root
 * @param {object} cfg  { conn, popout, group, panel, model, dual, maxPowerW, maxHz }  popout: the page's PopoutChild;
 *                      panel: a popAlone panel's id, to show that panel alone
 */
export function startYaesuRadioWindow(root, cfg) {
    const g = YAESU_WINDOWS.find(x => x.id === cfg.group && x.popout);
    if (!g) {
        root.textContent = 'No such window.';
        return;
    }
    const one = cfg.panel ? g.panels.find(p => p.id === cfg.panel && p.popAlone) : null;
    if (cfg.panel && !one) {
        root.textContent = 'No such panel.';
        return;
    }
    const title = one ? g.title + ' · ' + one.title : g.title;
    const mine = (gid, pid) => gid === g.id && (!one || pid === one.id);
    document.title = title + ' - Yaesu Web Control';
    const pop = mountWindowPopout(root, {
        title,
        panels: one ? [one] : g.panels,
        onReattach: () => cfg.popout?.reattach(),
        onClose: () => window.close(),
        storagePrefix: 'ywc.radioPanel.',
        fontBase: '/lib/fonts',
    });

    // The windows API the panel builders expect: this group's panels (or the
    // one panel) are the page's boxes and always shown; every other is a body
    // that is never put on a page.
    const spare = {};
    const subtitles = {};
    const windows = {
        slot: (gid, pid) => (mine(gid, pid) ? pop.slot(pid) : (spare[gid + '/' + pid] ??= document.createElement('div'))),
        isOpen: gid => gid === g.id,
        isShown: (gid, pid) => mine(gid, pid),
        open() {}, close() {}, closePanel() {}, showPanel() {},
        setSubtitle: (gid, text) => { subtitles[gid] = text; if (gid === g.id) pop.setSubtitle(text); },
    };

    const r = { sel: 'A', freq: { A: 0, B: 0 } };
    const win = startYaesuWindows(windows, {
        model: cfg.model,
        dual: !!cfg.dual,
        maxPowerW: num(cfg.maxPowerW) || 100,
        maxHz: num(cfg.maxHz) || 75_000_000,
        post,
        selected: () => r.sel,
        freq: vfo => r.freq[vfo] ?? 0,
        popouts: false,
    });

    cfg.conn.on('RadioStateUpdate', msg => {
        const v = msg?.value;
        switch (msg?.property) {
            case 'ActiveVfo': r.sel = num(v) === 1 ? 'B' : 'A'; break;
            case 'FrequencyA': r.freq.A = num(v); break;
            case 'FrequencyB': r.freq.B = num(v); break;
        }
        win.onRadioState(msg);
    });
    // rwcHubConnection hands back an unstarted connection, and this page has
    // no site.js to start it.
    cfg.conn.start().catch(e => console.warn('radio window: hub', e));
}
