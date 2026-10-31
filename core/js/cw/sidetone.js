// Sidetone for keying done in the page: a tone that follows Morse elements.
//
// Elements come with start and end times in performance.now() milliseconds,
// usually a little ahead (see iambic-keyer.js). They are scheduled on the
// audio clock, so the tone is as steady as the keyer's timing and not as
// jittery as a timer. The oscillator runs all the time behind a gain of zero;
// starting and stopping it per element would click.
//
// The browser only lets audio start from a click or key press, so start()
// has to be called from one.

const RAMP_S = 0.005;   // 5 ms rise and fall: no key clicks in the headphones

export class Sidetone {
    constructor({ pitchHz = 600, volume = 0.3 } = {}) {
        this._pitch = pitchHz;
        this._volume = volume;
        this._ctx = null;
        this._osc = null;
        this._gain = null;
    }

    start() {
        if (!this._ctx) {
            this._ctx = new AudioContext({ latencyHint: 'interactive' });
            this._osc = this._ctx.createOscillator();
            this._osc.type = 'sine';
            this._osc.frequency.value = this._pitch;
            this._gain = this._ctx.createGain();
            this._gain.gain.value = 0;
            this._osc.connect(this._gain).connect(this._ctx.destination);
            this._osc.start();
        }
        return this._ctx.resume();
    }

    get running() { return this._ctx?.state === 'running'; }

    set pitch(hz) {
        this._pitch = hz;
        if (this._osc) this._osc.frequency.setValueAtTime(hz, this._ctx.currentTime);
    }

    set volume(v) { this._volume = Math.max(0, Math.min(1, v)); }

    // One element, start and end in performance.now() milliseconds.
    element(startMs, endMs) {
        if (!this.running || this._volume <= 0) return;
        const now = this._ctx.currentTime;
        const s = Math.max(now, this._toAudioTime(startMs));
        const e = Math.max(s + RAMP_S, this._toAudioTime(endMs));
        const g = this._gain.gain;
        g.setValueAtTime(0, s);
        g.linearRampToValueAtTime(this._volume, s + RAMP_S);
        g.setValueAtTime(this._volume, e);
        g.linearRampToValueAtTime(0, e + RAMP_S);
    }

    // Silence now, dropping anything scheduled.
    cancel() {
        if (!this._ctx) return;
        const now = this._ctx.currentTime, g = this._gain.gain;
        g.cancelScheduledValues(now);
        g.setValueAtTime(g.value, now);
        g.linearRampToValueAtTime(0, now + RAMP_S);
    }

    _toAudioTime(ms) {
        return this._ctx.currentTime + (ms - performance.now()) / 1000;
    }
}
