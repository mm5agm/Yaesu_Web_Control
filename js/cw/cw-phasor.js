// CW phasor tuning aid.
//
// The idea is forty years old and borrowed from RTTY: put the audio on an X-Y
// display and tune until the figure stops moving. On RTTY you watched two tones
// make crossed ellipses. CW has one tone, so what goes on the screen instead is
// the audio resolved against the pitch you have asked the radio for:
//
//   on frequency          the dot sits still at one angle
//   a little high         it walks anticlockwise, one turn per Hz per second
//   a little low          it walks clockwise
//   a long way off        it smears into a ring
//
// So the operator tunes for "stopped", exactly as before, and does not have to
// read a number or hear a beat note. The arithmetic is all server side in
// CwToneDetector - this file only draws what arrives.
//
// Radio-agnostic: it talks to /api/cw/phasor and nothing else.

const POLL_MS   = 60;     // ~16 Hz; the ring holds two seconds, so this is ample
const TRAIL     = 220;    // points kept on screen, about one second
const MIN_R     = 1e-7;   // below this the pen is up and nothing is drawn

export class CwPhasor {
    constructor() {
        this._canvas = null;
        this._ctx    = null;
        this._timer  = null;
        this._cursor = 0;
        this._trail  = [];      // {x, y, key}
        this._scale  = 1e-3;    // decaying peak radius, so the figure fills the box
        this._info   = null;
        this._last   = null;
    }

    attach(canvasId, infoId) {
        this._canvas = document.getElementById(canvasId);
        if (!this._canvas) return false;
        this._ctx  = this._canvas.getContext('2d');
        this._info = infoId ? document.getElementById(infoId) : null;
        this._resize();
        this._draw();
        return true;
    }

    start() {
        if (this._timer) return;
        // Start from whatever is in the ring now rather than replaying the
        // last two seconds, which would draw a burst of stale audio.
        this._cursor = -1;
        this._timer  = setInterval(() => this._poll(), POLL_MS);
    }

    stop() {
        if (this._timer) { clearInterval(this._timer); this._timer = null; }
        this._trail.length = 0;
        this._draw();
    }

    get running() { return this._timer !== null; }

    async _poll() {
        try {
            const since = this._cursor < 0 ? 0 : this._cursor;
            const res = await fetch(`/api/cw/phasor?since=${since}`);
            if (!res.ok) return;
            const f = await res.json();

            const first = this._cursor < 0;
            this._cursor = f.cursor;
            this._last   = f;

            // The very first reply only establishes the cursor. Drawing it
            // would paint a second of history in one frame.
            if (!first) this._push(f);
            this._draw();
        } catch {
            // A dropped poll costs one frame out of sixteen. Not worth saying.
        }
    }

    _push(f) {
        const pts = f.points || [];
        const key = f.keyDown || [];
        for (let k = 0; k < pts.length; k += 2) {
            const x = pts[k], y = pts[k + 1];
            const r = Math.hypot(x, y);
            if (r > this._scale) this._scale = r;
            this._trail.push({ x, y, key: !!key[k / 2] });
        }
        if (this._trail.length > TRAIL) this._trail.splice(0, this._trail.length - TRAIL);

        // Let the scale fall back slowly, so the figure grows to fill the box
        // after a fade instead of shrinking away in the corner for ever.
        this._scale *= 0.995;
        if (this._scale < 1e-9) this._scale = 1e-9;
    }

    _resize() {
        const c = this._canvas;
        const dpr = window.devicePixelRatio || 1;
        const css = Math.min(c.clientWidth || 220, 320);
        c.width = c.height = Math.round(css * dpr);
        c.style.height = `${css}px`;
        this._ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        this._css = css;
    }

    _draw() {
        const ctx = this._ctx;
        if (!ctx) return;
        const s = this._css, mid = s / 2, rad = s * 0.44;

        ctx.clearRect(0, 0, s, s);
        ctx.fillStyle = '#0b0f0b';
        ctx.fillRect(0, 0, s, s);

        // Graticule: a scope face, because that is what this is pretending to be.
        ctx.strokeStyle = '#1f3a24';
        ctx.lineWidth = 1;
        for (const f of [0.33, 0.66, 1.0]) {
            ctx.beginPath();
            ctx.arc(mid, mid, rad * f, 0, Math.PI * 2);
            ctx.stroke();
        }
        ctx.beginPath();
        ctx.moveTo(mid - rad, mid); ctx.lineTo(mid + rad, mid);
        ctx.moveTo(mid, mid - rad); ctx.lineTo(mid, mid + rad);
        ctx.stroke();

        // The trail, oldest dimmest. Phosphor persistence, in other words.
        const n = this._trail.length;
        const k = rad / (this._scale || 1e-9);
        let penDown = false;
        ctx.lineWidth = 1.6;
        ctx.lineCap = 'round';

        for (let i = 0; i < n; i++) {
            const p = this._trail[i];
            const r = Math.hypot(p.x, p.y);

            // Key-up collapses the figure to the middle. Lifting the pen there
            // keeps the gaps between characters from drawing spokes through
            // the origin and filling the screen with a star.
            if (!p.key || r < MIN_R) { penDown = false; continue; }

            const x = mid + p.x * k;
            const y = mid - p.y * k;
            const age = i / n;

            if (!penDown) { ctx.beginPath(); ctx.moveTo(x, y); penDown = true; continue; }
            ctx.strokeStyle = `rgba(80, 255, 120, ${0.10 + 0.75 * age})`;
            ctx.lineTo(x, y);
            ctx.stroke();
            ctx.beginPath();
            ctx.moveTo(x, y);
        }

        // The head of the trail, bright, so the eye can follow the rotation.
        for (let i = n - 1; i >= 0; i--) {
            const p = this._trail[i];
            if (!p.key || Math.hypot(p.x, p.y) < MIN_R) continue;
            ctx.fillStyle = '#c9ffd6';
            ctx.beginPath();
            ctx.arc(mid + p.x * k, mid - p.y * k, 2.6, 0, Math.PI * 2);
            ctx.fill();
            break;
        }

        this._info && (this._info.textContent = this._caption());
    }

    _caption() {
        const f = this._last;
        if (!f) return 'Waiting.';
        if (!f.signalPresent) return 'No signal - nothing to tune.';

        const err = Math.round(f.toneHz - f.pitchHz);
        if (Math.abs(err) <= 2) return `On pitch (${Math.round(f.pitchHz)} Hz). Figure holds still.`;
        return `${Math.abs(err)} Hz ${err > 0 ? 'high' : 'low'} - ` +
               `turning ${err > 0 ? 'anticlockwise' : 'clockwise'}, ` +
               `${Math.abs(err)} turns a second.`;
    }
}

export const cwPhasor = new CwPhasor();
