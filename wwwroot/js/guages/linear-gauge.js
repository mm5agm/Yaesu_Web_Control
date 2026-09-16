// Yaesu Web Control – Compact horizontal bar gauges (PoC)
// Canvas bar-graph meters: labelled track + fill + optional ticks.
// Exposes this.gauge = { value, draw() } so update-engine.js is unchanged.
// No calibration, no WebSocket, no orchestrator logic.

/**
 * Base horizontal bar gauge.
 * Config shape (subset used by subclasses):
 *   renderTo, width, height, minValue, maxValue, value,
 *   highlights: [{ from, to, color }],
 *   majorTicks: string[],
 *   colorBar, colorBarProgress, colorMajorTicks, colorNumbers, colorPlate,
 *   gaugeTitle, gaugeTitleId, gaugeTitleDefault, gaugeTitleSuffix,
 *   gaugeTitleBg, gaugeTitleColor
 */
export class LinearGauge {
    static defaultWidth = 160;
    static defaultHeight = 28;

    constructor(canvasId, config = {}) {
        this.canvasId = canvasId;
        this.config = Object.assign({
            width: LinearGauge.defaultWidth,
            height: LinearGauge.defaultHeight,
            minValue: 0,
            maxValue: 100,
            value: 0,
            highlights: [],
            majorTicks: [],
            colorBar: '#3a3a3a',
            colorBarProgress: '#198754',
            colorMajorTicks: '#c8c8c8',
            colorNumbers: '#c8c8c8',
            colorPlate: 'transparent',
            borders: false
        }, config);

        // Compatible with update-engine.js (sets gauge.value then calls draw).
        const self = this;
        this.gauge = {
            get value() { return self._value; },
            set value(v) { self._value = Number(v) || 0; },
            draw() { self.draw(); }
        };
        this._value = this.config.value || 0;
        this._canvas = null;
        this._ctx = null;
    }

    render() {
        const canvas = document.getElementById(this.canvasId);
        if (!canvas) return;

        this._canvas = canvas;
        this._ctx = canvas.getContext('2d');
        this._applySize();
        this.draw();

        // Fill the flex slot in the operating-controls column. The radial
        // gauges are fixed-size; these bars should track the column width.
        if (!this._resizeObserver && typeof ResizeObserver !== 'undefined') {
            this._resizeObserver = new ResizeObserver(() => {
                this._applySize();
                this.draw();
            });
            // Observe the row (parent) so we get width when the flex layout settles.
            const row = canvas.parentElement;
            if (row) this._resizeObserver.observe(row);
        }
    }

    _applySize() {
        const canvas = this._canvas;
        if (!canvas) return;

        const row = canvas.parentElement;
        // Prefer the flex-allocated canvas width; fall back to config.
        let w = this.config.width || LinearGauge.defaultWidth;
        if (row) {
            const label = row.querySelector('.linear-meter-label');
            const value = row.querySelector('.linear-meter-value');
            const gap = 8; // ~0.35rem * 2 between three flex items
            const used = (label ? label.offsetWidth : 0) + (value ? value.offsetWidth : 0) + gap;
            const avail = row.clientWidth - used;
            if (avail > 40) w = avail;
        }
        const h = this.config.height || LinearGauge.defaultHeight;

        // Store so draw() uses the live size, not the constructor default.
        this.config.width = w;
        this.config.height = h;
        canvas.width = w;
        canvas.height = h;
        canvas.style.width = w + 'px';
        canvas.style.height = h + 'px';
        canvas.removeAttribute('title');
    }

    draw() {
        const ctx = this._ctx;
        const canvas = this._canvas;
        if (!ctx || !canvas) return;

        const w = this.config.width;
        const h = this.config.height;
        const min = this.config.minValue;
        const max = this.config.maxValue;
        const range = max - min || 1;
        const value = Math.max(min, Math.min(max, this._value));

        // Layout: small top margin for tick numbers, track in the middle, tiny bottom pad.
        const padX = 2;
        const trackTop = 10;
        const trackHeight = Math.max(8, h - trackTop - 4);
        const trackLeft = padX;
        const trackWidth = w - padX * 2;

        ctx.clearRect(0, 0, w, h);

        if (this.config.colorPlate && this.config.colorPlate !== 'transparent') {
            ctx.fillStyle = this.config.colorPlate;
            ctx.fillRect(0, 0, w, h);
        }

        // Track background
        ctx.fillStyle = this.config.colorBar || '#3a3a3a';
        ctx.fillRect(trackLeft, trackTop, trackWidth, trackHeight);

        // Highlight zones (under the fill so progress sits on top)
        const highlights = this.config.highlights || [];
        for (const zone of highlights) {
            const z0 = Math.max(min, zone.from);
            const z1 = Math.min(max, zone.to);
            if (z1 <= z0) continue;
            const x0 = trackLeft + ((z0 - min) / range) * trackWidth;
            const x1 = trackLeft + ((z1 - min) / range) * trackWidth;
            ctx.fillStyle = zone.color;
            ctx.fillRect(x0, trackTop, x1 - x0, trackHeight);
        }

        // Fill from left to value
        const fillFrac = (value - min) / range;
        const fillW = Math.max(0, fillFrac * trackWidth);
        if (fillW > 0) {
            ctx.fillStyle = this.config.colorBarProgress || '#198754';
            ctx.fillRect(trackLeft, trackTop, fillW, trackHeight);
        }

        // Tick marks + numbers — same count/labels as the radial face.
        const ticks = this.config.majorTicks || [];
        if (ticks.length > 1) {
            ctx.strokeStyle = this.config.colorMajorTicks || '#c8c8c8';
            ctx.fillStyle = this.config.colorNumbers || '#c8c8c8';
            ctx.lineWidth = 1;
            // Slightly smaller than the radial overlay so nine labels fit a narrow bar.
            ctx.font = '8px sans-serif';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'bottom';

            for (let i = 0; i < ticks.length; i++) {
                const frac = i / (ticks.length - 1);
                const x = trackLeft + frac * trackWidth;
                ctx.beginPath();
                ctx.moveTo(x, trackTop);
                ctx.lineTo(x, trackTop + 3);
                ctx.stroke();
                ctx.fillText(ticks[i], x, trackTop - 1);
            }
        }

        // Thin border around the track
        ctx.strokeStyle = this.config.colorMajorTicks || '#c8c8c8';
        ctx.lineWidth = 1;
        ctx.strokeRect(trackLeft + 0.5, trackTop + 0.5, trackWidth - 1, trackHeight - 1);
    }
}

// ------------------------------------------------------------
// POWER — same scale / highlight zones as PowerGauge (radial)
// ------------------------------------------------------------

export class LinearPowerGauge extends LinearGauge {
    constructor(canvasId, options = {}) {
        // Nine ticks matching PowerGauge (radial): 0 … maxValue in 8 equal steps.
        const maxValue = options.maxValue ?? 200;
        const ticks = Array.from({ length: 9 }, (_, i) =>
            String(Math.round((maxValue * i) / 8)));
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 0,
            maxValue,
            majorTicks: ticks,
            // Green to 75 % of rated output, amber to 87.5 %, red above — same as radial.
            highlights: [
                { from: 0, to: maxValue * 0.75, color: 'rgba(0,255,0,.25)' },
                { from: maxValue * 0.75, to: maxValue * 0.875, color: 'rgba(255,255,0,.25)' },
                { from: maxValue * 0.875, to: maxValue, color: 'rgba(255,0,0,.25)' }
            ],
            colorBarProgress: '#198754',
            colorBar: '#3a3a3a',
            value: 0,
            gaugeTitle: 'PWR',
            gaugeTitleId: 'powerLinearValue',
            gaugeTitleDefault: '0',
            gaugeTitleSuffix: ' W',
            gaugeTitleBg: '#dc3545',
            gaugeTitleColor: '#ffffff'
        }, options);

        super(canvasId, config);
    }
}

// ------------------------------------------------------------
// SWR — same 0–255 needle scale as SWRGauge (radial)
//   gauge value = (SWR - 1.0) * 127.5; face 1.0–3.0
// ------------------------------------------------------------

export class LinearSWRGauge extends LinearGauge {
    constructor(canvasId, options = {}) {
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 0,
            maxValue: 255,
            // Same nine face labels as SWRGauge (radial). Needle units stay
            // (SWR - 1.0) * 127.5 — SWR 1.5 → 64, 2.0 → 127.5, 3.0 → 255.
            majorTicks: ['1.0', '1.25', '1.5', '1.75', '2.0', '2.25', '2.5', '2.75', '3.0'],
            highlights: [
                { from: 0, to: 64, color: 'rgba(0,255,0,.25)' },
                { from: 64, to: 127, color: 'rgba(255,255,0,.25)' },
                { from: 127, to: 255, color: 'rgba(255,0,0,.25)' }
            ],
            colorBarProgress: '#198754',
            colorBar: '#3a3a3a',
            value: 0,
            gaugeTitle: 'SWR',
            gaugeTitleId: 'swrLinearValue',
            gaugeTitleDefault: '1.0:1',
            gaugeTitleBg: '#dc3545',
            gaugeTitleColor: '#ffffff'
        }, options);

        super(canvasId, config);
    }
}

// ------------------------------------------------------------
// ALC — same 0–255 needle / 0–50 face labels as ALCGauge (radial)
// ------------------------------------------------------------

export class LinearALCGauge extends LinearGauge {
    constructor(canvasId, options = {}) {
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 0,
            maxValue: 255,
            majorTicks: ['0', '6', '12', '19', '25', '31', '37', '44', '50'],
            highlights: [
                { from: 0, to: 178, color: 'rgba(0,255,0,.25)' },
                { from: 178, to: 230, color: 'rgba(255,255,0,.25)' },
                { from: 230, to: 255, color: 'rgba(255,0,0,.25)' }
            ],
            colorBarProgress: '#198754',
            colorBar: '#3a3a3a',
            value: 0,
            gaugeTitle: 'ALC',
            gaugeTitleId: 'alcLinearValue',
            gaugeTitleDefault: '0V',
            gaugeTitleBg: '#0dcaf0',
            gaugeTitleColor: '#000000'
        }, options);

        super(canvasId, config);
    }
}

// ------------------------------------------------------------
// COMPRESSION — same 0–20 dB scale as CompressionGauge (radial)
// ------------------------------------------------------------

export class LinearCompressionGauge extends LinearGauge {
    constructor(canvasId, options = {}) {
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 0,
            maxValue: 20,
            majorTicks: ['0', '2.5', '5', '7.5', '10', '12.5', '15', '17.5', '20'],
            highlights: [
                { from: 0, to: 5, color: 'rgba(0,255,0,.25)' },
                { from: 5, to: 10, color: 'rgba(255,255,0,.25)' },
                { from: 10, to: 20, color: 'rgba(255,0,0,.25)' }
            ],
            colorBarProgress: '#198754',
            colorBar: '#3a3a3a',
            value: 0,
            gaugeTitle: 'COMP',
            gaugeTitleId: 'compressionLinearValue',
            gaugeTitleDefault: '0',
            gaugeTitleSuffix: 'dB',
            gaugeTitleBg: '#ffc107',
            gaugeTitleColor: '#000000'
        }, options);

        super(canvasId, config);
    }
}

// ------------------------------------------------------------
// S-METER — same 0–255 scale / S0…+60 labels as SMeterGauge (radial)
// ------------------------------------------------------------

export class LinearSMeterGauge extends LinearGauge {
    constructor(canvasId, options = {}) {
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 0,
            maxValue: 255,
            majorTicks: ['S0', 'S1', 'S3', 'S5', 'S7', 'S9', '+20', '+40', '+60'],
            highlights: [
                { from: 0, to: 130, color: 'rgba(0,255,0,.25)' },
                { from: 130, to: 255, color: 'rgba(255,0,0,.25)' }
            ],
            colorBarProgress: '#198754',
            colorBar: '#3a3a3a',
            value: 0,
            gaugeTitle: 'S',
            gaugeTitleId: options.gaugeTitleId || 'sMeterLinearValue',
            gaugeTitleDefault: 'S0',
            gaugeTitleBg: '#198754',
            gaugeTitleColor: '#ffffff'
        }, options);

        super(canvasId, config);
    }
}

// ------------------------------------------------------------
// PA TEMPERATURE — same 0–100 °C scale as TempGauge (radial)
// ------------------------------------------------------------

export class LinearTempGauge extends LinearGauge {
    constructor(canvasId, options = {}) {
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 0,
            maxValue: 100,
            majorTicks: ['0', '13', '25', '38', '50', '63', '75', '88', '100'],
            highlights: [
                { from: 0, to: 40, color: 'rgba(0,255,0,.25)' },
                { from: 40, to: 60, color: 'rgba(255,255,0,.25)' },
                { from: 60, to: 100, color: 'rgba(255,0,0,.25)' }
            ],
            colorBarProgress: '#198754',
            colorBar: '#3a3a3a',
            value: 0,
            gaugeTitle: 'TEMP',
            gaugeTitleId: 'tempLinearValue',
            gaugeTitleDefault: '--',
            gaugeTitleSuffix: '°C',
            gaugeTitleBg: '#198754',
            gaugeTitleColor: '#ffffff'
        }, options);

        super(canvasId, config);
    }
}

// ------------------------------------------------------------
// IDD — same 0–25 A scale as IDDGauge (radial)
// ------------------------------------------------------------

export class LinearIDDGauge extends LinearGauge {
    constructor(canvasId, options = {}) {
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 0,
            maxValue: 25,
            majorTicks: ['0', '3', '6', '9', '12', '16', '19', '22', '25'],
            highlights: [
                { from: 0, to: 10, color: 'rgba(0,255,0,.25)' },
                { from: 10, to: 20, color: 'rgba(255,255,0,.25)' },
                { from: 20, to: 25, color: 'rgba(255,0,0,.25)' }
            ],
            colorBarProgress: '#198754',
            colorBar: '#3a3a3a',
            value: 0,
            gaugeTitle: 'IDD',
            gaugeTitleId: 'iddLinearValue',
            gaugeTitleDefault: '0.0',
            gaugeTitleSuffix: 'A',
            gaugeTitleBg: '#0d6efd',
            gaugeTitleColor: '#ffffff'
        }, options);

        super(canvasId, config);
    }
}

// ------------------------------------------------------------
// VDD — same 40–55 V scale as VDDGauge (radial)
// ------------------------------------------------------------

export class LinearVDDGauge extends LinearGauge {
    constructor(canvasId, options = {}) {
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 40,
            maxValue: 55,
            majorTicks: ['40', '42', '44', '46', '48', '50', '52', '54', '55'],
            highlights: [
                { from: 40, to: 45, color: 'rgba(255,255,0,.25)' },
                { from: 45, to: 52, color: 'rgba(0,255,0,.25)' },
                { from: 52, to: 55, color: 'rgba(255,0,0,.25)' }
            ],
            colorBarProgress: '#198754',
            colorBar: '#3a3a3a',
            value: 48,
            gaugeTitle: 'VDD',
            gaugeTitleId: 'vddLinearValue',
            gaugeTitleDefault: '48.0',
            gaugeTitleSuffix: 'V',
            gaugeTitleBg: '#198754',
            gaugeTitleColor: '#ffffff'
        }, options);

        super(canvasId, config);
    }
}
