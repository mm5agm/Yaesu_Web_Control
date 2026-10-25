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
import { spectrumSettingsPanel, tidySpectrumSettings } from './spectrum-settings.js';
import {
    formatFrequency, sMeterFraction, sMeterText, meterGeometry, LOWER_SCALES,
    passbandPercent, axisLabels, spanText, widthText, knobAngle,
} from './panel-math.js';
import { mountPanelWindows } from './panel-windows.js';

const DESIGN_WIDTH = 1600;
const SIZES = ['fit', '50', '67', '75', '90', '100', '110', '125', '150'];
const MIN_SIZE = 30, MAX_SIZE = 200;   // percent, for the corner grip
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

// Tooltips for the meter keys; an app's opts.meterTitles overrides them.
const METER_TITLES = {
    PO: 'Power output', SWR: 'SWR', ALC: 'ALC', COMP: 'Speech compression',
    IDD: 'Final amplifier current', VDD: 'Supply voltage', TEMP: 'PA temperature',
};

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

// Makes `el` turn like a knob: the mouse wheel over it, or a drag round its
// centre. `partAt(x, y)` names the part under the pointer (a ring or the knob
// inside it) and is fixed for the length of a drag. `onStep(part, dir)` is
// called once per wheel notch or per `deg` of drag, dir +1 clockwise; and
// `onTurn(part, deg)`, when given, follows the drag so the part can rotate
// with the pointer. The part under the pointer (or being dragged) is named in
// `data-hot` ('all' when the control is one part) so the style can light it.
function turnable(el, { deg = 15, partAt = () => undefined, onStep, onTurn }) {
    el.classList.add('rp-turn');
    const hot = part => { el.dataset.hot = part ?? 'all'; };
    el.addEventListener('pointerleave', () => { if (!drag) delete el.dataset.hot; });
    el.addEventListener('wheel', e => {
        if (!e.deltaY) return;
        e.preventDefault();
        const part = partAt(e.clientX, e.clientY), dir = e.deltaY < 0 ? 1 : -1;
        hot(part);
        onTurn?.(part, dir * deg);
        onStep(part, dir);
    }, { passive: false });
    let drag = null;
    const angle = e => {
        const rc = el.getBoundingClientRect();
        return Math.atan2(e.clientY - (rc.top + rc.height / 2), e.clientX - (rc.left + rc.width / 2)) * 180 / Math.PI;
    };
    el.addEventListener('pointerdown', e => {
        if (e.button !== 0) return;
        drag = { id: e.pointerId, part: partAt(e.clientX, e.clientY), last: angle(e), acc: 0 };
        hot(drag.part);
        el.setPointerCapture?.(e.pointerId);
        el.classList.add('turning');
        e.preventDefault();
    });
    el.addEventListener('pointermove', e => {
        if (!drag) return hot(partAt(e.clientX, e.clientY));
        if (e.pointerId !== drag.id) return;
        const a = angle(e);
        let d = a - drag.last;
        if (d > 180) d -= 360; else if (d < -180) d += 360;
        drag.last = a;
        onTurn?.(drag.part, d);
        drag.acc += d;
        while (Math.abs(drag.acc) >= deg) {
            const dir = drag.acc > 0 ? 1 : -1;
            drag.acc -= dir * deg;
            onStep(drag.part, dir);
        }
    });
    const end = e => {
        if (!drag || e.pointerId !== drag.id) return;
        drag = null;
        el.classList.remove('turning');
        if (!el.matches(':hover')) delete el.dataset.hot;
    };
    el.addEventListener('pointerup', end);
    el.addEventListener('pointercancel', end);
}

/**
 * @param {HTMLElement} root
 * @param {object} opts
 *   brandShort, brandName, model, dual (bool),
 *   views:  [{ label, href, on }]          layout switch: a VIEWS key in the top
 *                                          strip dropping down the views, the `on`
 *                                          one highlighted
 *   menu:   [{ group, items: [{ label, href, title }] }]  optional: a MENU key at the
 *                                          right of the top strip, dropping down the
 *                                          app's other pages. Each opens in a new tab,
 *                                          so the panel keeps running behind it
 *   lamps:  [{ id, label, click, title }]  `click` makes the lamp a key: act('lamp', id)
 *   keys:   [{ id, label, inert, hold, title }]  left column, two per row; `inert` keeps
 *                                          one key display-only even with onAction
 *                                          (e.g. anything that would transmit);
 *                                          `hold` makes it a press-and-hold key that
 *                                          acts act('keyDown', id) / act('keyUp', id)
 *                                          instead of act('key', id) (push to talk)
 *   vfoKeys:[{ id, label, inert, title }]  under the dial, vfoKeyColumns per row (default 3)
 *   dualDial (bool)                        on a dual-receiver radio, the dial is an outer
 *                                          ring (MAIN, VFO-A) round an inner knob (SUB,
 *                                          VFO-B), each tagged, the selected one glowing.
 *                                          The wheel over either acts as
 *                                          act('dial', 'up' | 'down', vfo); without it the
 *                                          wheel over the dial acts with vfo undefined
 *   memKeys:[{ id, label, sub, inert, title }]  a Memory group under the VFO keys,
 *                                          "acts on" the selected receiver: act('memKey', id)
 *   clarKeys:[{ id, label }]               small keys beside the clarifier readout,
 *                                          act('vfoKey', id); lit through setKey(id, { on })
 *   title on any key, knob, band or mode   its tooltip; the board's own controls have
 *                                          theirs built in (meterTitles: { PO: ... })
 *   meters: ['PO', ...]                    the meter keys under the S-meter (default
 *                                          PO SWR ALC COMP IDD); meterScales: { name:
 *                                          [five legends] } for any not in LOWER_SCALES
 *   liveMeters: ['VDD', ...]               meters read in receive as well, so their
 *                                          needle shows whenever they are chosen; the
 *                                          rest show the S-meter until TX
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
 *   spectrumPanes: [{ id, tab, name }]     optional: one spectrum per SDR, id 'A' or 'B'
 *                                          (the VFO it follows). Each gets a tab, and a
 *                                          'Both' tab stacks them, the selected receiver's
 *                                          pane outlined. Without it there is one pane, A
 *   windows: [{ id, key, title, pos, panels }]  optional: the launcher keys and the
 *                                          windows they open - see panel-windows.js.
 *                                          The adapter fills each panel through
 *                                          api.windows.slot(groupId, panelId)
 *   onWindowPanel(groupId, panelId, shown) told when a window panel shows or hides
 *   onWindowPopout(groupId, panelId)       a window panel's ⇱ key (panels with popout set)
 *   onSpectrumPopout(), onSpectrumBack()   optional: a ⧉ key on the spectrum, and the
 *                                          BRING BACK key shown while setSpectrumAway(true)
 *   spectrumTuneTo(hz, paneId)             optional: the Hz a spectrum click at hz will put
 *                                          the dial on, when the app tunes somewhere else
 *                                          (an RTTY mark offset); the readout shows it
 *   frequencyEntry (bool)                  show an ENT chip in each VFO box
 *   clock (bool)                           a UTC clock in the top bar, from the PC's clock
 *   fullscreenKey (bool)                   bare F goes full screen (not while typing)
 *   vfoChips: ['ant', 'roof', ...]         optional: more picker chips in each VFO box,
 *                                          after AGC; filled with setChip(vfo, kind,
 *                                          text) and listed by choices(kind, vfo)
 *   storagePrefix, fontBase
 *   onAction(kind, id, vfo)                kind: 'key' | 'keyDown' | 'keyUp' (a `hold` key)
 *                                          | 'band' | 'mode' | 'step' | 'vfoKey'
 *                                          | 'vfo' (the operator clicked VFO 'A' or 'B')
 *                                          | 'width' | 'agc' | a vfoChips kind (picked
 *                                            from a VFO box chip)
 *                                          | 'tune' (id = signed Hz, from the wheel over a
 *                                            frequency digit)
 *                                          | 'entry' (the ENT chip in a VFO box: the
 *                                            operator wants to type a frequency)
 *                                          | 'spectrum' (id = the Hz clicked on a spectrum
 *                                            or waterfall, vfo the pane)
 *                                          | 'span' (id the span in Hz, vfo the pane;
 *                                            with opts.spectrumSpans [{ hz, label? }])
 *                                          | 'dial' (id 'up' | 'down'; vfo names the
 *                                            ring or knob of a dual dial, or the
 *                                            spectrum pane the wheel was over)
 *                                          | 'knob' (a turned knob, id 'afrf:inner' etc.
 *                                            or 'clar'; a fourth argument, +1 or -1, is
 *                                            the direction; only knobs given `turn: true`,
 *                                            and the clarifier with opts.clarTurn). `vfo` is set when the
 *                                          action came from inside a VFO box and names
 *                                          that box; otherwise it is undefined.
 *   choices(kind, vfo)                     kind 'mode' | 'width' | 'agc' | a vfoChips
 *                                          kind: the list a click
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
    // A key that acts while it is held: keyDown on press, keyUp on release,
    // however the press ends - lifted on the key or off it, cancelled by the
    // browser, or the window losing focus mid-press - so a held key can never
    // be left down.
    const holdButton = (cls, id, inert = false) => {
        const b = h('button', cls);
        b.type = 'button';
        if (!act || inert) { b.disabled = true; return b; }
        let down = false;
        const up = () => {
            if (!down) return;
            down = false;
            b.classList.remove('held');
            act('keyUp', id);
        };
        b.addEventListener('pointerdown', e => {
            if (e.button !== 0 || down) return;
            down = true;
            try { b.setPointerCapture?.(e.pointerId); } catch { /* blur and pointerup still end it */ }
            b.classList.add('held');
            act('keyDown', id);
        });
        b.addEventListener('pointerup', up);
        b.addEventListener('pointercancel', up);
        b.addEventListener('lostpointercapture', up);
        window.addEventListener('blur', up);
        // A long touch would otherwise open the context menu and end the press.
        b.addEventListener('contextmenu', e => e.preventDefault());
        b.style.touchAction = 'none';
        return b;
    };

    // ---- top strip -----------------------------------------------------
    const top = h('div', 'rp-top');
    const brand = h('div', 'rp-brand');
    const model = h('span', 'rp-brand-model', opts.model ?? '');
    brand.append(h('span', 'rp-brand-short', opts.brandShort ?? ''), Object.assign(h('span', 'rp-legend', opts.brandName ?? ''), { style: 'color:var(--rp-legend-dim)' }), model);

    // A key that drops down a list of links: the view switch and MENU.
    // Closes on a pick, a click anywhere else, or Esc. `newTab` opens the
    // links in a new tab, so the panel keeps running behind them.
    const dropKey = (label, title, groups, { newTab = false, alignLeft = false } = {}) => {
        const wrap = h('div', 'rp-menu');
        const key = h('button', 'rp-viewbtn', label);
        key.type = 'button';
        key.title = title;
        key.setAttribute('aria-haspopup', 'true');
        key.setAttribute('aria-expanded', 'false');
        const list = h('div', 'rp-menu-list' + (alignLeft ? ' rp-menu-left' : ''));
        list.hidden = true;
        for (const g of groups) {
            if (g.group) list.append(h('div', 'rp-menu-group', g.group));
            for (const it of g.items ?? []) {
                const a = h('a', 'rp-menu-item' + (it.on ? ' on' : ''), it.label);
                a.href = it.href;
                if (newTab) { a.target = '_blank'; a.rel = 'noopener'; }
                if (it.on) a.setAttribute('aria-current', 'page');
                if (it.title) a.title = it.title;
                a.addEventListener('click', () => show(false));
                list.append(a);
            }
        }
        const show = on => {
            list.hidden = !on;
            key.classList.toggle('on', on);
            key.setAttribute('aria-expanded', String(on));
        };
        key.addEventListener('click', () => show(list.hidden));
        document.addEventListener('pointerdown', e => { if (!list.hidden && !wrap.contains(e.target)) show(false); });
        document.addEventListener('keydown', e => { if (e.key === 'Escape' && !list.hidden) show(false); });
        wrap.append(key, list);
        const bar = h('div', 'rp-bar');
        bar.append(wrap);
        return bar;
    };

    // Reads VIEWS, never the current view's name, so it is plain there
    // are others to pick.
    const viewList = opts.views ?? [];
    const views = viewList.length
        ? dropKey('Views ▾', 'Choose a view. The one you used last opens when the app starts.',
            [{ items: viewList }], { alignLeft: true })
        : h('div', 'rp-bar');

    const right = h('div', 'rp-top-right');
    const lamps = h('div', 'rp-lamps rp-legend');
    // Bare F goes full screen, as on the classic page; Esc leaves (the
    // browser's own). Not while typing, and never with a modifier, so
    // Ctrl+F still finds in the page.
    if (opts.fullscreenKey)
        document.addEventListener('keydown', e => {
            if (e.key !== 'f' && e.key !== 'F') return;
            if (e.ctrlKey || e.metaKey || e.altKey || e.repeat || document.fullscreenElement) return;
            const t = e.target;
            if (t?.closest?.('input, textarea, select, [contenteditable=""], [contenteditable="true"]')) return;
            document.documentElement.requestFullscreen?.()?.catch?.(() => {});
            e.preventDefault();
        });
    // UTC from this computer's clock, as on the classic page.
    if (opts.clock) {
        const clock = h('span', 'rp-clock', '--:--:--Z');
        clock.title = "UTC from this computer's clock";
        const tick = () => { clock.textContent = new Date().toISOString().slice(11, 19) + 'Z'; };
        tick();
        setInterval(tick, 1000);
        lamps.append(clock);
    }
    const lampEls = {};
    for (const l of opts.lamps ?? []) {
        const wrap = l.click && act ? button('rp-lampkey', 'lamp', l.id) : h('span');
        if (l.title) wrap.title = l.title;
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

    // MENU: the app's other pages, one key wide.
    if (opts.menu?.length)
        right.append(dropKey('MENU', 'Settings, memories, the manual and the other pages, each in a new tab',
            opts.menu, { newTab: true }));
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
    const meterList = opts.meters ?? METERS;
    const meterScales = { ...LOWER_SCALES, ...opts.meterScales };
    const liveMeters = new Set(opts.liveMeters ?? []);
    if (meterList.length !== 5) {
        meterBtns.style.gridTemplateColumns = `repeat(${meterList.length},minmax(0,1fr))`;
        meterBtns.classList.add('many');
    }
    for (const m of meterList) {
        const b = h('button', 'rp-chipbtn', m);
        b.type = 'button';
        b.title = (opts.meterTitles?.[m] ?? METER_TITLES[m] ?? m) + ' on the lower meter scale';
        b.addEventListener('click', () => chooseMeter(m));
        meterBtns.append(b);
        meterBtnEls[m] = b;
    }
    const keysGrid = h('div', 'rp-keys');
    const keyEls = {};
    for (const k of opts.keys ?? []) {
        const b = k.hold ? holdButton('rp-key', k.id, k.inert) : button('rp-key', 'key', k.id, k.inert);
        if (k.title) b.title = k.title;
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
    rxtx.title = 'RX while receiving, TX while transmitting';
    const splitChip = h('span', 'rp-chip hidden', 'SPLIT');
    const selA = h('span', 'rp-chip sel hidden', 'SELECTED');
    selA.title = 'The selected receiver: the keys and the mode, band and memory rows act on it';
    // ENT: type a frequency into this VFO (the adapter opens its keypad).
    const entA = h('span', 'rp-chip ent hidden', 'ENT'), entB = h('span', 'rp-chip ent hidden', 'ENT');
    // OUT OF BAND: the adapter says when a VFO is outside every band.
    const oobA = h('span', 'rp-chip oob hidden', 'OUT OF BAND'), oobB = h('span', 'rp-chip oob hidden', 'OUT OF BAND');
    mainHead.append(mainName, rxtx, splitChip, selA, oobA, entA);
    const mainFreq = h('div', 'rp-freq');
    const mainBig = h('span', 'big', '-.---'), mainSmall = h('span', 'small', '.---');
    mainFreq.append(mainBig, mainSmall);
    const mainChips = h('div', 'rp-chips');
    const chip = cls => h('span', 'rp-chip' + (cls ? ' ' + cls : ''), '');
    // Extra picker chips the adapter asks for; empty text hides one.
    const extraKinds = (opts.vfoChips ?? []).filter(k => !['mode', 'width', 'agc', 'flags'].includes(k));
    const extraChips = cls => Object.fromEntries(extraKinds.map(k => [k, chip(cls)]));
    const chipsA = { mode: chip('mode'), width: chip(), agc: chip(), ...extraChips(), flags: h('span') };
    chipsA.flags.style.display = 'contents';
    mainChips.append(chipsA.mode, chipsA.width, chipsA.agc, ...extraKinds.map(k => chipsA[k]), chipsA.flags);
    main.append(mainHead, mainFreq, mainChips);

    const sub = h('div', 'rp-subvfo');
    const subHead = h('div', 'rp-vfo-head');
    const subName = h('span', 'rp-vfo-name', dual ? 'SUB · VFO-B' : 'VFO-B');
    const subRx = h('span', 'rp-chip dim', dual ? 'RX' : '');
    subRx.title = 'RX while receiving, TX while transmitting';
    const selB = h('span', 'rp-chip sel hidden', 'SELECTED');
    selB.title = selA.title;
    subHead.append(subName, subRx, selB, oobB, entB);
    const subFreq = h('div', 'rp-subfreq');
    const subBig = h('span', 'big', '-.---'), subSmall = h('span', 'small', '.---');
    subFreq.append(subBig, subSmall);
    const subChips = h('div', 'rp-chips');
    const chipsB = { mode: chip('dim'), width: chip('dim'), agc: chip('dim'), ...extraChips('dim'), flags: h('span') };
    chipsB.flags.style.display = 'contents';
    subChips.append(chipsB.mode, chipsB.width, chipsB.agc, ...extraKinds.map(k => chipsB[k]), chipsB.flags);
    for (const k of extraKinds) { chipsA[k].hidden = true; chipsB[k].hidden = true; }
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
            for (const kind of ['mode', 'width', 'agc', ...extraKinds]) {
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

    // One spectrum pane per SDR, each following its own VFO.
    const paneDefs = (opts.spectrumPanes ?? []).filter(x => x.id === 'A' || x.id === 'B');
    if (!paneDefs.length) paneDefs.push({ id: 'A', tab: 'SDR', name: '' });
    const sdrView = h('div', 'rp-sdrview');
    const panes = {};         // 'A' | 'B' -> { box, spec, pb, spots, label, empty, axis, wf, view, name, text }
    for (const d of paneDefs) {
        const box = h('div', 'rp-specpane rp-specpane-' + d.id);
        const spec = h('div', 'rp-spectrum');
        const pb = h('div', 'rp-passband');
        const spotsEl = h('div');
        const marksEl = h('div');
        const label = h('div', 'rp-spec-label', '');
        const empty = h('div', 'rp-spec-empty', 'Waiting for data');
        const oob = h('div', 'rp-spec-oob', 'OUT OF BAND');
        spec.append(pb, marksEl, h('div', 'rp-cursor'), spotsEl, label, oob, empty);
        const axis = h('div', 'rp-axis');
        const wf = h('div', 'rp-waterfall');
        // The span picker, HOLD and the settings key sit in the waterfall's
        // bottom-left corner (the view tabs have the right). The span is the
        // SDR's: act('span', hz, pane id). The rest are only how this pane
        // draws, so they stay in the browser.
        const tools = h('div', 'rp-spec-tools');
        for (const ev of ['pointerdown', 'click', 'wheel']) tools.addEventListener(ev, e => e.stopPropagation());
        let spanSel = null;
        if (act && opts.spectrumSpans?.length) {
            spanSel = h('select', 'rp-select rp-spec-span');
            spanSel.title = 'Spectrum span';
            // Until the SDR reports its span.
            const unknown = h('option', null, 'SPAN');
            unknown.value = '';
            unknown.disabled = unknown.hidden = true;
            spanSel.append(unknown);
            for (const sp of opts.spectrumSpans) {
                const o = h('option', null, 'SPAN ' + (sp.label ?? spanText(sp.hz)));
                o.value = String(sp.hz);
                spanSel.append(o);
            }
            spanSel.value = '';
            spanSel.addEventListener('change', () => act('span', spanSel.value, d.id));
            tools.append(spanSel);
        }
        const holdKey = h('button', 'rp-wkey rp-spec-hold', 'HOLD');
        holdKey.type = 'button';
        holdKey.title = 'Freeze the spectrum and waterfall';
        const setKey = h('button', 'rp-wkey rp-spec-setkey', 'SCOPE');
        setKey.type = 'button';
        setKey.title = 'Range, waterfall speed and brightness, smoothing, passband';
        tools.append(holdKey, setKey);
        wf.append(tools);
        const sets = specSettings(d.id);
        box.append(spec, axis, wf, sets.el);
        holdKey.addEventListener('click', () => sets.change({ hold: !sets.get().hold }));
        setKey.addEventListener('click', () => { sets.el.classList.toggle('open'); setKey.classList.toggle('on', sets.el.classList.contains('open')); });
        // A click on the spectrum or waterfall names the frequency under it,
        // or a DX spot's own frequency when the click is within 8 px of its
        // marker: act('spectrum', Hz, pane id). The pane's centre is its dial.
        // The readout below names exactly what a click there would send: both
        // come from hzAt, and from the same x. A click's clientX is whole CSS
        // pixels while a pointer's is fractional (display scaling), and at a
        // 1 MHz span one pixel is over 1 kHz - so the click takes the pointer's
        // last x when it is within a pixel of it, or the readout and the radio
        // disagree by a few hundred Hz (Colin, 2026-10-15: 14.815.690 read,
        // 14.815.439 tuned).
        const hzAt = (el, clientX) => {
            const span = st.spanHz[d.id], f = st.freq[d.id];
            const rc = el.getBoundingClientRect();
            if (!(span > 0 && f > 0) || !rc.width) return null;
            const frac = Math.max(0, Math.min(1, (clientX - rc.left) / rc.width));
            let hz = Math.round(f + (frac - 0.5) * span), spot = null;
            let best = 8 / rc.width * span;
            for (const sp of st.spots) {
                const gap = Math.abs(sp.hz - hz);
                if (gap <= best) { best = gap; spot = sp; }
            }
            if (spot) hz = spot.hz;
            return { hz, frac, spot };
        };
        let lastX = null;
        if (act)
            for (const el of [spec, wf]) {
                el.classList.add('rp-tunable');
                el.title = 'Click to tune there; mouse wheel steps the dial; Shift+click marks a frequency (again on the mark clears it)';
                el.addEventListener('click', e => {
                    if (e.target.closest('select, button, .rp-spec-tools')) return;
                    const x = lastX != null && Math.abs(lastX - e.clientX) < 1 ? lastX : e.clientX;
                    const at = hzAt(el, x);
                    if (!at) return;
                    // Shift+click drops a mark to come back to, as the classic
                    // spectrum does, and doesn't tune; on or within 8 px of the
                    // mark it clears it. Not saved, as on the classic page.
                    if (e.shiftKey) {
                        const pn = panes[d.id], span = st.spanHz[d.id];
                        const w = el.getBoundingClientRect().width || 1;
                        pn.pinHz = pn.pinHz != null && Math.abs(pn.pinHz - at.hz) <= 8 / w * span ? null : at.hz;
                        drawPin(pn, st.freq[d.id], span);
                        return;
                    }
                    act('spectrum', String(at.hz), d.id);
                });
                // The wheel tunes that pane's VFO one step a notch, as the dial does.
                el.addEventListener('wheel', e => {
                    if (!e.deltaY || e.target.closest('select')) return;
                    e.preventDefault();
                    act('dial', e.deltaY < 0 ? 'up' : 'down', d.id);
                }, { passive: false });
            }
        // A hairline and readout follow the pointer over the spectrum and
        // waterfall, naming the frequency under it, as the classic spectrum does.
        const xRead = h('div', 'rp-xread');
        const xLines = [spec, wf].map(el => { const x = h('div', 'rp-xhair'); el.append(x); return x; });
        spec.append(xRead);
        const xHide = () => { xRead.style.display = 'none'; for (const x of xLines) x.style.display = 'none'; };
        xHide();
        let xOver = null;
        const xShow = (el, clientX) => {
            const at = hzAt(el, clientX);
            if (!at) return xHide();
            const { frac } = at;
            const fr = formatFrequency(at.hz);
            for (const x of xLines) { x.style.left = frac * 100 + '%'; x.style.display = 'block'; }
            // Where the dial will land, when the app tunes somewhere other than
            // the click (Yaesu RTTY puts mark on the dial, half the shift
            // above the clicked midpoint) - so the readout never surprises.
            const to = opts.spectrumTuneTo?.(at.hz, d.id);
            const dial = Number.isFinite(to) && Math.round(to) !== at.hz ? formatFrequency(Math.round(to)) : null;
            xRead.textContent = fr.big + fr.small + (at.spot ? ' ' + at.spot.call : '')
                + (dial ? ' → dial ' + dial.big + dial.small : '');
            xRead.style.display = 'block';
            xRead.classList.toggle('left', frac > 0.75);
            xRead.style.left = frac * 100 + '%';
        };
        // When the dial moves (the wheel, or the radio's knob) the trace slides
        // under a still pointer: name what is under it now, not what was.
        const xReread = () => { if (xOver && lastX != null) xShow(xOver, lastX); };
        for (const el of [spec, wf]) {
            el.addEventListener('pointermove', e => {
                lastX = e.clientX;
                xOver = e.target.closest('select, button, .rp-spec-tools') ? null : el;
                if (!xOver) return xHide();
                xShow(el, e.clientX);
            });
            el.addEventListener('pointerleave', () => { lastX = null; xOver = null; xHide(); });
        }
        const pinLines = [spec, wf].map(el => { const x = h('div', 'rp-pin'); el.append(x); return x; });
        const pinRead = h('div', 'rp-pin-read');
        spec.append(pinRead);
        sdrView.append(box);
        panes[d.id] = { box, spec, pb, spots: spotsEl, marks: marksEl, label, empty, oob, spanSel, holdKey, sets, axis, wf, name: d.name ?? '', text: '',
            pinHz: null, pinLines, pinRead, reread: xReread };
        drawPin(panes[d.id], 0, 0);
    }

    // A pane's drawing settings - the classic spectrum's Range, Speed,
    // Bright, Smooth, Hold and Passband - kept per pane in the browser.
    function specSettings(id) {
        const key = prefix + 'spectrum.' + id;
        let saved = {};
        try { saved = JSON.parse(store.get(key, '{}')) ?? {}; } catch { /* keep defaults */ }
        let cur = tidySpectrumSettings(saved);
        const panel = spectrumSettingsPanel(p => change(p));
        const el = panel.el;
        function change(p) {
            cur = tidySpectrumSettings({ ...cur, ...p });
            store.set(key, JSON.stringify(cur));
            apply();
        }
        function apply() {
            panel.show(cur);
            const pn = panes[id];
            if (!pn) return;
            pn.view?.setSettings(cur);
            pn.holdKey.classList.toggle('on', cur.hold);
            pn.box.classList.toggle('held', cur.hold);
            drawOverlays(id);
        }
        // The pop-out window's HOLD and SCOPE panel write the same key.
        window.addEventListener('storage', e => {
            if (e.key !== key) return;
            let s = {};
            try { s = JSON.parse(e.newValue ?? '{}') ?? {}; } catch { return; }
            cur = tidySpectrumSettings(s);
            apply();
        });
        return { el, change, apply, get: () => cur };
    }

    // The spectrum and any other view share the space between the VFOs and
    // the band keys; the switch sits in the waterfall's corner, which is the
    // one place DX spots never land. With two panes the spectrum has a tab
    // each, and Both; every other view is one tab.
    const specViews = paneDefs.length > 1 ? [...paneDefs.map(d => d.id), 'both'] : [paneDefs[0].id];
    const screenViewList = [{ id: 'sdr', label: 'SDR' }, ...(opts.screenViews ?? []).filter(x => x.id !== 'sdr')];
    const tabList = [
        ...(paneDefs.length > 1
            ? [...paneDefs.map(d => ({ view: 'sdr', spec: d.id, label: d.tab ?? d.id })), { view: 'sdr', spec: 'both', label: 'Both' }]
            : [{ view: 'sdr', spec: paneDefs[0].id, label: 'SDR' }]),
        ...screenViewList.filter(x => x.id !== 'sdr').map(x => ({ view: x.id, label: x.label })),
    ];
    const viewArea = h('div', 'rp-viewarea');
    viewArea.append(sdrView);
    // The spectrum can go to a window of its own (a second screen); while it
    // is there the board shows where it went, and a key to fetch it back.
    let specAway = null;
    if (typeof opts.onSpectrumPopout === 'function') {
        const pop = h('button', 'rp-wkey rp-spec-pop', '⧉');
        pop.type = 'button';
        pop.title = 'Spectrum in its own window';
        pop.addEventListener('click', () => opts.onSpectrumPopout());
        sdrView.append(pop);
        specAway = h('div', 'rp-spec-away');
        const back = h('button', 'rp-wkey', 'BRING BACK');
        back.type = 'button';
        back.addEventListener('click', () => opts.onSpectrumBack?.());
        specAway.append(h('span', null, 'Spectrum is in its own window'), back);
        sdrView.append(specAway);
    }
    const slots = {};
    const viewTabs = h('div', 'rp-viewtabs');
    const viewTabEls = [];     // [{ el, view, spec }]
    for (const vw of screenViewList) {
        if (vw.id !== 'sdr') {
            slots[vw.id] = h('div', 'rp-altview');
            slots[vw.id].style.display = 'none';
            viewArea.append(slots[vw.id]);
        }
    }
    for (const tb of tabList) {
        const t = h('button', 'rp-viewtab', tb.label);
        t.type = 'button';
        t.title = tb.spec === 'both' ? 'Both spectrums, one above the other'
            : tb.spec ? `The ${tb.label} spectrum` : `Show the ${tb.label}`;
        t.addEventListener('click', () => { if (tb.spec) setSpecView(tb.spec); setScreenView(tb.view); });
        viewTabs.append(t);
        viewTabEls.push({ el: t, ...tb });
    }
    if (tabList.length > 1) viewArea.append(viewTabs);

    const bandsRow = h('div', 'rp-bands');
    const bandEls = {};
    for (const b of opts.bands ?? []) { const el = button('rp-soft', 'band', b.id); el.textContent = b.label; el.title = b.title ?? `${b.label} band`; bandsRow.append(el); bandEls[b.id] = el; }
    const modesRow = h('div', 'rp-modes');
    const modeEls = {};
    for (const m of opts.modes ?? []) { const el = button('rp-soft', 'mode', m.id); el.textContent = m.label; el.title = m.title ?? `${m.label} mode`; modesRow.append(el); modeEls[m.id] = el; }

    // One row each, however many the radio has: IWC has 12 bands and 10 modes,
    // YWC 11 and 8.
    bandsRow.style.gridTemplateColumns = `repeat(${Math.max(1, bandsRow.childElementCount)},minmax(0,1fr))`;
    modesRow.style.gridTemplateColumns = `repeat(${Math.max(1, modesRow.childElementCount)},minmax(0,1fr))`;
    screen.append(vfos, viewArea, bandsRow, modesRow);

    // right: dial and VFO keys
    const rightCol = h('div', 'rp-right');
    const dualDial = dual && !!opts.dualDial;
    const dial = h('div', 'rp-dial' + (dualDial ? ' dual' : ''));
    const dialInner = h('div', 'rp-dial-inner');
    dialInner.append(h('div', 'rp-dial-dimple'));
    let dialKnob = null;
    if (dualDial) {
        // The well between the ring and the knob, then the knob itself.
        const well = h('div', 'rp-dial-well');
        dialKnob = h('div', 'rp-dial-knob');
        dialKnob.append(dialInner);
        well.append(dialKnob);
        dial.append(well);
    } else dial.append(dialInner);
    dial.title = dualDial
        ? 'Tune: mouse wheel or drag. The outer ring tunes MAIN (VFO-A), the inner knob SUB (VFO-B)'
        : 'Tune: mouse wheel or drag';
    const dialTags = {};
    const dialTagRow = h('div', 'rp-dial-tags');
    if (dualDial)
        for (const [vfo, part, name] of [['A', 'OUTER RING', 'MAIN · VFO-A'], ['B', 'INNER KNOB', 'SUB · VFO-B']]) {
            const t = h('div', 'rp-dial-tag');
            const lamp = h('span', 'rp-lamp');
            t.append(lamp, h('span', null, part), h('span', 'rp-dial-tag-vfo', name));
            dialTagRow.append(t);
            dialTags[vfo] = { t, lamp };
        }
    // The wheel over the dial tunes, and so does dragging it round like the
    // real knob: one step per 10 degrees, clockwise up. With a dual dial, the
    // inner knob tunes SUB and the outer ring MAIN.
    const dialRot = { A: 0, B: 0 };
    if (act)
        turnable(dial, {
            deg: 10,
            partAt: (x, y) => {
                if (!dualDial) return undefined;
                const rc = dial.getBoundingClientRect(), kc = dialKnob.getBoundingClientRect();
                return Math.hypot(x - (rc.left + rc.width / 2), y - (rc.top + rc.height / 2)) <= kc.width / 2 ? 'B' : 'A';
            },
            // Turn the part being tuned: the ring's knurling for MAIN, the
            // knob and its dimple for SUB (or the single dial).
            onTurn: (vfo, d) => {
                const k = vfo ?? 'B';
                dialRot[k] = (dialRot[k] + d) % 360;
                if (vfo === 'A') dial.style.setProperty('--rp-ring-rot', dialRot.A + 'deg');
                else {
                    dialInner.style.transform = `rotate(${dialRot.B}deg)`;
                    dialKnob?.style.setProperty('--rp-knob-rot', dialRot.B + 'deg');
                }
            },
            onStep: (vfo, dir) => act('dial', dir > 0 ? 'up' : 'down', vfo),
        });
    const stepRow = h('div', 'rp-step');
    const down = button('rp-key', 'step', 'down'); down.append(h('span', 'big', '−'));
    const stepKey = button('rp-key', 'step', 'cycle'); const stepTxt = h('span', 'rp-sub', '');
    stepKey.append(h('span', null, 'STEP'), stepTxt);
    const up = button('rp-key', 'step', 'up'); up.append(h('span', 'big', '+'));
    down.title = 'Tune down one step';
    stepKey.title = 'Change the tuning step';
    up.title = 'Tune up one step';
    stepRow.append(down, stepKey, up);
    const vfoKeys = h('div', 'rp-vfokeys');
    if (opts.vfoKeyColumns) {
        vfoKeys.style.gridTemplateColumns = `repeat(${opts.vfoKeyColumns},minmax(0,1fr))`;
        vfoKeys.classList.add('dense');
    }
    for (const k of opts.vfoKeys ?? []) {
        const b = button('rp-key', 'vfoKey', k.id, k.inert);
        const led = h('span', 'rp-led');
        b.append(led, h('span', null, k.label));
        if (k.title) b.title = k.title;
        vfoKeys.append(b);
        keyEls[k.id] = { b, led, sub: null };
    }
    // Memory keys act on the selected receiver, as the radio's own do.
    let memBox = null, memOn = null;
    if (opts.memKeys?.length) {
        memBox = h('div', 'rp-mem');
        const head = h('div', 'rp-mem-head');
        memOn = h('span', 'rp-mem-on', '');
        head.append(h('span', 'rp-legend', 'Memory'), memOn);
        const grid = h('div', 'rp-mem-keys');
        for (const k of opts.memKeys) {
            const b = button('rp-skey', 'memKey', k.id, k.inert);
            if (k.title) b.title = k.title;
            const led = h('span', 'rp-led'), sub = h('span', 'rp-sub', k.sub ?? '');
            b.append(led, h('span', 'rp-skey-label', k.label), sub);
            grid.append(b);
            keyEls['mem:' + k.id] = { b, led, sub };
        }
        memBox.append(head, grid);
    }
    const clar = h('div', 'rp-clar');
    const clarKnob = knobSvg(dualDial ? 76 : 88);
    const clarBox = h('div');
    const clarTxt = h('span', 'rp-lcd', '+0 Hz');
    const clarName = h('span', 'rp-legend', 'Clarifier');
    const clarRow = h('div', 'rp-clar-row');
    clarRow.append(clarTxt);
    for (const k of opts.clarKeys ?? []) {
        const b = button('rp-chipbtn rp-clar-key', 'vfoKey', k.id);
        b.textContent = k.label;
        if (k.title) b.title = k.title;
        clarRow.append(b);
        keyEls[k.id] = { b, led: null, sub: null };
    }
    clarBox.append(clarName, clarRow);
    clar.append(clarKnob.svg, clarBox);
    if (act && opts.clarTurn) {
        turnable(clarKnob.svg, { onStep: (_, dir) => act('knob', 'clar', undefined, dir) });
        clarKnob.svg.append(s('title', {}, 'Clarifier offset: mouse wheel or drag to turn'));
    }
    // A single dial tunes whichever VFO is selected: say which, in that VFO's
    // display colour. A dual dial's tags say it instead.
    const dialLegend = h('span', 'rp-legend', 'Main dial');
    const dialVfo = h('span', 'rp-dial-vfo', 'A');
    if (!dualDial) {
        dialVfo.dataset.vfo = 'A';
        dialVfo.title = 'The dial tunes VFO-A';
        dialLegend.append(dialVfo);
    }
    rightCol.append(dialLegend, dial);
    if (dualDial) rightCol.append(dialTagRow);
    rightCol.append(stepRow, vfoKeys);
    if (memBox) rightCol.append(memBox);
    rightCol.append(clar);
    if (dualDial) rightCol.classList.add('dual');

    body.append(left, well, rightCol);

    // knob row
    const knobs = h('div', 'rp-knobs');
    const knobEls = {};
    for (const k of opts.rxGroups ? [] : opts.knobs ?? []) {
        const wrap = h('div', 'rp-knob idle');
        const kn = knobSvg(88);
        const val = h('span', 'rp-lcd', '--');
        wrap.append(h('span', 'rp-legend', k.label), kn.svg, val);
        if (k.title) wrap.title = k.title;
        knobs.append(wrap);
        knobEls[k.id] = { wrap, g: kn.g, val };
    }

    // Or the bottom row as receiver groups: MAIN | SHARED | SUB.
    const groupEls = {};      // 'A' | 'B' | 'shared' -> { box, name, tag, knobs: { id -> { outer, inner, val, wrap } } }
    // Turned by the wheel or a drag when `k.turn` says so: act('knob',
    // id + ':inner' | ':outer', vfo, dir). The inner knob is the middle
    // 17 of the 32 units of radius.
    function concentric(row, k, vfo) {
        const wrap = h('div', 'rp-cknob');
        const kn = knob2Svg(80);
        const val = h('span', 'rp-lcd', '--');
        wrap.append(h('span', 'rp-legend', k.label), kn.svg, val);
        if (k.title) wrap.title = k.title;
        row.append(wrap);
        if (act && k.turn)
            turnable(kn.svg, {
                partAt: (x, y) => {
                    const rc = kn.svg.getBoundingClientRect();
                    return Math.hypot(x - (rc.left + rc.width / 2), y - (rc.top + rc.height / 2)) <= rc.width * 17 / 64 ? 'inner' : 'outer';
                },
                onStep: (part, dir) => act('knob', k.id + ':' + part, vfo, dir),
            });
        return { wrap, outer: kn.outer, inner: kn.inner, val };
    }
    function rxGroup(vfo) {
        const box = h('div', 'rp-group rp-group-' + vfo);
        const head = h('div', 'rp-group-head');
        const name = h('span', 'rp-group-name', dual ? (vfo === 'A' ? 'MAIN · VFO-A' : 'SUB · VFO-B') : 'RECEIVER · VFO-A');
        const tag = h('span', 'rp-chip sel rp-group-tag hidden', 'SELECTED');
        tag.title = 'The selected receiver';
        head.append(name, tag);
        const row = h('div', 'rp-group-row');
        const g = { box, name, tag, knobs: {} };
        for (const k of opts.rxGroups.knobs ?? []) g.knobs[k.id] = concentric(row, k, dual ? vfo : undefined);
        const grid = h('div', 'rp-gkeys');
        for (const k of opts.rxGroups.keys ?? []) {
            const b = h('button', 'rp-skey');
            b.type = 'button';
            if (k.title) b.title = k.title;
            if (act && !k.inert) b.addEventListener('click', () => act('key', k.id, dual ? vfo : undefined));
            else b.disabled = true;
            const led = h('span', 'rp-led'), sub = h('span', 'rp-sub', '');
            b.append(led, h('span', 'rp-skey-label', k.label), sub);
            grid.append(b);
            keyEls[vfo + ':' + k.id] = { b, led, sub };
        }
        row.append(grid);
        box.append(head, row);
        // A click on the group (not on one of its keys or knobs, which do
        // their own job) selects that receiver, like clicking its VFO box.
        if (act && dual) {
            box.classList.add('pick');
            box.addEventListener('click', e => {
                if (!e.target.closest('button, .rp-turn')) act('vfo', vfo);
            });
        }
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

    // A drop-down list under a key, for a key whose press is a choice (YWC's
    // M.STORE: which bank). Board pixels, as the VFO pickers are.
    const keyList = h('div', 'rp-menu-list rp-keylist');
    keyList.hidden = true;
    panel.append(keyList);
    function closeKeyList() { keyList.hidden = true; keyList.dataset.for = ''; }
    document.addEventListener('pointerdown', e => {
        if (!keyList.hidden && !keyList.contains(e.target) && !keyList.anchor?.contains(e.target)) closeKeyList();
    });
    document.addEventListener('keydown', e => { if (e.key === 'Escape') closeKeyList(); });

    // ---- state ---------------------------------------------------------
    const st = {
        freq: { A: 0, B: 0 }, mode: { A: '', B: '' }, widthHz: { A: null, B: null },
        s: { A: 0, B: 0 }, tx: false, meter: store.get(prefix + 'meter', 'PO'),
        txMeters: {}, spanHz: { A: 0, B: 0 }, theme: null, spots: [], marks: { edges: [], ticks: [] }, sel: 'A',
        specView: specViews[0],
    };
    if (!meterList.includes(st.meter)) st.meter = 'PO';

    for (const pn of Object.values(panes)) { pn.view = new SpectrumView(pn.spec, pn.wf); pn.sets.apply(); }

    // ---- meter ---------------------------------------------------------
    let needle;
    function drawMeterFace() {
        const g = meterGeometry(0, meterScales[st.meter] ?? LOWER_SCALES.PO);
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
        const frac = st.tx || liveMeters.has(st.meter) ? (st.txMeters[st.meter] ?? 0) : sMeterFraction(st.s[st.sel]);
        const n = meterGeometry(frac).needle;
        needle.setAttribute('x1', n.x1); needle.setAttribute('y1', n.y1);
        needle.setAttribute('x2', n.x2); needle.setAttribute('y2', n.y2);
    }
    function chooseMeter(m) { st.meter = m; store.set(prefix + 'meter', m); drawMeterFace(); }

    // ---- spectrum overlays ---------------------------------------------
    // Each pane's passband, axis and spots come from its own VFO.
    function drawOverlays(vfo) {
        if (!vfo) { for (const id of Object.keys(panes)) drawOverlays(id); return; }
        const pn = panes[vfo];
        if (!pn) return;
        const span = st.spanHz[vfo], f = st.freq[vfo];
        const pct = pn.sets.get().passband && passbandPercent(st.mode[vfo], st.widthHz[vfo], span);
        if (pct) { pn.pb.style.display = 'block'; pn.pb.style.left = pct.left + '%'; pn.pb.style.width = pct.width + '%'; }
        else pn.pb.style.display = 'none';
        pn.axis.textContent = '';
        if (span > 0 && f > 0) for (const t of axisLabels(f, span)) pn.axis.append(h('span', null, t));
        drawMarks(pn, f, span);
        drawPin(pn, f, span);
        pn.spots.textContent = '';
        if (span > 0 && f > 0) {
            let row = 0;
            for (const sp of st.spots) {
                const pos = 50 + (sp.hz - f) / span * 100;
                if (pos < 2 || pos > 98) continue;
                const el = h('div', 'rp-spot', sp.call);
                el.style.left = pos + '%';
                el.style.top = (30 + (row++ % 4) * 28) + 'px';
                pn.spots.append(el);
            }
        }
        pn.reread();
    }
    // The Shift+click mark: a solid line through spectrum and waterfall and
    // its frequency, hidden while it is off the pane.
    function drawPin(pn, f, span) {
        const x = pn.pinHz != null && span > 0 && f > 0 ? 50 + (pn.pinHz - f) / span * 100 : null;
        const shown = x != null && x >= 0 && x <= 100;
        for (const l of pn.pinLines) { l.style.display = shown ? 'block' : 'none'; if (shown) l.style.left = x + '%'; }
        pn.pinRead.style.display = shown ? 'block' : 'none';
        if (!shown) return;
        const fr = formatFrequency(pn.pinHz);
        pn.pinRead.textContent = fr.big + fr.small;
        pn.pinRead.style.left = x + '%';
        pn.pinRead.classList.toggle('left', x > 75);
    }
    // Band edges are dashed lines the full height of the trace; band-plan
    // marks are short ticks along the bottom, their labels stacked up to three
    // rows deep where they would overlap, as the classic spectrum draws them.
    function drawMarks(pn, f, span) {
        pn.marks.textContent = '';
        if (!(span > 0 && f > 0)) return;
        const pos = hz => 50 + (hz - f) / span * 100;
        for (const hz of st.marks.edges) {
            const x = pos(hz);
            if (x < 0 || x > 100) continue;
            const el = h('div', 'rp-edge');
            el.style.left = x + '%';
            pn.marks.append(el);
        }
        const width = pn.spec.offsetWidth || 1000;
        const ends = [-Infinity, -Infinity, -Infinity];
        const ticks = st.marks.ticks.map(t => ({ ...t, x: pos(t.hz) })).filter(t => t.x >= 0 && t.x <= 100).sort((a, b) => a.x - b.x);
        for (const t of ticks) {
            const px = t.x / 100 * width, half = String(t.label).length * 4.5 + 2;
            let row = ends.findIndex(e => px - half >= e + 4);
            if (row < 0) row = ends.length - 1;
            ends[row] = px + half;
            const tick = h('div', 'rp-mark');
            tick.style.left = t.x + '%';
            const lab = h('div', 'rp-mark-label', t.label);
            lab.style.left = t.x + '%';
            // Near either side the label hangs inward, so it is not cut off.
            if (px < half) lab.style.transform = 'none';
            else if (px > width - half) lab.style.transform = 'translateX(-100%)';
            lab.style.bottom = (20 + row * 14) + 'px';
            pn.marks.append(tick, lab);
        }
    }
    // "MAIN · SDR A · SPAN 100k": the pane's name, then the adapter's source
    // label unless the name already says it, then the span.
    function paneLabel(vfo) {
        const pn = panes[vfo];
        const parts = [pn.name];
        if (pn.text && !pn.name.includes(pn.text)) parts.push(pn.text);
        if (st.spanHz[vfo]) parts.push('SPAN ' + spanText(st.spanHz[vfo]));
        pn.label.textContent = parts.filter(Boolean).join(' · ');
    }
    // Main / Sub show one pane; Both stacks them and outlines the selected receiver's.
    function setSpecView(id) {
        if (!specViews.includes(id)) id = specViews[0];
        st.specView = id;
        sdrView.classList.toggle('both', id === 'both');
        for (const [vfo, pn] of Object.entries(panes)) {
            pn.box.style.display = id === 'both' || id === vfo ? '' : 'none';
            pn.box.classList.toggle('sel', id === 'both' && vfo === st.sel);
        }
        store.set(prefix + 'spectrum', id);
        markTabs();
    }
    function markTabs() {
        for (const t of viewTabEls)
            t.el.classList.toggle('on', t.view === st.screenView && (t.view !== 'sdr' || t.spec === st.specView));
    }

    // setMode is given the mode as shown, which can be the key's label rather
    // than its id (IWC's DATA-U key is labelled USB-D), so either one lights it.
    function markModeKeys() {
        const m = st.mode[st.sel];
        for (const [id, el] of Object.entries(modeEls)) el.classList.toggle('on', !!m && (id === m || el.textContent === m));
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
        sizer.style.position = 'relative';
        root.style.overflow = z === 'fit' ? 'hidden' : 'auto';
        root.style.height = '100vh';
    }
    function setTheme(id) {
        st.theme = applyTheme(root, id);
        themeSel.value = st.theme.id;
        store.set(prefix + 'theme', st.theme.id);
        for (const pn of Object.values(panes)) pn.view.setTheme(st.theme);
    }
    function setScreenView(id) {
        if (!screenViewList.some(x => x.id === id)) id = 'sdr';
        st.screenView = id;
        sdrView.style.display = id === 'sdr' ? '' : 'none';
        for (const [k, el] of Object.entries(slots)) el.style.display = k === id ? '' : 'none';
        markTabs();
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
    // A size from the corner grip is a percentage the list does not have: it
    // gets an entry of its own, one at a time.
    function setSize(z) {
        if (![...sizeSel.options].some(o => o.value === z)) {
            sizeSel.querySelector('option.custom')?.remove();
            sizeSel.append(Object.assign(h('option', 'custom', z + '%'), { value: z }));
        }
        sizeSel.value = z;
        store.set(prefix + 'size', z);
        rescale();
    }
    const savedSize = store.get(prefix + 'size', 'fit');
    const sizeOk = z => SIZES.includes(z) || (/^\d+$/.test(z) && +z >= MIN_SIZE && +z <= MAX_SIZE);
    setSize(sizeOk(savedSize) ? savedSize : 'fit');
    sizeSel.addEventListener('change', () => setSize(sizeSel.value));

    // The corner grip scales the board by dragging. It keeps its shape, so the
    // way the pointer has gone further, across or up and down, sets the size.
    const grip = h('div', 'rp-board-grip');
    grip.title = 'Drag to resize the panel';
    sizer.append(grip);
    grip.addEventListener('pointerdown', e => {
        e.preventDefault();
        grip.setPointerCapture?.(e.pointerId);
        const natH = scaler.offsetHeight;
        const k0 = sizer.getBoundingClientRect().width / DESIGN_WIDTH;
        const move = m => {
            const rc = sizer.getBoundingClientRect(), page = root.getBoundingClientRect();
            // Centred while it fits, the right edge moves half as far as the width.
            const w = rc.width < page.width ? 2 * (m.clientX - (page.left + page.width / 2)) : m.clientX - rc.left;
            const kw = w / DESIGN_WIDTH, kh = (m.clientY - rc.top) / natH;
            const k = Math.abs(kw - k0) > Math.abs(kh - k0) ? kw : kh;
            setSize(String(Math.round(Math.min(MAX_SIZE, Math.max(MIN_SIZE, 100 * k)))));
        };
        const up = () => {
            grip.removeEventListener('pointermove', move);
            grip.removeEventListener('pointerup', up);
            grip.removeEventListener('pointercancel', up);
        };
        grip.addEventListener('pointermove', move);
        grip.addEventListener('pointerup', up);
        grip.addEventListener('pointercancel', up);
    });
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
        onPopout: opts.onWindowPopout,
    });

    setTheme(store.get(prefix + 'theme', DEFAULT_THEME));
    setSpecView(store.get(prefix + 'spectrum', specViews[0]));
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
            drawOverlays(vfo);
        },
        setMode(vfo, mode) {
            st.mode[vfo] = String(mode ?? '');
            flagsFor(vfo).mode.textContent = st.mode[vfo];
            if (vfo === st.sel) markModeKeys();
            drawOverlays(vfo);
        },
        // `hz` null means the mode has no settable width (AM/FM): no chip, no passband.
        setWidth(vfo, hz) {
            st.widthHz[vfo] = hz;
            flagsFor(vfo).width.textContent = widthText(hz);
            drawOverlays(vfo);
        },
        setAgc(vfo, text) { flagsFor(vfo).agc.textContent = text ? 'AGC ' + text : ''; },
        // A vfoChips chip's text; empty hides it.
        setChip(vfo, kind, text) {
            const el = flagsFor(vfo)[kind];
            if (!el || !extraKinds.includes(kind)) return;
            el.textContent = text ?? '';
            el.hidden = !text;
        },
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
        setTxMeter(name, frac) { st.txMeters[name] = Math.max(0, Math.min(1, Number(frac) || 0)); if (st.tx || liveMeters.has(name)) drawNeedle(); },
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
            for (const [id, pn] of Object.entries(panes)) pn.box.classList.toggle('sel', st.specView === 'both' && id === st.sel);
            // The dial part that tunes the selected receiver glows.
            dial.classList.toggle('sel-a', dualDial && st.sel === 'A');
            dial.classList.toggle('sel-b', dualDial && st.sel === 'B');
            dialVfo.textContent = dialVfo.dataset.vfo = st.sel;
            dialVfo.title = 'The dial tunes VFO-' + st.sel;
            for (const [id, t] of Object.entries(dialTags)) {
                t.t.classList.toggle('on', id === st.sel);
                t.lamp.classList.toggle('on', id === st.sel);
            }
            const selName = dual ? (st.sel === 'A' ? 'MAIN' : 'SUB') : 'VFO-' + st.sel;
            if (memOn) memOn.textContent = 'acts on ' + selName;
            if (opts.clarKeys?.length || dualDial) clarName.textContent = 'Clarifier · ' + selName;
            markModeKeys();
            drawNeedle();
        },
        selected() { return st.sel; },
        setSplit(on) { splitChip.classList.toggle('hidden', !on); },
        setBand(id) { for (const [b, el] of Object.entries(bandEls)) el.classList.toggle('on', b === id); },
        // `vfo` names a receiver group's key ('A' or 'B'; on a single-receiver
        // radio the one group is 'A').
        // Opens a list under key `id` (vfo as setKey's: 'mem' for a Memory
        // key). Items are { group } headings or { label, title, on, run };
        // a click runs one and closes the list. A second press closes it.
        openKeyList(id, items, vfo) {
            const k = keyEls[vfo ? vfo + ':' + id : id];
            const tag = (vfo ?? '') + ':' + id;
            if (!k || (!keyList.hidden && keyList.dataset.for === tag)) return closeKeyList();
            keyList.textContent = '';
            for (const it of items ?? []) {
                if (it.group != null) { keyList.append(h('div', 'rp-menu-group', it.group)); continue; }
                const b = h('button', 'rp-menu-item' + (it.on ? ' on' : ''), it.label);
                b.type = 'button';
                if (it.title) b.title = it.title;
                b.addEventListener('click', () => { closeKeyList(); it.run?.(); });
                keyList.append(b);
            }
            keyList.dataset.for = tag;
            keyList.anchor = k.b;
            keyList.hidden = false;
            const pr = panel.getBoundingClientRect(), kr = k.b.getBoundingClientRect();
            const f = panel.offsetWidth / (pr.width || 1);
            const left = (kr.left - pr.left) * f;
            keyList.style.right = 'auto';
            keyList.style.left = Math.max(0, Math.min(left, panel.offsetWidth - keyList.offsetWidth)) + 'px';
            // Fit what is on screen: below the key if it fits or there is more
            // room there, else above it. A long list (99 memory channels)
            // scrolls inside that height rather than running off the page.
            // The bounds are the window cut down by every ancestor that clips
            // (the sizer does, at the panel's own bottom edge).
            let lo = 0, hi = window.innerHeight;
            for (let a = keyList.parentElement; a && a !== document.documentElement; a = a.parentElement) {
                if (getComputedStyle(a).overflowY === 'visible') continue;
                const ar = a.getBoundingClientRect();
                lo = Math.max(lo, ar.top);
                hi = Math.min(hi, ar.bottom);
            }
            const below = hi - kr.bottom - 12, above = kr.top - lo - 12;
            const down = below >= 240 || below >= above;
            keyList.style.maxHeight = Math.max(120, (down ? below : above) * f) + 'px';
            keyList.style.top = down
                ? ((kr.bottom - pr.top) * f + 6) + 'px'
                : ((kr.top - pr.top) * f - 6 - keyList.offsetHeight) + 'px';
        },
        closeKeyList,
        // `title`, when given, replaces the key's hover text.
        setKey(id, { on = false, tx = false, sub, title } = {}, vfo) {
            const k = keyEls[vfo ? vfo + ':' + id : id];
            if (!k) return;
            if (title !== undefined) k.b.title = title ?? '';
            if (!k.led) { k.b.classList.toggle('on', on || tx); return; }
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
        // `vfo` names the pane ('A' when left out).
        setSpectrumSource({ label, spanHz, status } = {}, vfo = 'A') {
            const pn = panes[vfo];
            if (!pn) return;
            if (spanHz != null && Number(spanHz) !== st.spanHz[vfo]) {
                st.spanHz[vfo] = Number(spanHz) || 0;
                drawOverlays(vfo);
                // The nearest listed span: a cropped span arrives a few Hz off.
                if (pn.spanSel && st.spanHz[vfo] > 0 && document.activeElement !== pn.spanSel) {
                    let best = null;
                    for (const sp of opts.spectrumSpans)
                        if (!best || Math.abs(Math.log(sp.hz / st.spanHz[vfo])) < Math.abs(Math.log(best.hz / st.spanHz[vfo]))) best = sp;
                    pn.spanSel.value = String(best.hz);
                }
            }
            if (label !== undefined) pn.text = label ?? '';
            paneLabel(vfo);
            if (status !== undefined) {
                pn.empty.textContent = status ?? '';
                pn.empty.style.display = status ? 'flex' : 'none';
                if (status) pn.view.clear();
            }
        },
        pushSpectrum(bins, vfo = 'A') { panes[vfo]?.view.push(bins); },
        /** VFO `vfo` is outside every band: `text` is the hover note, or
         *  null when it is back inside one. */
        setOutOfBand(vfo, text) {
            const c = vfo === 'B' ? oobB : oobA;
            c.classList.toggle('hidden', !text);
            c.title = text ?? '';
            const pn = panes[vfo];
            if (pn) { pn.oob.classList.toggle('on', !!text); pn.oob.title = text ?? ''; }
        },
        /** The spectrum is in its own window (true) or back on the board. */
        setSpectrumAway(away) {
            specAway?.classList.toggle('on', !!away);
            if (away) for (const pn of Object.values(panes)) pn.view.clear();
        },
        // One pane, or every pane when `vfo` is left out.
        clearSpectrum(vfo) { for (const [id, pn] of Object.entries(panes)) if (!vfo || id === vfo) pn.view.clear(); },
        // Show or hide a pane's waterfall. Hidden, it shrinks to a strip that
        // keeps its keys, for a pane whose waterfall could not fill (e.g. one
        // only refreshed by an occasional cross-band peek).
        setWaterfall(vfo, show) { panes[vfo]?.box.classList.toggle('no-wf', !show); },
        setSpots(list) { st.spots = list ?? []; drawOverlays(); },
        // Band edges in Hz, and band-plan marks as { hz, label }, drawn on every pane.
        setBandMarks({ edges, ticks } = {}) { st.marks = { edges: edges ?? [], ticks: ticks ?? [] }; drawOverlays(); },
        spectrumView() { return st.specView; },
        setSpectrumView: id => setSpecView(id),
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
