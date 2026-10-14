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
// The bottom row is one group per receiver (MAIN | SHARED · TX | SUB). On a
// dual-receiver radio each group's keys write to their own receiver whichever
// is selected; on a single-receiver radio there is one group and it follows
// the active VFO. The concentric knobs show values and turn (wheel or drag).
//
// No WSJT-X / JTAlert / Log4OM launcher links on this page, and no
// accessibility layer: both were Colin's call on 2026-10-13. Classic keeps them.
// The WSJT-X TX chip is status only: it lights while WSJT-X reports it is
// transmitting (polled from /api/wsjtx/status, which the hub does not carry).
//
// The TX timeout (TX SETUP window) puts the main page's banner and tone across
// the board. It warns; it never unkeys the radio.

import { mountRadioPanel } from '/js/radio-panel/radio-panel.js';
import { YAESU_WINDOWS, startYaesuWindows } from '/js/ui/radio-panel-yaesu-windows.js';
import { alignBins, txMeterFraction, rangeLegends } from '/js/radio-panel/panel-math.js';
import { BAND_EDGES, nearestBandForHz, loadBandPlanFromServer, autoModeForHz, getSegments, segmentForHz } from '/js/ui/band-plan.js';
import { loadRttySettings, afskMidpointAudioHz } from '/js/rtty/rtty-settings.js';
import { calibrateNumeric, loadFromBackend } from '/js/calibration/calibration-engine.js';
import { axisOffsetHz } from '/js/sdr/if-out-offset.js';
import { ifWidthHzFor, ifWidthOptionsFor } from '/js/ui/if-width-tables.js';
import { RadioScopeControl } from '/js/ui/radio-scope.js';
import { RadioDisplayHotspots } from '/js/video/radio-display-hotspots.js';
import { PopoutHost } from '/js/popout/popout.js';
import { txTimeoutBeep, txTimeoutWarning } from '/js/radio-panel/tx-timeout.js';

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
const WSJTX_POLL_MS = 3000;
const TX_BEEP_MS = 3000;

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
    // VDD and TEMP are read every two seconds in receive too (RM8 / RM9), on
    // the models whose Classic page shows them. The VDD dial is the model's
    // (40-55 V on the FTdx101MP, 10-16 V on the 13.8 V radios).
    const vddDial = cfg.vdd && num(cfg.vdd.max) > num(cfg.vdd.min) ? { min: num(cfg.vdd.min), max: num(cfg.vdd.max) } : null;
    const paTemp = !!cfg.paTemp && !!vddDial;
    // Classic's antenna and roofing selects, as VFO box chips: only on a
    // radio that has them, and only the roofing filters fitted.
    const antennas = Array.isArray(cfg.antennas) ? cfg.antennas.map(String) : [];
    const roofing = Array.isArray(cfg.roofing) ? cfg.roofing.filter(o => o?.code) : [];
    const roofLabel = code => roofing.find(o => o.code === String(code))?.label ?? '';

    let radioScreen = null;     // set just after mount; the panel may call onScreenView during it
    let win = null;             // likewise: windows restored from last time report during mount
    const panel = mountRadioPanel(root, {
        brandShort: 'YWC',
        brandName: 'Yaesu Web Control',
        model,
        dual,
        views: cfg.views ?? [],
        lamps: [{ id: 'cat', label: 'CAT' }, { id: 'sdr', label: 'SDR' }, { id: 'dx', label: 'DX' }],
        statusChips: [{ id: 'wsjtx', label: 'WSJT-X TX', title: 'Lit while WSJT-X is transmitting through YWC' }],
        keys: [
            { id: 'mainsub', label: dual ? 'MAIN / SUB' : 'VFO A / B' }, { id: 'proc', label: 'PROC' },
            { id: 'vox', label: 'VOX', inert: true }, { id: 'tune', label: 'TUNE', inert: true },
        ],
        rxGroups: {
            knobs: [
                { id: 'afrf', label: 'AF / RF·SQL', turn: true }, { id: 'shiftwidth', label: 'SHIFT / WIDTH', turn: true },
                { id: 'notchcont', label: 'NOTCH / CONT', turn: true },
            ],
            keys: [
                { id: 'ipo', label: 'IPO' }, { id: 'att', label: 'ATT' }, { id: 'agc', label: 'AGC' },
                { id: 'nb', label: 'NB' }, { id: 'nr', label: 'DNR' }, { id: 'dnf', label: 'DNF' },
                { id: 'notch', label: 'NOTCH' }, { id: 'contour', label: 'CONT' }, { id: 'apf', label: 'APF' },
            ],
        },
        sharedKnobs: [
            { id: 'micproc', label: 'MIC / PROC', turn: true }, { id: 'powermon', label: 'POWER / MONI', turn: true },
            { id: 'speedpitch', label: 'SPEED / PITCH', turn: true },
        ],
        // The dial: on the FTdx101 the outer ring tunes MAIN and the inner
        // knob SUB, as on the radio.
        dualDial: true,
        clarTurn: true,
        meters: ['PO', 'SWR', 'ALC', 'COMP', 'IDD', ...(vddDial ? ['VDD'] : []), ...(paTemp ? ['TEMP'] : [])],
        meterScales: vddDial ? { VDD: rangeLegends(vddDial.min, vddDial.max) } : {},
        liveMeters: ['VDD', 'TEMP'],
        vfoKeys: [
            { id: 'ab', label: 'A▸B' }, { id: 'ba', label: 'B▸A' }, { id: 'swap', label: 'A⇄B' },
            { id: 'split', label: 'SPLIT' }, { id: 'qsplit', label: 'Q.SPLIT' },
            // Classic's TX VFO select. It picks the VFO, it does not key the radio.
            { id: 'txa', label: 'TX A', title: 'Transmit on VFO A (selects it, does not transmit)' },
            { id: 'txb', label: 'TX B', title: 'Transmit on VFO B (selects it, does not transmit)' },
        ],
        vfoKeyColumns: 4,
        // M▸VFO and VFO▸M would read and overwrite the radio's own memory
        // channels; they stay display-only until that has been bench-checked.
        memKeys: [
            { id: 'vm', label: 'V/M' },
            { id: 'mtovfo', label: 'M▸VFO', inert: true, title: 'Not wired yet' },
            { id: 'vfotom', label: 'VFO▸M', inert: true, title: 'Not wired yet' },
            { id: 'qmbsto', label: 'QMB STO' }, { id: 'qmbrcl', label: 'QMB RCL' },
            { id: 'mstore', label: 'M.STORE', sub: 'app', title: "Save the selected receiver to YWC's memories" },
        ],
        // CLR zeroes the offset and leaves RX / TX clarifier as they are, as the
        // radio's CLEAR does.
        clarKeys: [{ id: 'rxclar', label: 'RX' }, { id: 'txclar', label: 'TX' }, { id: 'clrclar', label: 'CLR' }],
        bands: BANDS.map(b => ({ id: b, label: b.replace('m', '') })),
        modes: MODES.map(m => ({ id: m, label: m })),
        // One spectrum per SDR, as on Classic and the pop-out: SDR A follows
        // VFO-A (MAIN), SDR B follows VFO-B (SUB).
        spectrumPanes: dual
            ? [{ id: 'A', tab: 'Main', name: 'MAIN · SDR A' }, { id: 'B', tab: 'Sub', name: 'SUB · SDR B' }]
            : [{ id: 'A', tab: 'VFO-A', name: 'VFO-A · SDR A' }, { id: 'B', tab: 'VFO-B', name: 'VFO-B · SDR B' }],
        // The spans Classic offers (SpectrumSpanPlan.ValidSpans).
        spectrumSpans: [1e3, 2e3, 5e3, 10e3, 20e3, 50e3, 100e3, 200e3, 500e3, 1e6, 2e6].map(hz => ({ hz })),
        screenViews: [{ id: 'tft', label: 'Radio screen' }],
        windows: YAESU_WINDOWS,
        onWindowPanel: (g, p, shown) => win?.onShow(g, p, shown),
        frequencyEntry: true,
        vfoChips: ['seg', ...(antennas.length ? ['ant'] : []), ...(roofing.length ? ['roof'] : [])],
        onScreenView: id => { if (id === 'tft') radioScreen?.show(); },
        // The spectrum's own window, for a second screen: /RadioSpectrum.
        onSpectrumPopout: () => {
            if (!specHost.open()) alert('The browser blocked the spectrum window. Allow pop-ups for this page and press the key again.');
        },
        onSpectrumBack: () => specHost.recall(),
        onAction: (kind, id, vfo, dir) => act(kind, id, vfo, dir),
        choices: (kind, vfo) => choices(kind, vfo),
        storagePrefix: 'ywc.radioPanel.',
        fontBase: '/lib/fonts',
    });
    // While the spectrum is in its own window the board stops drawing it.
    let specAway = false;
    const specHost = new PopoutHost({
        name: 'radio-spectrum',
        url: '/RadioSpectrum',
        defaultSize: { width: 1100, height: 700 },
        onChange: open => { specAway = open; panel.setSpectrumAway(open); },
    }).start();
    radioScreen = startRadioScreen(panel.screenSlot('tft'), { model, dual, scopeCat: !!cfg.scopeCat });
    if (panel.screenView() === 'tft') radioScreen.show();

    // ---- the slice of radio state the panel derives things from ----------
    const r = {
        freq: { A: 0, B: 0 }, mode: { A: '', B: '' }, widthCode: { A: null, B: null },
        ant: { A: '', B: '' }, roof: { A: '', B: '' },
        shift: { A: 0, B: 0 }, pitchCode: 30, dataShiftHz: 1500, band: { A: '', B: '' },
        att: { A: '00', B: '00' }, ipo: { A: '0', B: '0' }, nb: { A: '0', B: '0' }, nr: { A: '0', B: '0' },
        dnf: { A: '0', B: '0' }, notch: { A: '0', B: '0' }, contour: { A: false, B: false }, apf: { A: false, B: false },
        agc: { A: '', B: '' }, agcCode: { A: '', B: '' }, rxClar: false, txClar: false, clarHz: { A: 0, B: 0 },
        contourHz: { A: 800, B: 800 }, apfHz: { A: 0, B: 0 }, notchHz: { A: null, B: null },
        af: { A: null, B: null }, rf: { A: null, B: null }, sql: { A: null, B: null },
        mic: null, procl: null, power: null, mon: null, speed: null, pitchKnown: false,
        tx: false, txMeterRaw: {}, sel: 'A', split: 0, txVfo: 0, proc: false,
    };
    let stepHz = (() => { try { return num(localStorage.getItem(STEP_KEY)); } catch { return 0; } })();
    if (!STEPS_HZ.includes(stepHz)) stepHz = 100;
    panel.setStep(stepText(stepHz));
    panel.setSelected('A');
    const spots = new Map();       // callsign -> { call, hz, at }

    win = startYaesuWindows(panel.windows, {
        model, dual, maxPowerW, maxHz: num(cfg.maxHz) || 75_000_000, post,
        selected: () => r.sel,
        freq: vfo => r.freq[vfo] ?? 0,
        showScreen: () => panel.setScreenView('tft'),
        onTxTimeout: s => txWarning(s),
    });

    // ---- TX timeout: the banner and a beep every few seconds while tripped --
    let beepTimer = null;
    function txWarning(s) {
        panel.setTxWarning(s.tripped ? txTimeoutWarning(s.limit) : null, () => win?.txTimeout.dismiss());
        if (s.tripped && beepTimer == null) {
            txTimeoutBeep();
            beepTimer = setInterval(txTimeoutBeep, TX_BEEP_MS);
        } else if (!s.tripped && beepTimer != null) {
            clearInterval(beepTimer);
            beepTimer = null;
        }
    }

    // ---- WSJT-X TX light ----------------------------------------------------
    async function pollWsjtx() {
        let d = null;
        try {
            const res = await fetch('/api/wsjtx/status', { cache: 'no-store' });
            if (res.ok) d = await res.json();
        } catch { /* app restarting: leave it dim */ }
        const title = !d ? 'WSJT-X status unavailable'
            : !d.running ? 'WSJT-X is not running'
            : d.transmitting ? `WSJT-X is transmitting${d.mode ? ' (' + d.mode + ')' : ''}`
            : `WSJT-X is running${d.mode ? ' (' + d.mode + ')' : ''}, not transmitting`;
        panel.setStatusChip('wsjtx', !!d?.transmitting, title);
    }
    pollWsjtx();
    setInterval(pollWsjtx, WSJTX_POLL_MS);

    const pitchHz = () => 300 + num(r.pitchCode) * 10;
    const widthHz = vfo => (r.widthCode[vfo] == null ? null : ifWidthHzFor(model, r.mode[vfo], parseInt(r.widthCode[vfo], 10)));

    // Classic's Segment select: the chip names the segment the VFO is in, and
    // picking one sets its mode and then tunes to it, as Classic does.
    function refreshSeg(vfo) {
        const segs = getSegments(cfg.bandPlan, r.band[vfo]);
        const hz = r.freq[vfo];
        if (!segs || !hz) return panel.setChip(vfo, 'seg', '');
        const k = segmentForHz(cfg.bandPlan, r.band[vfo], hz);
        panel.setChip(vfo, 'seg', k ? 'SEG ' + (segs[k]?.label ?? k) : 'SEGMENT');
    }
    async function segmentJump(vfo, key) {
        const sg = getSegments(cfg.bandPlan, r.band[vfo])?.[key];
        if (!sg) return;
        const rx = vfo.toLowerCase();
        if (MODE_CAT[sg.mode] && sg.mode !== r.mode[vfo]) await post(`/api/cat/mode/${rx}`, { mode: MODE_CAT[sg.mode] });
        return post(`/api/cat/frequency/${rx}`, { frequencyHz: sg.freq });
    }

    function setAnt(vfo, v) {
        r.ant[vfo] = String(v ?? '');
        if (antennas.length) panel.setChip(vfo, 'ant', antennas.includes(r.ant[vfo]) ? 'ANT ' + r.ant[vfo] : '');
    }
    function setRoof(vfo, v) {
        r.roof[vfo] = String(v ?? '');
        const label = roofLabel(r.roof[vfo]);
        if (roofing.length) panel.setChip(vfo, 'roof', label ? 'ROOF ' + label : '');
    }

    function refreshWidth(vfo) {
        panel.setWidth(vfo, widthHz(vfo));
        refreshRxKnobs(vfo);
    }

    // ---- the concentric knobs: values (turned in knobTurn) -----------------
    // Inner knob first in each label's order the radio's panel uses: AF inside
    // RF/SQL, SHIFT inside WIDTH, NOTCH inside CONTOUR.
    const val = x => (x == null ? '--' : String(x));
    const frac = (x, max) => (x == null ? null : Math.max(0, Math.min(1, x / max)));
    const signed = x => (x >= 0 ? '+' : '') + x;
    // On a single-receiver radio the one group (A) shows the active VFO.
    const groupFor = vfo => (dual ? vfo : vfo === r.sel ? 'A' : null);
    function refreshRxKnobs(vfo) {
        const g = groupFor(vfo);
        if (!g) return;
        const { af, rf, sql } = { af: r.af[vfo], rf: r.rf[vfo], sql: r.sql[vfo] };
        panel.setGroupKnob(g, 'afrf', {
            inner: frac(af, 255), outer: frac(rf, 255), text: val(af) + ' · ' + val(rf),
            title: `AF ${val(af)} · RF ${val(rf)} · SQL ${val(sql)}`,
        });
        const hz = widthHz(vfo);
        const opts = ifWidthOptionsFor(model, r.mode[vfo]);
        const code = parseInt(r.widthCode[vfo], 10);
        let wFrac = null;
        if (opts?.length && Number.isFinite(code)) {
            const i = Math.max(0, opts.findIndex(o => o.code === code || String(o.code) === String(code)));
            wFrac = opts.length > 1 ? i / (opts.length - 1) : 0;
        }
        const wTxt = hz ? (hz >= 1000 ? (hz / 1000).toFixed(hz % 100 ? 2 : 1) + 'k' : hz + '') : '--';
        panel.setGroupKnob(g, 'shiftwidth', {
            inner: 0.5 + r.shift[vfo] / 2400, outer: wFrac, text: signed(r.shift[vfo]) + ' · ' + wTxt,
            title: `IF shift ${signed(r.shift[vfo])} Hz · width ${wTxt}`,
        });
        const nOn = on(r.notch[vfo]), cOn = !!r.contour[vfo];
        panel.setGroupKnob(g, 'notchcont', {
            inner: frac(r.notchHz[vfo], 3200), outer: frac(r.contourHz[vfo], 3200),
            // The frequencies show whether or not the filter is on (the NOTCH
            // and CONTOUR keys light for that), so turning a knob always shows.
            text: val(r.notchHz[vfo]) + '·' + val(r.contourHz[vfo]),
            title: `Notch ${nOn ? 'on' : 'off'} ${val(r.notchHz[vfo])} Hz · contour ${cOn ? 'on' : 'off'} ${r.contourHz[vfo]} Hz`,
        });
    }
    function refreshSharedKnobs() {
        panel.setGroupKnob('shared', 'micproc', {
            inner: frac(r.mic, 100), outer: frac(r.procl, 100), text: val(r.mic) + ' · ' + val(r.procl),
            title: `Mic gain ${val(r.mic)} · processor level ${val(r.procl)}`,
        });
        panel.setGroupKnob('shared', 'powermon', {
            inner: frac(r.power, maxPowerW), outer: frac(r.mon, 100),
            text: (r.power == null ? '--' : r.power + ' W') + ' · ' + val(r.mon),
            title: `RF power ${r.power == null ? '--' : r.power + ' W'} · monitor ${val(r.mon)}`,
        });
        panel.setGroupKnob('shared', 'speedpitch', {
            inner: r.speed == null ? null : (r.speed - 4) / 56, outer: r.pitchKnown ? r.pitchCode / 75 : null,
            text: val(r.speed) + ' · ' + (r.pitchKnown ? pitchHz() : '--'),
            title: `Key speed ${val(r.speed)} wpm · CW pitch ${r.pitchKnown ? pitchHz() + ' Hz' : '--'}`,
        });
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
        refreshKeys();
        refreshRxKnobs(vfo);
    }

    // Each receiver group's keys show their own receiver. A single-receiver
    // radio has one group, showing the active VFO, which is what a press changes.
    // The VFO that transmits, worked out as Classic does: a single-receiver
    // radio transmits on the other VFO in split, a dual one says so with FT.
    function txVfoNow() {
        if (!dual) return r.split ? (r.sel === 'A' ? 'B' : 'A') : r.sel;
        return r.txVfo === 1 ? 'B' : 'A';
    }
    function refreshTxVfo() {
        const t = txVfoNow();
        panel.setKey('txa', { on: t === 'A' });
        panel.setKey('txb', { on: t === 'B' });
    }

    function refreshKeys() {
        for (const v of dual ? ['A', 'B'] : [r.sel]) {
            const g = groupFor(v);
            panel.setKey('ipo', { on: r.ipo[v] !== '0', sub: IPO[r.ipo[v]] ?? '' }, g);
            panel.setKey('att', { on: on(r.att[v]), sub: on(r.att[v]) ? parseInt(r.att[v], 10) + ' dB' : 'OFF' }, g);
            panel.setKey('agc', { on: !!r.agc[v] && r.agc[v] !== 'OFF', sub: r.agc[v] }, g);
            panel.setKey('nb', { on: on(r.nb[v]) }, g);
            panel.setKey('nr', { on: on(r.nr[v]) }, g);
            panel.setKey('dnf', { on: on(r.dnf[v]) }, g);
            panel.setKey('notch', { on: on(r.notch[v]) }, g);
            panel.setKey('contour', { on: !!r.contour[v] }, g);
            panel.setKey('apf', { on: !!r.apf[v] }, g);
        }
        panel.setKey('mainsub', { on: true, sub: dual ? (r.sel === 'A' ? 'MAIN' : 'SUB') : 'VFO-' + r.sel });
        refreshTxVfo();
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
            case 'seg': {
                const segs = getSegments(cfg.bandPlan, r.band[vfo]);
                if (!segs) return null;
                const cur = segmentForHz(cfg.bandPlan, r.band[vfo], r.freq[vfo]);
                return Object.entries(segs).map(([k, sg]) => ({ id: k, label: sg.label ?? k, on: k === cur }));
            }
            case 'ant':
                return antennas.map(a => ({ id: a, label: 'ANT ' + a, on: a === r.ant[vfo] }));
            case 'roof':
                return roofing.map(o => ({ id: o.code, label: 'ROOF ' + o.label, on: o.code === r.roof[vfo] }));
        }
        return null;
    }

    function act(kind, id, vfo, dir) {
        if (kind === 'entry') return win.entry(id);
        // The digit wheel, the dial and the spectrum panes tune either VFO.
        if (vfo && !['tune', 'dial', 'spectrum', 'span'].includes(kind) && !boxLive(vfo)) return;
        const v = vfo || r.sel, rx = v.toLowerCase();
        const flip = x => (on(x) ? '0' : '1');
        switch (kind) {
            case 'width':
                return post(`/api/cat/ifwidth/${rx}`, { code: String(id) });
            case 'agc':
                return post(`/api/cat/agc/${rx}`, { code: String(id) });
            case 'seg':
                return segmentJump(v, id);
            case 'ant':
                return antennas.includes(String(id)) ? post(`/api/cat/antenna/${rx}`, { antenna: String(id) }) : undefined;
            case 'roof':
                return roofLabel(id) ? post(`/api/cat/roofingfilter/${rx}`, { filter: String(id) }) : undefined;
            case 'tune':
                return wheelTune(v, num(id));
            case 'knob':
                return knobTurn(id, v, dir > 0 ? 1 : -1);
            case 'dial':
                return dialTune(v, id === 'up' ? 1 : -1);
            case 'vfo':
                if (id !== r.sel) post(`/api/cat/active-vfo/${id}`);
                return;
            case 'span':
                return post(`/api/sdr/span?hz=${id}&sdrId=${vfo}`);
            case 'spectrum':
                return spectrumTune(v, num(id));
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
                    case 'mainsub': return act('vfo', r.sel === 'A' ? 'B' : 'A');
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
                    case 'qsplit': return post('/api/cat/split/2');
                    case 'ab': return post('/api/cat/copy-vfo/ab');
                    case 'ba': return post('/api/cat/copy-vfo/ba');
                    case 'swap': return post('/api/cat/swap-vfo');
                    case 'txa': return txVfoNow() === 'A' ? undefined : post('/api/cat/tx-vfo/A');
                    case 'txb': return txVfoNow() === 'B' ? undefined : post('/api/cat/tx-vfo/B');
                    case 'rxclar': return post('/api/cat/clarifier', { vfo: v, rxOn: !r.rxClar, txOn: r.txClar, offsetHz: r.clarHz[v] });
                    case 'txclar': return post('/api/cat/clarifier', { vfo: v, rxOn: r.rxClar, txOn: !r.txClar, offsetHz: r.clarHz[v] });
                    case 'clrclar':
                        if (!r.clarHz[v]) return;
                        r.clarHz[v] = 0;
                        refreshClar();
                        return post('/api/cat/clarifier', { vfo: v, rxOn: r.rxClar, txOn: r.txClar, offsetHz: 0 });
                }
                return;
            case 'memKey':
                // The radio's memory keys act on the selected receiver; M.STORE
                // saves it to YWC's own memories (the Memories page).
                switch (id) {
                    case 'vm': return post('/api/cat/qmb/vfo');
                    case 'qmbsto': return post('/api/cat/qmb/store');
                    case 'qmbrcl': return post('/api/cat/qmb/recall');
                    case 'mstore': return post(`/api/memory/save-vfo/${rx}`, { label: '' });
                }
        }
    }

    // ---- the turned knobs ----------------------------------------------
    // Each notch moves the value one step from where the last notch left it,
    // shown at once; writes are folded like the dial's, so a fast spin sends
    // only the value it stops on. RF power uses the same endpoint as Classic's
    // slider (never radiopower, which switches the radio off).
    const knobQ = {};
    function knobFrom(key, now) {
        const q = knobQ[key];
        return q && (q.busy || Date.now() < q.until) ? q.to : now;
    }
    async function knobSend(key, value, send) {
        const q = knobQ[key] ??= { to: null, busy: false, until: 0 };
        q.to = value;
        q.until = Date.now() + 700;
        if (q.busy) return;
        q.busy = true;
        try {
            let sent = null;
            while (q.to !== sent) { sent = q.to; await send(sent); }
        } finally { q.busy = false; }
    }
    const clampTo = (x, lo, hi) => Math.max(lo, Math.min(hi, x));
    function knobTurn(id, v, dir) {
        const rx = v.toLowerCase();
        // key, current value, step, low, high, show it, send it
        const turn = (key, now, step, lo, hi, show, send) => {
            if (now == null) return;
            const from = knobFrom(key, now);
            const to = clampTo(Math.round((from + dir * step) / step) * step, lo, hi);
            if (to === from) return;
            show(to);
            return knobSend(key, to, send);
        };
        switch (id) {
            case 'afrf:inner':
                return turn('af' + v, r.af[v], 5, 0, 255, x => { r.af[v] = x; refreshRxKnobs(v); },
                    x => post(`/api/cat/afgain/${rx}`, x));
            case 'afrf:outer':
                return turn('rf' + v, r.rf[v], 5, 0, 255, x => { r.rf[v] = x; refreshRxKnobs(v); },
                    x => post(`/api/cat/rfgain/${rx}`, { value: x }));
            case 'shiftwidth:inner':
                return turn('shift' + v, r.shift[v], 20, -1000, 1000, x => { r.shift[v] = x; refreshRxKnobs(v); },
                    x => post(`/api/cat/ifshift/${rx}`, { shiftHz: x }));
            case 'shiftwidth:outer': {
                const opts = ifWidthOptionsFor(model, r.mode[v]);
                const code = parseInt(knobFrom('width' + v, r.widthCode[v]), 10);
                if (!opts?.length || !Number.isFinite(code)) return;
                const i = opts.findIndex(o => parseInt(o.code, 10) === code);
                const o = opts[clampTo((i < 0 ? 0 : i) + dir, 0, opts.length - 1)];
                if (parseInt(o.code, 10) === code) return;
                r.widthCode[v] = o.code;
                refreshWidth(v);
                return knobSend('width' + v, o.code, x => post(`/api/cat/ifwidth/${rx}`, { code: String(x) }));
            }
            case 'notchcont:inner':
                return turn('notch' + v, r.notchHz[v], 10, 10, 3200, x => { r.notchHz[v] = x; refreshRxKnobs(v); },
                    x => post(`/api/cat/manualnotchfreq/${rx}`, { frequencyHz: x }));
            case 'notchcont:outer':
                return turn('cont' + v, r.contourHz[v], 10, 10, 3200, x => { r.contourHz[v] = x; refreshRxKnobs(v); },
                    x => post(`/api/cat/contour/${rx}`, { on: !!r.contour[v], freqHz: x }));
            case 'micproc:inner':
                return turn('mic', r.mic, 1, 0, 100, x => { r.mic = x; refreshSharedKnobs(); },
                    x => post('/api/cat/micgain', { value: x }));
            case 'micproc:outer':
                return turn('procl', r.procl, 1, 0, 100, x => { r.procl = x; refreshSharedKnobs(); },
                    x => post('/api/cat/proclevel', { value: x }));
            case 'powermon:inner':
                return turn('power', r.power, 1, 5, maxPowerW, x => { r.power = x; refreshSharedKnobs(); },
                    x => post(`/api/cat/power/${r.sel.toLowerCase()}`, { Watts: x }));
            case 'powermon:outer':
                return turn('mon', r.mon, 1, 0, 100, x => { r.mon = x; refreshSharedKnobs(); },
                    x => post('/api/cat/monitorlevel/a', { level: x }));
            case 'speedpitch:inner':
                return turn('speed', r.speed, 1, 4, 60, x => { r.speed = x; refreshSharedKnobs(); },
                    x => post('/api/cat/cw/speed', { speed: x }));
            case 'speedpitch:outer':
                return turn('pitch', r.pitchKnown ? r.pitchCode : null, 1, 0, 75, x => { r.pitchCode = x; refreshSharedKnobs(); },
                    x => post('/api/cat/cwpitch', { code: x }));
            case 'clar':
                return turn('clar' + v, r.clarHz[v], 10, -9990, 9990, x => { r.clarHz[v] = x; refreshClar(); },
                    x => post('/api/cat/clarifier', { vfo: v, rxOn: r.rxClar, txOn: r.txClar, offsetHz: x }));
        }
    }

    // ---- click-to-tune on the SDR spectrum ---------------------------------
    // Classic's rules (spectrum-panel.js): the band plan may pick the mode
    // (unless Settings turns that off), and in RTTY / DATA-L the dial goes
    // the tone offset away from the clicked signal, which is the midpoint.
    const rttyTones = { shiftHz: 170, polarityRev: false, asked: false };
    function askRttyTones(mode) {
        if (rttyTones.asked || !String(mode).startsWith('RTTY')) return;
        rttyTones.asked = true;
        fetch('/api/cat/rtty').then(res => res.ok ? res.json() : null).then(t => {
            if (Number(t?.shiftHz) > 0) rttyTones.shiftHz = Number(t.shiftHz);
            if (typeof t?.polarityRev === 'boolean') rttyTones.polarityRev = t.polarityRev;
        }).catch(() => { rttyTones.asked = false; });
    }
    function toneOffsetHz(mode) {
        if (mode === 'RTTY-L' || mode === 'RTTY-U') return (rttyTones.polarityRev ? -1 : 1) * rttyTones.shiftHz / 2;
        if (mode === 'DATA-L') return afskMidpointAudioHz(loadRttySettings());
        return 0;
    }
    function spectrumTune(v, hz) {
        if (!(hz > 0)) return;
        const auto = autoModeForHz(hz);
        const mode = auto && MODE_CAT[auto] ? auto : null;
        const to = Math.round(hz + toneOffsetHz(mode || r.mode[v]));
        r.freq[v] = to;
        panel.setFrequency(v, to);
        checkBand(v);
        post(`/api/cat/frequency/${v.toLowerCase()}`, { frequencyHz: to });
        if (mode && mode !== r.mode[v]) post(`/api/cat/mode/${v.toLowerCase()}`, { mode: MODE_CAT[mode] });
    }

    // ---- out of band ------------------------------------------------------
    // Classic's test: outside every band edge of the configured region
    // (the band plan JSON can move them, so it is looked up each time).
    function checkBand(v) {
        const hz = r.freq[v];
        if (!hz) return;
        const edges = BAND_EDGES[cfg.bandPlan] || BAND_EDGES.Region1;
        const inside = edges.some(b => hz >= b.lo && hz <= b.hi);
        const near = inside ? null : nearestBandForHz(hz, edges);
        panel.setOutOfBand(v, inside ? null : 'Outside every amateur band' + (near ? ' - nearest ' + near : ''));
    }
    loadBandPlanFromServer().then(() => { for (const v of ['A', 'B']) { checkBand(v); refreshSeg(v); } });

    function tuneBy(deltaHz) {
        const v = r.sel;
        const cur = r.freq[v];
        if (!cur) return;
        // Land on the step grid, as a radio's dial does.
        const next = deltaHz > 0 ? Math.floor(cur / stepHz) * stepHz + stepHz : Math.ceil(cur / stepHz) * stepHz - stepHz;
        r.freq[v] = next;
        panel.setFrequency(v, next);
        checkBand(v);
        post(`/api/cat/frequency/${v.toLowerCase()}`, { frequencyHz: next });
    }

    // The dial: one STEP per notch, landing on the step grid as the radio's
    // dial does. Folded into the digit wheel's writes, so a fast spin and an
    // FA/FB echo cannot fight.
    function dialTune(vfo, dir) {
        const cur = wheel[vfo].to ?? r.freq[vfo];
        if (!cur) return;
        const next = dir > 0 ? Math.floor(cur / stepHz) * stepHz + stepHz : Math.ceil(cur / stepHz) * stepHz - stepHz;
        return wheelTune(vfo, next - cur);
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
        checkBand(vfo);
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
    function offsetFor(vfo, frame) {
        if (frame.rfOrdered) return num(frame.centreHz) - r.freq[vfo];
        const filter = {
            mode: r.mode[vfo],
            ifWidthHz: widthHz(vfo) ?? 3000,
            ifShiftHz: num(r.shift[vfo]),
            cwPitchHz: pitchHz(),
            dataShiftHz: r.dataShiftHz,
        };
        try { return axisOffsetHz(model, vfo, frame.centreHz, filter) ?? 0; } catch { return 0; }
    }
    // The SDR lamp shows the better of the two SDRs; one that is not
    // configured does not count against the other.
    const sdrState = { A: 'unconfigured', B: 'unconfigured' };
    function refreshSdrLamp() {
        const sts = Object.values(sdrState);
        panel.setLamp('sdr', sts.includes('streaming') ? 'on' : sts.includes('connecting') ? 'warn'
            : sts.every(x => x === 'unconfigured') ? 'off' : 'bad');
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
        FrequencyA: v => { if (wheelHolds('A')) return; r.freq.A = num(v); panel.setFrequency('A', r.freq.A); checkBand('A'); refreshSeg('A'); },
        FrequencyB: v => { if (wheelHolds('B')) return; r.freq.B = num(v); panel.setFrequency('B', r.freq.B); checkBand('B'); refreshSeg('B'); },
        BandA: v => { if (v !== r.band.A) { r.band.A = v; panel.clearSpectrum('A'); } panel.setBand(r.band[r.sel]); refreshSeg('A'); },
        BandB: v => { if (v !== r.band.B) { r.band.B = v; panel.clearSpectrum('B'); } panel.setBand(r.band[r.sel]); refreshSeg('B'); },
        ModeA: v => { r.mode.A = String(v ?? ''); panel.setMode('A', r.mode.A); refreshWidth('A'); readDataShift(r.mode.A); askRttyTones(r.mode.A); },
        ModeB: v => { r.mode.B = String(v ?? ''); panel.setMode('B', r.mode.B); refreshWidth('B'); readDataShift(r.mode.B); askRttyTones(r.mode.B); },
        IfWidthA: v => { r.widthCode.A = v; refreshWidth('A'); },
        IfWidthB: v => { r.widthCode.B = v; refreshWidth('B'); },
        IfShiftA: v => { r.shift.A = num(v); refreshRxKnobs('A'); },
        IfShiftB: v => { r.shift.B = num(v); refreshRxKnobs('B'); },
        AgcA: v => { r.agcCode.A = String(v); r.agc.A = AGC[String(v)] ?? ''; panel.setAgc('A', r.agc.A); refreshFlags('A'); },
        AgcB: v => { r.agcCode.B = String(v); r.agc.B = AGC[String(v)] ?? ''; panel.setAgc('B', r.agc.B); refreshFlags('B'); },
        AntennaA: v => setAnt('A', v),
        AntennaB: v => setAnt('B', v),
        RoofingFilterA: v => setRoof('A', v),
        RoofingFilterB: v => setRoof('B', v),
        ActiveVfo: v => { r.sel = num(v) === 1 ? 'B' : 'A'; panel.setSelected(r.sel); panel.setBand(r.band[r.sel]); refreshKeys(); refreshClar(); if (!dual) { refreshFlags('A'); refreshFlags('B'); refreshRxKnobs(r.sel); } },
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
        ContourFreqA: v => { r.contourHz.A = num(v); refreshRxKnobs('A'); },
        ContourFreqB: v => { r.contourHz.B = num(v); refreshRxKnobs('B'); },
        ManualNotchFreqA: v => { r.notchHz.A = num(v); refreshRxKnobs('A'); },
        ManualNotchFreqB: v => { r.notchHz.B = num(v); refreshRxKnobs('B'); },
        ApfFreqA: v => { r.apfHz.A = num(v); },
        ApfFreqB: v => { r.apfHz.B = num(v); },
        SplitMode: v => { r.split = num(v); panel.setSplit(r.split !== 0); panel.setKey('split', { on: r.split !== 0 }); refreshTxVfo(); },
        TxVfo: v => { r.txVfo = num(v); refreshTxVfo(); },
        RxClarOn: v => { r.rxClar = on(v); refreshClar(); },
        TxClarOn: v => { r.txClar = on(v); refreshClar(); },
        ClarifierOffsetA: v => { r.clarHz.A = num(v); refreshClar(); },
        ClarifierOffsetB: v => { r.clarHz.B = num(v); refreshClar(); },
        IsTransmitting: v => { r.tx = on(v); panel.setTx(r.tx); },
        AtuTuning: v => panel.setKey('tune', { tx: on(v) }),
        ProcEnabled: v => { r.proc = on(v); panel.setKey('proc', { on: r.proc }); },
        VoxOn: v => panel.setKey('vox', { on: on(v) }),
        AfGainA: v => { r.af.A = num(v); refreshRxKnobs('A'); },
        AfGainB: v => { r.af.B = num(v); refreshRxKnobs('B'); },
        RfGainA: v => { r.rf.A = num(v); refreshRxKnobs('A'); },
        RfGainB: v => { r.rf.B = num(v); refreshRxKnobs('B'); },
        SquelchA: v => { r.sql.A = num(v); refreshRxKnobs('A'); },
        SquelchB: v => { r.sql.B = num(v); refreshRxKnobs('B'); },
        CwPitch: v => { r.pitchCode = num(v); r.pitchKnown = true; refreshSharedKnobs(); },
        CwSpeed: v => { r.speed = num(v); refreshSharedKnobs(); },
        Power: v => { r.power = num(v); refreshSharedKnobs(); },
        MicGain: v => { r.mic = num(v); refreshSharedKnobs(); },
        ProcLevel: v => { r.procl = num(v); refreshSharedKnobs(); },
        MonitorLevelA: v => { r.mon = num(v); refreshSharedKnobs(); },
        SMeterA: v => panel.setSMeter('A', calibrateNumeric('SMETER', num(v))),
        SMeterB: v => panel.setSMeter('B', calibrateNumeric('SMETER', num(v))),
        PowerMeter: v => { r.txMeterRaw.PO = typeof v === 'object' && v ? v.value : v; refreshTxMeters(); },
        SWRMeter: v => { r.txMeterRaw.SWR = v; refreshTxMeters(); },
        ALCMeter: v => { r.txMeterRaw.ALC = v; refreshTxMeters(); },
        CompressionMeter: v => { r.txMeterRaw.COMP = v; refreshTxMeters(); },
        IDDMeter: v => { r.txMeterRaw.IDD = v; refreshTxMeters(); },
        // Raw 0 is no reading (a PA supply is never 0 V with the radio on), and
        // a VDD off its dial is a bad read, as the Classic gauge treats them.
        VDDMeter: v => {
            const raw = num(v);
            if (!vddDial || raw <= 0) return;
            const volts = calibrateNumeric('VPA', raw);
            if (volts >= vddDial.min && volts <= vddDial.max) panel.setTxMeter('VDD', txMeterFraction('VDD', volts, vddDial));
        },
        Temperature: v => {
            const raw = num(v);
            if (paTemp && raw > 0) panel.setTxMeter('TEMP', txMeterFraction('TEMP', calibrateNumeric('TPA', raw)));
        },
        SdrStatus: v => {
            const vfo = v?.sdrId;
            if (vfo !== 'A' && vfo !== 'B') return;
            const st = String(v.status ?? '');
            sdrState[vfo] = st;
            refreshSdrLamp();
            const msg = { streaming: null, connecting: 'SDR connecting…', unconfigured: 'No SDR configured', nodll: 'SDR driver not found', disconnected: 'SDR disconnected' };
            panel.setSpectrumSource({ label: 'SDR ' + vfo, status: st in msg ? msg[st] : st }, vfo);
        },
        SpectrumUpdate: v => {
            const vfo = v?.sdrId;
            if ((vfo !== 'A' && vfo !== 'B') || !v.bins?.length) return;
            const span = num(v.spanHz);
            if (sdrState[vfo] !== 'streaming') { sdrState[vfo] = 'streaming'; refreshSdrLamp(); }
            panel.setSpectrumSource({ label: 'SDR ' + vfo, spanHz: span, status: null }, vfo);
            let bins = v.rfOrdered ? v.bins : Array.prototype.slice.call(v.bins).reverse();
            if (r.freq[vfo] > 0) bins = alignBins(bins, offsetFor(vfo, v), span);
            if (!specAway) panel.pushSpectrum(bins, vfo);
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
        if (fn) try { fn(msg.value); } catch (e) { console.warn('radio panel:', msg.property, e); }
        win.onRadioState(msg);
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
