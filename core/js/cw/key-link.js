// Radio Web Control - CW key link
// Shared by Icom Web Control and Yaesu Web Control. This file is copied into
// each app's wwwroot at build time - see js/README.md. Edit it here, in the
// core, never in a wwwroot copy.
//
// Carries a browser keyer's elements to the shack PC, where CwKeyingPlayer
// plays them after a jitter buffer. Each element goes as two timestamped
// events (key down at its start, key up at its end) with sequence numbers
// that run on without gaps, so the shack can tell when one is lost.
//
// Every packet repeats the last few events, so one lost packet costs nothing;
// the shack drops events it already has. A ping goes every 100 ms whether or
// not anything is keyed: the shack lets go of the key if it hears nothing for
// 0.8 s while the key is down. abort() tells it to let go now.
//
// The transport is the caller's: send(packet) gets
//   { now, events: [{ seq, down, t }], abort }
// with times on this page's performance.now() clock.

export class KeyLink {
    /**
     * @param {object} o
     * @param {(packet: object) => void} o.send   delivers one packet; must not throw
     * @param {number} [o.repeat=8]  events carried in each packet
     * @param {number} [o.pingMs=100]
     * @param {() => number} [o.clock]  defaults to performance.now
     */
    constructor({ send, repeat = 8, pingMs = 100, clock = () => performance.now() }) {
        this._send = send;
        this._repeat = repeat;
        this._pingMs = pingMs;
        this._clock = clock;
        this._seq = 0;
        this._recent = [];
        this._timer = null;
    }

    get running() { return this._timer !== null; }

    /** Starts the pings. Elements are only sent while running. */
    start() {
        if (this._timer !== null) return;
        this._timer = setInterval(() => this.ping(), this._pingMs);
        this.ping();
    }

    /** Stops the pings, telling the shack to let go first. */
    stop() {
        if (this._timer === null) return;
        this.abort();
        clearInterval(this._timer);
        this._timer = null;
    }

    /** One keyed element, times on the page clock (start may be a few ms ahead). */
    element(start, end) {
        if (this._timer === null) return;
        this._push(true, start);
        this._push(false, end);
        this._packet(false);
    }

    /** Tells the shack to let go of the key now and drop what it has queued. */
    abort() {
        this._recent = [];
        this._packet(true);
    }

    /** The link is alive; repeats the latest events in case a packet was lost. */
    ping() { this._packet(false); }

    _push(down, t) {
        this._recent.push({ seq: this._seq++, down, t });
        if (this._recent.length > this._repeat) this._recent.shift();
    }

    _packet(abort) {
        this._send({ now: this._clock(), events: abort ? [] : this._recent.slice(), abort });
    }
}
