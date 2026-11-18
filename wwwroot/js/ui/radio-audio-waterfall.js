// Yaesu Web Control — the radio audio waterfall.
//
// The radio's USB RX audio, the same audio WSJT-X decodes, from 0 to 4 kHz:
// a spectrum line above a waterfall, with a label over each signal saying
// what mode it looks like. The host sends frames on RadioStateUpdate as
// property "AudioWaterfall" and the guesses as "AudioWaterfallModes"
// (AudioWaterfallService) only while a page has asked: this subscribes when
// an AUDIO group is open and on screen, and unsubscribes when none is, so the
// capture is let go.
//
// One hub connection for the page, shared by every panel on it (the Radio
// Scope card and the Radio Display Controls dialog can both be on one page).
// Panels are found by class, never by id, for the same reason.

import { decodeBytes, averageRow, rowStretch, fallPalette } from './acc-audio-scope.js';

const MAX_HZ = 4000;
// Frames come about 11.7 a second, so 2 a row puts about 17 seconds in 100
// rows: more than a whole FT8 cycle.
export const FRAMES_PER_ROW = 2;
const FALL_ROWS = 600;             // more than the tallest pane, in device pixels
const FALL_FOLLOW = 0.15;          // how fast the colours follow the levels
const LINE_FOLLOW = 0.4;           // how much of each frame goes into the line
const SPECTRUM_SHARE = 0.3;        // the line's share of the height
const LABEL_STRIP = 16;            // CSS pixels for the mode labels
const STALE_MS = 1500;             // frames older than this are not drawn
const OPEN_KEY = 'ywc.radioAudioFallOpen';
// AudioModeClassifier.MinHistorySeconds: no guesses before a whole FT8 cycle.
export const GUESS_AFTER_MS = 16000;
const PALETTE = fallPalette();

/** What a guess says, in words: WSJT-X decodes are facts, the rest are guesses. */
export function guessText(g) {
    const where = `${Math.round(g.lowHz)}–${Math.round(g.highHz)} Hz`;
    const how = g.fromWsjtx ? 'WSJT-X decoded' : 'looks like';
    return `${where}: ${how} ${g.mode}${g.detail ? ` (${g.detail})` : ''}`;
}

/** The label drawn over a signal: the mode, with a ? when it's only a guess. */
export function labelText(g) {
    return g.fromWsjtx || g.mode.endsWith('?') ? g.mode : `${g.mode}?`;
}

// ── one panel ────────────────────────────────────────────────────────────────

class Panel {
    constructor(root) {
        this.root = root;
        this.details = root.closest('details');
        this.canvas = root.querySelector('canvas');
        this.list = root.querySelector('.radio-audio-fall-modes');
        this.status = root.querySelector('.radio-audio-fall-status');
        this.message = '';
        this.visible = false;
        this.lastAt = 0;
        this.guesses = [];
        this.bins = 0;
        this.binHz = 1;
        this.line = null;
        this.fall = null;
        this.rowSum = null;
        this.rowFrames = 0;
        this.stretch = null;

        if (this.details) {
            try { this.details.open = localStorage.getItem(OPEN_KEY) === '1'; } catch { /* storage blocked */ }
            this.details.addEventListener('toggle', () => {
                try { localStorage.setItem(OPEN_KEY, this.details.open ? '1' : '0'); } catch { /* storage blocked */ }
                update();
            });
        }
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

    _size(bins) {
        this.bins = bins;
        this.line = new Float64Array(bins);
        this.rowSum = new Float64Array(bins);
        this.rowFrames = 0;
        this.stretch = null;
        this.fall = document.createElement('canvas');
        this.fall.width = bins;
        this.fall.height = FALL_ROWS;
        this.fallCtx = this.fall.getContext('2d');
        this.fallCtx.fillStyle = '#000';
        this.fallCtx.fillRect(0, 0, bins, FALL_ROWS);
    }

    receive(value) {
        if (!value) return;
        const bins = decodeBytes(value.bins);
        if (!bins.length) return;
        if (bins.length !== this.bins) this._size(bins.length);
        this.binHz = value.binHz || this.binHz;
        const first = !this.lastAt;
        if (first) this.firstAt = Date.now();
        for (let i = 0; i < bins.length; i++)
            this.line[i] = first ? bins[i] : this.line[i] + (bins[i] - this.line[i]) * LINE_FOLLOW;
        this._addToRow(bins);
        this.lastAt = Date.now();
        const listening = this.lastAt - this.firstAt < GUESS_AFTER_MS;
        this.setStatus('The radio\'s USB audio, 0 to 4 kHz. ' + (listening
            ? 'Mode guesses start after 16 s of listening.'
            : 'Modes with a ? are guesses; WSJT-X decodes have none.'));
    }

    receiveModes(list) {
        this.guesses = Array.isArray(list) ? list : [];
        if (!this.list) return;
        this.list.replaceChildren(...this.guesses.map(g => {
            const li = document.createElement('li');
            li.textContent = guessText(g);
            if (g.fromWsjtx) li.classList.add('radio-audio-fall-wsjtx');
            return li;
        }));
    }

    setStatus(text) {
        if (text === this.message || !this.status) return;
        this.message = text;
        this.status.textContent = text;
    }

    clear() {
        this.lastAt = 0;
        this.guesses = [];
        if (this.line) this.line.fill(0);
        if (this.list) this.list.replaceChildren();
    }

    _addToRow(bins) {
        for (let i = 0; i < this.bins; i++) this.rowSum[i] += bins[i];
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
        const g = this.fallCtx, n = this.bins;
        g.drawImage(this.fall, 0, 0, n, FALL_ROWS - 1, 0, 1, n, FALL_ROWS - 1);
        const img = g.createImageData(n, 1);
        for (let i = 0; i < n; i++) {
            const v = row[i] <= low ? 0 : row[i] >= high ? 255 : Math.round((row[i] - low) * 255 / (high - low));
            img.data[i * 4] = PALETTE[v * 3];
            img.data[i * 4 + 1] = PALETTE[v * 3 + 1];
            img.data[i * 4 + 2] = PALETTE[v * 3 + 2];
            img.data[i * 4 + 3] = 255;
        }
        g.putImageData(img, 0, 0);
    }

    draw() {
        if (this.lastAt && Date.now() - this.lastAt > STALE_MS) this.clear();
        const c = this.canvas;
        if (!c) return;
        const dpr = window.devicePixelRatio || 1;
        const w = Math.max(1, Math.round(c.clientWidth * dpr));
        const h = Math.max(1, Math.round(c.clientHeight * dpr));
        if (c.width !== w || c.height !== h) { c.width = w; c.height = h; }
        const g = c.getContext('2d');
        g.fillStyle = '#000';
        g.fillRect(0, 0, w, h);

        const labelH = Math.round(LABEL_STRIP * dpr);
        const lineH = Math.round((h - labelH) * SPECTRUM_SHARE);
        const gap = Math.round(2 * dpr);
        const xAt = hz => hz * w / MAX_HZ;

        this._drawLine(g, 0, 0, w, lineH, dpr, xAt);
        this._drawLabels(g, lineH, w, labelH, dpr, xAt);
        this._drawFall(g, lineH + labelH + gap, w, h - lineH - labelH - gap, xAt);
    }

    _drawLine(g, x, y, w, h, dpr, xAt) {
        g.strokeStyle = '#222';
        g.lineWidth = 1;
        g.fillStyle = '#8aa';
        g.font = `${Math.round(10 * dpr)}px system-ui, sans-serif`;
        g.textAlign = 'center';
        g.textBaseline = 'top';
        for (let hz = 500; hz < MAX_HZ; hz += 500) {
            const px = Math.round(xAt(hz)) + 0.5;
            g.beginPath(); g.moveTo(px, y); g.lineTo(px, y + h); g.stroke();
            if (hz % 1000 === 0) g.fillText(`${hz / 1000} kHz`, px, y + 2 * dpr);
        }
        if (!this.lastAt || !this.line) return;

        // From the median (about the noise) to the peak, like the waterfall.
        const sorted = Array.from(this.line).sort((a, b) => a - b);
        const low = sorted[Math.floor(sorted.length / 2)] - 4;
        const range = Math.max(sorted[sorted.length - 1] - low, 20) * 1.1;
        const yAt = v => Math.min(y + h - 1, Math.max(y, y + h - 1 - (v - low) * (h - 2) / range));
        g.beginPath();
        for (let i = 1; i < this.bins; i++) {
            const px = xAt(i * this.binHz), py = yAt(this.line[i]);
            if (i === 1) g.moveTo(px, py); else g.lineTo(px, py);
        }
        g.strokeStyle = '#4fc3f7';
        g.lineWidth = Math.max(1, dpr);
        g.stroke();
    }

    // A bracket under each signal, its mode above it: grey for a guess,
    // green for a WSJT-X decode.
    _drawLabels(g, y, w, h, dpr, xAt) {
        if (!this.guesses.length) return;
        g.font = `${Math.round(10 * dpr)}px system-ui, sans-serif`;
        g.textAlign = 'center';
        g.textBaseline = 'top';
        let lastRight = -Infinity;
        for (const s of this.guesses) {
            const x0 = xAt(s.lowHz), x1 = Math.max(xAt(s.highHz), x0 + 2 * dpr);
            const colour = s.fromWsjtx ? '#81c784' : '#cfd8dc';
            g.strokeStyle = colour;
            g.lineWidth = Math.max(1, dpr);
            g.beginPath();
            g.moveTo(x0, y + h - 4 * dpr); g.lineTo(x0, y + h - 1); g.lineTo(x1, y + h - 1); g.lineTo(x1, y + h - 4 * dpr);
            g.stroke();
            const text = labelText(s);
            const tw = g.measureText(text).width;
            const cx = Math.min(Math.max((x0 + x1) / 2, tw / 2), w - tw / 2);
            if (cx - tw / 2 < lastRight + 4 * dpr) continue;   // no room: the list has it
            g.fillStyle = colour;
            g.fillText(text, cx, y + 1);
            lastRight = cx + tw / 2;
        }
    }

    // One row a device pixel; the bins stretched across the width.
    _drawFall(g, y, w, h, xAt) {
        if (h < 2 || !this.fall) return;
        const rows = Math.min(h, FALL_ROWS);
        const right = Math.min(w, xAt(this.bins * this.binHz));
        g.imageSmoothingEnabled = false;
        g.drawImage(this.fall, 0, 0, this.bins, rows, 0, y, right, rows);
        g.imageSmoothingEnabled = true;
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
        if (!update) return;
        if (update.property === 'AudioWaterfall') {
            panels.forEach(p => { if (p.wanted) p.receive(update.value); });
            scheduleDraw();
        } else if (update.property === 'AudioWaterfallModes') {
            panels.forEach(p => { if (p.wanted) p.receiveModes(update.value); });
        }
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
                const reason = await connection.invoke('SubscribeAudioWaterfall');
                if (reason) panels.forEach(p => p.setStatus(reason));
                else panels.forEach(p => { if (!p.lastAt) p.setStatus('Waiting for the radio\'s audio...'); });
            } else {
                await connection.invoke('UnsubscribeAudioWaterfall');
            }
        } catch {
            panels.forEach(p => p.setStatus('Could not reach YWC for the audio waterfall.'));
            subscribed = false;
        }
    });
}

/** Wires every audio waterfall panel inside root. Safe to call more than once. */
export function attachRadioAudioWaterfall(root) {
    if (!root) return;
    root.querySelectorAll('.radio-audio-fall').forEach(el => {
        if (el.dataset.radioAudioFallWired === '1') return;
        el.dataset.radioAudioFallWired = '1';
        panels.push(new Panel(el));
    });
    update();
}
