// Radio Web Control - CW Reader Panel
// Shared by Icom Web Control and Yaesu Web Control. This file is copied into
// each app's wwwroot at build time - see js/README.md. Edit it here, in the
// core, never in a wwwroot copy.
//
// Shows what the server-side CW decoder is making of the radio's receive
// audio, live. The decoding happens in Core (CwDecoderEngine); this only
// starts it, polls it and prints it. Radio-agnostic: everything radio-specific
// arrives through the /api/cw endpoints, which each app implements itself.
//
// Polling, not a socket: decoded CW arrives a few characters at a time at a
// handful of characters a second, so twice a second reads as live and costs
// nothing. Each poll sends the cursor from the previous reply and gets back
// only what is new.
//
// A word on what the text is worth. This decoder is honest about being a
// machine reading Morse out of noise: on a clean strong signal it is close to
// perfect, and on a marginal one it prints plausible-looking rubbish with no
// outward sign of the difference. The readouts along the bottom - signal
// present, SNR, whether the element timing has locked - are there so the
// operator can tell which they are looking at. That is also why the text is
// never dressed up as a transcript, and why nothing here hides low-confidence
// output: measurements on bench recordings found the decoder's own confidence
// figure to be anti-informative, high on junk and low on clean copy, so
// gating the display on it would show the rubbish and hide the good copy.

const POLL_MS      = 500;
const LS_KEY       = 'cwReaderPanel';
const MAX_RENDERED = 20000;   // characters kept in the pane

import { cwPhasor }   from './cw-phasor.js';
import { cwSpectrum } from './cw-spectrum.js';
import { renderInto } from './cw-tokens.js';

export class CwReaderPanel {
    constructor() {
        this._dialog   = null;
        this._out      = null;
        this._status   = null;
        this._statusSig = null;   // last message shown, so a redraw that says the same thing leaves the DOM alone
        this._startBtn = null;
        this._clearBtn = null;
        this._autoScrl = null;
        this._timer    = null;
        this._cursor   = 0;
        this._running  = false;
        this._text     = '';
        this._readerMode = false;
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────

    init() {
        this._dialog = document.getElementById('cwReaderDialog');
        if (!this._dialog) return;

        this._out      = document.getElementById('cwReaderOut');
        this._status   = document.getElementById('cwReaderStatus');
        this._startBtn = document.getElementById('cwReaderStartBtn');
        this._clearBtn = document.getElementById('cwReaderClearBtn');
        this._autoScrl = document.getElementById('cwReaderAutoScroll');

        this._loadSettings();
        if (this._autoScrl) {
            this._autoScrl.addEventListener('change', () => this._saveSettings());
        }

        this._startBtn?.addEventListener('click', () => this._toggleRunning());
        this._clearBtn?.addEventListener('click', () => this._clear());

        // Reader Mode sets the radio up for decoding and remembers what to put
        // back. The button is optional: the host page may not offer it, and an
        // app whose radio cannot be driven this way simply omits it.
        this._modeBtn = document.getElementById('cwReaderModeBtn');
        if (this._modeBtn) {
            this._modeBtn.addEventListener('click', () => this._toggleReaderMode());
            this._refreshReaderMode();
        }

        // The tuning figure. Off by default: it is a thing you reach for while
        // hunting for a signal, not something to leave spinning all session.
        this._phasorBox = document.getElementById('cwPhasorBox');
        this._phasorTgl = document.getElementById('cwPhasorToggle');
        this._phasorTgl?.addEventListener('change', () => {
            this._applyPhasor();
            this._saveSettings();
        });

        // Stopping the decoder when the dialog closes would throw away the
        // operator's copy mid-QSO if they closed it by accident. The decoder
        // is cheap to leave running, so closing only stops the polling.
        this._dialog.addEventListener('close', () => this._stopPolling());

        this._initDrag();
    }

    toggle() {
        if (!this._dialog) return;
        if (this._dialog.open) {
            this._dialog.close();
        } else {
            // show(), not showModal(): the whole point of reading CW is to
            // work the station, and a modal would lock the operator out of
            // the VFO and the keyer while the text was on screen.
            this._dialog.show();
            this._startPolling();
        }
    }

    // ── Decoder control ─────────────────────────────────────────────────────

    async _toggleRunning() {
        try {
            // Stopping the decoder puts the radio back. The plan says the
            // restore happens when the reader closes, but closing the dialog
            // deliberately leaves the decoder running - so Stop, not close, is
            // the moment the operator has actually finished reading. Closing
            // the panel and finding the filter had silently re-opened to 2.4
            // kHz mid-QSO would be the worse surprise of the two.
            if (this._running && this._readerMode) await this._setReaderMode(false);

            const res = await fetch(this._running ? '/api/cw/stop' : '/api/cw/start', { method: 'POST' });
            if (!res.ok) {
                const body = await res.json().catch(() => ({}));
                this._showError(body.error || `HTTP ${res.status}`);
                return;
            }
            this._apply(await res.json());
            this._startPolling();
        } catch (e) {
            this._showError(e.message);
        }
    }

    async _clear() {
        this._text = '';
        if (this._out) this._out.textContent = '';
        try {
            const res = await fetch('/api/cw/clear', { method: 'POST' });
            if (res.ok) {
                const snap = await res.json();
                // Take the server's cursor, so the next poll asks for what
                // comes after the clear rather than replaying the buffer.
                this._cursor = snap.cursor ?? 0;
                this._apply(snap, { skipText: true });
            }
        } catch (e) {
            this._showError(e.message);
        }
    }

    // ── Reader Mode ─────────────────────────────────────────────────────────
    //
    // What the operator feeds the decoder matters more than the decoding: a
    // 2.4 kHz filter full of adjacent signals defeats any decoder there is.
    // The server holds the previous settings, not this panel, so the restore
    // survives a page reload - which is the whole reason it is not three fetch
    // calls from here.

    async _toggleReaderMode() {
        await this._setReaderMode(!this._readerMode);
    }

    async _setReaderMode(on) {
        if (!this._modeBtn) return;
        this._modeBtn.disabled = true;
        try {
            const res = await fetch(`/api/cw/readermode/${on ? 'on' : 'off'}`, { method: 'POST' });
            const body = await res.json().catch(() => ({}));
            if (!res.ok) { this._showError(body.error || `HTTP ${res.status}`); return; }
            this._applyReaderMode(body);
        } catch (e) {
            this._showError(e.message);
        } finally {
            this._modeBtn.disabled = false;
        }
    }

    async _refreshReaderMode() {
        try {
            const res = await fetch('/api/cw/readermode');
            if (res.ok) this._applyReaderMode(await res.json());
        } catch { /* the button just stays as it is */ }
    }

    _applyReaderMode(status) {
        this._readerMode = !!status.on;
        if (!this._modeBtn) return;

        this._modeBtn.textContent = this._readerMode ? 'Reader Mode ON' : 'Reader Mode';
        this._modeBtn.classList.toggle('btn-warning', this._readerMode);
        this._modeBtn.classList.toggle('btn-outline-info', !this._readerMode);
        this._modeBtn.setAttribute('aria-pressed', String(this._readerMode));

        // Naming what will be put back is what makes the button safe to press.
        // An operator who has spent a while getting their filters right will
        // not hand them to a button that does not say what it is holding.
        const width = status.ifWidthHz ? `${status.ifWidthHz} Hz` : `code ${status.ifWidthCode ?? '?'}`;
        this._modeBtn.title = this._readerMode
            ? `${status.mode ?? 'CW'}, ${width}, APF ${status.apfOn ? 'on' : 'off'}. `
              + `Press again to restore ${status.restoresMode ?? 'your mode'}`
              + `${status.restoresWidth ? ` and IF width code ${status.restoresWidth}` : ''}.`
            : 'Set CW mode, a narrow filter and APF for decoding. Your current settings are put back when you stop.';
    }

    // ── Polling ─────────────────────────────────────────────────────────────

    _startPolling() {
        // Re-apply here rather than only on the toggle, so a panel reopened
        // with the figure remembered comes back with it running.
        this._applyPhasor();
        if (this._timer) return;
        this._poll();
        this._timer = setInterval(() => this._poll(), POLL_MS);
    }

    _stopPolling() {
        cwPhasor.stop();
        cwSpectrum.stop();
        if (!this._timer) return;
        clearInterval(this._timer);
        this._timer = null;
    }

    /// Show or hide the tuning aids, and start or stop their polling with them.
    ///
    /// Both live behind the one switch because they answer the two halves of
    /// the same question and in a fixed order: the spectrum shows where a
    /// signal is in the passband, and the phasor shows whether you have landed
    /// on it. Splitting them into two toggles would ask the operator to know
    /// which half of the problem they have before they have looked.
    _applyPhasor() {
        const on = !!this._phasorTgl?.checked;
        if (this._phasorBox) this._phasorBox.style.display = on ? '' : 'none';

        if (!on) { cwPhasor.stop(); cwSpectrum.stop(); return; }
        if (cwSpectrum.attach('cwSpectrumCanvas', 'cwSpectrumInfo')) cwSpectrum.start();
        if (cwPhasor.attach('cwPhasorCanvas', 'cwPhasorInfo')) cwPhasor.start();
    }

    async _poll() {
        try {
            const res = await fetch(`/api/cw/poll?since=${this._cursor}`);
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
            if (snap.truncated) {
                // The buffer rolled over between polls. Say so rather than
                // silently splicing text that is not contiguous.
                this._text += '\n[...]\n';
            }
            this._text += snap.text;
            if (this._text.length > MAX_RENDERED) {
                this._text = this._text.slice(this._text.length - MAX_RENDERED);
            }
            if (this._out) {
                // Re-render the whole buffer rather than appending: which
                // callsigns count as confirmed depends on how often they have
                // appeared, so new copy can change the markup of old copy.
                renderInto(this._out, this._text);
                if (this._autoScrl?.checked) this._out.scrollTop = this._out.scrollHeight;
            }
        }

        if (this._startBtn) {
            this._startBtn.textContent = this._running ? 'Stop' : 'Start';
            this._startBtn.classList.toggle('btn-danger',  this._running);
            this._startBtn.classList.toggle('btn-success', !this._running);
        }

        this._renderStatus(snap);
    }

    // A status line that names a missing prerequisite is much more use with a
    // way to fix it, but the fix is app-specific and this panel is shared.
    // The host app may publish `window.radioFeatureSetup`; if it has not, or
    // it does not provide this particular pop-out, the text stands on its own
    // and nothing here changes.
    _setStatus(text, setupKind, level) {
        if (!this._status) return;

        // Rebuilt on every poll tick, this would replace the button between a
        // mousedown and its mouseup and the click would never land. Nothing
        // here changes while the message does not, so leave the node alone.
        const sig = level + '|' + setupKind + '|' + text;
        if (this._statusSig === sig) return;
        this._statusSig = sig;

        this._status.textContent = text;

        // Running commentary and 'your radio audio is not working' were being
        // drawn identically - small, dim and monospaced - and the operator
        // read straight past the one that mattered. The host app styles
        // `.cwr-problem`; it is the host's business because only the host
        // knows its own palette.
        const problem = level === 'problem';
        this._status.classList.toggle('cwr-problem', problem);

        const setup = (typeof window !== 'undefined') ? window.radioFeatureSetup : null;
        if (!setupKind || !setup || typeof setup.open !== 'function') return;
        if (typeof setup.provides === 'function' && !setup.provides(setupKind)) return;

        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-sm ms-2 ' + (problem ? 'btn-warning' : 'btn-outline-primary');
        btn.textContent = 'Set it up';
        btn.addEventListener('click', () => setup.open(setupKind));
        this._status.appendChild(btn);
    }

    _renderStatus(snap) {
        if (!this._status) return;

        if (!snap.running) {
            this._setStatus('Stopped.');
            return;
        }

        // The decoder opens the radio's capture device itself, for listening
        // only, so nothing else has to be running first. It used to need a
        // live remote-audio session, and the advice here used to say so -
        // that is no longer true and must not come back.
        //
        // What it can still hit is a device that is not chosen, or one that
        // has gone away. The host reports either as a message rather than a
        // bare failure, so repeat what it said: a healthy-looking panel
        // printing nothing, with no way to tell why, is what sent the
        // operator hunting through Settings the first time.
        if (snap.captureError) {
            this._setStatus('Running, but the radio audio could not be opened. '
                + snap.captureError, 'cw-audio', 'problem');
            return;
        }

        // No error and no device is a genuine fault rather than something the
        // operator has left undone - the capture went away under us.
        if (!snap.audioDevicesOpen) {
            this._setStatus('Running, but the radio audio device is not open. '
                + 'Stop and start the reader; if it keeps happening, check the '
                + 'radio is still connected.', 'cw-audio', 'problem');
            return;
        }

        const bits = [];

        // The decoder shows no text when the marks arriving cannot be Morse,
        // so the panel has to say why. A blank transcript sitting beside a
        // healthy SNR and a confident tone reading is precisely what sent the
        // operator looking for a fault in the radio, when the radio was fine
        // and the band was not.
        if (snap.readability === 'Chatter') {
            bits.push('nothing readable - the tone is breaking up, not keying');
        } else if (snap.readability === 'Jumbled') {
            bits.push('nothing readable - more than one signal in the passband');
        } else {
            bits.push(snap.signalPresent ? 'signal' : 'no signal');
        }
        if (snap.toneHz)        bits.push(`tone ${Math.round(snap.toneHz)} Hz`);
        bits.push(`pitch ${Math.round(snap.pitchHz)} Hz`);

        // Both numbers have been shown side by side all along, and it still
        // took a bench session to notice that one was 120 Hz above the other:
        // a station reported as very quiet turned out to be sitting on the
        // skirt of a 300 Hz filter. The reader already computes the
        // correction, so say it in words rather than leaving it to be
        // inferred from two readings that look equally healthy.
        //
        // Below about 25 Hz this is tracker jitter rather than mistuning -
        // the tone wanders that much on a perfectly centred signal - so
        // saying nothing is the honest answer there.
        if (Number.isFinite(snap.zeroInOffsetHz) && Math.abs(snap.zeroInOffsetHz) >= 25) {
            const hz = Math.round(snap.zeroInOffsetHz);
            bits.push(`off pitch - tune ${hz > 0 ? '+' : ''}${hz} Hz`);
        }

        // Say plainly when the radio has not told us a width, because the
        // search window is then a default rather than the passband.
        bits.push(snap.filterWidthHz
            ? `filter ${snap.filterWidthHz} Hz`
            : 'filter unknown');
        // The window is sized from the filter width, but only over part of
        // the range: it is half the width clamped to 100..500 Hz. So a 2.4 kHz
        // SSB filter implies +/-1200 and receives +/-500, and a 100 Hz CW
        // filter implies +/-50 and receives +/-100. Printing the width and the
        // window side by side without saying that reads as though the width
        // set the window, and an operator who widens the filter to help the
        // reader find a station is then owed an explanation of why nothing
        // changed. Above the clamp a wider window would only offer more wrong
        // tones to lock onto - that regime is what multi-signal decode is for.
        const win  = Math.round(snap.searchWindowHz);
        const half = snap.filterWidthHz ? snap.filterWidthHz / 2 : null;
        let note = '';
        if (half !== null && half > win + 1)      note = ' (clamped)';
        else if (half !== null && half < win - 1) note = ' (wider than the filter)';
        bits.push(`search +/-${win} Hz${note}`);

        // Speed is left out unless the reader says the estimate is worth
        // reporting. It is the reading that misleads most: with the detector
        // chattering it rails at the maximum, which looks like a very fast
        // operator rather than a decoder with nothing to decode.
        //
        // isLocked is the whole test now. It used to be paired with a
        // readability check here because isLocked only counted marks and would
        // happily lock onto noise; it now carries the readability condition
        // itself, with a hold so the speed does not blink off across QSB. The
        // check that was here also let Unknown through, so a quiet band showed
        // a speed for the first few seconds of every session.
        if (snap.wordsPerMinute && snap.isLocked) bits.push(`${snap.wordsPerMinute.toFixed(0)} wpm`);
        if (Number.isFinite(snap.snrDb)) bits.push(`SNR ${snap.snrDb.toFixed(0)} dB`);
        if (snap.droppedFrames)  bits.push(`${snap.droppedFrames} frames dropped`);

        this._setStatus(bits.join('  |  '));
    }

    _showError(message) {
        this._setStatus(`Error: ${message}`, null, 'problem');
    }

    // ── Settings ────────────────────────────────────────────────────────────

    _loadSettings() {
        try {
            const raw = localStorage.getItem(LS_KEY);
            if (!raw) return;
            const s = JSON.parse(raw);
            if (this._phasorTgl && typeof s.phasor === 'boolean') {
                this._phasorTgl.checked = s.phasor;
            }
            if (this._autoScrl && typeof s.autoScroll === 'boolean') {
                this._autoScrl.checked = s.autoScroll;
            }
            if (this._dialog && typeof s.left === 'number' && typeof s.top === 'number') {
                // A <dialog> shown with show() is position:absolute, so it is placed
                // against the document and scrolls with it. Every coordinate here is a
                // viewport one - getBoundingClientRect on the way out, the stored value
                // on the way back - so pinning it fixed is what makes the two agree.
                // Left absolute, grabbing the header moved the panel up by exactly the
                // page's scroll offset, i.e. it jumped to the top of the document.
                this._dialog.style.position = 'fixed';
                this._dialog.style.margin   = '0';
                this._dialog.style.left   = `${s.left}px`;
                this._dialog.style.top    = `${s.top}px`;
            }
        } catch {
            // Corrupt or unavailable storage is not worth failing the panel for.
        }
    }

    _saveSettings() {
        try {
            const rect = this._dialog?.getBoundingClientRect();
            localStorage.setItem(LS_KEY, JSON.stringify({
                autoScroll: this._autoScrl ? this._autoScrl.checked : true,
                phasor:     this._phasorTgl ? this._phasorTgl.checked : false,
                left: rect ? Math.round(rect.left) : undefined,
                top:  rect ? Math.round(rect.top)  : undefined,
            }));
        } catch {
            // Private browsing, quota, storage disabled - all fine to ignore.
        }
    }

    // ── Drag ─────────────────────────────────────────────────────────────────────

    // Same behaviour as the DX Spots panel: a non-modal dialog that cannot be
    // moved will eventually sit over whatever the operator needs to see.
    _initDrag() {
        const header = this._dialog.querySelector('.cwr-header');
        if (!header) return;
        header.addEventListener('mousedown', (e) => {
            if (e.target.closest('button, input, label')) return;
            const rect  = this._dialog.getBoundingClientRect();
            const origX = e.clientX, origY = e.clientY;
            const baseL = rect.left,  baseT = rect.top;

            // A <dialog> shown with show() is position:absolute, so it is placed
            // against the document and scrolls with it. Every coordinate here is a
            // viewport one - getBoundingClientRect on the way out, the stored value
            // on the way back - so pinning it fixed is what makes the two agree.
            // Left absolute, grabbing the header moved the panel up by exactly the
            // page's scroll offset, i.e. it jumped to the top of the document.
            this._dialog.style.position = 'fixed';
            this._dialog.style.margin   = '0';
            this._dialog.style.left     = `${baseL}px`;
            this._dialog.style.top      = `${baseT}px`;

            const onMove = (ev) => {
                let l = baseL + (ev.clientX - origX);
                let t = baseT + (ev.clientY - origY);
                l = Math.max(-rect.width + 40, Math.min(window.innerWidth - 40, l));
                t = Math.max(0,                 Math.min(window.innerHeight - 40, t));
                this._dialog.style.left = `${l}px`;
                this._dialog.style.top  = `${t}px`;
            };
            const onUp = () => {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup',   onUp);
                this._saveSettings();
            };
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup',   onUp);
            e.preventDefault();
        });
    }
}
