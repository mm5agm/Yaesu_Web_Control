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
import { applyTheme, themeIds, resolveTheme, DEFAULT_THEME } from './radio-panel-themes.js';
import { SpectrumView } from './spectrum-view.js';
import {
    formatFrequency, sMeterFraction, sMeterText, meterGeometry, LOWER_SCALES,
    passbandPercent, axisLabels, spanText, widthText, knobAngle,
} from './panel-math.js';

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

/**
 * @param {HTMLElement} root
 * @param {object} opts
 *   brandShort, brandName, model, dual (bool),
 *   views:  [{ label, href, on }]          layout switch in the top strip
 *   lamps:  [{ id, label }]
 *   keys:   [{ id, label }]                left column, two per row
 *   vfoKeys:[{ id, label }]                under the dial, three per row
 *   bands:  [{ id, label }]   modes: [{ id, label }]
 *   knobs:  [{ id, label }]
 *   storagePrefix, fontBase
 *   onAction(kind, id)                     kind: 'key' | 'band' | 'mode' | 'step' | 'vfoKey'
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

    const button = (cls, kind, id) => {
        const b = h('button', cls);
        b.type = 'button';
        if (act && kind) b.addEventListener('click', () => act(kind, id));
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
        const b = button('rp-key', 'key', k.id);
        const led = h('span', 'rp-led'), sub = h('span', 'rp-sub', '');
        b.append(led, h('span', null, k.label), sub);
        keysGrid.append(b);
        keyEls[k.id] = { b, led, sub };
    }
    left.append(meterWell, meterBtns, keysGrid);

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
    mainHead.append(mainName, rxtx, splitChip);
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
    subHead.append(subName, subRx);
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

    const spec = h('div', 'rp-spectrum');
    const pb = h('div', 'rp-passband');
    const cursor = h('div', 'rp-cursor');
    const spots = h('div');
    const specLabel = h('div', 'rp-spec-label', '');
    const specEmpty = h('div', 'rp-spec-empty', 'No SDR');
    spec.append(pb, cursor, spots, specLabel, specEmpty);
    const axis = h('div', 'rp-axis');
    const wf = h('div', 'rp-waterfall');

    const bandsRow = h('div', 'rp-bands');
    const bandEls = {};
    for (const b of opts.bands ?? []) { const el = button('rp-soft', 'band', b.id); el.textContent = b.label; bandsRow.append(el); bandEls[b.id] = el; }
    const modesRow = h('div', 'rp-modes');
    const modeEls = {};
    for (const m of opts.modes ?? []) { const el = button('rp-soft', 'mode', m.id); el.textContent = m.label; modesRow.append(el); modeEls[m.id] = el; }

    screen.append(vfos, spec, axis, wf, bandsRow, modesRow);

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
        const b = button('rp-key', 'vfoKey', k.id);
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
    for (const k of opts.knobs ?? []) {
        const wrap = h('div', 'rp-knob idle');
        const kn = knobSvg(88);
        const val = h('span', 'rp-lcd', '--');
        wrap.append(h('span', 'rp-legend', k.label), kn.svg, val);
        knobs.append(wrap);
        knobEls[k.id] = { wrap, g: kn.g, val };
    }

    panel.append(top, body, knobs);

    // ---- state ---------------------------------------------------------
    const st = {
        freq: { A: 0, B: 0 }, mode: { A: '', B: '' }, widthHz: { A: null, B: null },
        s: { A: 0, B: 0 }, tx: false, meter: store.get(prefix + 'meter', 'PO'),
        txMeters: {}, spanHz: 0, theme: null, spots: [],
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
        const frac = st.tx ? (st.txMeters[st.meter] ?? 0) : sMeterFraction(st.s.A);
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

    function setVfoText(vfo) {
        const f = formatFrequency(st.freq[vfo]);
        if (vfo === 'A') { mainBig.textContent = f.big; mainSmall.textContent = f.small; }
        else { subBig.textContent = f.big; subSmall.textContent = f.small; }
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
    themeSel.addEventListener('change', () => setTheme(themeSel.value));
    sizeSel.value = SIZES.includes(store.get(prefix + 'size', 'fit')) ? store.get(prefix + 'size', 'fit') : 'fit';
    sizeSel.addEventListener('change', () => { store.set(prefix + 'size', sizeSel.value); rescale(); });
    window.addEventListener('resize', rescale);

    setTheme(store.get(prefix + 'theme', DEFAULT_THEME));
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
            if (vfo === 'A') {
                for (const [id, el] of Object.entries(modeEls)) el.classList.toggle('on', id === st.mode.A);
                drawOverlays();
            }
        },
        // `hz` null means the mode has no settable width (AM/FM): no chip, no passband.
        setWidth(vfo, hz) {
            st.widthHz[vfo] = hz;
            flagsFor(vfo).width.textContent = widthText(hz);
            if (vfo === 'A') drawOverlays();
        },
        setAgc(vfo, text) { flagsFor(vfo).agc.textContent = text ? 'AGC ' + text : ''; },
        setFlags(vfo, list) {
            const c = flagsFor(vfo);
            c.flags.textContent = '';
            for (const f of list ?? []) c.flags.append(h('span', 'rp-chip' + (vfo === 'B' ? ' dim' : ''), f));
        },
        setSMeter(vfo, sValue) {
            st.s[vfo] = st.tx ? 0 : Number(sValue) || 0;
            if (vfo === 'A') drawNeedle();
            else { sFill.style.width = (sMeterFraction(st.s.B) * 100) + '%'; sTxt.textContent = sMeterText(st.s.B); }
        },
        setTxMeter(name, frac) { st.txMeters[name] = Math.max(0, Math.min(1, Number(frac) || 0)); if (st.tx) drawNeedle(); },
        setTx(on) {
            st.tx = !!on;
            rxtx.className = 'rp-chip ' + (st.tx ? 'tx' : 'rx');
            rxtx.textContent = st.tx ? 'TX' : 'RX';
            if (st.tx) { st.s.A = 0; st.s.B = 0; sFill.style.width = '0%'; sTxt.textContent = 'S0'; }
            drawNeedle();
        },
        setSplit(on) { splitChip.classList.toggle('hidden', !on); },
        setBand(id) { for (const [b, el] of Object.entries(bandEls)) el.classList.toggle('on', b === id); },
        setKey(id, { on = false, tx = false, sub } = {}) {
            const k = keyEls[id];
            if (!k) return;
            k.led.className = 'rp-led' + (tx ? ' tx' : on ? ' on' : '');
            if (k.sub && sub !== undefined) k.sub.textContent = sub ?? '';
        },
        setLamp(id, state) {
            const el = lampEls[id];
            if (el) el.className = 'rp-lamp' + (state && state !== 'off' ? ' ' + state : '');
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
        clearSpectrum() { spectrum.clear(); },
        setSpots(list) { st.spots = list ?? []; drawOverlays(); },
        setTheme,
        rescale,
    };
}
