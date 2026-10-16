// tx-timeout.js - warn when the transmitter has been on too long.
//
// A stuck PTT, a VOX that will not drop or a keyer left running looks like
// nothing on the screen until someone notices the PA is hot. This watches the
// app's transmitting flag and trips once TX has been on, unbroken, for longer
// than the limit. It does not unkey the radio - it only says so; what the
// page does about it (a banner, a tone) is the page's.
//
// The limit is kept in localStorage under the key the main pages already use
// ('tx_timeout_seconds'), so the radio panel and the classic page share one
// setting. Radio-agnostic: all it needs is "is it transmitting".

export const TX_TIMEOUT_KEY = 'tx_timeout_seconds';
export const TX_TIMEOUT_DEFAULT_S = 120;
export const TX_TIMEOUT_MIN_S = 5;
export const TX_TIMEOUT_MAX_S = 3600;

/** A limit as the operator typed or stepped it, put inside the range. */
export function clampTimeoutSeconds(s) {
    const n = parseInt(s, 10);
    if (!Number.isFinite(n) || n <= 0) return TX_TIMEOUT_DEFAULT_S;
    return Math.max(TX_TIMEOUT_MIN_S, Math.min(TX_TIMEOUT_MAX_S, n));
}

/** 90 -> "1:30", 3600 -> "60:00". */
export function timeoutText(s) {
    const n = Math.max(0, Math.floor(Number(s) || 0));
    return Math.floor(n / 60) + ':' + String(n % 60).padStart(2, '0');
}

/** The step the - / + keys move the limit by: finer near the bottom. */
export function stepTimeout(s, dir) {
    const cur = clampTimeoutSeconds(s);
    const step = (dir > 0 ? cur >= 120 : cur > 120) ? 60 : (dir > 0 ? cur >= 30 : cur > 30) ? 15 : 5;
    return Math.max(TX_TIMEOUT_MIN_S, Math.min(TX_TIMEOUT_MAX_S, cur + (dir > 0 ? step : -step)));
}

/** The banner's words, as the main page has them. */
export function txTimeoutWarning(limitSeconds) {
    return `\u26A0 TX has been ON for more than ${clampTimeoutSeconds(limitSeconds)} seconds \u2014 check your microphone, keyer or VOX!`;
}

/** One short 440 Hz square-wave beep, the main page's warning tone. Silent
 *  where the browser has not allowed audio yet. */
export function txTimeoutBeep() {
    try {
        const ac = new (window.AudioContext || window.webkitAudioContext)();
        const o = ac.createOscillator();
        const g = ac.createGain();
        o.type = 'square';
        o.frequency.setValueAtTime(440, ac.currentTime);
        g.gain.setValueAtTime(0.0001, ac.currentTime);
        g.gain.exponentialRampToValueAtTime(0.25, ac.currentTime + 0.02);
        g.gain.exponentialRampToValueAtTime(0.0001, ac.currentTime + 0.25);
        o.connect(g).connect(ac.destination);
        o.start();
        o.stop(ac.currentTime + 0.3);
        o.onended = () => { try { ac.close(); } catch { /* already closed */ } };
    } catch { /* audio not allowed */ }
}

const storage = {
    get(k) { try { return localStorage.getItem(k); } catch { return null; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { /* private window */ } },
};

export class TxTimeout {
    /**
     * @param {object} [opts]
     * @param {(s: {tx:boolean, tripped:boolean, txSeconds:number, limit:number}) => void} [opts.onChange]
     *        called when TX starts or stops, when it trips, when it is dismissed,
     *        when the limit changes, and once a second while transmitting
     * @param {() => number} [opts.now]   for tests
     * @param {object} [opts.timers]      { setInterval, clearInterval } for tests
     * @param {object} [opts.store]       { get(k), set(k, v) } for tests
     */
    constructor(opts = {}) {
        this._onChange = opts.onChange ?? (() => {});
        this._now = opts.now ?? (() => Date.now());
        this._timers = opts.timers ?? { setInterval: (f, ms) => setInterval(f, ms), clearInterval: id => clearInterval(id) };
        this._store = opts.store ?? storage;
        this._tx = false;
        this._since = 0;
        this._tripped = false;
        this._dismissed = false;
        this._tick = null;
        // Another tab (the classic page, a second panel) changed the limit.
        try {
            window.addEventListener('storage', e => { if (e.key === TX_TIMEOUT_KEY) this._emit(); });
        } catch { /* no window: tests */ }
    }

    get limit() { return clampTimeoutSeconds(this._store.get(TX_TIMEOUT_KEY)); }
    set limit(s) {
        this._store.set(TX_TIMEOUT_KEY, String(clampTimeoutSeconds(s)));
        this._check();
        this._emit();
    }

    /** Whole seconds of the current transmission; 0 on receive. */
    get txSeconds() { return this._tx ? Math.floor((this._now() - this._since) / 1000) : 0; }
    /** Over the limit and not dismissed. */
    get tripped() { return this._tripped && !this._dismissed; }

    /** Feed it every change of the transmitting flag; repeats are harmless. */
    setTransmitting(on) {
        on = !!on;
        if (on === this._tx) return;
        this._tx = on;
        this._tripped = false;
        this._dismissed = false;
        if (on) {
            this._since = this._now();
            this._tick = this._timers.setInterval(() => { this._check(); this._emit(); }, 1000);
        } else if (this._tick != null) {
            this._timers.clearInterval(this._tick);
            this._tick = null;
        }
        this._emit();
    }

    /** Hide the warning for the rest of this transmission. */
    dismiss() {
        if (!this._tripped || this._dismissed) return;
        this._dismissed = true;
        this._emit();
    }

    _check() {
        this._tripped = this._tx && this.txSeconds >= this.limit;
    }

    _emit() {
        try {
            this._onChange({ tx: this._tx, tripped: this.tripped, txSeconds: this.txSeconds, limit: this.limit });
        } catch (e) { console.warn('tx-timeout:', e); }
    }
}
