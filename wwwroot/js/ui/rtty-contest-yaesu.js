// rtty-contest-yaesu.js - YWC's feed for the RTTY Contest view
// (core/js/rtty-contest/contest-view.js), the page at /rtty-contest.
//
// The spectrum is the pop-out spectrum's (spectrum-feed-yaesu.js): SDR A
// follows MAIN, SDR B follows SUB. This adds the strip: frequency and mode
// of the receiver on screen, the tuner's Mark/Shift, the S meter, and PO,
// SWR and ALC while transmitting. A click tunes as on /RadioSpectrum, except
// that the band plan never changes the mode here (#169): the page sets
// ywcAutoModeChangeOnTune false before this loads.
//
// The band keys land on the band's RTTY spot and put the receiver in the
// operator's RTTY mode: the band plan's RTTY segment where it has one, else
// RTTY_SPOTS below. RTTY is not added to the band plan itself, which every
// other GUI reads for its segment keys and its mode-on-tune. A band with no
// spot here (60 m, 4 m, 2 m, 70 cm) goes to the radio's own band memory, as
// the band keys on the other GUIs do, and then to that mode.
//
// The RTTY mode is RTTY-L, RTTY-U, DATA-L or DATA-U, kept in Settings
// (RttyTunerMode) and picked on the strip's MODE list, which shows the mode
// of the receiver on screen. Bruce VK2RT runs AFSK in DATA-L (2026-10-26).
//
// Under the tuner, PWR, WIDTH and SHIFT, which Bruce named as the controls he
// reaches for most: power for the radio, width and shift for the receiver on
// screen. WIDTH offers the radio's widths for the mode, as the Radio panel's
// width chip does (if-width-tables.js).

import { mountContestView } from '/js/rtty-contest/contest-view.js';
import { spanText, sMeterFraction, sMeterText, txMeterFraction } from '/js/radio-panel/panel-math.js';
import { dialStepper } from '/js/radio-panel/dial-stepper.js';
import { calibrateNumeric, loadFromBackend } from '/js/calibration/calibration-engine.js';
import { loadRttySettings } from '/js/rtty/rtty-settings.js';
import { post, startYaesuSpectrumFeed } from '/js/ui/spectrum-feed-yaesu.js';
import { BAND_EDGES, getSegments, loadBandPlanFromServer } from '/js/ui/band-plan.js';
import { ifWidthOptionsFor } from '/js/ui/if-width-tables.js';

// The SDR's spans (SpectrumSpanPlan.ValidSpans): the radio's own, with 30 and
// 40 kHz between 20 and 50 for Bruce VK2RT (Discussion #257).
const SPANS_HZ = [1e3, 2e3, 5e3, 10e3, 20e3, 30e3, 40e3, 50e3, 100e3, 200e3, 500e3, 1e6, 2e6];

// The bands, as the band keys on the radio panel and Classic have them.
const HF_BANDS = ['160m', '80m', '60m', '40m', '30m', '20m', '17m', '15m', '12m', '10m', '6m'];
const bandsFor = model => model === 'FT-991A' ? [...HF_BANDS, '2m', '70cm'] : [...HF_BANDS, '4m'];

// Where RTTY is found on bands whose band plan has no RTTY segment.
const RTTY_SPOTS = {
    '160m': 1_838_000, '80m': 3_580_000, '40m': 7_040_000, '30m': 10_140_000, '20m': 14_080_000,
    '17m': 18_105_000, '15m': 21_080_000, '12m': 24_925_000, '10m': 28_080_000, '6m': 50_300_000,
};

// The modes MODE offers, and their MD digits (MODE_CAT's, which has them all).
const RTTY_MODES = { 'RTTY-L': '6', 'RTTY-U': '9', 'DATA-L': '8', 'DATA-U': 'C' };

const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };
const on = v => v === true || v === 'true' || v === 1 || (typeof v === 'string' && v !== '' && v !== '0' && v !== '00' && v !== 'false');

/**
 * @param {HTMLElement} root
 * @param {object} cfg  { conn, model, dual, sdrB, maxPowerW, minHz, maxHz, bandPlan, rttyMode, views, tuner }
 *        sdrB: a second SDR is configured, so Main/Sub can be switched
 *        rttyMode: the operator's RTTY mode, from Settings
 */
export function startYaesuRttyContest(root, cfg) {
    const dual = !!cfg.dual;
    let rttyMode = RTTY_MODES[cfg.rttyMode] ? cfg.rttyMode : 'RTTY-L';
    const maxPowerW = num(cfg.maxPowerW) || 100;
    const receivers = cfg.sdrB
        ? [{ id: 'A', tab: dual ? 'Main' : 'VFO-A', name: dual ? 'MAIN · SDR A' : 'VFO-A · SDR A' },
           { id: 'B', tab: dual ? 'Sub' : 'VFO-B', name: dual ? 'SUB · SDR B' : 'VFO-B · SDR B' }]
        : [{ id: 'A', tab: dual ? 'Main' : 'VFO-A', name: dual ? 'MAIN · SDR A' : 'VFO-A · SDR A' }];

    const bandPlan = cfg.bandPlan || 'Region1';
    const bands = bandsFor(cfg.model);
    const bandRows = dual ? [{ id: 'A', label: 'MAIN' }, { id: 'B', label: 'SUB' }] : [{ id: 'A', label: 'BAND' }];

    const view = mountContestView(root, {
        receivers,
        bands: bands.map(b => ({ id: b, label: b })),
        bandRows,
        onBand: (vfo, band) => toBand(vfo, band),
        onNudge: (vfo, hz) => stepper.nudge(vfo, hz),
        spans: SPANS_HZ.map(hz => ({ hz, label: spanText(hz) })),
        onSpan: (id, hz) => feed.setSpan(id, hz),
        onTune: (vfo, hz) => feed.tune(vfo, hz),
        tuneTo: (vfo, hz) => feed.tuneTo(vfo, hz),
        onStep: (vfo, dir) => stepper.turn(vfo, dir),
        onFrequencyEntry: (vfo, hz) => post(`/api/cat/frequency/${vfo.toLowerCase()}`, { frequencyHz: hz }),
        modes: Object.keys(RTTY_MODES).map(m => ({ id: m, label: m })),
        onMode: (vfo, mode) => chooseMode(vfo, mode),
        modeTitle: 'The mode of the receiver on screen. What you pick is also kept, in Settings, as the mode the band keys and the RTTY tuner use',
        controls: [
            { id: 'power', label: 'PWR', kind: 'range', min: 5, max: maxPowerW, step: 1, unit: ' W',
              title: 'RF power. Drag and let go, or turn the mouse wheel over it' },
            { id: 'width', label: 'WIDTH', kind: 'list', perReceiver: true,
              title: 'IF width of the receiver on screen' },
            { id: 'shift', label: 'SHIFT', kind: 'range', perReceiver: true, min: -1000, max: 1000, step: 20, reset: 0,
              format: v => `${v > 0 ? '+' : ''}${v} Hz`,
              title: 'IF shift of the receiver on screen. Double-click to centre it' },
        ],
        onControl: (id, value, vfo) => setControl(id, value, vfo),
        meters: [{ id: 'S', label: 'S' }, { id: 'PO', label: 'PO' }, { id: 'SWR', label: 'SWR' }, { id: 'ALC', label: 'ALC' }],
        views: cfg.views ?? [],
        menu: cfg.menu ?? [],
        tuner: cfg.tuner,
        decode: cfg.decode,
        storagePrefix: 'ywc.rttyContest.',
        themePrefix: 'ywc.radioPanel.',
        fontBase: '/lib/fonts',
        minHz: cfg.minHz,
        maxHz: cfg.maxHz,
        part: cfg.part,
        onDock: cfg.onDock,
        windowUrl: cfg.windowUrl,
        onTunerOut: cfg.onTunerOut,
        onDecodeOut: cfg.onDecodeOut,
        layoutsApi: cfg.layoutsApi,
        onDecode: cfg.onDecode,
    });

    const feed = startYaesuSpectrumFeed({
        conn: cfg.conn,
        model: cfg.model,
        pane: vfo => view.pane(vfo),
        holds: vfo => stepper.holds(vfo),
        vfos: receivers.map(r => r.id),
        // DATA-U here is RTTY: a click puts the tones in the passband.
        dataURtty: true,
    });
    const stepper = dialStepper({
        // The page's own STEP, 1 Hz to 1 kHz, not the board's.
        stepHz: () => view.stepHz,
        freq: vfo => feed.state.freq[vfo],
        show: (vfo, hz) => { feed.showFrequency(vfo, hz); view.setFrequency(vfo, hz); },
        write: (vfo, hz) => post(`/api/cat/frequency/${vfo.toLowerCase()}`, { frequencyHz: hz }),
    });

    // A band key: to the band's RTTY spot, or the radio's band memory, then the RTTY mode.
    async function toBand(vfo, band) {
        const rx = vfo.toLowerCase();
        const spot = getSegments(bandPlan, band)?.RTTY?.freq ?? RTTY_SPOTS[band];
        const res = spot
            ? await post(`/api/cat/frequency/${rx}`, { frequencyHz: spot })
            : await post(`/api/cat/band/${rx}`, { band });
        if (res?.ok) await post(`/api/cat/mode/${rx}`, { mode: RTTY_MODES[rttyMode] });
    }
    // MODE: kept first, so a running tuner holds the new mode rather than
    // putting the old one back, then written to the receiver on screen.
    async function chooseMode(vfo, mode) {
        if (!RTTY_MODES[mode]) return;
        rttyMode = mode;
        const res = await post('/api/rtty/mode', { mode });
        if (res?.ok) await post(`/api/cat/mode/${vfo.toLowerCase()}`, { mode: RTTY_MODES[mode] });
    }
    // PWR, WIDTH and SHIFT. Power has no receiver; the URL names the one on
    // screen, as the Radio panel's knob names the selected one.
    function setControl(id, value, vfo) {
        const rx = (vfo ?? view.receiver).toLowerCase();
        if (id === 'power') return post(`/api/cat/power/${rx}`, { Watts: Math.round(num(value)) });
        if (id === 'width') return post(`/api/cat/ifwidth/${rx}`, { code: String(value) });
        if (id === 'shift') return post(`/api/cat/ifshift/${rx}`, { shiftHz: Math.round(num(value)) });
    }
    // WIDTH's choices change with the mode: in AM or FM there are none.
    const widthOf = { A: null, B: null }, modeOf = { A: '', B: '' };
    function showWidth(vfo) {
        const opts = ifWidthOptionsFor(cfg.model, modeOf[vfo]) ?? [];
        const code = widthOf[vfo] == null ? null : String(parseInt(widthOf[vfo], 10));
        view.setControl('width', code, { vfo, options: opts.map(o => ({ id: o.code, label: o.label })) });
    }

    // Which band key is lit follows the frequency, however it got there.
    const bandAt = hz => (BAND_EDGES[bandPlan] ?? BAND_EDGES.Region1).find(e => hz >= e.lo && hz <= e.hi)?.name ?? null;
    const showBand = (vfo, hz) => { if (bandRows.some(r => r.id === vfo)) view.setBand(vfo, bandAt(hz)); };
    loadBandPlanFromServer();

    // The tuner keeps Mark and Shift in this browser's storage, which is
    // what a click reads too (spectrum-tune-yaesu.js). The tuner changes
    // them without an event this page can hear - Auto, or a read from the
    // radio - so the strip looks twice a second.
    let tones = '';
    function showTones() {
        const s = loadRttySettings();
        const t = `${s.markHz}/${s.shiftHz}`;
        if (t === tones) return;
        tones = t;
        view.setTones(num(s.markHz), num(s.shiftHz));
    }
    showTones();
    setInterval(showTones, 500);

    // The meters. S follows the receiver on screen; PO, SWR and ALC read
    // only while transmitting, and are averaged as on the radio panel.
    const tx = { on: false, po: [], swr: [], alc: 0 };
    const avg = a => (a.length ? a.reduce((s, x) => s + x, 0) / a.length : 0);
    const s = { A: 0, B: 0 };
    function drawS() {
        const v = tx.on ? 0 : s[view.receiver] ?? 0;
        view.setMeter('S', sMeterFraction(v), sMeterText(v));
    }
    function drawTx() {
        const po = tx.on ? Math.min(maxPowerW, calibrateNumeric('PWR', avg(tx.po))) : 0;
        const swr = tx.on && tx.swr.length ? calibrateNumeric('SWR', avg(tx.swr)) : 0;
        const alc = tx.on ? num(tx.alc) : 0;
        view.setMeter('PO', Math.min(1, po / maxPowerW), tx.on ? `${Math.round(po)} W` : '');
        view.setMeter('SWR', txMeterFraction('SWR', swr), swr > 0 ? swr.toFixed(1) : '', swr >= 3);
        view.setMeter('ALC', txMeterFraction('ALC', alc), '');
    }
    const keep = (a, raw, n) => { a.push(num(raw)); if (a.length > n) a.shift(); };

    const handlers = {
        FrequencyA: v => { showBand('A', num(v)); if (!stepper.holds('A')) view.setFrequency('A', num(v)); },
        FrequencyB: v => { showBand('B', num(v)); if (!stepper.holds('B')) view.setFrequency('B', num(v)); },
        ModeA: v => { modeOf.A = String(v ?? ''); view.setMode('A', modeOf.A); showWidth('A'); },
        ModeB: v => { modeOf.B = String(v ?? ''); view.setMode('B', modeOf.B); showWidth('B'); },
        IfWidthA: v => { widthOf.A = v; showWidth('A'); },
        IfWidthB: v => { widthOf.B = v; showWidth('B'); },
        IfShiftA: v => view.setControl('shift', num(v), { vfo: 'A' }),
        IfShiftB: v => view.setControl('shift', num(v), { vfo: 'B' }),
        Power: v => view.setControl('power', num(v)),
        SMeterA: v => { s.A = calibrateNumeric('SMETER', num(v)); drawS(); },
        SMeterB: v => { s.B = calibrateNumeric('SMETER', num(v)); drawS(); },
        IsTransmitting: v => {
            tx.on = on(v);
            if (!tx.on) { tx.po = []; tx.swr = []; tx.alc = 0; }
            view.setTx(tx.on);
            drawS();
            drawTx();
        },
        PowerMeter: v => { if (!tx.on) return; keep(tx.po, typeof v === 'object' && v ? v.value : v, 4); drawTx(); },
        // Under 1 W the SWR reading measures nothing (the #124 trap): leave it out.
        SWRMeter: v => { if (!tx.on || calibrateNumeric('PWR', avg(tx.po)) < 1 || !num(v)) return; keep(tx.swr, v, 3); drawTx(); },
        ALCMeter: v => { if (!tx.on) return; tx.alc = v; drawTx(); },
    };
    cfg.conn.on('RadioStateUpdate', msg => {
        const fn = handlers[msg?.property];
        if (fn) try { fn(msg.value); } catch (e) { console.warn('rtty contest:', msg.property, e); }
    });

    loadFromBackend({ 'S-Meter': ['SMETER', 'SMETER_LABELS'] });
    drawS();
    drawTx();
    cfg.conn.start().catch(e => console.warn('rtty contest: hub', e));
    return view;
}
