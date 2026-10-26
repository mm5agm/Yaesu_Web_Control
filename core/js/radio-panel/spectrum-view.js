// spectrum-view.js - the radio panel's spectrum trace and waterfall.
//
// Takes FFT bins already ordered low to high frequency, in dB, and draws them
// into two canvases. It knows nothing about SDRs, SignalR or which radio is
// attached: the app's adapter does the ordering (Yaesu IF frames arrive
// inverted) and hands over plain numbers.
//
// The vertical scale follows the noise floor so a quiet band and a loud one
// both fill the screen: the floor is a slow average of the lower quartile and
// the screen shows `range` dB above it (60 by default). Columns keep the
// strongest bin they cover, so a single-bin CW carrier is not averaged away at
// wide spans.
//
// The settings are the classic spectrum's, with the same meanings:
//   range   dB from bottom to top of the trace, 5-160 (the waterfall keeps 60)
//   speed   the waterfall scrolls one row every `speed` frames: 1, 2, 4 ... 128
//   bright  dB of lift for the waterfall only, 0-60
//   smooth  half-width in bins of a moving average across the trace, 0-8
//   hold    true freezes trace and waterfall; frames are dropped, not queued
//
// Levels, which the RTTY Contest view sets and the radio panel leaves alone
// (setLevels): `auto` false freezes the floor where it is, and `ref` puts
// the bottom of the trace that many dB from the floor (negative is below it)
// in place of the default share of the range. fit() sets the range so the
// strongest peak of the last few seconds sits just under the top.

import { binsToColumns, percentile, waterfallColour } from './panel-math.js';

const WATERFALL_DB = 60;
const FLOOR_MARGIN_DB = 6;
const FLOOR_ALPHA = 0.08;
const PEAK_FRAMES = 40;
const HIDDEN_COLUMNS = 1000;
const FIT_HEADROOM_DB = 3;

export const SPECTRUM_SPEEDS = [1, 2, 4, 8, 16, 32, 64, 128];
export const SPECTRUM_DEFAULTS = Object.freeze({ range: 60, speed: 1, bright: 0, smooth: 2, hold: false });

// Bring a settings object into range; anything missing takes the default.
export function cleanSpectrumSettings(s = {}) {
    const num = (x, d, lo, hi) => { const n = Number(x); return Number.isFinite(n) ? Math.max(lo, Math.min(hi, Math.round(n))) : d; };
    const D = SPECTRUM_DEFAULTS;
    const speed = Number(s.speed);
    return {
        range: num(s.range, D.range, 5, 160),
        speed: SPECTRUM_SPEEDS.includes(speed) ? speed : D.speed,
        bright: num(s.bright, D.bright, 0, 60),
        smooth: num(s.smooth, D.smooth, 0, 8),
        hold: s.hold === true,
    };
}

// Moving average of half-width r across the bins; the ends average what they have.
export function smoothBins(bins, r) {
    if (!(r > 0) || bins.length < 3) return bins;
    const n = bins.length, out = new Float64Array(n);
    let sum = 0, lo = 0, hi = -1;
    for (let i = 0; i < n; i++) {
        const a = Math.max(0, i - r), b = Math.min(n - 1, i + r);
        while (hi < b) sum += bins[++hi];
        while (lo < a) sum -= bins[lo++];
        out[i] = sum / (hi - lo + 1);
    }
    return out;
}

export class SpectrumView {
    /**
     * @param {object} [opts]
     * @param {boolean} [opts.dbScale=false]  label the grid in dB above the floor
     */
    constructor(spectrumEl, waterfallEl, opts = {}) {
        this._specEl = spectrumEl;
        this._wfEl = waterfallEl;
        this._spec = document.createElement('canvas');
        this._wf = document.createElement('canvas');
        spectrumEl.prepend(this._spec);
        waterfallEl.prepend(this._wf);
        this._sctx = this._spec.getContext('2d');
        this._wctx = this._wf.getContext('2d', { willReadFrequently: false });
        this._floor = null;
        this._auto = true;
        this._ref = null;
        this._peaks = [];
        this._dbScale = !!opts.dbScale;
        this._last = null;
        this._set = { ...SPECTRUM_DEFAULTS };
        this._frames = 0;
        this._palette = buildPalette();
        this._colours = {};
        this._ro = new ResizeObserver(() => this._resize());
        this._ro.observe(spectrumEl);
        this._ro.observe(waterfallEl);
        this._resize();
    }

    // Re-read the colours after a theme change. `theme` is resolveTheme()'s
    // result; the CSS colours come from the custom properties it set.
    setTheme(theme) {
        const cs = getComputedStyle(this._specEl);
        const get = k => cs.getPropertyValue('--rp-' + k).trim();
        this._colours = { grid: get('spec-grid'), trace: get('spec-trace'), fill: get('spec-fill'), bg: get('spec-bg') };
        this._palette = buildPalette(theme?.waterfall);
        this._wctx.fillStyle = this._colours.bg || '#000';
        this._wctx.fillRect(0, 0, this._wf.width, this._wf.height);
        this._draw();
    }

    // Forget the floor and the picture, e.g. on a band change or when the SDR
    // stops, so the old band's levels do not linger.
    clear() {
        this._floor = null;
        this._peaks = [];
        this._last = null;
        this._wctx.fillStyle = this._colours.bg || '#000';
        this._wctx.fillRect(0, 0, this._wf.width, this._wf.height);
        this._draw();
    }

    /** Change any of range, speed, bright, smooth, hold; returns the settings in force. */
    setSettings(s) {
        this._set = cleanSpectrumSettings({ ...this._set, ...s });
        this._frames = 0;
        this._draw();
        return { ...this._set };
    }

    get settings() { return { ...this._set }; }

    /**
     * The trace's width in CSS pixels, 0.5-3; 1.2 until set. Not one of the
     * settings, so the radio panel's kept settings are untouched by it.
     */
    setTraceWidth(px) {
        const n = Number(px);
        this._traceW = Number.isFinite(n) ? Math.max(0.5, Math.min(3, n)) : 1.2;
        this._draw();
    }

    /**
     * auto: the floor follows the noise (true, the default) or stays where it
     * is. ref: dB from the floor to the bottom of the trace, -40..40, or null
     * for the default. Returns the levels in force.
     */
    setLevels({ auto, ref } = {}) {
        if (auto !== undefined) this._auto = auto !== false;
        if (ref !== undefined) {
            const n = Number(ref);
            this._ref = ref === null || !Number.isFinite(n) ? null : Math.max(-40, Math.min(40, Math.round(n)));
        }
        this._draw();
        return this.levels;
    }

    /** floor: the noise floor in the bins' dB (null before the first frame); lo, hi: the trace's window. */
    get levels() {
        const { lo, hi } = this._range();
        return { auto: this._auto, ref: this._ref, floor: this._floor, lo, hi };
    }

    /**
     * Set the range so the strongest peak of the last PEAK_FRAMES frames sits
     * FIT_HEADROOM_DB under the top, keeping the bottom where it is. Returns
     * the range in force, or null with nothing yet to fit.
     */
    fit() {
        if (!this._peaks.length || this._floor == null) return null;
        // dB above the floor that should land on the trace's top, which is
        // 92% of the height (_draw). The default bottom moves with the range.
        const top = Math.max(...this._peaks) + FIT_HEADROOM_DB - this._floor;
        const r = this._ref == null ? top / (0.92 - FLOOR_MARGIN_DB / 60) : (top - this._ref) / 0.92;
        return this.setSettings({ range: Math.ceil(r) }).range;
    }

    push(bins) {
        if (!bins?.length || this._set.hold) return;
        bins = smoothBins(bins, this._set.smooth);
        // A hidden trace's canvas is 1 px wide, and a floor from one column
        // is the average, not the floor: take the columns at the waterfall's
        // width then (a waterfall-only pop-out), or at a screen's.
        const sw = this._spec.width, ww = this._wf.width;
        const cols = binsToColumns(bins, sw > 1 ? sw : ww > 1 ? ww : HIDDEN_COLUMNS);
        const f = percentile(cols, 0.25);
        if (this._floor == null) this._floor = f;
        else if (this._auto) this._floor += (f - this._floor) * FLOOR_ALPHA;
        this._peaks.push(Math.max(...cols));
        if (this._peaks.length > PEAK_FRAMES) this._peaks.shift();
        this._last = cols;
        this._draw();
        if (this._frames++ % this._set.speed === 0) this._waterfallRow(bins);
    }

    // The trace's window: FLOOR_MARGIN_DB of it below the floor, as a share
    // of the 60 dB default so a wide range does not push the floor up.
    _range() {
        const r = this._set.range;
        const lo = (this._floor ?? -100) + this._bottom(r);
        return { lo, hi: lo + r };
    }

    _bottom(r) { return this._ref ?? -FLOOR_MARGIN_DB * r / 60; }

    _resize() {
        const dpr = window.devicePixelRatio || 1;
        for (const [cv, el] of [[this._spec, this._specEl], [this._wf, this._wfEl]]) {
            const r = el.getBoundingClientRect();
            const w = Math.max(1, Math.round(r.width * dpr));
            const h = Math.max(1, Math.round(r.height * dpr));
            if (cv.width !== w || cv.height !== h) {
                const keep = cv === this._wf && cv.width > 1 ? cv : null;
                let old = null;
                if (keep) { old = document.createElement('canvas'); old.width = cv.width; old.height = cv.height; old.getContext('2d').drawImage(cv, 0, 0); }
                cv.width = w; cv.height = h;
                if (cv === this._wf) {
                    this._wctx.fillStyle = this._colours.bg || '#000';
                    this._wctx.fillRect(0, 0, w, h);
                    if (old) this._wctx.drawImage(old, 0, 0, w, h);
                }
            }
        }
        this._last = null;
        this._draw();
    }

    _draw() {
        const ctx = this._sctx, w = this._spec.width, h = this._spec.height;
        ctx.clearRect(0, 0, w, h);
        ctx.strokeStyle = this._colours.grid || '#10283a';
        ctx.lineWidth = 1;
        ctx.beginPath();
        for (let i = 1; i < 4; i++) {
            const x = Math.round(w * i / 4) + 0.5, y = Math.round(h * i / 4) + 0.5;
            ctx.moveTo(x, 0); ctx.lineTo(x, h);
            ctx.moveTo(0, y); ctx.lineTo(w, y);
        }
        ctx.stroke();

        const cols = this._last;
        if (!cols?.length) return;
        const { lo, hi } = this._range();
        const yOf = v => h - Math.max(0, Math.min(1, (v - lo) / (hi - lo))) * h * 0.92;
        if (this._dbScale && this._floor != null) this._drawScale(ctx, h, lo, hi, yOf);
        const step = w / cols.length;

        ctx.beginPath();
        ctx.moveTo(0, h);
        for (let i = 0; i < cols.length; i++) ctx.lineTo(i * step, yOf(cols[i]));
        ctx.lineTo(w, h);
        ctx.closePath();
        ctx.fillStyle = this._colours.fill || 'rgba(95,211,255,.16)';
        ctx.fill();

        ctx.beginPath();
        for (let i = 0; i < cols.length; i++) {
            const x = i * step, y = yOf(cols[i]);
            if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
        }
        ctx.strokeStyle = this._colours.trace || '#7fdcff';
        // Thinner than a device pixel only when asked: below that it greys.
        const tw = this._traceW ?? 1.2;
        ctx.lineWidth = Math.max(tw < 1 ? 0.5 : 1, (window.devicePixelRatio || 1) * tw);
        ctx.stroke();
    }

    // dB above the floor at the left edge, every 5, 10 or 20 dB.
    _drawScale(ctx, h, lo, hi, yOf) {
        const dpr = window.devicePixelRatio || 1;
        const r = hi - lo;
        const step = r <= 30 ? 5 : r <= 90 ? 10 : 20;
        const f = this._floor;
        ctx.save();
        ctx.font = `${Math.round(12 * dpr)}px 'Share Tech Mono', Consolas, monospace`;
        ctx.fillStyle = this._colours.trace || '#7fdcff';
        ctx.globalAlpha = 0.7;
        ctx.textBaseline = 'middle';
        for (let d = Math.ceil((lo - f) / step) * step; f + d <= hi; d += step) {
            const y = yOf(f + d);
            if (y < 8 * dpr || y > h - 6 * dpr) continue;
            ctx.fillText(d > 0 ? '+' + d : String(d), 4 * dpr, y);
        }
        ctx.restore();
    }

    _waterfallRow(bins) {
        const w = this._wf.width, h = this._wf.height;
        if (w < 2 || h < 2) return;
        const rowH = Math.max(1, Math.round(window.devicePixelRatio || 1));
        this._wctx.drawImage(this._wf, 0, 0, w, h - rowH, 0, rowH, w, h - rowH);
        const cols = binsToColumns(bins, w);
        const lo = (this._floor ?? -100) - FLOOR_MARGIN_DB - this._set.bright, hi = lo + WATERFALL_DB;
        const img = this._wctx.createImageData(w, rowH);
        const d = img.data, pal = this._palette;
        for (let x = 0; x < w; x++) {
            const t = Math.max(0, Math.min(255, Math.round((cols[x] - lo) / (hi - lo) * 255)));
            for (let r = 0; r < rowH; r++) {
                const o = (r * w + x) * 4;
                d[o] = pal[t * 3]; d[o + 1] = pal[t * 3 + 1]; d[o + 2] = pal[t * 3 + 2]; d[o + 3] = 255;
            }
        }
        this._wctx.putImageData(img, 0, 0);
    }

    destroy() { this._ro.disconnect(); }
}

function buildPalette(stops) {
    const p = new Uint8ClampedArray(256 * 3);
    for (let i = 0; i < 256; i++) {
        const [r, g, b] = waterfallColour(i / 255, stops);
        p[i * 3] = r; p[i * 3 + 1] = g; p[i * 3 + 2] = b;
    }
    return p;
}
