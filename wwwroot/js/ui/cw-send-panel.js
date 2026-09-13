// CW Send - type a line, press Enter, the radio keys it.
//
// The radio has no "send this text" command. What it has is five keyer
// memories of up to 50 characters (KM) and a command that plays one of them
// whole (KY). So a typed line becomes a sequence of writes to one scratch
// slot - M5 - each followed by a play, each waited on before the next, with
// the operator's own M5 text put back when the queue drains.
//
// Everything the radio is asked to do goes through the endpoints the keyer
// panel already uses: /api/cat/cw/send writes-then-plays one chunk and
// reports whether break-in put it on the air; /api/cat/cw/playing is the
// radio's RI4 flag; /api/cat/cw/keyer/5 reads and writes the slot without
// playing it. Nothing here is a new CAT command - the sequencing is the
// whole feature.
//
// Two facts measured on the FTdx101MP (2026-09-09) shape this file:
//   - there is NO stop. KY1; clears RI4 but the message keys on regardless.
//     "Stop" here therefore means "send nothing further"; the chunk on the
//     air finishes.
//   - RI4 cannot be relied on to report a KY playback at all. With break-in
//     off it stays 0 throughout (measured 2026-09-09), and on 2026-09-13 it
//     stayed 0 for a whole message keyed on air with semi break-in too. So
//     the wait between chunks is the keying time computed from the Morse
//     table (core/js/cw/morse-timing.js) in both cases - the highlight
//     driven by the same clock kept pace with the sidetone on air, which is
//     the check that the textbook timing is the radio's. RI4 is only a
//     bonus: if it does assert it can end the wait early, or hold it.
//
// YWC-local (it is built on KM/KY/RI4, which are Yaesu's), so it lives in
// wwwroot/js/ui/, not core/.

import { durationMs, charTimeline } from '../cw/morse-timing.js';

const SLOT = 5;                 // scratch keyer memory
const MAX_CHUNK = 50;           // the radio's KM limit
const POLL_MS = 250;            // RI4 poll while a chunk plays
const CURSOR_MS = 40;           // how often the sent-character highlight moves
const RETRY_BUSY_MS = 1000;     // wait when the radio says something else is playing
const MAX_FOLLOW_MS = 150000;   // a 50-char chunk at 4 wpm is ~2.5 min
// Slack on top of the computed keying time before the next piece is
// written: the radio's own latency between KY and the first element, and
// a little for a keyer whose spacing is not exactly the textbook's. Small
// on purpose - every millisecond here is silence between pieces.
const TIMING_MARGIN_MS = 250;
const TIMING_MARGIN_FRAC = 0.03;

// Same character set the server keeps (CleanCw): the chunk lengths have to
// agree with what the radio will actually store, or a 50-char chunk full of
// dropped punctuation would be padded out by the next word.
export function cleanCw(text) {
    return (text || '').toUpperCase().replace(/\s+/g, ' ').replace(/[^A-Z0-9 ?/.,]/g, '').replace(/ +/g, ' ').trim();
}

// Split cleaned text into <=50-char pieces at word boundaries. A single
// word longer than 50 characters (nobody sends one, but a pasted string
// might) is cut hard rather than dropped.
export function chunkCw(clean, max = MAX_CHUNK) {
    const out = [];
    let cur = '';
    for (const word of clean.split(' ')) {
        if (!word) continue;
        if (word.length > max) {
            if (cur) { out.push(cur); cur = ''; }
            for (let i = 0; i < word.length; i += max) out.push(word.slice(i, i + max));
            continue;
        }
        if (!cur) cur = word;
        else if (cur.length + 1 + word.length <= max) cur += ' ' + word;
        else { out.push(cur); cur = word; }
    }
    if (cur) out.push(cur);
    return out;
}

export class CwSendPanel {
    constructor(ids = {}) {
        this._ids = Object.assign({
            dialog: 'cwSendDialog',
            input: 'cwSendInput',
            log: 'cwSendLog',
            status: 'cwSendStatus',
            banner: 'cwSendBanner',
            stopBtn: 'cwSendStopBtn',
            clearBtn: 'cwSendClearBtn',
            clearInputBtn: 'cwSendClearInputBtn',
            speedSlider: 'cwSendSpeedSlider',
            speedValue: 'cwSendSpeedValue',
        }, ids);
        this._queue = [];          // [{ line, chunks }]
        this._current = null;      // the item whose chunks are going out
        this._running = false;
        this._savedSlot = null;    // what M5 held before we borrowed it; null = not read
        this._lastWritten = null;  // what we last put in M5
        this._breakIn = null;      // '0' | '1' | '2' | null unknown
        this._lineNo = 0;
        this._cursor = null;       // interval moving the highlight along the piece on air
    }

    init() {
        const $ = id => document.getElementById(id);
        this._dialog = $(this._ids.dialog);
        this._input = $(this._ids.input);
        this._log = $(this._ids.log);
        this._status = $(this._ids.status);
        this._banner = $(this._ids.banner);
        this._speedSlider = $(this._ids.speedSlider);
        this._speedValue = $(this._ids.speedValue);
        if (!this._dialog || !this._input) return;

        this._input.addEventListener('keydown', e => this._onKeydown(e));
        $(this._ids.stopBtn)?.addEventListener('click', () => this.stop());
        $(this._ids.clearBtn)?.addEventListener('click', () => this.clearLog());
        $(this._ids.clearInputBtn)?.addEventListener('click', () => this.clearInput());
        if (this._speedSlider) {
            this._speedSlider.addEventListener('input', () => {
                if (this._speedValue) this._speedValue.textContent = this._speedSlider.value;
            });
            this._speedSlider.addEventListener('change', () => this._postSpeed(this._speedSlider.value));
        }
        // The keyer dialog's own controls are the source of truth for what
        // the radio said last; copy them so the box is right before the
        // first SignalR update reaches it.
        const ks = document.getElementById('cwSpeedSlider');
        if (ks) this.setSpeed(ks.value);
        const bi = document.getElementById('cwBreakInSelect');
        if (bi) this.setBreakIn(bi.value);
        this._say('Type a line and press Enter to send it. Escape or Stop drops anything not yet started.');
    }

    // ── Open / close ─────────────────────────────────────────────────────

    toggle() {
        if (!this._dialog) return;
        if (this._dialog.open) this._dialog.close();
        else this.show();
    }

    show() {
        if (!this._dialog) return;
        // show(), not showModal(): the operator works the rest of the station
        // while a line is on its way, and reads the CW reader beside it.
        if (!this._dialog.open) this._dialog.show();
        this._input.focus();
    }

    // ── Radio state pushed in from the page (SignalR) ─────────────────────

    setSpeed(v) {
        const n = parseInt(v, 10);
        if (!Number.isFinite(n)) return;
        if (this._speedSlider) this._speedSlider.value = n;
        if (this._speedValue) this._speedValue.textContent = n;
    }

    setBreakIn(v) {
        this._breakIn = v == null ? null : String(v);
        this._renderBanner();
    }

    _renderBanner() {
        if (!this._banner) return;
        // Break-in off is a legitimate practice mode (the manual's own way
        // to audition a memory), so it is a notice, not a refusal.
        const off = this._breakIn === '0';
        this._banner.hidden = !off;
        if (off) this._banner.textContent = 'Break-in is off - text plays to the monitor only, nothing is transmitted.';
    }

    // ── Input ────────────────────────────────────────────────────────────

    _onKeydown(e) {
        if (e.key === 'Enter') {
            e.preventDefault();
            this.send(this._input.value);
            this._input.value = '';
        } else if (e.key === 'Escape') {
            e.preventDefault();
            // Escape is Stop while anything is going out; with nothing to
            // stop it empties the box instead (a paste that was never
            // meant for the keyer is the usual reason to want that).
            if (this._running || this._queue.length) this.stop();
            else this.clearInput();
        }
    }

    clearInput() {
        if (!this._input) return;
        this._input.value = '';
        this._input.focus();
    }

    // Queue a line. Returns the number of chunks it became, 0 if nothing
    // sendable was in it.
    send(text) {
        const clean = cleanCw(text);
        if (!clean) {
            if ((text || '').trim()) this._say('Nothing sendable in that line - the keyer takes A-Z, 0-9, space ? / . , only.', true);
            return 0;
        }
        const chunks = chunkCw(clean);
        const item = { no: ++this._lineNo, line: clean, chunks, pos: 0, stopped: false, el: this._logLine(clean, 'queued') };
        this._queue.push(item);
        this._pump();
        return chunks.length;
    }

    // "Stop" is the honest version: the chunk already playing cannot be
    // stopped (no CAT command exists), but nothing after it starts.
    stop() {
        const dropped = this._queue.splice(0, this._queue.length);
        for (const it of dropped) this._tag(it.el, 'dropped', 'not sent');
        if (this._current) {
            this._current.stopped = true;
            this._say('Stopping - the piece already playing has to finish, nothing more will start.');
        } else if (dropped.length) {
            this._say('Queue cleared.');
        }
    }

    clearLog() {
        if (this._log) this._log.textContent = '';
    }

    // ── Sequencing ───────────────────────────────────────────────────────

    async _pump() {
        if (this._running) return;
        this._running = true;
        this._setMemButtons(true);
        try {
            await this._saveSlot();
            while (this._queue.length) {
                this._current = this._queue.shift();
                await this._sendItem(this._current);
                this._current = null;
            }
        } finally {
            this._current = null;
            await this._restoreSlot();
            this._running = false;
            this._setMemButtons(false);
            this._updateQueueStatus();
        }
    }

    async _sendItem(item) {
        const n = item.chunks.length;
        let wentOnAir = null;
        for (let i = 0; i < n; i++) {
            if (item.stopped) {
                this._tag(item.el, 'dropped', i ? `stopped after part ${i} of ${n}` : 'not sent');
                return;
            }
            this._tag(item.el, 'sending', n > 1 ? `sending part ${i + 1} of ${n}` : 'sending');
            this._updateQueueStatus();
            const r = await this._sendChunk(item.chunks[i]);
            if (!r) {
                this._tag(item.el, 'error', i ? `failed at part ${i + 1} of ${n}` : 'failed');
                return;
            }
            if (wentOnAir == null) wentOnAir = r.transmitted !== false;
            else if (r.transmitted === false) wentOnAir = false;
            this._cursorStart(item, r);
            await this._follow(r);
            this._cursorStop();
        }
        this._tag(item.el, wentOnAir ? 'sent' : 'monitor', wentOnAir ? 'sent' : 'monitor only');
    }

    // One KM+KY through the server. Returns the response body, or null if
    // the chunk could not be started (the line is abandoned then - text
    // arriving with a hole in it is worse than text that stops).
    async _sendChunk(chunk) {
        const deadline = Date.now() + MAX_FOLLOW_MS;
        for (;;) {
            let r, d;
            try {
                r = await fetch('/api/cat/cw/send', {
                    method: 'POST', headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ message: chunk, slot: SLOT }),
                });
                d = await r.json().catch(() => ({}));
            } catch (e) {
                this._say(`Send failed: ${e.message}`, true);
                return null;
            }
            if (r.ok) {
                this._lastWritten = d.sent ?? chunk;
                if (d.breakIn != null) this.setBreakIn(d.breakIn);
                return d;
            }
            // 409 = the radio says something is still playing (a memory
            // button pressed from the keyer dialog, or our previous chunk
            // running past its estimate). It cannot be stopped, so wait.
            if (r.status === 409 && Date.now() < deadline && !this._current?.stopped) {
                this._say('Radio is still sending - waiting for it to finish.');
                await new Promise(res => setTimeout(res, RETRY_BUSY_MS));
                continue;
            }
            this._say(d.error || `Send failed (HTTP ${r.status}).`, true);
            return null;
        }
    }

    // How long the radio will be keying this piece. The speed is the one
    // the server read from the radio just before KY - it is not returned
    // as such, but its estimate is length x 10 units at that speed, so the
    // speed falls straight out of it. The slider is the fallback.
    _wpmOf(d) {
        const sent = d.sent || '';
        let wpm = NaN;
        if (d.estimatedMs > 0 && sent.length) wpm = Math.round(sent.length * 12000 / d.estimatedMs);
        if (!(wpm >= 4 && wpm <= 60)) wpm = parseInt(this._speedSlider?.value, 10);
        if (!(wpm >= 4 && wpm <= 60)) wpm = 18;
        return wpm;
    }

    _keyingMs(d) {
        return durationMs(d.sent || '', this._wpmOf(d));
    }

    // ── The character under the key ──────────────────────────────────────
    //
    // Nothing comes back from the radio about where it is in the text, so
    // this is the textbook timeline run from the moment KY was sent: the
    // same clock the wait between pieces uses. Off by the radio's own start
    // latency, and by whatever its keyer does that the textbook does not -
    // a display, not a measurement.

    _cursorStart(item, d) {
        this._cursorStop();
        const el = item.el;
        const chunk = d.sent || '';
        if (!el || !chunk) return;
        const from = item.line.indexOf(chunk, item.pos);
        if (from < 0) return;
        item.pos = from + chunk.length;
        const spans = el.querySelectorAll('.cws-ch');
        const timeline = charTimeline(chunk, this._wpmOf(d));
        const t0 = Date.now();
        const tick = () => {
            const t = Date.now() - t0;
            for (const e of timeline) {
                const sp = spans[from + e.index];
                if (!sp) continue;
                sp.classList.toggle('cws-done', e.end <= t && e.end > e.start);
                sp.classList.toggle('cws-cur', e.start <= t && t < e.end);
            }
        };
        tick();
        this._cursor = { id: setInterval(tick, CURSOR_MS), spans, from, len: chunk.length };
    }

    // The piece has ended (RI4 cleared, or the computed time ran out):
    // everything in it is sent, whatever the clock says.
    _cursorStop() {
        const c = this._cursor;
        if (!c) return;
        this._cursor = null;
        clearInterval(c.id);
        for (let i = c.from; i < c.from + c.len; i++) {
            const sp = c.spans[i];
            if (!sp) continue;
            sp.classList.remove('cws-cur');
            sp.classList.add('cws-done');
        }
    }

    // Wait for the chunk to finish before the next one is written: the
    // computed keying time plus a small margin, in both modes.
    //
    // Monitor only (break-in off): the radio reports nothing, so there is
    // nothing to poll.
    //
    // On air: RI4 is polled as well, but only as a bonus. It has never
    // asserted for a KY playback on the bench radio (2026-09-13, semi
    // break-in, RI4 read 0 for the whole message) - a wait that expected
    // it and fell back after an extra 1.5 s was the whole of the on-air
    // gap. If it does assert on some radio it ends the wait the moment it
    // clears, and holds the wait while it stays up.
    async _follow(d) {
        const keying = Math.max(300, this._keyingMs(d));
        const margin = TIMING_MARGIN_MS + keying * TIMING_MARGIN_FRAC;
        if (d.transmitted === false) {
            await new Promise(r => setTimeout(r, keying + margin));
            return;
        }
        const started = Date.now();
        const endBy = started + keying + margin;
        const giveUp = started + MAX_FOLLOW_MS;
        let sawPlaying = false;
        while (Date.now() < giveUp) {
            await new Promise(r => setTimeout(r, POLL_MS));
            let playing = null;
            try {
                const r = await fetch('/api/cat/cw/playing');
                const dd = await r.json().catch(() => ({}));
                playing = dd.playing;
            } catch { break; }
            if (playing === true) { sawPlaying = true; continue; }
            if (playing === false && sawPlaying) {
                // Worth a line in the console: the radio's own end against
                // the textbook time is how the monitor-only wait gets
                // checked without a second measurement rig.
                console.info(`cw-send: RI4 cleared after ${Date.now() - started} ms, computed keying ${keying} ms`);
                return;
            }
            if (Date.now() >= endBy) return;
        }
    }

    // ── The borrowed slot ────────────────────────────────────────────────

    async _saveSlot() {
        if (this._savedSlot !== null) return;
        try {
            const r = await fetch(`/api/cat/cw/keyer/${SLOT}`);
            const d = await r.json().catch(() => ({}));
            if (r.ok && d.read && typeof d.text === 'string') this._savedSlot = d.text;
        } catch { /* leave it unknown - nothing to put back */ }
    }

    async _restoreSlot() {
        const saved = this._savedSlot;
        if (saved === null) return;
        this._savedSlot = null;
        // Nothing to do if the radio still holds what it held, or if what we
        // wrote happens to be the same text.
        if (this._lastWritten === null || saved === this._lastWritten) return;
        try {
            const r = await fetch(`/api/cat/cw/keyer/${SLOT}`, {
                method: 'PUT', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ text: saved }),
            });
            const d = await r.json().catch(() => ({}));
            if (!r.ok || d.matches === false) this._say(`M${SLOT} could not be put back to "${saved}" - check it in the CW keyer panel.`, true);
        } catch (e) {
            this._say(`M${SLOT} could not be put back: ${e.message}`, true);
        }
        this._lastWritten = null;
    }

    // ── Speed ────────────────────────────────────────────────────────────

    async _postSpeed(v) {
        try {
            await fetch('/api/cat/cw/speed', {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ speed: parseInt(v, 10) }),
            });
        } catch (e) { this._say(`Speed change failed: ${e.message}`, true); }
    }

    // ── Display ──────────────────────────────────────────────────────────

    _logLine(text, state) {
        if (!this._log) return null;
        const row = document.createElement('div');
        row.className = 'cws-line';
        const t = document.createElement('span');
        t.className = 'cws-time';
        t.textContent = new Date().toTimeString().slice(0, 8);
        const body = document.createElement('span');
        body.className = 'cws-text';
        // One span per character so the one under the key can be lit.
        for (const ch of text) {
            const sp = document.createElement('span');
            sp.className = 'cws-ch';
            sp.textContent = ch;
            body.appendChild(sp);
        }
        const tag = document.createElement('span');
        tag.className = 'cws-tag';
        row.append(t, body, tag);
        this._log.appendChild(row);
        this._tag(row, state, state);
        this._log.scrollTop = this._log.scrollHeight;
        return row;
    }

    _tag(row, state, label) {
        if (!row) return;
        row.dataset.state = state;
        const tag = row.querySelector('.cws-tag');
        if (tag) tag.textContent = label;
    }

    _updateQueueStatus() {
        if (!this._running) {
            // A problem reported on the way out (M5 not put back) outlives
            // the queue; only a clean finish says Ready.
            if (!this._status?.classList.contains('cws-problem')) this._say('Ready.');
            return;
        }
        const waiting = this._queue.length;
        this._say(waiting ? `Sending - ${waiting} more line${waiting === 1 ? '' : 's'} queued.` : 'Sending.');
    }

    _say(text, bad) {
        if (!this._status) return;
        this._status.textContent = text;
        this._status.classList.toggle('cws-problem', !!bad);
    }

    // The keyer dialog's M1-M5 buttons: mark them busy while a typed line
    // is on its way so a press there does not land on top of it. The hook
    // is optional - the page provides it, the module only calls it.
    _setMemButtons(busy) {
        try { window.cwMemButtonsBusy?.(busy); } catch { /* ignore */ }
    }
}
