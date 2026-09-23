// radio-display-hotspots.js — make the captured TFT image interactive.
//
// PROTOTYPE. The Radio Display panel shows the radio's own screen as an MJPEG
// stream. Everything drawn on that screen sits at a fixed place for a given
// front-panel layout, so a table of rectangles turns the picture into a
// control surface:
//
//   * the spectrum / waterfall area becomes click-to-tune, with a hover
//     readout of the frequency under the cursor;
//   * the ANT / ATT / IPO / R.FIL / AGC readouts cycle their setting on click,
//     over the same /api/cat endpoints the toolbar selects already use;
//   * the scope soft-buttons drive the CAT scope control; the ones with no
//     CAT equivalent say so instead of pretending;
//   * the meter opens a pop-up for choosing the TX meters. The radio's own
//     selection screen cannot be opened over CAT - nothing touches the TFT -
//     so YWC draws its own, from /api/cat/meters, and the choice goes to the
//     radio as MS. The captured picture then shows the new meter.
//
// A zone is WHERE something is drawn AND WHAT clicking it should do, because
// the same soft-key means different things on different radios. EXPAND is the
// proof: on the FTdx101 and the FTdx10 it expands the scope vertically over
// the readout row and has no CAT command (FTdx10 OM p26; L/N/S is cycled by
// touching the waterfall itself, OM p25); on the FT-710 it toggles
// EXPAND/NORMAL, which IS the SS size. So each zone in LAYOUTS carries an
// `action` from the ACTIONS vocabulary below, and the radio model is looked
// up exactly once, in builtinFor(). Nothing in the hover/click path asks
// which radio it is talking to.
//
// EXPAND is also the limit of what the table can know. With it on, the scope
// grows upward over the ATT/IPO/R.FIL/AGC readouts (Fabio's FTdx10 shots,
// 2026-09-14), and nothing over CAT says so - SS reports W/F vs 3DSS, the
// placement and the L/N/S size, never EXPAND. Both tables assume EXPAND off;
// with it on, the readout zones sit over scope and the plot's top is wrong.
//
// Frequency under the cursor is worked out from the VFO marker line, not from
// the left-hand edge of the box. The radio draws the marker at the VFO
// frequency in CENTER, CURSOR and FIX placement alike, so
//
//     f(x) = VFO + (x - markerX) * span / scopeWidth
//
// holds in all three without needing to know where the axis starts. The
// marker is found by drawing the current frame to an off-screen canvas and
// looking for the tallest red vertical run inside the spectrum rows. When no
// marker is visible (FIX placement with the VFO off the edge) CENTER falls
// back to the box centre and the other placements report "no marker".
//
// Geometry is expressed as FRACTIONS of the captured frame, not pixels. The
// capture dongle stretches the TFT to fill whatever frame size is selected
// (see USER_MANUAL on video capture resolution), so fractions survive a change
// of capture size where pixel offsets would not.
//
// FTdx101MP/D MONO W/F is measured (Colin, 2026-09-12). FTdx10 MONO W/F is
// measured from pixel boxes on an 800×600 frame (Fabio, 2026-09-12): no ANT,
// no MONO/HOLD/MEM CH, SPEED instead. The FT-710 has an EXT DISPLAY output
// too but nobody has measured it; the FTDX3000 has no display output at all.
//
// Bench tools: localStorage['ywc.radioDisplayHotspots.debug'] = '1' (or
// window.radioDisplayHotspots.debug(true)) draws every zone and the detected
// marker; snapshot() opens the current frame at native size. A layout override
// goes in localStorage['ywc.radioDisplayHotspots.layout.<RadioModel>'] as JSON
// (e.g. ...layout.FTdx10) so a 101 nudge cannot leak onto a 10. reloadLayout()
// re-reads that key without a rebuild. measure() lets you drag boxes on the
// live picture instead of typing fractions. An override only needs rects —
// actions come from the built-in table for the model unless the override
// names one — and the older { readouts, buttons, scope } shape is still read.
// The unscoped ywc.radioDisplayHotspots.layout key is still read for FTdx101.
//
// YWC-local: Remote Video is permanently YWC-only (see CLAUDE.md).

const DEBUG_KEY  = 'ywc.radioDisplayHotspots.debug';
const LAYOUT_KEY = 'ywc.radioDisplayHotspots.layout';

function layoutStorageKey(radioModel) {
    const m = String(radioModel || '').trim();
    return m ? `${LAYOUT_KEY}.${m}` : LAYOUT_KEY;
}

// SS span code -> Hz. Mirrors the span table in radio-scope.js.
const SPAN_HZ = [1e3, 2e3, 5e3, 1e4, 2e4, 5e4, 1e5, 2e5, 5e5, 1e6];

// Value rings for the readout boxes. The FTdx101's optional roofing filters
// (1.2 kHz = 8, 300 Hz = A) are included; the server answers with a warning
// rather than an error when one is not fitted, and _cycleRoofing skips on
// round the ring until the radio accepts one.
const RINGS = {
    att:  ['00', '06', '12', '18'],
    ipo:  ['0', '1', '2'],
    agc:  ['0', '1', '2', '3', '4'],
    rfil: {
        default: ['6', '7', '8', '9', 'A'],
        FTdx10:  ['6', '7', '9', 'A'],
    },
    ant:  ['1', '2', '3'],
};

// What a zone can do. `readout.*` cycles a receiver setting through its ring
// (label + state key); `scope.*` calls into RadioScopeControl; `none` is a
// soft-key with no CAT equivalent — the hint says so on hover and click.
// A zone's own `hint` overrides the default here (EXPAND is 'EXPAND / NORMAL'
// on the FT-710, where it is `scope.size`).
const ACTIONS = {
    'readout.ant':     { label: 'ANT',   key: 'ant' },
    'readout.att':     { label: 'ATT',   key: 'att' },
    'readout.ipo':     { label: 'IPO',   key: 'ipo' },
    'readout.rfil':    { label: 'R.FIL', key: 'rfil' },
    'readout.agc':     { label: 'AGC',   key: 'agc' },
    'scope.placement': { hint: 'CENTER / CURSOR / FIX', call: sc => sc.cyclePlacement?.() },
    'scope.span':      { hint: 'Next span',             call: sc => sc.cycleSpan?.() },
    'scope.speed':     { hint: 'Next FFT speed',        call: sc => sc.cycleSpeed?.() },
    'scope.size':      { hint: 'Next scope size',       call: sc => sc.cycleSize?.() },
    'scope.3dss':      { hint: 'W/F ↔ 3DSS',            call: sc => sc.toggle3dss?.() },
    'scope.hold':      { hint: 'HOLD',                  call: sc => sc.toggleHold?.() },
    'meter.select':    { hint: 'Choose the TX meters',  popup: 'meters' },
    'none':            { hint: 'No CAT command — press it on the radio' },
};

// `hideWhen` conditions, evaluated against the scope state. A hidden zone is
// not hit-tested, so a soft-key the radio has taken off the screen does not
// take ghost clicks. No built-in table uses them today: the FTdx10's button
// row was thought to leave the screen in size L and in 3DSS, and Fabio's
// screenshots (2026-09-14, every size, EXPAND on and off) show it is always
// there. Kept for the next radio that does move something SS can see.
const CONDITIONS = {
    '3dss':   ss => !!ss.is3dss,
    'size:0': ss => !ss.is3dss && (ss.size | 0) === 0,
    'size:1': ss => !ss.is3dss && (ss.size | 0) === 1,
    'size:2': ss => !ss.is3dss && (ss.size | 0) === 2,
};

// Per-model zone tables. rect = [left, top, right, bottom] as fractions of
// the frame (normalizeZone also accepts pixels and x/y/w/h). `scope.plot` is
// the clickable spectrum + waterfall area, `scope.marker` the rows scanned
// for the red VFO line (spectrum only — the waterfall can hold red streaks in
// the hotter colour schemes).
//
// Add a radio by adding an entry: the zones it has, what each does, and
// rects once someone has measured them (a zone with no rect is inert but
// still listed by measure()).
const LAYOUTS = {
    // FTdx101MP / FTdx101D, MONO layout, W/F display. Measured from
    // pictures/Radio_Display_Docked.png and bench-confirmed 2026-09-12.
    // MONO / MULTI / EXPAND / MEM CH have no CAT command on this radio —
    // EXPAND here is the vertical expand, not L/N/S.
    FTdx101: {
        scope: {
            plot:   [0.003, 0.520, 0.986, 0.812],
            marker: [0.003, 0.540, 0.986, 0.720],
        },
        zones: {
            // Both meters, top left: the S/PO/COMP/TEMP arc and the
            // ALC/VDD/ID/SWR arc. Bench-checked on the FTdx101MP 2026-09-23
            // (0.499 had cut the SWR meter in half). Right of it is the
            // filter display.
            meter:  { rect: [0.000, 0.095, 0.700, 0.300], action: 'meter.select' },
            ant:    { rect: [0.003, 0.416, 0.196, 0.476], action: 'readout.ant' },
            att:    { rect: [0.199, 0.416, 0.392, 0.476], action: 'readout.att' },
            ipo:    { rect: [0.395, 0.416, 0.591, 0.476], action: 'readout.ipo' },
            rfil:   { rect: [0.594, 0.416, 0.787, 0.476], action: 'readout.rfil' },
            agc:    { rect: [0.790, 0.416, 0.986, 0.476], action: 'readout.agc' },
            cursor: { rect: [0.003, 0.847, 0.119, 0.902], action: 'scope.placement' },
            span:   { rect: [0.122, 0.847, 0.242, 0.902], action: 'scope.span' },
            dss3:   { rect: [0.245, 0.847, 0.364, 0.902], action: 'scope.3dss' },
            mono:   { rect: [0.367, 0.847, 0.484, 0.902], action: 'none', hint: 'MONO / dual layout has no CAT command' },
            multi:  { rect: [0.487, 0.847, 0.606, 0.902], action: 'none', hint: 'MULTI has no CAT command — press it on the radio' },
            expand: { rect: [0.609, 0.847, 0.729, 0.902], action: 'none', hint: 'EXPAND has no CAT command — press it on the radio' },
            hold:   { rect: [0.732, 0.847, 0.851, 0.902], action: 'scope.hold' },
            memch:  { rect: [0.854, 0.847, 0.971, 0.902], action: 'none', hint: 'MEM CH has no CAT command' },
        },
    },
    // FTdx10 MONO W/F. Pixel boxes from Fabio 2026-09-12 on an 800-wide
    // frame (treated as 800×600, the EXT MONITOR PIXEL we recommend).
    // No ANT (one jack). No MONO / HOLD / MEM CH on this layout. Button
    // row is CURSOR, 3DSS, MULTI, EXPAND, SPAN, SPEED, always on screen
    // (Fabio 2026-09-14). EXPAND is the vertical expand, no CAT, same as
    // the '101 - L/N/S is the waterfall touch (OM p25) and our scope panel.
    // plot covers spectrum+waterfall; marker is the upper strip only, and
    // that strip stays inside the spectrum in all three sizes.
    FTdx10: {
        scope: {
            plot:   [0.000, 0.433, 1.000, 0.767],
            marker: [0.000, 0.433, 1.000, 0.533],
        },
        zones: {
            att:    { rect: [0.000, 0.283, 0.138, 0.400], action: 'readout.att' },
            ipo:    { rect: [0.139, 0.283, 0.275, 0.400], action: 'readout.ipo' },
            rfil:   { rect: [0.276, 0.283, 0.413, 0.400], action: 'readout.rfil' },
            agc:    { rect: [0.414, 0.283, 0.550, 0.400], action: 'readout.agc' },
            cursor: { rect: [0.000, 0.800, 0.163, 0.900], action: 'scope.placement' },
            dss3:   { rect: [0.163, 0.800, 0.325, 0.900], action: 'scope.3dss' },
            multi:  { rect: [0.325, 0.800, 0.488, 0.900], action: 'none', hint: 'MULTI has no CAT command — press it on the radio' },
            expand: { rect: [0.488, 0.800, 0.650, 0.900], action: 'none', hint: 'EXPAND has no CAT command — press it on the radio' },
            span:   { rect: [0.650, 0.800, 0.813, 0.900], action: 'scope.span' },
            speed:  { rect: [0.813, 0.800, 0.975, 0.900], action: 'scope.speed' },
        },
    },
};

// Which built-in table a model uses. The ONLY place the model string is
// consulted for layout; everything after this works from the table.
function builtinFor(radioModel) {
    const m = String(radioModel || '').trim();
    if (/^FTdx101/i.test(m)) return LAYOUTS.FTdx101;
    if (/^FTdx10$/i.test(m)) return LAYOUTS.FTdx10;
    return null;
}

function emptyLayout() {
    return { scope: {}, zones: {} };
}

// Bring any accepted layout shape to { scope, zones: { id: { rect, action,
// hint, hideWhen } } }. Accepts the current shape, the older
// { readouts, buttons, scope } shape written by earlier measure() runs, and a
// zone given as a bare rect. Actions missing from an override are taken from
// the built-in table for the model, so a rects-only override keeps working.
function normalizeLayout(raw, builtin) {
    const out = emptyLayout();
    if (!raw || typeof raw !== 'object') return out;
    out.scope = { ...(raw.scope || {}) };
    const src = {};
    for (const group of ['readouts', 'buttons'])
        for (const [id, z] of Object.entries(raw[group] || {})) src[id] = z;
    for (const [id, z] of Object.entries(raw.zones || {})) src[id] = z;
    for (const [id, z] of Object.entries(src)) {
        const zone = (z && typeof z === 'object' && !Array.isArray(z) && 'rect' in z) ? { ...z } : { rect: z };
        const base = builtin?.zones?.[id];
        if (!zone.action && base?.action) zone.action = base.action;
        if (!zone.hint && base?.hint) zone.hint = base.hint;
        if (!zone.hideWhen && base?.hideWhen) zone.hideWhen = base.hideWhen;
        if (!zone.action) zone.action = 'none';
        out.zones[id] = zone;
    }
    return out;
}

function layoutFor(radioModel) {
    const builtin = builtinFor(radioModel);
    try {
        const keyed = localStorage.getItem(layoutStorageKey(radioModel));
        if (keyed) return normalizeLayout(JSON.parse(keyed), builtin);
        // Legacy unscoped key: FTdx101 only, so a 101 bench override cannot
        // leak onto an FTdx10.
        if (builtin === LAYOUTS.FTdx101) {
            const legacy = localStorage.getItem(LAYOUT_KEY);
            if (legacy) return normalizeLayout(JSON.parse(legacy), builtin);
        }
    } catch { /* fall through to the built-in table */ }
    return builtin ? normalizeLayout(builtin, builtin) : null;
}

function roundFrac(n) {
    return Math.round(n * 1000) / 1000;
}

// Accept [l,t,r,b] fractions, the same in pixels (any value > 1), [x,y,w,h]
// when right/bottom is a size, a comma string, or {left,top,right,bottom}.
function normalizeZone(z, nw, nh) {
    if (!z) return null;
    let a;
    if (typeof z === 'string') a = z.trim().split(/[\s,]+/).map(Number);
    else if (Array.isArray(z)) a = z.map(Number);
    else if (typeof z === 'object') {
        if (z.left != null) a = [+z.left, +z.top, +z.right, +z.bottom];
        else if (z.x != null && (z.w != null || z.width != null))
            a = [+z.x, +z.y, +z.x + +(z.w ?? z.width), +z.y + +(z.h ?? z.height)];
        else return null;
    } else return null;
    if (!a || a.length < 4 || a.some(n => !Number.isFinite(n))) return null;
    let [l, t, r, b] = a;
    if (Math.max(l, t, r, b) > 1 && nw > 0 && nh > 0) {
        l /= nw; t /= nh; r /= nw; b /= nh;
    }
    if (r < l) r = l + r;
    if (b < t) b = t + b;
    return [roundFrac(l), roundFrac(t), roundFrac(r), roundFrac(b)];
}

function inZone(z, fx, fy, nw, nh) {
    const r = normalizeZone(z, nw, nh);
    return !!r && fx >= r[0] && fx <= r[2] && fy >= r[1] && fy <= r[3];
}

// Generic measure list for a radio with no built-in table; a model with one
// measures the zones it declares instead.
const MEASURE_GENERIC = [
    'scope.plot', 'scope.marker',
    'zones.att', 'zones.ipo', 'zones.rfil', 'zones.agc',
    'zones.cursor', 'zones.span', 'zones.dss3',
];

function measureList(layout) {
    const ids = Object.keys(layout?.zones || {});
    if (!ids.length) return MEASURE_GENERIC.slice();
    return ['scope.plot', 'scope.marker', ...ids.map(id => `zones.${id}`)];
}

function setPath(obj, path, value) {
    const parts = path.split('.');
    let o = obj;
    for (let i = 0; i < parts.length - 1; i++) {
        if (!o[parts[i]] || typeof o[parts[i]] !== 'object') o[parts[i]] = {};
        o = o[parts[i]];
    }
    o[parts[parts.length - 1]] = value;
}

function formatHz(hz) {
    const s = String(Math.round(hz)).padStart(7, '0');
    return `${s.slice(0, -6)}.${s.slice(-6, -3)}.${s.slice(-3)}`;
}

// The pop-up's styles travel with the module rather than going in site.css:
// the overlay is hosted on two pages, and site.css is where the theme work is.
function ensureMeterPopupStyle() {
    if (document.getElementById('rdh-meters-style')) return;
    const st = document.createElement('style');
    st.id = 'rdh-meters-style';
    st.textContent = `
.radio-display-hotspots .rdh-meters {
    position: absolute; z-index: 3; margin-top: 2px;
    min-width: 11em; padding: 6px 8px 8px;
    background: rgba(12, 16, 28, 0.94); color: #fff;
    border: 1px solid #5b6b8c; border-radius: 4px;
    font: 13px/1.3 system-ui, sans-serif;
    box-shadow: 0 4px 14px rgba(0, 0, 0, 0.6);
}
.radio-display-hotspots .rdh-meters-head {
    display: flex; justify-content: space-between; align-items: center;
    font-weight: 600; margin-bottom: 6px;
}
.radio-display-hotspots .rdh-meters-close {
    width: 26px; height: 26px; margin-left: 12px; padding: 0;
    display: inline-flex; align-items: center; justify-content: center;
    background: #2a3450; color: #fff; font-size: 20px; font-weight: 700; line-height: 1;
    border: 1px solid #8fa2cc; border-radius: 4px; cursor: pointer;
}
.radio-display-hotspots .rdh-meters-close:hover { background: #c0392b; border-color: #e06050; }
.radio-display-hotspots .rdh-meters-hint { margin-top: 6px; font-size: 11px; color: #aab; }
.radio-display-hotspots .rdh-meters-body { display: flex; gap: 10px; }
.radio-display-hotspots .rdh-meters-col { display: flex; flex-direction: column; gap: 4px; }
.radio-display-hotspots .rdh-meters-label { font-size: 11px; color: #aab; }
.radio-display-hotspots .rdh-meters-col button {
    min-width: 5em; padding: 3px 8px; cursor: pointer;
    background: #1d2536; color: #fff; border: 1px solid #45526e; border-radius: 3px;
}
.radio-display-hotspots .rdh-meters-col button:hover { border-color: #9fb3dd; }
.radio-display-hotspots .rdh-meters-col button.rdh-on { background: #0d6efd; border-color: #0d6efd; }
.radio-display-hotspots .rdh-meters-note { margin-top: 6px; font-size: 11px; color: #f5c46b; max-width: 16em; }
.radio-display-hotspots .rdh-meters-note:empty { display: none; }
`;
    document.head.appendChild(st);
}

export class RadioDisplayHotspots {
    /**
     * @param {HTMLImageElement} img          the MJPEG <img>
     * @param {object} opts
     * @param {string} opts.radioModel
     * @param {boolean} opts.dualReceiver
     * @param {() => any} opts.getScopeControl  returns a RadioScopeControl (or null)
     */
    constructor(img, opts = {}) {
        this.img = img;
        this.pane = img?.parentElement;
        this.radioModel = opts.radioModel || '';
        this.dualReceiver = !!opts.dualReceiver;
        this.getScopeControl = opts.getScopeControl || (() => null);
        // A model with no built-in table still gets the overlay, so that
        // snapshot() / debug() / measure() work on a live capture; an empty
        // table hits nothing until a measured layout is loaded.
        this.layout = layoutFor(this.radioModel) || emptyLayout();

        // Radio state the hotspots need. Seeded from /api/cat/status, then kept
        // current by onRadioState() from the page's SignalR handler.
        this.state = {
            A: { hz: 0, ant: '', att: '', ipo: '', agc: '', rfil: '' },
            B: { hz: 0, ant: '', att: '', ipo: '', agc: '', rfil: '' },
            activeVfo: 0,
        };

        this._canvas = document.createElement('canvas');
        this._ctx = this._canvas.getContext('2d', { willReadFrequently: true });
        this._marker = { at: 0, x: null };
        this._busy = false;
        this._lastScopeRefresh = 0;
        this._debug = localStorage.getItem(DEBUG_KEY) === '1';
        this._measureQueue = [];
        this._measureDrag = null;

        if (!this.img || !this.pane || !this.layout) return;
        this._buildOverlay();
        this._seed();
    }

    // ── public ───────────────────────────────────────────────────────────────

    /** Feed every SignalR RadioStateUpdate { property, value } here. */
    onRadioState(update) {
        if (!update) return;
        const p = String(update.property || '');
        const v = update.value;
        const m = /^(Frequency|Antenna|Att|Ipo|Agc|RoofingFilter)([AB])$/.exec(p);
        if (m) {
            const s = this.state[m[2]];
            switch (m[1]) {
                case 'Frequency':     s.hz   = Number(v) || 0; break;
                case 'Antenna':       s.ant  = String(v ?? ''); break;
                case 'Att':           s.att  = String(v ?? ''); break;
                case 'Ipo':           s.ipo  = String(v ?? ''); break;
                case 'Agc':           s.agc  = String(v ?? ''); break;
                case 'RoofingFilter': s.rfil = String(v ?? ''); break;
            }
            return;
        }
        if (p === 'ActiveVfo') this.state.activeVfo = Number(v) || 0;
        // Someone changed the meters (on the radio's own panel, or from
        // another tab) while the pop-up is open: re-read, so it never shows
        // a stale choice. The server does the digit parsing.
        if (p === 'MeterSelection' && this._meterPopup) this._refreshMeterPopup();
    }

    debug(on) {
        this._debug = !!on;
        localStorage.setItem(DEBUG_KEY, on ? '1' : '0');
        this._drawDebug();
    }

    /**
     * Open the current frame at native size for measuring.
     * Chrome blocks top-level data: image URLs (blank white tab), so this
     * uses a blob URL and also downloads radio-display-snapshot.png.
     */
    snapshot() {
        if (!this._grabFrame()) {
            console.warn('[radio-display-hotspots] snapshot: no frame (is the stream running?)');
            return null;
        }
        this._canvas.toBlob(blob => {
            if (!blob) {
                console.warn('[radio-display-hotspots] snapshot: canvas produced no image');
                return;
            }
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = 'radio-display-snapshot.png';
            a.click();
            const w = window.open(url, '_blank');
            if (!w) console.warn('[radio-display-hotspots] snapshot: pop-up blocked; use the downloaded PNG');
            // Keep the blob alive while the tab loads. Revoking immediately
            // blanks the tab the same way a blocked data: URL does.
            setTimeout(() => URL.revokeObjectURL(url), 120000);
        }, 'image/png');
        return this._canvas;
    }

    /**
     * Re-read the per-model localStorage override (or the built-in table).
     * After pasting JSON into ywc.radioDisplayHotspots.layout.FTdx10, call
     * this instead of reloading the page.
     */
    reloadLayout() {
        this.layout = layoutFor(this.radioModel) || emptyLayout();
        if (!this.overlay && this.img && this.pane && this.layout) {
            this._buildOverlay();
            this._seed();
        }
        if (this.overlay) this._syncOverlay();
        else this._drawDebug();
        return this.layout;
    }

    /**
     * Draw zones on the live picture. Drag a box around each control in turn
     * (plot, marker, then every zone the model's table declares — or a
     * generic list for a radio with no table yet). The label says which one
     * is next. At the end the JSON is stored and printed. Pass your own list
     * of 'zones.<id>' / 'scope.<id>' paths to measure a subset, or zones the
     * table does not know about.
     */
    measure(paths) {
        this.debug(true);
        if (!this.layout || !this.layout.zones) this.layout = emptyLayout();
        this._measureQueue = Array.isArray(paths) && paths.length ? paths.slice() : measureList(this.layout);
        this._measureDrag = null;
        const next = this._measureQueue[0];
        this._flash(`draw ${next} — drag a box on the picture`);
        if (this.overlay) this.overlay.style.cursor = 'crosshair';
        console.info('[radio-display-hotspots] measure:', next);
        return next;
    }

    dump() {
        const json = JSON.stringify(this.layout, null, 2);
        console.log('[radio-display-hotspots] layout\n' + json);
        try { localStorage.setItem(layoutStorageKey(this.radioModel), JSON.stringify(this.layout)); }
        catch { /* private mode */ }
        return this.layout;
    }

    // ── overlay ──────────────────────────────────────────────────────────────

    _buildOverlay() {
        const ov = document.createElement('div');
        ov.className = 'radio-display-hotspots';
        ov.innerHTML =
            '<canvas class="rdh-debug"></canvas>' +
            '<div class="rdh-cursor"></div>' +
            '<div class="rdh-label"></div>';
        this.pane.appendChild(ov);
        this.overlay = ov;
        this.debugCanvas = ov.querySelector('.rdh-debug');
        this.cursorEl = ov.querySelector('.rdh-cursor');
        this.labelEl = ov.querySelector('.rdh-label');

        ov.addEventListener('pointerdown', e => this._onDown(e));
        ov.addEventListener('pointermove', e => this._onMove(e));
        ov.addEventListener('pointerup', e => this._onUp(e));
        ov.addEventListener('pointerleave', () => {
            if (!this._measureDrag) this._hideCursor();
        });
        ov.addEventListener('click', e => this._onClick(e));
        window.addEventListener('resize', () => this._drawDebug());

        // Keep the overlay exactly over the drawn image, whatever Fit / Fill /
        // docking has done to the <img> box.
        const sync = () => this._syncOverlay();
        this._syncTimer = setInterval(sync, 500);
        sync();
    }

    /** Rectangle (page coords) of the DRAWN image inside the <img> box. */
    _imageRect() {
        const box = this.img.getBoundingClientRect();
        const nw = this.img.naturalWidth, nh = this.img.naturalHeight;
        if (!(nw > 0 && nh > 0) || box.width <= 0 || box.height <= 0) return null;
        const fit = getComputedStyle(this.img).objectFit || 'contain';
        const scale = fit === 'cover'
            ? Math.max(box.width / nw, box.height / nh)
            : Math.min(box.width / nw, box.height / nh);
        const w = nw * scale, h = nh * scale;
        return {
            left: box.left + (box.width - w) / 2,
            top:  box.top  + (box.height - h) / 2,
            width: w, height: h, scale,
        };
    }

    _syncOverlay() {
        const shown = this.img.style.display !== 'none' && this.img.naturalWidth > 0;
        this.overlay.hidden = !shown;
        if (!shown) return;
        const r = this._imageRect();
        const pr = this.pane.getBoundingClientRect();
        if (!r) return;
        Object.assign(this.overlay.style, {
            left:   `${r.left - pr.left}px`,
            top:    `${r.top - pr.top}px`,
            width:  `${r.width}px`,
            height: `${r.height}px`,
        });
        this._drawDebug();
    }

    /** Pointer event -> fractional frame coordinates. */
    _frac(e) {
        const r = this._imageRect();
        if (!r) return null;
        return { fx: (e.clientX - r.left) / r.width, fy: (e.clientY - r.top) / r.height, rect: r };
    }

    _nat() {
        return { nw: this.img?.naturalWidth || 0, nh: this.img?.naturalHeight || 0 };
    }

    _hit(fx, fy) {
        const L = this.layout;
        const { nw, nh } = this._nat();
        for (const [id, zone] of Object.entries(L.zones || {})) {
            if (this._zoneHidden(zone)) continue;
            if (inZone(zone.rect, fx, fy, nw, nh)) return { kind: 'zone', id, zone };
        }
        if (inZone(L.scope?.plot, fx, fy, nw, nh)) return { kind: 'scope' };
        return null;
    }

    /** True when one of the zone's hideWhen conditions holds for the scope now. */
    _zoneHidden(zone) {
        const conds = zone?.hideWhen;
        if (!conds || !conds.length) return false;
        const ss = this.getScopeControl()?.state;
        if (!ss) return false;   // unknown state: leave the zone live
        return conds.some(c => CONDITIONS[c]?.(ss));
    }

    // ── pointer handling ─────────────────────────────────────────────────────

    _onDown(e) {
        if (!this._measureQueue.length) return;
        const f = this._frac(e);
        if (!f) return;
        e.preventDefault();
        this.overlay.setPointerCapture?.(e.pointerId);
        this._measureDrag = { x0: f.fx, y0: f.fy, x1: f.fx, y1: f.fy };
    }

    _onMove(e) {
        const f = this._frac(e);
        if (!f) return;

        if (this._measureDrag) {
            this._measureDrag.x1 = f.fx;
            this._measureDrag.y1 = f.fy;
            const z = this._dragZone();
            this._showCursor(null, `${this._measureQueue[0]}  [${z.join(', ')}]`);
            this._drawDebug();
            this.overlay.style.cursor = 'crosshair';
            return;
        }

        if (this._measureQueue.length) {
            this.overlay.style.cursor = 'crosshair';
            this._showCursor(null, `draw ${this._measureQueue[0]}`);
            return;
        }

        const hit = this._hit(f.fx, f.fy);
        this.overlay.style.cursor = hit ? (hit.kind === 'scope' ? 'crosshair' : 'pointer') : 'default';

        if (!hit) { this._hideCursor(); return; }

        if (hit.kind === 'scope') {
            const r = this._freqAt(f.fx);
            this._showCursor(e.clientX - f.rect.left, r.ok ? formatHz(r.hz) : r.why);
            return;
        }
        this._showCursor(null, this._describe(hit));
    }

    _onUp(e) {
        if (!this._measureDrag || !this._measureQueue.length) return;
        const f = this._frac(e);
        if (f) { this._measureDrag.x1 = f.fx; this._measureDrag.y1 = f.fy; }
        const z = this._dragZone();
        const path = this._measureQueue.shift();
        // A zone keeps its action/hint; only the rect is being measured.
        setPath(this.layout, path.startsWith('zones.') ? `${path}.rect` : path, z);
        if (path.startsWith('zones.')) {
            const zone = this.layout.zones[path.slice(6)];
            if (!zone.action) zone.action = 'none';
        }
        this._measureDrag = null;
        this.dump();
        if (this._measureQueue.length) this._flash(`draw ${this._measureQueue[0]}`);
        else this._flash('layout saved — paste the console JSON if it looks right');
        this._drawDebug();
    }

    _dragZone() {
        const d = this._measureDrag;
        const l = Math.min(d.x0, d.x1), t = Math.min(d.y0, d.y1);
        const r = Math.max(d.x0, d.x1), b = Math.max(d.y0, d.y1);
        return [roundFrac(l), roundFrac(t), roundFrac(r), roundFrac(b)];
    }

    _onClick(e) {
        if (this._measureQueue.length) { e.preventDefault(); e.stopPropagation(); return; }
        // A click anywhere else on the picture dismisses the meter pop-up and
        // does nothing more, so closing it cannot also retune the radio.
        if (this._meterPopup) { this._closeMeterPopup(); return; }
        const f = this._frac(e);
        if (!f || this._busy) return;
        const hit = this._hit(f.fx, f.fy);
        if (!hit) return;
        if (hit.kind === 'scope') {
            const r = this._freqAt(f.fx);
            if (r.ok) this._tune(r.hz);
            return;
        }
        this._activate(hit.zone);
    }

    /** Run a zone's action: cycle a readout, drive the scope, or say why not. */
    _activate(zone) {
        const action = ACTIONS[zone.action] || ACTIONS.none;
        if (action.key) return this._cycleReadout(action.key);
        if (action.popup === 'meters') return this._openMeterPopup(zone);
        if (action.call) {
            const sc = this.getScopeControl();
            return sc ? action.call(sc) : undefined;
        }
        this._flash(zone.hint || action.hint);
    }

    /** Make the hover label noticeable for a moment after a click. */
    _flash(text) {
        this._showCursor(null, text);
        this.labelEl.classList.add('rdh-label-flash');
        clearTimeout(this._flashTimer);
        this._flashTimer = setTimeout(() => this.labelEl.classList.remove('rdh-label-flash'), 1200);
    }

    _showCursor(x, text) {
        if (x === null) this.cursorEl.style.display = 'none';
        else { this.cursorEl.style.display = ''; this.cursorEl.style.left = `${x}px`; }
        this.labelEl.textContent = text;
        this.labelEl.style.display = text ? '' : 'none';
    }

    _hideCursor() { this._showCursor(null, ''); }

    _describe(hit) {
        const zone = hit.zone;
        const action = ACTIONS[zone?.action] || ACTIONS.none;
        if (action.key) {
            const s = this.state[this._vfo()];
            return `${action.label} ${s[action.key] || '?'} — click to cycle`;
        }
        return zone?.hint || action.hint || hit.id;
    }

    // ── frequency under the cursor ───────────────────────────────────────────

    /** Which VFO the displayed scope belongs to: 'A' or 'B'. */
    _vfo() {
        const sc = this.getScopeControl();
        if (this.dualReceiver) return sc?.band === 'sub' ? 'B' : 'A';
        return this.state.activeVfo === 1 ? 'B' : 'A';
    }

    _scopeState() {
        const sc = this.getScopeControl();
        if (!sc) return null;
        if (!sc.state) {
            // The scope panel reads lazily, on first expand; ask it to read now
            // so the span is known, but not more than once every few seconds.
            const now = Date.now();
            if (now - this._lastScopeRefresh > 5000) {
                this._lastScopeRefresh = now;
                sc.refresh?.();
            }
            return null;
        }
        return sc.state;
    }

    _freqAt(fx) {
        const ss = this._scopeState();
        if (!ss) return { ok: false, why: 'scope span unknown' };
        if (ss.is3dss) return { ok: false, why: '3DSS: no tuning' };
        const spanHz = SPAN_HZ[parseInt(ss.span, 10)];
        if (!spanHz) return { ok: false, why: 'scope span unknown' };

        const vfoHz = this.state[this._vfo()].hz;
        if (!vfoHz) return { ok: false, why: 'VFO unknown' };

        const { nw, nh } = this._nat();
        const plot = normalizeZone(this.layout?.scope?.plot, nw, nh);
        if (!plot) return { ok: false, why: 'no plot layout' };
        const width = plot[2] - plot[0];
        let markerFx = this._findMarker();
        if (markerFx === null) {
            if ((ss.placement | 0) === 0) markerFx = plot[0] + width / 2;   // CENTER
            else return { ok: false, why: 'marker not found' };
        }
        const hz = vfoHz + (fx - markerFx) * spanHz / width;
        return { ok: true, hz: Math.round(hz / 10) * 10, markerFx };
    }

    _grabFrame() {
        const nw = this.img.naturalWidth, nh = this.img.naturalHeight;
        if (!(nw > 0 && nh > 0)) return false;
        if (this._canvas.width !== nw || this._canvas.height !== nh) {
            this._canvas.width = nw; this._canvas.height = nh;
        }
        try { this._ctx.drawImage(this.img, 0, 0, nw, nh); } catch { return false; }
        return true;
    }

    /**
     * Fractional x of the VFO marker, or null. Scans the spectrum rows for the
     * column holding the longest contiguous run of saturated red; the marker
     * has to span at least half the scanned band to count, so a red trace or
     * a red waterfall streak does not pass for it. Cached for 250 ms — this is
     * called from pointermove.
     */
    _findMarker() {
        const now = Date.now();
        if (now - this._marker.at < 250) return this._marker.x;
        this._marker.at = now;
        this._marker.x = null;

        if (!this._grabFrame()) return null;
        const { nw, nh } = this._nat();
        const z = normalizeZone(this.layout?.scope?.marker, nw, nh);
        if (!z) return null;
        const W = this._canvas.width, H = this._canvas.height;
        const x0 = Math.round(z[0] * W), x1 = Math.round(z[2] * W);
        const y0 = Math.round(z[1] * H), y1 = Math.round(z[3] * H);
        const w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return null;

        const px = this._ctx.getImageData(x0, y0, w, h).data;
        let bestX = -1, bestRun = 0;
        for (let x = 0; x < w; x++) {
            let run = 0, longest = 0;
            for (let y = 0; y < h; y++) {
                const i = (y * w + x) * 4;
                const r = px[i], g = px[i + 1], b = px[i + 2];
                if (r > 150 && g < 90 && b < 90) { run++; if (run > longest) longest = run; }
                else run = 0;
            }
            if (longest > bestRun) { bestRun = longest; bestX = x; }
        }
        if (bestRun < h * 0.5) return null;
        this._marker.x = (x0 + bestX + 0.5) / W;
        return this._marker.x;
    }

    // ── actions ──────────────────────────────────────────────────────────────

    /** POST and return the parsed JSON reply (null on failure). */
    async _post(url, body) {
        this._busy = true;
        try {
            const res = await fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body),
            });
            return await res.json().catch(() => null);
        } catch (err) {
            console.error('[radio-display-hotspots]', url, err);
            return null;
        } finally {
            this._busy = false;
        }
    }

    _tune(hz) {
        const vfo = this._vfo().toLowerCase();
        return this._post(`/api/cat/frequency/${vfo}`, { frequencyHz: hz });
    }

    _cycleReadout(key) {
        const vfo = this._vfo();
        const s = this.state[vfo];
        const v = vfo.toLowerCase();
        const next = (ring, cur) => {
            const i = ring.indexOf(cur);
            return ring[(i + 1) % ring.length];   // unknown current -> first
        };
        switch (key) {
            case 'ant':
                return this._post(`/api/cat/antenna/${v}`, { antenna: next(RINGS.ant, s.ant) });
            case 'att':
                return this._post(`/api/cat/attenuator/${v}`, { code: next(RINGS.att, s.att) });
            case 'ipo':
                return this._post(`/api/cat/ipo/${v}`, { code: next(RINGS.ipo, s.ipo) });
            case 'agc':
                return this._post(`/api/cat/agc/${v}`, { code: next(RINGS.agc, s.agc) });
            case 'rfil':
                return this._cycleRoofing(v, s);
        }
    }

    // The ring lists the optional filters too. When one is not fitted the
    // server answers { warning: true } and the radio stays where it was, so
    // a plain "next in ring" would retry the same missing filter on every
    // click (measured 2026-09-12: stuck at 3 kHz trying 1.2 kHz). Keep going
    // round the ring until the radio accepts one.
    async _cycleRoofing(v, s) {
        const ring = RINGS.rfil[this.radioModel] || RINGS.rfil.default;
        let cur = s.rfil;
        for (let i = 0; i < ring.length; i++) {
            const want = ring[(ring.indexOf(cur) + 1) % ring.length];
            const reply = await this._post(`/api/cat/roofingfilter/${v}`, { filter: want });
            if (!reply || !reply.warning) return;
            cur = want;   // refused; try the one after it
        }
    }

    // ── meter pop-up ─────────────────────────────────────────────────────────

    // Drawn inside the overlay, under the meter zone, so it moves and scales
    // with the picture. Everything it offers comes from the server's
    // FrontPanelMeters table; nothing about MS digits lives in the browser.
    async _openMeterPopup(zone) {
        this._closeMeterPopup();
        ensureMeterPopupStyle();
        const pop = document.createElement('div');
        pop.className = 'rdh-meters';
        pop.setAttribute('role', 'dialog');
        pop.setAttribute('aria-label', 'TX meter selection');
        pop.innerHTML = '<div class="rdh-meters-head"><span>TX meters</span>' +
            '<button type="button" class="rdh-meters-close" aria-label="Close" title="Close">×</button></div>' +
            '<div class="rdh-meters-body">Reading the radio…</div>' +
            '<div class="rdh-meters-note"></div>' +
            '<div class="rdh-meters-hint">Pick a meter on each side, or × / Esc when done.</div>';
        // Keep the overlay's own hover / click / measure handling off the pop-up.
        for (const t of ['pointerdown', 'pointermove', 'pointerup', 'click'])
            pop.addEventListener(t, e => e.stopPropagation());
        pop.querySelector('.rdh-meters-close').addEventListener('click', () => this._closeMeterPopup());

        const { nw, nh } = this._nat();
        const r = normalizeZone(zone.rect, nw, nh) || [0, 0, 0.5, 0.3];
        pop.style.left = `${r[0] * 100}%`;
        pop.style.top = `${r[3] * 100}%`;
        this.overlay.appendChild(pop);
        this._meterPopup = pop;
        this._hideCursor();

        this._onMeterKey = e => { if (e.key === 'Escape') this._closeMeterPopup(); };
        document.addEventListener('keydown', this._onMeterKey);

        try {
            const res = await fetch('/api/cat/meters');
            const data = await res.json();
            if (this._meterPopup === pop) this._renderMeterPopup(data);
        } catch (err) {
            console.error('[radio-display-hotspots] meters', err);
            if (this._meterPopup === pop)
                pop.querySelector('.rdh-meters-body').textContent = 'Could not read the meters.';
        }
    }

    async _refreshMeterPopup() {
        const pop = this._meterPopup;
        try {
            const data = await (await fetch('/api/cat/meters')).json();
            if (this._meterPopup === pop) this._renderMeterPopup(data);
        } catch (err) {
            console.error('[radio-display-hotspots] meters', err);
        }
    }

    _closeMeterPopup() {
        this._meterPopup?.remove();
        this._meterPopup = null;
        if (this._onMeterKey) document.removeEventListener('keydown', this._onMeterKey);
        this._onMeterKey = null;
    }

    _renderMeterPopup(data) {
        const pop = this._meterPopup;
        const body = pop.querySelector('.rdh-meters-body');
        const note = pop.querySelector('.rdh-meters-note');
        if (!data || !data.supported) {
            body.textContent = `No meter selection for ${data?.radioModel || 'this radio'}.`;
            return;
        }
        const selected = data.selected || [];
        body.textContent = '';
        data.slots.forEach((slot, i) => {
            const col = document.createElement('div');
            col.className = 'rdh-meters-col';
            col.setAttribute('role', 'group');
            col.setAttribute('aria-label', slot.label);
            if (data.slots.length > 1) {
                const h = document.createElement('div');
                h.className = 'rdh-meters-label';
                h.textContent = slot.label;
                col.appendChild(h);
            }
            for (const opt of slot.options) {
                const b = document.createElement('button');
                b.type = 'button';
                b.textContent = opt.label;
                const on = selected[i] === opt.code;
                b.classList.toggle('rdh-on', on);
                b.setAttribute('aria-pressed', on ? 'true' : 'false');
                b.addEventListener('click', () => this._chooseMeter(data, i, opt.code));
                col.appendChild(b);
            }
            body.appendChild(col);
        });
        // The radio only drives these needles in TX, and a pick keeps YWC off
        // them while you transmit - which costs YWC whichever of its own COMP /
        // SWR gauges the panel is no longer showing.
        const lost = data.txGaugesLost || [];
        note.textContent = data.pinned && lost.length
            ? `In TX the radio shows your choice; YWC's ${lost.join(' and ')} gauge${lost.length > 1 ? 's stay' : ' stays'} blank.`
            : '';
    }

    async _chooseMeter(data, slotIndex, code) {
        // MS sets every meter at once. A slot not known yet (the radio never
        // answered MS) takes its first option, which is the radio's default.
        const codes = data.slots.map((s, i) =>
            i === slotIndex ? code : (data.selected?.[i] ?? s.options[0].code));
        try {
            const res = await fetch('/api/cat/meters', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ codes }),
            });
            const reply = await res.json().catch(() => null);
            if (!this._meterPopup) return;
            if (!res.ok || !reply) {
                this._meterPopup.querySelector('.rdh-meters-note').textContent =
                    reply?.error || 'The radio did not take that.';
                return;
            }
            // Close once every meter has had a pick, so on the FTdx101 both
            // sides can be changed in one visit; × / Esc for just one side.
            // A warning about YWC's own gauges stays up long enough to read.
            const pop = this._meterPopup;
            this._renderMeterPopup(reply);
            pop._picked = (pop._picked || new Set()).add(slotIndex);
            if (pop._picked.size < data.slots.length) return;
            if (!(reply.txGaugesLost || []).length) this._closeMeterPopup();
            else setTimeout(() => { if (this._meterPopup === pop) this._closeMeterPopup(); }, 4000);
        } catch (err) {
            console.error('[radio-display-hotspots] set meters', err);
        }
    }

    // ── seed ─────────────────────────────────────────────────────────────────

    async _seed() {
        try {
            const res = await fetch('/api/cat/status');
            if (!res.ok) return;
            const d = await res.json();
            for (const [k, vfo] of [['vfoA', 'A'], ['vfoB', 'B']]) {
                const src = d[k]; if (!src) continue;
                const s = this.state[vfo];
                s.hz   = Number(src.frequency) || s.hz;
                s.ant  = String(src.antenna ?? s.ant);
                s.rfil = String(src.roofingFilter ?? s.rfil);
                if (src.att !== undefined) s.att = String(src.att ?? '');
                if (src.ipo !== undefined) s.ipo = String(src.ipo ?? '');
                if (src.agc !== undefined) s.agc = String(src.agc ?? '');
            }
            if (d.activeVfo !== undefined) this.state.activeVfo = Number(d.activeVfo) || 0;
        } catch (err) {
            console.warn('[radio-display-hotspots] seed failed', err);
        }
    }

    // ── debug overlay ────────────────────────────────────────────────────────

    _drawDebug() {
        const c = this.debugCanvas;
        if (!c) return;
        const r = this._imageRect();
        if (!this._debug || !r) { c.style.display = 'none'; return; }
        c.style.display = '';
        c.width = Math.round(r.width); c.height = Math.round(r.height);
        const g = c.getContext('2d');
        g.clearRect(0, 0, c.width, c.height);
        g.font = '11px sans-serif';
        // Frame of the overlay coordinate space — if this is not around the
        // whole TFT, the overlay is not sitting on the picture.
        g.strokeStyle = 'rgba(255,255,255,0.55)';
        g.strokeRect(0.5, 0.5, c.width - 1, c.height - 1);
        const { nw, nh } = this._nat();
        const box = (z, colour, label, hidden) => {
            const r = normalizeZone(z, nw, nh);
            if (!r) return;
            const x = r[0] * c.width, y = r[1] * c.height;
            const w = (r[2] - r[0]) * c.width, h = (r[3] - r[1]) * c.height;
            g.strokeStyle = colour; g.lineWidth = 1;
            if (hidden) g.setLineDash([2, 4]);
            g.strokeRect(x + 0.5, y + 0.5, w, h);
            g.setLineDash([]);
            g.fillStyle = colour; g.fillText(hidden ? `${label} (hidden)` : label, x + 3, y + 12);
        };
        const L = this.layout;
        // Green = readout, yellow = scope soft-key, grey = no CAT action;
        // dashed = hidden by a hideWhen condition right now.
        for (const [id, zone] of Object.entries(L.zones || {})) {
            const a = zone.action || 'none';
            const colour = a.startsWith('readout.') ? '#0f0' : a === 'none' ? '#aaa' : '#ff0';
            box(zone.rect, colour, id, this._zoneHidden(zone));
        }
        box(L.scope?.plot, '#0ff', 'plot');
        box(L.scope?.marker, '#f0f', 'marker scan');
        if (this._measureDrag) box(this._dragZone(), '#fff', this._measureQueue[0] || 'drag');
        const mx = this._findMarker();
        if (mx !== null) {
            g.strokeStyle = '#fff'; g.setLineDash([4, 3]);
            g.beginPath(); g.moveTo(mx * c.width, 0); g.lineTo(mx * c.width, c.height); g.stroke();
            g.setLineDash([]);
            g.fillStyle = '#fff'; g.fillText('marker', mx * c.width + 3, c.height - 4);
        }
    }
}
