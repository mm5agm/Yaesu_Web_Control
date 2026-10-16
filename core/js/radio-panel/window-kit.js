// window-kit.js - the parts of the radio panel's windows that are the same in
// every app: framed pages, browser windows of their own (⇱), memories and
// banks, the DX watch list, the frequency keypad and meter bars.
// Shared by Icom Web Control and Yaesu Web Control. This file is copied into
// each app's wwwroot at build time - see js/README.md. Edit it here, in the
// core, never in a wwwroot copy.
//
// Each app says which windows it has and fills the radio's own panels itself
// (radio-panel-yaesu-windows.js, radio-panel-icom-windows.js); what is here
// talks only to the apps' shared HTTP API (/api/memory, /api/memorybank,
// /api/dxcluster/watched, /api/cat/frequency) and to panel-windows.js.
//
// `kit` is { windows, shows }: windows is the panel's windows API
// (panel-windows.js, or the stand-in on a /RadioWindow page), and shows maps
// 'group/panel' to fn(shown), which the app calls from its onShow.

import { PopoutHost } from '/js/popout/popout.js';

export const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };
export const on = v => v === true || v === 'true' || v === 1 || (typeof v === 'string' && v !== '' && v !== '0' && v !== 'false');

export function h(tag, cls, text) {
    const el = document.createElement(tag);
    if (cls) el.className = cls;
    if (text != null) el.textContent = text;
    return el;
}
export function key(label, onClick, cls = '') {
    const b = h('button', 'rp-wkey' + (cls ? ' ' + cls : ''), label);
    b.type = 'button';
    b.addEventListener('click', onClick);
    return b;
}
export function note(el, text) { el.append(h('div', 'rp-wnote', text)); }

/** A label and its value on one line; returns the value's element. */
export function row(body, label) {
    const out = h('span', 'rp-wval', '--');
    const r = h('div', 'rp-wrow');
    r.append(h('span', 'rp-wlabel', label), out);
    body.append(r);
    return out;
}
/** A line of keys: list is [label, onClick, cls?]; returns the line. */
export function keys(body, list) {
    const b = h('div', 'rp-wbtns');
    for (const [label, fn, cls] of list) b.append(key(label, fn, cls));
    body.append(b);
    return b;
}

export async function getJson(url) {
    try { const r = await fetch(url); return r.ok ? await r.json() : null; } catch { return null; }
}

export function mhzText(hz) {
    const f = num(hz) / 1e6;
    return f.toFixed(f >= 10 ? 4 : 5).replace(/0+$/, '').replace(/\.$/, '.0');
}

// ---- framed pages ----------------------------------------------------------
// The pop-out pages' own window controls mean nothing in a frame: the panel
// window has its own title, close and tear-off. embedCss is the app's list.
//
// url may be a function, read each time the panel is shown; reload() re-reads
// it for a page that follows the selected receiver.
export function framed(kit, g, p, url, title, { allow, embedCss } = {}) {
    const body = kit.windows.slot(g, p);
    if (!body) return null;
    body.classList.add('flush');
    const f = h('iframe', 'rp-wframe');
    f.title = title;
    if (allow) f.allow = allow;
    const urlNow = () => (typeof url === 'function' ? url() : url);
    f.addEventListener('load', () => {
        try {
            const doc = f.contentDocument;
            if (!embedCss || !doc?.head || doc.getElementById('rp-embed-css')) return;
            const st = doc.createElement('style');
            st.id = 'rp-embed-css';
            st.textContent = embedCss;
            doc.head.append(st);
        } catch { /* not same-origin: leave it as it is */ }
    });
    body.append(f);
    // Loaded the first time the panel is shown, not with the page.
    kit.shows[g + '/' + p] = shown => { if (shown && !f.getAttribute('src')) f.src = urlNow(); };
    return {
        reload() {
            const u = urlNow();
            if (f.getAttribute('src') && f.getAttribute('src') !== u) f.src = u;
        },
    };
}

// ---- browser windows of their own (⇱) ----------------------------------------
// While one is open its panel is off the board, so the page is not running
// twice. A pop-out already open from Classic says so when this page starts,
// and takes its panel off here too. A group marked popout opens whole, at
// /RadioWindow?g=<id>, the same way. Returns 'g' / 'g/p' -> PopoutHost.
export function windowPopouts(kit, groups) {
    const { windows } = kit;
    const popouts = {};
    for (const g of groups) {
        if (g.popout) {
            popouts[g.id] = new PopoutHost({
                name: 'radio-win-' + g.id,
                url: '/RadioWindow?g=' + g.id,
                defaultSize: g.popout,
                // Out of the board whole, torn-off panels included (Colin,
                // 2026-10-15); REATTACH brings them all back in the window.
                onChange: open => {
                    if (!open) return;
                    windows.close(g.id);
                    for (const p of g.panels) windows.closePanel(g.id, p.id);
                },
                onReattach: () => windows.open(g.id),
            }).start();
        }
        for (const p of g.panels) {
            // In a group that pops out whole (CW, RTTY), a panel's ⇱ does that
            // too: the reader alone, without its keyer and send, is half a job.
            if (!p.popout || g.popout) continue;
            popouts[g.id + '/' + p.id] = new PopoutHost({
                name: p.popout.name,
                url: p.popout.url,
                defaultSize: p.popout.size,
                onChange: open => { if (open) windows.closePanel(g.id, p.id); },
                onReattach: () => windows.showPanel(g.id, p.id),
            }).start();
        }
    }
    return popouts;
}

/** A panel's ⇱ key, or a group's (p null): open its browser window. */
export function openPopout(popouts, g, p) {
    const host = (p && popouts[g + '/' + p]) || popouts[g];
    if (host && !host.open()) alert('The browser blocked the window. Allow pop-ups for this page and press the key again.');
}

// ---- meter bars ----------------------------------------------------------------
/** Bars in a panel body: add(id, label, tx), set(id, fraction, text). */
export function meterBars(body) {
    const bars = {};
    return {
        add(id, label, tx) {
            const el = h('div', 'rp-wbar' + (tx ? ' tx' : ''));
            const track = h('div', 'track'), fill = h('div', 'fill'), txt = h('span', 'txt', '--');
            track.append(fill);
            el.append(h('span', 'lbl', label), track, txt);
            el.dataset.meter = id;
            body.append(el);
            bars[id] = { fill, txt };
        },
        set(id, frac, text) {
            const b = bars[id];
            if (!b) return;
            b.fill.style.width = (Math.max(0, Math.min(1, frac)) * 100).toFixed(1) + '%';
            b.txt.textContent = text;
        },
    };
}

// ---- memories ----------------------------------------------------------------
/**
 * The memories window: bank, channel tiles, save. Panels mem/memBank,
 * mem/memTiles and mem/memRadio.
 * @param {object} ctx  { post, selected(), freq(vfo), dual, appShort }  appShort: 'YWC' / 'IWC'
 * @returns {{ mark() }}  mark: light the tile the selected VFO sits on
 */
export function memoriesWindow(kit, ctx) {
    const STARTER = '__starter__';
    const slot = (g, p) => kit.windows.slot(g, p);
    const bankBody = slot('mem', 'memBank');
    const sel = h('select', 'rp-wselect');
    const loadBtn = key('LOAD', () => loadBank());
    const saveBtn = key('SAVE TO BANK', () => saveBank());
    saveBtn.title = 'Write the memories you have now over the chosen bank';
    const IDLE = 'LOAD replaces the memories you have now with the bank. SAVE TO BANK writes them over the bank.';
    const bankMsg = h('div', 'rp-wnote', IDLE);
    const bb = h('div', 'rp-wbtns');
    bb.append(loadBtn, saveBtn);
    bankBody.append(sel, bb, bankMsg);

    const tilesBody = slot('mem', 'memTiles');
    const tiles = h('div', 'rp-wtiles');
    tilesBody.append(tiles);

    const saveBody = slot('mem', 'memRadio');
    const saveMsg = h('div', 'rp-wnote', '');
    const sb = h('div', 'rp-wbtns');
    sb.append(key(ctx.dual ? 'SAVE MAIN' : 'SAVE VFO-A', () => saveVfo('A')),
              key(ctx.dual ? 'SAVE SUB' : 'SAVE VFO-B', () => saveVfo('B')));
    const edit = key('EDIT…', () => window.open('/Memories', '_blank', 'noopener'));
    saveBody.append(sb, saveMsg, edit);

    let list = [], armed = null, armTimer = null;    // armed: 'load' or 'save', waiting for its second press

    async function refreshBanks() {
        const names = (await getJson('/api/memorybank')) ?? [];
        const keep = sel.value;
        sel.textContent = '';
        sel.append(new Option('Choose a bank…', ''), new Option((ctx.appShort ?? '') + ' starter bank', STARTER));
        for (const n of names) sel.append(new Option(String(n), String(n)));
        if ([...sel.options].some(o => o.value === keep)) sel.value = keep;
    }
    async function refreshTiles() {
        const got = await getJson('/api/memory');
        if (!got) { tiles.textContent = ''; note(tiles, 'Could not read the memories.'); return; }
        list = [...got].sort((a, b) => num(a.frequencyHz) - num(b.frequencyHz));
        tiles.textContent = '';
        if (!list.length) { note(tiles, 'No memories yet. SAVE puts the VFO here; EDIT… opens the editor.'); return; }
        for (const m of list) {
            const t = h('button', 'rp-wtile');
            t.type = 'button';
            t.dataset.memId = m.id;
            t.append(h('span', 't', m.label || ('Mem ' + m.id)), h('span', 'f', mhzText(m.frequencyHz)), h('span', 'm', m.mode ?? ''));
            t.title = 'Tune the selected receiver here';
            t.addEventListener('click', () => ctx.post(`/api/memory/${m.id}/recall`));
            tiles.append(t);
        }
        mark();
    }
    // The tile the selected receiver is sitting on.
    function mark() {
        const hz = ctx.freq(ctx.selected());
        for (const t of tiles.children) {
            const m = list.find(x => String(x.id) === t.dataset.memId);
            t.classList.toggle('on', !!m && num(m.frequencyHz) === hz);
        }
    }
    // Both bank keys take two presses: each replaces a whole list.
    function arm(which, btn, text, msg) {
        if (armed === which) return true;
        disarm();
        armed = which;
        btn.textContent = text;
        btn.classList.add('warn');
        bankMsg.textContent = msg;
        armTimer = setTimeout(disarm, 4000);
        return false;
    }
    async function loadBank() {
        const name = sel.value;
        if (!name) { bankMsg.textContent = 'Choose a bank first.'; return; }
        if (!arm('load', loadBtn, 'SURE? LOAD', 'Press again to replace your memories with this bank.')) return;
        disarm();
        const res = name === STARTER
            ? await ctx.post('/api/memory/starter-bank/load', { mode: 'replace' })
            : await ctx.post(`/api/memorybank/${encodeURIComponent(name)}/load`);
        bankMsg.textContent = res?.ok ? 'Loaded.' : 'That bank did not load.';
        refreshTiles();
    }
    async function saveBank() {
        const name = sel.value;
        if (!name) { bankMsg.textContent = 'Choose a bank first.'; return; }
        if (name === STARTER) { bankMsg.textContent = "The starter bank is built in and can't be saved over. Use EDIT… to save as a new bank."; return; }
        if (!arm('save', saveBtn, 'SURE? SAVE', `Press again to write your memories over ${name}.`)) return;
        disarm();
        const res = await ctx.post('/api/memorybank/save', { name });
        bankMsg.textContent = res?.ok ? `Saved to ${name}.` : 'That bank was not saved.';
    }
    function disarm() {
        armed = null;
        clearTimeout(armTimer);
        loadBtn.textContent = 'LOAD';
        saveBtn.textContent = 'SAVE TO BANK';
        loadBtn.classList.remove('warn');
        saveBtn.classList.remove('warn');
        bankMsg.textContent = IDLE;
    }
    async function saveVfo(vfo) {
        const res = await ctx.post(`/api/memory/save-vfo/${vfo.toLowerCase()}`, { label: '' });
        saveMsg.textContent = res?.ok ? 'Saved ' + mhzText(ctx.freq(vfo)) + ' MHz.' : 'Not saved.';
        refreshTiles();
    }
    kit.shows['mem/memBank'] = shown => { if (shown) refreshBanks(); };
    kit.shows['mem/memTiles'] = shown => { if (shown) refreshTiles(); };
    return { mark };
}

// ---- DX watch list -------------------------------------------------------------
/** Panel dx/dxWatch: the cluster's watch list, one call or prefix per line. */
export function dxWatchWindow(kit) {
    const body = kit.windows.slot('dx', 'dxWatch');
    if (!body) return;
    const ta = h('textarea', 'rp-wselect');
    ta.rows = 8;
    ta.spellcheck = false;
    ta.style.flex = '1';
    ta.style.resize = 'none';
    ta.style.fontFamily = 'monospace';
    ta.placeholder = 'One call per line.\nVK2* for a prefix.';
    const msg = h('div', 'rp-wnote', 'A spot for any of these pops up an alert.');
    const b = h('div', 'rp-wbtns');
    b.append(key('SAVE', async () => {
        const patterns = ta.value.split('\n').map(s => s.trim()).filter(Boolean);
        try {
            const r = await fetch('/api/dxcluster/watched', {
                method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ patterns }),
            });
            const j = r.ok ? await r.json() : null;
            if (j) ta.value = (j.patterns ?? []).join('\n');
            msg.textContent = j ? 'Saved.' : 'Not saved.';
        } catch { msg.textContent = 'Not saved.'; }
    }));
    body.append(ta, b, msg);
    kit.shows['dx/dxWatch'] = async shown => {
        if (!shown) return;
        const j = await getJson('/api/dxcluster/watched');
        if (j && document.activeElement !== ta) ta.value = (j.patterns ?? []).join('\n');
    };
}

// ---- frequency entry -----------------------------------------------------------
/**
 * Panel kbd/kbdPad, opened by a VFO box's ENT chip.
 * @param {object} ctx  { post, freq(vfo), dual, maxHz, minHz? }
 * @returns {{ open(vfo), draw() }}
 */
export function frequencyEntryWindow(kit, ctx) {
    const { windows } = kit;
    const minHz = ctx.minHz ?? 30000;
    const body = windows.slot('kbd', 'kbdPad');
    let target = 'A', typed = '';
    const shown = h('div', 'rp-wentry', '');
    const err = h('div', 'rp-wnote', 'Type MHz with a point, or kHz without.');
    const pad = h('div', 'rp-wpad');
    for (const d of ['7', '8', '9', '4', '5', '6', '1', '2', '3', '.', '0', '⌫'])
        pad.append(key(d, () => press(d)));
    const ends = h('div', 'rp-wbtns');
    const clr = key('CLR', () => { typed = ''; draw(); });
    const go = key('ENTER', () => enter());
    go.classList.add('on');
    clr.style.flex = go.style.flex = '1';
    ends.append(clr, go);
    body.append(shown, pad, ends, err);
    function draw() {
        shown.textContent = typed || mhzText(ctx.freq(target));
        shown.style.opacity = typed ? '1' : '.55';
    }
    function press(d) {
        if (d === '⌫') typed = typed.slice(0, -1);
        else if (d === '.') { if (!typed.includes('.')) typed = (typed || '0') + '.'; }
        else if (typed.replace('.', '').length < 10) typed += d;
        err.textContent = 'Type MHz with a point, or kHz without.';
        draw();
    }
    // 14.074 is MHz; 14074 is kHz; a bare number up to the radio's top MHz
    // with no point is still MHz (7 -> 7 MHz), as on a radio's keypad.
    function toHz(t) {
        const v = Number(t);
        if (!t || !Number.isFinite(v) || v <= 0) return null;
        if (t.includes('.') || v <= ctx.maxHz / 1e6) return Math.round(v * 1e6);
        return Math.round(v * 1e3);
    }
    function enter() {
        const hz = toHz(typed);
        if (hz == null || hz < minHz || hz > ctx.maxHz) {
            err.textContent = `Between ${(minHz / 1e6).toFixed(3)} and ${(ctx.maxHz / 1e6).toFixed(3)} MHz.`;
            return;
        }
        ctx.post(`/api/cat/frequency/${target.toLowerCase()}`, { frequencyHz: hz });
        typed = '';
        draw();
        windows.close('kbd');
    }
    // Keys typed while the keypad is open go to it.
    document.addEventListener('keydown', e => {
        if (!windows.isOpen('kbd') || e.ctrlKey || e.altKey || e.metaKey) return;
        const t = e.target;
        if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT')) return;
        if (/^[0-9.]$/.test(e.key)) press(e.key);
        else if (e.key === 'Backspace') press('⌫');
        else if (e.key === 'Enter') enter();
        else if (e.key === 'Escape') windows.close('kbd');
        else return;
        e.preventDefault();
    });
    return {
        open(vfo) {
            target = vfo === 'B' ? 'B' : 'A';
            typed = '';
            windows.setSubtitle('kbd', ctx.dual ? (target === 'A' ? 'MAIN · VFO-A' : 'SUB · VFO-B') : 'VFO-' + target);
            draw();
            windows.open('kbd');
        },
        draw,
    };
}

/** The group's own page, /RadioWindow?g=: the windows API the builders expect. */
export function windowPageApi(pop, groupId) {
    const spare = {};
    return {
        slot: (gid, pid) => (gid === groupId ? pop.slot(pid) : (spare[gid + '/' + pid] ??= document.createElement('div'))),
        isOpen: gid => gid === groupId,
        isShown: gid => gid === groupId,
        open() {}, close() {}, closePanel() {}, showPanel() {},
        setSubtitle: (gid, text) => { if (gid === groupId) pop.setSubtitle(text); },
    };
}
