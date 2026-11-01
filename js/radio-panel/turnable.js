// turnable.js - the knob behaviour shared by the radio panel's dial and knobs
// and the RTTY Contest page's dial: the mouse wheel, or a drag round the centre.

// Makes `el` turn like a knob: the mouse wheel over it, or a drag round its
// centre. `partAt(x, y)` names the part under the pointer (a ring or the knob
// inside it) and is fixed for the length of a drag. `onStep(part, dir)` is
// called once per wheel notch or per `deg` of drag, dir +1 clockwise; and
// `onTurn(part, deg)`, when given, follows the drag so the part can rotate
// with the pointer. The part under the pointer (or being dragged) is named in
// `data-hot` ('all' when the control is one part) so the style can light it.
//
// `spin` makes a drag behave like a weighted VFO knob. Turned slowly, every
// `deg` is one step. Spun faster than SPIN_FROM turns a second, the steps are
// multiplied, up to SPIN_MAX times at SPIN_TOP turns a second. Let go while
// spinning fast and it runs on for a moment, slowing down; a click or the
// wheel stops it. After a slow turn it stops dead, so fine tuning never
// drifts. `spin` may be a function, asked at the release, that returns false
// to forbid the run-on (while transmitting, say). The idea comes from the
// tuning dial in Brian Clark's IC-7300-Scope.
const SPIN_FROM = 1.5, SPIN_TOP = 4, SPIN_MAX = 8;  // turns a second, and the most steps per step
const SPIN_STOP = 0.75;                            // a run-on ends below this, in turns a second
const SPIN_FADE = 0.25;                            // seconds for a run-on to slow to 1/e
const SPIN_WINDOW = 100;                           // ms of drag the speed is measured over
export function spinSteps(turnsPerSec) {
    const t = Math.abs(turnsPerSec);
    if (t <= SPIN_FROM) return 1;
    return Math.round(1 + (SPIN_MAX - 1) * (Math.min(t, SPIN_TOP) - SPIN_FROM) / (SPIN_TOP - SPIN_FROM));
}
export function turnable(el, { deg = 15, partAt = () => undefined, onStep, onTurn, spin = false }) {
    el.classList.add('rp-turn');
    const hot = part => { el.dataset.hot = part ?? 'all'; };
    el.addEventListener('pointerleave', () => { if (!drag) delete el.dataset.hot; });
    // Turns d degrees of `part`, giving a step per `deg`, each one `times` steps.
    const turn = (rec, d, times) => {
        onTurn?.(rec.part, d);
        rec.acc += d;
        while (Math.abs(rec.acc) >= deg) {
            const dir = rec.acc > 0 ? 1 : -1;
            rec.acc -= dir * deg;
            for (let i = 0; i < times; i++) onStep(rec.part, dir);
        }
    };
    let coast = null;
    const stopCoast = () => {
        if (coast) cancelAnimationFrame(coast.frame);
        coast = null;
    };
    el.addEventListener('wheel', e => {
        if (!e.deltaY) return;
        e.preventDefault();
        stopCoast();
        const part = partAt(e.clientX, e.clientY), dir = e.deltaY < 0 ? 1 : -1;
        hot(part);
        onTurn?.(part, dir * deg);
        onStep(part, dir);
    }, { passive: false });
    let drag = null;
    const angle = e => {
        const rc = el.getBoundingClientRect();
        return Math.atan2(e.clientY - (rc.top + rc.height / 2), e.clientX - (rc.left + rc.width / 2)) * 180 / Math.PI;
    };
    el.addEventListener('pointerdown', e => {
        if (e.button !== 0) return;
        stopCoast();
        drag = { id: e.pointerId, part: partAt(e.clientX, e.clientY), last: angle(e), acc: 0, moves: [], t: e.timeStamp };
        hot(drag.part);
        el.setPointerCapture?.(e.pointerId);
        el.classList.add('turning');
        e.preventDefault();
    });
    el.addEventListener('pointermove', e => {
        if (!drag) return hot(partAt(e.clientX, e.clientY));
        if (e.pointerId !== drag.id) return;
        const a = angle(e);
        let d = a - drag.last;
        if (d > 180) d -= 360; else if (d < -180) d += 360;
        drag.last = a;
        turn(drag, d, spin ? spinSteps(speed(drag, e.timeStamp, d)) : 1);
    });
    // The drag's speed in turns a second over the moves of the last
    // SPIN_WINDOW ms, counting this move of d degrees when given. Each move
    // is timed from the one before it, or from the press.
    const speed = (rec, now, d) => {
        if (d !== undefined) {
            rec.moves.push({ t: now, d, dt: now - rec.t });
            rec.t = now;
        }
        while (rec.moves.length && now - rec.moves[0].t > SPIN_WINDOW) rec.moves.shift();
        const time = rec.moves.reduce((s, m) => s + m.dt, 0);
        return time > 0 ? rec.moves.reduce((s, m) => s + m.d, 0) / 360 / (time / 1000) : 0;
    };
    const runOn = (rec, turnsPerSec) => {
        coast = { v: turnsPerSec, t: performance.now(), frame: 0 };
        const tick = now => {
            if (!coast) return;
            const dt = Math.min((now - coast.t) / 1000, 0.1);
            coast.t = now;
            coast.v *= Math.exp(-dt / SPIN_FADE);
            if (Math.abs(coast.v) < SPIN_STOP) return stopCoast();
            turn(rec, coast.v * 360 * dt, spinSteps(coast.v));
            coast.frame = requestAnimationFrame(tick);
        };
        coast.frame = requestAnimationFrame(tick);
    };
    const end = e => {
        if (!drag || e.pointerId !== drag.id) return;
        const rec = drag;
        drag = null;
        el.classList.remove('turning');
        if (!el.matches(':hover')) delete el.dataset.hot;
        if (!spin || e.type !== 'pointerup') return;
        const v = speed(rec, e.timeStamp);
        if (Math.abs(v) > SPIN_FROM && (typeof spin !== 'function' || spin())) runOn(rec, v);
    };
    el.addEventListener('pointerup', end);
    el.addEventListener('pointercancel', end);
}
