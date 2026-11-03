// freq-digits.js - a frequency drawn one span per character, each digit
// carrying the Hz it is worth, and the mouse wheel over a digit moving the
// dial by that much. The radio panel's VFOs and the RTTY Contest strip both
// use it, so a digit turns the same way everywhere.

/**
 * Draw `text` into `el`, one span per character. `unitHz` is what the last
 * digit of `text` is worth; each digit to its left is worth ten times more.
 */
export function freqDigits(el, text, unitHz) {
    if (el.textContent === text) return;
    el.textContent = '';
    let step = unitHz;
    const out = [];
    for (let i = text.length - 1; i >= 0; i--) {
        const span = document.createElement('span');
        span.textContent = text[i];
        if (text[i] >= '0' && text[i] <= '9') { span.dataset.step = String(step); step *= 10; }
        out.unshift(span);
    }
    el.append(...out);
}

/**
 * The wheel over a digit of `el` calls onStep(+hz) up or onStep(-hz) down,
 * hz being what that digit is worth. Elsewhere in `el` the wheel is left alone.
 */
export function wheelDigits(el, onStep) {
    el.classList.add('wheel');
    el.addEventListener('wheel', e => {
        const step = Number(e.target?.dataset?.step);
        if (!step) return;
        e.preventDefault();
        if (e.deltaY) onStep(e.deltaY < 0 ? step : -step);
    }, { passive: false });
}
