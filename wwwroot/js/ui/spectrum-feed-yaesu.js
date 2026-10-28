// spectrum-feed-yaesu.js - YWC's SDR spectrum feed for the shared spectrum
// panes (core/js/radio-panel/spectrum-pane.js), for every page that shows
// them on their own: the pop-out spectrum (radio-spectrum-yaesu.js) and the
// RTTY Contest view.
//
// One pane per SDR: SDR A follows VFO-A (MAIN), SDR B follows VFO-B (SUB),
// as on Classic. What is Yaesu lives here: the bin order, the IF OUT axis
// correction, the SH-code-to-Hz widths and the DATA shift. A click tunes as
// a click on the board does (spectrum-tune-yaesu.js).
//
// It listens on the page's hub connection but does not start it: the page
// may have handlers of its own to add first.

import { alignBins } from '/js/radio-panel/panel-math.js';
import { axisOffsetHz, viewOffsetHz } from '/js/sdr/if-out-offset.js';
import { ifWidthHzFor } from '/js/ui/if-width-tables.js';
import { loadBandPlanFromServer } from '/js/ui/band-plan.js';
import { MODE_CAT, spectrumTuner } from '/js/ui/spectrum-tune-yaesu.js';

const SPOT_MAX_AGE_MS = 30 * 60 * 1000;
const STATUS_TEXT = {
    streaming: null, connecting: 'SDR connecting…', unconfigured: 'No SDR configured',
    nodll: 'SDR driver not found', disconnected: 'SDR disconnected',
};

const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };

export function post(url, body) {
    return fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
        .then(res => { if (!res.ok) console.warn('spectrum:', url, res.status); return res; })
        .catch(e => console.warn('spectrum:', url, e));
}

/**
 * @param {object} cfg
 * @param {object} cfg.conn  the page's hub connection, not yet started
 * @param {string} cfg.model
 * @param {(vfo:string) => object|undefined} cfg.pane  the SpectrumPane showing that VFO, if any
 * @param {(vfo:string, hz:number) => void} [cfg.onSpan]  a frame arrived at this span
 * @param {(vfo:string) => boolean} [cfg.holds]  true while the page's own dial write is in flight
 * @param {string[]} [cfg.vfos=['A','B']]  the panes spots are published to
 * @param {boolean} [cfg.dataURtty]  a DATA-U click tunes for RTTY tones (see spectrumTuner)
 */
export function startYaesuSpectrumFeed(cfg) {
    const model = cfg.model;
    const vfos = cfg.vfos ?? ['A', 'B'];
    const pane = vfo => cfg.pane(vfo);
    const tuner = spectrumTuner({ dataURtty: cfg.dataURtty });
    loadBandPlanFromServer();

    const r = {
        freq: { A: 0, B: 0 }, mode: { A: '', B: '' }, widthCode: { A: null, B: null },
        shift: { A: 0, B: 0 }, pitchCode: 30, dataShiftHz: 1500,
    };
    const pitchHz = () => 300 + num(r.pitchCode) * 10;
    const widthHz = vfo => (r.widthCode[vfo] == null ? null : ifWidthHzFor(model, r.mode[vfo], parseInt(r.widthCode[vfo], 10)));
    const refreshPassband = vfo => pane(vfo)?.setPassband(r.mode[vfo], widthHz(vfo), r.dataShiftHz);

    // Same convention as the board and Classic: true RF of the frame's centre
    // = dial + offset; null (an unmeasured radio) is no correction.
    const filterFor = vfo => ({
        mode: r.mode[vfo],
        ifWidthHz: widthHz(vfo) ?? 3000,
        ifShiftHz: num(r.shift[vfo]),
        cwPitchHz: pitchHz(),
        dataShiftHz: r.dataShiftHz,
    });
    function offsetFor(vfo, frame) {
        if (frame.rfOrdered) return num(frame.centreHz) - r.freq[vfo];
        try { return axisOffsetHz(model, vfo, frame.centreHz, filterFor(vfo)) ?? 0; } catch { return 0; }
    }
    // Where the pane is centred from the dial: the filter in DATA modes
    // (viewOffsetHz). The radio's own scope is drawn as it is sent.
    function viewFor(vfo, frame) {
        if (frame.rfOrdered) return 0;
        try { return viewOffsetHz(model, filterFor(vfo)); } catch { return 0; }
    }

    function readDataShift(mode) {
        if (mode !== 'DATA-L' && mode !== 'DATA-U') return;
        fetch('/api/cat/datashift')
            .then(x => (x.ok ? x.json() : null))
            .then(d => { const hz = Number(d?.dataShiftHz); if (Number.isFinite(hz)) r.dataShiftHz = hz; vfos.forEach(refreshPassband); })
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
        for (const v of vfos) pane(v)?.setSpots(list);
    }
    setInterval(publishSpots, 60_000);

    const perVfo = vfo => ({
        ['Frequency' + vfo]: v => { if (cfg.holds?.(vfo)) return; r.freq[vfo] = num(v); pane(vfo)?.setFrequency(r.freq[vfo]); },
        ['Mode' + vfo]: v => { r.mode[vfo] = String(v ?? ''); refreshPassband(vfo); readDataShift(r.mode[vfo]); tuner.askTones(r.mode[vfo]); },
        ['IfWidth' + vfo]: v => { r.widthCode[vfo] = v; refreshPassband(vfo); },
        ['IfShift' + vfo]: v => { r.shift[vfo] = num(v); },
        ['Band' + vfo]: () => pane(vfo)?.clear(),
    });
    const handlers = {
        ...perVfo('A'),
        ...perVfo('B'),
        CwPitch: v => { r.pitchCode = num(v); },
        SdrStatus: v => {
            const p = pane(v?.sdrId);
            if (!p) return;
            const st = String(v.status ?? '');
            p.setStatus(st in STATUS_TEXT ? STATUS_TEXT[st] : st);
        },
        SpectrumUpdate: v => {
            const vfo = v?.sdrId;
            const p = pane(vfo);
            if (!p || !v.bins?.length) return;
            const span = num(v.spanHz);
            p.setStatus(null);
            p.setSpan(span);
            cfg.onSpan?.(vfo, span);
            let bins = v.rfOrdered ? v.bins : Array.prototype.slice.call(v.bins).reverse();
            if (r.freq[vfo] > 0) {
                const view = p.viewFor(viewFor(vfo, v));
                p.setViewOffset(view);
                bins = alignBins(bins, offsetFor(vfo, v) - view, span);
            }
            p.push(bins);
        },
        DxSpot: addSpot,
    };

    fetch('/api/dxcluster/spots').then(x => (x.ok ? x.json() : [])).then(list => { for (const sp of list ?? []) addSpot(sp); }).catch(() => {});

    cfg.conn.on('RadioStateUpdate', msg => {
        const fn = handlers[msg?.property];
        if (fn) try { fn(msg.value); } catch (e) { console.warn('spectrum:', msg.property, e); }
    });

    return {
        /** What the feed has heard: freq, mode, ... per VFO. Read only. */
        state: r,
        /** Where the dial lands for a click at hz. */
        tuneTo: (vfo, hz) => tuner.target(hz, r.mode[vfo]).to,
        /** A click at hz: tune there, and change mode if the band plan says to (and it is on). */
        tune(vfo, hz) {
            if (!(hz > 0)) return;
            const { mode, to } = tuner.target(hz, r.mode[vfo]);
            post(`/api/cat/frequency/${vfo.toLowerCase()}`, { frequencyHz: to });
            if (mode && mode !== r.mode[vfo]) post(`/api/cat/mode/${vfo.toLowerCase()}`, { mode: MODE_CAT[mode] });
        },
        /** Draw the VFO at hz now, ahead of the radio's echo (a dial step). */
        showFrequency(vfo, hz) { r.freq[vfo] = hz; pane(vfo)?.setFrequency(hz); },
        setSpan(vfo, hz) {
            fetch(`/api/sdr/span?hz=${hz}&sdrId=${vfo}`, { method: 'POST' })
                .then(res => { if (!res.ok) console.warn('spectrum: span', res.status); })
                .catch(e => console.warn('spectrum: span', e));
        },
    };
}
