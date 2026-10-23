// panel-math.js - the arithmetic behind the radio-style front panel.
//
// Pure functions only: no DOM, no radio, no module state. Everything that
// draws (radio-panel.js, spectrum-view.js) calls into here, so the parts where
// a wrong answer is silent rather than visible - where the needle sits, how a
// frequency splits into big and small digits - are pinned by
// tests/js/radio-panel-math.test.mjs.

// Frequency in Hz -> the two halves the VFO display draws: big "MHz.kHz" and
// small ".Hz". 7020949 -> { big: '7.020', small: '.949' }.
export function formatFrequency(hz) {
    const f = Math.max(0, Math.round(Number(hz) || 0));
    const pad = n => String(n).padStart(3, '0');
    return {
        big: Math.floor(f / 1e6) + '.' + pad(Math.floor(f / 1000) % 1000),
        small: '.' + pad(f % 1000),
    };
}

// The dial is divided into 15 equal steps: S0..S9 take the first nine, and
// each 10 dB over S9 takes one more, so S9 sits at 9/15 = 0.6 (where the red
// arc starts) and S9+60 at full scale.
//
// Input is the calibrated S-meter scale both apps' calibration tables use:
// 0..9 are S-units, 10..60 are dB over S9. Between 9 and 10 the table is
// interpolating from S9 towards +10 dB, so that stretch is read as 0..10 dB.
export function sMeterFraction(sValue) {
    const v = Number(sValue);
    if (!Number.isFinite(v) || v <= 0) return 0;
    let steps;
    if (v <= 9) steps = v;
    else if (v < 10) steps = v;                 // 9 + (v - 9): S9 .. S9+10
    else steps = 9 + v / 10;
    return Math.min(1, steps / 15);
}

// Text for the same reading: "S5", "S9+20 dB".
export function sMeterText(sValue) {
    const v = Number(sValue);
    if (!Number.isFinite(v) || v <= 0) return 'S0';
    if (v <= 9) return 'S' + Math.round(v);
    const db = v < 10 ? (v - 9) * 10 : v;
    return 'S9+' + Math.round(db) + ' dB';
}

// Lower-scale legends for the TX meter chosen under the S-meter.
export const LOWER_SCALES = {
    PO:   ['0', '50', '100', '150', '200'],
    SWR:  ['1', '1.5', '2', '3', '∞'],
    ALC:  ['0', '', '', '', 'MAX'],
    COMP: ['0', '5', '10', '15', '20'],
    IDD:  ['0', '10', '20', '30', '40'],
    TEMP: ['0', '25', '50', '75', '100'],
};

// Legends for a meter whose dial is a range rather than from zero (a PA
// supply, 40-55 V or 10-16 V): the ends and the middle, blanks between.
export function rangeLegends(min, max) {
    const f = x => String(Math.round(x * 10) / 10);
    return [f(min), '', f((min + max) / 2), '', f(max)];
}

// SVG geometry for the analogue meter face (viewBox 0 0 250 145). `frac` is
// the needle position 0..1; `lower` is a five-entry legend list.
export function meterGeometry(frac, lower = LOWER_SCALES.PO) {
    const f = Math.max(0, Math.min(1, Number(frac) || 0));
    const pt = (a, r) => { const t = a * Math.PI / 180; return [125 + r * Math.sin(t), 175 - r * Math.cos(t)]; };
    const ang = u => -48 + 96 * u;
    const P = (a, r) => pt(a, r).map(n => n.toFixed(1)).join(' ');
    const lab = (u, r) => { const p = pt(ang(u), r); return { x: +p[0].toFixed(1), y: +(p[1] + 4).toFixed(1) }; };

    let ticks = '';
    for (let i = 0; i <= 15; i++) {
        const major = i % 2 === 1;
        ticks += ' M' + P(ang(i / 15), 140) + ' L' + P(ang(i / 15), major ? 152 : 147);
    }
    let inTicks = '';
    for (let i = 0; i <= 8; i++) inTicks += ' M' + P(ang(i / 8), 100) + ' L' + P(ang(i / 8), i % 2 ? 104 : 108);

    const upper = [
        ['1', 1], ['3', 3], ['5', 5], ['7', 7], ['9', 9],
    ].map(([t, i]) => ({ text: t, red: false, ...lab(i / 15, 127) }))
     .concat([['+20', 11], ['+40', 13], ['+60', 15]].map(([t, i]) => ({ text: t, red: true, ...lab(i / 15, 127) })));
    const lowerLabels = [0, 0.25, 0.5, 0.75, 1].map((u, i) => ({ text: lower[i] ?? '', ...lab(u, 88) }));

    const a = ang(f), n1 = pt(a, 30), n2 = pt(a, 154);
    return {
        arc: 'M' + P(-48, 140) + ' A140 140 0 0 1 ' + P(48, 140),
        red: 'M' + P(ang(0.6), 140) + ' A140 140 0 0 1 ' + P(48, 140),
        inArc: 'M' + P(-48, 100) + ' A100 100 0 0 1 ' + P(48, 100),
        ticks: ticks.trim(),
        inTicks: inTicks.trim(),
        upper,
        lower: lowerLabels,
        needle: { x1: +n1[0].toFixed(1), y1: +n1[1].toFixed(1), x2: +n2[0].toFixed(1), y2: +n2[1].toFixed(1) },
    };
}

// Where the receive passband sits relative to the dial, in Hz. Upper-sideband
// modes listen above the dial, lower-sideband below, the rest either side.
// Data modes come by the radio's name (DATA-U) or the one the board shows
// (USB-D); both are upper sideband. The 100 Hz SSB offset is the conventional low edge of a voice filter; it is
// drawing, not a measurement of any particular radio.
export function passbandHz(mode, widthHz) {
    const w = Math.max(0, Number(widthHz) || 0);
    const m = String(mode || '').toUpperCase();
    if (!w) return null;
    if (m === 'USB' || m === 'DATA-U' || m === 'USB-D' || m === 'PSK' || m === 'DATA-USB') return { lo: 100, hi: 100 + w };
    if (m === 'LSB' || m === 'DATA-L' || m === 'LSB-D' || m === 'DATA-LSB') return { lo: -100 - w, hi: -100 };
    return { lo: -w / 2, hi: w / 2 };
}

// The same passband as percentages of a span centred on the dial, clipped to
// the screen. Returns null when there is nothing to draw.
export function passbandPercent(mode, widthHz, spanHz) {
    const pb = passbandHz(mode, widthHz);
    const span = Number(spanHz) || 0;
    if (!pb || span <= 0) return null;
    let left = 50 + pb.lo / span * 100;
    let right = 50 + pb.hi / span * 100;
    left = Math.max(0, Math.min(100, left));
    right = Math.max(0, Math.min(100, right));
    if (right - left <= 0) return null;
    return { left, width: right - left };
}

// The frequency under a point a fraction (0 left, 1 right) of the way across
// a span centred on the dial, or a DX spot's own frequency when one is within
// snapHz of it - what a click there tunes to. Null until there is a span and
// a dial.
export function spectrumHzAt(frac, centreHz, spanHz, spots = [], snapHz = 0) {
    const c = Number(centreHz) || 0, s = Number(spanHz) || 0;
    if (!(c > 0 && s > 0)) return null;
    const f = Math.max(0, Math.min(1, Number(frac) || 0));
    let hz = Math.round(c + (f - 0.5) * s), spot = null, best = snapHz;
    for (const sp of spots ?? []) {
        const gap = Math.abs(sp.hz - hz);
        if (gap <= best) { best = gap; spot = sp; }
    }
    if (spot) hz = spot.hz;
    return { hz, frac: f, spot };
}

// Axis labels for a span centred on the dial, in kHz: five evenly spaced.
export function axisLabels(centreHz, spanHz, count = 5) {
    const c = Number(centreHz) || 0, s = Number(spanHz) || 0;
    const lo = c - s / 2;
    const decimals = s <= 20_000 ? 2 : 1;
    const out = [];
    for (let i = 0; i < count; i++) out.push(((lo + (i / (count - 1)) * s) / 1000).toFixed(decimals));
    return out;
}

// Span as the screen shows it: "50 kHz", "2 MHz", "500 Hz". A frame's span
// is whole bins, so a 1 kHz zoom arrives as 999 Hz and 20 kHz as 19989 Hz;
// anything within 2% of a standard span is shown as that span.
const STANDARD_SPANS = [500, 1e3, 2e3, 5e3, 10e3, 20e3, 50e3, 100e3, 200e3, 250e3, 500e3, 1e6, 2e6];
export function spanText(spanHz) {
    let s = Number(spanHz) || 0;
    const near = STANDARD_SPANS.find(x => Math.abs(s - x) <= x * 0.02);
    if (near) s = near;
    else if (s >= 1000) s = Math.round(s / 100) * 100;
    if (s >= 1e6) return (s / 1e6).toString() + ' MHz';
    if (s >= 1000) return (s / 1000).toString() + ' kHz';
    return s + ' Hz';
}

// Filter width as text: 2400 -> "2.4 kHz", 250 -> "250 Hz".
export function widthText(hz) {
    const w = Number(hz);
    if (!Number.isFinite(w) || w <= 0) return '';
    if (w >= 1000) return (w / 1000).toFixed(w % 1000 === 0 ? 1 : (w % 100 === 0 ? 1 : 2)) + ' kHz';
    return w + ' Hz';
}

// Knob pointer angle in degrees for a 0..1 setting: a 270 degree sweep.
export function knobAngle(frac) {
    const f = Math.max(0, Math.min(1, Number(frac) || 0));
    return -135 + 270 * f;
}

// Waterfall colour for a 0..1 level, as [r, g, b], interpolated along `stops`.
// The default runs dark blue through cyan to yellow, the stops mock-up A was
// drawn with; a theme passes its own.
export const WATERFALL_STOPS = [[4, 16, 28], [10, 58, 107], [29, 143, 209], [127, 227, 255], [255, 224, 102]];
export function waterfallColour(t, stops = WATERFALL_STOPS) {
    const s = stops?.length >= 2 ? stops : WATERFALL_STOPS;
    const x = Math.max(0, Math.min(1, Number(t) || 0));
    const p = x * (s.length - 1);
    const i = Math.min(s.length - 2, Math.floor(p));
    const f = p - i;
    return s[i].map((v, k) => Math.round(v + (s[i + 1][k] - v) * f));
}

// Reduce `bins` (low to high frequency) to `columns` values, keeping the
// strongest bin in each column so a one-bin CW carrier is not averaged away.
// With fewer bins than columns (a narrow software-zoom span sends ~130 bins)
// it draws straight lines between bin centres instead of repeating each bin,
// which would show as flat steps several pixels wide.
export function binsToColumns(bins, columns) {
    const n = bins?.length ?? 0;
    const cols = Math.max(1, Math.floor(columns));
    const out = new Float32Array(cols);
    if (!n) return out;
    if (n < cols) {
        for (let c = 0; c < cols; c++) {
            const x = Math.max(0, Math.min(n - 1, (c + 0.5) * n / cols - 0.5));
            const i = Math.min(n - 2, Math.floor(x));
            out[c] = n === 1 ? bins[0] : bins[i] + (bins[i + 1] - bins[i]) * (x - i);
        }
        return out;
    }
    for (let c = 0; c < cols; c++) {
        const a = Math.floor(c * n / cols);
        const b = Math.max(a + 1, Math.floor((c + 1) * n / cols));
        let m = -Infinity;
        for (let i = a; i < b && i < n; i++) if (bins[i] > m) m = bins[i];
        out[c] = m;
    }
    return out;
}

// Noise floor estimate: the given percentile of the bins.
export function percentile(values, p) {
    const n = values?.length ?? 0;
    if (!n) return 0;
    const sorted = Array.prototype.slice.call(values).sort((a, b) => a - b);
    const idx = Math.max(0, Math.min(n - 1, Math.floor(p * (n - 1))));
    return sorted[idx];
}

// Re-centre a frame on the dial. `offsetHz` is how far the true RF of the
// frame's centre bin is above the dial (the app knows this - on a Yaesu it is
// the IF OUT slide); positive moves the picture right. Bins shifted in from
// off the edge take the frame's lowest value, so they read as floor.
export function alignBins(bins, offsetHz, spanHz) {
    const n = bins?.length ?? 0;
    const span = Number(spanHz) || 0;
    const off = Number(offsetHz) || 0;
    if (!n || !span || !off) return bins;
    const d = Math.round(off * n / span);
    if (!d) return bins;
    let floor = Infinity;
    for (let i = 0; i < n; i++) if (bins[i] < floor) floor = bins[i];
    const out = new Float32Array(n);
    for (let i = 0; i < n; i++) {
        const k = i - d;
        out[i] = k >= 0 && k < n ? bins[k] : floor;
    }
    return out;
}

// Needle positions for the TX meters, matching LOWER_SCALES' five legends at
// 0, 1/4, 1/2, 3/4 and full scale. VDD takes its dial as `range`, matching
// rangeLegends(range.min, range.max).
export function txMeterFraction(meter, value, range) {
    const v = Number(value);
    if (!Number.isFinite(v) || v <= 0) return 0;
    const clamp = x => Math.max(0, Math.min(1, x));
    switch (meter) {
        case 'TEMP': return clamp(v / 100);          // degrees C
        case 'VDD':  return range && range.max > range.min ? clamp((v - range.min) / (range.max - range.min)) : 0;
        case 'PO':   return clamp(v / 200);          // watts
        case 'COMP': return clamp(v / 20);           // dB
        case 'IDD':  return clamp(v / 40);           // amps
        case 'ALC':  return clamp(v / 255);          // raw
        case 'SWR': {                                // ratio: 1, 1.5, 2, 3, inf
            if (v <= 1) return 0;
            if (v <= 1.5) return (v - 1) / 0.5 * 0.25;
            if (v <= 2) return 0.25 + (v - 1.5) / 0.5 * 0.25;
            if (v <= 3) return 0.5 + (v - 2) * 0.25;
            return clamp(0.75 + 0.25 * (1 - 1 / (v - 2)));
        }
        default: return 0;
    }
}
