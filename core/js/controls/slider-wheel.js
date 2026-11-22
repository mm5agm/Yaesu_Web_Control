// The mouse wheel over a slider (input type="range") moves it a step at a
// time, for fine adjustment (YWC #145). Shift moves ten steps.
//
// Two guards:
// - Scrolling the page past a slider must not change it. A wheel that comes
//   within SCROLL_GAP_MS of a wheel that scrolled the page is left to the page.
// - A slider that sets a transmit level (power, mic gain and the like) only
//   takes the wheel once it has been clicked or tabbed to. Mark it with
//   data-wheel-focus, or pass its id in focusOnly.
//
// The new value goes out as input and change events, so the page's own
// handlers send it to the radio as if the slider had been dragged.

export const SCROLL_GAP_MS = 300;

/**
 * The slider's value after one wheel notch, or null when it doesn't move.
 * @param {{min:string,max:string,step:string,value:string}} slider
 * @param {number} delta  the wheel's deltaY (or deltaX); negative is up
 * @param {boolean} [big] ten steps instead of one
 */
export function wheelValue(slider, delta, big = false) {
    if (!delta) return null;
    const min = slider.min === '' ? 0 : Number(slider.min);
    const max = slider.max === '' ? 100 : Number(slider.max);
    if (!Number.isFinite(min) || !Number.isFinite(max) || max <= min) return null;
    let step = Number(slider.step);
    if (!Number.isFinite(step) || step <= 0) step = slider.step === 'any' ? (max - min) / 100 : 1;
    const now = Number(slider.value);
    if (!Number.isFinite(now)) return null;
    const n = (delta < 0 ? 1 : -1) * (big ? 10 : 1);
    // Count steps from min, as the browser does, so the value stays on a step.
    const steps = Math.round((now - min) / step) + n;
    let v = Math.min(max, Math.max(min, min + steps * step));
    // Lose the binary fraction a step like 0.1 leaves behind.
    const places = (String(step).split('.')[1] || '').length;
    v = Number(v.toFixed(places));
    return v === now ? null : v;
}

/**
 * Listen on root for the wheel over its sliders.
 * @param {Document|HTMLElement} root
 * @param {{focusOnly?: string[], now?: () => number}} [opts]
 * @returns {() => void} removes the listener
 */
export function attachSliderWheel(root, opts = {}) {
    const focusOnly = new Set(opts.focusOnly || []);
    const clock = opts.now || (() => performance.now());
    let lastPageScroll = -Infinity;

    const onWheel = e => {
        const slider = e.target?.closest?.('input[type="range"]');
        if (!slider) {
            lastPageScroll = clock();
            return;
        }
        if (slider.disabled || e.ctrlKey) return;               // Ctrl+wheel is the browser's zoom
        if (clock() - lastPageScroll < SCROLL_GAP_MS) {         // still scrolling the page
            lastPageScroll = clock();
            return;
        }
        const needsFocus = focusOnly.has(slider.id) || slider.hasAttribute('data-wheel-focus');
        if (needsFocus && slider.ownerDocument.activeElement !== slider) return;

        // Shift+wheel comes as deltaX in Chromium.
        const delta = e.deltaY || e.deltaX;
        const v = wheelValue(slider, delta, e.shiftKey);
        e.preventDefault();
        if (v === null) return;
        slider.value = String(v);
        slider.dispatchEvent(new Event('input', { bubbles: true }));
        slider.dispatchEvent(new Event('change', { bubbles: true }));
    };

    root.addEventListener('wheel', onWheel, { passive: false });
    return () => root.removeEventListener('wheel', onWheel, { passive: false });
}
