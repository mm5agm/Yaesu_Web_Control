// Radio Web Control - Spot Map data
// Shared by Icom Web Control and Yaesu Web Control. This file is copied into
// each app's wwwroot at build time - see js/README.md. Edit it here, in the
// core, never in a wwwroot copy.
//
// The pure half of the Spot Map: grid squares to positions, distances, bands,
// and the store of who heard whom. No DOM, no network, so it is tested in
// tests/js/spot-data.test.mjs. spot-map-page.js draws what this holds.
//
// Two kinds of spot, both from PSK Reporter:
//   heardMe - the operator transmitted and another station reported it;
//   iHeard  - the operator's station reported hearing another.
//
// A station with no grid can still go on the map at an approximate position,
// the centre of its country from the callsign (countryFor, from cty-data.js).
// Such a spot has ll and approx (the country) and an empty grid.

import { CTY } from './cty-data.js';

export const KINDS = ['heardMe', 'iHeard'];

/** A Maidenhead locator (4 or 6 characters) to [lat, lon] at the square's centre, or null. */
export function gridToLatLon(grid) {
    const g = String(grid ?? '').trim().toUpperCase();
    if (g.length < 4) return null;
    if (!/^[A-R]{2}[0-9]{2}/.test(g)) return null;
    const A = 65;
    let lon = (g.charCodeAt(0) - A) * 20 - 180 + Number(g[2]) * 2;
    let lat = (g.charCodeAt(1) - A) * 10 - 90 + Number(g[3]);
    if (g.length >= 6 && /^[A-X]{2}$/.test(g.slice(4, 6))) {
        lon += (g.charCodeAt(4) - A) * (2 / 24) + 1 / 24;
        lat += (g.charCodeAt(5) - A) * (1 / 24) + 1 / 48;
    } else {
        lon += 1;
        lat += 0.5;
    }
    return [lat, lon];
}

let ctyTable = null;
function cty() {
    if (ctyTable) return ctyTable;
    const prefixes = new Map(), exact = new Map();
    let longest = 0;
    for (const line of CTY.split('\n')) {
        const [name, lat, lon, toks] = line.split('|');
        if (!toks) continue;
        for (const tok of toks.split(',')) {
            const [key, at] = tok.split('@');
            const [la, lo] = at ? at.split('/') : [lat, lon];
            const c = { name, lat: Number(la), lon: Number(lo) };
            if (key.startsWith('=')) exact.set(key.slice(1), c);
            else { prefixes.set(key, c); longest = Math.max(longest, key.length); }
        }
    }
    return (ctyTable = { prefixes, exact, longest });
}

const SUFFIXES = new Set(['P', 'M', 'A', 'QRP', 'LH', 'B', 'R', 'J', 'T']);

/**
 * The country a callsign belongs to, as { name, lat, lon } at its centre
 * (east positive), or null: an exact match first, then the longest prefix.
 * A portable prefix (EA8/G4ABC) wins; /P, /M, /QRP and the like are ignored;
 * /MM and /AM are at sea or in the air, so nowhere.
 */
export function countryFor(call) {
    const c = String(call ?? '').trim().toUpperCase().replace(/^<|>$/g, '');
    if (!c) return null;
    const t = cty();
    if (t.exact.has(c)) return t.exact.get(c);
    let parts = c.split('/').filter(Boolean);
    if (parts.some(p => p === 'MM' || p === 'AM')) return null;
    parts = parts.filter(p => !SUFFIXES.has(p) && !/^\d$/.test(p));
    if (!parts.length) return null;
    let base = parts[0];
    if (parts.length > 1) {
        if (t.exact.has(parts.join('/'))) return t.exact.get(parts.join('/'));
        // The shorter part is the prefix where the station is operating.
        base = parts.reduce((a, b) => (b.length < a.length ? b : a));
        if (t.exact.has(base)) return t.exact.get(base);
    }
    for (let n = Math.min(base.length, t.longest); n > 0; n--) {
        const hit = t.prefixes.get(base.slice(0, n));
        if (hit) return hit;
    }
    return null;
}

/** A spot's position: its grid square's centre, or its approximate position. */
export function spotLatLon(s) {
    return s?.ll ?? gridToLatLon(s?.grid);
}

/** Great-circle distance in km between two [lat, lon] points, or null if either is missing. */
export function distanceKm(a, b) {
    if (!a || !b) return null;
    const rad = Math.PI / 180;
    const dLat = (b[0] - a[0]) * rad;
    const dLon = (b[1] - a[1]) * rad;
    const h = Math.sin(dLat / 2) ** 2 + Math.cos(a[0] * rad) * Math.cos(b[0] * rad) * Math.sin(dLon / 2) ** 2;
    return 2 * 6371 * Math.asin(Math.min(1, Math.sqrt(h)));
}

const BANDS = [
    [1.8, 2, '160m'], [3.5, 4, '80m'], [5.25, 5.45, '60m'], [7, 7.3, '40m'], [10.1, 10.15, '30m'],
    [14, 14.35, '20m'], [18.068, 18.168, '17m'], [21, 21.45, '15m'], [24.89, 24.99, '12m'], [28, 29.7, '10m'],
    [50, 54, '6m'], [70, 71, '4m'], [144, 148, '2m'], [222, 225, '1.25m'], [420, 450, '70cm'],
];

/** A frequency in Hz to a band name ('20m'), or the MHz figure when it isn't in a band, or ''. */
export function bandOf(hz) {
    const m = Number(hz) / 1e6;
    if (!m) return '';
    for (const [lo, hi, name] of BANDS) if (m >= lo && m <= hi) return name;
    return m.toFixed(3) + ' MHz';
}

/** Band names in frequency order, for sorting the list by band. */
export function bandRank(band) {
    const i = BANDS.findIndex(b => b[2] === band);
    return i < 0 ? BANDS.length : i;
}

/** A callsign as PSK Reporter's MQTT topics spell it: '/' becomes '.'. */
export function topicCall(call) {
    return String(call ?? '').trim().toUpperCase().replace(/\//g, '.');
}

/** True for something that looks like a callsign (letters, digits and '/', 3 to 15 long). */
export function isCallsign(call) {
    return /^[A-Z0-9/]{3,15}$/.test(String(call ?? '').trim().toUpperCase());
}

/** The two MQTT topics that carry every spot of a callsign, sent or received. */
export function topicsFor(call) {
    const t = topicCall(call);
    return [`pskr/filter/v2/+/+/${t}/#`, `pskr/filter/v2/+/+/+/${t}/#`];
}

/**
 * One MQTT message (already JSON-parsed) to { kind, raw } for the operator's
 * call, or null if the operator is in neither end. MQTT field names: sc/rc
 * sender and receiver callsigns, sl/rl their locators, f Hz, md mode, rp SNR,
 * b band, t (or t_tx) Unix seconds.
 */
export function classifyMqtt(msg, call) {
    if (!msg) return null;
    const me = String(call ?? '').toUpperCase();
    const raw = { ...msg, t: msg.t_tx ?? msg.t };
    if (String(msg.sc ?? '').toUpperCase() === me) return { kind: 'heardMe', raw };
    if (String(msg.rc ?? '').toUpperCase() === me) return { kind: 'iHeard', raw };
    return null;
}

/** A PSK Reporter query API receptionReport to the MQTT field names. */
export function fromReceptionReport(r) {
    return {
        sc: r.senderCallsign, sl: r.senderLocator, rc: r.receiverCallsign, rl: r.receiverLocator,
        t: r.flowStartSeconds, f: r.frequency, md: r.mode, rp: r.sNR,
    };
}

function toNumberOrNull(v) {
    if (v === undefined || v === null || v === '') return null;
    const n = Number(v);
    return Number.isFinite(n) ? n : null;
}

/**
 * Who heard whom: for each kind, the latest spot of each other station. A
 * station heard again replaces its older spot, so the map and the list show
 * one entry per station, as PSK Reporter's own map does.
 *
 * It also keeps the latest spot of each station on each band, so the band
 * panel counts a station on every band it was heard on, and picking a band
 * shows everyone heard there, even if they have since moved band.
 */
export class SpotStore {
    constructor() {
        this.byKind = { heardMe: new Map(), iHeard: new Map() };
        /** kind -> Map('CALL|band' -> latest spot of that station on that band). */
        this.byBand = { heardMe: new Map(), iHeard: new Map() };
        this.seen = new Set();
        /** The operator's grid as the spots give it, when the app doesn't know it. */
        this.gridFromSpots = '';
    }

    /**
     * Adds one spot in MQTT field names. Returns the stored spot, or null if
     * it was a repeat, had no other station or grid, or was older than both
     * the station's latest spot and its latest spot on that band.
     *
     * With raw.ll ([lat, lon]) and raw.approx (the country) and no grid, the
     * spot goes on at that approximate position. It never replaces a spot
     * with a real grid, and a real grid replaces it even if a little older.
     */
    add(kind, raw) {
        if (!this.byKind[kind]) return null;
        if (raw.sq !== undefined && raw.sq !== null) {
            const key = kind + ':' + raw.sq;
            if (this.seen.has(key)) return null;
            this.seen.add(key);
            if (this.seen.size > 50000) this.seen.clear();
        }
        const other = String((kind === 'heardMe' ? raw.rc : raw.sc) ?? '').trim().toUpperCase();
        const grid = String((kind === 'heardMe' ? raw.rl : raw.sl) ?? '').trim().toUpperCase();
        const myGrid = String((kind === 'heardMe' ? raw.sl : raw.rl) ?? '').trim().toUpperCase();
        const approx = !gridToLatLon(grid) && Array.isArray(raw.ll) ? String(raw.approx ?? '') : null;
        if (!other || (approx === null && !gridToLatLon(grid))) return null;
        if (!this.gridFromSpots && gridToLatLon(myGrid)) this.gridFromSpots = myGrid;
        const t = Number(raw.t) || 0;
        const spot = {
            kind,
            call: other,
            grid,
            t,
            band: raw.b || bandOf(raw.f),
            hz: Number(raw.f) || 0,
            mode: raw.md || '',
            snr: toNumberOrNull(raw.rp),
        };
        if (approx !== null) { spot.grid = ''; spot.ll = [Number(raw.ll[0]), Number(raw.ll[1])]; spot.approx = approx; }
        // A real grid beats an approximate position, whichever is newer.
        const beats = held => {
            if (!held) return true;
            const heldApprox = held.approx !== undefined, newApprox = approx !== null;
            if (heldApprox !== newApprox) return heldApprox;
            return held.t <= t;
        };
        const bandKey = other + '|' + spot.band;
        // A spot with no frequency has no band, so it isn't counted on one.
        const newOnBand = !!spot.band && beats(this.byBand[kind].get(bandKey));
        if (newOnBand) this.byBand[kind].set(bandKey, spot);
        if (!beats(this.byKind[kind].get(other))) return newOnBand ? spot : null;
        this.byKind[kind].set(other, spot);
        return spot;
    }

    /** A station's grid from any spot held for it, or ''. */
    gridFor(call) {
        for (const kind of KINDS) {
            const s = this.byKind[kind].get(call);
            if (s?.grid) return s.grid;
        }
        return '';
    }

    /**
     * Spots heard in the last windowSecs seconds, for the kinds asked for:
     * each station's latest spot, or with band given, its latest spot on that band.
     */
    within(nowSecs, windowSecs, kinds = KINDS, band = '') {
        const out = [];
        for (const kind of kinds) {
            const spots = band ? this.byBand[kind]?.values() : this.byKind[kind]?.values();
            for (const s of spots ?? []) {
                if (nowSecs - s.t <= windowSecs && (!band || s.band === band)) out.push(s);
            }
        }
        return out;
    }

    /**
     * Stations per band in the last windowSecs seconds, both ways: one row per
     * band with a spot, in frequency order, as { band, heardMe, iHeard }. A
     * station heard on two bands counts on both.
     */
    bandCounts(nowSecs, windowSecs) {
        const rows = new Map();
        for (const kind of KINDS) {
            for (const s of this.byBand[kind].values()) {
                if (nowSecs - s.t > windowSecs) continue;
                const row = rows.get(s.band) ?? { band: s.band, heardMe: 0, iHeard: 0 };
                row[kind]++;
                rows.set(s.band, row);
            }
        }
        return [...rows.values()].sort((a, b) =>
            (bandRank(a.band) - bandRank(b.band)) || ((parseFloat(a.band) || 0) - (parseFloat(b.band) || 0)));
    }

    clear() {
        for (const k of KINDS) { this.byKind[k].clear(); this.byBand[k].clear(); }
        this.seen.clear();
        this.gridFromSpots = '';
    }
}

/**
 * The spot list's rows: each spot with its distance from home, sorted.
 * sortKey: 'time' | 'call' | 'band' | 'snr' | 'distance'; desc: true for
 * largest/newest first.
 */
export function listRows(spots, home, sortKey = 'time', desc = true) {
    const rows = spots.map(s => ({ ...s, km: distanceKm(home, spotLatLon(s)) }));
    const val = r => {
        switch (sortKey) {
            case 'call': return r.call;
            case 'band': return bandRank(r.band);
            case 'snr': return r.snr ?? -999;
            case 'distance': return r.km ?? -1;
            default: return r.t;
        }
    };
    rows.sort((a, b) => {
        const va = val(a), vb = val(b);
        let c = typeof va === 'string' ? va.localeCompare(vb) : va - vb;
        if (c === 0) c = a.t - b.t;
        return desc ? -c : c;
    });
    return rows;
}

/**
 * Best DX: the furthest station each way among the spots given, as
 * { heardMe, iHeard }, each the spot plus km, or null. Spots without a
 * distance (no home grid yet) don't count, nor approximate ones: the middle
 * of a big country is no measure of how far the signal went.
 */
export function bestDx(spots, home) {
    const best = { heardMe: null, iHeard: null };
    for (const s of spots) {
        if (s.approx !== undefined) continue;
        const km = distanceKm(home, gridToLatLon(s.grid));
        if (km === null || !(s.kind in best)) continue;
        const held = best[s.kind];
        if (!held || km > held.km || (km === held.km && s.t > held.t)) best[s.kind] = { ...s, km };
    }
    return best;
}

/** Unix seconds to 'HH:MM:SS' UTC, for the clock. */
export function utcHhmmss(secs) {
    return new Date(secs * 1000).toISOString().slice(11, 19);
}

/**
 * Where the sun is overhead at a moment (ms since 1970), as [lat, lon] in
 * degrees. A low-precision solar formula (about 0.01 degree from 1950 to
 * 2050), plenty for a grey line on a world map.
 */
export function subsolarPoint(ms) {
    const rad = Math.PI / 180;
    const d = ms / 86400000 - 10957.5;                  // days since 2000-01-01 12:00 UTC
    const g = (357.529 + 0.98560028 * d) * rad;         // mean anomaly
    const q = 280.459 + 0.98564736 * d;                 // mean longitude
    const lambda = (q + 1.915 * Math.sin(g) + 0.020 * Math.sin(2 * g)) * rad;
    const eps = (23.439 - 0.00000036 * d) * rad;
    const ra = Math.atan2(Math.cos(eps) * Math.sin(lambda), Math.cos(lambda)) / rad;
    const dec = Math.asin(Math.sin(eps) * Math.sin(lambda)) / rad;
    const gmst = (18.697374558 + 24.06570982441908 * d) * 15;   // degrees
    const lon = ((ra - gmst) % 360 + 540) % 360 - 180;
    return [dec, lon];
}

/**
 * The night side of the Earth at a moment, as a polygon of [lat, lon] for the
 * map: the grey line (where the sun is on the horizon) from 180 W to 180 E,
 * closed round the pole that is in darkness. stepDeg sets the spacing.
 */
export function nightPolygon(ms, stepDeg = 2) {
    const rad = Math.PI / 180;
    const [sunLat, sunLon] = subsolarPoint(ms);
    // At an equinox the sun is over the equator and the line runs pole to pole.
    const tanDec = Math.tan((Math.abs(sunLat) < 1e-6 ? 1e-6 : sunLat) * rad);
    const pts = [];
    for (let lon = -180; lon <= 180 + 1e-9; lon += stepDeg) {
        const lat = Math.atan(-Math.cos((lon - sunLon) * rad) / tanDec) / rad;
        pts.push([lat, lon]);
    }
    const darkPole = sunLat > 0 ? -90 : 90;
    pts.push([darkPole, 180], [darkPole, -180]);
    return pts;
}

/** Unix seconds to 'HH:MM' UTC. */
export function utcHhmm(secs) {
    return new Date(secs * 1000).toISOString().slice(11, 16);
}

/** A distance in km for the list, in km or miles. */
export function formatDistance(km, miles = false) {
    if (km === null || km === undefined) return '';
    const v = miles ? km / 1.609344 : km;
    return Math.round(v).toLocaleString('en-GB') + (miles ? ' mi' : ' km');
}
