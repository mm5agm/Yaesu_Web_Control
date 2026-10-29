// radio-spectrum-yaesu.js - YWC's feed for the radio panel's pop-out spectrum
// (core/js/radio-panel/spectrum-popout.js), the window at /RadioSpectrum.
//
// One pane per SDR: SDR A follows VFO-A (MAIN), SDR B follows VFO-B (SUB),
// as on Classic. What is Yaesu is in spectrum-feed-yaesu.js, shared with the
// RTTY Contest view: the bin order, the IF OUT axis correction and the
// SH-code-to-Hz widths. The span picker changes the SDR only. A click on a
// pane tunes that VFO as a click on the board does, and the mouse wheel
// steps it by the board's STEP.

import { mountSpectrumPopout } from '/js/radio-panel/spectrum-popout.js';
import { spanText } from '/js/radio-panel/panel-math.js';
import { dialStepper } from '/js/radio-panel/dial-stepper.js';
import { post, startYaesuSpectrumFeed } from '/js/ui/spectrum-feed-yaesu.js';

// SpectrumSpanPlan.ValidSpans.
const SPANS_HZ = [1e3, 2e3, 5e3, 10e3, 20e3, 30e3, 40e3, 50e3, 100e3, 200e3, 500e3, 1e6, 2e6];

// The board's STEP (radio-panel-yaesu.js), saved in this browser.
const STEP_KEY = 'ywc.radioPanel.stepHz';
const STEPS_HZ = [10, 50, 100, 500, 1000, 5000, 10000];

const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };
function boardStepHz() {
    let hz = 0;
    try { hz = num(localStorage.getItem(STEP_KEY)); } catch { /* private window */ }
    return STEPS_HZ.includes(hz) ? hz : 100;
}

/**
 * @param {HTMLElement} root
 * @param {object} cfg  { conn, model, dual, popout }  popout: the page's PopoutChild
 */
export function startYaesuSpectrumPopout(root, cfg) {
    const dual = !!cfg.dual;
    const panes = dual
        ? [{ id: 'A', tab: 'Main', name: 'MAIN · SDR A' }, { id: 'B', tab: 'Sub', name: 'SUB · SDR B' }]
        : [{ id: 'A', tab: 'VFO-A', name: 'VFO-A · SDR A' }, { id: 'B', tab: 'VFO-B', name: 'VFO-B · SDR B' }];

    const pop = mountSpectrumPopout(root, {
        title: 'SPECTRUM',
        panes,
        spans: SPANS_HZ.map(hz => ({ hz, label: spanText(hz) })),
        onSpan: (id, hz) => feed.setSpan(id, hz),
        onTune: (vfo, hz) => feed.tune(vfo, hz),
        tuneTo: (vfo, hz) => feed.tuneTo(vfo, hz),
        onStep: (vfo, dir) => stepper.turn(vfo, dir),
        onReattach: () => cfg.popout?.reattach(),
        onClose: () => window.close(),
        storagePrefix: 'ywc.radioPanel.',
        fontBase: '/lib/fonts',
    });

    const feed = startYaesuSpectrumFeed({
        conn: cfg.conn,
        model: cfg.model,
        pane: vfo => pop.pane(vfo),
        onSpan: (vfo, span) => pop.setSpanValue(vfo, span),
        holds: vfo => stepper.holds(vfo),
    });
    const stepper = dialStepper({
        stepHz: boardStepHz,
        freq: vfo => feed.state.freq[vfo],
        show: (vfo, hz) => feed.showFrequency(vfo, hz),
        write: (vfo, hz) => post(`/api/cat/frequency/${vfo.toLowerCase()}`, { frequencyHz: hz }),
    });

    cfg.conn.start().catch(e => console.warn('spectrum: hub', e));
    return pop;
}
