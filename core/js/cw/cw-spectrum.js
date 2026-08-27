// CW passband spectrum with a pitch marker.
//
// The phasor tells you whether you are on the tone. It cannot tell you where
// the tone is, because it only sees the one frequency the reader is already
// looking at - so when the reader goes quiet the operator is left unable to
// say whether the band is empty or the dial is simply off. That is the gap
// this fills.
//
// What is drawn is the slice of the audio passband the reader searches, plus a
// margin, with a fixed marker at the pitch the operator asked for. The tuning
// error is then the horizontal gap between the marker and the peak, read
// straight off the screen with no number to interpret and no beat note to
// hear. A second marker shows where the reader believes the tone is, so when
// the two disagree with an obvious peak on screen you can see that the reader
// has locked onto the wrong thing rather than guess at it.
//
// The axis deliberately does not move. Core fixes the span at construction
// from the configured pitch and search window rather than following the
// tracked tone, because an axis that slides while you are turning the dial is
// worse than no display at all.
//
// Levels are dB above the median of the span, not absolute: nothing upstream
// has a calibrated scale, so an absolute figure would be a number with no
// units. Only the shape carries information.
//
// Radio-agnostic: it talks to /api/cw/spectrum and nothing else.

const POLL_MS  = 120;   // the FFT only advances every 64 ms; faster is redraw for nothing
const FLOOR_DB = -6;    // bottom of the scale
const TOP_DB   = 30;    // headroom above the median; a strong CW carrier runs 20-30
const PEAK_FALL = 0.6;  // dB per frame the held peak trace sinks

export class CwSpectrum {
    constructor() {
        this._canvas = null;
        this._ctx    = null;
        this._timer  = null;
        this._last   = null;
        this._peak   = null;    // held maxima, so a keyed signal does not flicker
        this._info   = null;
        this._css    = { w: 320, h: 90 };
    }

    attach(canvasId, infoId) {
        this._canvas = document.getElementById(canvasId);
        if (!this._canvas) return false;
        this._ctx = this._canvas.getContext('2d');
        this._info = infoId ? document.getElementById(infoId) : null;
        this._resize();
        this._draw();
        return true;
    }

    start() {
        if (this._timer) return;
        this._timer = setInterval(() => this._poll(), POLL_MS);
        this._poll();
    }

    stop() {
        if (this._timer) { clearInterval(this._timer); this._timer = null; }
        this._last = null;
        this._peak = null;
        this._draw();
    }

    get running() { return this._timer !== null; }

    async _poll() {
        try {
            const res = await fetch('/api/cw/spectrum');
            if (!res.ok) return;
            const f = await res.json();
            this._last = f;

            // CW is on and off by nature, so the live trace spends much of its
            // time showing the gaps between elements. A slowly falling peak
            // hold is what makes a keyed signal read as a steady line rather
            // than as a flicker, and it is how every band scope solves the
            // same problem.
            const db = f.db || [];
            if (!this._peak || this._peak.length !== db.length) {
                this._peak = db.slice();
            } else {
                for (let i = 0; i < db.length; i++) {
                    this._peak[i] = db[i] > this._peak[i]
                        ? db[i]
                        : this._peak[i] - PEAK_FALL;
                }
            }
            this._draw();
        } catch {
            // A dropped poll costs one frame. Not worth saying.
        }
    }

    _resize() {
        const c = this._canvas;
        const dpr = window.devicePixelRatio || 1;
        const w = c.clientWidth || 320;
        const h = c.clientHeight || 90;
        c.width  = Math.round(w * dpr);
        c.height = Math.round(h * dpr);
        this._ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        this._css = { w, h };
    }

    // Frequency of bin i, and the x it lands on.
    _hzOf(f, i)  { return f.firstHz + i * f.binHz; }
    _xOf(f, hz)  {
        const n = (f.db || []).length;
        if (!n || !f.binHz) return 0;
        const lo = f.firstHz, hi = f.firstHz + (n - 1) * f.binHz;
        return ((hz - lo) / (hi - lo)) * this._css.w;
    }
    _yOf(db) {
        const t = (db - FLOOR_DB) / (TOP_DB - FLOOR_DB);
        return this._css.h - Math.max(0, Math.min(1, t)) * this._css.h;
    }

    _draw() {
        const ctx = this._ctx;
        if (!ctx) return;
        const { w, h } = this._css;

        ctx.clearRect(0, 0, w, h);
        ctx.fillStyle = '#0b0f0b';
        ctx.fillRect(0, 0, w, h);

        const f = this._last;
        const db = f && f.db ? f.db : [];
        if (!db.length || !f.binHz) {
            this._caption();
            return;
        }

        // Frequency graticule every 100 Hz, labelled where there is room.
        const lo = f.firstHz, hi = f.firstHz + (db.length - 1) * f.binHz;
        ctx.strokeStyle = '#1f3a24';
        ctx.fillStyle   = '#3f6a48';
        ctx.font = '9px system-ui, sans-serif';
        ctx.lineWidth = 1;
        for (let hz = Math.ceil(lo / 100) * 100; hz <= hi; hz += 100) {
            const x = this._xOf(f, hz);
            ctx.beginPath();
            ctx.moveTo(x, 0); ctx.lineTo(x, h);
            ctx.stroke();
            ctx.fillText(String(hz), x + 2, h - 2);
        }

        // The held peak, filled, then the live trace over it. Two traces
        // because the peak says "there is a signal here" and the live one
        // says "it is keyed down right now".
        if (this._peak) {
            ctx.beginPath();
            ctx.moveTo(0, h);
            for (let i = 0; i < this._peak.length; i++)
                ctx.lineTo(this._xOf(f, this._hzOf(f, i)), this._yOf(this._peak[i]));
            ctx.lineTo(w, h);
            ctx.closePath();
            ctx.fillStyle = 'rgba(60, 190, 100, 0.28)';
            ctx.fill();
        }

        ctx.beginPath();
        for (let i = 0; i < db.length; i++) {
            const x = this._xOf(f, this._hzOf(f, i)), y = this._yOf(db[i]);
            i ? ctx.lineTo(x, y) : ctx.moveTo(x, y);
        }
        ctx.strokeStyle = '#5cff8c';
        ctx.lineWidth = 1.2;
        ctx.stroke();

        // The pitch marker: where the operator asked to listen. Fixed, and the
        // whole point of the display - the gap between this and the peak is
        // how far the dial is out.
        const px = this._xOf(f, f.pitchHz);
        ctx.strokeStyle = '#ffd24a';
        ctx.lineWidth = 1.5;
        ctx.setLineDash([4, 3]);
        ctx.beginPath();
        ctx.moveTo(px, 0); ctx.lineTo(px, h);
        ctx.stroke();
        ctx.setLineDash([]);

        // Where the reader thinks the tone is. Only drawn when it is worth
        // believing - an unlocked search wanders, and a marker that wanders
        // invites the operator to chase it.
        if (f.signalPresent && f.confidence >= 0.5 && f.toneHz > 0) {
            const tx = this._xOf(f, f.toneHz);
            ctx.strokeStyle = '#7fd4ff';
            ctx.lineWidth = 1.5;
            ctx.beginPath();
            ctx.moveTo(tx, 0); ctx.lineTo(tx, 10);
            ctx.moveTo(tx, h); ctx.lineTo(tx, h - 10);
            ctx.stroke();
        }

        this._caption();
    }

    _caption() {
        if (!this._info) return;
        const f = this._last;
        if (!f || !f.db || !f.db.length) { this._info.textContent = 'Waiting.'; return; }
        if (!f.signalPresent) {
            this._info.textContent =
                `No signal. Marker at ${Math.round(f.pitchHz)} Hz - a peak away from it means the dial is off.`;
            return;
        }
        const err = Math.round(f.toneHz - f.pitchHz);
        this._info.textContent = Math.abs(err) <= 2
            ? `On pitch (${Math.round(f.pitchHz)} Hz).`
            : `Tone ${Math.abs(err)} Hz ${err > 0 ? 'above' : 'below'} the marker.`;
    }
}

export const cwSpectrum = new CwSpectrum();
