// Yaesu Web Control – S-Meter History Panel
//
// Compact 30-second strip-chart that sits below the analog S-meter and shows:
//   - History polyline of the last 30 s of raw S-meter readings
//   - Peak-hold horizontal line at the highest reading still inside the window
//   - Noise-floor reference line at the 10th-percentile reading in the window
//
// The radial S-meter gauge already tells you the instantaneous signal level;
// this strip shows you how it's *moving* — QSB fading, brief interference
// spikes, noise floor rising when the neighbour switches a light on.
//
// Off by default. The Index page wires up a toggle button that flips the
// container visibility and stores the choice in localStorage so it persists
// across page reloads.
//
// Architecture: this class owns its canvas and nothing else. No SignalR, no
// DOM access outside the canvas, no string formatting. The caller pushes raw
// 0-255 values via push(); the panel does its own ring-buffer + render loop.

// ?v=1 is a one-time cache-buster, not a number to bump — see gaugeFactory.js.
import { calibrateSMeterLabel } from '../calibration/calibration-engine.js?v=1';

export class SMeterHistoryPanel {

    /**
     * @param {string} canvasId   id of the <canvas> element to render into
     * @param {object} [options]  windowMs (default 30000), gridColor, etc.
     */
    constructor(canvasId, options = {}) {
        this.canvas  = document.getElementById(canvasId);
        if (!this.canvas) {
            console.warn(`SMeterHistoryPanel: canvas "${canvasId}" not found`);
            return;
        }
        this.ctx      = this.canvas.getContext('2d');
        this.windowMs = options.windowMs ?? 30000;
        this.colors   = Object.assign({
            background:  '#1e2a38',
            grid:        'rgba(180,200,220,0.12)',
            gridLabel:   '#8899aa',
            history:     '#4af09a',
            peakHold:    '#f0c040',
            noiseFloor:  'rgba(255,120,120,0.7)'
        }, options.colors ?? {});

        // Ring buffer of {t: ms, v: raw 0-255}. Sorted by t ascending.
        // 30 s × ~10 Hz = ~300 entries — small enough to keep as an array
        // and trim from the head as samples age out.
        this._samples = [];

        // requestAnimationFrame token; we only redraw when there's new data
        // or when the canvas was resized.
        this._rafPending = false;

        // Track size changes so the strip stays sharp on the device-pixel-
        // ratio grid.
        this._lastCssWidth  = 0;
        this._lastCssHeight = 0;
        if (typeof ResizeObserver !== 'undefined') {
            this._ro = new ResizeObserver(() => this._scheduleRender());
            this._ro.observe(this.canvas);
        }
    }

    /**
     * Add a sample. Caller passes the raw 0-255 S-meter value as broadcast
     * by the radio (same value the radial gauge sees). The panel buffers it,
     * trims anything older than windowMs, and schedules a redraw.
     */
    push(rawValue) {
        if (!this.canvas || this._stopped) return;
        const v = Math.max(0, Math.min(255, Number(rawValue) || 0));
        const t = performance.now();
        this._samples.push({ t, v });
        // Drop anything older than windowMs from the front. A while-loop is
        // cheap because we only ever add to the back and drop a handful from
        // the front each push.
        const cutoff = t - this.windowMs;
        while (this._samples.length > 0 && this._samples[0].t < cutoff) {
            this._samples.shift();
        }
        this._scheduleRender();
    }

    /** Clear the buffer (e.g. on band change). */
    reset() {
        this._samples = [];
        this._scheduleRender();
    }

    /**
     * Stop accepting new samples and disconnect the resize observer. Called
     * by the ServerShutdown overlay in site.js when the server is going down,
     * so the panel stops scheduling RAF redraws on a tab the user has clearly
     * abandoned. Idempotent.
     */
    stop() {
        this._stopped = true;
        if (this._ro) {
            try { this._ro.disconnect(); } catch { /* ignore */ }
            this._ro = null;
        }
    }

    _scheduleRender() {
        if (this._rafPending) return;
        this._rafPending = true;
        requestAnimationFrame(() => {
            this._rafPending = false;
            this._render();
        });
    }

    _resizeCanvasIfNeeded() {
        const cssW = this.canvas.clientWidth;
        const cssH = this.canvas.clientHeight;
        if (cssW === this._lastCssWidth && cssH === this._lastCssHeight) return;
        const dpr  = window.devicePixelRatio || 1;
        this.canvas.width  = Math.max(1, Math.floor(cssW * dpr));
        this.canvas.height = Math.max(1, Math.floor(cssH * dpr));
        this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        this._lastCssWidth  = cssW;
        this._lastCssHeight = cssH;
    }

    _render() {
        if (!this.canvas) return;
        this._resizeCanvasIfNeeded();
        const W = this._lastCssWidth;
        const H = this._lastCssHeight;
        if (W <= 0 || H <= 0) return;

        const ctx = this.ctx;
        ctx.clearRect(0, 0, W, H);

        // Background
        ctx.fillStyle = this.colors.background;
        ctx.fillRect(0, 0, W, H);

        const padL = 28;  // room for "S9" labels
        const padR = 4;
        const padT = 4;
        const padB = 12;  // room for time tick at the bottom
        const plotW = Math.max(1, W - padL - padR);
        const plotH = Math.max(1, H - padT - padB);

        // Y-axis tick raw values and labels. We use raw 0-255 internally —
        // the chart math stays simple and the labels come from the same
        // calibration table that drives the radial gauge.
        const yTicks = [0, 64, 128, 192, 255]; // sparse, deliberately
        ctx.font = '9px sans-serif';
        ctx.textBaseline = 'middle';
        ctx.textAlign = 'right';
        ctx.fillStyle = this.colors.gridLabel;
        ctx.strokeStyle = this.colors.grid;
        ctx.lineWidth = 1;
        for (const raw of yTicks) {
            const y = padT + plotH - (raw / 255) * plotH;
            ctx.beginPath();
            ctx.moveTo(padL, y);
            ctx.lineTo(padL + plotW, y);
            ctx.stroke();
            const label = calibrateSMeterLabel(raw) || '';
            ctx.fillText(label, padL - 3, y);
        }

        // Time-axis ticks every 10 s. Labels: "-30s", "-20s", "-10s", "now".
        ctx.textAlign = 'center';
        ctx.textBaseline = 'top';
        for (let s = 0; s <= 30; s += 10) {
            const x = padL + plotW - (s / 30) * plotW;
            ctx.strokeStyle = this.colors.grid;
            ctx.beginPath();
            ctx.moveTo(x, padT);
            ctx.lineTo(x, padT + plotH);
            ctx.stroke();
            ctx.fillStyle = this.colors.gridLabel;
            const label = s === 0 ? 'now' : `-${s}s`;
            ctx.fillText(label, x, padT + plotH + 1);
        }

        if (this._samples.length === 0) return;

        // Map sample (t, v) → plot (x, y).
        const tNow   = performance.now();
        const tLeft  = tNow - this.windowMs;
        const xOf = t => padL + ((t - tLeft) / this.windowMs) * plotW;
        const yOf = v => padT + plotH - (v / 255) * plotH;

        // Noise floor: 10th-percentile of the values in the window. Robust
        // against the occasional signal spike — won't be dragged up just
        // because someone called CQ for two seconds — and reacts quickly
        // when a real noise source switches on (the bulk of the recent
        // values move up together so the 10th percentile follows).
        const sortedV = this._samples.map(s => s.v).sort((a, b) => a - b);
        const p10Idx  = Math.max(0, Math.floor(sortedV.length * 0.10));
        const noiseFloor = sortedV[p10Idx];

        // Peak hold: maximum in window.
        const peak = sortedV[sortedV.length - 1];

        // ── Noise floor line (drawn first so the history sits over it) ──
        ctx.strokeStyle = this.colors.noiseFloor;
        ctx.setLineDash([3, 3]);
        ctx.lineWidth = 1;
        const yNoise = yOf(noiseFloor);
        ctx.beginPath();
        ctx.moveTo(padL, yNoise);
        ctx.lineTo(padL + plotW, yNoise);
        ctx.stroke();
        ctx.setLineDash([]);

        // ── Peak-hold line ───────────────────────────────────────────────
        ctx.strokeStyle = this.colors.peakHold;
        ctx.setLineDash([4, 4]);
        ctx.lineWidth = 1;
        const yPeak = yOf(peak);
        ctx.beginPath();
        ctx.moveTo(padL, yPeak);
        ctx.lineTo(padL + plotW, yPeak);
        ctx.stroke();
        ctx.setLineDash([]);

        // ── History polyline ─────────────────────────────────────────────
        ctx.strokeStyle = this.colors.history;
        ctx.lineWidth = 1.25;
        ctx.beginPath();
        let first = true;
        for (const s of this._samples) {
            const x = xOf(s.t);
            const y = yOf(s.v);
            if (first) { ctx.moveTo(x, y); first = false; }
            else        { ctx.lineTo(x, y); }
        }
        ctx.stroke();
    }
}
