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
//   * the scope soft-buttons (CURSOR, SPAN, 3DSS, HOLD) drive the CAT scope
//     control; the ones with no CAT equivalent (MONO, MULTI, EXPAND, MEM CH)
//     say so instead of pretending.
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
// FTdx101MP/D MONO W/F is measured. FTdx10 MONO W/F is measured from
// pixel boxes on an 800×600 frame (2026-09-12): no ANT, no MONO/HOLD/MEM CH,
// SPEED instead. FT-710 stays off.
//
// Bench tools: localStorage['ywc.radioDisplayHotspots.debug'] = '1' (or
// window.radioDisplayHotspots.debug(true)) draws every zone and the detected
// marker; snapshot() opens the current frame at native size. A layout override
// goes in localStorage['ywc.radioDisplayHotspots.layout.<RadioModel>'] as JSON
// (e.g. ...layout.FTdx10) so a 101 nudge cannot leak onto a 10. reloadLayout()
// re-reads that key without a rebuild. measure() lets you drag boxes on the
// live picture instead of typing fractions. The unscoped
// ywc.radioDisplayHotspots.layout key is still read for FTdx101 only.
//
// YWC-local: Remote Video is permanently YWC-only (see CLAUDE.md).

const DEBUG_KEY  = 'ywc.radioDisplayHotspots.debug';
const LAYOUT_KEY = 'ywc.radioDisplayHotspots.layout';

function layoutStorageKey(radioModel) {
    const m = String(radioModel || '').trim();
    return m ? `${LAYOUT_KEY}.${m}` : LAYOUT_KEY;
}

function isFtdx101(radioModel) {
    return /^FTdx101/i.test(radioModel || '');
}

function isFtdx10(radioModel) {
    // Must not match FTdx101. The 101 test runs first in layoutFor; this is
    // the exact model string Settings stores for the FTdx10.
    return /^FTdx10$/i.test(radioModel || '');
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

// Zones as [left, top, right, bottom] fractions of the frame.
const LAYOUTS = {
    // FTdx101MP / FTdx101D, MONO layout, W/F display. Measured from
    // pictures/Radio_Display_Docked.png; see the header note.
    FTdx101: {
        readouts: {
            ant:  [0.003, 0.416, 0.196, 0.476],
            att:  [0.199, 0.416, 0.392, 0.476],
            ipo:  [0.395, 0.416, 0.591, 0.476],
            rfil: [0.594, 0.416, 0.787, 0.476],
            agc:  [0.790, 0.416, 0.986, 0.476],
        },
        // The scope box: `plot` is the clickable spectrum + waterfall area,
        // `marker` the rows scanned for the red VFO line (spectrum only — the
        // waterfall can hold red streaks in the hotter colour schemes).
        scope: {
            plot:   [0.003, 0.520, 0.986, 0.812],
            marker: [0.003, 0.540, 0.986, 0.720],
        },
        buttons: {
            cursor: [0.003, 0.847, 0.119, 0.902],
            span:   [0.122, 0.847, 0.242, 0.902],
            dss3:   [0.245, 0.847, 0.364, 0.902],
            mono:   [0.367, 0.847, 0.484, 0.902],
            multi:  [0.487, 0.847, 0.606, 0.902],
            expand: [0.609, 0.847, 0.729, 0.902],
            hold:   [0.732, 0.847, 0.851, 0.902],
            memch:  [0.854, 0.847, 0.971, 0.902],
        },
    },
    // FTdx10 MONO W/F. Pixel boxes from Fabio 2026-09-12 on an 800-wide
    // frame (treated as 800×600, the EXT MONITOR PIXEL we recommend).
    // No ANT (one jack). No MONO / HOLD / MEM CH on this layout. Button
    // row is CURSOR, 3DSS, MULTI, EXPAND, SPAN, SPEED.
    // plot covers spectrum+waterfall; marker is the upper strip only
    // (their two y-ranges were swapped vs the 101 names).
    FTdx10: {
        readouts: {
            att:  [0.000, 0.283, 0.138, 0.400],
            ipo:  [0.139, 0.283, 0.275, 0.400],
            rfil: [0.276, 0.283, 0.413, 0.400],
            agc:  [0.414, 0.283, 0.550, 0.400],
        },
        scope: {
            plot:   [0.000, 0.433, 1.000, 0.767],
            marker: [0.000, 0.433, 1.000, 0.533],
        },
        buttons: {
            cursor: [0.000, 0.800, 0.163, 0.900],
            dss3:   [0.163, 0.800, 0.325, 0.900],
            multi:  [0.325, 0.800, 0.488, 0.900],
            expand: [0.488, 0.800, 0.650, 0.900],
            span:   [0.650, 0.800, 0.813, 0.900],
            speed:  [0.813, 0.800, 0.975, 0.900],
        },
    },
};

const NO_CAT = {
    mono:   'MONO / dual layout has no CAT command',
    multi:  'MULTI has no CAT command — press it on the radio',
    expand: 'EXPAND has no CAT command — press it on the radio',
    memch:  'MEM CH has no CAT command',
};

function emptyLayout() {
    return { readouts: {}, buttons: {}, scope: {} };
}

function layoutFor(radioModel) {
    try {
        const keyed = localStorage.getItem(layoutStorageKey(radioModel));
        if (keyed) return JSON.parse(keyed);
        // Legacy unscoped key: FTdx101 only, so a 101 bench override cannot
        // leak onto an FTdx10.
        if (isFtdx101(radioModel)) {
            const legacy = localStorage.getItem(LAYOUT_KEY);
            if (legacy) return JSON.parse(legacy);
        }
    } catch { /* fall through to the built-in table */ }
    if (isFtdx101(radioModel)) return LAYOUTS.FTdx101;
    if (isFtdx10(radioModel)) return LAYOUTS.FTdx10 || null;
    return null;
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

const MEASURE_FTDX10 = [
    'readouts.att', 'readouts.ipo', 'readouts.rfil', 'readouts.agc',
    'scope.plot', 'scope.marker',
    'buttons.cursor', 'buttons.dss3', 'buttons.multi', 'buttons.expand',
    'buttons.span', 'buttons.speed',
];

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
        this.layout = layoutFor(this.radioModel);
        // FTdx10 has no committed fractions yet. Still build the overlay so
        // snapshot() / debug() / reloadLayout() work on a live capture; an
        // empty table means clicks hit nothing until a measured layout is loaded.
        if (!this.layout && isFtdx10(this.radioModel)) this.layout = emptyLayout();

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
        this.layout = layoutFor(this.radioModel);
        if (!this.layout && isFtdx10(this.radioModel)) this.layout = emptyLayout();
        if (!this.overlay && this.img && this.pane && this.layout) {
            this._buildOverlay();
            this._seed();
        }
        if (this.overlay) this._syncOverlay();
        else this._drawDebug();
        return this.layout;
    }

    /**
     * Draw zones on the live picture. Drag a box around each control in order
     * (ATT, IPO, R.FIL, AGC, plot, marker, then the soft-buttons). The label
     * says which one is next. At the end the JSON is stored and printed.
     */
    measure() {
        this.debug(true);
        if (!this.layout || !this.layout.readouts) this.layout = emptyLayout();
        this._measureQueue = MEASURE_FTDX10.slice();
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
        for (const [id, z] of Object.entries(L.readouts || {}))
            if (inZone(z, fx, fy, nw, nh)) return { kind: 'readout', id };
        for (const [id, z] of Object.entries(L.buttons || {}))
            if (inZone(z, fx, fy, nw, nh)) return { kind: 'button', id };
        if (inZone(L.scope?.plot, fx, fy, nw, nh)) return { kind: 'scope' };
        return null;
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
        setPath(this.layout, path, z);
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
        const f = this._frac(e);
        if (!f || this._busy) return;
        const hit = this._hit(f.fx, f.fy);
        if (!hit) return;
        if (hit.kind === 'scope') {
            const r = this._freqAt(f.fx);
            if (r.ok) this._tune(r.hz);
            return;
        }
        if (hit.kind === 'readout') this._cycleReadout(hit.id);
        else if (this._noCat(hit.id)) this._flash(this._noCat(hit.id));
        else this._pressButton(hit.id);
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

    _noCat(id) {
        if (id === 'expand' && isFtdx10(this.radioModel)) return null;
        return NO_CAT[id] || null;
    }

    _describe(hit) {
        const vfo = this._vfo();
        const s = this.state[vfo];
        switch (hit.id) {
            case 'ant':  return `ANT ${s.ant || '?'} — click to cycle`;
            case 'att':  return `ATT ${s.att || '?'} — click to cycle`;
            case 'ipo':  return `IPO ${s.ipo || '?'} — click to cycle`;
            case 'rfil': return `R.FIL ${s.rfil || '?'} — click to cycle`;
            case 'agc':  return `AGC ${s.agc || '?'} — click to cycle`;
            case 'cursor': return 'CENTER / CURSOR / FIX';
            case 'span':   return 'Next span';
            case 'speed':  return 'Next FFT speed';
            case 'dss3':   return 'W/F ↔ 3DSS';
            case 'expand': return isFtdx10(this.radioModel) ? 'L / N / S' : (NO_CAT.expand || 'expand');
            case 'hold':   return 'HOLD';
            default:       return this._noCat(hit.id) || hit.id;
        }
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

    _cycleReadout(id) {
        const vfo = this._vfo();
        const s = this.state[vfo];
        const v = vfo.toLowerCase();
        const next = (ring, cur) => {
            const i = ring.indexOf(cur);
            return ring[(i + 1) % ring.length];   // unknown current -> first
        };
        switch (id) {
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

    _pressButton(id) {
        const sc = this.getScopeControl();
        if (!sc) return;
        switch (id) {
            case 'cursor': return sc.cyclePlacement?.();
            case 'span':   return sc.cycleSpan?.();
            case 'speed':  return sc.cycleSpeed?.();
            case 'dss3':   return sc.toggle3dss?.();
            case 'expand': return sc.cycleSize?.();
            case 'hold':   return sc.toggleHold?.();
            default:       return;   // no CAT equivalent; the hover text says so
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
        const box = (z, colour, label) => {
            const r = normalizeZone(z, nw, nh);
            if (!r) return;
            const x = r[0] * c.width, y = r[1] * c.height;
            const w = (r[2] - r[0]) * c.width, h = (r[3] - r[1]) * c.height;
            g.strokeStyle = colour; g.lineWidth = 1; g.strokeRect(x + 0.5, y + 0.5, w, h);
            g.fillStyle = colour; g.fillText(label, x + 3, y + 12);
        };
        const L = this.layout;
        for (const [id, z] of Object.entries(L.readouts || {})) box(z, '#0f0', id);
        for (const [id, z] of Object.entries(L.buttons || {})) box(z, '#ff0', id);
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
