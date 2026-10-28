// contest-view.js - the RTTY Contest view: only what a contest RTTY operator
// uses, on one screen. A top strip (frequency, mode or a MODE list, Mark/Shift, compact
// meters, Main/Sub, DX SPOTS, Views), a row of scope controls (ref
// level, auto floor, range, fit, swap, wheel step), the spectrum, with a
// SPAN list in its label, over the waterfall with a grip between them, and
// the RTTY tuner docked at the side.
//
// The scale is dB above the noise floor. REF is where the bottom of the
// trace sits from the floor, AUTO lets the floor follow the noise, FIT (or a
// double-click on the grip) sets the range so the strongest recent signal
// just fits. They are kept per receiver.
//
// Every part pops out into a window of its own (core popout.js): the
// spectrum, the waterfall, both, and the tuner. A window is this same view
// with opts.part set, showing that part and a DOCK key. The main page hides
// what is out, and the windows follow its choices - receiver, spots, levels,
// swap, step - through storage events, so there is one set of them. The
// tuner can also go On top (pip-dock.js), where the browser allows it.
// DECODE on the tuner's bar shows the decoded text with it (opts.onDecode:
// the app shows its reader and starts or stops the decoder). Between the
// two, a box of settings the app names (opts.controls), e.g. power, IF width
// and IF shift.
//
// Under the strip, a row of band keys for each receiver (opts.bandRows and
// opts.bands): the app decides where a band key lands. BANDS picks which keys
// each row shows, and can hide the second receiver (its tab and the band row
// of the same id), kept in this browser. The wheel over a
// digit of the frequency moves the dial by what that digit is worth
// (opts.onNudge), as on the radio panel; a click on it types one. Arrows at
// the edges of the spectrum (or of the waterfall, on top) move the dial one
// span down or up, as a typed frequency would.
//
// The operator sizes two boxes here, as windows are sized: the spectrum and
// waterfall, and the docked tuner, each by its right edge, its bottom edge or
// its bottom-right corner. A box nobody has sized fills the room it has.
// LAYOUT saves all of it by name on the server (opts.layoutsApi, the same
// API and store as Experimental's layouts, in a file of its own): those
// sizes, the choices above, which parts are out and where each window is,
// so a layout can put every window back on its own monitor.
//
// Like the radio panel it has no radio in it. The app's adapter feeds each
// receiver's pane (spectrum-pane.js) and the strip, and is told what the
// operator did. The tuner's own markup is the app's too: the page hands its
// element over and this only gives it a place.
//
// Needs the radio panel's stylesheet (injected here) and its themes.

import { injectRadioPanelStyle } from '../radio-panel/radio-panel-style.js';
import { applyTheme, DEFAULT_THEME } from '../radio-panel/radio-panel-themes.js';
import { SpectrumPane } from '../radio-panel/spectrum-pane.js';
import { formatFrequency } from '../radio-panel/panel-math.js';
import { freqDigits, wheelDigits } from '../radio-panel/freq-digits.js';
import { PopoutHost, popoutPlace, setPopoutPlace } from '../popout/popout.js';
import { initFlexLayoutsAdmin } from '../flex/flex-layouts-admin.js';
import { pipDock, pipSupported } from './pip-dock.js';

const store = {
    get(k, d) { try { return localStorage.getItem(k) ?? d; } catch { return d; } },
    set(k, val) { try { localStorage.setItem(k, val); } catch { /* private window */ } },
};

function h(tag, cls, text) {
    const el = document.createElement(tag);
    if (cls) el.className = cls;
    if (text != null) el.textContent = text;
    return el;
}
function btn(cls, text, onClick) {
    const b = h('button', cls, text);
    b.type = 'button';
    b.addEventListener('click', onClick);
    return b;
}

/**
 * A typed frequency, as an operator types it: 14.08 or 7.0405 is MHz,
 * 14080 or 7040.5 is kHz, anything from 100000 up is Hz. Commas count as
 * points. Null for anything else, or outside minHz..maxHz.
 */
export function parseFrequencyEntry(text, minHz = 30_000, maxHz = 75_000_000) {
    const t = String(text ?? '').trim().replace(/,/g, '.').replace(/\s+/g, '');
    if (!/^\d+(\.\d*)?$|^\.\d+$/.test(t)) return null;
    const v = Number(t);
    if (!(v > 0)) return null;
    const hz = Math.round(v < 100 ? v * 1e6 : v < 100_000 ? v * 1e3 : v);
    return hz >= minHz && hz <= maxHz ? hz : null;
}

// The radio panel's faces (radio-panel-style.js loads them).
export const STEP_CHOICES_HZ = [1, 10, 100, 1000];
const DEFAULT_STEP_HZ = 10;
// The trace's width in CSS pixels. Bruce VK2RT found the radio panel's 1.2
// heavy here, so the view starts thinner.
export const TRACE_CHOICES_PX = [0.5, 0.8, 1.2, 2];
const DEFAULT_TRACE_PX = 0.8;
export const POPOUT_PARTS = ['spectrum', 'waterfall', 'both', 'tuner', 'decode'];
const POPOUT_SIZES = {
    spectrum: { width: 1100, height: 380 },
    waterfall: { width: 1100, height: 520 },
    both: { width: 1100, height: 760 },
    tuner: { width: 380, height: 620 },
    decode: { width: 560, height: 420 },
};
const POPOUT_LABELS = { spectrum: 'SPECTRUM', waterfall: 'WATERFALL', both: 'BOTH', tuner: 'POP OUT', decode: 'POP OUT' };

/**
 * What the main page still draws, given which windows are out:
 * 'both', 'spectrum', 'waterfall' or 'none'.
 */
export function partsLeft(out) {
    const spec = !(out?.both || out?.spectrum);
    const wf = !(out?.both || out?.waterfall);
    return spec && wf ? 'both' : spec ? 'spectrum' : wf ? 'waterfall' : 'none';
}
const REF_STEP_DB = 2;
const RANGE_STEP_DB = 5;

// The two boxes' sizes, from their grabs. Null is a box nobody has sized:
// the spectrum fills what is left, the tuner is the partial's own width and
// the full height. The tuner's least width is its own min-width
// (_RttyTunerPartial).
export const BOX_SIZES = {
    stackWidth: { min: 320 },
    stackHeight: { min: 200 },
    tunerWidth: { min: 240, max: 760 },
    tunerHeight: { min: 300 },
};
const GRAB_KEY_STEP = 20;
// The least the decoded text under the tuner keeps when the tuner is made taller.
const DECODE_MIN_H = 120;
const BODY_GAP = 10;
export function tidyBoxSize(name, w) {
    const r = BOX_SIZES[name];
    const v = Number(w);
    if (!r || w == null || w === '' || !Number.isFinite(v)) return null;
    return Math.max(r.min, Math.min(r.max ?? Infinity, Math.round(v)));
}

/**
 * A saved layout, tidied: whatever is missing or wrong is the default, so a
 * layout saved by an older version, or edited by hand, still loads.
 */
export function tidyLayout(l) {
    const o = l && typeof l === 'object' ? l : {};
    const out = {};
    for (const p of POPOUT_PARTS) out[p] = o.out?.[p] === true;
    const places = {};
    for (const p of POPOUT_PARTS) {
        const g = o.places?.[p];
        if (g && typeof g === 'object' && Number(g.width) > 0 && Number(g.height) > 0) places[p] = g;
    }
    const split = Number(o.split);
    return {
        stackWidth: tidyBoxSize('stackWidth', o.stackWidth),
        stackHeight: tidyBoxSize('stackHeight', o.stackHeight),
        tunerWidth: tidyBoxSize('tunerWidth', o.tunerWidth),
        tunerHeight: tidyBoxSize('tunerHeight', o.tunerHeight),
        meters: o.meters !== false,
        spots: o.spots !== false,
        wfFirst: o.wfFirst === true,
        decode: o.decode === true,
        split: split > 0 && split < 1 ? split : 0.5,
        step: STEP_CHOICES_HZ.includes(Number(o.step)) ? Number(o.step) : DEFAULT_STEP_HZ,
        trace: TRACE_CHOICES_PX.includes(Number(o.trace)) ? Number(o.trace) : DEFAULT_TRACE_PX,
        out,
        places,
    };
}

/** A receiver's kept levels, tidied: { auto, ref (null for the default), range (null for the default) }. */
export function tidyLevels(l) {
    const n = (x, lo, hi) => { const v = Number(x); return x != null && x !== '' && Number.isFinite(v) ? Math.max(lo, Math.min(hi, Math.round(v))) : null; };
    return { auto: l?.auto !== false, ref: n(l?.ref, -40, 40), range: n(l?.range, 5, 160) };
}

const SANS = "'Barlow Condensed', 'Arial Narrow', sans-serif";
const MONO = "'Share Tech Mono', Consolas, monospace";

const CSS = `
.rc-page{height:100vh;display:flex;flex-direction:column}
.rc-top{flex:none;display:flex;align-items:center;flex-wrap:wrap;gap:6px 10px;padding:6px 10px;background:var(--rp-key-bg);border-bottom:1px solid var(--rp-key-border)}
.rc-title{font:700 16px ${SANS};letter-spacing:.1em;color:var(--rp-key-fg);white-space:nowrap}
.rc-freq{appearance:none;border:1px solid transparent;background:none;border-radius:4px;padding:0 4px;font:500 26px ${MONO};color:var(--rp-key-fg);cursor:text;white-space:nowrap}
.rc-freq small{font-size:18px;color:var(--rp-chip-fg)}
.rc-freq:hover{border-color:var(--rp-soft-border)}
.rc-freq.wheel span[data-step]:hover{background:var(--rp-accent-bg);border-radius:3px;cursor:ns-resize}
.rc-bands{flex:none;display:flex;flex-direction:column;gap:4px;padding:6px 10px 0}
.rc-brow{display:flex;align-items:center;flex-wrap:wrap;gap:4px}
.rc-brow .rc-lbl{min-width:3.2em}
.rc-brow .rp-spop-key{min-height:26px;min-width:44px;padding:1px 6px;font-size:14px}
.rc-freq-in{width:9em;font:500 22px ${MONO};background:var(--rp-spec-bg);color:var(--rp-freq);border:1px solid var(--rp-highlight);border-radius:4px;padding:1px 6px}
.rc-chip{font:600 15px ${SANS};letter-spacing:.06em;color:var(--rp-chip-fg);border:1px solid var(--rp-well-border);border-radius:4px;padding:2px 8px;white-space:nowrap}
.rc-chip.tx{background:var(--rp-tx);border-color:var(--rp-tx);color:var(--rp-tx-fg)}
.rc-meters{display:flex;gap:8px;align-items:center}
.rc-meters[hidden]{display:none}
.rc-meter{display:flex;align-items:center;gap:4px;font:600 13px ${SANS};color:var(--rp-legend-dim)}
.rc-bar{width:48px;height:8px;border-radius:2px;background:var(--rp-bar-bg);border:1px solid var(--rp-well-border);overflow:hidden}
.rc-fill{height:100%;width:0;background:var(--rp-highlight);transition:width .12s linear}
.rc-meter.over .rc-fill{background:var(--rp-tx)}
.rc-val{min-width:3.6em;font:400 13px ${MONO};color:var(--rp-chip-fg)}
.rc-gap{flex:1}
.rc-body{flex:1;min-height:0;display:flex;gap:10px;padding:10px}
.rc-stack{flex:1;min-width:0;display:flex;position:relative}
.rc-stack.sized-w{flex:0 1 auto;min-width:320px}
.rc-stack.sized-h,.rc-tuner.sized-h{max-height:100%}
.rc-side{flex:none;display:flex;flex-direction:column;gap:10px;min-height:0}
.rc-side[hidden]{display:none}
.rc-holder{flex:1;min-width:0;display:flex}
.rc-holder[hidden],.rc-holder:has(>.rp-pane[hidden]){display:none}
.rc-page .rp-pane{position:relative;padding:6px 8px 8px;border-radius:6px;background:var(--rp-screen);border:1px solid var(--rp-screen-line)}
.rc-tuner{flex:0 1 auto;display:flex;flex-direction:column;min-height:0;position:relative}
.rc-tuner:empty{display:none}
.rc-tuner.sized-h{flex:none}
.rc-ctl{flex:none;display:flex;flex-direction:column;gap:6px;width:var(--rc-tuner-w,300px);box-sizing:border-box;padding:8px 10px;border:1px solid var(--rp-well-border);border-radius:6px;background:var(--rp-key-bg)}
.rc-crow{display:flex;align-items:center;gap:8px}
.rc-crow .rc-lbl{min-width:3.6em}
.rc-crow input[type=range]{flex:1;min-width:0;accent-color:var(--rp-highlight)}
.rc-crow select{flex:1;min-width:0}
.rc-crow .rc-num{min-width:4.6em;text-align:right}
.rc-dbox{flex:1 1 0;min-height:${DECODE_MIN_H}px;display:flex;flex-direction:column;width:var(--rc-tuner-w,300px)}
.rc-dbox[hidden]{display:none}
.rc-dbox.out{flex:none;min-height:0}
.rc-scope{flex:none;display:flex;align-items:center;flex-wrap:wrap;gap:6px 8px;padding:6px 10px 0}
.rc-grp{display:flex;align-items:center;gap:3px}
.rc-lbl{font:600 13px ${SANS};letter-spacing:.08em;color:var(--rp-legend-dim);margin-right:2px}
.rc-num{min-width:3em;text-align:center;font:400 14px ${MONO};color:var(--rp-chip-fg)}
.rc-scope .rp-spop-key{min-height:28px;padding:1px 10px;font-size:14px}
.rc-scope .rc-pm{min-width:28px;padding:1px 0}
.rc-step,.rc-mode{font:500 14px ${SANS};min-height:28px;background:var(--rp-chipbtn-bg);color:var(--rp-chipbtn-fg);border:1px solid var(--rp-key-border);border-radius:4px;padding:1px 4px}
.rc-mode{font:600 15px ${SANS};letter-spacing:.06em}
.rc-out{flex:1;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:12px;font:500 18px ${SANS};color:var(--rp-legend-dim);border:1px dashed var(--rp-well-border);border-radius:6px}
.rc-out[hidden]{display:none}
.rc-tbar{display:flex;gap:6px;justify-content:flex-end;padding:0 0 6px}
.rc-tbar .rp-spop-key{min-height:28px;padding:1px 10px;font-size:14px}
.rc-tbar .rp-spop-key:disabled{opacity:.45;cursor:default}
.rc-tnote{width:var(--rc-tuner-w,300px);box-sizing:border-box;padding:14px;font:500 16px ${SANS};color:var(--rp-legend-dim);border:1px dashed var(--rp-well-border);border-radius:6px}
.rc-tnote[hidden]{display:none}
.rc-pop{display:flex;align-items:center;gap:4px}
.rc-pop[hidden]{display:none}
body>#rttyTunerDialog{width:100% !important;height:100vh;border:0 !important;border-radius:0 !important}
.rc-views{font:500 15px ${SANS};min-height:32px;background:var(--rp-chipbtn-bg);color:var(--rp-chipbtn-fg);border:1px solid var(--rp-key-border);border-radius:4px;padding:2px 6px}
.rc-layouts{max-width:16em}
.rc-grab{position:absolute;z-index:3;border-radius:4px;touch-action:none}
.rc-grab:hover,.rc-grab:focus-visible,.rc-grab.drag{background:var(--rp-soft-border);outline:none}
.rc-grab[hidden]{display:none}
.rc-grab-r{top:0;bottom:12px;right:-10px;width:10px;cursor:ew-resize}
.rc-grab-b{left:0;right:12px;bottom:-10px;height:10px;cursor:ns-resize}
.rc-grab-rb{right:-10px;bottom:-10px;width:16px;height:16px;cursor:nwse-resize;background:linear-gradient(135deg,transparent 50%,var(--rp-soft-border) 50%)}
.rc-blocked{flex:none;display:flex;align-items:center;flex-wrap:wrap;gap:6px 8px;padding:6px 10px;font:500 15px ${SANS};color:var(--rp-chip-fg);background:var(--rp-key-bg);border-bottom:1px solid var(--rp-key-border)}
.rc-blocked[hidden]{display:none}
.rc-blocked .rp-spop-key{min-height:28px;padding:1px 10px;font-size:14px}
.rc-manage{width:min(640px,95vw);max-height:85vh;padding:0;border:1px solid var(--rp-key-border);border-radius:8px;background:var(--rp-key-bg);color:var(--rp-chip-fg)}
.rc-manage::backdrop{background:rgba(0,0,0,.5)}
.rc-manage-head{display:flex;align-items:center;justify-content:space-between;padding:8px 12px;font:700 16px ${SANS};letter-spacing:.08em;border-bottom:1px solid var(--rp-key-border)}
.rc-manage-body{padding:12px;overflow:auto}
.rc-bandset{width:min(520px,95vw)}
.rc-bandset .rc-manage-body{display:flex;flex-direction:column;gap:10px}
.rc-bandrow{display:flex;flex-wrap:wrap;gap:6px 14px;margin:0;padding:6px 10px 8px;border:1px solid var(--rp-key-border);border-radius:6px}
.rc-bandrow legend{font:600 13px ${SANS};letter-spacing:.08em;color:var(--rp-legend-dim);padding:0 4px}
.rc-tick{display:inline-flex;align-items:center;gap:5px;font:600 14px ${SANS};cursor:pointer}
.rc-brow[hidden],.rc-bands[hidden],.rc-brow .rp-spop-key[hidden],.rp-spop-tabs[hidden],.rp-spop-tab[hidden]{display:none}
`;

/**
 * @param {HTMLElement} root
 * @param {object} opts
 * @param {{id:string, tab:string, name:string}[]} opts.receivers  one or two; with one there is no Main/Sub switch
 * @param {{hz:number, label:string}[]} [opts.spans]  the SPAN list in each pane's label
 * @param {(id:string, hz:number) => void} [opts.onSpan]
 * @param {(id:string, hz:number) => void} [opts.onTune]  a click on a pane
 * @param {(id:string, hz:number) => number} [opts.tuneTo]  where the dial lands for a click at hz
 * @param {(id:string, dir:number) => void} [opts.onStep]  a mouse wheel notch
 * @param {(id:string, hz:number) => void} [opts.onFrequencyEntry]  a typed frequency
 * @param {(id:string) => void} [opts.onReceiver]  the operator picked Main or Sub
 * @param {{id:string, label:string}[]} [opts.modes]  the modes the MODE list offers; without
 *        them, or without opts.onMode, the mode is only shown
 * @param {(id:string, mode:string) => void} [opts.onMode]  a mode picked for the receiver on screen
 * @param {string} [opts.modeTitle]  the MODE list's tooltip
 * @param {{id:string, label:string, kind:'range'|'list', title?:string, perReceiver?:boolean,
 *          min?:number, max?:number, step?:number, reset?:number, unit?:string,
 *          format?:(v:number) => string}[]} [opts.controls]  settings in a box under the tuner,
 *        e.g. power, IF width and IF shift; their values come through setControl
 * @param {(id:string, value:number|string, vfo:string|null) => void} [opts.onControl]  one was
 *        changed here; vfo is the receiver on screen for a perReceiver one
 * @param {{id:string, label:string}[]} [opts.meters]  compact bars in the strip, e.g. S, PO, SWR, ALC
 * @param {{label:string, href:string, on?:boolean}[]} [opts.views]
 * @param {HTMLElement} [opts.tuner]  the RTTY tuner's element, docked at the side
 * @param {HTMLElement} [opts.decode]  the decoded text, in a box of its own under the
 *        tuner while DECODE is on, with its own POP OUT
 * @param {string} [opts.storagePrefix='rwc.rttyContest.']  this view's own choices
 * @param {string} [opts.themePrefix='rwc.radioPanel.']  the radio panel's: the theme is read from it
 * @param {string} [opts.fontBase]
 * @param {number} [opts.minHz]
 * @param {number} [opts.maxHz]
 * @param {'spectrum'|'waterfall'|'both'} [opts.part]  a pop-out window showing only this
 * @param {() => void} [opts.onDock]  the window's DOCK key
 * @param {(part:string) => string} [opts.windowUrl]  the main page: the window for a part;
 *        without it there are no pop-outs
 * @param {string} [opts.windowName='rtty-contest-']  names the pop-outs, their channels and places
 * @param {(out:boolean) => void} [opts.onTunerOut]  the tuner window opened or closed: the
 *        docked tuner must stand still while it is out, or the two fight over the audio
 * @param {string} [opts.layoutsApi]  the main page: the named-layouts API (the same as
 *        Experimental's); without it there is no LAYOUT
 * @param {(out:boolean) => void} [opts.onDecodeOut]  the decoded text's window opened or
 *        closed: the text here stands still while it is out
 * @param {(on:boolean) => void} [opts.onDecode]  the main page: DECODE was turned on or
 *        off, here or in another window, or at the start; without it there is no DECODE
 */
export function mountContestView(root, opts) {
    injectRadioPanelStyle(document, opts.fontBase);
    if (!document.getElementById('rc-style')) {
        const st = document.createElement('style');
        st.id = 'rc-style';
        st.textContent = CSS;
        document.head.append(st);
    }
    const own = opts.storagePrefix ?? 'rwc.rttyContest.';
    const themePrefix = opts.themePrefix ?? 'rwc.radioPanel.';
    const ids = opts.receivers.map(r => r.id);
    const part = opts.part ?? null;
    const isMain = !part;

    root.classList.add('rp-page', 'rc-page');
    const top = h('div', 'rc-top');
    if (isMain) top.append(h('span', 'rc-title', 'RTTY CONTEST'));

    // The frequency: click it to type one, or turn the wheel over a digit.
    const freqBtn = btn('rc-freq', '', () => editFrequency());
    freqBtn.title = 'Click to type a frequency: 14.08 or 14080 or 14080000. The mouse wheel over a digit moves it up or down';
    const freqBig = h('span'), freqSmall = h('small');
    freqBtn.append(freqBig, freqSmall);
    if (opts.onNudge) wheelDigits(freqBtn, hz => opts.onNudge(receiver, hz));
    const freqIn = h('input', 'rc-freq-in');
    freqIn.type = 'text';
    freqIn.inputMode = 'decimal';
    freqIn.hidden = true;
    freqIn.setAttribute('aria-label', 'Frequency');
    // The mode: a list to pick from when the app gives one (opts.modes),
    // showing the receiver's mode, which may be none of them.
    const modeSel = isMain && opts.modes?.length && opts.onMode ? h('select', 'rc-mode') : null;
    const modeOther = new Option('-', '');
    if (modeSel) {
        modeOther.disabled = true;
        modeSel.append(modeOther);
        for (const m of opts.modes) modeSel.append(new Option(m.label, m.id));
        modeSel.setAttribute('aria-label', 'Mode');
        modeSel.addEventListener('change', () => { if (modeSel.value) opts.onMode(receiver, modeSel.value); });
    }
    const modeChip = modeSel ?? h('span', 'rc-chip', '');
    modeChip.title = modeSel ? opts.modeTitle ?? 'Mode' : 'Mode';
    const toneChip = h('span', 'rc-chip', '');
    toneChip.title = "Mark and shift, as the RTTY tuner has them. A click on the spectrum puts the mark there";
    const txChip = h('span', 'rc-chip', 'RX');
    top.append(freqBtn, freqIn, modeChip, toneChip, txChip);

    const meterRow = h('div', 'rc-meters');
    const meterEls = {};
    for (const m of opts.meters ?? []) {
        const el = h('span', 'rc-meter');
        const fill = h('div', 'rc-fill');
        const bar = h('div', 'rc-bar');
        bar.append(fill);
        const val = h('span', 'rc-val', '');
        el.append(h('span', null, m.label), bar, val);
        meterRow.append(el);
        meterEls[m.id] = { el, fill, val };
    }
    let metersShown = store.get(own + 'meters', '1') === '1';
    const meterKey = btn('rp-spop-key', 'METERS', () => setMetersShown(!metersShown));
    meterKey.title = 'Show or hide the meters';
    if (isMain && opts.meters?.length) top.append(meterRow, meterKey);

    const tabs = h('div', 'rp-spop-tabs');
    const tabEls = {};
    for (const r of opts.receivers) {
        tabEls[r.id] = btn('rp-spop-tab', r.tab, () => setReceiver(r.id, true));
        tabs.append(tabEls[r.id]);
    }
    if (ids.length > 1) top.append(tabs);

    top.append(h('span', 'rc-gap'));
    const spotsBtn = btn('rp-spop-key', 'DX SPOTS', () => setShowSpots(!showSpots));
    spotsBtn.title = 'Show or hide the DX spot tags on the spectrum';
    top.append(spotsBtn);

    if (part) {
        const dock = btn('rp-spop-key on rc-dock', 'DOCK', () => opts.onDock?.());
        dock.title = 'Put this back in the RTTY Contest page and close the window';
        top.append(dock);
    }

    // LAYOUT: the saved layouts, and saving one. Filled from the server.
    const layoutSel = isMain && opts.layoutsApi ? h('select', 'rc-views rc-layouts') : null;
    if (layoutSel) {
        layoutSel.title = 'Layouts: the sizes, the choices on this page, and which windows are open and where. Saved on the server';
        layoutSel.setAttribute('aria-label', 'Layout');
        top.append(layoutSel);
    }

    if (isMain && opts.views?.length) {
        const views = h('select', 'rc-views');
        views.title = 'Views';
        views.setAttribute('aria-label', 'Views');
        for (const v of opts.views) {
            const o = new Option(v.label, v.href);
            if (v.on) o.selected = true;
            views.append(o);
        }
        views.addEventListener('change', () => { location.href = views.value; });
        top.append(views);
    }

    // The scope row. Each control acts on the receiver on screen.
    const scope = h('div', 'rc-scope');
    const key = (text, title, onClick, cls = '') => { const b = btn('rp-spop-key ' + cls, text, onClick); b.title = title; return b; };
    const refVal = h('span', 'rc-num', '');
    const rangeVal = h('span', 'rc-num', '');
    const refGrp = h('span', 'rc-grp');
    refGrp.append(h('span', 'rc-lbl', 'REF'),
        key('\u2212', 'Lower the bottom of the trace: more of what is under the noise floor', () => nudgeRef(-REF_STEP_DB), 'rc-pm rc-ref-dn'),
        refVal,
        key('+', 'Raise the bottom of the trace: less of the noise', () => nudgeRef(REF_STEP_DB), 'rc-pm rc-ref-up'));
    refGrp.title = 'Where the bottom of the trace sits, in dB from the noise floor';
    const autoKey = key('AUTO', 'The floor follows the noise. Off, it stays where it is now', () => setLevel({ auto: !levels[receiver].auto }), 'rc-auto');
    const rangeGrp = h('span', 'rc-grp');
    rangeGrp.append(h('span', 'rc-lbl', 'RANGE'),
        key('\u2212', 'Less range: weak signals stand taller', () => nudgeRange(-RANGE_STEP_DB), 'rc-pm rc-range-dn'),
        rangeVal,
        key('+', 'More range: strong signals fit', () => nudgeRange(RANGE_STEP_DB), 'rc-pm rc-range-up'));
    rangeGrp.title = 'dB from the bottom of the trace to the top';
    const fitKey = key('FIT', 'Fit the range to the strongest signal of the last few seconds (or double-click the bar between the spectrum and the waterfall)', () => fitNow(), 'rc-fit');
    const swapKey = key('WF ON TOP', 'Put the waterfall above the spectrum', () => setOrder(!wfFirst), 'rc-swap');
    const stepSel = h('select', 'rc-step');
    stepSel.title = 'How far one notch of the mouse wheel moves the dial';
    stepSel.setAttribute('aria-label', 'Wheel step');
    for (const hz of STEP_CHOICES_HZ) stepSel.append(new Option(`STEP ${hz >= 1000 ? hz / 1000 + ' kHz' : hz + ' Hz'}`, String(hz)));
    let stepHz = Number(store.get(own + 'step', String(DEFAULT_STEP_HZ)));
    if (!STEP_CHOICES_HZ.includes(stepHz)) stepHz = DEFAULT_STEP_HZ;
    stepSel.value = String(stepHz);
    stepSel.addEventListener('change', () => { stepHz = Number(stepSel.value); store.set(own + 'step', String(stepHz)); });
    const traceSel = h('select', 'rc-step rc-trace');
    traceSel.title = 'How thick the spectrum trace is drawn';
    traceSel.setAttribute('aria-label', 'Trace width');
    for (const px of TRACE_CHOICES_PX) traceSel.append(new Option(`TRACE ${px}`, String(px)));
    let tracePx = Number(store.get(own + 'trace', String(DEFAULT_TRACE_PX)));
    if (!TRACE_CHOICES_PX.includes(tracePx)) tracePx = DEFAULT_TRACE_PX;
    traceSel.value = String(tracePx);
    traceSel.addEventListener('change', () => setTrace(Number(traceSel.value), true));
    if (part !== 'waterfall') scope.append(refGrp, autoKey, rangeGrp, fitKey);
    if (isMain || part === 'both') scope.append(swapKey);
    scope.append(stepSel);
    if (part !== 'waterfall') scope.append(traceSel);
    if (isMain && opts.bands?.length && opts.bandRows?.length) {
        scope.append(key('BANDS', opts.receivers.length > 1
            ? `Choose the band keys each row shows, and whether ${opts.receivers[1].tab.toUpperCase()} shows`
            : 'Choose the band keys that show', () => chooseBands(), 'rc-bandset-key'));
    }

    // The pop-out keys: each opens its part in a window, or docks it again.
    const popGrp = h('span', 'rc-pop');
    const popBtns = {};
    if (isMain && opts.windowUrl) {
        popGrp.append(h('span', 'rc-lbl', 'POP OUT'));
        for (const p of ['spectrum', 'waterfall', 'both']) {
            popBtns[p] = key(POPOUT_LABELS[p], '', () => togglePopout(p), 'rc-pop-' + p);
            popGrp.append(popBtns[p]);
        }
        popGrp.style.marginLeft = 'auto';
        scope.append(popGrp);
    }

    const body = h('div', 'rc-body');
    const stack = h('div', 'rc-stack');
    const outNote = h('div', 'rc-out');
    outNote.hidden = true;
    outNote.append(h('span', null, 'The spectrum and the waterfall are in their own windows'),
        key('DOCK THEM', 'Bring the spectrum and the waterfall back here', () => { for (const p of ['spectrum', 'waterfall', 'both']) hosts[p]?.recall(); }, 'on rc-dock-all'));
    // The side column: the tuner, and under it the decoded text in a box of
    // its own, so each can go out to a window by itself.
    const side = h('div', 'rc-side');
    const tunerSlot = h('div', 'rc-tuner');
    const decodeSlot = h('div', 'rc-dbox');
    side.append(tunerSlot);
    body.append(stack, side);
    // Windows the browser would not open, with a key for each: one click, one window.
    const blockedBar = h('div', 'rc-blocked');
    blockedBar.hidden = true;
    // The band keys: a row per receiver, on the main page.
    const bandsEl = h('div', 'rc-bands');
    const bandKeys = {};
    const bandRowEls = {};
    const hasBands = isMain && !!opts.bands?.length && !!opts.bandRows?.length;
    if (hasBands) {
        for (const r of opts.bandRows) {
            const row = h('div', 'rc-brow rc-brow-' + r.id);
            row.append(h('span', 'rc-lbl', r.label));
            bandKeys[r.id] = {};
            for (const b of opts.bands) {
                const k = key(b.label, `${r.label}: to the RTTY part of ${b.label}`, () => opts.onBand?.(r.id, b.id), 'rc-band');
                k.dataset.band = b.id;
                bandKeys[r.id][b.id] = k;
                row.append(k);
            }
            bandsEl.append(row);
            bandRowEls[r.id] = row;
        }
    }
    // What the operator chose not to see: band keys, per row, and the
    // receivers after the first. A hidden receiver loses its tab and the
    // band row of the same id, and the page stays off it. Kept as what is
    // hidden, so a band the app adds later shows.
    const readSet = (k, keep) => {
        try { const a = JSON.parse(store.get(own + k, '[]')); return new Set(Array.isArray(a) ? a.filter(keep) : []); } catch { return new Set(); }
    };
    const hiddenRx = readSet('hiddenReceivers', id => ids.indexOf(id) > 0);
    const hiddenBands = {};
    for (const r of opts.bandRows ?? []) hiddenBands[r.id] = readSet('hiddenBands.' + r.id, b => typeof b === 'string');
    function drawHidden() {
        for (const [rowId, keys] of Object.entries(bandKeys)) {
            for (const [b, k] of Object.entries(keys)) k.hidden = hiddenBands[rowId].has(b);
            bandRowEls[rowId].hidden = hiddenRx.has(rowId) || Object.values(keys).every(k => k.hidden);
        }
        for (const [id, el] of Object.entries(tabEls)) el.hidden = hiddenRx.has(id);
        tabs.hidden = ids.filter(id => !hiddenRx.has(id)).length < 2;
        bandsEl.hidden = Object.values(bandRowEls).every(el => el.hidden);
    }
    root.append(top);
    if (bandsEl.childElementCount) root.append(bandsEl);
    root.append(scope, blockedBar, body);

    // The tuner, with its own bar: POP OUT into a window, ON TOP into the
    // always-on-top window. Out either way, a note keeps its place.
    let tunerPop = null, onTopKey = null, decodeKey = null, pip = null;
    let decoding = store.get(own + 'decode', '0') === '1';
    const tunerNote = h('div', 'rc-tnote');
    tunerNote.hidden = true;
    if (isMain && opts.tuner) {
        const tbar = h('div', 'rc-tbar');
        if (opts.windowUrl) {
            tunerPop = key(POPOUT_LABELS.tuner, '', () => togglePopout('tuner'), 'rc-pop-tuner');
            tbar.append(tunerPop);
        }
        if (opts.onDecode) {
            decodeKey = key('DECODE', 'Show the decoded text with the tuner, or stop decoding and hide it',
                () => setDecode(!decoding, true), 'rc-decode');
            tbar.append(decodeKey);
        }
        if (pipSupported()) {
            onTopKey = key('ON TOP', 'Keep the tuner on top of every other window, the logging program too. Close that window to put it back',
                () => toggleOnTop(), 'rc-ontop');
            tbar.append(onTopKey);
        }
        if (tbar.childElementCount) tunerSlot.append(tbar);
        tunerSlot.append(opts.tuner, tunerNote);
    }
    // The settings an operator reaches for during a contest - power, the IF
    // width and shift, as Bruce VK2RT asked (2026-10-26) - in a box under the
    // tuner. The app names them (opts.controls) and writes them
    // (opts.onControl); what the radio has comes back through setControl, per
    // receiver for a setting each receiver has. A slider writes when it is
    // let go, a wheel notch over it a moment after the last notch, and a
    // double-click puts it to its reset value where it has one.
    const ctlBox = h('div', 'rc-ctl');
    const ctls = {};
    const ctlVals = {};
    const ctlKey = c => (c.perReceiver ? receiver : '');
    const ctlText = (c, v) => (v == null || !Number.isFinite(Number(v)) ? '-' : c.format ? c.format(Number(v)) : `${v}${c.unit ?? ''}`);
    if (isMain && opts.controls?.length && opts.onControl) {
        for (const c of opts.controls) {
            const row = h('label', 'rc-crow rc-c-' + c.id);
            row.title = c.title ?? '';
            const ctl = { c, row, val: h('span', 'rc-num', ''), held: false, timer: 0, opts: '' };
            const send = v => opts.onControl(c.id, v, c.perReceiver ? receiver : null);
            if (c.kind === 'list') {
                ctl.input = h('select', 'rc-step');
                ctl.input.addEventListener('change', () => { if (ctl.input.value !== '') send(ctl.input.value); });
                row.append(h('span', 'rc-lbl', c.label), ctl.input);
            } else {
                const r = ctl.input = h('input');
                r.type = 'range';
                r.min = String(c.min); r.max = String(c.max); r.step = String(c.step ?? 1);
                const now = () => Number(r.value);
                r.addEventListener('pointerdown', () => { ctl.held = true; });
                const letGo = () => { ctl.held = false; };
                r.addEventListener('pointerup', letGo);
                r.addEventListener('pointercancel', letGo);
                r.addEventListener('input', () => { ctl.val.textContent = ctlText(c, now()); });
                r.addEventListener('change', () => { letGo(); send(now()); });
                r.addEventListener('wheel', e => {
                    e.preventDefault();
                    const step = Number(r.step) || 1;
                    r.value = String(Math.max(Number(r.min), Math.min(Number(r.max), now() + (e.deltaY < 0 ? step : -step))));
                    ctl.val.textContent = ctlText(c, now());
                    ctl.held = true;
                    clearTimeout(ctl.timer);
                    ctl.timer = setTimeout(() => { ctl.held = false; send(now()); }, 300);
                }, { passive: false });
                if (c.reset != null) r.addEventListener('dblclick', () => { r.value = String(c.reset); ctl.val.textContent = ctlText(c, c.reset); send(Number(c.reset)); });
                row.append(h('span', 'rc-lbl', c.label), r, ctl.val);
            }
            ctls[c.id] = ctl;
            ctlBox.append(row);
        }
        side.append(ctlBox);
    }
    function drawControls() {
        for (const ctl of Object.values(ctls)) {
            const e = ctlVals[ctl.c.id]?.[ctlKey(ctl.c)];
            if (ctl.c.kind === 'list') {
                const list = e?.options ?? [];
                const sig = JSON.stringify(list);
                if (sig !== ctl.opts) {
                    ctl.opts = sig;
                    ctl.input.replaceChildren(...(list.length ? list.map(o => new Option(o.label, o.id)) : [new Option('-', '')]));
                }
                ctl.input.disabled = !list.length;
                if (document.activeElement !== ctl.input) ctl.input.value = list.some(o => o.id === String(e?.value)) ? String(e.value) : '';
            } else {
                if (e?.max != null) ctl.input.max = String(e.max);
                if (ctl.held) continue;
                if (e?.value != null) ctl.input.value = String(e.value);
                ctl.val.textContent = ctlText(ctl.c, e?.value);
            }
        }
    }
    // The decoded text, under the tuner while DECODE is on: POP OUT on its own
    // bar, and a note keeps its place while it is out.
    let decodePop = null;
    const decodeNote = h('div', 'rc-tnote');
    decodeNote.hidden = true;
    if (isMain && opts.decode && decodeKey) {
        const dbar = h('div', 'rc-tbar');
        if (opts.windowUrl) {
            decodePop = key(POPOUT_LABELS.decode, '', () => togglePopout('decode'), 'rc-pop-decode');
            dbar.append(decodePop);
        }
        if (dbar.childElementCount) decodeSlot.append(dbar);
        decodeSlot.append(opts.decode, decodeNote);
        side.append(decodeSlot);
    }
    side.hidden = !opts.tuner || !isMain;

    // The boxes' sizes. Each is kept, and applied only here on the main page:
    // a window is its part and nothing else, the size of the window.
    const sizes = {};
    for (const name of Object.keys(BOX_SIZES)) sizes[name] = tidyBoxSize(name, store.get(own + name, ''));
    const tunerShown = () => side.offsetWidth > 0;
    const decodeDocked = () => decodeSlot.offsetHeight > 0;
    const room = {
        w: () => body.clientWidth - 2 * BODY_GAP,
        h: () => body.clientHeight - 2 * BODY_GAP,
    };
    const boxes = {
        stackWidth: { el: stack, axis: 'x', apply: v => { stack.style.width = v == null ? '' : v + 'px'; stack.classList.toggle('sized-w', v != null); },
            // The spectrum's right edge pushes the tuner along, not into it.
            max: () => room.w() - (tunerShown() ? side.offsetWidth + BODY_GAP : 0) },
        stackHeight: { el: stack, axis: 'y', apply: v => { stack.style.height = v == null ? '' : v + 'px'; stack.classList.toggle('sized-h', v != null); },
            max: () => room.h() },
        tunerWidth: { el: opts.tuner, axis: 'x', apply: v => { if (v == null) root.style.removeProperty('--rc-tuner-w'); else root.style.setProperty('--rc-tuner-w', v + 'px'); },
            // Wider than the room on its right squeezes the spectrum, down to its least.
            max: () => room.w() - BOX_SIZES.stackWidth.min - BODY_GAP },
        tunerHeight: { el: tunerSlot, axis: 'y', apply: v => { tunerSlot.style.height = v == null ? '' : v + 'px'; tunerSlot.classList.toggle('sized-h', v != null); },
            // Taller squeezes the decoded text under it, down to its least,
            // and never pushes the controls box off the bottom.
            max: () => room.h() - (decodeDocked() ? DECODE_MIN_H + BODY_GAP : 0) - (ctlBox.offsetHeight ? ctlBox.offsetHeight + BODY_GAP : 0) },
    };
    function setSize(name, v, keep) {
        sizes[name] = tidyBoxSize(name, v);
        if (isMain) boxes[name].apply(sizes[name]);
        if (keep) store.set(own + name, sizes[name] == null ? '' : String(sizes[name]));
    }
    const sizeNow = name => {
        const r = boxes[name].el?.getBoundingClientRect();
        return Math.round((boxes[name].axis === 'x' ? r?.width : r?.height) || sizes[name] || BOX_SIZES[name].min);
    };
    const sizeMax = name => Math.max(BOX_SIZES[name].min, Math.min(BOX_SIZES[name].max ?? Infinity, boxes[name].max()));
    // A grab on a box's edge or corner: dragged, it sizes the box under the
    // pointer; the arrow keys size it in steps; a double-click puts it back
    // to filling its room.
    function grab(where, names, label) {
        const g = h('div', 'rc-grab rc-grab-' + where);
        g.tabIndex = 0;
        g.setAttribute('aria-label', label);
        if (names.length === 1) {
            g.setAttribute('role', 'separator');
            g.setAttribute('aria-orientation', boxes[names[0]].axis === 'x' ? 'vertical' : 'horizontal');
        }
        g.title = `Drag to size the ${label.toLowerCase()}. Double-click to put it back`;
        g.addEventListener('pointerdown', e => {
            if (e.button !== 0) return;
            e.preventDefault();
            const from = names.map(n => ({ n, p0: boxes[n].axis === 'x' ? e.clientX : e.clientY, v0: sizeNow(n), max: sizeMax(n) }));
            g.setPointerCapture(e.pointerId);
            g.classList.add('drag');
            const move = ev => {
                for (const f of from) setSize(f.n, Math.min(f.max, f.v0 + (boxes[f.n].axis === 'x' ? ev.clientX : ev.clientY) - f.p0), false);
            };
            const end = () => {
                g.classList.remove('drag');
                g.removeEventListener('pointermove', move);
                g.removeEventListener('pointerup', end);
                g.removeEventListener('pointercancel', end);
                for (const f of from) setSize(f.n, sizes[f.n], true);
            };
            g.addEventListener('pointermove', move);
            g.addEventListener('pointerup', end);
            g.addEventListener('pointercancel', end);
        });
        g.addEventListener('dblclick', () => { for (const n of names) setSize(n, null, true); });
        g.addEventListener('keydown', e => {
            const d = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key];
            if (!d) return;
            let did = false;
            for (const n of names) {
                const step = d[boxes[n].axis === 'x' ? 0 : 1];
                if (!step) continue;
                setSize(n, Math.min(sizeMax(n), sizeNow(n) + step * GRAB_KEY_STEP), true);
                did = true;
            }
            if (did) e.preventDefault();
        });
        return g;
    }
    const stackGrabs = [], tunerGrabs = [];
    if (isMain) {
        stackGrabs.push(grab('r', ['stackWidth'], 'Spectrum width'), grab('b', ['stackHeight'], 'Spectrum height'),
            grab('rb', ['stackWidth', 'stackHeight'], 'Spectrum size'));
        stack.append(...stackGrabs);
        if (opts.tuner) {
            tunerGrabs.push(grab('r', ['tunerWidth'], 'Tuner width'), grab('b', ['tunerHeight'], 'Tuner height'),
                grab('rb', ['tunerWidth', 'tunerHeight'], 'Tuner size'));
            tunerSlot.append(...tunerGrabs);
        }
    }

    const split = Number(store.get(own + 'split', '0.5')) || 0.5;
    let wfFirst = store.get(own + 'wfFirst', '0') === '1';
    const levels = {};
    for (const id of ids) {
        let saved = null;
        try { saved = JSON.parse(store.get(own + 'levels.' + id, 'null')); } catch { /* start again */ }
        levels[id] = tidyLevels(saved);
    }
    const panes = {};
    const holders = {};
    for (const r of opts.receivers) {
        const holder = h('div', 'rc-holder');
        stack.append(holder);
        const pane = new SpectrumPane(holder, {
            onTune: opts.onTune && (hz => opts.onTune(r.id, hz)),
            tuneTo: opts.onTune && opts.tuneTo && (hz => opts.tuneTo(r.id, hz)),
            onStep: opts.onStep && (dir => opts.onStep(r.id, dir)),
            // Bruce VK2RT: arrows that move the dial one span, to look along the band.
            onJump: opts.onFrequencyEntry && (hz => {
                if (hz >= (opts.minHz ?? 30_000) && hz <= (opts.maxHz ?? 75_000_000)) opts.onFrequencyEntry(r.id, Math.round(hz));
            }),
            // The span is picked in the pane's label, beside the frequency it goes with.
            spans: opts.spans,
            onSpan: opts.onSpan && (hz => opts.onSpan(r.id, hz)),
            grip: true,
            dbScale: true,
            waterfallFirst: wfFirst,
            onFit: () => keepRange(r.id),
            // One split for both receivers: switching Main to Sub should not move it.
            onSplit: f => {
                store.set(own + 'split', String(f));
                for (const p of Object.values(panes)) if (p !== pane) p.setSplit(f);
            },
        });
        pane.setName(r.name);
        pane.setSplit(split);
        const l = levels[r.id];
        pane.setLevels({ auto: l.auto, ref: l.ref });
        if (l.range != null) pane.setSettings({ range: l.range });
        panes[r.id] = pane;
        holders[r.id] = holder;
    }

    function setTrace(px, keep) {
        if (!TRACE_CHOICES_PX.includes(px)) return;
        tracePx = px;
        traceSel.value = String(px);
        if (keep) store.set(own + 'trace', String(px));
        for (const p of Object.values(panes)) p.setTraceWidth(px);
    }
    setTrace(tracePx, false);

    stack.append(outNote);
    for (const p of Object.values(panes)) p.setParts(part ?? 'both');

    // What the strip shows, per receiver; only the selected one's is drawn.
    const shown = {};
    for (const id of ids) shown[id] = { hz: 0, mode: '' };

    let receiver = store.get(own + 'receiver', ids[0]);
    let showSpots = store.get(own + 'spots', '1') === '1';

    function drawStrip() {
        const s = shown[receiver];
        const f = formatFrequency(s.hz);
        if (s.hz > 0) { freqDigits(freqBig, f.big, 1000); freqDigits(freqSmall, f.small, 1); }
        else { freqBig.textContent = '-.---'; freqSmall.textContent = '.---'; }
        if (!modeSel) { modeChip.textContent = s.mode || '-'; return; }
        const known = opts.modes.some(m => m.id === s.mode);
        modeOther.textContent = s.mode || '-';
        modeOther.hidden = known;
        modeSel.value = known ? s.mode : '';
    }
    function editFrequency() {
        freqIn.value = '';
        freqIn.placeholder = shown[receiver].hz ? (shown[receiver].hz / 1000).toFixed(3) : 'kHz';
        freqBtn.hidden = true;
        freqIn.hidden = false;
        freqIn.focus();
    }
    function endEdit(take) {
        if (freqIn.hidden) return;
        const hz = take ? parseFrequencyEntry(freqIn.value, opts.minHz, opts.maxHz) : null;
        freqIn.hidden = true;
        freqBtn.hidden = false;
        if (hz) opts.onFrequencyEntry?.(receiver, hz);
    }
    freqIn.addEventListener('keydown', e => {
        if (e.key === 'Enter') { e.preventDefault(); endEdit(true); }
        else if (e.key === 'Escape') { e.preventDefault(); endEdit(false); }
    });
    freqIn.addEventListener('blur', () => endEdit(false));

    // The levels. The range lives in the pane's settings; it is kept here.
    function saveLevels(id) { store.set(own + 'levels.' + id, JSON.stringify(levels[id])); }
    function shownBottom(id) {
        const lv = panes[id].levels;
        return lv.ref ?? (lv.floor != null ? Math.round(lv.lo - lv.floor) : null);
    }
    function drawScope() {
        const l = levels[receiver];
        const b = shownBottom(receiver);
        refVal.textContent = b == null ? '-' : (b > 0 ? '+' : '') + b;
        rangeVal.textContent = String(panes[receiver].settings.range);
        autoKey.classList.toggle('on', l.auto);
        autoKey.setAttribute('aria-pressed', l.auto ? 'true' : 'false');
        swapKey.classList.toggle('on', wfFirst);
        swapKey.setAttribute('aria-pressed', wfFirst ? 'true' : 'false');
    }
    function setLevel(patch) {
        const l = levels[receiver] = tidyLevels({ ...levels[receiver], ...patch });
        panes[receiver].setLevels({ auto: l.auto, ref: l.ref });
        if (l.range != null) panes[receiver].setSettings({ range: l.range });
        saveLevels(receiver);
        drawScope();
    }
    function nudgeRef(db) {
        const b = shownBottom(receiver);
        if (b != null) setLevel({ ref: b + db });
    }
    function nudgeRange(db) { setLevel({ range: panes[receiver].settings.range + db }); }
    function keepRange(id) {
        levels[id] = tidyLevels({ ...levels[id], range: panes[id].settings.range });
        saveLevels(id);
        if (id === receiver) drawScope();
    }
    function fitNow() { if (panes[receiver].fit() != null) keepRange(receiver); }
    function setOrder(on) {
        wfFirst = on;
        store.set(own + 'wfFirst', on ? '1' : '0');
        for (const p of Object.values(panes)) p.setOrder(on);
        drawScope();
    }
    // The pop-outs (main page only).
    const hosts = {};
    const out = { spectrum: false, waterfall: false, both: false, tuner: false, decode: false };
    function drawOut() {
        // A window shows its own part; the main page what has not gone out.
        const left = part ?? partsLeft(out);
        for (const p of Object.values(panes)) p.setParts(left);
        outNote.hidden = left !== 'none';
        for (const g of stackGrabs) g.hidden = left === 'none';
        for (const [p, b] of Object.entries(popBtns)) {
            b.classList.toggle('on', out[p]);
            b.title = out[p] ? `The ${p === 'both' ? 'spectrum and waterfall are' : p + ' is'} in a window: click to dock`
                : `Open the ${p === 'both' ? 'spectrum and waterfall' : p} in a window of its own, for another monitor`;
        }
        if (opts.tuner) {
            // On top, the tuner is in the PiP window: hiding it would hide it there.
            opts.tuner.hidden = out.tuner && !pip;
            tunerNote.hidden = !(out.tuner || pip);
            tunerNote.textContent = pip ? 'The tuner is on top, in its own window. Close that window to put it back here.'
                : 'The tuner is in a window of its own. DOCK puts it back here.';
            for (const g of tunerGrabs) g.hidden = out.tuner || !!pip;
        }
        if (tunerPop) {
            tunerPop.classList.toggle('on', out.tuner);
            tunerPop.textContent = out.tuner ? 'DOCK' : POPOUT_LABELS.tuner;
            tunerPop.title = out.tuner ? 'Put the tuner back here and close its window' : 'Open the tuner in a window of its own, for another monitor';
            tunerPop.disabled = !!pip;
        }
        if (onTopKey) {
            onTopKey.classList.toggle('on', !!pip);
            onTopKey.disabled = out.tuner;
        }
        if (decodeSlot.parentNode) {
            // Shown only with DECODE on. Out, the box is its bar and the note,
            // and the tuner has the column again.
            decodeSlot.hidden = !decoding;
            decodeSlot.classList.toggle('out', out.decode);
            side.classList.toggle('decoding', decoding && !out.decode);
            opts.decode.hidden = out.decode;
            decodeNote.hidden = !out.decode;
            decodeNote.textContent = 'The decoded text is in a window of its own. DOCK puts it back here.';
        }
        if (decodePop) {
            decodePop.classList.toggle('on', out.decode);
            decodePop.textContent = out.decode ? 'DOCK' : POPOUT_LABELS.decode;
            decodePop.title = out.decode ? 'Put the decoded text back here and close its window' : 'Open the decoded text in a window of its own, for another monitor';
        }
    }
    // A part is in one window at a time: BOTH takes the single ones back, and
    // either single one takes BOTH back. Also run when a window turns up by
    // itself (a reload, an address typed in), not only on a POP OUT click.
    function recallClashing(p) {
        const clash = p === 'both' ? ['spectrum', 'waterfall'] : p === 'tuner' || p === 'decode' ? [] : ['both'];
        for (const c of clash) if (hosts[c]?.isOpen) hosts[c].recall();
    }
    function togglePopout(p) {
        const host = hosts[p];
        if (!host) return;
        if (host.isOpen) { host.recall(); return; }
        recallClashing(p);
        if (!host.open()) showBlocked([p]);
        else showBlocked(blocked.filter(b => b !== p));
    }

    // Blocked windows. A browser opens one window per click unless pop-ups
    // are allowed for the address, so a layout with three windows out may
    // get only the first: the rest wait here, a key each.
    let blocked = [];
    function showBlocked(parts) {
        blocked = parts.filter(p => hosts[p] && !hosts[p].isOpen);
        blockedBar.replaceChildren();
        blockedBar.hidden = blocked.length === 0;
        if (!blocked.length) return;
        blockedBar.append(h('span', null, blocked.length > 1
            ? 'The browser opened one window for that click. Click each to open the rest:'
            : 'The browser blocked a window. Click to open it, or allow pop-ups for this address:'));
        for (const p of blocked) {
            const what = { both: 'spectrum and waterfall', decode: 'decoded text' }[p] ?? p;
            blockedBar.append(key({ tuner: 'TUNER', decode: 'DECODE' }[p] ?? POPOUT_LABELS[p], `Open the ${what} window`,
                () => togglePopout(p), 'on rc-open-' + p));
        }
        blockedBar.append(key('×', 'Leave them docked', () => showBlocked([]), 'rc-pm rc-blocked-close'));
    }
    async function toggleOnTop() {
        if (pip) { pip.dock(); return; }
        if (out.tuner) return;
        try {
            const rc = opts.tuner.getBoundingClientRect();
            pip = await pipDock(opts.tuner, {
                width: Math.max(320, rc.width), height: Math.max(300, rc.height),
                // The host styles the tuner by this to fill the window there.
                className: 'rc-on-top',
                onDock: () => { pip = null; drawOut(); },
            });
            drawOut();
        } catch (e) {
            pip = null;
            console.warn('rtty contest: on top', e);
            alert('The browser would not open an always-on-top window: ' + (e?.message ?? e));
        }
    }
    const winName = opts.windowName ?? 'rtty-contest-';
    if (isMain && opts.windowUrl) {
        for (const p of POPOUT_PARTS) {
            if (p === 'tuner' && !opts.tuner) continue;
            if (p === 'decode' && !decodePop) continue;
            hosts[p] = new PopoutHost({
                name: winName + p,
                url: opts.windowUrl(p),
                defaultSize: POPOUT_SIZES[p],
                onChange: o => {
                    if (o) recallClashing(p);
                    out[p] = o;
                    drawOut();
                    if (p === 'tuner') opts.onTunerOut?.(o);
                    if (p === 'decode') opts.onDecodeOut?.(o);
                },
            }).start();
        }
    }
    drawOut();

    // The other windows of this view - pop-outs, another tab - made a choice:
    // follow it. A storage event comes only from another window, and only
    // when the value changed, so following it does not echo back.
    window.addEventListener('storage', e => {
        const k = e.key, v = e.newValue;
        if (!k?.startsWith(own) || v == null) return;
        const name = k.slice(own.length);
        if (name === 'receiver') setReceiver(v, false);
        else if (name === 'spots') setShowSpots(v === '1');
        else if (name === 'wfFirst') setOrder(v === '1');
        else if (name === 'split') { const f = Number(v); if (f > 0) for (const p of Object.values(panes)) p.setSplit(f); }
        else if (name in BOX_SIZES) setSize(name, v, false);
        else if (name === 'decode') setDecode(v === '1', false);
        else if (name === 'trace') setTrace(Number(v), false);
        else if (name === 'step') { const n = Number(v); if (STEP_CHOICES_HZ.includes(n)) { stepHz = n; stepSel.value = v; } }
        else if (name.startsWith('levels.')) {
            const id = name.slice('levels.'.length);
            if (!panes[id]) return;
            let l = null;
            try { l = JSON.parse(v); } catch { return; }
            levels[id] = tidyLevels(l);
            panes[id].setLevels({ auto: levels[id].auto, ref: levels[id].ref });
            if (levels[id].range != null) panes[id].setSettings({ range: levels[id].range });
            if (id === receiver) drawScope();
        }
    });

    // The default bottom has no number until the first frames find the floor.
    const scopeTimer = setInterval(() => {
        if (shownBottom(receiver) == null) return;
        drawScope();
        clearInterval(scopeTimer);
    }, 500);

    function setReceiver(id, byUser) {
        if (!ids.includes(id) || hiddenRx.has(id)) id = ids[0];
        receiver = id;
        store.set(own + 'receiver', id);
        for (const [k, el] of Object.entries(tabEls)) el.classList.toggle('on', k === id);
        for (const [k, el] of Object.entries(holders)) el.hidden = k !== id;
        drawStrip();
        drawScope();
        drawControls();
        if (byUser) opts.onReceiver?.(id);
    }
    function setShowSpots(on) {
        showSpots = on;
        store.set(own + 'spots', on ? '1' : '0');
        spotsBtn.classList.toggle('on', on);
        spotsBtn.setAttribute('aria-pressed', on ? 'true' : 'false');
        for (const p of Object.values(panes)) p.setShowSpots(on);
    }
    function setDecode(on, keep) {
        decoding = on;
        if (!decodeKey) return;
        if (keep) store.set(own + 'decode', on ? '1' : '0');
        decodeKey.classList.toggle('on', on);
        decodeKey.setAttribute('aria-pressed', on ? 'true' : 'false');
        // Off, the decoded text's window goes too: there is nothing for it to show.
        if (!on && hosts.decode?.isOpen) hosts.decode.recall();
        drawOut();
        opts.onDecode(on);
    }
    function setMetersShown(on) {
        metersShown = on;
        store.set(own + 'meters', on ? '1' : '0');
        meterRow.hidden = !on;
        meterKey.classList.toggle('on', on);
    }
    // Layouts: what this page is now, and putting one back.
    function currentLayout() {
        const places = {};
        for (const p of Object.keys(hosts)) {
            const g = popoutPlace(winName + p);
            if (g) places[p] = g;
        }
        return {
            kind: 'rtty-contest',
            version: 1,
            ...sizes,
            meters: metersShown, spots: showSpots, wfFirst, decode: decoding,
            split: Number(store.get(own + 'split', '0.5')) || 0.5,
            step: stepHz,
            trace: tracePx,
            out: { ...out },
            places,
        };
    }
    function applyLayout(raw) {
        const l = tidyLayout(raw);
        for (const name of Object.keys(BOX_SIZES)) setSize(name, l[name], true);
        setMetersShown(l.meters);
        setShowSpots(l.spots);
        setOrder(l.wfFirst);
        if (decodeKey && l.decode !== decoding) setDecode(l.decode, true);
        store.set(own + 'split', String(l.split));
        for (const p of Object.values(panes)) p.setSplit(l.split);
        stepHz = l.step;
        stepSel.value = String(stepHz);
        store.set(own + 'step', String(stepHz));
        setTrace(l.trace, true);
        // The windows: those not in the layout go back; the rest open where
        // the layout had them. One that is open already stays where it is.
        for (const [p, host] of Object.entries(hosts)) {
            if (l.places[p]) setPopoutPlace(winName + p, l.places[p]);
            if (host.isOpen && !l.out[p]) host.recall();
        }
        if (pip && l.out.tuner) pip.dock();
        const fail = [];
        for (const [p, host] of Object.entries(hosts)) {
            if (l.out[p] && (p !== 'decode' || decoding) && !host.isOpen && !host.open()) fail.push(p);
        }
        showBlocked(fail);
    }

    let layouts = [], activeLayout = null;
    const layoutApi = opts.layoutsApi;
    async function api(path, method = 'GET', bodyObj) {
        const res = await fetch(layoutApi + path, {
            method,
            headers: bodyObj ? { 'Content-Type': 'application/json', Accept: 'application/json' } : { Accept: 'application/json' },
            body: bodyObj ? JSON.stringify(bodyObj) : undefined,
        });
        const data = await res.json().catch(() => null);
        if (!res.ok) throw new Error(data?.error || `The server said ${res.status}.`);
        return data;
    }
    async function loadLayouts() {
        if (!layoutSel) return;
        try {
            const snap = await api('');
            layouts = Array.isArray(snap?.layouts) ? snap.layouts : [];
            activeLayout = layouts.find(l => l.id === snap?.activeId) ?? null;
        } catch (e) {
            console.warn('rtty contest: layouts', e);
            layouts = [];
            activeLayout = null;
        }
        drawLayouts();
    }
    function drawLayouts() {
        const head = new Option(activeLayout ? 'LAYOUT: ' + activeLayout.name : 'LAYOUT', '');
        const use = document.createElement('optgroup');
        use.label = 'Use';
        for (const l of layouts) use.append(new Option(l.name, 'use:' + l.id));
        use.append(new Option('Standard', 'standard'));
        const save = document.createElement('optgroup');
        save.label = 'Save';
        save.append(new Option('Save as a new layout…', 'save-new'));
        if (activeLayout) save.append(new Option(`Save over “${activeLayout.name}”`, 'save-over'));
        save.append(new Option('Manage layouts…', 'manage'));
        layoutSel.replaceChildren(head, use, save);
        layoutSel.value = '';
    }
    async function onLayoutChoice(v) {
        layoutSel.value = '';
        try {
            if (v.startsWith('use:')) {
                const id = v.slice(4);
                // The windows open after this fetch. A browser lets a click
                // open a window for a few seconds after it, which is plenty
                // here; any it blocks wait in the bar (showBlocked).
                const d = await api('/' + encodeURIComponent(id));
                applyLayout(d?.layout);
                await api('/active', 'PUT', { id });
            } else if (v === 'standard') {
                applyLayout({});
                await api('/active', 'PUT', { id: '__default__' });
            } else if (v === 'save-new') {
                const name = prompt('A name for this layout:', '')?.trim();
                if (!name) return;
                await api('', 'POST', { name: name.slice(0, 40), layout: currentLayout(), setActive: true });
            } else if (v === 'save-over' && activeLayout) {
                if (!confirm(`Save what is on screen now as “${activeLayout.name}”?`)) return;
                await api('/' + encodeURIComponent(activeLayout.id), 'PUT', { layout: currentLayout() });
            } else if (v === 'manage') {
                manageLayouts();
                return;
            } else return;
        } catch (e) {
            alert('Layouts: ' + (e?.message ?? e));
        }
        await loadLayouts();
    }
    let manageDlg = null;
    function manageLayouts() {
        if (!manageDlg) {
            manageDlg = h('dialog', 'rc-manage');
            const head = h('div', 'rc-manage-head');
            head.append(h('span', null, 'RTTY CONTEST LAYOUTS'), key('×', 'Close', () => manageDlg.close(), 'rc-pm'));
            const box = h('div', 'rc-manage-body');
            manageDlg.append(head, box);
            manageDlg.addEventListener('close', () => loadLayouts());
            root.append(manageDlg);
            manageDlg._admin = initFlexLayoutsAdmin(box, {
                apiBase: layoutApi,
                emptyText: 'No saved layouts yet. Arrange the page and its windows, then choose LAYOUT, Save as a new layout.',
                onSelect: id => { manageDlg.close(); onLayoutChoice('use:' + id); },
            });
        } else manageDlg._admin.refresh();
        manageDlg.showModal();
    }
    layoutSel?.addEventListener('change', () => onLayoutChoice(layoutSel.value));

    // BANDS: which band keys show on each row, and whether the receivers
    // after the first show at all.
    let bandsDlg = null;
    function chooseBands() {
        if (!bandsDlg) {
            bandsDlg = h('dialog', 'rc-manage rc-bandset');
            const head = h('div', 'rc-manage-head');
            head.append(h('span', null, 'BANDS'), key('×', 'Close', () => bandsDlg.close(), 'rc-pm'));
            const box = h('div', 'rc-manage-body');
            const tick = (text, checked, onChange, cls) => {
                const l = h('label', 'rc-tick' + (cls ? ' ' + cls : ''));
                const c = h('input');
                c.type = 'checkbox';
                c.checked = checked;
                c.addEventListener('change', () => onChange(c.checked));
                l.append(c, document.createTextNode(text));
                return l;
            };
            for (const r of opts.receivers.slice(1)) {
                box.append(tick('Show ' + r.tab.toUpperCase(), !hiddenRx.has(r.id), on => {
                    if (on) hiddenRx.delete(r.id); else hiddenRx.add(r.id);
                    store.set(own + 'hiddenReceivers', JSON.stringify([...hiddenRx]));
                    drawHidden();
                    if (!on && receiver === r.id) setReceiver(ids[0], true);
                }, 'rc-tick-rx rc-tick-rx-' + r.id));
            }
            for (const r of opts.bandRows) {
                const set = h('fieldset', 'rc-bandrow rc-bandrow-' + r.id);
                set.append(h('legend', null, r.label));
                for (const b of opts.bands) {
                    set.append(tick(b.label, !hiddenBands[r.id].has(b.id), on => {
                        if (on) hiddenBands[r.id].delete(b.id); else hiddenBands[r.id].add(b.id);
                        store.set(own + 'hiddenBands.' + r.id, JSON.stringify([...hiddenBands[r.id]]));
                        drawHidden();
                    }));
                }
                box.append(set);
            }
            bandsDlg.append(head, box);
            root.append(bandsDlg);
        }
        bandsDlg.showModal();
    }

    function setTheme() {
        const t = applyTheme(root, store.get(themePrefix + 'theme', DEFAULT_THEME));
        for (const p of Object.values(panes)) p.setTheme(t);
    }
    window.addEventListener('storage', e => { if (e.key === themePrefix + 'theme') setTheme(); });
    try {
        window.matchMedia('(prefers-color-scheme: light)').addEventListener('change', setTheme);
    } catch { /* nothing to follow */ }

    setTheme();
    drawHidden();
    setReceiver(receiver, false);
    setShowSpots(showSpots);
    setMetersShown(metersShown);
    setDecode(decoding, false);
    for (const name of Object.keys(BOX_SIZES)) setSize(name, sizes[name], false);
    loadLayouts();

    return {
        pane: id => panes[id],
        get receiver() { return receiver; },
        setReceiver: id => setReceiver(id, false),
        setFrequency(id, hz) {
            if (!shown[id]) return;
            shown[id].hz = Number(hz) || 0;
            if (id === receiver) drawStrip();
        },
        setMode(id, mode) {
            if (!shown[id]) return;
            shown[id].mode = mode ?? '';
            if (id === receiver) drawStrip();
        },
        /** e.g. setTones(1415, 170) shows "MARK 1415 / 170". */
        setTones(markHz, shiftHz) {
            toneChip.textContent = markHz > 0 ? `MARK ${Math.round(markHz)} / ${Math.round(shiftHz)}` : '';
            toneChip.hidden = !(markHz > 0);
        },
        /**
         * What the radio has for one of opts.controls. vfo names the receiver
         * for a per-receiver setting; options is a list's choices
         * ({id, label}[], empty when it does not apply), max a slider's top.
         */
        setControl(id, value, { vfo = null, options, max } = {}) {
            const c = ctls[id]?.c;
            if (!c) return;
            const k = c.perReceiver ? vfo : '';
            const e = ((ctlVals[id] ??= {})[k] ??= {});
            e.value = value;
            if (options !== undefined) e.options = options;
            if (max != null) e.max = max;
            if (!c.perReceiver || k === receiver) drawControls();
        },
        /** frac 0..1 for the bar, text beside it; over lights it red. */
        setMeter(id, frac, text, over = false) {
            const m = meterEls[id];
            if (!m) return;
            m.fill.style.width = Math.max(0, Math.min(1, Number(frac) || 0)) * 100 + '%';
            m.val.textContent = text ?? '';
            m.el.classList.toggle('over', !!over);
        },
        /** Light the band key a receiver is on; null lights none. */
        setBand(rowId, bandId) {
            for (const [b, k] of Object.entries(bandKeys[rowId] ?? {})) k.classList.toggle('on', b === bandId);
        },
        setTx(on) {
            txChip.textContent = on ? 'TX' : 'RX';
            txChip.classList.toggle('tx', !!on);
        },
        get showSpots() { return showSpots; },
        /** The wheel step the operator picked, in Hz. */
        get stepHz() { return stepHz; },
        /** Which parts are in windows: { spectrum, waterfall, both, tuner, decode }. */
        get out() { return { ...out }; },
        get onTop() { return !!pip; },
        /** What a saved layout would hold now. */
        get layout() { return currentLayout(); },
        applyLayout,
    };
}
