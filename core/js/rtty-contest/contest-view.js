// contest-view.js - the RTTY Contest view: only what a contest RTTY operator
// uses, on one screen. A top strip (frequency, mode, Mark/Shift, compact
// meters, Main/Sub, DX SPOTS, SPAN, Views), a row of scope controls (ref
// level, auto floor, range, fit, swap, wheel step), the spectrum over the
// waterfall with a grip between them, and the RTTY tuner docked at the side.
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
import { PopoutHost } from '../popout/popout.js';
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
export const POPOUT_PARTS = ['spectrum', 'waterfall', 'both', 'tuner'];
const POPOUT_SIZES = {
    spectrum: { width: 1100, height: 380 },
    waterfall: { width: 1100, height: 520 },
    both: { width: 1100, height: 760 },
    tuner: { width: 380, height: 620 },
};
const POPOUT_LABELS = { spectrum: 'SPECTRUM', waterfall: 'WATERFALL', both: 'BOTH', tuner: 'POP OUT' };

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
.rc-freq{appearance:none;border:1px solid transparent;background:none;border-radius:4px;padding:0 4px;font:500 26px ${MONO};color:var(--rp-title);cursor:text;white-space:nowrap}
.rc-freq small{font-size:18px;color:var(--rp-chip-fg)}
.rc-freq:hover{border-color:var(--rp-soft-border)}
.rc-freq-in{width:9em;font:500 22px ${MONO};background:var(--rp-spec-bg);color:var(--rp-title);border:1px solid var(--rp-highlight);border-radius:4px;padding:1px 6px}
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
.rc-stack{flex:1;min-width:0;display:flex}
.rc-holder{flex:1;min-width:0;display:flex}
.rc-holder[hidden],.rc-holder:has(>.rp-pane[hidden]){display:none}
.rc-page .rp-pane{position:relative;padding:6px 8px 8px;border-radius:6px;background:var(--rp-screen);border:1px solid var(--rp-screen-line)}
.rc-tuner{flex:none;display:flex;flex-direction:column;min-height:0}
.rc-tuner:empty{display:none}
.rc-scope{flex:none;display:flex;align-items:center;flex-wrap:wrap;gap:6px 8px;padding:6px 10px 0}
.rc-grp{display:flex;align-items:center;gap:3px}
.rc-lbl{font:600 13px ${SANS};letter-spacing:.08em;color:var(--rp-legend-dim);margin-right:2px}
.rc-num{min-width:3em;text-align:center;font:400 14px ${MONO};color:var(--rp-chip-fg)}
.rc-scope .rp-spop-key{min-height:28px;padding:1px 10px;font-size:14px}
.rc-scope .rc-pm{min-width:28px;padding:1px 0}
.rc-step{font:500 14px ${SANS};min-height:28px;background:var(--rp-chipbtn-bg);color:var(--rp-chipbtn-fg);border:1px solid var(--rp-key-border);border-radius:4px;padding:1px 4px}
.rc-out{flex:1;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:12px;font:500 18px ${SANS};color:var(--rp-legend-dim);border:1px dashed var(--rp-well-border);border-radius:6px}
.rc-out[hidden]{display:none}
.rc-tbar{display:flex;gap:6px;justify-content:flex-end;padding:0 0 6px}
.rc-tbar .rp-spop-key{min-height:28px;padding:1px 10px;font-size:14px}
.rc-tbar .rp-spop-key:disabled{opacity:.45;cursor:default}
.rc-tnote{width:340px;padding:14px;font:500 16px ${SANS};color:var(--rp-legend-dim);border:1px dashed var(--rp-well-border);border-radius:6px}
.rc-tnote[hidden]{display:none}
.rc-pop{display:flex;align-items:center;gap:4px}
.rc-pop[hidden]{display:none}
body>#rttyTunerDialog{width:100% !important;height:100vh;border:0 !important;border-radius:0 !important}
.rc-views{font:500 15px ${SANS};min-height:32px;background:var(--rp-chipbtn-bg);color:var(--rp-chipbtn-fg);border:1px solid var(--rp-key-border);border-radius:4px;padding:2px 6px}
`;

/**
 * @param {HTMLElement} root
 * @param {object} opts
 * @param {{id:string, tab:string, name:string}[]} opts.receivers  one or two; with one there is no Main/Sub switch
 * @param {{hz:number, label:string}[]} [opts.spans]
 * @param {(id:string, hz:number) => void} [opts.onSpan]
 * @param {(id:string, hz:number) => void} [opts.onTune]  a click on a pane
 * @param {(id:string, hz:number) => number} [opts.tuneTo]  where the dial lands for a click at hz
 * @param {(id:string, dir:number) => void} [opts.onStep]  a mouse wheel notch
 * @param {(id:string, hz:number) => void} [opts.onFrequencyEntry]  a typed frequency
 * @param {(id:string) => void} [opts.onReceiver]  the operator picked Main or Sub
 * @param {{id:string, label:string}[]} [opts.meters]  compact bars in the strip, e.g. S, PO, SWR, ALC
 * @param {{label:string, href:string, on?:boolean}[]} [opts.views]
 * @param {HTMLElement} [opts.tuner]  the RTTY tuner's element, docked at the side
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

    // The frequency: click it to type one.
    const freqBtn = btn('rc-freq', '', () => editFrequency());
    freqBtn.title = 'Click to type a frequency: 14.08 or 14080 or 14080000';
    const freqIn = h('input', 'rc-freq-in');
    freqIn.type = 'text';
    freqIn.inputMode = 'decimal';
    freqIn.hidden = true;
    freqIn.setAttribute('aria-label', 'Frequency');
    const modeChip = h('span', 'rc-chip', '');
    modeChip.title = 'Mode';
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

    let spanSel = null;
    if (opts.spans?.length) {
        spanSel = h('select', 'rp-spop-span');
        spanSel.title = 'Span';
        for (const s of opts.spans) spanSel.append(new Option('SPAN ' + s.label, String(s.hz)));
        spanSel.addEventListener('change', () => opts.onSpan?.(receiver, Number(spanSel.value)));
        top.append(spanSel);
    }

    if (part) {
        const dock = btn('rp-spop-key on rc-dock', 'DOCK', () => opts.onDock?.());
        dock.title = 'Put this back in the RTTY Contest page and close the window';
        top.append(dock);
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
    if (part !== 'waterfall') scope.append(refGrp, autoKey, rangeGrp, fitKey);
    if (isMain || part === 'both') scope.append(swapKey);
    scope.append(stepSel);

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
    const tunerSlot = h('div', 'rc-tuner');
    body.append(stack, tunerSlot);
    root.append(top, scope, body);

    // The tuner, with its own bar: POP OUT into a window, ON TOP into the
    // always-on-top window. Out either way, a note keeps its place.
    let tunerPop = null, onTopKey = null, pip = null;
    const tunerNote = h('div', 'rc-tnote');
    tunerNote.hidden = true;
    if (isMain && opts.tuner) {
        const tbar = h('div', 'rc-tbar');
        if (opts.windowUrl) {
            tunerPop = key(POPOUT_LABELS.tuner, '', () => togglePopout('tuner'), 'rc-pop-tuner');
            tbar.append(tunerPop);
        }
        if (pipSupported()) {
            onTopKey = key('ON TOP', 'Keep the tuner on top of every other window, the logging program too. Close that window to put it back',
                () => toggleOnTop(), 'rc-ontop');
            tbar.append(onTopKey);
        }
        if (tbar.childElementCount) tunerSlot.append(tbar);
        tunerSlot.append(opts.tuner, tunerNote);
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
        freqBtn.textContent = '';
        if (s.hz > 0) {
            freqBtn.append(f.big);
            freqBtn.append(h('small', null, f.small));
        } else freqBtn.textContent = '-.---.---';
        modeChip.textContent = s.mode || '-';
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
    const out = { spectrum: false, waterfall: false, both: false, tuner: false };
    function drawOut() {
        // A window shows its own part; the main page what has not gone out.
        const left = part ?? partsLeft(out);
        for (const p of Object.values(panes)) p.setParts(left);
        outNote.hidden = left !== 'none';
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
    }
    // A part is in one window at a time: BOTH takes the single ones back, and
    // either single one takes BOTH back. Also run when a window turns up by
    // itself (a reload, an address typed in), not only on a POP OUT click.
    function recallClashing(p) {
        const clash = p === 'both' ? ['spectrum', 'waterfall'] : p === 'tuner' ? [] : ['both'];
        for (const c of clash) if (hosts[c]?.isOpen) hosts[c].recall();
    }
    function togglePopout(p) {
        const host = hosts[p];
        if (!host) return;
        if (host.isOpen) { host.recall(); return; }
        recallClashing(p);
        if (!host.open()) alert('The browser blocked the pop-out window. Allow pop-ups for this address, then try again.');
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
    if (isMain && opts.windowUrl) {
        const name = opts.windowName ?? 'rtty-contest-';
        for (const p of POPOUT_PARTS) {
            if (p === 'tuner' && !opts.tuner) continue;
            hosts[p] = new PopoutHost({
                name: name + p,
                url: opts.windowUrl(p),
                defaultSize: POPOUT_SIZES[p],
                onChange: o => {
                    if (o) recallClashing(p);
                    out[p] = o;
                    drawOut();
                    if (p === 'tuner') opts.onTunerOut?.(o);
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
        if (!ids.includes(id)) id = ids[0];
        receiver = id;
        store.set(own + 'receiver', id);
        for (const [k, el] of Object.entries(tabEls)) el.classList.toggle('on', k === id);
        for (const [k, el] of Object.entries(holders)) el.hidden = k !== id;
        drawStrip();
        drawScope();
        if (byUser) opts.onReceiver?.(id);
    }
    function setShowSpots(on) {
        showSpots = on;
        store.set(own + 'spots', on ? '1' : '0');
        spotsBtn.classList.toggle('on', on);
        spotsBtn.setAttribute('aria-pressed', on ? 'true' : 'false');
        for (const p of Object.values(panes)) p.setShowSpots(on);
    }
    function setMetersShown(on) {
        metersShown = on;
        store.set(own + 'meters', on ? '1' : '0');
        meterRow.hidden = !on;
        meterKey.classList.toggle('on', on);
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
    setReceiver(receiver, false);
    setShowSpots(showSpots);
    setMetersShown(metersShown);

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
        /** frac 0..1 for the bar, text beside it; over lights it red. */
        setMeter(id, frac, text, over = false) {
            const m = meterEls[id];
            if (!m) return;
            m.fill.style.width = Math.max(0, Math.min(1, Number(frac) || 0)) * 100 + '%';
            m.val.textContent = text ?? '';
            m.el.classList.toggle('over', !!over);
        },
        setTx(on) {
            txChip.textContent = on ? 'TX' : 'RX';
            txChip.classList.toggle('tx', !!on);
        },
        /** Show the span the SDR is actually at. A frame's span is whole bins: 20 kHz arrives as 19989 Hz. */
        setSpanValue(id, hz) {
            if (!spanSel || id !== receiver) return;
            const n = Number(hz) || 0;
            const o = [...spanSel.options].find(x => Math.abs(Number(x.value) - n) <= Number(x.value) * 0.02);
            if (o && spanSel.value !== o.value && document.activeElement !== spanSel) spanSel.value = o.value;
        },
        get showSpots() { return showSpots; },
        /** The wheel step the operator picked, in Hz. */
        get stepHz() { return stepHz; },
        /** Which parts are in windows: { spectrum, waterfall, both, tuner }. */
        get out() { return { ...out }; },
        get onTop() { return !!pip; },
    };
}
