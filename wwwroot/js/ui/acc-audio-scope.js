// Yaesu Web Control — the FTdx101's audio scope, from the ACC board.
//
// Draws each receiver's AF-FFT (0 to 4 kHz) above an audio waterfall made from
// it, or above the radio's oscilloscope when the Oscilloscope box is ticked,
// inside the MULTI group of the Radio Scope controls. The host sends them on
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
// The audio waterfall. Frames come about 12.5 a second, so 2 a row puts about
// 16 seconds in 100 rows: a whole FT8 cycle. Point 0 (DC) is left out.
export const FRAMES_PER_ROW = 2;
export const FALL_POINTS = FFT_POINTS - 1;
const FALL_ROWS = 600;             // more than the tallest pane, in device pixels
const FALL_FOLLOW = 0.15;          // how fast the colours follow the levels
const FFT_SHARE_FALL = 0.45;       // the AF-FFT's share of the height over the waterfall
const FFT_SHARE_OSC = 0.6;         // and over the oscilloscope
const OSC_KEY = 'ywc.accAudioOsc';
// Black, blue, cyan, yellow, red, white: weak to strong, as in the ACC viewer.
const FALL_STOPS = [[0, 0x000000], [60, 0x00008C], [120, 0x00AADC], [170, 0xF0E600], [215, 0xE61E00], [255, 0xFFFFFF]];
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

/** One waterfall row: the sums of its frames, averaged and rounded, 0 to 255. */
export function averageRow(sum, frames) {
    const row = new Uint8Array(sum.length);
    const n = Math.max(1, frames);
    for (let i = 0; i < sum.length; i++) row[i] = Math.max(0, Math.min(255, Math.floor((sum[i] + n / 2) / n)));
    return row;
}

/**
 * Where a row's colours start and end: black at its median, about the noise
 * floor, so weak signals such as FT8 show against it, and white at its peak.
 */
export function rowStretch(row) {
    const sorted = Array.from(row).sort((a, b) => a - b);
    if (!sorted.length) return { low: 0, high: 255 };
    return { low: sorted[Math.floor(sorted.length / 2)], high: sorted[sorted.length - 1] };
}

/** 256 colours, [r, g, b] each, for the waterfall. */
export function fallPalette() {
    const p = new Uint8ClampedArray(256 * 3);
    for (let v = 0; v < 256; v++) {
        let s = 0;
        while (s < FALL_STOPS.length - 2 && v > FALL_STOPS[s + 1][0]) s++;
        const [a, ca] = FALL_STOPS[s], [b, cb] = FALL_STOPS[s + 1];
        const t = (v - a) / (b - a);
        for (let k = 0; k < 3; k++) {
            const shift = 16 - 8 * k, from = (ca >> shift) & 255, to = (cb >> shift) & 255;
            p[v * 3 + k] = Math.round(from + (to - from) * t);
        }
    }
    return p;
}

/** The AF-FFT's share of the pane's height: more room for the waterfall. */
export function fftShare(oscilloscope) {
    return oscilloscope ? FFT_SHARE_OSC : FFT_SHARE_FALL;
}

let showOsc = false;
try { showOsc = localStorage.getItem(OSC_KEY) === '1'; } catch { /* storage blocked */ }
const PALETTE = fallPalette();

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
        this.fall = document.createElement('canvas');
        this.fall.width = FALL_POINTS;
        this.fall.height = FALL_ROWS;
        this.fallCtx = this.fall.getContext('2d');
        this.fallCtx.fillStyle = '#000';
        this.fallCtx.fillRect(0, 0, FALL_POINTS, FALL_ROWS);
        this.rowSum = new Float64Array(FALL_POINTS);
        this.rowFrames = 0;
        this.stretch = null;
    }

    add(data) {
        const fft = decodeBytes(data.fft);
        const osc = decodeBytes(data.osc);
        if (fft.length < FFT_POINTS || osc.length < OSC_COLUMNS * 2) return;
        smooth(this.fft, fft, this.has ? FFT_FOLLOW : 1);
        this._addToRow(fft);
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
        this.rowSum.fill(0);
        this.rowFrames = 0;
    }

    // Raw frames, not the smoothed trace, so the waterfall stays sharp in time.
    _addToRow(fft) {
        for (let i = 0; i < FALL_POINTS; i++) this.rowSum[i] += fft[i + 1];
        if (++this.rowFrames < FRAMES_PER_ROW) return;
        const row = averageRow(this.rowSum, this.rowFrames);
        this.rowSum.fill(0);
        this.rowFrames = 0;

        const target = rowStretch(row);
        if (!this.stretch) this.stretch = { ...target };
        else {
            this.stretch.low += (target.low - this.stretch.low) * FALL_FOLLOW;
            this.stretch.high += (target.high - this.stretch.high) * FALL_FOLLOW;
        }
        const low = Math.round(this.stretch.low);
        const high = Math.max(Math.round(this.stretch.high), low + 1);

        // Newest row at the top: move the rest down one.
        const g = this.fallCtx;
        g.drawImage(this.fall, 0, 0, FALL_POINTS, FALL_ROWS - 1, 0, 1, FALL_POINTS, FALL_ROWS - 1);
        const img = g.createImageData(FALL_POINTS, 1);
        for (let i = 0; i < FALL_POINTS; i++) {
            const v = row[i] <= low ? 0 : row[i] >= high ? 255 : Math.round((row[i] - low) * 255 / (high - low));
            img.data[i * 4] = PALETTE[v * 3];
            img.data[i * 4 + 1] = PALETTE[v * 3 + 1];
            img.data[i * 4 + 2] = PALETTE[v * 3 + 2];
            img.data[i * 4 + 3] = 255;
        }
        g.putImageData(img, 0, 0);
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
        const fftH = Math.round(h * fftShare(showOsc));
        this._drawFft(g, 0, 0, w, fftH, dpr);
        if (showOsc) this._drawOsc(g, 0, fftH + gap, w, h - fftH - gap);
        else this._drawFall(g, 0, fftH + gap, w, h - fftH - gap);
    }

    // One row a device pixel, under the AF-FFT's points 1 to 199.
    _drawFall(g, x, y, w, h) {
        if (h < 2) return;
        const rows = Math.min(h, FALL_ROWS);
        const left = x + w / FFT_POINTS;
        g.imageSmoothingEnabled = false;
        g.drawImage(this.fall, 0, 0, FALL_POINTS, rows, left, y, x + w - left, rows);
        g.imageSmoothingEnabled = true;
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
        this.osc = root.querySelector('.acc-audio-osc');
        if (this.osc) {
            this.osc.checked = showOsc;
            this.osc.addEventListener('change', () => setShowOsc(this.osc.checked));
        }
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

/** The Oscilloscope box: one choice for every panel on the page, remembered. */
function setShowOsc(on) {
    showOsc = on;
    try { localStorage.setItem(OSC_KEY, on ? '1' : '0'); } catch { /* storage blocked */ }
    panels.forEach(p => {
        if (p.osc) p.osc.checked = on;
        if (p.wanted) p.draw();
    });
}
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
