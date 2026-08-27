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
        this._startBtn = null;
        this._clearBtn = null;
        this._autoScrl = null;
        this._timer    = null;
        this._cursor   = 0;
        this._running  = false;
        this._text     = '';
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

    _renderStatus(snap) {
        if (!this._status) return;

        if (!snap.running) {
            this._status.textContent = 'Stopped.';
            return;
        }

        // The decoder listens to the remote-audio capture rather than opening
        // a device of its own, so it hears nothing until an audio session is
        // actually connected. Without saying so the panel looks healthy and
        // prints nothing, and the operator has no way to tell why.
        //
        // Two different states, and telling them apart matters. Enabling
        // Remote Audio in Settings only makes the Remote Audio bar appear; it
        // does not connect anything. Saying 'start remote audio' to an
        // operator who has already switched it on sends them back to the
        // setting they have already set, which is exactly what happened the
        // first time this panel met a radio.
        if (!snap.audioSessionActive) {
            this._status.textContent =
                'Running, but no audio is reaching it. The decoder listens to the '
                + 'remote audio stream: press the green telephone button on the Remote '
                + 'Audio bar to connect. (If that bar is not showing, switch Remote '
                + 'Audio on in Settings first.)';
            return;
        }

        // Session connected but no device open is a genuine fault rather than
        // something the operator has left undone.
        if (!snap.audioDevicesOpen) {
            this._status.textContent =
                'Remote audio is connected but no capture device opened - check the '
                + 'audio device selection in Settings.';
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
        bits.push(`search +/-${Math.round(snap.searchWindowHz)} Hz`);

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

        this._status.textContent = bits.join('  |  ');
    }

    _showError(message) {
        if (this._status) this._status.textContent = `Error: ${message}`;
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
                this._dialog.style.margin = '0';
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

            this._dialog.style.margin = '0';
            this._dialog.style.left   = `${baseL}px`;
            this._dialog.style.top    = `${baseT}px`;

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
