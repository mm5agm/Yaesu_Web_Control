// radio-panel.js - a radio-style front panel: meter, keys, VFO screen,
// spectrum, waterfall, main dial and knob row, laid out like a transceiver.
//
// Radio-agnostic. It builds the DOM, scales it to the window, owns the theme
// and size settings, and exposes setters; the app's adapter
// (YWC: wwwroot/js/ui/radio-panel-yaesu.js) connects to its own hub, turns its
// radio's properties into these setters, and decides what each key does.
//
// A key, band or mode button does nothing unless the adapter passes
// `onAction` - so the panel can be put on screen read-only, and every write
// path is the adapter's, where the radio's own commands live.
//
// No accessibility layer by design (Colin, 2026-10-13): this page starts from
// scratch; Classic keeps the screen-reader and voice work.

import { injectRadioPanelStyle } from './radio-panel-style.js';
import { applyTheme, themeIds, resolveTheme, DEFAULT_THEME, SYSTEM_THEME } from './radio-panel-themes.js';
import { SpectrumView } from './spectrum-view.js';
import {
    formatFrequency, sMeterFraction, sMeterText, meterGeometry, LOWER_SCALES,
    passbandPercent, axisLabels, spanText, widthText, knobAngle,
} from './panel-math.js';
import { mountPanelWindows } from './panel-windows.js';

const DESIGN_WIDTH = 1600;
const SIZES = ['fit', '50', '67', '75', '90', '100', '110', '125', '150'];
const METERS = ['PO', 'SWR', 'ALC', 'COMP', 'IDD'];

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

const SVG = 'http://www.w3.org/2000/svg';
function s(tag, attrs = {}, text) {
    const el = document.createElementNS(SVG, tag);
    for (const [k, val] of Object.entries(attrs)) el.setAttribute(k, val);
    if (text != null) el.textContent = text;
    return el;
}

function knobSvg(size) {
    const svg = s('svg', { width: size, height: size, viewBox: '0 0 64 64', class: 'rp-knobsvg' });
    svg.append(
        s('circle', { cx: 32, cy: 32, r: 31, class: 'o' }),
        s('circle', { cx: 32, cy: 32, r: 27, class: 'm' }),
        s('circle', { cx: 32, cy: 32, r: 20, class: 'c' }),
    );
    const g = s('g', { transform: 'rotate(-135 32 32)' });
    g.append(s('line', { x1: 32, y1: 9, x2: 32, y2: 18, class: 'mark' }));
    svg.append(g);
    return { svg, g };
}

// A concentric pair, as on a transceiver: an outer ring round an inner knob,
// each with its own pointer.
function knob2Svg(size) {
    const svg = s('svg', { width: size, height: size, viewBox: '0 0 64 64', class: 'rp-knobsvg' });
    svg.append(s('circle', { cx: 32, cy: 32, r: 31, class: 'o' }), s('circle', { cx: 32, cy: 32, r: 28, class: 'm' }));
    const outer = s('g', { transform: 'rotate(-135 32 32)', class: 'idle' });
    outer.append(s('line', { x1: 32, y1: 5, x2: 32, y2: 12, class: 'mark' }));
    svg.append(outer, s('circle', { cx: 32, cy: 32, r: 17, class: 'o' }), s('circle', { cx: 32, cy: 32, r: 15, class: 'c ring' }));
    const inner = s('g', { transform: 'rotate(-135 32 32)', class: 'idle' });
    inner.append(s('line', { x1: 32, y1: 19, x2: 32, y2: 25, class: 'mark2' }));
    svg.append(inner);
    return { svg, outer, inner };
}

/**
 * @param {HTMLElement} root
 * @param {object} opts
 *   brandShort, brandName, model, dual (bool),
 *   views:  [{ label, href, on }]          layout switch in the top strip
 *   lamps:  [{ id, label }]
 *   keys:   [{ id, label, inert }]         left column, two per row; `inert` keeps
 *                                          one key display-only even with onAction
 *                                          (e.g. anything that would transmit)
 *   vfoKeys:[{ id, label, inert }]         under the dial, three per row
 *   bands:  [{ id, label }]   modes: [{ id, label }]
 *   knobs:  [{ id, label }]                the knob row, used when rxGroups is not given
 *   rxGroups: { keys: [{ id, label }], knobs: [{ id, label }] }
 *                                          optional: the bottom row as one group per
 *                                          receiver (MAIN and SUB; SUB only when dual),
 *                                          each with the same concentric knobs and keys,
 *                                          and a SHARED group between them. A group key
 *                                          acts as act('key', id, vfo) - vfo undefined on
 *                                          a single-receiver radio, where the one group
 *                                          follows the selected VFO
 *   sharedKnobs: [{ id, label }]           the SHARED group's concentric knobs
 *   sharedName                             its heading (default 'SHARED · TX')
 *   screenViews: [{ id, label }]         optional: what the centre screen can show
 *                                          instead of the spectrum. The spectrum is
 *                                          always 'sdr'; each other view gets an empty
 *                                          slot (screenSlot(id)) for the adapter to fill
 *   onScreenView(id)                       told when the operator switches view
 *   windows: [{ id, key, title, pos, panels }]  optional: the launcher keys and the
 *                                          windows they open - see panel-windows.js.
 *                                          The adapter fills each panel through
 *                                          api.windows.slot(groupId, panelId)
 *   onWindowPanel(groupId, panelId, shown) told when a window panel shows or hides
 *   onSpectrumPopout(), onSpectrumBack()   optional: a ⧉ key on the spectrum, and the
 *                                          BRING BACK key shown while setSpectrumAway(true)
 *   frequencyEntry (bool)                  show an ENT chip in each VFO box
 *   storagePrefix, fontBase
 *   onAction(kind, id, vfo)                kind: 'key' | 'band' | 'mode' | 'step' | 'vfoKey'
 *                                          | 'vfo' (the operator clicked VFO 'A' or 'B')
 *                                          | 'width' | 'agc' (picked from a VFO box chip)
 *                                          | 'tune' (id = signed Hz, from the wheel over a
 *                                            frequency digit)
 *                                          | 'entry' (the ENT chip in a VFO box: the
 *                                            operator wants to type a frequency). `vfo` is set when the
 *                                          action came from inside a VFO box and names
 *                                          that box; otherwise it is undefined.
 *   choices(kind, vfo)                     kind 'mode' | 'width' | 'agc': the list a click
 *                                          on that chip offers, [{ id, label, on }], or
 *                                          null for a chip that does nothing
 *
 * The selected receiver (setSelected) is the one the meter needle, the mode
 * keys and - in the adapter - the keys follow. The spectrum stays VFO A's.
 */
export function mountRadioPanel(root, opts = {}) {
    injectRadioPanelStyle(document, opts.fontBase);
    const prefix = opts.storagePrefix ?? 'rwc.radioPanel.';
    const act = typeof opts.onAction === 'function' ? opts.onAction : null;
    const dual = opts.dual !== false;

    root.classList.add('rp-page');
    root.textContent = '';
    const sizer = h('div', 'rp-sizer');
    const scaler = h('div', 'rp-scale');
    const panel = h('div', 'rp-panel');
    sizer.append(scaler); scaler.append(panel); root.append(sizer);

    const button = (cls, kind, id, inert = false) => {
        const b = h('button', cls);
        b.type = 'button';
        if (act && kind && !inert) b.addEventListener('click', () => act(kind, id));
        else if (kind) b.disabled = true;
        return b;
    };

    // ---- top strip -----------------------------------------------------
    const top = h('div', 'rp-top');
    const brand = h('div', 'rp-brand');
    const model = h('span', 'rp-brand-model', opts.model ?? '');
    brand.append(h('span', 'rp-brand-short', opts.brandShort ?? ''), Object.assign(h('span', 'rp-legend', opts.brandName ?? ''), { style: 'color:var(--rp-legend-dim)' }), model);

    const views = h('div', 'rp-bar');
    for (const vw of opts.views ?? []) {
        const a = h('a', 'rp-viewbtn' + (vw.on ? ' on' : ''), vw.label);
        a.href = vw.href;
        views.append(a);
    }

    const right = h('div', 'rp-top-right');
    const lamps = h('div', 'rp-lamps rp-legend');
    const lampEls = {};
    for (const l of opts.lamps ?? []) {
        const wrap = h('span');
        const dot = h('span', 'rp-lamp');
        wrap.append(dot, document.createTextNode(l.label));
        lamps.append(wrap);
        lampEls[l.id] = dot;
    }
    // Status chips sit with the lamps: dim until the adapter lights one
    // (e.g. 'WSJT-X TX' while another program is transmitting through the app).
    const statusChipEls = {};
    for (const c of opts.statusChips ?? []) {
        const el = h('span', 'rp-chip dim rp-status-chip', c.label);
        if (c.title) el.title = c.title;
        lamps.append(el);
        statusChipEls[c.id] = el;
    }
    const settings = h('div', 'rp-bar');
    const themeSel = h('select', 'rp-select');
    themeSel.title = 'Theme';
    for (const id of themeIds()) themeSel.append(Object.assign(h('option', null, resolveTheme(id).label), { value: id }));
    const sizeSel = h('select', 'rp-select');
    sizeSel.title = 'Size';
    for (const z of SIZES) sizeSel.append(Object.assign(h('option', null, z === 'fit' ? 'Fit' : z + '%'), { value: z }));
    settings.append(themeSel, sizeSel);
    right.append(lamps, settings);
    top.append(brand, views, right);

    // ---- body ----------------------------------------------------------
    const body = h('div', 'rp-body');

    // left: meter + keys
    const left = h('div', 'rp-left');
    const meterWell = h('div', 'rp-meter-well');
    const meterSvg = s('svg', { width: 276, height: 160, viewBox: '0 0 250 145', class: 'rp-meter' });
    meterWell.append(meterSvg);
    const meterBtns = h('div', 'rp-meter-btns');
    const meterBtnEls = {};
    for (const m of METERS) {
        const b = h('button', 'rp-chipbtn', m);
        b.type = 'button';
        b.addEventListener('click', () => chooseMeter(m));
        meterBtns.append(b);
        meterBtnEls[m] = b;
    }
    const keysGrid = h('div', 'rp-keys');
    const keyEls = {};
    for (const k of opts.keys ?? []) {
        const b = button('rp-key', 'key', k.id, k.inert);
        const led = h('span', 'rp-led'), sub = h('span', 'rp-sub', '');
        b.append(led, h('span', null, k.label), sub);
        keysGrid.append(b);
        keyEls[k.id] = { b, led, sub };
    }
    const launcher = h('div', 'rp-launcher');
    left.append(meterWell, meterBtns, keysGrid, launcher);

    // centre: the screen
    const well = h('div', 'rp-screen-well');
    const screen = h('div', 'rp-screen');
    well.append(screen);

    const vfos = h('div', 'rp-vfos');
    const main = h('div', 'rp-main' + (dual ? '' : ''));
    const mainHead = h('div', 'rp-vfo-head');
    const mainName = h('span', 'rp-vfo-name', dual ? 'MAIN · VFO-A' : 'VFO-A');
    const rxtx = h('span', 'rp-chip rx', 'RX');
    const splitChip = h('span', 'rp-chip hidden', 'SPLIT');
    const selA = h('span', 'rp-chip sel hidden', 'SELECTED');
    // ENT: type a frequency into this VFO (the adapter opens its keypad).
    const entA = h('span', 'rp-chip ent hidden', 'ENT'), entB = h('span', 'rp-chip ent hidden', 'ENT');
    mainHead.append(mainName, rxtx, splitChip, selA, entA);
    const mainFreq = h('div', 'rp-freq');
    const mainBig = h('span', 'big', '-.---'), mainSmall = h('span', 'small', '.---');
    mainFreq.append(mainBig, mainSmall);
    const mainChips = h('div', 'rp-chips');
    const chip = cls => h('span', 'rp-chip' + (cls ? ' ' + cls : ''), '');
    const chipsA = { mode: chip('mode'), width: chip(), agc: chip(), flags: h('span') };
    chipsA.flags.style.display = 'contents';
    mainChips.append(chipsA.mode, chipsA.width, chipsA.agc, chipsA.flags);
    main.append(mainHead, mainFreq, mainChips);

    const sub = h('div', 'rp-subvfo');
    const subHead = h('div', 'rp-vfo-head');
    const subName = h('span', 'rp-vfo-name', dual ? 'SUB · VFO-B' : 'VFO-B');
    const subRx = h('span', 'rp-chip dim', dual ? 'RX' : '');
    const selB = h('span', 'rp-chip sel hidden', 'SELECTED');
    subHead.append(subName, subRx, selB, entB);
    const subFreq = h('div', 'rp-subfreq');
    const subBig = h('span', 'big', '-.---'), subSmall = h('span', 'small', '.---');
    subFreq.append(subBig, subSmall);
    const subChips = h('div', 'rp-chips');
    const chipsB = { mode: chip('dim'), width: chip('dim'), agc: chip('dim'), flags: h('span') };
    chipsB.flags.style.display = 'contents';
    subChips.append(chipsB.mode, chipsB.width, chipsB.agc, chipsB.flags);
    const sbar = h('div', 'rp-sbar');
    const sFill = h('div', 'fill'), sTrack = h('div', 'track'), sTxt = h('span', 'txt', 'S0');
    sTrack.append(sFill);
    sbar.append(h('span', 'lbl', 'S'), sTrack, sTxt);
    sub.append(subHead, subFreq, subChips);
    if (dual) sub.append(sbar);
    vfos.append(main, sub);
    if (act) {
        vfos.classList.add('pick');
        main.addEventListener('click', () => act('vfo', 'A'));
        sub.addEventListener('click', () => act('vfo', 'B'));
    }

    // Inside a VFO box: mode, width and AGC chips open a list, a flag chip
    // is pressed like its key, and the wheel over a frequency digit moves
    // that digit. All of it works the box's own VFO without selecting it,
    // so the chip clicks do not reach the box's own click.
    const picker = h('div', 'rp-picker');
    picker.hidden = true;
    vfos.append(picker);
    function closePicker() { picker.hidden = true; picker.dataset.for = ''; }
    function openPicker(chipEl, kind, vfo) {
        if (!picker.hidden && picker.dataset.for === kind + vfo) return closePicker();
        const list = opts.choices?.(kind, vfo);
        if (!list?.length) return closePicker();
        picker.textContent = '';
        for (const c of list) {
            const b = h('button', 'rp-chipbtn' + (c.on ? ' on' : ''), c.label);
            b.type = 'button';
            b.addEventListener('click', e => { e.stopPropagation(); closePicker(); act(kind, c.id, vfo); });
            picker.append(b);
        }
        picker.dataset.for = kind + vfo;
        picker.hidden = false;
        // Board pixels, not screen pixels: the board is scaled by a transform.
        const vr = vfos.getBoundingClientRect(), cr = chipEl.getBoundingClientRect();
        const k = vfos.offsetWidth / (vr.width || 1);
        const left = (cr.left - vr.left) * k;
        picker.style.left = Math.max(0, Math.min(left, vfos.offsetWidth - picker.offsetWidth)) + 'px';
        picker.style.top = ((cr.bottom - vr.top) * k + 4) + 'px';
    }
    if (act) {
        document.addEventListener('click', e => { if (!picker.hidden && !picker.contains(e.target)) closePicker(); });
        document.addEventListener('keydown', e => { if (e.key === 'Escape') closePicker(); });
        for (const [chips, vfo] of [[chipsA, 'A'], [chipsB, 'B']])
            for (const kind of ['mode', 'width', 'agc']) {
                const el = chips[kind];
                el.classList.add('act');
                el.addEventListener('click', e => { e.stopPropagation(); openPicker(el, kind, vfo); });
            }
        if (opts.frequencyEntry)
            for (const [el, vfo] of [[entA, 'A'], [entB, 'B']]) {
                el.classList.remove('hidden');
                el.classList.add('act');
                el.title = 'Type a frequency';
                el.addEventListener('click', e => { e.stopPropagation(); act('entry', vfo, vfo); });
            }
        for (const [freqEl, vfo] of [[mainFreq, 'A'], [subFreq, 'B']]) {
            freqEl.classList.add('wheel');
            freqEl.addEventListener('wheel', e => {
                const step = Number(e.target?.dataset?.step);
                if (!step) return;
                e.preventDefault();
                if (e.deltaY) act('tune', String(e.deltaY < 0 ? step : -step), vfo);
            }, { passive: false });
        }
    }

    const spec = h('div', 'rp-spectrum');
    const pb = h('div', 'rp-passband');
    const cursor = h('div', 'rp-cursor');
    const spots = h('div');
    const specLabel = h('div', 'rp-spec-label', '');
    const specEmpty = h('div', 'rp-spec-empty', 'No SDR');
    spec.append(pb, cursor, spots, specLabel, specEmpty);
    const axis = h('div', 'rp-axis');
    const wf = h('div', 'rp-waterfall');

    // The spectrum and any other view share the space between the VFOs and
    // the band keys; the switch sits in the waterfall's corner, which is the
    // one place DX spots never land.
    const screenViewList = [{ id: 'sdr', label: 'SDR' }, ...(opts.screenViews ?? []).filter(x => x.id !== 'sdr')];
    const viewArea = h('div', 'rp-viewarea');
    const sdrView = h('div', 'rp-sdrview');
    sdrView.append(spec, axis, wf);
    viewArea.append(sdrView);
    // The spectrum can go to a window of its own (a second screen); while it
    // is there the board shows where it went, and a key to fetch it back.
    let specAway = null;
    if (typeof opts.onSpectrumPopout === 'function') {
        const pop = h('button', 'rp-wkey rp-spec-pop', '⧉');
        pop.type = 'button';
        pop.title = 'Spectrum in its own window';
        pop.addEventListener('click', () => opts.onSpectrumPopout());
        spec.append(pop);
        specAway = h('div', 'rp-spec-away');
        const back = h('button', 'rp-wkey', 'BRING BACK');
        back.type = 'button';
        back.addEventListener('click', () => opts.onSpectrumBack?.());
        specAway.append(h('span', null, 'Spectrum is in its own window'), back);
        sdrView.append(specAway);
    }
    const slots = {};
    const viewTabs = h('div', 'rp-viewtabs');
    const viewTabEls = {};
    for (const vw of screenViewList) {
        if (vw.id !== 'sdr') {
            slots[vw.id] = h('div', 'rp-altview');
            slots[vw.id].style.display = 'none';
            viewArea.append(slots[vw.id]);
        }
        const t = h('button', 'rp-viewtab', vw.label);
        t.type = 'button';
        t.addEventListener('click', () => setScreenView(vw.id));
        viewTabs.append(t);
        viewTabEls[vw.id] = t;
    }
    if (screenViewList.length > 1) viewArea.append(viewTabs);

    const bandsRow = h('div', 'rp-bands');
    const bandEls = {};
    for (const b of opts.bands ?? []) { const el = button('rp-soft', 'band', b.id); el.textContent = b.label; bandsRow.append(el); bandEls[b.id] = el; }
    const modesRow = h('div', 'rp-modes');
    const modeEls = {};
    for (const m of opts.modes ?? []) { const el = button('rp-soft', 'mode', m.id); el.textContent = m.label; modesRow.append(el); modeEls[m.id] = el; }

    screen.append(vfos, viewArea, bandsRow, modesRow);

    // right: dial and VFO keys
    const rightCol = h('div', 'rp-right');
    const dial = h('div', 'rp-dial');
    const dialInner = h('div', 'rp-dial-inner');
    dialInner.append(h('div', 'rp-dial-dimple'));
    dial.append(dialInner);
    const stepRow = h('div', 'rp-step');
    const down = button('rp-key', 'step', 'down'); down.append(h('span', 'big', '−'));
    const stepKey = button('rp-key', 'step', 'cycle'); const stepTxt = h('span', 'rp-sub', '');
    stepKey.append(h('span', null, 'STEP'), stepTxt);
    const up = button('rp-key', 'step', 'up'); up.append(h('span', 'big', '+'));
    stepRow.append(down, stepKey, up);
    const vfoKeys = h('div', 'rp-vfokeys');
    for (const k of opts.vfoKeys ?? []) {
        const b = button('rp-key', 'vfoKey', k.id, k.inert);
        const led = h('span', 'rp-led');
        b.append(led, h('span', null, k.label));
        vfoKeys.append(b);
        keyEls[k.id] = { b, led, sub: null };
    }
    const clar = h('div', 'rp-clar');
    const clarKnob = knobSvg(88);
    const clarBox = h('div');
    const clarTxt = h('span', 'rp-lcd', '+0 Hz');
    clarBox.append(h('span', 'rp-legend', 'Clarifier'), clarTxt);
    clar.append(clarKnob.svg, clarBox);
    rightCol.append(h('span', 'rp-legend', 'Main dial'), dial, stepRow, vfoKeys, clar);

    body.append(left, well, rightCol);

    // knob row
    const knobs = h('div', 'rp-knobs');
    const knobEls = {};
    for (const k of opts.rxGroups ? [] : opts.knobs ?? []) {
        const wrap = h('div', 'rp-knob idle');
        const kn = knobSvg(88);
        const val = h('span', 'rp-lcd', '--');
        wrap.append(h('span', 'rp-legend', k.label), kn.svg, val);
        knobs.append(wrap);
        knobEls[k.id] = { wrap, g: kn.g, val };
    }

    // Or the bottom row as receiver groups: MAIN | SHARED | SUB.
    const groupEls = {};      // 'A' | 'B' | 'shared' -> { box, name, tag, knobs: { id -> { outer, inner, val, wrap } } }
    function concentric(row, k) {
        const wrap = h('div', 'rp-cknob');
        const kn = knob2Svg(80);
        const val = h('span', 'rp-lcd', '--');
        wrap.append(h('span', 'rp-legend', k.label), kn.svg, val);
        row.append(wrap);
        return { wrap, outer: kn.outer, inner: kn.inner, val };
    }
    function rxGroup(vfo) {
        const box = h('div', 'rp-group rp-group-' + vfo);
        const head = h('div', 'rp-group-head');
        const name = h('span', 'rp-group-name', dual ? (vfo === 'A' ? 'MAIN · VFO-A' : 'SUB · VFO-B') : 'RECEIVER · VFO-A');
        const tag = h('span', 'rp-chip sel rp-group-tag hidden', 'SELECTED');
        head.append(name, tag);
        const row = h('div', 'rp-group-row');
        const g = { box, name, tag, knobs: {} };
        for (const k of opts.rxGroups.knobs ?? []) g.knobs[k.id] = concentric(row, k);
        const grid = h('div', 'rp-gkeys');
        for (const k of opts.rxGroups.keys ?? []) {
            const b = h('button', 'rp-skey');
            b.type = 'button';
            if (act && !k.inert) b.addEventListener('click', () => act('key', k.id, dual ? vfo : undefined));
            else b.disabled = true;
            const led = h('span', 'rp-led'), sub = h('span', 'rp-sub', '');
            b.append(led, h('span', 'rp-skey-label', k.label), sub);
            grid.append(b);
            keyEls[vfo + ':' + k.id] = { b, led, sub };
        }
        row.append(grid);
        box.append(head, row);
        groupEls[vfo] = g;
        return box;
    }
    let bottom = knobs;
    if (opts.rxGroups) {
        bottom = h('div', 'rp-groups' + (dual ? '' : ' single'));
        const shared = h('div', 'rp-group rp-group-shared');
        const sharedRow = h('div', 'rp-group-row');
        const sg = { box: shared, name: h('span', 'rp-group-name', opts.sharedName ?? 'SHARED · TX'), tag: null, knobs: {} };
        for (const k of opts.sharedKnobs ?? []) sg.knobs[k.id] = concentric(sharedRow, k);
        const sharedHead = h('div', 'rp-group-head');
        sharedHead.append(sg.name);
        shared.append(sharedHead, sharedRow);
        groupEls.shared = sg;
        bottom.append(rxGroup('A'), shared);
        if (dual) bottom.append(rxGroup('B'));
    }

    panel.append(top, body, bottom);

    // ---- state ---------------------------------------------------------
    const st = {
        freq: { A: 0, B: 0 }, mode: { A: '', B: '' }, widthHz: { A: null, B: null },
        s: { A: 0, B: 0 }, tx: false, meter: store.get(prefix + 'meter', 'PO'),
        txMeters: {}, spanHz: 0, theme: null, spots: [], sel: 'A',
    };
    if (!METERS.includes(st.meter)) st.meter = 'PO';

    const spectrum = new SpectrumView(spec, wf);

    // ---- meter ---------------------------------------------------------
    let needle;
    function drawMeterFace() {
        const g = meterGeometry(0, LOWER_SCALES[st.meter]);
        meterSvg.textContent = '';
        meterSvg.append(
            s('path', { d: g.arc, class: 'ink' }),
            s('path', { d: g.red, class: 'red' }),
            s('path', { d: g.ticks, class: 'ink' }),
            s('path', { d: g.inArc, class: 'ink2' }),
            s('path', { d: g.inTicks, class: 'ink2' }),
        );
        for (const u of g.upper) meterSvg.append(s('text', { x: u.x, y: u.y, 'text-anchor': 'middle', 'font-size': u.red ? 11 : 12, class: u.red ? 't-red' : 't-ink' }, u.text));
        for (const l of g.lower) meterSvg.append(s('text', { x: l.x, y: l.y, 'text-anchor': 'middle', 'font-size': 10, class: 't-ink2' }, l.text));
        meterSvg.append(s('text', { x: 14, y: 30, 'font-size': 16, class: 't-ink t-title' }, 'S'));
        meterSvg.append(s('text', { x: 14, y: 138, 'font-size': 13, class: 't-ink2 t-title' }, st.meter));
        needle = s('line', { class: 'needle' });
        meterSvg.append(needle, s('rect', { x: 95, y: 128, width: 60, height: 17, class: 'hub' }));
        for (const [m, b] of Object.entries(meterBtnEls)) b.classList.toggle('on', m === st.meter);
        drawNeedle();
    }
    function drawNeedle() {
        const frac = st.tx ? (st.txMeters[st.meter] ?? 0) : sMeterFraction(st.s[st.sel]);
        const n = meterGeometry(frac).needle;
        needle.setAttribute('x1', n.x1); needle.setAttribute('y1', n.y1);
        needle.setAttribute('x2', n.x2); needle.setAttribute('y2', n.y2);
    }
    function chooseMeter(m) { st.meter = m; store.set(prefix + 'meter', m); drawMeterFace(); }

    // ---- spectrum overlays ---------------------------------------------
    function drawOverlays() {
        const pct = passbandPercent(st.mode.A, st.widthHz.A, st.spanHz);
        if (pct) { pb.style.display = 'block'; pb.style.left = pct.left + '%'; pb.style.width = pct.width + '%'; }
        else pb.style.display = 'none';
        axis.textContent = '';
        if (st.spanHz > 0 && st.freq.A > 0) for (const t of axisLabels(st.freq.A, st.spanHz)) axis.append(h('span', null, t));
        spots.textContent = '';
        if (st.spanHz > 0 && st.freq.A > 0) {
            let row = 0;
            for (const sp of st.spots) {
                const pos = 50 + (sp.hz - st.freq.A) / st.spanHz * 100;
                if (pos < 2 || pos > 98) continue;
                const el = h('div', 'rp-spot', sp.call);
                el.style.left = pos + '%';
                el.style.top = (30 + (row++ % 4) * 28) + 'px';
                spots.append(el);
            }
        }
    }

    function markModeKeys() {
        for (const [id, el] of Object.entries(modeEls)) el.classList.toggle('on', id === st.mode[st.sel]);
    }

    function setVfoText(vfo) {
        const f = formatFrequency(st.freq[vfo]);
        if (!st.freq[vfo]) {
            if (vfo === 'A') { mainBig.textContent = '-.---'; mainSmall.textContent = '.---'; }
            else { subBig.textContent = '-.---'; subSmall.textContent = '.---'; }
            return;
        }
        if (vfo === 'A') { digits(mainBig, f.big, 1000); digits(mainSmall, f.small, 1); }
        else { digits(subBig, f.big, 1000); digits(subSmall, f.small, 1); }
    }

    // One span per character; each digit carries the Hz it is worth, for the
    // wheel. `unitHz` is what the last digit of `text` is worth.
    function digits(el, text, unitHz) {
        if (el.textContent === text) return;
        el.textContent = '';
        let step = unitHz;
        const out = [];
        for (let i = text.length - 1; i >= 0; i--) {
            const span = h('span', null, text[i]);
            if (text[i] >= '0' && text[i] <= '9') { span.dataset.step = String(step); step *= 10; }
            out.unshift(span);
        }
        el.append(...out);
    }

    // ---- scaling, theme ------------------------------------------------
    function rescale() {
        const z = sizeSel.value;
        const natH = scaler.offsetHeight;
        const scale = z === 'fit'
            ? Math.min(window.innerWidth / DESIGN_WIDTH, window.innerHeight / natH)
            : Number(z) / 100;
        scaler.style.transform = `scale(${scale})`;
        sizer.style.width = Math.floor(DESIGN_WIDTH * scale) + 'px';
        sizer.style.height = Math.floor(natH * scale) + 'px';
        sizer.style.margin = '0 auto';
        sizer.style.overflow = 'hidden';
        root.style.overflow = z === 'fit' ? 'hidden' : 'auto';
        root.style.height = '100vh';
    }
    function setTheme(id) {
        st.theme = applyTheme(root, id);
        themeSel.value = st.theme.id;
        store.set(prefix + 'theme', st.theme.id);
        spectrum.setTheme(st.theme);
    }
    function setScreenView(id) {
        if (!screenViewList.some(x => x.id === id)) id = 'sdr';
        st.screenView = id;
        sdrView.style.display = id === 'sdr' ? '' : 'none';
        for (const [k, el] of Object.entries(slots)) el.style.display = k === id ? '' : 'none';
        for (const [k, el] of Object.entries(viewTabEls)) el.classList.toggle('on', k === id);
        store.set(prefix + 'screen', id);
        opts.onScreenView?.(id);
    }
    themeSel.addEventListener('change', () => setTheme(themeSel.value));
    // "Match computer" redraws when Windows (or the browser) flips light/dark.
    try {
        window.matchMedia('(prefers-color-scheme: light)').addEventListener('change', () => {
            if (st.theme?.id === SYSTEM_THEME) { setTheme(SYSTEM_THEME); drawMeterFace(); }
        });
    } catch { /* no matchMedia: nothing to follow */ }
    sizeSel.value = SIZES.includes(store.get(prefix + 'size', 'fit')) ? store.get(prefix + 'size', 'fit') : 'fit';
    sizeSel.addEventListener('change', () => { store.set(prefix + 'size', sizeSel.value); rescale(); });
    window.addEventListener('resize', rescale);

    // The TX warning is fixed across the top of the browser window, outside the
    // scaled board, so it shows however the page is scrolled or sized, until
    // the adapter clears it or DISMISS is pressed.
    const txBanner = h('div', 'rp-txbanner hidden');
    txBanner.setAttribute('role', 'alert');
    const txBannerText = h('span', 'rp-txbanner-text', '');
    const txBannerBtn = h('button', 'rp-txbanner-btn', 'DISMISS');
    txBannerBtn.type = 'button';
    let txBannerDismiss = null;
    txBannerBtn.addEventListener('click', () => txBannerDismiss?.());
    txBanner.append(txBannerText, txBannerBtn);
    root.append(txBanner);

    // Windows (CW, RTTY, Memories ...) sit on the board, so they scale with it.
    const windows = mountPanelWindows({
        board: panel, launcher, groups: opts.windows ?? [], store, prefix,
        onShow: opts.onWindowPanel,
    });

    setTheme(store.get(prefix + 'theme', DEFAULT_THEME));
    setScreenView(store.get(prefix + 'screen', 'sdr'));
    drawMeterFace();
    rescale();
    if (document.fonts?.ready) document.fonts.ready.then(rescale);

    // ---- API -----------------------------------------------------------
    const flagsFor = vfo => (vfo === 'A' ? chipsA : chipsB);
    return {
        setModel(name) { model.textContent = name ?? ''; },
        setFrequency(vfo, hz) {
            st.freq[vfo] = Number(hz) || 0;
            setVfoText(vfo);
            if (vfo === 'A') drawOverlays();
        },
        setMode(vfo, mode) {
            st.mode[vfo] = String(mode ?? '');
            flagsFor(vfo).mode.textContent = st.mode[vfo];
            if (vfo === st.sel) markModeKeys();
            if (vfo === 'A') drawOverlays();
        },
        // `hz` null means the mode has no settable width (AM/FM): no chip, no passband.
        setWidth(vfo, hz) {
            st.widthHz[vfo] = hz;
            flagsFor(vfo).width.textContent = widthText(hz);
            if (vfo === 'A') drawOverlays();
        },
        setAgc(vfo, text) { flagsFor(vfo).agc.textContent = text ? 'AGC ' + text : ''; },
        // Each flag is its text, or { id, label }: one with an id is pressed
        // like key `id`, for this VFO.
        setFlags(vfo, list) {
            const c = flagsFor(vfo);
            c.flags.textContent = '';
            for (const f of list ?? []) {
                const label = typeof f === 'string' ? f : f.label;
                const el = h('span', 'rp-chip' + (vfo === 'B' ? ' dim' : ''), label);
                if (act && f?.id) {
                    el.classList.add('act');
                    el.addEventListener('click', e => { e.stopPropagation(); act('key', f.id, vfo); });
                }
                c.flags.append(el);
            }
        },
        setSMeter(vfo, sValue) {
            st.s[vfo] = st.tx ? 0 : Number(sValue) || 0;
            if (vfo === st.sel) drawNeedle();
            if (vfo === 'B') { sFill.style.width = (sMeterFraction(st.s.B) * 100) + '%'; sTxt.textContent = sMeterText(st.s.B); }
        },
        setTxMeter(name, frac) { st.txMeters[name] = Math.max(0, Math.min(1, Number(frac) || 0)); if (st.tx) drawNeedle(); },
        setTx(on) {
            st.tx = !!on;
            rxtx.className = 'rp-chip ' + (st.tx ? 'tx' : 'rx');
            rxtx.textContent = st.tx ? 'TX' : 'RX';
            if (st.tx) { st.s.A = 0; st.s.B = 0; sFill.style.width = '0%'; sTxt.textContent = 'S0'; }
            drawNeedle();
        },
        // 'A' | 'B': outlines that VFO, tags it SELECTED, and points the
        // needle and mode keys at it. The adapter decides what it means for
        // its radio (YWC: the VS state).
        setSelected(vfo) {
            st.sel = vfo === 'B' ? 'B' : 'A';
            main.classList.toggle('sel', st.sel === 'A');
            sub.classList.toggle('sel', st.sel === 'B');
            selA.classList.toggle('hidden', st.sel !== 'A');
            selB.classList.toggle('hidden', st.sel !== 'B');
            for (const vfo of ['A', 'B']) {
                const g = groupEls[vfo];
                if (!g) continue;
                const on = !dual || st.sel === vfo;
                g.box.classList.toggle('sel', on);
                g.tag.classList.toggle('hidden', !(dual && on));
            }
            if (!dual && groupEls.A) groupEls.A.name.textContent = 'RECEIVER · VFO-' + st.sel;
            markModeKeys();
            drawNeedle();
        },
        selected() { return st.sel; },
        setSplit(on) { splitChip.classList.toggle('hidden', !on); },
        setBand(id) { for (const [b, el] of Object.entries(bandEls)) el.classList.toggle('on', b === id); },
        // `vfo` names a receiver group's key ('A' or 'B'; on a single-receiver
        // radio the one group is 'A').
        setKey(id, { on = false, tx = false, sub } = {}, vfo) {
            const k = keyEls[vfo ? vfo + ':' + id : id];
            if (!k) return;
            k.led.className = 'rp-led' + (tx ? ' tx' : on ? ' on' : '');
            if (k.sub && sub !== undefined) k.sub.textContent = sub ?? '';
        },
        setLamp(id, state) {
            const el = lampEls[id];
            if (el) el.className = 'rp-lamp' + (state && state !== 'off' ? ' ' + state : '');
        },
        // Lit (red) or dim; `title` replaces the hover text when given.
        setStatusChip(id, lit, title) {
            const el = statusChipEls[id];
            if (!el) return;
            el.classList.toggle('tx', !!lit);
            el.classList.toggle('dim', !lit);
            if (title !== undefined) el.title = title ?? '';
        },
        // `text` null hides the banner; onDismiss is what its DISMISS key does.
        setTxWarning(text, onDismiss) {
            txBanner.classList.toggle('hidden', !text);
            txBannerText.textContent = text ?? '';
            txBannerDismiss = text ? (onDismiss ?? null) : null;
            txBannerBtn.hidden = !txBannerDismiss;
        },
        // `frac` 0..1 positions the pointer; null leaves the knob dimmed (no reading yet).
        setKnob(id, frac, text) {
            const k = knobEls[id];
            if (!k) return;
            const known = frac != null && Number.isFinite(Number(frac));
            k.wrap.classList.toggle('idle', !known);
            k.g.setAttribute('transform', `rotate(${knobAngle(known ? frac : 0)} 32 32)`);
            k.val.textContent = text ?? '--';
        },
        // A concentric knob in a group ('A' | 'B' | 'shared'). Fractions are
        // 0..1; null leaves that pointer dimmed. `title` is the hover text.
        setGroupKnob(group, id, { inner, outer, text, title } = {}) {
            const k = groupEls[group]?.knobs[id];
            if (!k) return;
            for (const [g, f] of [[k.outer, outer], [k.inner, inner]]) {
                const known = f != null && Number.isFinite(Number(f));
                g.classList.toggle('idle', !known);
                g.setAttribute('transform', `rotate(${knobAngle(known ? f : 0)} 32 32)`);
            }
            k.val.textContent = text ?? '--';
            if (title !== undefined) k.wrap.title = title ?? '';
        },
        setClarifier(text, frac) {
            clarTxt.textContent = text;
            clarKnob.g.setAttribute('transform', `rotate(${knobAngle(frac ?? 0.5)} 32 32)`);
        },
        setStep(text) { stepTxt.textContent = text ?? ''; },
        // label: e.g. 'SDR A'; status: shown full-screen when there is no picture.
        setSpectrumSource({ label, spanHz, status } = {}) {
            if (spanHz != null && spanHz !== st.spanHz) { st.spanHz = Number(spanHz) || 0; drawOverlays(); }
            specLabel.textContent = label && st.spanHz ? `${label} · SPAN ${spanText(st.spanHz)}` : (label ?? '');
            if (status !== undefined) {
                specEmpty.textContent = status ?? '';
                specEmpty.style.display = status ? 'flex' : 'none';
                if (status) spectrum.clear();
            }
        },
        pushSpectrum(bins) { spectrum.push(bins); },
        /** The spectrum is in its own window (true) or back on the board. */
        setSpectrumAway(away) {
            specAway?.classList.toggle('on', !!away);
            if (away) spectrum.clear();
        },
        clearSpectrum() { spectrum.clear(); },
        setSpots(list) { st.spots = list ?? []; drawOverlays(); },
        setTheme,
        rescale,
        // The empty element for a screenViews entry; the adapter owns its content.
        screenSlot(id) { return slots[id] ?? null; },
        screenView() { return st.screenView; },
        setScreenView,
        // The windows: slot(groupId, panelId) is a panel's body for the adapter to fill.
        windows,
    };
}
