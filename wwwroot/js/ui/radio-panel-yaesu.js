// radio-panel-yaesu.js - YWC's adapter for the shared radio panel
// (core/js/radio-panel/radio-panel.js, served at /js/radio-panel/).
//
// The panel is radio-agnostic; everything Yaesu lives here: which hub
// properties drive which setters, the SH-code-to-Hz width table, the IF OUT
// axis correction and the calibration tables.
//
// Stage 1 is DISPLAY ONLY. No onAction is passed, so the panel disables every
// key, band and mode button and nothing on this page writes to the radio.
//
// No WSJT-X / JTAlert / Log4OM launcher links on this page, and no
// accessibility layer: both were Colin's call on 2026-10-13. Classic keeps them.

import { mountRadioPanel } from '/js/radio-panel/radio-panel.js';
import { alignBins, txMeterFraction } from '/js/radio-panel/panel-math.js';
import { calibrateNumeric, loadFromBackend } from '/js/calibration/calibration-engine.js';
import { axisOffsetHz } from '/js/sdr/if-out-offset.js';
import { ifWidthHzFor, ifWidthOptionsFor } from '/js/ui/if-width-tables.js';

const BANDS = ['160m', '80m', '60m', '40m', '30m', '20m', '17m', '15m', '12m', '10m', '6m'];
const MODES = ['LSB', 'USB', 'CW-U', 'CW-L', 'AM', 'FM', 'RTTY-L', 'DATA-U'];
const AGC = { '0': 'OFF', '1': 'FAST', '2': 'MID', '3': 'SLOW', '4': 'AUTO', '5': 'AUTO', '6': 'AUTO' };
const IPO = { '0': 'IPO', '1': 'AMP1', '2': 'AMP2' };
const SPOT_MAX_AGE_MS = 30 * 60 * 1000;

const on = v => v === true || v === 'true' || v === 1 || (typeof v === 'string' && v !== '' && v !== '0' && v !== '00' && v !== 'false');
const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };

/**
 * @param {HTMLElement} root
 * @param {object} cfg  { model, dual, maxPowerW, views }
 */
export function startYaesuRadioPanel(root, cfg) {
    const model = cfg.model;
    const dual = !!cfg.dual;
    const maxPowerW = num(cfg.maxPowerW) || 100;

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
            { id: 'vox', label: 'VOX' }, { id: 'tune', label: 'TUNE' },
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
        storagePrefix: 'ywc.radioPanel.',
        fontBase: '/lib/fonts',
    });

    // ---- the slice of radio state the panel derives things from ----------
    const r = {
        freq: { A: 0, B: 0 }, mode: { A: '', B: '' }, widthCode: { A: null, B: null },
        shift: { A: 0, B: 0 }, pitchCode: 30, dataShiftHz: 1500, band: '',
        att: { A: '00', B: '00' }, ipo: { A: '0', B: '0' }, nb: { A: '0', B: '0' }, nr: { A: '0', B: '0' },
        dnf: { A: '0', B: '0' }, notch: { A: '0', B: '0' }, contour: { A: false, B: false }, apf: { A: false, B: false },
        agc: { A: '', B: '' }, rxClar: false, txClar: false, clarHz: { A: 0, B: 0 },
        tx: false, txMeterRaw: {},
    };
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
        if (r.ipo[vfo] && r.ipo[vfo] !== '0') f.push(IPO[r.ipo[vfo]] ?? 'AMP');
        if (on(r.att[vfo])) f.push('ATT ' + parseInt(r.att[vfo], 10));
        if (on(r.nb[vfo])) f.push('NB');
        if (on(r.nr[vfo])) f.push('DNR');
        if (on(r.dnf[vfo])) f.push('DNF');
        if (on(r.notch[vfo])) f.push('NOTCH');
        panel.setFlags(vfo, f);
        if (vfo !== 'A') return;
        panel.setKey('ipo', { on: r.ipo.A !== '0', sub: IPO[r.ipo.A] ?? '' });
        panel.setKey('att', { on: on(r.att.A), sub: on(r.att.A) ? parseInt(r.att.A, 10) + ' dB' : 'OFF' });
        panel.setKey('nb', { on: on(r.nb.A) });
        panel.setKey('nr', { on: on(r.nr.A) });
        panel.setKey('dnf', { on: on(r.dnf.A) });
        panel.setKey('notch', { on: on(r.notch.A) });
        panel.setKey('contour', { on: !!r.contour.A });
        panel.setKey('apf', { on: !!r.apf.A });
    }

    function refreshClar() {
        const hz = r.clarHz.A;
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
        FrequencyA: v => { r.freq.A = num(v); panel.setFrequency('A', r.freq.A); },
        FrequencyB: v => { r.freq.B = num(v); panel.setFrequency('B', r.freq.B); },
        BandA: v => { if (v !== r.band) { r.band = v; panel.setBand(v); panel.clearSpectrum(); } },
        ModeA: v => { r.mode.A = String(v ?? ''); panel.setMode('A', r.mode.A); refreshWidth('A'); readDataShift(r.mode.A); },
        ModeB: v => { r.mode.B = String(v ?? ''); panel.setMode('B', r.mode.B); refreshWidth('B'); },
        IfWidthA: v => { r.widthCode.A = v; refreshWidth('A'); },
        IfWidthB: v => { r.widthCode.B = v; refreshWidth('B'); },
        IfShiftA: v => { r.shift.A = num(v); panel.setKnob('shift', 0.5 + r.shift.A / 2000, (r.shift.A > 0 ? '+' : '') + r.shift.A); },
        IfShiftB: v => { r.shift.B = num(v); },
        AgcA: v => { r.agc.A = AGC[String(v)] ?? ''; panel.setAgc('A', r.agc.A); panel.setKey('agc', { on: r.agc.A && r.agc.A !== 'OFF', sub: r.agc.A }); },
        AgcB: v => { r.agc.B = AGC[String(v)] ?? ''; panel.setAgc('B', r.agc.B); },
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
        ApfOnA: v => { r.apf.A = on(v); refreshFlags('A'); },
        SplitMode: v => { panel.setSplit(on(v)); panel.setKey('split', { on: on(v) }); },
        RxClarOn: v => { r.rxClar = on(v); refreshClar(); },
        TxClarOn: v => { r.txClar = on(v); refreshClar(); },
        ClarifierOffsetA: v => { r.clarHz.A = num(v); refreshClar(); },
        IsTransmitting: v => { r.tx = on(v); panel.setTx(r.tx); },
        AtuTuning: v => panel.setKey('tune', { tx: on(v) }),
        ProcEnabled: v => panel.setKey('proc', { on: on(v) }),
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

    const conn = window.rwcHubConnection('/radioHub');
    conn.on('RadioStateUpdate', msg => {
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
