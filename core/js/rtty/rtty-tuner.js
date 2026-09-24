// RTTY crossed-ellipse tuning scope.
//
// How RTTY was tuned before anything had a waterfall: the terminal unit's mark
// filter went to the X plates of an oscilloscope and its space filter to the Y
// plates. Each tone then draws a line - mark flat across, space straight up -
// and a signal shifting between them draws a cross:
//
//   on tune          a clean cross, each arm a thin ellipse
//   off tune         the arms lean towards each other and open up, because
//                    each tone now leaks into both filters
//   wrong shift      one arm right, the other a blob
//   nothing there    a fuzzy ball of noise in the middle
//
// Tune for the cross. The filtering is done on the host (RttyTuningScope); this
// file only draws the sweeps that arrive and keeps the last few on screen,
// fading, the way the phosphor did.
//
// Radio-agnostic: it talks to /api/rtty and nothing else. The host page
// provides the dialog and the elements named in the constructor defaults.

import { RTTY_SHIFTS as SHIFTS, DEFAULT_RTTY_SETTINGS, loadRttySettings, saveRttySettings } from './rtty-settings.js';

const POLL_MS    = 50;     // 20 redraws a second
const POINTS     = 500;    // about 21 ms at 24,000 points a second: one sweep
const PERSIST    = 6;      // sweeps kept on screen, oldest dimmest
const QUIET_DB   = -80;    // below this in both filters there is nothing to draw
const CHUNK      = 5;      // points per stroke: half a cycle of 2 kHz at 24,000 points a second

// How much of what the receiver passes lands in the two tone filters, in dB.
// RTTY on tune puts nearly all of it there; noise, or a signal off to one side,
// puts most of it elsewhere.
function filterShare(f) {
    const inFilters = 10 * Math.log10(Math.pow(10, f.markDb / 10) + Math.pow(10, f.spaceDb / 10));
    return inFilters - f.inputDb;
}

export class RttyTuner {
    constructor(ids = {}) {
        this._ids = Object.assign({
            dialog:  'rttyTunerDialog',
            canvas:  'rttyTunerCanvas',
            info:    'rttyTunerInfo',
            status:  'rttyTunerStatus',
            mark:    'rttyTunerMark',
            shift:   'rttyTunerShift',
            reverse: 'rttyTunerReverse',
        }, ids);

        this._timer    = null;
        this._inFlight = false;
        this._sweeps   = [];       // arrays of [x, y, ...] in amplitude units
        this._scale    = 1e-4;     // decaying peak, so the figure fills the face
        this._last     = null;
        this._settings = { ...DEFAULT_RTTY_SETTINGS };
    }

    init() {
        const $ = id => document.getElementById(this._ids[id]);
        this._dialog  = $('dialog');
        this._canvas  = $('canvas');
        this._info    = $('info');
        this._status  = $('status');
        this._markEl  = $('mark');
        this._shiftEl = $('shift');
        this._revEl   = $('reverse');
        if (!this._dialog || !this._canvas) return false;

        this._ctx = this._canvas.getContext('2d');
        this._loadSettings();
        this._showSettings();

        const changed = () => { this._readSettings(); this._saveSettings(); this._send('start'); };
        this._markEl?.addEventListener('change', changed);
        this._shiftEl?.addEventListener('change', changed);
        this._revEl?.addEventListener('change', changed);

        // The audio is held only while the dialog is open. Closing it by any
        // route - the X, Escape, or the page's own code - lets the host go.
        this._dialog.addEventListener('close', () => {
            this._stopPolling();
            this._send('stop');
        });

        if (window.ResizeObserver) {
            new ResizeObserver(() => { this._resize(); this._draw(); }).observe(this._canvas);
        }
        this._resize();
        this._draw();
        return true;
    }

    toggle() {
        if (!this._dialog) return;
        if (this._dialog.open) { this._dialog.close(); return; }
        // Non-modal: the operator tunes the VFO while watching the figure.
        this._dialog.show();
        this._resize();
        this._send('start');
        this._startPolling();
    }

    // ── Settings ────────────────────────────────────────────────────────────

    // Shared with click-to-tune, which reads them for AFSK modes.
    _loadSettings() { this._settings = loadRttySettings(); }

    _saveSettings() { saveRttySettings(this._settings); }

    _showSettings() {
        if (this._markEl)  this._markEl.value    = String(this._settings.markHz);
        if (this._shiftEl) this._shiftEl.value   = String(this._settings.shiftHz);
        if (this._revEl)   this._revEl.checked   = this._settings.reverse;
    }

    _readSettings() {
        const mark  = Number(this._markEl?.value);
        const shift = Number(this._shiftEl?.value);
        if (Number.isFinite(mark) && mark >= 300 && mark <= 3000) this._settings.markHz = Math.round(mark);
        if (SHIFTS.includes(shift)) this._settings.shiftHz = shift;
        this._settings.reverse = !!this._revEl?.checked;
    }

    // ── Host ────────────────────────────────────────────────────────────────

    async _send(what) {
        try {
            const res = await fetch(`/api/rtty/tuner/${what}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: what === 'start' ? JSON.stringify(this._settings) : '{}',
            });
            if (!res.ok) {
                const body = await res.json().catch(() => ({}));
                this._setStatus(body.error || `HTTP ${res.status}`);
                return;
            }
            if (what === 'start') this._sweeps.length = 0;
        } catch {
            this._setStatus('Cannot reach Yaesu Web Control.');
        }
    }

    _startPolling() {
        if (this._timer) return;
        this._timer = setInterval(() => this._poll(), POLL_MS);
    }

    _stopPolling() {
        if (this._timer) { clearInterval(this._timer); this._timer = null; }
        this._sweeps.length = 0;
        this._last = null;
        this._draw();
    }

    async _poll() {
        // A slow reply must not stack requests behind it: skip a frame instead.
        if (this._inFlight) return;
        this._inFlight = true;
        try {
            const res = await fetch(`/api/rtty/tuner?points=${POINTS}`);
            if (!res.ok) return;
            const f = await res.json();
            this._last = f;
            this._adoptServerSettings(f);
            this._push(f);
            this._draw();
        } catch {
            // One dropped frame in twenty. Not worth saying.
        } finally {
            this._inFlight = false;
        }
    }

    // There is one tuner on the server, so another tab or browser changing
    // its settings changes them for this one too. Show what it is really
    // using, or the dialog says 170 while the filters sit at 450. Not while
    // the operator is typing a mark, and not saved: this page's own choice
    // is still what it starts with next time.
    _adoptServerSettings(f) {
        if (!f.running) return;
        const s = this._settings;
        if (document.activeElement === this._markEl) return;
        let changed = false;
        if (SHIFTS.includes(f.shiftHz) && f.shiftHz !== s.shiftHz) { s.shiftHz = f.shiftHz; changed = true; }
        if (typeof f.reverse === 'boolean' && f.reverse !== s.reverse) { s.reverse = f.reverse; changed = true; }
        if (Number.isFinite(f.markHz) && Math.round(f.markHz) !== s.markHz) { s.markHz = Math.round(f.markHz); changed = true; }
        if (changed) { this._showSettings(); this._saveSettings(); }
    }

    _push(f) {
        const pts = f.points || [];
        const peak = f.peak || 0;
        if (!pts.length || peak <= 0) return;

        const k = peak / 1000;
        const sweep = new Float32Array(pts.length);
        for (let i = 0; i < pts.length; i++) sweep[i] = pts[i] * k;
        this._sweeps.push(sweep);
        if (this._sweeps.length > PERSIST) this._sweeps.shift();

        // The face follows the loudest recent sweep and relaxes slowly, so a
        // fade shrinks the figure for a moment rather than the figure jumping
        // to fill the face on every sweep - which would make noise look as
        // confident as a signal.
        this._scale = Math.max(peak, this._scale * 0.97, 1e-6);
    }

    // ── Drawing ─────────────────────────────────────────────────────────────

    _resize() {
        const c = this._canvas;
        if (!c) return;
        const dpr = window.devicePixelRatio || 1;
        const css = Math.max(160, Math.min(c.clientWidth || 280, 480));
        if (this._css === css && c.width === Math.round(css * dpr)) return;
        c.width = c.height = Math.round(css * dpr);
        c.style.height = `${css}px`;
        this._ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        this._css = css;
    }

    _draw() {
        const ctx = this._ctx;
        if (!ctx || !this._css) return;
        const s = this._css, mid = s / 2, rad = s * 0.44;

        ctx.clearRect(0, 0, s, s);
        ctx.fillStyle = '#0b0f0b';
        ctx.fillRect(0, 0, s, s);

        // Graticule, square this time: the old scopes had a ruled grid.
        ctx.strokeStyle = '#1f3a24';
        ctx.lineWidth = 1;
        ctx.beginPath();
        for (let g = -4; g <= 4; g++) {
            const d = rad * g / 4;
            ctx.moveTo(mid - rad, mid + d); ctx.lineTo(mid + rad, mid + d);
            ctx.moveTo(mid + d, mid - rad); ctx.lineTo(mid + d, mid + rad);
        }
        ctx.stroke();
        ctx.strokeStyle = '#2f5a36';
        ctx.beginPath();
        ctx.moveTo(mid - rad, mid); ctx.lineTo(mid + rad, mid);
        ctx.moveTo(mid, mid - rad); ctx.lineTo(mid, mid + rad);
        ctx.stroke();

        ctx.fillStyle = '#3f7a48';
        ctx.font = '10px ui-monospace, Consolas, monospace';
        ctx.textAlign = 'right';
        ctx.fillText('MARK', mid + rad, mid - 4);
        ctx.textAlign = 'left';
        ctx.fillText('SPACE', mid + 4, mid - rad + 10);

        // The face scales to whatever is there, so receiver noise alone
        // fills it as fully as a signal would. Dim it instead, so a ball of
        // noise never looks like something worth tuning.
        const f = this._last;
        const quiet = !f || Math.max(f.markDb ?? -120, f.spaceDb ?? -120) < QUIET_DB
                         || filterShare(f) < -10;
        const k = rad / (this._scale || 1e-6);
        const n = this._sweeps.length;

        // Drawn the way a beam lights phosphor: each step between points adds
        // a little light, so where the trace dwells - along the arms, retraced
        // twice a cycle - it glows, and the quick swings between mark and
        // space stay faint. Drawn at full brightness instead, those swings
        // look as solid as the arms and bury the cross in a tangle.
        ctx.save();
        ctx.globalCompositeOperation = 'lighter';
        ctx.lineWidth = 1.4;
        ctx.lineJoin = 'round';
        for (let j = 0; j < n; j++) {
            const sw = this._sweeps[j];
            const age = (j + 1) / n;                     // newest = 1
            const a = (quiet ? 0.3 : 1) * (0.02 + 0.13 * age * age);
            ctx.strokeStyle = `rgba(60, 255, 100, ${a.toFixed(3)})`;
            // One path is lit once however often it crosses itself, so a
            // sweep is stroked in pieces of about half a tone cycle: short
            // enough not to overlap themselves, so the retraces add up.
            for (let i = 0; i + 2 < sw.length; i += CHUNK * 2) {
                ctx.beginPath();
                ctx.moveTo(mid + sw[i] * k, mid - sw[i + 1] * k);
                const end = Math.min(sw.length, i + CHUNK * 2 + 2);
                for (let p = i + 2; p < end; p += 2) ctx.lineTo(mid + sw[p] * k, mid - sw[p + 1] * k);
                ctx.stroke();
            }
        }
        ctx.restore();

        if (this._info) this._info.textContent = this._caption();
        if (f) this._setStatus(this._statusText(f));
    }

    _caption() {
        const f = this._last;
        if (!f) return 'Waiting.';
        const hz = v => Math.round(v);
        const db = v => (v <= -119 ? '---' : v.toFixed(0));
        return `Mark ${hz(f.markHz)}  Space ${hz(f.spaceHz)} Hz   ` +
               `M ${db(f.markDb)}  S ${db(f.spaceDb)}  in ${db(f.inputDb)} dBFS`;
    }

    _statusText(f) {
        if (!f.running) return 'Stopped.';
        if (f.captureError) return f.captureError;
        if (Math.max(f.markDb, f.spaceDb) < QUIET_DB) return 'No signal - nothing in either filter.';

        const share = filterShare(f);
        const tilt = f.markDb - f.spaceDb;
        const mode = f.mode ? ` (${f.mode})` : '';

        if (share < -10) return `Little of the audio is in the tone filters - tune for the cross${mode}.`;
        if (tilt > 10)  return `Mark only - idling, or the shift or reverse is wrong${mode}.`;
        if (tilt < -10) return `Space only - the shift or reverse is wrong${mode}.`;
        return `Both tones in their filters - fine-tune for the thinnest cross${mode}.`;
    }

    _setStatus(text) {
        if (this._status && this._status.textContent !== text) this._status.textContent = text;
    }
}
