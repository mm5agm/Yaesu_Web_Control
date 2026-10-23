// dial-stepper.js - the board's dial rule for a window without the board: one
// STEP a notch, landing on the step grid as the radio's dial does. Notches
// that come while a write is in flight are folded into the next write, so a
// fast spin lands where the operator stopped; holds(vfo) says to ignore the
// radio's frequency echo until it has caught up.

/**
 * @param {object} o
 * @param {() => number} o.stepHz  the STEP to use, read at each notch
 * @param {(vfo:string) => number} o.freq  the VFO's frequency as last heard
 * @param {(vfo:string, hz:number) => void} o.show  draw the VFO at hz now
 * @param {(vfo:string, hz:number) => Promise} o.write  send hz to the radio
 * @param {number} [o.minHz]
 * @param {number} [o.maxHz]
 */
export function dialStepper({ stepHz, freq, show, write, minHz = 30_000, maxHz = 75_000_000 }) {
    const wheel = { A: { to: null, busy: false, until: 0 }, B: { to: null, busy: false, until: 0 } };
    async function turn(vfo, dir) {
        const w = wheel[vfo];
        const cur = w?.to ?? freq(vfo);
        const s = stepHz();
        if (!w || !cur || !(s > 0) || !dir) return;
        const to = dir > 0 ? Math.floor(cur / s) * s + s : Math.ceil(cur / s) * s - s;
        if (to < minHz || to > maxHz) return;
        w.to = to;
        w.until = Date.now() + 700;
        show(vfo, to);
        if (w.busy) return;
        w.busy = true;
        try {
            let sent = null;
            while (w.to !== sent) {
                sent = w.to;
                await write(vfo, sent);
            }
        } finally {
            w.busy = false;
            setTimeout(() => { if (!w.busy && Date.now() >= w.until) w.to = null; }, 700);
        }
    }
    return { turn, holds: vfo => !!wheel[vfo] && (wheel[vfo].busy || Date.now() < wheel[vfo].until) };
}
