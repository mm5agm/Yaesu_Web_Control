// gauge.js - Base and derived classes for all meter gauges
// Requires: canvas-gauge library loaded globally (RadialGauge)

class Gauge {
    static defaultWidth = 220;
    static defaultHeight = 135;

    constructor(canvasId, config) {
        this.canvasId = canvasId;
        // Use defaults if not provided
        this.config = Object.assign({
            width: Gauge.defaultWidth,
            height: Gauge.defaultHeight
        }, config);
        this.gauge = null;
    }

    render() {
        if (!window.RadialGauge) {
            return;
        }

        // Set canvas size to match config
        const canvas = document.getElementById(this.canvasId);
        if (canvas) {
            canvas.width = this.config.width;
            canvas.height = this.config.height;
            canvas.style.width = this.config.width + 'px';
            canvas.style.height = this.config.height + 'px';
            const canvasOffset = this.getCanvasOffset();
            if (canvasOffset) {
                canvas.style.position = 'relative';
                canvas.style.left = canvasOffset + 'px';
            } else {
                canvas.style.position = '';
                canvas.style.left = '';
            }
        }

        // Create gauge
        this.gauge = new RadialGauge(this.config);
        this.gauge.draw();

        // The canvas-gauges library may inject a `title` attribute during draw(),
        // which NVDA reads in preference to aria-label. Strip it so the aria-label wins.
        if (canvas) {
            canvas.removeAttribute('title');
        }

        // Create overlay labels
        this.createLabels();
    }

    getCanvasOffset() { return 0; }
    getLabelCenterXOffset() { return 0; }

    createLabels() {
        const canvas = document.getElementById(this.canvasId);
        if (!canvas) return;

        const wrapper = canvas.parentNode;
        wrapper.style.position = "relative";
        wrapper.style.width = this.config.width + 'px';
        wrapper.style.height = this.config.height + 'px';

        // Remove any existing overlay (prevents stale labels)
        const existing = wrapper.querySelector('.gauge-labels-overlay');
        if (existing) existing.remove();

        // Overlay container — hidden from screen readers; the canvas has aria-label.
        const labelsDiv = document.createElement('div');
        labelsDiv.className = 'gauge-labels-overlay';
        labelsDiv.setAttribute('aria-hidden', 'true');
        labelsDiv.style.position = 'absolute';
        labelsDiv.style.left = '0';
        labelsDiv.style.top = '0';
        labelsDiv.style.width = this.config.width + 'px';
        labelsDiv.style.height = this.config.height + 'px';
        labelsDiv.style.pointerEvents = 'none';

        // Use ONLY your own labels
        const labels = this.config.labels || [];

        // Canvas-gauges centres the radial gauge at (width/2, height/2).
        // The label arc uses the same centre with a radius slightly inside
        // the gauge arc radius (which is ~min(width,height)/2).
        let centerX = this.config.width / 2;
        const centerY = this.config.height / 2;
        const radius = this.config.width * 0.32;

        centerX += this.getLabelCenterXOffset();

        const angleStep = 180 / (labels.length - 1);

        labels.forEach((label, index) => {
            const angle = 180 - (angleStep * index);
            const radians = angle * Math.PI / 180;

            const x = centerX + radius * Math.cos(radians);
            const y = centerY - radius * Math.sin(radians);

            const span = document.createElement('span');
            span.className = 'gauge-label';
            span.textContent = label;
            span.style.position = 'absolute';
            span.style.left = `${x}px`;
            span.style.top = `${y}px`;
            span.style.transform = 'translate(-50%, -50%)';
            span.style.fontSize = '0.8rem';
            span.style.fontWeight = 'bold';
            span.style.color = '#333';
            labelsDiv.appendChild(span);
        });

        // Title label: purpose text + live value, centred below the gauge pivot point
        if (this.config.gaugeTitleShow !== false && this.config.gaugeTitle) {
            const bg     = this.config.gaugeTitleBg    || '#6c757d';
            const fg     = this.config.gaugeTitleColor || '#ffffff';
            const suffix = this.config.gaugeTitleSuffix || '';
            const defVal = this.config.gaugeTitleDefault || '';
            const valId  = this.config.gaugeTitleId || '';

            const titleDiv = document.createElement('div');
            titleDiv.style.cssText = `position:absolute;left:${centerX}px;top:${centerY + 10}px;transform:translateX(-50%);white-space:nowrap;background:${bg};color:${fg};padding:2px 8px;border-radius:4px;font-size:12px;font-weight:bold;z-index:100;`;
            titleDiv.innerHTML = `${this.config.gaugeTitle} <span id="${valId}" style="display:inline-block;min-width:3ch;text-align:right;">${defVal}</span>${suffix}`;
            labelsDiv.appendChild(titleDiv);
        }

        wrapper.appendChild(labelsDiv);
    }
}

// ------------------------------------------------------------
// S-METER
// ------------------------------------------------------------

class SMeterGauge extends Gauge {
    constructor(canvasId, options = {}) {
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 0,
            maxValue: 255,
            majorTicks: ["0", "4", "30", "65", "95", "130", "171", "212", "255"],
            highlights: [
                { from: 0, to: 130, color: "rgba(0,255,0,.25)" },
                { from: 130, to: 255, color: "rgba(255,0,0,.25)" }
            ],
            labels: ["S0", "S1", "S3", "S5", "S7", "S9", "+20", "+40", "+60"],
            startAngle: 90,
            ticksAngle: 180,
            valueBox: false,
            minorTicks: 0,
            strokeTicks: false,
            tickSide: "out",
            needleSide: "center",
            colorPlate: "transparent",
            borders: false,
            needleShadow: false,
            colorMajorTicks: "#555555",
            colorMinorTicks: "transparent",
            colorNumbers: "transparent",
            fontNumbersSize: 0,
            colorBarProgress: "#198754",
            colorBarProgressEnd: "#198754",
            colorBar: "#dddddd",
            barShadow: 0,
            barWidth: 10,
            barStrokeWidth: 0,
            needleType: "arrow",
            needleWidth: 3,
            needleCircleSize: 7,
            needleCircleOuter: false,
            needleCircleInner: true,
            colorNeedleCircleInner: "#dc3545",
            colorNeedleCircleInnerEnd: "#dc3545",
            animationDuration: 400,
            animationRule: "linear",
            value: 0,
            // Title label under the gauge, matching SWR / Power / Temp etc.
            // Added in v2.3.9 when the S-meter moved into the top meter row
            // (out of the per-VFO panels) -- having a "S-Meter S5" label
            // makes it visually consistent with the neighbouring gauges.
            // The text is updated by site.js updateSMeter() with the
            // calibrated S-unit (e.g. "S5", "S9+20").
            gaugeTitleShow: true,
            gaugeTitle: 'S-Meter',
            gaugeTitleId: 'sMeterValue',
            gaugeTitleDefault: 'S0',
            gaugeTitleBg: '#198754',
            gaugeTitleColor: '#ffffff'
        }, options);

        super(canvasId, config);
    }

    getCanvasOffset() { return 0; }
    getLabelCenterXOffset() { return 0; }
}

// ------------------------------------------------------------
// POWER METER
// ------------------------------------------------------------

class PowerGauge extends Gauge {
    constructor(canvasId, options = {}) {
        console.log('[PowerGauge] constructor called for', canvasId);
        // Scale the dial to the radio's own maximum. The face was fixed at
        // 0-200 W (the FTdx101MP's rating) for every model, so on a 100 W radio
        // full output only reached the middle of the arc. Nine ticks either way,
        // so 200 W still reads 0/25/.../200 exactly as before and 100 W reads
        // 0/13/.../100. Caller passes maxValue; see RadioCapabilities.MaxPowerWatts.
        const maxValue = options.maxValue ?? 200;
        const ticks = Array.from({ length: 9 }, (_, i) => String(Math.round((maxValue * i) / 8)));
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 0,
            maxValue,
            majorTicks: ticks,
            // Green to 75 % of rated output, amber to 87.5 %, red above.
            highlights: [
                { from: 0, to: maxValue * 0.75, color: "rgba(0,255,0,.25)" },
                { from: maxValue * 0.75, to: maxValue * 0.875, color: "rgba(255,255,0,.25)" },
                { from: maxValue * 0.875, to: maxValue, color: "rgba(255,0,0,.25)" }
            ],
            labels: ticks,
            startAngle: 90,
            ticksAngle: 180,
            valueBox: false,
            minorTicks: 0,
            strokeTicks: false,
            tickSide: "out",
            needleSide: "center",
            colorPlate: "transparent",
            borders: false,
            needleShadow: false,
            colorMajorTicks: "#555555",
            colorMinorTicks: "transparent",
            colorNumbers: "transparent",
            fontNumbersSize: 0,
            colorBarProgress: "#198754",
            colorBarProgressEnd: "#198754",
            colorBar: "#dddddd",
            barShadow: 0,
            barWidth: 10,
            barStrokeWidth: 0,
            needleType: "arrow",
            needleWidth: 3,
            needleCircleSize: 7,
            needleCircleOuter: false,
            needleCircleInner: true,
            colorNeedleCircleInner: "#dc3545",
            colorNeedleCircleInnerEnd: "#dc3545",
            animationDuration: 400,
            animationRule: "linear",
            value: 0,
            gaugeTitleShow: true,
            gaugeTitle: 'Power Out',
            gaugeTitleId: 'powerMeterValue',
            gaugeTitleDefault: '0',
            gaugeTitleSuffix: 'W',
            gaugeTitleBg: '#dc3545',
            gaugeTitleColor: '#ffffff'
        }, options);

        super(canvasId, config);
    }

    render() {
        console.log('[PowerGauge] render called for', this.canvasId);
        super.render();
    }
}

// ------------------------------------------------------------
// SWR METER
// ------------------------------------------------------------

class SWRGauge extends Gauge {
    constructor(canvasId, options = {}) {
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 0,
            maxValue: 255,
            majorTicks: ["0", "32", "64", "96", "128", "160", "192", "224", "255"],
            // Highlight zones match the linear SWR scale used by the orchestrator:
            //   gauge value = (SWR - 1.0) * 127.5
            //   SWR 1.5 → 64,  SWR 2.0 → 127,  SWR 3.0 → 255
            highlights: [
                { from: 0,   to: 64,  color: "rgba(0,255,0,.25)" },
                { from: 64,  to: 127, color: "rgba(255,255,0,.25)" },
                { from: 127, to: 255, color: "rgba(255,0,0,.25)" }
            ],
            // 9 evenly-spaced labels across the arc, matching the linear 1.0–3.0 scale.
            labels: ["1.0", "1.25", "1.5", "1.75", "2.0", "2.25", "2.5", "2.75", "3.0"],
            startAngle: 90,
            ticksAngle: 180,
            valueBox: false,
            minorTicks: 0,
            strokeTicks: false,
            tickSide: "out",
            needleSide: "center",
            colorPlate: "transparent",
            borders: false,
            needleShadow: false,
            colorMajorTicks: "#555555",
            colorMinorTicks: "transparent",
            colorNumbers: "transparent",
            fontNumbersSize: 0,
            colorBarProgress: "#198754",
            colorBarProgressEnd: "#198754",
            colorBar: "#dddddd",
            barShadow: 0,
            barWidth: 10,
            barStrokeWidth: 0,
            needleType: "arrow",
            needleWidth: 3,
            needleCircleSize: 7,
            needleCircleOuter: false,
            needleCircleInner: true,
            colorNeedleCircleInner: "#dc3545",
            colorNeedleCircleInnerEnd: "#dc3545",
            animationDuration: 400,
            animationRule: "linear",
            value: 0,
            gaugeTitleShow: true,
            gaugeTitle: 'SWR',
            gaugeTitleId: 'swrMeterValue',
            gaugeTitleDefault: '1.0:1',
            gaugeTitleBg: '#dc3545',
            gaugeTitleColor: '#ffffff'
        }, options);

        super(canvasId, config);
    }
}

// ------------------------------------------------------------
// ALC METER
// ------------------------------------------------------------

class ALCGauge extends Gauge {
    constructor(canvasId, options = {}) {
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 0,
            maxValue: 255,
            majorTicks: ["0", "32", "64", "96", "128", "160", "192", "224", "255"],
            highlights: [
                { from: 0, to: 178, color: "rgba(0,255,0,.25)" },
                { from: 178, to: 230, color: "rgba(255,255,0,.25)" },
                { from: 230, to: 255, color: "rgba(255,0,0,.25)" }
            ],
            labels: ["0", "6", "12", "19", "25", "31", "37", "44", "50"],
            startAngle: 90,
            ticksAngle: 180,
            valueBox: false,
            minorTicks: 0,
            strokeTicks: false,
            tickSide: "out",
            needleSide: "center",
            colorPlate: "transparent",
            borders: false,
            needleShadow: false,
            colorMajorTicks: "#555555",
            colorMinorTicks: "transparent",
            colorNumbers: "transparent",
            fontNumbersSize: 0,
            colorBarProgress: "#198754",
            colorBarProgressEnd: "#198754",
            colorBar: "#dddddd",
            barShadow: 0,
            barWidth: 10,
            barStrokeWidth: 0,
            needleType: "arrow",
            needleWidth: 3,
            needleCircleSize: 7,
            needleCircleOuter: false,
            needleCircleInner: true,
            colorNeedleCircleInner: "#dc3545",
            colorNeedleCircleInnerEnd: "#dc3545",
            animationDuration: 400,
            animationRule: "linear",
            value: 0,
            gaugeTitleShow: true,
            gaugeTitle: 'ALC',
            gaugeTitleId: 'alcMeterValue',
            gaugeTitleDefault: '0V',
            gaugeTitleBg: '#0dcaf0',
            gaugeTitleColor: '#000000'
        }, options);

        super(canvasId, config);
    }
}

// ------------------------------------------------------------
// PA TEMPERATURE METER
// ------------------------------------------------------------

class TempGauge extends Gauge {
    constructor(canvasId, options = {}) {
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 0,
            maxValue: 100,
            majorTicks: ["0", "13", "25", "38", "50", "63", "75", "88", "100"],
            highlights: [
                { from: 0,  to: 40,  color: "rgba(0,255,0,.25)" },
                { from: 40, to: 60,  color: "rgba(255,255,0,.25)" },
                { from: 60, to: 100, color: "rgba(255,0,0,.25)" }
            ],
            labels: ["0", "13", "25", "38", "50", "63", "75", "88", "100"],
            startAngle: 90,
            ticksAngle: 180,
            valueBox: false,
            minorTicks: 0,
            strokeTicks: false,
            tickSide: "out",
            needleSide: "center",
            colorPlate: "transparent",
            borders: false,
            needleShadow: false,
            colorMajorTicks: "#555555",
            colorMinorTicks: "transparent",
            colorNumbers: "transparent",
            fontNumbersSize: 0,
            colorBarProgress: "#198754",
            colorBarProgressEnd: "#dc3545",
            colorBar: "#dddddd",
            barShadow: 0,
            barWidth: 10,
            barStrokeWidth: 0,
            needleType: "arrow",
            needleWidth: 3,
            needleCircleSize: 7,
            needleCircleOuter: false,
            needleCircleInner: true,
            colorNeedleCircleInner: "#dc3545",
            colorNeedleCircleInnerEnd: "#dc3545",
            animationDuration: 400,
            animationRule: "linear",
            value: 0,
            gaugeTitleShow: true,
            gaugeTitle: 'PA Temp',
            gaugeTitleId: 'paTemperatureValue',
            gaugeTitleDefault: '--',
            gaugeTitleSuffix: '°C',
            gaugeTitleBg: '#198754',
            gaugeTitleColor: '#ffffff'
        }, options);

        super(canvasId, config);
    }
}

// ------------------------------------------------------------
// COMPRESSION METER
// ------------------------------------------------------------

class CompressionGauge extends Gauge {
    constructor(canvasId, options = {}) {
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 0,
            maxValue: 20,
            majorTicks: ["0", "2.5", "5", "7.5", "10", "12.5", "15", "17.5", "20"],
            highlights: [
                { from: 0,  to: 5,  color: "rgba(0,255,0,.25)" },
                { from: 5,  to: 10, color: "rgba(255,255,0,.25)" },
                { from: 10, to: 20, color: "rgba(255,0,0,.25)" }
            ],
            labels: ["0", "2.5", "5", "7.5", "10", "12.5", "15", "17.5", "20"],
            startAngle: 90,
            ticksAngle: 180,
            valueBox: false,
            minorTicks: 0,
            strokeTicks: false,
            tickSide: "out",
            needleSide: "center",
            colorPlate: "transparent",
            borders: false,
            needleShadow: false,
            colorMajorTicks: "#555555",
            colorMinorTicks: "transparent",
            colorNumbers: "transparent",
            fontNumbersSize: 0,
            colorBarProgress: "#198754",
            colorBarProgressEnd: "#dc3545",
            colorBar: "#dddddd",
            barShadow: 0,
            barWidth: 10,
            barStrokeWidth: 0,
            needleType: "arrow",
            needleWidth: 3,
            needleCircleSize: 7,
            needleCircleOuter: false,
            needleCircleInner: true,
            colorNeedleCircleInner: "#dc3545",
            colorNeedleCircleInnerEnd: "#dc3545",
            animationDuration: 400,
            animationRule: "linear",
            value: 0,
            gaugeTitleShow: true,
            gaugeTitle: 'Compression',
            gaugeTitleId: 'compressionMeterValue',
            gaugeTitleDefault: '0',
            gaugeTitleSuffix: 'dB',
            gaugeTitleBg: '#ffc107',
            gaugeTitleColor: '#000000'
        }, options);

        super(canvasId, config);
    }
}

// ------------------------------------------------------------
// IDD METER
// ------------------------------------------------------------

class IDDGauge extends Gauge {
    constructor(canvasId, options = {}) {
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 0,
            maxValue: 25,
            majorTicks: ["0", "3", "6", "9", "12", "16", "19", "22", "25"],
            highlights: [
                { from: 0, to: 10, color: "rgba(0,255,0,.25)" },
                { from: 10, to: 20, color: "rgba(255,255,0,.25)" },
                { from: 20, to: 25, color: "rgba(255,0,0,.25)" }
            ],
            labels: ["0", "3", "6", "9", "12", "16", "19", "22", "25"],
            startAngle: 90,
            ticksAngle: 180,
            valueBox: false,
            minorTicks: 0,
            strokeTicks: false,
            tickSide: "out",
            needleSide: "center",
            colorPlate: "transparent",
            borders: false,
            needleShadow: false,
            colorMajorTicks: "#555555",
            colorMinorTicks: "transparent",
            colorNumbers: "transparent",
            fontNumbersSize: 0,
            colorBarProgress: "#198754",
            colorBarProgressEnd: "#dc3545",
            colorBar: "#dddddd",
            barShadow: 0,
            barWidth: 10,
            barStrokeWidth: 0,
            needleType: "arrow",
            needleWidth: 3,
            needleCircleSize: 7,
            needleCircleOuter: false,
            needleCircleInner: true,
            colorNeedleCircleInner: "#dc3545",
            colorNeedleCircleInnerEnd: "#dc3545",
            animationDuration: 400,
            animationRule: "linear",
            value: 0,
            gaugeTitleShow: true,
            gaugeTitle: 'IDD',
            gaugeTitleId: 'iddMeterValue',
            gaugeTitleDefault: '0.0',
            gaugeTitleSuffix: 'A',
            gaugeTitleBg: '#0d6efd',
            gaugeTitleColor: '#ffffff'
        }, options);

        super(canvasId, config);
    }
}

// ------------------------------------------------------------
// VDD METER
// ------------------------------------------------------------

class VDDGauge extends Gauge {
    constructor(canvasId, options = {}) {
        const config = Object.assign({
            renderTo: canvasId,
            minValue: 40,
            maxValue: 55,
            majorTicks: ["40", "42", "44", "46", "48", "50", "52", "54", "55"],
            highlights: [
                { from: 40, to: 45, color: "rgba(255,255,0,.25)" },
                { from: 45, to: 52, color: "rgba(0,255,0,.25)" },
                { from: 52, to: 55, color: "rgba(255,0,0,.25)" }
            ],
            labels: ["40", "42", "44", "46", "48", "50", "52", "54", "55"],
            startAngle: 90,
            ticksAngle: 180,
            valueBox: false,
            minorTicks: 0,
            strokeTicks: false,
            tickSide: "out",
            needleSide: "center",
            colorPlate: "transparent",
            borders: false,
            needleShadow: false,
            colorMajorTicks: "#555555",
            colorMinorTicks: "transparent",
            colorNumbers: "transparent",
            fontNumbersSize: 0,
            colorBarProgress: "#198754",
            colorBarProgressEnd: "#dc3545",
            colorBar: "#dddddd",
            barShadow: 0,
            barWidth: 10,
            barStrokeWidth: 0,
            needleType: "arrow",
            needleWidth: 3,
            needleCircleSize: 7,
            needleCircleOuter: false,
            needleCircleInner: true,
            colorNeedleCircleInner: "#dc3545",
            colorNeedleCircleInnerEnd: "#dc3545",
            animationDuration: 400,
            animationRule: "linear",
            value: 48,
            gaugeTitleShow: true,
            gaugeTitle: 'VDD',
            gaugeTitleId: 'vddMeterValue',
            gaugeTitleDefault: '48.0',
            gaugeTitleSuffix: 'V',
            gaugeTitleBg: '#198754',
            gaugeTitleColor: '#ffffff'
        }, options);

        super(canvasId, config);
    }
}

// Export classes as ES module
export { Gauge, SMeterGauge, PowerGauge, SWRGauge, ALCGauge, TempGauge, CompressionGauge, IDDGauge, VDDGauge };
