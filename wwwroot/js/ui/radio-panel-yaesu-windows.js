// radio-panel-yaesu-windows.js - which windows YWC's radio panel offers, and
// what goes in them.
//
// The window frame (launcher keys, drag, close, tear-off, remembered places)
// is shared: core/js/radio-panel/panel-windows.js. This file is the Yaesu
// side - the list of windows and their panels, and filling each panel's body.
//
// Positions are [left, top, width, height] in the panel's own 1600-wide board
// pixels, so they are the same at every zoom.
//
// The CW reader, the RTTY tuner/reader and the DX spot list are the pages the
// main page already pops out (/CwReader, /RttyTuner, /DxSpots), shown here in
// a frame. Those pages know when they are framed (core popout.js) and do not
// announce themselves as pop-outs. A framed page is unloaded when its panel
// is closed and loaded again when it is shown - including when it is torn off
// or joined back, because a browser reloads a frame that is moved. The
// decoders run in the server, so only the text on screen starts again.
//
// Nothing here transmits. Sending (CW, RTTY) and TX setup come later.

import { calibrateNumeric } from '/js/calibration/calibration-engine.js';
import { sMeterFraction, sMeterText, txMeterFraction } from '/js/radio-panel/panel-math.js';
import { SMeterHistoryPanel } from '/js/guages/smeter-history-panel.js';

const LATER = 'Comes in a later build.';

export const YAESU_WINDOWS = [
    { id: 'cw', key: 'CW', title: 'CW', pos: [330, 230, 980, 340], panels: [
        { id: 'keyer', title: 'KEYER', w: 230 },
        { id: 'cwReader', title: 'READER', dpos: [380, 560, 760, 320] },
        { id: 'cwSend', title: 'SEND', w: 230 },
    ] },
    { id: 'rtty', key: 'RTTY', title: 'RTTY', pos: [330, 380, 1040, 420], panels: [
        { id: 'rttyReader', title: 'TUNER · READER', dpos: [360, 380, 820, 420] },
        { id: 'rttySend', title: 'SEND', w: 220 },
    ] },
    { id: 'mem', key: 'MEMORIES', title: 'MEMORIES', pos: [560, 150, 940, 460], panels: [
        { id: 'memBank', title: 'BANK', w: 200 },
        { id: 'memTiles', title: 'CHANNELS' },
        { id: 'memRadio', title: 'SAVE', w: 170 },
    ] },
    { id: 'dx', key: 'DX', title: 'DX CLUSTER', pos: [700, 150, 860, 480], panels: [
        { id: 'dxSpots', title: 'SPOTS' },
        { id: 'dxWatch', title: 'WATCH LIST', w: 220 },
    ] },
    { id: 'meters', key: 'METERS', title: 'METERS', pos: [330, 380, 1000, 400], panels: [
        { id: 'mGauges', title: 'ALL METERS', w: 380 },
        { id: 'mHistory', title: 'S-METER HISTORY' },
    ] },
    { id: 'audio', key: 'AUDIO', title: 'REMOTE AUDIO', pos: [330, 150, 800, 300], panels: [
        { id: 'aLink', title: 'LINK', w: 240 },
        { id: 'aRx', title: 'RECEIVE' },
        { id: 'aTx', title: 'TRANSMIT' },
    ] },
    { id: 'scope', key: 'SCOPE', title: 'SCOPE · SCREEN', pos: [320, 200, 980, 340], panels: [
        { id: 'scCat', title: "RADIO'S OWN SCOPE (CAT)" },
        { id: 'scScreen', title: 'RADIO SCREEN (VIDEO)' },
    ] },
    { id: 'rx', key: 'RX SETUP', title: 'RX SETUP', pos: [280, 200, 1100, 340], panels: [
        { id: 'rxFilter', title: 'FILTER' },
        { id: 'rxShift', title: 'SHIFT · NOTCH', w: 230 },
        { id: 'rxFm', title: 'FM · REPEATER', w: 220 },
    ] },
    { id: 'tx', key: 'TX SETUP', title: 'TX SETUP', pos: [340, 560, 920, 300], panels: [
        { id: 'txVox', title: 'VOX' },
        { id: 'txTimeout', title: 'TX TIMEOUT' },
        { id: 'txAtu', title: 'ATU', w: 230 },
    ] },
    // No launcher key: the ENT chip in a VFO box opens it.
    { id: 'kbd', title: 'FREQUENCY ENTRY', pos: [600, 200, 360, 470], panels: [
        { id: 'kbdPad', title: 'KEYPAD' },
    ] },
];

// The pop-out pages' own window controls mean nothing in a frame: the panel
// window has its own title, close and tear-off.
const EMBED_CSS = `
[id$="ReattachBtn"], .ywc-dialog-close, .popout-screen-offer,
#cwReaderTitle, #rttyTunerTitle, #dxSpotsTitle { display: none !important; }
html, body { background: #111 !important; }
`;

const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };
const on = v => v === true || v === 'true' || v === 1 || (typeof v === 'string' && v !== '' && v !== '0' && v !== 'false');

function h(tag, cls, text) {
    const el = document.createElement(tag);
    if (cls) el.className = cls;
    if (text != null) el.textContent = text;
    return el;
}
function key(label, onClick, cls = '') {
    const b = h('button', 'rp-wkey' + (cls ? ' ' + cls : ''), label);
    b.type = 'button';
    b.addEventListener('click', onClick);
    return b;
}
function note(el, text) { el.append(h('div', 'rp-wnote', text)); }

async function getJson(url) {
    try { const r = await fetch(url); return r.ok ? await r.json() : null; } catch { return null; }
}

function mhzText(hz) {
    const f = num(hz) / 1e6;
    return f.toFixed(f >= 10 ? 4 : 5).replace(/0+$/, '').replace(/\.$/, '.0');
}

/**
 * Fill the window panels. Each panel's body is handed out once and kept for
 * the page's life.
 * @param {object} windows  the panel's windows API (panel-windows.js)
 * @param {object} ctx      { model, dual, maxPowerW, maxHz, post(url, body), selected() -> 'A'|'B', freq(vfo) }
 * @returns {{ onShow(g, p, shown), onRadioState(msg), entry(vfo) }}
 */
export function startYaesuWindows(windows, ctx) {
    const slot = (g, p) => windows.slot(g, p);
    const shows = {};       // 'g/p' -> fn(shown)
    const state = {};       // hub property -> last value

    // ---- framed pages ----------------------------------------------------
    function framed(g, p, url, title) {
        const body = slot(g, p);
        if (!body) return;
        body.classList.add('flush');
        const f = h('iframe', 'rp-wframe');
        f.title = title;
        f.addEventListener('load', () => {
            try {
                const doc = f.contentDocument;
                if (!doc?.head || doc.getElementById('rp-embed-css')) return;
                const st = doc.createElement('style');
                st.id = 'rp-embed-css';
                st.textContent = EMBED_CSS;
                doc.head.append(st);
            } catch { /* not same-origin: leave it as it is */ }
        });
        body.append(f);
        // Loaded the first time the panel is shown, not with the page.
        shows[g + '/' + p] = shown => { if (shown && !f.getAttribute('src')) f.src = url; };
    }
    framed('cw', 'cwReader', '/CwReader', 'CW reader');
    framed('rtty', 'rttyReader', '/RttyTuner', 'RTTY tuner and reader');
    framed('dx', 'dxSpots', '/DxSpots', 'DX spots');
    for (const [g, p] of [['cw', 'cwSend'], ['rtty', 'rttySend']]) note(slot(g, p), 'Sending comes with the TX windows, in a later build.');

    // ---- CW keyer --------------------------------------------------------
    const keyer = (() => {
        const body = slot('cw', 'keyer');
        const row = (label, minus, plus) => {
            const val = h('span', 'rp-wval', '--');
            const r = h('div', 'rp-wrow');
            r.append(h('span', 'rp-wlabel', label), val);
            const b = h('div', 'rp-wbtns');
            b.append(key('−', minus), key('+', plus));
            body.append(r, b);
            return val;
        };
        const speed = row('SPEED', () => setSpeed(-1), () => setSpeed(1));
        const pitch = row('PITCH', () => setPitch(-1), () => setPitch(1));
        const bk = h('span', 'rp-wval', '--');
        const r = h('div', 'rp-wrow');
        r.append(h('span', 'rp-wlabel', 'BREAK-IN'), bk);
        body.append(r);
        function setSpeed(d) {
            const wpm = Math.max(4, Math.min(60, num(state.CwSpeed) + d));
            if (!state.CwSpeed || wpm === num(state.CwSpeed)) return;
            ctx.post('/api/cat/cw/speed', { speed: wpm });
        }
        function setPitch(d) {
            const code = Math.max(0, Math.min(75, num(state.CwPitch) + d));
            if (state.CwPitch == null || code === num(state.CwPitch)) return;
            ctx.post('/api/cat/cwpitch', { code });
        }
        return {
            CwSpeed: v => { speed.textContent = num(v) + ' wpm'; },
            CwPitch: v => { pitch.textContent = (300 + num(v) * 10) + ' Hz'; },
            CwBreakIn: v => { bk.textContent = { 0: 'OFF', 1: 'SEMI', 2: 'FULL' }[String(v)] ?? String(v); },
        };
    })();

    // ---- meters ----------------------------------------------------------
    const meters = (() => {
        const body = slot('meters', 'mGauges');
        const bars = {};
        const bar = (id, label, tx) => {
            const el = h('div', 'rp-wbar' + (tx ? ' tx' : ''));
            const track = h('div', 'track'), fill = h('div', 'fill'), txt = h('span', 'txt', '--');
            track.append(fill);
            el.append(h('span', 'lbl', label), track, txt);
            el.dataset.meter = id;
            body.append(el);
            bars[id] = { fill, txt };
        };
        bar('SA', ctx.dual ? 'S MAIN' : 'S');
        if (ctx.dual) bar('SB', 'S SUB');
        bar('PO', 'PO', true); bar('SWR', 'SWR', true); bar('ALC', 'ALC', true);
        bar('COMP', 'COMP', true); bar('IDD', 'IDD', true);
        bar('VDD', 'VDD'); bar('TEMP', 'TEMP');
        const set = (id, frac, text) => {
            const b = bars[id];
            if (!b) return;
            b.fill.style.width = (Math.max(0, Math.min(1, frac)) * 100).toFixed(1) + '%';
            b.txt.textContent = text;
        };
        // VDD is the PA supply: about 50 V on the FTdx101MP, 13.8 V elsewhere.
        const vddFull = ctx.model === 'FTdx101MP' ? 55 : 16;

        let history = null;
        const histBody = slot('meters', 'mHistory');
        const canvas = h('canvas', 'rp-wcanvas');
        canvas.id = 'rpWinSMeterHistory';
        histBody.append(canvas);
        const histNote = h('div', 'rp-wnote', '');
        histBody.append(histNote);
        shows['meters/mHistory'] = shown => {
            // The history panel finds its canvas by id, so it is made once the
            // canvas is in the document.
            if (shown && !history) history = new SMeterHistoryPanel(canvas.id, { windowMs: 60000 });
            histNote.textContent = 'Last 60 s, ' + (ctx.selected() === 'B' ? 'SUB' : 'MAIN') + ' receiver';
        };
        const sm = vfo => v => {
            const s = calibrateNumeric('SMETER', num(v));
            set('S' + vfo, sMeterFraction(s), sMeterText(s));
            if (vfo === ctx.selected()) history?.push(num(v));
        };
        const tx = (id, cal, fmt) => v => {
            const raw = num(typeof v === 'object' && v ? v.value : v);
            const val = cal(raw);
            set(id, txMeterFraction(id, val), fmt(val));
        };
        return {
            SMeterA: sm('A'),
            SMeterB: sm('B'),
            PowerMeter: tx('PO', r => Math.min(ctx.maxPowerW, calibrateNumeric('PWR', r)), v => Math.round(v) + ' W'),
            SWRMeter: tx('SWR', r => calibrateNumeric('SWR', r), v => (v > 0 ? v.toFixed(1) : '--')),
            ALCMeter: tx('ALC', r => r, v => Math.round(v / 2.55) + ' %'),
            CompressionMeter: tx('COMP', r => Math.max(0, Math.min(20, calibrateNumeric('Compression', r))), v => v.toFixed(0) + ' dB'),
            IDDMeter: tx('IDD', r => calibrateNumeric('IDD', r), v => v.toFixed(1) + ' A'),
            // Raw 0 is "no reading", never a real supply or temperature.
            VDDMeter: v => { if (num(v) > 0) { const x = calibrateNumeric('VPA', num(v)); set('VDD', x / vddFull, x.toFixed(1) + ' V'); } },
            Temperature: v => { if (num(v) > 0) { const x = calibrateNumeric('TPA', num(v)); set('TEMP', x / 100, Math.round(x) + ' °C'); } },
            ActiveVfo: () => shows['meters/mHistory'](windows.isShown?.('meters', 'mHistory') ?? false),
        };
    })();

    // ---- memories --------------------------------------------------------
    const memories = (() => {
        const STARTER = '__ywc_starter__';
        const bankBody = slot('mem', 'memBank');
        const sel = h('select', 'rp-wselect');
        const loadBtn = key('LOAD', () => loadBank());
        const bankMsg = h('div', 'rp-wnote', 'Loading a bank replaces the memories you have now.');
        const bb = h('div', 'rp-wbtns');
        bb.append(loadBtn);
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

        let list = [], armed = false, armTimer = null;

        async function refreshBanks() {
            const names = (await getJson('/api/memorybank')) ?? [];
            const keep = sel.value;
            sel.textContent = '';
            sel.append(new Option('Choose a bank…', ''), new Option('YWC starter bank', STARTER));
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
        async function loadBank() {
            const name = sel.value;
            if (!name) { bankMsg.textContent = 'Choose a bank first.'; return; }
            // Two presses: a bank replaces every memory there is now.
            if (!armed) {
                armed = true;
                loadBtn.textContent = 'SURE? LOAD';
                loadBtn.classList.add('warn');
                bankMsg.textContent = 'Press again to replace your memories with this bank.';
                clearTimeout(armTimer);
                armTimer = setTimeout(disarm, 4000);
                return;
            }
            disarm();
            const res = name === STARTER
                ? await ctx.post('/api/memory/starter-bank/load', { mode: 'replace' })
                : await ctx.post(`/api/memorybank/${encodeURIComponent(name)}/load`);
            bankMsg.textContent = res?.ok ? 'Loaded.' : 'That bank did not load.';
            refreshTiles();
        }
        function disarm() {
            armed = false;
            loadBtn.textContent = 'LOAD';
            loadBtn.classList.remove('warn');
            bankMsg.textContent = 'Loading a bank replaces the memories you have now.';
        }
        async function saveVfo(vfo) {
            const res = await ctx.post(`/api/memory/save-vfo/${vfo.toLowerCase()}`, { label: '' });
            saveMsg.textContent = res?.ok ? 'Saved ' + mhzText(ctx.freq(vfo)) + ' MHz.' : 'Not saved.';
            refreshTiles();
        }
        shows['mem/memBank'] = shown => { if (shown) refreshBanks(); };
        shows['mem/memTiles'] = shown => { if (shown) refreshTiles(); };
        return { mark };
    })();

    // ---- DX watch list ---------------------------------------------------
    (() => {
        const body = slot('dx', 'dxWatch');
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
        shows['dx/dxWatch'] = async shown => {
            if (!shown) return;
            const j = await getJson('/api/dxcluster/watched');
            if (j && document.activeElement !== ta) ta.value = (j.patterns ?? []).join('\n');
        };
    })();

    // ---- frequency entry -------------------------------------------------
    const entry = (() => {
        const body = slot('kbd', 'kbdPad');
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
        // 14.074 is MHz; 14074 is kHz; a bare number up to the radio's top
        // MHz with no point is still MHz (7 -> 7 MHz), as on the radio's keypad.
        function toHz(t) {
            const v = Number(t);
            if (!t || !Number.isFinite(v) || v <= 0) return null;
            if (t.includes('.') || v <= ctx.maxHz / 1e6) return Math.round(v * 1e6);
            return Math.round(v * 1e3);
        }
        function enter() {
            const hz = toHz(typed);
            if (hz == null || hz < 30000 || hz > ctx.maxHz) {
                err.textContent = `Between 0.030 and ${(ctx.maxHz / 1e6).toFixed(3)} MHz.`;
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
    })();

    // ---- placeholders for the windows still to come ----------------------
    for (const g of YAESU_WINDOWS)
        for (const p of g.panels) {
            const el = slot(g.id, p.id);
            if (el && !el.childElementCount) note(el, LATER);
        }

    // Windows left open last time were shown while the panel was mounting,
    // before any of the above existed.
    for (const g of YAESU_WINDOWS)
        for (const p of g.panels)
            if (windows.isShown(g.id, p.id)) shows[g.id + '/' + p.id]?.(true);

    const handlers = [keyer, meters];
    return {
        onShow(g, p, shown) { try { shows[g + '/' + p]?.(shown); } catch (e) { console.warn('radio panel window:', g, p, e); } },
        onRadioState(msg) {
            const prop = msg?.property;
            if (!prop) return;
            state[prop] = msg.value;
            for (const hset of handlers) {
                const fn = hset[prop];
                if (fn) try { fn(msg.value); } catch (e) { console.warn('radio panel window:', prop, e); }
            }
            if (prop === 'FrequencyA' || prop === 'FrequencyB' || prop === 'ActiveVfo') { memories.mark(); entry.draw(); }
        },
        entry: vfo => entry.open(vfo),
    };
}
