// Yaesu Web Control — the FTdx101's audio scope, from the ACC board.
//
// Draws each receiver's AF-FFT (0 to 4 kHz) above its oscilloscope, inside the
// MULTI group of the Radio Scope controls. The host sends them on
// RadioStateUpdate as property "AccAudio" (AccAudioService) only while a page
// has asked: this subscribes when a MULTI group with the panes is open and on
// screen, and unsubscribes when none is, so the ACC board's port is let go.
//
// One hub connection for the page, shared by every panel on it (the Radio
// Scope card and the Radio Display Controls dialog can both be on one page).
// Panels are found by class, never by id, for the same reason.

const FFT_POINTS = 200;            // 20 Hz a point, 0 to 3,980 Hz
const OSC_COLUMNS = 200;           // max, min pairs
const FFT_FOLLOW = 0.5;            // how much of each new AF-FFT goes into the trace
const FLOOR_FOLLOW = 0.05;         // how fast the floor follows
const TOP_FALL = 0.02;             // how fast the top comes down after a loud signal
const MIN_RANGE = 20;              // the least the trace is stretched over
const STALE_MS = 1500;             // frames older than this are not drawn
const ATT_DB = ['0 dB', '10 dB', '20 dB'];
const OSC_TIMES = ['1 ms', '3 ms', '10 ms', '30 ms', '100 ms', '300 ms'];

/** Bytes from the JSON hub protocol, which sends byte[] as base64. */
export function decodeBytes(value) {
    if (value instanceof Uint8Array) return value;
    if (Array.isArray(value)) return Uint8Array.from(value);
    if (typeof value !== 'string') return new Uint8Array(0);
    const raw = atob(value);
    const out = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
    return out;
}

/** Moves each smoothed point a share of the way to the new AF-FFT. */
export function smooth(smoothed, latest, follow) {
    const n = Math.min(smoothed.length, latest.length);
    for (let i = 0; i < n; i++) smoothed[i] += (latest[i] - smoothed[i]) * follow;
    return smoothed;
}

/**
 * The AF-FFT's floor (the 10th percentile, so a few deep points don't drag it
 * down) and peak, leaving out point 0: DC, and on Main always full scale.
 */
export function fftRange(fft) {
    const sorted = Array.from(fft).slice(1).sort((a, b) => a - b);
    if (!sorted.length) return { low: 0, peak: 255 };
    return { low: sorted[Math.floor(sorted.length / 10)], peak: sorted[sorted.length - 1] };
}

/** The radio's audio scope settings, shared by both receivers. */
export function settingsText(fftAtt, oscAtt, oscTime) {
    const att = a => ATT_DB[a] ?? `step ${a}`;
    const time = OSC_TIMES[oscTime] ?? `t${oscTime}`;
    return `AF-FFT att ${att(fftAtt)} · OSC att ${att(oscAtt)} · ${time} (set on the radio or with the buttons below)`;
}

// ── one receiver's pane ──────────────────────────────────────────────────────

class Pane {
    constructor(figure) {
        this.figure = figure;
        this.receiver = figure.dataset.receiver === 'sub' ? 'sub' : 'main';
        this.canvas = figure.querySelector('canvas');
        this.fft = new Float64Array(FFT_POINTS);
        this.osc = new Uint8Array(OSC_COLUMNS * 2);
        this.has = false;
        this.low = null;
        this.top = null;
    }

    add(data) {
        const fft = decodeBytes(data.fft);
        const osc = decodeBytes(data.osc);
        if (fft.length < FFT_POINTS || osc.length < OSC_COLUMNS * 2) return;
        smooth(this.fft, fft, this.has ? FFT_FOLLOW : 1);
        this.osc.set(osc.subarray(0, OSC_COLUMNS * 2));
        const { low, peak } = fftRange(this.fft);
        this.low = this.low === null ? low : this.low + (low - this.low) * FLOOR_FOLLOW;
        this.top = this.top === null || peak > this.top ? peak : this.top + (peak - this.top) * TOP_FALL;
        this.has = true;
    }

    clear() {
        this.has = false;
        this.low = this.top = null;
        this.fft.fill(0);
    }

    draw() {
        const c = this.canvas;
        if (!c) return;
        const dpr = window.devicePixelRatio || 1;
        const w = Math.max(1, Math.round(c.clientWidth * dpr));
        const h = Math.max(1, Math.round(c.clientHeight * dpr));
        if (c.width !== w || c.height !== h) { c.width = w; c.height = h; }
        const g = c.getContext('2d');
        g.fillStyle = '#000';
        g.fillRect(0, 0, w, h);

        const gap = Math.round(4 * dpr);
        const fftH = Math.round(h * 0.6);
        this._drawFft(g, 0, 0, w, fftH, dpr);
        this._drawOsc(g, 0, fftH + gap, w, h - fftH - gap);
    }

    _drawFft(g, x, y, w, h, dpr) {
        g.strokeStyle = '#222';
        g.lineWidth = 1;
        g.fillStyle = '#8aa';
        g.font = `${Math.round(10 * dpr)}px system-ui, sans-serif`;
        g.textAlign = 'center';
        g.textBaseline = 'top';
        for (let khz = 1; khz < 4; khz++) {
            const px = Math.round(x + (khz * 50 + 0.5) * w / FFT_POINTS) + 0.5;
            g.beginPath(); g.moveTo(px, y); g.lineTo(px, y + h); g.stroke();
            g.fillText(`${khz} kHz`, px, y + 2 * dpr);
        }
        for (let q = 1; q < 4; q++) {
            const py = Math.round(y + h * q / 4) + 0.5;
            g.beginPath(); g.moveTo(x, py); g.lineTo(x + w, py); g.stroke();
        }
        if (!this.has) return;

        const low = Math.floor(this.low ?? 0);
        const range = Math.max((this.top ?? 255) - low, MIN_RANGE) * 1.1;
        const yAt = v => Math.min(y + h - 1, Math.max(y, y + h - 1 - (v - low) * (h - 2) / range));
        g.beginPath();
        g.moveTo(x + 1.5 * w / FFT_POINTS, y + h);
        for (let i = 1; i < FFT_POINTS; i++) g.lineTo(x + (i + 0.5) * w / FFT_POINTS, yAt(this.fft[i]));
        g.lineTo(x + (FFT_POINTS - 0.5) * w / FFT_POINTS, y + h);
        g.closePath();
        g.fillStyle = 'rgba(79, 195, 247, 0.19)';
        g.fill();
        g.beginPath();
        for (let i = 1; i < FFT_POINTS; i++) {
            const px = x + (i + 0.5) * w / FFT_POINTS, py = yAt(this.fft[i]);
            if (i === 1) g.moveTo(px, py); else g.lineTo(px, py);
        }
        g.strokeStyle = '#4fc3f7';
        g.lineWidth = Math.max(1, dpr);
        g.stroke();
    }

    _drawOsc(g, x, y, w, h) {
        if (h < 2) return;
        g.strokeStyle = '#333';
        g.lineWidth = 1;
        g.strokeRect(x + 0.5, y + 0.5, w - 1, h - 1);
        const mid = Math.round(y + h / 2) + 0.5;
        g.strokeStyle = '#222';
        g.beginPath(); g.moveTo(x, mid); g.lineTo(x + w, mid); g.stroke();
        if (!this.has) return;

        // Each column is the highest and lowest sample in its time, 128 the middle.
        const yAt = v => y + h - 1 - v * (h - 1) / 255;
        const cw = w / OSC_COLUMNS;
        g.fillStyle = '#66bb6a';
        for (let col = 0; col < OSC_COLUMNS; col++) {
            const top = yAt(this.osc[2 * col]), bottom = yAt(this.osc[2 * col + 1]);
            g.fillRect(x + col * cw, top, Math.max(cw, 1), Math.max(bottom - top, 1));
        }
    }
}

// ── one panel: both panes and a status line ──────────────────────────────────

class Panel {
    constructor(root) {
        this.root = root;
        this.details = root.closest('details');
        this.status = root.querySelector('.acc-audio-status');
        this.panes = Array.from(root.querySelectorAll('.acc-audio-pane')).map(f => new Pane(f));
        this.visible = false;
        this.lastAt = 0;
        this.message = '';

        if (this.details) this.details.addEventListener('toggle', () => update());
        if ('IntersectionObserver' in window) {
            new IntersectionObserver(entries => {
                this.visible = entries.some(e => e.isIntersecting);
                update();
            }).observe(root);
        } else {
            this.visible = true;
        }
    }

    get wanted() {
        return this.visible && (!this.details || this.details.open);
    }

    receive(value) {
        if (!value) return;
        if (value.problem) {
            this.setStatus(value.problem);
            this.panes.forEach(p => p.clear());
            this.draw();
            return;
        }
        this.panes.forEach(p => { const d = value[p.receiver]; if (d) p.add(d); });
        this.lastAt = Date.now();
        this.setStatus(settingsText(value.fftAtt, value.oscAtt, value.oscTime));
    }

    setStatus(text) {
        if (text === this.message || !this.status) return;
        this.message = text;
        this.status.textContent = text;
    }

    draw() {
        if (this.lastAt && Date.now() - this.lastAt > STALE_MS) {
            this.panes.forEach(p => p.clear());
            this.lastAt = 0;
        }
        this.panes.forEach(p => p.draw());
    }
}

// ── the page's subscription ──────────────────────────────────────────────────

const panels = [];
let connection = null;
let started = null;
let subscribed = false;
let changing = Promise.resolve();
let drawPending = false;

function ensureConnection() {
    if (connection) return started;
    if (typeof window.rwcHubConnection !== 'function') return Promise.reject(new Error('no hub'));
    connection = window.rwcHubConnection('/radioHub');
    connection.on('RadioStateUpdate', update => {
        if (!update || update.property !== 'AccAudio') return;
        panels.forEach(p => { if (p.wanted) p.receive(update.value); });
        scheduleDraw();
    });
    // Group membership dies with the connection, so ask again after a reconnect.
    connection.onreconnected(() => { subscribed = false; update(); });
    started = connection.start();
    return started;
}

function scheduleDraw() {
    if (drawPending) return;
    drawPending = true;
    requestAnimationFrame(() => {
        drawPending = false;
        panels.forEach(p => { if (p.wanted) p.draw(); });
    });
}

function update() {
    const want = panels.some(p => p.wanted);
    if (want === subscribed) return;
    subscribed = want;
    changing = changing.then(async () => {
        try {
            await ensureConnection();
            if (want) {
                const reason = await connection.invoke('SubscribeAccAudio');
                if (reason) panels.forEach(p => p.setStatus(reason));
                else panels.forEach(p => { if (!p.lastAt) p.setStatus('Waiting for the ACC board...'); });
            } else {
                await connection.invoke('UnsubscribeAccAudio');
            }
        } catch {
            panels.forEach(p => p.setStatus('Could not reach YWC for the audio scope.'));
            subscribed = false;
        }
    });
}

/** Wires every audio-scope panel inside root. Safe to call more than once. */
export function attachAccAudio(root) {
    if (!root) return;
    root.querySelectorAll('.acc-audio').forEach(el => {
        if (el.dataset.accAudioWired === '1') return;
        el.dataset.accAudioWired = '1';
        panels.push(new Panel(el));
    });
    update();
}
