// FT8 Tiles (issue #332): which station sits in which tile.
//
// A tile keeps its station from one poll to the next, so an operator who is
// slow to reach a tile, by switch scanning or eye gaze, doesn't find another
// station there when they get to it. A new station takes the first empty
// tile. A station the server stops listing frees its tile.
//
// Pure logic: no DOM, no fetch.

export class TileSlots {
    constructor() {
        /** @type {(string|null)[]} callsign per slot, null = empty */
        this.slots = [];
    }

    /**
     * @param {string[]} calls the stations the server lists now, in its order
     * @returns {(string|null)[]} the slot array after the update
     */
    update(calls) {
        const wanted = new Set(calls);
        // Free the tiles of stations no longer listed.
        this.slots = this.slots.map(c => (c !== null && wanted.has(c) ? c : null));
        const placed = new Set(this.slots.filter(c => c !== null));
        for (const call of calls) {
            if (placed.has(call)) continue;
            const free = this.slots.indexOf(null);
            if (free >= 0) this.slots[free] = call;
            else this.slots.push(call);
            placed.add(call);
        }
        // Drop empty tiles from the end so the page count shrinks again.
        while (this.slots.length > 0 && this.slots[this.slots.length - 1] === null) this.slots.pop();
        return this.slots;
    }

    /**
     * Puts the stations in the order given, from the first tile, once: the
     * Sort tile. After this, update() keeps each station in its new tile as
     * before, so nothing moves again until the operator sorts again.
     * Stations not in `ordered` keep no tile; update() places them next poll.
     * @param {string[]} ordered
     */
    arrange(ordered) {
        const listed = new Set(this.slots.filter(c => c !== null));
        const seen = new Set();
        this.slots = [];
        for (const call of ordered) {
            if (!listed.has(call) || seen.has(call)) continue;
            this.slots.push(call);
            seen.add(call);
        }
        return this.slots;
    }

    clear() {
        this.slots = [];
    }

    pageCount(perPage) {
        return Math.max(1, Math.ceil(this.slots.length / perPage));
    }

    page(index, perPage) {
        const out = this.slots.slice(index * perPage, (index + 1) * perPage);
        while (out.length < perPage) out.push(null);
        return out;
    }
}

// The Sort tile steps through these. "heard" is the order the server lists
// them in (stations calling you first, then as first heard).
export const SORT_MODES = ["heard", "strength", "newest", "callsign"];
export const SORT_LABEL = {
    heard: "as heard", strength: "by strength", newest: "newest first", callsign: "by callsign",
};

/**
 * The callsigns in the order a sort mode puts them. Stations calling you stay
 * first in every mode: they are the ones to answer.
 * @param {{call:string, kind:string, snr:number, ageSeconds:number}[]} stations
 * @param {string} mode one of SORT_MODES
 * @returns {string[]}
 */
export function sortCalls(stations, mode) {
    const list = stations.map((s, i) => ({ s, i }));
    const you = x => (x.s.kind === "you" ? 0 : 1);
    const by = {
        heard: (a, b) => a.i - b.i,
        strength: (a, b) => b.s.snr - a.s.snr || a.i - b.i,
        newest: (a, b) => a.s.ageSeconds - b.s.ageSeconds || a.i - b.i,
        callsign: (a, b) => a.s.call.localeCompare(b.s.call),
    }[mode] || ((a, b) => a.i - b.i);
    list.sort((a, b) => you(a) - you(b) || by(a, b));
    return list.map(x => x.s.call);
}

/**
 * The status line's text: what WSJT-Z is doing, in plain words.
 * @param {{connected:boolean, transmitting:boolean, txEnabled:boolean, dxCall:string, txMessage?:string}} d
 * @returns {{state:string, cls:string, line:string}}
 */
export function txStatus(d) {
    const msg = (d.txMessage || "").trim();
    const to = d.dxCall ? ` to ${d.dxCall}` : (msg.startsWith("CQ") ? ": calling CQ" : "");
    if (!d.connected) return { state: "WSJT-Z not connected", cls: "ft-off", line: "" };
    if (d.transmitting) return { state: "Transmitting", cls: "ft-tx", line: `Transmitting${to}` + (msg ? ` · sending ${msg}` : "") };
    if (d.txEnabled) return { state: "Waiting to transmit", cls: "ft-armed", line: `Transmits next period${to}` + (msg ? ` · will send ${msg}` : "") };
    return { state: "Receiving", cls: "ft-rx", line: "Not transmitting" + (msg ? ` · last sent ${msg}` : "") };
}

/**
 * True while WSJT-Z (or WS) is calling CQ: Enable Tx on or transmitting, no
 * station being worked, and the Tx message a CQ. Lights the Call CQ tile.
 * Works whatever turned the CQ on, the tile or a click in WSJT-Z itself.
 */
export function callingCq(d) {
    if (!d.connected || d.dxCall) return false;
    if (!d.transmitting && !d.txEnabled) return false;
    return /^CQ\s/.test((d.txMessage || "").trim());
}

/**
 * The words on the Call CQ tile. cqRun is from /api/wsjtx/calls: sent is the
 * CQs in a row with no answer, limit the most allowed (0 = no limit), and
 * stoppedSecondsAgo when a run last stopped at its limit.
 */
export function cqTileText(cq, waiting, cqRun) {
    const run = cqRun || {};
    const limit = run.limit || 0;
    if (cq) {
        return {
            big: limit && run.active ? `Calling CQ ${Math.max(1, run.sent)} of ${limit}` : "Calling CQ",
            small: "Stop transmit ends it",
        };
    }
    if (waiting) return { big: "CQ next period", small: "Stop transmit ends it" };
    if (run.stoppedSecondsAgo != null && run.stoppedSecondsAgo < 120 && limit) {
        return { big: "Call CQ", small: `stopped after ${limit}, no answer` };
    }
    return { big: "Call CQ", small: limit ? `stops after ${limit} if no answer` : "until someone answers" };
}

/** How long the last transmission's levels stay on the status line. */
export const TX_LEVELS_SHOW_SECONDS = 600;

/**
 * The ALC and SWR on the status line: the transmission now, or the last one
 * for 10 minutes. For FT8 the ALC should sit near 0; at or over the TX alert
 * limit it says what to do. Returns { text, high } or null.
 */
export function txLevelsText(txPeaks) {
    if (!txPeaks) return null;
    const now = txPeaks.current;
    const p = now || (txPeaks.last && txPeaks.last.ageSeconds < TX_LEVELS_SHOW_SECONDS ? txPeaks.last : null);
    if (!p) return null;
    const alcHigh = txPeaks.alcLimit != null && p.alc >= txPeaks.alcLimit;
    const swrHigh = txPeaks.swrLimit != null && p.swr >= txPeaks.swrLimit;
    const parts = [
        `${now ? "" : "Last TX: "}ALC ${p.alc}%${alcHigh ? " too high: turn Pwr down in WSJT-Z" : now ? "" : " good"}`,
        `SWR ${p.swr.toFixed(1)}:1${swrHigh ? " high" : ""}`,
    ];
    return { text: parts.join(" · "), high: alcHigh || swrHigh };
}

/**
 * Remembers the last QSO for the status line: the station WSJT-Z was working,
 * and whether WSJT-Z logged it. A QSO counts as over when WSJT-Z logs it or
 * moves on to another station (or none).
 */
export class LastQso {
    constructor() {
        this.dx = "";
        this.last = null;        // { call, logged }
        this.loggedCalls = new Set();
        this.loggedKey = "";
    }

    /** @param {{dxCall:string, logged?:{call:string, frequency?:number, mode?:string, sent?:string, received?:string}|null}} d */
    update(d) {
        const dx = d.dxCall || "";
        // Working a station starts a new QSO with it: an earlier log doesn't count.
        if (dx && dx !== this.dx) this.loggedCalls.delete(dx);
        // The QSO with the station WSJT-Z was working ends when it moves on.
        if (this.dx && dx !== this.dx) {
            this.last = { call: this.dx, logged: this.loggedCalls.has(this.dx) };
        }
        this.dx = dx;
        // The server repeats the last logged QSO for a minute: count it once.
        const q = d.logged;
        const key = q && q.call ? [q.call, q.frequency, q.mode, q.sent, q.received].join("|") : "";
        if (key && key !== this.loggedKey) {
            this.loggedKey = key;
            this.loggedCalls.add(q.call);
            this.last = { call: q.call, logged: true };
        }
        return this.last;
    }

    text() {
        if (!this.last) return "";
        return `Last QSO: ${this.last.call} · ` + (this.last.logged ? "logged ✓" : "not logged");
    }
}
