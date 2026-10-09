// Radio Web Control - RTTY Reader
// Shared by Icom Web Control and Yaesu Web Control. This file is copied into
// each app's wwwroot at build time - see js/README.md. Edit it here, in the
// core, never in a wwwroot copy.
//
// Prints what the server-side RTTY demodulator is making of the receive audio.
// The decoding is Core's (RttyDemodulator); this starts it, polls it and shows
// it. Radio-agnostic: everything radio-specific arrives through the
// /api/rtty/reader endpoints, which each app implements itself.
//
// It has no Mark, Shift, Speed or Rev of its own, on purpose. Those belong to
// the tuner sitting next to it in the same panel, the server reads them from
// there, and the operator sets them once: tune the cross, or press Auto, and
// the decoder is already listening in the right place. A second set of boxes
// would be a second thing to keep in step and a new way to be wrong.
//
// Polling, not a socket: RTTY arrives at six characters a second at 45.45
// baud, so asking twice a second reads as live and costs nothing. Each poll
// sends the cursor from the previous reply and gets back only what is new.
//
// A word on what the text is worth. This is a machine reading tones out of
// noise: on a clean signal it is close to perfect, and on a marginal one it
// prints plausible-looking rubbish with no outward sign of the difference. The
// signal figure on the readout line is the honest answer to "is anything
// there?" - about 0.8 for a real station and around a third for hiss - and it
// is shown for exactly that reason. Nothing here hides low-confidence output:
// an operator can see a wrong letter, but they cannot see one that was never
// printed.

const POLL_MS      = 500;
const LS_KEY       = 'rttyReader';
const MAX_RENDERED = 24000;   // characters kept in the pane

export class RttyReader {
    /**
     * @param {object} [ids]
     * @param {string} [ids.dialog]  the panel this reader lives in. Polling
     *     follows that dialog being open, so the host page does not have to
     *     wire it up; a page where the reader is the whole window passes none
     *     and calls startPolling() itself.
     */
    constructor(ids = {}) {
        this._dialogId = ids.dialog ?? 'rttyTunerDialog';
        this._dialog   = null;
        this._out      = null;
        this._status   = null;
        this._info     = null;    // the numbers; deliberately not a live region
        this._statusSig = null;   // what status last said, so an unchanged line leaves the DOM alone
        this._startBtn = null;
        this._clearBtn = null;
        this._autoScrl = null;
        this._figures  = null;
        this._timer    = null;
        this._observer = null;
        this._cursor   = 0;
        this._running  = false;
        this._paused   = false;
        this._text     = '';
    }

    init() {
        this._out = document.getElementById('rttyReaderOut');
        if (!this._out) return this;          // a host page without the reader section

        this._dialog   = document.getElementById(this._dialogId);
        this._status   = document.getElementById('rttyReaderStatus');
        this._info     = document.getElementById('rttyReaderInfo');
        this._startBtn = document.getElementById('rttyReaderStartBtn');
        this._clearBtn = document.getElementById('rttyReaderClearBtn');
        this._autoScrl = document.getElementById('rttyReaderAutoScroll');
        this._figures  = document.getElementById('rttyReaderFigures');

        this._loadSettings();

        this._startBtn?.addEventListener('click', () => this._toggleRunning());
        this._clearBtn?.addEventListener('click', () => this._clear());
        this._autoScrl?.addEventListener('change', () => this._saveSettings());
        // Changing the alphabet mid-session re-sends start, which the server
        // takes as "same session, new table". Nothing is lost: the text stays
        // and only the character being assembled at that instant is dropped.
        this._figures?.addEventListener('change', () => {
            this._saveSettings();
            if (this._running) this._send('start');
        });

        if (this._dialog) {
            // Follow the dialog rather than asking the host to tell us. show()
            // and close() set and clear the open attribute, so one observer
            // covers every way the panel can be opened - the button, a pop-out
            // reattaching, or another panel closing this one.
            this._observer = new MutationObserver(() => this._followDialog());
            this._observer.observe(this._dialog, { attributes: true, attributeFilter: ['open'] });
            this._followDialog();
        }

        return this;
    }

    /** Start reading. For a page where the reader is the whole window. */
    startPolling() { this._startPolling(); }

    /** Stop polling. The decoder itself keeps running on the server. */
    stopPolling()  { this._stopPolling(); }

    get isRunning() { return this._running; }

    /**
     * Pause or resume without closing - for a pop-out window, where closing
     * itself on a mode change would make a window vanish off a second monitor.
     * The decoder keeps running on the server either way.
     */
    setPaused(paused) {
        this._paused = !!paused;
        if (this._paused) this._stopPolling();
        else this._followDialog();
    }

    _followDialog() {
        if (this._paused) return;
        if (this._dialog?.open) this._startPolling();
        else this._stopPolling();
    }

    // ── Decoder control ─────────────────────────────────────────────────────

    async _toggleRunning() {
        await this._send(this._running ? 'stop' : 'start');
    }

    async _send(what) {
        if (this._startBtn) this._startBtn.disabled = true;
        try {
            const body = what === 'start'
                ? JSON.stringify({ figures: this._figures?.value || 'Ita2' })
                : null;
            const res = await fetch(`/api/rtty/reader/${what}`, {
                method: 'POST',
                headers: body ? { 'Content-Type': 'application/json' } : undefined,
                body,
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                this._showError(err.error || `HTTP ${res.status}`);
                return;
            }
            this._apply(await res.json(), { skipText: true });
            if (what === 'start') this._startPolling();
        } catch (e) {
            this._showError(e.message);
        } finally {
            if (this._startBtn) this._startBtn.disabled = false;
        }
    }

    async _clear() {
        this._text = '';
        if (this._out) this._out.textContent = '';
        try {
            const res = await fetch('/api/rtty/reader/clear', { method: 'POST' });
            if (!res.ok) return;
            const snap = await res.json();
            // Take the server's cursor, so the next poll asks for what comes
            // after the clear rather than replaying the buffer into an empty
            // pane.
            this._cursor = snap.cursor ?? this._cursor;
            this._apply(snap, { skipText: true });
        } catch (e) {
            this._showError(e.message);
        }
    }

    // ── Polling ─────────────────────────────────────────────────────────────

    _startPolling() {
        if (this._paused || this._timer) return;
        this._poll();
        this._timer = setInterval(() => this._poll(), POLL_MS);
    }

    _stopPolling() {
        if (!this._timer) return;
        clearInterval(this._timer);
        this._timer = null;
    }

    async _poll() {
        try {
            const res = await fetch(`/api/rtty/reader?since=${this._cursor}`);
            if (!res.ok) return;
            this._apply(await res.json());
        } catch {
            // A dropped poll is not worth reporting: the next one is 500 ms
            // away and will either succeed or keep failing visibly in status.
        }
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    _apply(snap, opts = {}) {
        if (!snap) return;

        this._running = !!snap.running;
        this._cursor  = snap.cursor ?? this._cursor;

        if (!opts.skipText && snap.text) {
            let chunk = snap.text;
            if (snap.truncated) {
                // The buffer rolled over between polls. Say so rather than
                // silently splicing text that is not contiguous.
                chunk = '\n[...]\n' + chunk;
            }
            this._text += chunk;
            if (this._text.length > MAX_RENDERED) {
                // Rare, and the only time the whole pane is rewritten: text is
                // otherwise appended as a node, which costs nothing however
                // long the session runs.
                this._text = this._text.slice(this._text.length - MAX_RENDERED);
                this._out.textContent = this._text;
            } else {
                this._out.append(document.createTextNode(chunk));
            }
            if (this._autoScrl?.checked !== false) this._out.scrollTop = this._out.scrollHeight;
        }

        if (this._startBtn) {
            this._startBtn.textContent = this._running ? 'Stop' : 'Start';
            this._startBtn.classList.toggle('btn-warning', this._running);
            this._startBtn.classList.toggle('btn-success', !this._running);
            this._startBtn.setAttribute('aria-pressed', String(this._running));
        }
        if (this._figures && snap.figures) {
            // The server is the authority on which table is in use: it keeps
            // the setting across a reload, and this page may be the second tab.
            const want = String(snap.figures).toLowerCase();
            for (const o of this._figures.options) {
                if (o.value.toLowerCase() === want) { this._figures.value = o.value; break; }
            }
        }

        this._render(snap);
    }

    // Two lines, and the split is the whole point of having two. The numbers
    // change on every poll, so they go in a plain div: as a live region a
    // screen reader would read the signal figure out loud twice a second and
    // bury the decoded text underneath it. The status line is the live region
    // and therefore says only the handful of things that genuinely change,
    // which is why it is a sentence and not a readout. The tuner next to it
    // splits its own two lines the same way, for the same reason.
    _render(snap) {
        this._renderInfo(snap);
        this._renderStatus(snap);
    }

    _renderInfo(snap) {
        if (!this._info) return;

        let text;
        if (!snap.running) {
            text = 'Waiting.';
        } else {
            const hz   = v => Math.round(v);
            const bits = [
                `mark ${hz(snap.markHz)}  space ${hz(snap.spaceHz)} Hz`,
                `${snap.baud} baud`,
            ];
            // Reverse belongs here rather than in the sentence: it is a
            // setting being reported back, not an event.
            if (snap.reverse) bits.push('rev');
            bits.push(`signal ${Number(snap.activity ?? 0).toFixed(2)}`);
            text = bits.join('   ');
        }

        if (this._info.textContent !== text) this._info.textContent = text;
    }

    _renderStatus(snap) {
        if (!this._status) return;

        // Four possible sentences, so the signature guard below stops this
        // speaking at all unless something really changed.
        let text, tone;
        if (snap.captureError) {
            text = snap.captureError;
            tone = 'bad';
        } else if (!snap.running) {
            text = 'Stopped.';
            tone = 'idle';
        } else if (snap.activity >= snap.squelch) {
            text = 'Decoding.';
            tone = 'good';
        } else {
            // Not an error: there is audio, just not enough of it in the two
            // tones to be worth printing. Saying so beats an empty pane and no
            // explanation for it.
            text = 'Listening - nothing in the tones yet.';
            tone = 'idle';
        }

        // The status line is rewritten twice a second; skip the DOM write when
        // it would say the same thing, so a screen reader is not told the same
        // sentence over and over.
        const sig = `${tone}|${text}`;
        if (sig === this._statusSig) return;
        this._statusSig = sig;

        this._status.textContent = text;
        this._status.style.color = tone === 'bad'  ? '#ff6b6b'
                                 : tone === 'good' ? '#9d9'
                                 : '#9ab';
    }

    _showError(message) {
        if (!this._status) return;
        this._statusSig = null;
        this._status.textContent = message;
        this._status.style.color = '#ff6b6b';
    }

    // ── Remembered settings ─────────────────────────────────────────────────
    //
    // Only the two that are this browser's business. Which figures table is in
    // use is the server's, because it has to survive a reload and be the same
    // in a second tab - the same reason the tuner's Mark and Shift are kept
    // there. This copy is the starting point for a fresh page, and the first
    // reply overwrites it.

    _loadSettings() {
        let s = {};
        try { s = JSON.parse(localStorage.getItem(LS_KEY) || '{}') || {}; } catch { /* blocked storage */ }
        if (this._autoScrl) this._autoScrl.checked = s.autoScroll !== false;
        if (this._figures && typeof s.figures === 'string') {
            for (const o of this._figures.options) {
                if (o.value.toLowerCase() === s.figures.toLowerCase()) { this._figures.value = o.value; break; }
            }
        }
    }

    _saveSettings() {
        try {
            localStorage.setItem(LS_KEY, JSON.stringify({
                autoScroll: this._autoScrl?.checked !== false,
                figures:    this._figures?.value || 'Ita2',
            }));
        } catch { /* ignore */ }
    }
}
