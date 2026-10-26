// spectrum-levels.test.mjs - SpectrumView's levels (auto floor, ref, fit),
// which the RTTY Contest view uses, and the radio panel's defaults left as
// they were. A small stand-in for the canvas is enough: these read the
// numbers, not the picture.

import { test } from 'node:test';
import assert from 'node:assert/strict';

const ctx = new Proxy({}, { get: (t, k) => (k in t ? t[k] : () => ({ data: new Uint8ClampedArray(4096 * 4) })), set: (t, k, v) => { t[k] = v; return true; } });
function el() {
    return {
        width: 0, height: 0,
        prepend() {}, append() {},
        getContext: () => ctx,
        getBoundingClientRect: () => ({ width: 100, height: 50, top: 0, bottom: 50, left: 0 }),
    };
}
globalThis.document = { createElement: el };
globalThis.window = { devicePixelRatio: 1 };
globalThis.getComputedStyle = () => ({ getPropertyValue: () => '' });
globalThis.ResizeObserver = class { observe() {} disconnect() {} };

const { SpectrumView } = await import('../../js/radio-panel/spectrum-view.js');
const { tidyLevels, STEP_CHOICES_HZ } = await import('../../js/rtty-contest/contest-view.js');

// A flat band at -100 with one carrier of the given level in the middle.
const frame = (peak = -100) => { const b = new Array(100).fill(-100); b[50] = peak; return b; };
// Smoothing off, so the carrier the trace draws is the one pushed.
const view = () => { const v = new SpectrumView(el(), el()); v.setSettings({ smooth: 0 }); return v; };

test('the defaults are the radio panel\'s: the bottom 6 dB under the floor at 60 dB', () => {
    const v = view();
    v.push(frame());
    const l = v.levels;
    assert.equal(l.auto, true);
    assert.equal(l.ref, null);
    assert.equal(l.floor, -100);
    assert.equal(l.lo, -106);
    assert.equal(l.hi, -46);
});

test('ref puts the bottom that many dB from the floor, whatever the range', () => {
    const v = view();
    v.push(frame());
    v.setLevels({ ref: -20 });
    v.setSettings({ range: 100 });
    assert.equal(v.levels.lo, -120);
    assert.equal(v.levels.hi, -20);
    v.setLevels({ ref: null });
    assert.equal(v.levels.lo, -110);
});

test('ref is kept within -40..40 and rounded', () => {
    const v = view();
    assert.equal(v.setLevels({ ref: -99 }).ref, -40);
    assert.equal(v.setLevels({ ref: 3.6 }).ref, 4);
    assert.equal(v.setLevels({ ref: 'x' }).ref, null);
});

test('auto off freezes the floor; on, it follows the noise', () => {
    const v = view();
    v.push(frame());
    v.setLevels({ auto: false });
    for (let i = 0; i < 20; i++) v.push(new Array(100).fill(-80));
    assert.equal(v.levels.floor, -100);
    v.setLevels({ auto: true });
    for (let i = 0; i < 20; i++) v.push(new Array(100).fill(-80));
    assert.ok(v.levels.floor > -90);
});

test('auto off still finds a floor from the first frame', () => {
    const v = view();
    v.setLevels({ auto: false });
    v.push(frame());
    assert.equal(v.levels.floor, -100);
});

test('fit puts the strongest recent peak just under the top of the trace', () => {
    const v = view();
    v.push(frame(-30));
    for (let i = 0; i < 5; i++) v.push(frame(-60));
    const r = v.fit();
    const { lo, hi } = v.levels;
    // The trace's top is 92% of the way up.
    const top = lo + (hi - lo) * 0.92;
    assert.ok(top >= -27 && top < -26, `top ${top} for range ${r}`);
});

test('fit with a ref keeps the bottom where it is', () => {
    const v = view();
    v.setLevels({ ref: -10 });
    v.push(frame(-40));
    v.fit();
    const { lo, hi } = v.levels;
    assert.equal(lo, -110);
    assert.ok(lo + (hi - lo) * 0.92 >= -37);
});

test('fit has nothing to fit before a frame or after clear', () => {
    const v = view();
    assert.equal(v.fit(), null);
    v.push(frame(-30));
    v.clear();
    assert.equal(v.fit(), null);
});

test('tidyLevels: defaults, clamping, and null for "the default"', () => {
    assert.deepEqual(tidyLevels(null), { auto: true, ref: null, range: null });
    assert.deepEqual(tidyLevels({ auto: false, ref: -60, range: 500 }), { auto: false, ref: -40, range: 160 });
    assert.deepEqual(tidyLevels({ ref: '', range: 'wide' }), { auto: true, ref: null, range: null });
    assert.equal(tidyLevels({ ref: 0 }).ref, 0);
});

test('the wheel steps start at 1 Hz', () => {
    assert.deepEqual(STEP_CHOICES_HZ, [1, 10, 100, 1000]);
});

test('a hidden trace still finds the floor, for a waterfall-only window', () => {
    // Hidden, both canvases are 1 px: the floor must not come from one column.
    const hidden = () => ({ ...el(), getBoundingClientRect: () => ({ width: 0, height: 0, top: 0, bottom: 0, left: 0 }) });
    const v = new SpectrumView(hidden(), hidden());
    v.setSettings({ smooth: 0 });
    const b = new Array(100).fill(-100);
    for (let i = 0; i < 30; i++) b[i] = -60;    // a busy stretch of band
    v.push(b);
    assert.equal(v.levels.floor, -100);
});
