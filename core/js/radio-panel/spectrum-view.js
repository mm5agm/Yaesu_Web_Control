// spectrum-view.js - the radio panel's spectrum trace and waterfall.
//
// Takes FFT bins already ordered low to high frequency, in dB, and draws them
// into two canvases. It knows nothing about SDRs, SignalR or which radio is
// attached: the app's adapter does the ordering (Yaesu IF frames arrive
// inverted) and hands over plain numbers.
//
// The vertical scale follows the noise floor so a quiet band and a loud one
// both fill the screen: the floor is a slow average of the lower quartile and
// the screen shows 60 dB above it. Columns keep the strongest bin they cover,
// so a single-bin CW carrier is not averaged away at wide spans.

import { binsToColumns, percentile, waterfallColour } from './panel-math.js';

const RANGE_DB = 60;
const FLOOR_MARGIN_DB = 6;
const FLOOR_ALPHA = 0.08;

export class SpectrumView {
    constructor(spectrumEl, waterfallEl) {
        this._specEl = spectrumEl;
        this._wfEl = waterfallEl;
        this._spec = document.createElement('canvas');
        this._wf = document.createElement('canvas');
        spectrumEl.prepend(this._spec);
        waterfallEl.prepend(this._wf);
        this._sctx = this._spec.getContext('2d');
        this._wctx = this._wf.getContext('2d', { willReadFrequently: false });
        this._floor = null;
        this._last = null;
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
        this._last = null;
        this._wctx.fillStyle = this._colours.bg || '#000';
        this._wctx.fillRect(0, 0, this._wf.width, this._wf.height);
        this._draw();
    }

    push(bins) {
        if (!bins?.length) return;
        const cols = binsToColumns(bins, Math.max(1, this._spec.width));
        const f = percentile(cols, 0.25);
        this._floor = this._floor == null ? f : this._floor + (f - this._floor) * FLOOR_ALPHA;
        this._last = cols;
        this._draw();
        this._waterfallRow(bins);
    }

    _range() {
        const lo = (this._floor ?? -100) - FLOOR_MARGIN_DB;
        return { lo, hi: lo + RANGE_DB };
    }

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
        ctx.lineWidth = Math.max(1, (window.devicePixelRatio || 1) * 1.2);
        ctx.stroke();
    }

    _waterfallRow(bins) {
        const w = this._wf.width, h = this._wf.height;
        if (w < 2 || h < 2) return;
        const rowH = Math.max(1, Math.round(window.devicePixelRatio || 1));
        this._wctx.drawImage(this._wf, 0, 0, w, h - rowH, 0, rowH, w, h - rowH);
        const cols = binsToColumns(bins, w);
        const { lo, hi } = this._range();
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
