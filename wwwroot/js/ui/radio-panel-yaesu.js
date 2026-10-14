// radio-panel-yaesu.js - YWC's adapter for the shared radio panel
// (core/js/radio-panel/radio-panel.js, served at /js/radio-panel/).
//
// The panel is radio-agnostic; everything Yaesu lives here: which hub
// properties drive which setters, the SH-code-to-Hz width table, the IF OUT
// axis correction and the calibration tables.
//
// Stage 2: keys, bands, modes, step and the VFO keys write to the radio
// through the same /api/cat endpoints Classic uses, aimed at the SELECTED
// receiver (the radio's VS state - click a VFO to change it). Nothing here may
// transmit: TUNE (atu/tune keys the radio) and VOX (room noise can key it) are
// inert, display-only keys.
//
// No WSJT-X / JTAlert / Log4OM launcher links on this page, and no
// accessibility layer: both were Colin's call on 2026-10-13. Classic keeps them.

import { mountRadioPanel } from '/js/radio-panel/radio-panel.js';
import { YAESU_WINDOWS, startYaesuWindows } from '/js/ui/radio-panel-yaesu-windows.js';
import { alignBins, txMeterFraction } from '/js/radio-panel/panel-math.js';
import { calibrateNumeric, loadFromBackend } from '/js/calibration/calibration-engine.js';
import { axisOffsetHz } from '/js/sdr/if-out-offset.js';
import { ifWidthHzFor, ifWidthOptionsFor } from '/js/ui/if-width-tables.js';
import { RadioScopeControl } from '/js/ui/radio-scope.js';
import { RadioDisplayHotspots } from '/js/video/radio-display-hotspots.js';

const BANDS = ['160m', '80m', '60m', '40m', '30m', '20m', '17m', '15m', '12m', '10m', '6m'];
const MODES = ['LSB', 'USB', 'CW-U', 'CW-L', 'AM', 'FM', 'RTTY-L', 'DATA-U'];
// Classic's mode-to-MD-code table (site.js setMode), for the modes above.
const MODE_CAT = { 'LSB': '1', 'USB': '2', 'CW-U': '3', 'FM': '4', 'AM': '5', 'RTTY-L': '6', 'CW-L': '7', 'DATA-U': 'C' };
const AGC = { '0': 'OFF', '1': 'FAST', '2': 'MID', '3': 'SLOW', '4': 'AUTO', '5': 'AUTO', '6': 'AUTO' };
const IPO = { '0': 'IPO', '1': 'AMP1', '2': 'AMP2' };
// What a press moves to next: FAST > MID > SLOW > AUTO > OFF > FAST. OFF is
// in the cycle because the radio's own screen offers it; it pins the S-meter
// at zero, so the chip shows OFF while it is set.
const AGC_NEXT = { '1': '2', '2': '3', '3': '4', '4': '0', '5': '0', '6': '0', '0': '1' };
const IPO_NEXT = { '0': '1', '1': '2', '2': '0' };
const ATT_NEXT = { '00': '06', '06': '12', '12': '18', '18': '00' };
const STEPS_HZ = [10, 50, 100, 500, 1000, 5000, 10000];
const STEP_KEY = 'ywc.radioPanel.stepHz';
const SPOT_MAX_AGE_MS = 30 * 60 * 1000;

const on = v => v === true || v === 'true' || v === 1 || (typeof v === 'string' && v !== '' && v !== '0' && v !== '00' && v !== 'false');
const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };
const stepText = hz => (hz >= 1000 ? hz / 1000 + ' kHz' : hz + ' Hz');

function post(url, body) {
    const init = { method: 'POST' };
    if (body !== undefined) { init.headers = { 'Content-Type': 'application/json' }; init.body = JSON.stringify(body); }
    return fetch(url, init)
        .then(res => { if (!res.ok) console.warn('radio panel:', url, res.status); return res; })
        .catch(e => console.warn('radio panel:', url, e));
}

/**
 * @param {HTMLElement} root
 * @param {object} cfg  { model, dual, maxPowerW, views }
 */
export function startYaesuRadioPanel(root, cfg) {
    const model = cfg.model;
    const dual = !!cfg.dual;
    const maxPowerW = num(cfg.maxPowerW) || 100;

    let radioScreen = null;     // set just after mount; the panel may call onScreenView during it
    let onWindowPanel = () => {};   // likewise: windows restored from last time report during mount
    const panel = mountRadioPanel(root, {
        brandShort: 'YWC',
        brandName: 'Yaesu Web Control',
        model,
        dual,
        views: cfg.views ?? [],
        lamps: [{ id: 'cat', label: 'CAT' }, { id: 'sdr', label: 'SDR' }, { id: 'dx', label: 'DX' }],
        keys: [
            { id: 'ipo', label: 'IPO' }, { id: 'att', label: 'ATT' },
            { id: 'agc', label: 'AGC' }, { id: 'nb', label: 'NB' },
            { id: 'nr', label: 'DNR' }, { id: 'dnf', label: 'DNF' },
            { id: 'notch', label: 'NOTCH' }, { id: 'contour', label: 'CONTOUR' },
            { id: 'apf', label: 'APF' }, { id: 'proc', label: 'PROC' },
            { id: 'vox', label: 'VOX', inert: true }, { id: 'tune', label: 'TUNE', inert: true },
        ],
        vfoKeys: [
            { id: 'split', label: 'SPLIT' }, { id: 'rxclar', label: 'RX CLAR' }, { id: 'txclar', label: 'TX CLAR' },
        ],
        bands: BANDS.map(b => ({ id: b, label: b.replace('m', '') })),
        modes: MODES.map(m => ({ id: m, label: m })),
        knobs: [
            { id: 'af', label: 'AF Gain' }, { id: 'rf', label: 'RF Gain' }, { id: 'sql', label: 'Squelch' },
            { id: 'shift', label: 'IF Shift' }, { id: 'width', label: 'Width' }, { id: 'pitch', label: 'CW Pitch' },
            { id: 'speed', label: 'Key Speed' }, { id: 'power', label: 'RF Power' }, { id: 'mic', label: 'Mic Gain' },
            { id: 'procl', label: 'Proc Level' }, { id: 'mon', label: 'Monitor' },
        ],
        screenViews: [{ id: 'tft', label: 'Radio screen' }],
        windows: YAESU_WINDOWS,
        onWindowPanel: (g, p, shown) => onWindowPanel(g, p, shown),
        onScreenView: id => { if (id === 'tft') radioScreen?.show(); },
        onAction: (kind, id, vfo) => act(kind, id, vfo),
        choices: (kind, vfo) => choices(kind, vfo),
        storagePrefix: 'ywc.radioPanel.',
        fontBase: '/lib/fonts',
    });
    radioScreen = startRadioScreen(panel.screenSlot('tft'), { model, dual, scopeCat: !!cfg.scopeCat });
    if (panel.screenView() === 'tft') radioScreen.show();
    onWindowPanel = startYaesuWindows(panel.windows);

    // ---- the slice of radio state the panel derives things from ----------
    const r = {
        freq: { A: 0, B: 0 }, mode: { A: '', B: '' }, widthCode: { A: null, B: null },
        shift: { A: 0, B: 0 }, pitchCode: 30, dataShiftHz: 1500, band: { A: '', B: '' },
        att: { A: '00', B: '00' }, ipo: { A: '0', B: '0' }, nb: { A: '0', B: '0' }, nr: { A: '0', B: '0' },
        dnf: { A: '0', B: '0' }, notch: { A: '0', B: '0' }, contour: { A: false, B: false }, apf: { A: false, B: false },
        agc: { A: '', B: '' }, agcCode: { A: '', B: '' }, rxClar: false, txClar: false, clarHz: { A: 0, B: 0 },
        contourHz: { A: 800, B: 800 }, apfHz: { A: 0, B: 0 },
        tx: false, txMeterRaw: {}, sel: 'A', split: 0, proc: false,
    };
    let stepHz = (() => { try { return num(localStorage.getItem(STEP_KEY)); } catch { return 0; } })();
    if (!STEPS_HZ.includes(stepHz)) stepHz = 100;
    panel.setStep(stepText(stepHz));
    panel.setSelected('A');
    const spots = new Map();       // callsign -> { call, hz, at }

    const pitchHz = () => 300 + num(r.pitchCode) * 10;
    const widthHz = vfo => (r.widthCode[vfo] == null ? null : ifWidthHzFor(model, r.mode[vfo], parseInt(r.widthCode[vfo], 10)));

    function refreshWidth(vfo) {
        const hz = widthHz(vfo);
        panel.setWidth(vfo, hz);
        if (vfo !== 'A') return;
        const opts = ifWidthOptionsFor(model, r.mode.A);
        const code = parseInt(r.widthCode.A, 10);
        if (!opts?.length || !Number.isFinite(code)) { panel.setKnob('width', null, '--'); return; }
        const i = Math.max(0, opts.findIndex(o => o.code === code || String(o.code) === String(code)));
        panel.setKnob('width', opts.length > 1 ? i / (opts.length - 1) : 0, hz ? (hz >= 1000 ? (hz / 1000).toFixed(hz % 100 ? 2 : 1) + 'k' : hz + '') : '--');
    }

    function refreshFlags(vfo) {
        const f = [];
        const flag = (id, label) => f.push(boxLive(vfo) ? { id, label } : label);
        if (r.ipo[vfo] && r.ipo[vfo] !== '0') flag('ipo', IPO[r.ipo[vfo]] ?? 'AMP');
        if (on(r.att[vfo])) flag('att', 'ATT ' + parseInt(r.att[vfo], 10));
        if (on(r.nb[vfo])) flag('nb', 'NB');
        if (on(r.nr[vfo])) flag('nr', 'DNR');
        if (on(r.dnf[vfo])) flag('dnf', 'DNF');
        if (on(r.notch[vfo])) flag('notch', 'NOTCH');
        panel.setFlags(vfo, f);
        if (vfo === r.sel) refreshKeys();
    }

    // The receiver keys show the selected receiver, because that is the one
    // a press changes.
    function refreshKeys() {
        const v = r.sel;
        panel.setKey('ipo', { on: r.ipo[v] !== '0', sub: IPO[r.ipo[v]] ?? '' });
        panel.setKey('att', { on: on(r.att[v]), sub: on(r.att[v]) ? parseInt(r.att[v], 10) + ' dB' : 'OFF' });
        panel.setKey('agc', { on: !!r.agc[v] && r.agc[v] !== 'OFF', sub: r.agc[v] });
        panel.setKey('nb', { on: on(r.nb[v]) });
        panel.setKey('nr', { on: on(r.nr[v]) });
        panel.setKey('dnf', { on: on(r.dnf[v]) });
        panel.setKey('notch', { on: on(r.notch[v]) });
        panel.setKey('contour', { on: !!r.contour[v] });
        panel.setKey('apf', { on: !!r.apf[v] });
    }

    // ---- writes ----------------------------------------------------------
    // Every receiver write names the selected receiver in lower case, as
    // Classic does; the server routes it (on single-receiver radios it goes
    // to the active VFO whatever the URL says).
    //
    // A chip in a VFO box names its own VFO (`vfo`). On a dual-receiver
    // radio that is written to directly, without changing the selection. A
    // single-receiver radio routes receiver writes to the active VFO, so
    // there only the selected box's chips take settings (boxLive).
    const boxLive = vfo => dual || vfo === r.sel;

    function choices(kind, vfo) {
        if (!boxLive(vfo)) return null;
        switch (kind) {
            case 'mode':
                return MODES.map(m => ({ id: m, label: m, on: m === r.mode[vfo] }));
            case 'width': {
                const cur = String(parseInt(r.widthCode[vfo], 10));
                return ifWidthOptionsFor(model, r.mode[vfo])?.map(o => ({ id: o.code, label: o.label, on: o.code === cur })) ?? null;
            }
            case 'agc':
                // OFF is offered here, as on the radio's own screen.
                return ['0', '1', '2', '3', '4'].map(c => ({ id: c, label: AGC[c], on: AGC[c] === r.agc[vfo] }));
        }
        return null;
    }

    function act(kind, id, vfo) {
        if (vfo && kind !== 'tune' && !boxLive(vfo)) return;
        const v = vfo || r.sel, rx = v.toLowerCase();
        const flip = x => (on(x) ? '0' : '1');
        switch (kind) {
            case 'width':
                return post(`/api/cat/ifwidth/${rx}`, { code: String(id) });
            case 'agc':
                return post(`/api/cat/agc/${rx}`, { code: String(id) });
            case 'tune':
                return wheelTune(v, num(id));
            case 'vfo':
                if (id !== r.sel) post(`/api/cat/active-vfo/${id}`);
                return;
            case 'band':
                return post(`/api/cat/band/${rx}`, { band: id });
            case 'mode':
                return MODE_CAT[id] ? post(`/api/cat/mode/${rx}`, { mode: MODE_CAT[id] }) : undefined;
            case 'step':
                if (id === 'cycle') {
                    stepHz = STEPS_HZ[(STEPS_HZ.indexOf(stepHz) + 1) % STEPS_HZ.length];
                    try { localStorage.setItem(STEP_KEY, String(stepHz)); } catch { /* private window */ }
                    panel.setStep(stepText(stepHz));
                    return;
                }
                return tuneBy(id === 'up' ? stepHz : -stepHz);
            case 'key':
                switch (id) {
                    case 'ipo': return post(`/api/cat/ipo/${rx}`, { code: IPO_NEXT[r.ipo[v]] ?? '0' });
                    case 'att': return post(`/api/cat/attenuator/${rx}`, { code: ATT_NEXT[r.att[v]] ?? '00' });
                    case 'agc': return post(`/api/cat/agc/${rx}`, { code: AGC_NEXT[r.agcCode[v]] ?? '2' });
                    case 'nb': return post(`/api/cat/noiseblanker/${rx}`, { enabled: flip(r.nb[v]) });
                    case 'nr': return post(`/api/cat/nr/${rx}`, { code: flip(r.nr[v]) });
                    case 'dnf': return post(`/api/cat/autonotch/${rx}`, { code: flip(r.dnf[v]) });
                    case 'notch': return post(`/api/cat/manualnotch/${rx}`, { enabled: flip(r.notch[v]) });
                    case 'contour': return post(`/api/cat/contour/${rx}`, { on: !r.contour[v], freqHz: r.contourHz[v] || 800 });
                    case 'apf': return post(`/api/cat/apf/${rx}`, { on: !r.apf[v], freqHz: r.apfHz[v] });
                    case 'proc': return post('/api/cat/proc', { enabled: !r.proc });
                }
                return;    // vox and tune are inert
            case 'vfoKey':
                switch (id) {
                    case 'split': return post(`/api/cat/split/${r.split ? 0 : 1}`);
                    case 'rxclar': return post('/api/cat/clarifier', { vfo: v, rxOn: !r.rxClar, txOn: r.txClar, offsetHz: r.clarHz[v] });
                    case 'txclar': return post('/api/cat/clarifier', { vfo: v, rxOn: r.rxClar, txOn: !r.txClar, offsetHz: r.clarHz[v] });
                }
        }
    }

    function tuneBy(deltaHz) {
        const v = r.sel;
        const cur = r.freq[v];
        if (!cur) return;
        // Land on the step grid, as a radio's dial does.
        const next = deltaHz > 0 ? Math.floor(cur / stepHz) * stepHz + stepHz : Math.ceil(cur / stepHz) * stepHz - stepHz;
        r.freq[v] = next;
        panel.setFrequency(v, next);
        post(`/api/cat/frequency/${v.toLowerCase()}`, { frequencyHz: next });
    }

    // The wheel over a frequency digit. Notches that come while a write is in
    // flight are folded into the next write, so a fast spin lands where the
    // operator stopped; the radio's FA/FB echo is held off until it has.
    const wheel = { A: { to: null, busy: false, until: 0 }, B: { to: null, busy: false, until: 0 } };
    async function wheelTune(vfo, deltaHz) {
        const w = wheel[vfo];
        const from = w.to ?? r.freq[vfo];
        if (!from || !deltaHz) return;
        const to = from + deltaHz;
        if (to < 30000 || to > 75000000) return;
        w.to = to;
        w.until = Date.now() + 700;
        r.freq[vfo] = to;
        panel.setFrequency(vfo, to);
        if (w.busy) return;
        w.busy = true;
        try {
            let sent = null;
            while (w.to !== sent) {
                sent = w.to;
                await post(`/api/cat/frequency/${vfo.toLowerCase()}`, { frequencyHz: sent });
            }
        } finally {
            w.busy = false;
            setTimeout(() => { if (!w.busy && Date.now() >= w.until) w.to = null; }, 700);
        }
    }
    const wheelHolds = vfo => wheel[vfo].busy || Date.now() < wheel[vfo].until;

    function refreshClar() {
        const hz = r.clarHz[r.sel];
        const txt = (hz >= 0 ? '+' : '') + hz + ' Hz';
        panel.setClarifier(r.rxClar || r.txClar ? txt : 'OFF ' + txt, 0.5 + hz / 19980);
        panel.setKey('rxclar', { on: r.rxClar });
        panel.setKey('txclar', { on: r.txClar });
    }

    function refreshTxMeters() {
        const raw = r.txMeterRaw;
        const v = {
            PO: Math.min(maxPowerW, calibrateNumeric('PWR', num(raw.PO))),
            SWR: calibrateNumeric('SWR', num(raw.SWR)),
            ALC: num(raw.ALC),
            COMP: Math.max(0, Math.min(20, calibrateNumeric('Compression', num(raw.COMP)))),
            IDD: calibrateNumeric('IDD', num(raw.IDD)),
        };
        for (const [k, val] of Object.entries(v)) panel.setTxMeter(k, txMeterFraction(k, val));
    }

    // The axis correction, in the same convention as the Classic spectrum
    // panel: true RF of the frame's centre = dial + offset. Null (unmeasured
    // radio) is treated as no correction.
    function offsetFor(frame) {
        if (frame.rfOrdered) return num(frame.centreHz) - r.freq.A;
        const filter = {
            mode: r.mode.A,
            ifWidthHz: widthHz('A') ?? 3000,
            ifShiftHz: num(r.shift.A),
            cwPitchHz: pitchHz(),
            dataShiftHz: r.dataShiftHz,
        };
        try { return axisOffsetHz(model, 'A', frame.centreHz, filter) ?? 0; } catch { return 0; }
    }

    function readDataShift(mode) {
        if (mode !== 'DATA-L' && mode !== 'DATA-U') return;
        fetch('/api/cat/datashift')
            .then(x => (x.ok ? x.json() : null))
            .then(d => { const hz = Number(d?.dataShiftHz); if (Number.isFinite(hz)) r.dataShiftHz = hz; })
            .catch(() => { /* 1500 stays */ });
    }

    function addSpot(sp) {
        const call = sp?.callsign ?? sp?.Callsign;
        const hz = num(sp?.frequencyHz ?? sp?.FrequencyHz);
        if (!call || !hz) return;
        const at = Date.parse(sp.receivedUtc ?? sp.ReceivedUtc ?? '') || Date.now();
        spots.set(call, { call, hz, at });
        publishSpots();
    }
    function publishSpots() {
        const cutoff = Date.now() - SPOT_MAX_AGE_MS;
        for (const [k, sp] of spots) if (sp.at < cutoff) spots.delete(k);
        panel.setSpots([...spots.values()].sort((a, b) => b.at - a.at).slice(0, 40));
    }
    setInterval(publishSpots, 60_000);

    // ---- one hub, one router ---------------------------------------------
    const handlers = {
        IsConnected: v => panel.setLamp('cat', on(v) ? 'on' : 'bad'),
        FrequencyA: v => { if (wheelHolds('A')) return; r.freq.A = num(v); panel.setFrequency('A', r.freq.A); },
        FrequencyB: v => { if (wheelHolds('B')) return; r.freq.B = num(v); panel.setFrequency('B', r.freq.B); },
        BandA: v => { if (v !== r.band.A) { r.band.A = v; panel.clearSpectrum(); } panel.setBand(r.band[r.sel]); },
        BandB: v => { r.band.B = v; panel.setBand(r.band[r.sel]); },
        ModeA: v => { r.mode.A = String(v ?? ''); panel.setMode('A', r.mode.A); refreshWidth('A'); readDataShift(r.mode.A); },
        ModeB: v => { r.mode.B = String(v ?? ''); panel.setMode('B', r.mode.B); refreshWidth('B'); },
        IfWidthA: v => { r.widthCode.A = v; refreshWidth('A'); },
        IfWidthB: v => { r.widthCode.B = v; refreshWidth('B'); },
        IfShiftA: v => { r.shift.A = num(v); panel.setKnob('shift', 0.5 + r.shift.A / 2000, (r.shift.A > 0 ? '+' : '') + r.shift.A); },
        IfShiftB: v => { r.shift.B = num(v); },
        AgcA: v => { r.agcCode.A = String(v); r.agc.A = AGC[String(v)] ?? ''; panel.setAgc('A', r.agc.A); refreshFlags('A'); },
        AgcB: v => { r.agcCode.B = String(v); r.agc.B = AGC[String(v)] ?? ''; panel.setAgc('B', r.agc.B); refreshFlags('B'); },
        ActiveVfo: v => { r.sel = num(v) === 1 ? 'B' : 'A'; panel.setSelected(r.sel); panel.setBand(r.band[r.sel]); refreshKeys(); refreshClar(); if (!dual) { refreshFlags('A'); refreshFlags('B'); } },
        IpoA: v => { r.ipo.A = String(v); refreshFlags('A'); },
        IpoB: v => { r.ipo.B = String(v); refreshFlags('B'); },
        AttA: v => { r.att.A = String(v); refreshFlags('A'); },
        AttB: v => { r.att.B = String(v); refreshFlags('B'); },
        NbA: v => { r.nb.A = String(v); refreshFlags('A'); },
        NbB: v => { r.nb.B = String(v); refreshFlags('B'); },
        NrA: v => { r.nr.A = String(v); refreshFlags('A'); },
        NrB: v => { r.nr.B = String(v); refreshFlags('B'); },
        AutoNotchA: v => { r.dnf.A = String(v); refreshFlags('A'); },
        AutoNotchB: v => { r.dnf.B = String(v); refreshFlags('B'); },
        ManualNotchA: v => { r.notch.A = String(v); refreshFlags('A'); },
        ManualNotchB: v => { r.notch.B = String(v); refreshFlags('B'); },
        ContourOnA: v => { r.contour.A = on(v); refreshFlags('A'); },
        ContourOnB: v => { r.contour.B = on(v); refreshFlags('B'); },
        ApfOnA: v => { r.apf.A = on(v); refreshFlags('A'); },
        ApfOnB: v => { r.apf.B = on(v); refreshFlags('B'); },
        ContourFreqA: v => { r.contourHz.A = num(v); },
        ContourFreqB: v => { r.contourHz.B = num(v); },
        ApfFreqA: v => { r.apfHz.A = num(v); },
        ApfFreqB: v => { r.apfHz.B = num(v); },
        SplitMode: v => { r.split = num(v); panel.setSplit(r.split !== 0); panel.setKey('split', { on: r.split !== 0 }); },
        RxClarOn: v => { r.rxClar = on(v); refreshClar(); },
        TxClarOn: v => { r.txClar = on(v); refreshClar(); },
        ClarifierOffsetA: v => { r.clarHz.A = num(v); refreshClar(); },
        ClarifierOffsetB: v => { r.clarHz.B = num(v); refreshClar(); },
        IsTransmitting: v => { r.tx = on(v); panel.setTx(r.tx); },
        AtuTuning: v => panel.setKey('tune', { tx: on(v) }),
        ProcEnabled: v => { r.proc = on(v); panel.setKey('proc', { on: r.proc }); },
        VoxOn: v => panel.setKey('vox', { on: on(v) }),
        AfGainA: v => panel.setKnob('af', num(v) / 255, String(num(v))),
        RfGainA: v => panel.setKnob('rf', num(v) / 255, String(num(v))),
        SquelchA: v => panel.setKnob('sql', num(v) / 255, String(num(v))),
        CwPitch: v => { r.pitchCode = num(v); panel.setKnob('pitch', num(v) / 75, pitchHz() + ''); },
        CwSpeed: v => panel.setKnob('speed', (num(v) - 4) / 56, num(v) + ' wpm'),
        Power: v => panel.setKnob('power', num(v) / maxPowerW, num(v) + ' W'),
        MicGain: v => panel.setKnob('mic', num(v) / 100, String(num(v))),
        ProcLevel: v => panel.setKnob('procl', num(v) / 100, String(num(v))),
        MonitorLevelA: v => panel.setKnob('mon', num(v) / 100, String(num(v))),
        SMeterA: v => panel.setSMeter('A', calibrateNumeric('SMETER', num(v))),
        SMeterB: v => panel.setSMeter('B', calibrateNumeric('SMETER', num(v))),
        PowerMeter: v => { r.txMeterRaw.PO = typeof v === 'object' && v ? v.value : v; refreshTxMeters(); },
        SWRMeter: v => { r.txMeterRaw.SWR = v; refreshTxMeters(); },
        ALCMeter: v => { r.txMeterRaw.ALC = v; refreshTxMeters(); },
        CompressionMeter: v => { r.txMeterRaw.COMP = v; refreshTxMeters(); },
        IDDMeter: v => { r.txMeterRaw.IDD = v; refreshTxMeters(); },
        SdrStatus: v => {
            if (v?.sdrId !== 'A') return;
            const st = String(v.status ?? '');
            panel.setLamp('sdr', st === 'streaming' ? 'on' : st === 'connecting' ? 'warn' : st === 'unconfigured' ? 'off' : 'bad');
            const msg = { streaming: null, connecting: 'SDR connecting…', unconfigured: 'No SDR configured', nodll: 'SDR driver not found', disconnected: 'SDR disconnected' };
            panel.setSpectrumSource({ label: 'SDR A', status: st in msg ? msg[st] : st });
        },
        SpectrumUpdate: v => {
            if (v?.sdrId !== 'A' || !v.bins?.length) return;
            const span = num(v.spanHz);
            panel.setSpectrumSource({ label: 'SDR A', spanHz: span, status: null });
            let bins = v.rfOrdered ? v.bins : Array.prototype.slice.call(v.bins).reverse();
            if (r.freq.A > 0) bins = alignBins(bins, offsetFor(v), span);
            panel.pushSpectrum(bins);
        },
        DxSpot: addSpot,
        DxClusterStatus: v => {
            const st = String(v?.status ?? '').toLowerCase();
            panel.setLamp('dx', st === 'connected' ? 'on' : st === 'connecting' ? 'warn' : st === 'off' || !st ? 'off' : 'bad');
        },
    };

    loadFromBackend({ 'S-Meter': ['SMETER', 'SMETER_LABELS'] });
    fetch('/api/dxcluster/spots').then(x => (x.ok ? x.json() : [])).then(list => { for (const sp of list ?? []) addSpot(sp); }).catch(() => {});
    fetch('/api/dxcluster/status').then(x => (x.ok ? x.json() : null)).then(s => { if (s) handlers.DxClusterStatus(s); }).catch(() => {});

    // The page opens this and passes it in, so the page itself visibly holds
    // the host open (HostPresenceTests reads the .cshtml, not this file).
    const conn = cfg.conn ?? window.rwcHubConnection('/radioHub');
    conn.on('RadioStateUpdate', msg => {
        radioScreen?.onRadioState(msg);
        const fn = handlers[msg?.property];
        if (!fn) return;
        try { fn(msg.value); } catch (e) { console.warn('radio panel:', msg.property, e); }
    });
    conn.onreconnecting(() => panel.setLamp('cat', 'warn'));
    conn.onclose(() => panel.setLamp('cat', 'bad'));
    panel.setLamp('cat', 'warn');
    conn.start().catch(() => panel.setLamp('cat', 'bad'));

    return panel;
}

// ---- Radio screen: the radio's own TFT, through the HDMI capture device ----
//
// The same MJPEG stream the Radio Display panel shows (/api/video/*). The
// server opens the capture for its first viewer and closes it after its last,
// and some USB HDMI dongles crash natively if the capture is closed and
// reopened quickly - so once this page has started the stream it keeps it
// for as long as the page is open, even while the SDR view is showing.
//
// Clicks on the picture work as they do on the Radio Display page: the same
// RadioDisplayHotspots overlay (click-to-tune, the ATT / IPO / R.FIL / AGC
// readouts, the scope soft-keys, and the meter pop-up for choosing the TX
// meters). It needs the radio's scope span for click-to-tune, so a
// RadioScopeControl is kept here with no visible controls. It reads the
// radio when the Radio screen is shown, not at page load.
function startRadioScreen(slot, { model, dual, scopeCat } = {}) {
    if (!slot) return null;
    const img = document.createElement('img');
    img.alt = '';
    img.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;object-fit:fill;display:none';
    const msg = document.createElement('div');
    msg.className = 'rp-spec-empty';
    msg.style.cssText = 'flex-direction:column;gap:14px;text-align:center;padding:0 30px';
    const msgText = document.createElement('div');
    const startBtn = document.createElement('button');
    startBtn.type = 'button';
    startBtn.className = 'rp-viewtab on';
    startBtn.textContent = 'Start';
    startBtn.style.cssText = 'position:static;font-size:16px;padding:6px 18px';
    startBtn.style.display = 'none';
    msg.append(msgText, startBtn);
    slot.append(img, msg);

    let streaming = false, retryMs = 2000, retryTimer = null;
    const say = (text, offerStart = false) => {
        msgText.textContent = text;
        startBtn.style.display = offerStart ? '' : 'none';
        msg.style.display = text ? 'flex' : 'none';
    };

    async function attach() {
        clearTimeout(retryTimer); retryTimer = null;
        let st;
        try {
            const res = await fetch('/api/video/status');
            st = res.ok ? await res.json() : null;
        } catch { st = null; }
        if (!st) { say('Cannot reach Radio Display'); return retry(); }
        if (!st.enabled) return say('Radio Display is off. Turn it on in Settings, and choose the capture device on the Classic page.');
        if (!st.deviceKey) return say('No capture device chosen. Choose it in the Radio Display panel on the Classic page.');
        if (st.halted) return say(st.error || 'The capture device disconnected.', true);
        say('Connecting…');
        streaming = true;
        img.src = '/api/video/stream?t=' + Date.now();
    }
    function retry() {
        if (retryTimer) return;
        retryTimer = setTimeout(attach, retryMs);
        retryMs = Math.min(retryMs * 1.5, 15000);
    }
    img.onload = () => { retryMs = 2000; img.style.display = ''; say(''); };
    img.onerror = () => {
        streaming = false;
        img.style.display = 'none';
        img.removeAttribute('src');
        say('Lost the picture - retrying');
        retry();
    };
    startBtn.addEventListener('click', async () => {
        try { await fetch('/api/video/start', { method: 'POST' }); } catch { /* attach() reports it */ }
        attach();
    });

    let scope = null;
    if (scopeCat) {
        const root = document.createElement('div');
        root.style.display = 'none';
        // Band buttons only so setActiveBand() follows MAIN/SUB on a dual
        // receiver; nothing ever clicks them.
        if (dual) root.innerHTML = '<button class="scope-band-btn" data-band="main"></button><button class="scope-band-btn" data-band="sub"></button>';
        slot.append(root);
        scope = new RadioScopeControl(root);
    }
    ensureHotspotStyle();
    const hotspots = new RadioDisplayHotspots(img, { debug: false, radioModel: model, dualReceiver: dual, getScopeControl: () => scope });

    return {
        show() {
            if (!streaming && !retryTimer) attach();
            // Read the span now rather than on the first hover, which would
            // otherwise say "scope span unknown" while the read is out.
            if (scope && !scope.state) scope.refresh();
        },
        onRadioState(msg) {
            if (!msg) return;
            if (msg.property === 'ActiveVfo') scope?.setActiveBand(msg.value);
            else if (msg.property === 'ScopeSetting') scope?.applyRemote(msg.value);
            hotspots.onRadioState(msg);
        },
    };
}

// The overlay's own layout rules live in site.css, which this page does not
// load (no Bootstrap, no site theme). The same rules, scoped to the panel.
function ensureHotspotStyle() {
    if (document.getElementById('rp-hotspot-style')) return;
    const st = document.createElement('style');
    st.id = 'rp-hotspot-style';
    st.textContent = `
.rp-altview .radio-display-hotspots{position:absolute;z-index:2;overflow:visible;user-select:none;-webkit-user-select:none;cursor:crosshair}
.rp-altview .radio-display-hotspots .rdh-debug{position:absolute;inset:0;pointer-events:none}
.rp-altview .radio-display-hotspots .rdh-cursor{position:absolute;top:0;bottom:0;width:0;border-left:1px dashed rgba(255,255,255,.7);pointer-events:none}
.rp-altview .radio-display-hotspots .rdh-label{position:absolute;top:4px;left:50%;transform:translateX(-50%);padding:1px 6px;border-radius:3px;background:rgba(0,0,0,.75);color:#fff;font:12px/1.4 monospace;width:max-content;max-width:calc(100% - 8px);box-sizing:border-box;text-align:center;pointer-events:none}
.rp-altview .radio-display-hotspots .rdh-label.rdh-label-flash{background:#b02a37}
`;
    document.head.appendChild(st);
}
