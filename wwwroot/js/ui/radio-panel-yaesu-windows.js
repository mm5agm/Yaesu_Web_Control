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
// Those four - reader, tuner, DX spots and CW send - also have a ⇱ key: the
// same page in a browser window of its own, which can go on another screen.
// It is the window Classic's Pop out opens (same name and channel), so there
// is only ever one. While it is open the panel leaves the board; its Reattach,
// or closing it, puts the panel back. In CW and RTTY, though, the ⇱ takes
// the whole window out - keyer, reader and send together, at /RadioWindow -
// because the reader on its own is not what an operator wants on the other
// screen.
//
// Remote Audio, the radio's own scope and the audio filter are framed pages
// too: /RemoteAudio, /RadioScope and /AudioFilter.
//
// CW send is the /CwSend page in a frame, the same page and queue as its
// pop-out. TX SETUP's VOX and ATU keys use the endpoints the main page's own
// keys use. The TX timeout is core's (tx-timeout.js), sharing the main page's
// limit; it only warns.
//
// What transmits: the Remote Audio page's TX key, CW send's Send and keyer
// memories, and the ATU's TUNE, which asks for a second press before it keys
// a carrier. Each says so in red.

import { calibrateNumeric } from '/js/calibration/calibration-engine.js';
import { sMeterFraction, sMeterText, txMeterFraction } from '/js/radio-panel/panel-math.js';
import { SMeterHistoryPanel } from '/js/guages/smeter-history-panel.js';
import { TxTimeout, stepTimeout, timeoutText } from '/js/radio-panel/tx-timeout.js';
import { PopoutHost, screenShare } from '/js/popout/popout.js';

const LATER = 'Comes in a later build.';

export const YAESU_WINDOWS = [
    { id: 'cw', key: 'CW', title: 'CW', tip: 'Open or close the CW window: reader, zero-in and keyboard send. Opening it switches the selected receiver to CW-U', pos: [330, 230, 980, 340], popout: { width: 1000, height: 400 }, panels: [
        { id: 'keyer', title: 'KEYER', w: 230 },
        { id: 'cwReader', title: 'READER', dpos: [380, 560, 760, 320],
          popout: { name: 'cw-reader', url: '/CwReader', size: () => screenShare(0.25, window.screen) } },
        { id: 'cwSend', title: 'SEND', w: 320, dpos: [420, 300, 520, 400],
          popout: { name: 'cw-send', url: '/CwSend', size: { width: 600, height: 340 } } },
    ] },
    { id: 'rtty', key: 'RTTY', title: 'RTTY', tip: 'Open or close the RTTY window: reader and tuner. Opening it switches the selected receiver to RTTY-L', pos: [330, 380, 1040, 420], popout: { width: 1060, height: 480 }, panels: [
        { id: 'rttyReader', title: 'TUNER · READER', dpos: [360, 380, 820, 420],
          popout: { name: 'rtty-tuner', url: '/RttyTuner', size: { width: 380, height: 520 } } },
        { id: 'rttySend', title: 'SEND', w: 220 },
    ] },
    { id: 'mem', key: 'MEMORIES', title: 'MEMORIES', tip: "Open or close YWC's memories and banks", pos: [560, 150, 940, 460], popout: { width: 960, height: 500 }, panels: [
        { id: 'memBank', title: 'BANK', w: 200 },
        { id: 'memTiles', title: 'CHANNELS' },
        { id: 'memRadio', title: 'SAVE', w: 170 },
    ] },
    { id: 'dx', key: 'DX', title: 'DX CLUSTER', tip: 'Open or close the DX cluster spots', pos: [700, 150, 860, 480], panels: [
        { id: 'dxSpots', title: 'SPOTS',
          popout: { name: 'dx-spots', url: '/DxSpots', size: { width: 760, height: 480 } } },
        { id: 'dxWatch', title: 'WATCH LIST', w: 220 },
    ] },
    { id: 'meters', key: 'METERS', title: 'METERS', tip: 'Open or close the full meter set', pos: [330, 380, 1000, 400], popout: { width: 1020, height: 440 }, panels: [
        { id: 'mGauges', title: 'ALL METERS', w: 380 },
        { id: 'mHistory', title: 'S-METER HISTORY' },
    ] },
    // One panel, so it cannot be torn off: moving a frame reloads it, and
    // reloading the Remote Audio page drops the audio.
    { id: 'audio', key: 'AUDIO', title: 'REMOTE AUDIO', tip: 'Open or close Remote Audio', pos: [330, 150, 760, 380], panels: [
        { id: 'aLink', title: 'RECEIVE · TRANSMIT' },
    ] },
    { id: 'scope', key: 'SCOPE', title: 'SCOPE · SCREEN', tip: "Open or close the radio's own scope controls", pos: [300, 150, 1000, 520], popout: { width: 1020, height: 560 }, panels: [
        { id: 'scCat', title: "RADIO'S OWN SCOPE (CAT)", dpos: [300, 150, 680, 520] },
        { id: 'scScreen', title: 'RADIO SCREEN (VIDEO)', w: 280 },
    ] },
    { id: 'rx', key: 'RX SETUP', title: 'RX SETUP', tip: 'Open or close the receive settings', pos: [160, 180, 1380, 470], popout: { width: 1400, height: 510 }, panels: [
        { id: 'rxFilter', title: 'AUDIO FILTER', dpos: [280, 200, 560, 360] },
        { id: 'rxShift', title: 'SHIFT · NOTCH', w: 240 },
        { id: 'rxLevels', title: 'LEVELS', w: 220 },
        { id: 'rxFm', title: 'FM · REPEATER', w: 290 },
    ] },
    { id: 'tx', key: 'TX SETUP', title: 'TX SETUP', tip: 'Open or close the transmit settings: VOX, TX timeout, ATU', pos: [340, 500, 920, 400], popout: { width: 940, height: 440 }, panels: [
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
[id^="audioFilterReattachBtn"], [id^="audioFilterPopoutBtn"], #remoteAudioCloseBtn,
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
 * @param {object} ctx      { model, dual, maxPowerW, maxHz, post(url, body), selected() -> 'A'|'B', freq(vfo), showScreen(),
 *                            onTxTimeout(state) - optional, the TX timeout's state on every change,
 *                            onAtuTune({ armed, tuning }) - optional, the ATU TUNE key's state on every change,
 *                            popouts - false in a window group's own page (/RadioWindow), which opens none }
 * @returns {{ onShow(g, p, shown), onRadioState(msg), entry(vfo), txTimeout, atuTune() }}
 */
export function startYaesuWindows(windows, ctx) {
    const slot = (g, p) => windows.slot(g, p);
    const shows = {};       // 'g/p' -> fn(shown)
    const state = {};       // hub property -> last value

    // ---- framed pages ----------------------------------------------------
    // url may be a function, read each time the panel is shown; frame.reload()
    // re-reads it for a page that follows the selected receiver.
    function framed(g, p, url, title, allow) {
        const body = slot(g, p);
        if (!body) return null;
        body.classList.add('flush');
        const f = h('iframe', 'rp-wframe');
        f.title = title;
        if (allow) f.allow = allow;
        const urlNow = () => (typeof url === 'function' ? url() : url);
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
        shows[g + '/' + p] = shown => { if (shown && !f.getAttribute('src')) f.src = urlNow(); };
        return {
            reload() {
                const u = urlNow();
                if (f.getAttribute('src') && f.getAttribute('src') !== u) f.src = u;
            },
        };
    }
    framed('cw', 'cwReader', '/CwReader', 'CW reader');
    framed('rtty', 'rttyReader', '/RttyTuner', 'RTTY tuner and reader');
    framed('dx', 'dxSpots', '/DxSpots', 'DX spots');
    // The page's own Start/Stop, levels and microphone. Closing the window
    // unloads it, which stops the audio, as closing its pop-out does.
    framed('audio', 'aLink', '/RemoteAudio', 'Remote audio', 'microphone; autoplay');
    framed('scope', 'scCat', '/RadioScope', "The radio's own scope");
    const rxName = v => (ctx.dual ? (v === 'B' ? 'SUB' : 'MAIN') : 'VFO-' + v);
    const filterFrame = framed('rx', 'rxFilter', () => '/AudioFilter?vfo=' + ctx.selected(), 'Audio filter');
    framed('cw', 'cwSend', '/CwSend', 'CW send');

    // ---- browser windows of their own (⇱) --------------------------------
    // While one is open its panel is off the board, so the page is not
    // running twice. A pop-out already open from Classic says so when this
    // page starts, and takes its panel off here too.
    // A group marked popout opens whole, at /RadioWindow, the same way.
    const popouts = {};
    for (const g of ctx.popouts === false ? [] : YAESU_WINDOWS) {
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
        // Classic's ZIN: the radio nudges the selected receiver so the CW
        // signal sits on the pitch. It tunes; it does not transmit.
        const zin = h('div', 'rp-wbtns');
        zin.append(key('ZERO IN', () => ctx.post(`/api/cat/cw/zin/${ctx.selected().toLowerCase()}`)));
        body.append(zin);
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

    // ---- RX setup: shift and notch, for the selected receiver --------------
    // Values change here as soon as a key is pressed, and are put right by the
    // radio's own value when it comes back over the hub.
    const shiftNotch = (() => {
        const body = slot('rx', 'rxShift');
        const v = () => ctx.selected();
        const val = { IfShiftA: 0, IfShiftB: 0, ManualNotchA: '0', ManualNotchB: '0', ManualNotchFreqA: 1500, ManualNotchFreqB: 1500 };
        const row = (label) => {
            const out = h('span', 'rp-wval', '--');
            const r = h('div', 'rp-wrow');
            r.append(h('span', 'rp-wlabel', label), out);
            body.append(r);
            return out;
        };
        const btns = list => {
            const b = h('div', 'rp-wbtns');
            for (const [label, fn] of list) b.append(key(label, fn));
            body.append(b);
            return b;
        };
        const shiftOut = row('IF SHIFT');
        btns([['−20', () => shift(-20)], ['ZERO', () => shift(null)], ['+20', () => shift(20)]]);
        const notchOut = row('MANUAL NOTCH');
        const notchKeys = btns([['ON', () => notch('1')], ['OFF', () => notch('0')]]);
        const freqOut = row('NOTCH AT');
        btns([['−100', () => nfreq(-100)], ['−10', () => nfreq(-10)], ['+10', () => nfreq(10)], ['+100', () => nfreq(100)]]);
        function shift(d) {
            const k = 'IfShift' + v();
            const hz = d == null ? 0 : Math.max(-1000, Math.min(1000, val[k] + d));
            if (d != null && hz === val[k]) return;
            val[k] = hz;
            ctx.post(`/api/cat/ifshift/${v().toLowerCase()}`, { shiftHz: hz });
            draw();
        }
        function notch(on) {
            val['ManualNotch' + v()] = on;
            ctx.post(`/api/cat/manualnotch/${v().toLowerCase()}`, { enabled: on });
            draw();
        }
        function nfreq(d) {
            const k = 'ManualNotchFreq' + v();
            const hz = Math.max(10, Math.min(3200, val[k] + d));
            if (hz === val[k]) return;
            val[k] = hz;
            ctx.post(`/api/cat/manualnotchfreq/${v().toLowerCase()}`, { frequencyHz: hz });
            draw();
        }
        function draw() {
            const s = val['IfShift' + v()];
            shiftOut.textContent = (s > 0 ? '+' : '') + s + ' Hz';
            const n = on(val['ManualNotch' + v()]);
            notchOut.textContent = n ? 'ON' : 'OFF';
            notchKeys.children[0].classList.toggle('on', n);
            notchKeys.children[1].classList.toggle('on', !n);
            freqOut.textContent = val['ManualNotchFreq' + v()] + ' Hz';
        }
        draw();
        const take = k => x => { val[k] = k.startsWith('ManualNotch') && !k.includes('Freq') ? String(x) : num(x); draw(); };
        const out = { draw };
        for (const k of Object.keys(val)) out[k] = take(k);
        return out;
    })();

    // ---- RX setup: squelch, NB and DNR levels, for the selected receiver ---
    // Classic's Squelch slider and NB / DNR level selects. Squelch is offered
    // in every mode, as the radio's SQL is; Classic shows it only in FM.
    const levels = (() => {
        const body = slot('rx', 'rxLevels');
        const v = () => ctx.selected();
        const val = { SquelchA: null, SquelchB: null, NbLevelA: null, NbLevelB: null, NrLevelA: null, NrLevelB: null };
        const row = label => {
            const out = h('span', 'rp-wval', '--');
            const r = h('div', 'rp-wrow');
            r.append(h('span', 'rp-wlabel', label), out);
            body.append(r);
            return out;
        };
        const btns = list => {
            const b = h('div', 'rp-wbtns');
            for (const [label, fn] of list) b.append(key(label, fn));
            body.append(b);
        };
        const nrName = ['FTdx10', 'FT-710'].includes(ctx.model) ? 'DNR No.' : 'DNR LEVEL';
        const sqlOut = row('SQUELCH');
        btns([['−10', () => step('Squelch', -10, 0, 255, 'squelch', 'value')], ['−1', () => step('Squelch', -1, 0, 255, 'squelch', 'value')],
            ['+1', () => step('Squelch', 1, 0, 255, 'squelch', 'value')], ['+10', () => step('Squelch', 10, 0, 255, 'squelch', 'value')]]);
        const nbOut = row('NB LEVEL');
        btns([['−1', () => step('NbLevel', -1, 1, 20, 'nblevel', 'level')], ['+1', () => step('NbLevel', 1, 1, 20, 'nblevel', 'level')]]);
        const nrOut = row(nrName);
        btns([['−1', () => step('NrLevel', -1, 1, 15, 'nrlevel', 'level')], ['+1', () => step('NrLevel', 1, 1, 15, 'nrlevel', 'level')]]);
        function step(name, d, lo, hi, path, field) {
            const k = name + v();
            if (val[k] == null) return;     // not read from the radio yet
            const to = Math.max(lo, Math.min(hi, val[k] + d));
            if (to === val[k]) return;
            val[k] = to;
            ctx.post(`/api/cat/${path}/${v().toLowerCase()}`, { [field]: to });
            draw();
        }
        const text = x => (x == null ? '--' : String(x));
        function draw() {
            sqlOut.textContent = text(val['Squelch' + v()]);
            nbOut.textContent = text(val['NbLevel' + v()]);
            nrOut.textContent = text(val['NrLevel' + v()]);
        }
        draw();
        const out = { draw };
        for (const k of Object.keys(val)) out[k] = x => { val[k] = num(x); draw(); };
        return out;
    })();

    // ---- RX setup: FM repeater (read only) -------------------------------
    const fm = (() => {
        const body = slot('rx', 'rxFm');
        const TONES = ['67.0', '69.3', '71.9', '74.4', '77.0', '79.7', '82.5', '85.4', '88.5', '91.5',
            '94.8', '97.4', '100.0', '103.5', '107.2', '110.9', '114.8', '118.8', '123.0', '127.3',
            '131.8', '136.5', '141.3', '146.2', '151.4', '156.7', '159.8', '162.2', '165.5', '167.9',
            '171.3', '173.8', '177.3', '179.9', '183.5', '186.2', '189.9', '192.8', '196.6', '199.5',
            '203.5', '206.5', '210.7', '218.1', '225.7', '229.1', '233.6', '241.8', '250.3', '254.1'];
        const row = label => {
            const out = h('span', 'rp-wval', '--');
            const r = h('div', 'rp-wrow');
            r.append(h('span', 'rp-wlabel', label), out);
            body.append(r);
            return out;
        };
        const btns = list => {
            const d = h('div', 'rp-wbtns');
            for (const [label, fn] of list) d.append(key(label, fn));
            body.append(d);
        };
        // Classic's FM section: edit the four, then Save sends them together
        // (RS, RO, CT, CN). They set the repeater shift and tone; none of them
        // keys the radio. Nothing is sent until all four have been read.
        const SHIFT = { 0: 'SIMPLEX', 1: '+', 2: '−', 3: 'SPLIT' };
        const CT = { '00': 'OFF', '01': 'ENC / DEC', '02': 'ENC' };
        const radio = { FmShiftDir: null, FmOffsetHz: null, CtcssMode: null, CtcssTone: null };
        let edit = null;    // the unsaved copy, or null when showing the radio
        const cur = () => edit ?? radio;
        const known = () => Object.values(radio).every(x => x != null);
        const change = (k, fn) => {
            if (!known()) return;
            edit ??= { ...radio };
            edit[k] = fn(edit[k]);
            draw();
        };
        const dir = row('SHIFT');
        btns([['SHIFT', () => change('FmShiftDir', x => String((num(x) + 1) % 4))]]);
        const off = row('OFFSET');
        const offStep = d => change('FmOffsetHz', x => Math.max(0, Math.min(999000, num(x) + d)));
        btns([['−100k', () => offStep(-100000)], ['−10k', () => offStep(-10000)], ['+10k', () => offStep(10000)], ['+100k', () => offStep(100000)]]);
        const mode = row('CTCSS');
        btns([['CTCSS', () => change('CtcssMode', x => ({ '00': '01', '01': '02', '02': '00' })[String(x)] ?? '00')]]);
        const tone = row('TONE');
        const toneStep = d => change('CtcssTone', x => String(Math.max(0, Math.min(49, parseInt(x, 10) + d))).padStart(3, '0'));
        btns([['−1', () => toneStep(-1)], ['+1', () => toneStep(1)]]);
        const save = key('SAVE', () => {
            if (!edit) return;
            const e = edit;
            edit = null;
            Object.assign(radio, e);
            ctx.post('/api/cat/fmrepeater', { shiftDir: String(e.FmShiftDir), offsetHz: num(e.FmOffsetHz), ctcssMode: String(e.CtcssMode), ctcssTone: String(e.CtcssTone) });
            draw();
        });
        const undo = key('CANCEL', () => { edit = null; draw(); });
        const sb = h('div', 'rp-wbtns');
        sb.append(save, undo);
        body.append(sb);
        note(body, 'MAIN only, as on the radio. SAVE sends all four.');
        function draw() {
            const c = cur();
            dir.textContent = c.FmShiftDir == null ? '--' : SHIFT[String(c.FmShiftDir)] ?? String(c.FmShiftDir);
            off.textContent = c.FmOffsetHz == null ? '--' : (num(c.FmOffsetHz) / 1000).toLocaleString() + ' kHz';
            mode.textContent = c.CtcssMode == null ? '--' : CT[String(c.CtcssMode)] ?? String(c.CtcssMode);
            tone.textContent = c.CtcssTone == null ? '--' : (TONES[parseInt(c.CtcssTone, 10)] ?? '--') + ' Hz';
            save.disabled = !edit;
            undo.disabled = !edit;
            save.classList.toggle('warn', !!edit);
        }
        draw();
        const out = {};
        for (const k of Object.keys(radio)) out[k] = x => { radio[k] = k === 'FmOffsetHz' ? num(x) : String(x); draw(); };
        return out;
    })();

    // ---- TX setup: VOX, TX timeout, ATU ----------------------------------
    // Each row's value changes as soon as a key is pressed and is put right
    // by the radio's own value when it comes back over the hub. Anti-VOX has
    // no CAT command: the main page keeps it in the app only, and so does this.
    const txRow = (body, label) => {
        const out = h('span', 'rp-wval', '--');
        const r = h('div', 'rp-wrow');
        r.append(h('span', 'rp-wlabel', label), out);
        body.append(r);
        return out;
    };
    const txKeys = (body, list) => {
        const b = h('div', 'rp-wbtns');
        for (const [label, fn, cls] of list) b.append(key(label, fn, cls));
        body.append(b);
        return b;
    };

    const vox = (() => {
        const body = slot('tx', 'txVox');
        const val = { VoxOn: null, VoxGain: null, VoxDelay: null, AntiVoxGain: null };
        const onOff = txKeys(body, [['VOX ON', () => setOn(true)], ['VOX OFF', () => setOn(false)]]);
        const gain = txRow(body, 'GAIN');
        txKeys(body, [['−5', () => step('VoxGain', -5)], ['+5', () => step('VoxGain', 5)]]);
        const delay = txRow(body, 'DELAY');
        txKeys(body, [['−100', () => step('VoxDelay', -100)], ['+100', () => step('VoxDelay', 100)]]);
        const anti = txRow(body, 'ANTI-VOX');
        txKeys(body, [['−5', () => step('AntiVoxGain', -5)], ['+5', () => step('AntiVoxGain', 5)]]);
        note(body, 'VOX keys the radio when it hears the microphone.');
        const LIMIT = { VoxGain: [0, 100], VoxDelay: [0, 2500], AntiVoxGain: [0, 100] };
        const URL = { VoxGain: ['/api/cat/vox/gain', 'gain'], VoxDelay: ['/api/cat/vox/delay', 'delay'], AntiVoxGain: ['/api/cat/vox/antivox', 'gain'] };
        function setOn(x) {
            val.VoxOn = x;
            ctx.post('/api/cat/vox', { on: x });
            draw();
        }
        function step(k, d) {
            if (val[k] == null) return;      // nothing from the radio yet
            const [lo, hi] = LIMIT[k];
            const n = Math.max(lo, Math.min(hi, num(val[k]) + d));
            if (n === num(val[k])) return;
            val[k] = n;
            ctx.post(URL[k][0], { [URL[k][1]]: n });
            draw();
        }
        function draw() {
            const o = val.VoxOn == null ? null : on(val.VoxOn);
            onOff.children[0].classList.toggle('on', o === true);
            onOff.children[1].classList.toggle('on', o === false);
            gain.textContent = val.VoxGain == null ? '--' : String(num(val.VoxGain));
            delay.textContent = val.VoxDelay == null ? '--' : num(val.VoxDelay) + ' ms';
            anti.textContent = val.AntiVoxGain == null ? '--' : String(num(val.AntiVoxGain));
        }
        draw();
        const out = {};
        for (const k of Object.keys(val)) out[k] = x => { val[k] = x; draw(); };
        return out;
    })();

    // The timer is core's and only warns; the banner across the board is the
    // panel's (ctx.onTxTimeout). The limit is shared with the main page.
    const txTimer = (() => {
        const body = slot('tx', 'txTimeout');
        const limitOut = txRow(body, 'LIMIT');
        txKeys(body, [['−', () => { tt.limit = stepTimeout(tt.limit, -1); }], ['+', () => { tt.limit = stepTimeout(tt.limit, 1); }]]);
        const nowOut = txRow(body, 'TX FOR');
        const dismissKey = txKeys(body, [['DISMISS', () => tt.dismiss()]]).children[0];
        note(body, 'A banner across the top warns when TX has been on longer than this. It does not unkey the radio.');
        const draw = s => {
            limitOut.textContent = timeoutText(s.limit);
            nowOut.textContent = s.tx ? timeoutText(s.txSeconds) : 'RX';
            nowOut.classList.toggle('tx', s.tripped);
            dismissKey.disabled = !s.tripped;
        };
        const tt = new TxTimeout({
            onChange: s => {
                draw(s);
                try { ctx.onTxTimeout?.(s); } catch (e) { console.warn('radio panel: tx timeout', e); }
            },
        });
        draw({ tx: false, tripped: false, txSeconds: 0, limit: tt.limit });
        return { tt, IsTransmitting: x => tt.setTransmitting(on(x)) };
    })();

    // TUNE keys a carrier: the first press arms it, a second within a few
    // seconds sends it. The radio says nothing over CAT while it tunes (AC's
    // P2 is fixed at 0), so, as on the main page, "tuning" is a fixed window
    // after which the settled ATU state is read back. Pressed again while
    // tuning, the same AC002 stops it.
    let atuTune = () => {};
    const atu = (() => {
        const body = slot('tx', 'txAtu');
        const ARM_MS = 4000, TUNE_MS = 8000;
        const onOff = txKeys(body, [['ATU ON', () => setAtu(true)], ['THRU', () => setAtu(false)]]);
        const tuneKey = txKeys(body, [['TUNE', () => tune(), 'tx']]).children[0];
        body.append(h('div', 'rp-wnote tx', 'TUNE transmits a carrier. Press it twice.'));
        let enabled = null, armed = false, armTimer = null, tuning = false, tuneTimer = null;
        function setAtu(x) {
            enabled = x;
            ctx.post('/api/cat/atu', { enabled: x });
            draw();
        }
        function disarm() { armed = false; clearTimeout(armTimer); draw(); }
        function tune() {
            if (tuning) {
                ctx.post('/api/cat/atu/tune');
                clearTimeout(tuneTimer);
                tuneTimer = setTimeout(settle, 500);
                return;
            }
            if (!armed) {
                armed = true;
                clearTimeout(armTimer);
                armTimer = setTimeout(disarm, ARM_MS);
                draw();
                return;
            }
            disarm();
            tuning = true;
            ctx.post('/api/cat/atu/tune');
            tuneTimer = setTimeout(settle, TUNE_MS);
            draw();
        }
        async function settle() {
            tuning = false;
            const d = await getJson('/api/cat/atu');
            if (d && typeof d.atuEnabled === 'boolean') enabled = d.atuEnabled;
            draw();
        }
        function draw() {
            onOff.children[0].classList.toggle('on', enabled === true);
            onOff.children[1].classList.toggle('on', enabled === false);
            tuneKey.classList.toggle('armed', armed || tuning);
            tuneKey.textContent = tuning ? 'TUNING · STOP' : armed ? 'PRESS AGAIN' : 'TUNE';
            try { ctx.onAtuTune?.({ armed, tuning }); } catch (e) { console.warn('radio panel: atu tune', e); }
        }
        draw();
        atuTune = tune;
        return { AtuEnabled: x => { enabled = on(x); draw(); } };
    })();

    // ---- radio screen (video): status, and where to see it ----------------
    (() => {
        const body = slot('scope', 'scScreen');
        const row = label => {
            const out = h('span', 'rp-wval', '--');
            const r = h('div', 'rp-wrow');
            r.append(h('span', 'rp-wlabel', label), out);
            body.append(r);
            return out;
        };
        const st = row('STATUS'), size = row('SIZE'), fps = row('FRAME RATE'), q = row('QUALITY');
        const b = h('div', 'rp-wbtns');
        b.append(
            key('SHOW ON BOARD', () => ctx.showScreen?.()),
            key('POP OUT', () => window.open('/RadioDisplay', 'ywc-radio-display', 'popup,width=1000,height=640')));
        body.append(b);
        note(body, 'Capture device, frame rate and quality are set in Settings and on Radio Display.');
        let timer = 0;
        async function poll() {
            const j = await getJson('/api/video/status');
            if (!j) { st.textContent = '--'; return; }
            st.textContent = !j.enabled ? 'OFF IN SETTINGS' : j.halted ? 'DISCONNECTED' : String(j.status ?? '--').toUpperCase();
            size.textContent = j.width && j.height ? `${j.width} × ${j.height}` : '--';
            fps.textContent = j.fps ? `${j.fps} fps` : (j.targetFps ? `${j.targetFps} fps set` : '--');
            q.textContent = j.jpegQuality ? String(j.jpegQuality) : '--';
        }
        shows['scope/scScreen'] = shown => {
            clearInterval(timer);
            timer = 0;
            if (shown) { poll(); timer = setInterval(poll, 3000); }
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

    // RTTY send needs a transmit path this radio's CAT does not have: there is
    // no CAT text-to-RTTY command (KY is CW only), so it would mean generating
    // AFSK or keying FSK from the app. Left for Colin's decision.
    const rttySend = slot('rtty', 'rttySend');
    if (rttySend) note(rttySend, 'Not built. The radio has no CAT command that sends RTTY text, so sending needs AFSK audio or FSK keying from YWC. For now, send RTTY from your RTTY program.');

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

    // RX Setup is for the selected receiver: its title says which, and the
    // audio filter page is reloaded for the other one when it changes.
    const rxFollow = () => {
        windows.setSubtitle('rx', rxName(ctx.selected()));
        shiftNotch.draw();
        levels.draw();
        filterFrame?.reload();
    };
    rxFollow();

    const handlers = [keyer, meters, shiftNotch, levels, fm, vox, txTimer, atu];
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
            if (prop === 'ActiveVfo') rxFollow();
        },
        entry: vfo => entry.open(vfo),
        /** A panel's ⇱ key, or a group's (p null): a browser window of its own. */
        popout(g, p) {
            const host = (p && popouts[g + '/' + p]) || popouts[g];
            if (host && !host.open()) alert('The browser blocked the window. Allow pop-ups for this page and press the key again.');
        },
        txTimeout: txTimer.tt,
        /** The TX SETUP window's TUNE, for the panel's own TUNE key. */
        atuTune: () => atuTune(),
    };
}
