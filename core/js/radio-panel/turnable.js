// turnable.js - the knob behaviour shared by the radio panel's dial and knobs
// and the RTTY Contest page's dial: the mouse wheel, or a drag round the centre.

// Makes `el` turn like a knob: the mouse wheel over it, or a drag round its
// centre. `partAt(x, y)` names the part under the pointer (a ring or the knob
// inside it) and is fixed for the length of a drag. `onStep(part, dir)` is
// called once per wheel notch or per `deg` of drag, dir +1 clockwise; and
// `onTurn(part, deg)`, when given, follows the drag so the part can rotate
// with the pointer. The part under the pointer (or being dragged) is named in
// `data-hot` ('all' when the control is one part) so the style can light it.
export function turnable(el, { deg = 15, partAt = () => undefined, onStep, onTurn }) {
    el.classList.add('rp-turn');
    const hot = part => { el.dataset.hot = part ?? 'all'; };
    el.addEventListener('pointerleave', () => { if (!drag) delete el.dataset.hot; });
    el.addEventListener('wheel', e => {
        if (!e.deltaY) return;
        e.preventDefault();
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
        drag = { id: e.pointerId, part: partAt(e.clientX, e.clientY), last: angle(e), acc: 0 };
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
        onTurn?.(drag.part, d);
        drag.acc += d;
        while (Math.abs(drag.acc) >= deg) {
            const dir = drag.acc > 0 ? 1 : -1;
            drag.acc -= dir * deg;
            onStep(drag.part, dir);
        }
    });
    const end = e => {
        if (!drag || e.pointerId !== drag.id) return;
        drag = null;
        el.classList.remove('turning');
        if (!el.matches(':hover')) delete el.dataset.hot;
    };
    el.addEventListener('pointerup', end);
    el.addEventListener('pointercancel', end);
}
