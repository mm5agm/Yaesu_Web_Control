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
// The band keys land on the band's RTTY spot and put the receiver in
// RTTY-L: the band plan's RTTY segment where it has one, else RTTY_SPOTS
// below. RTTY is not added to the band plan itself, which every other GUI
// reads for its segment keys and its mode-on-tune. A band with no spot here
// (60 m, 4 m, 2 m, 70 cm) goes to the radio's own band memory, as the band
// keys on the other GUIs do, and then to RTTY-L.

import { mountContestView } from '/js/rtty-contest/contest-view.js';
import { spanText, sMeterFraction, sMeterText, txMeterFraction } from '/js/radio-panel/panel-math.js';
import { dialStepper } from '/js/radio-panel/dial-stepper.js';
import { calibrateNumeric, loadFromBackend } from '/js/calibration/calibration-engine.js';
import { loadRttySettings } from '/js/rtty/rtty-settings.js';
import { post, startYaesuSpectrumFeed } from '/js/ui/spectrum-feed-yaesu.js';
import { MODE_CAT } from '/js/ui/spectrum-tune-yaesu.js';
import { BAND_EDGES, getSegments, loadBandPlanFromServer } from '/js/ui/band-plan.js';

const SPANS_HZ = [1e3, 2e3, 5e3, 10e3, 20e3, 50e3, 100e3, 200e3, 500e3, 1e6, 2e6];

// The bands, as the band keys on the radio panel and Classic have them.
const HF_BANDS = ['160m', '80m', '60m', '40m', '30m', '20m', '17m', '15m', '12m', '10m', '6m'];
const bandsFor = model => model === 'FT-991A' ? [...HF_BANDS, '2m', '70cm'] : [...HF_BANDS, '4m'];

// Where RTTY is found on bands whose band plan has no RTTY segment.
const RTTY_SPOTS = {
    '160m': 1_838_000, '80m': 3_580_000, '40m': 7_040_000, '30m': 10_140_000, '20m': 14_080_000,
    '17m': 18_105_000, '15m': 21_080_000, '12m': 24_925_000, '10m': 28_080_000, '6m': 50_300_000,
};

const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };
const on = v => v === true || v === 'true' || v === 1 || (typeof v === 'string' && v !== '' && v !== '0' && v !== '00' && v !== 'false');

/**
 * @param {HTMLElement} root
 * @param {object} cfg  { conn, model, dual, sdrB, maxPowerW, minHz, maxHz, bandPlan, views, tuner }
 *        sdrB: a second SDR is configured, so Main/Sub can be switched
 */
export function startYaesuRttyContest(root, cfg) {
    const dual = !!cfg.dual;
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
        meters: [{ id: 'S', label: 'S' }, { id: 'PO', label: 'PO' }, { id: 'SWR', label: 'SWR' }, { id: 'ALC', label: 'ALC' }],
        views: cfg.views ?? [],
        tuner: cfg.tuner,
        storagePrefix: 'ywc.rttyContest.',
        themePrefix: 'ywc.radioPanel.',
        fontBase: '/lib/fonts',
        minHz: cfg.minHz,
        maxHz: cfg.maxHz,
        part: cfg.part,
        onDock: cfg.onDock,
        windowUrl: cfg.windowUrl,
        onTunerOut: cfg.onTunerOut,
        layoutsApi: cfg.layoutsApi,
        onDecode: cfg.onDecode,
    });

    const feed = startYaesuSpectrumFeed({
        conn: cfg.conn,
        model: cfg.model,
        pane: vfo => view.pane(vfo),
        onSpan: (vfo, span) => view.setSpanValue(vfo, span),
        holds: vfo => stepper.holds(vfo),
        vfos: receivers.map(r => r.id),
    });
    const stepper = dialStepper({
        // The page's own STEP, 1 Hz to 1 kHz, not the board's.
        stepHz: () => view.stepHz,
        freq: vfo => feed.state.freq[vfo],
        show: (vfo, hz) => { feed.showFrequency(vfo, hz); view.setFrequency(vfo, hz); },
        write: (vfo, hz) => post(`/api/cat/frequency/${vfo.toLowerCase()}`, { frequencyHz: hz }),
    });

    // A band key: to the band's RTTY spot, or the radio's band memory, then RTTY-L.
    async function toBand(vfo, band) {
        const rx = vfo.toLowerCase();
        const spot = getSegments(bandPlan, band)?.RTTY?.freq ?? RTTY_SPOTS[band];
        const res = spot
            ? await post(`/api/cat/frequency/${rx}`, { frequencyHz: spot })
            : await post(`/api/cat/band/${rx}`, { band });
        if (res?.ok) await post(`/api/cat/mode/${rx}`, { mode: MODE_CAT['RTTY-L'] });
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
        ModeA: v => view.setMode('A', String(v ?? '')),
        ModeB: v => view.setMode('B', String(v ?? '')),
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
