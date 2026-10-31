// Iambic keyer: paddle presses in, Morse elements out.
//
// This is the keyer for a paddle used through the browser (a HaliKey MIDI,
// say). The radio's own keyer cannot be used remotely: it would time the
// elements from paddle contacts that have crossed the network, and network
// jitter would be in every dit. So the keyer runs here, next to the paddle,
// and what goes on is finished elements with their times.
//
// Behaviour is the usual Curtis iambic:
//   - a paddle held sends a string of its element;
//   - both held (squeeze) alternates dit and dah;
//   - the opposite paddle pressed while an element is being sent is
//     remembered and sent next, even if it is let go before then.
// Mode B remembers a squeeze too: let go of both during an element and one
// more element of the other kind follows. Mode A stops after the element
// when both are let go, and remembers the other paddle only when it was
// pressed on its own.
//
// Elements are reported with start and end times on the clock's timebase
// (performance.now() in the browser), often a few milliseconds ahead, so a
// sidetone or a transmitter can be scheduled on time rather than after a
// timer's lateness.
//
// No radio and no audio in here.

import { MORSE } from './morse-timing.js';

export const DIT = 'dit';
export const DAH = 'dah';
const other = p => (p === DIT ? DAH : DIT);

// How long before the end of the space after an element the keyer wakes to
// choose the next one, so that element can be scheduled to start on time.
// A press in those last milliseconds, after the keyer has gone idle, still
// starts no earlier than the space allows.
const LOOKAHEAD_MS = 8;

const defaultClock = {
    now: () => performance.now(),
    setTimeout: (fn, ms) => setTimeout(fn, ms),
    clearTimeout: id => clearTimeout(id),
};

export class IambicKeyer {
    // onElement({ paddle, start, end })  an element to send
    // onStop(atMs)                       stop() cut an element short: silence now
    // onStuck(paddle)                    a paddle held longer than stuckMs is
    //                                    treated as let go until it really is
    constructor({
        wpm = 20, mode = 'B', stuckMs = 3000,
        onElement = () => {}, onStop = () => {}, onStuck = () => {},
        clock = defaultClock,
    } = {}) {
        this.wpm = wpm;
        this.mode = mode === 'A' ? 'A' : 'B';
        this.stuckMs = stuckMs;
        this._onElement = onElement;
        this._onStop = onStop;
        this._onStuck = onStuck;
        this._clock = clock;
        this._held = { dit: false, dah: false };
        this._stuck = { dit: false, dah: false };
        this._stuckTimer = { dit: null, dah: null };
        this._memory = { dit: false, dah: false };
        this._current = null;    // paddle of the element being sent; null when idle
        this._elementEnd = 0;
        this._spaceEnd = 0;      // the earliest the next element may start
        this._timer = null;
    }

    get unitMs() { return 1200 / this.wpm; }
    get busy() { return this._current !== null; }
    isDown(p) { return this._held[p] && !this._stuck[p]; }

    // A paddle pressed (down true) or let go.
    paddle(p, down) {
        if (p !== DIT && p !== DAH) return;
        down = !!down;
        if (this._held[p] === down) return;
        this._held[p] = down;
        if (this._stuckTimer[p] !== null) {
            this._clock.clearTimeout(this._stuckTimer[p]);
            this._stuckTimer[p] = null;
        }
        if (!down) {
            this._stuck[p] = false;
            return;
        }
        if (this.stuckMs > 0) this._stuckTimer[p] = this._clock.setTimeout(() => this._stuckNow(p), this.stuckMs);
        if (this.busy) {
            // The same paddle again counts once its element has finished
            // sounding: a tap in the space is the next element, not a bounce.
            if (p === this._current) {
                if (this._clock.now() >= this._elementEnd) this._memory[p] = true;
            } else if (this.mode === 'B' || !this.isDown(this._current)) {
                this._memory[p] = true;
            }
        } else {
            this._start(p, Math.max(this._clock.now(), this._spaceEnd));
        }
    }

    // Lets go of everything at once and cuts off any element being sent.
    stop() {
        if (this._timer !== null) this._clock.clearTimeout(this._timer);
        this._timer = null;
        const now = this._clock.now();
        this._memory.dit = this._memory.dah = false;
        if (this._current !== null && this._elementEnd > now) this._onStop(now);
        this._current = null;
        this._spaceEnd = now;
    }

    _stuckNow(p) {
        this._stuckTimer[p] = null;
        this._stuck[p] = true;
        this._memory[p] = false;
        this._onStuck(p);
    }

    _start(p, at) {
        const len = (p === DIT ? 1 : 3) * this.unitMs;
        this._current = p;
        this._memory.dit = this._memory.dah = false;
        this._elementEnd = at + len;
        this._spaceEnd = at + len + this.unitMs;
        this._onElement({ paddle: p, start: at, end: this._elementEnd });
        const wait = Math.max(0, this._spaceEnd - LOOKAHEAD_MS - this._clock.now());
        this._timer = this._clock.setTimeout(() => this._next(), wait);
    }

    _next() {
        this._timer = null;
        const last = this._current, opp = other(last);
        let p = null;
        if (this._memory[opp] || this.isDown(opp)) p = opp;
        else if (this._memory[last] || this.isDown(last)) p = last;
        if (p) this._start(p, Math.max(this._spaceEnd, this._clock.now()));
        else this._current = null;
    }
}

const FROM_MORSE = Object.fromEntries(Object.entries(MORSE).map(([c, m]) => [m, String(c)]));

// Reads the keyer's own elements back as text, to show what was sent. The
// timing is exact (it is the keyer's), so plain thresholds do: a gap of 2
// units or more ends a character (nominal 3), 5 or more a word (nominal 7).
// A pattern not in the table comes out in brackets, e.g. [.-.-..].
export class ElementDecoder {
    constructor(onText) {
        this._onText = onText;
        this._code = '';
        this._lastEnd = -Infinity;
        this._unit = 60;
        this._spaced = true;     // no space before the first character
    }

    element(paddle, start, end, unitMs) {
        this.idle(start);
        this._code += paddle === DIT ? '.' : '-';
        this._lastEnd = end;
        this._unit = unitMs;
        this._spaced = false;
    }

    // Call now and then while nothing is being sent, so the last character
    // and the word space come out without waiting for the next element.
    idle(now) {
        const gap = now - this._lastEnd;
        if (this._code && gap >= 2 * this._unit) {
            this._onText(FROM_MORSE[this._code] ?? `[${this._code}]`);
            this._code = '';
        }
        if (!this._spaced && !this._code && gap >= 5 * this._unit) {
            this._onText(' ');
            this._spaced = true;
        }
    }
}
