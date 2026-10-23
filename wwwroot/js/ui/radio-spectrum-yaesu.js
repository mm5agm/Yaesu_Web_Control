// radio-spectrum-yaesu.js - YWC's feed for the radio panel's pop-out spectrum
// (core/js/radio-panel/spectrum-popout.js), the window at /RadioSpectrum.
//
// One pane per SDR: SDR A follows VFO-A (MAIN), SDR B follows VFO-B (SUB),
// as on Classic. What is Yaesu lives here, as it does in radio-panel-yaesu.js:
// the bin order, the IF OUT axis correction and the SH-code-to-Hz widths.
// The span picker changes the SDR only. A click on a pane tunes that VFO as
// a click on the board does (spectrum-tune-yaesu.js).

import { mountSpectrumPopout } from '/js/radio-panel/spectrum-popout.js';
import { alignBins, spanText } from '/js/radio-panel/panel-math.js';
import { axisOffsetHz } from '/js/sdr/if-out-offset.js';
import { ifWidthHzFor } from '/js/ui/if-width-tables.js';
import { loadBandPlanFromServer } from '/js/ui/band-plan.js';
import { MODE_CAT, spectrumTuner } from '/js/ui/spectrum-tune-yaesu.js';

const SPANS_HZ = [1e3, 2e3, 5e3, 10e3, 20e3, 50e3, 100e3, 200e3, 500e3, 1e6, 2e6];
const SPOT_MAX_AGE_MS = 30 * 60 * 1000;
const STATUS_TEXT = {
    streaming: null, connecting: 'SDR connecting…', unconfigured: 'No SDR configured',
    nodll: 'SDR driver not found', disconnected: 'SDR disconnected',
};

const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };

function post(url, body) {
    return fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
        .then(res => { if (!res.ok) console.warn('spectrum:', url, res.status); return res; })
        .catch(e => console.warn('spectrum:', url, e));
}

/**
 * @param {HTMLElement} root
 * @param {object} cfg  { conn, model, dual, popout }  popout: the page's PopoutChild
 */
export function startYaesuSpectrumPopout(root, cfg) {
    const model = cfg.model;
    const dual = !!cfg.dual;
    const panes = dual
        ? [{ id: 'A', tab: 'Main', name: 'MAIN · SDR A' }, { id: 'B', tab: 'Sub', name: 'SUB · SDR B' }]
        : [{ id: 'A', tab: 'VFO-A', name: 'VFO-A · SDR A' }, { id: 'B', tab: 'VFO-B', name: 'VFO-B · SDR B' }];
    const tuner = spectrumTuner();
    loadBandPlanFromServer();

    const pop = mountSpectrumPopout(root, {
        title: 'SPECTRUM',
        panes,
        spans: SPANS_HZ.map(hz => ({ hz, label: spanText(hz) })),
        onSpan: (id, hz) => {
            fetch(`/api/sdr/span?hz=${hz}&sdrId=${id}`, { method: 'POST' })
                .then(res => { if (!res.ok) console.warn('spectrum: span', res.status); })
                .catch(e => console.warn('spectrum: span', e));
        },
        onTune: (vfo, hz) => {
            if (!(hz > 0)) return;
            const { mode, to } = tuner.target(hz, r.mode[vfo]);
            post(`/api/cat/frequency/${vfo.toLowerCase()}`, { frequencyHz: to });
            if (mode && mode !== r.mode[vfo]) post(`/api/cat/mode/${vfo.toLowerCase()}`, { mode: MODE_CAT[mode] });
        },
        tuneTo: (vfo, hz) => tuner.target(hz, r.mode[vfo]).to,
        onReattach: () => cfg.popout?.reattach(),
        onClose: () => window.close(),
        storagePrefix: 'ywc.radioPanel.',
        fontBase: '/lib/fonts',
    });

    const r = {
        freq: { A: 0, B: 0 }, mode: { A: '', B: '' }, widthCode: { A: null, B: null },
        shift: { A: 0, B: 0 }, pitchCode: 30, dataShiftHz: 1500,
    };
    const pitchHz = () => 300 + num(r.pitchCode) * 10;
    const widthHz = vfo => (r.widthCode[vfo] == null ? null : ifWidthHzFor(model, r.mode[vfo], parseInt(r.widthCode[vfo], 10)));
    const refreshPassband = vfo => pop.pane(vfo).setPassband(r.mode[vfo], widthHz(vfo));

    // Same convention as the board and Classic: true RF of the frame's centre
    // = dial + offset; null (an unmeasured radio) is no correction.
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

    function readDataShift(mode) {
        if (mode !== 'DATA-L' && mode !== 'DATA-U') return;
        fetch('/api/cat/datashift')
            .then(x => (x.ok ? x.json() : null))
            .then(d => { const hz = Number(d?.dataShiftHz); if (Number.isFinite(hz)) r.dataShiftHz = hz; })
            .catch(() => { /* 1500 stays */ });
    }

    const spots = new Map();       // callsign -> { call, hz, at }
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
        const list = [...spots.values()].sort((a, b) => b.at - a.at).slice(0, 40);
        for (const p of panes) pop.pane(p.id).setSpots(list);
    }
    setInterval(publishSpots, 60_000);

    const perVfo = vfo => ({
        ['Frequency' + vfo]: v => { r.freq[vfo] = num(v); pop.pane(vfo).setFrequency(r.freq[vfo]); },
        ['Mode' + vfo]: v => { r.mode[vfo] = String(v ?? ''); refreshPassband(vfo); readDataShift(r.mode[vfo]); tuner.askTones(r.mode[vfo]); },
        ['IfWidth' + vfo]: v => { r.widthCode[vfo] = v; refreshPassband(vfo); },
        ['IfShift' + vfo]: v => { r.shift[vfo] = num(v); },
        ['Band' + vfo]: () => pop.pane(vfo).clear(),
    });
    const handlers = {
        ...perVfo('A'),
        ...perVfo('B'),
        CwPitch: v => { r.pitchCode = num(v); },
        SdrStatus: v => {
            const pane = pop.pane(v?.sdrId);
            if (!pane) return;
            const st = String(v.status ?? '');
            pane.setStatus(st in STATUS_TEXT ? STATUS_TEXT[st] : st);
        },
        SpectrumUpdate: v => {
            const vfo = v?.sdrId;
            const pane = pop.pane(vfo);
            if (!pane || !v.bins?.length) return;
            const span = num(v.spanHz);
            pane.setStatus(null);
            pane.setSpan(span);
            pop.setSpanValue(vfo, span);
            let bins = v.rfOrdered ? v.bins : Array.prototype.slice.call(v.bins).reverse();
            if (r.freq[vfo] > 0) bins = alignBins(bins, offsetFor(vfo, v), span);
            pane.push(bins);
        },
        DxSpot: addSpot,
    };

    fetch('/api/dxcluster/spots').then(x => (x.ok ? x.json() : [])).then(list => { for (const sp of list ?? []) addSpot(sp); }).catch(() => {});

    const conn = cfg.conn;
    conn.on('RadioStateUpdate', msg => {
        const fn = handlers[msg?.property];
        if (fn) try { fn(msg.value); } catch (e) { console.warn('spectrum:', msg.property, e); }
    });
    conn.start().catch(e => console.warn('spectrum: hub', e));
    return pop;
}
