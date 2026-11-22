// node --test tests/js/slider-wheel.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { wheelValue, attachSliderWheel, SCROLL_GAP_MS } from '../../js/controls/slider-wheel.js';

const range = (value, { min = '0', max = '100', step = '1' } = {}) => ({ min, max, step, value: String(value) });

test('a notch up or down moves one step', () => {
    assert.equal(wheelValue(range(50), -100), 51);
    assert.equal(wheelValue(range(50), 100), 49);
});

test('shift moves ten steps', () => {
    assert.equal(wheelValue(range(50), -100, true), 60);
    assert.equal(wheelValue(range(5), 100, true), 0);
});

test('stops at the ends, and says so with null', () => {
    assert.equal(wheelValue(range(100), -100), null);
    assert.equal(wheelValue(range(0), 100), null);
    assert.equal(wheelValue(range(99), -100, true), 100);
});

test('follows the slider step and keeps the value on a step', () => {
    assert.equal(wheelValue(range(1000, { min: '-1200', max: '1200', step: '10' }), -1), 1010);
    assert.equal(wheelValue(range(0.3, { min: '0', max: '1', step: '0.1' }), -1), 0.4);
    assert.equal(wheelValue(range(1.7, { min: '0', max: '3', step: '0.5' }), -1), 2);
});

test('a zero delta does nothing', () => {
    assert.equal(wheelValue(range(50), 0), null);
});

// A small stand-in for the page: the root takes the wheel listener, each
// slider records the events it is sent.
function fakePage() {
    let listener = null;
    const doc = { activeElement: null };
    const root = {
        addEventListener: (type, fn) => { if (type === 'wheel') listener = fn; },
        removeEventListener: () => { listener = null; },
    };
    const slider = (id, value = 50, attrs = {}) => {
        const s = {
            id, min: '0', max: '100', step: '1', value: String(value), disabled: false,
            ownerDocument: doc, sent: [],
            hasAttribute: a => a in attrs,
            dispatchEvent: e => s.sent.push(e.type),
        };
        s.closest = sel => (sel.includes('range') ? s : null);
        return s;
    };
    const page = { closest: () => null };
    const wheel = (target, deltaY, extra = {}) => {
        let prevented = false;
        listener({ target, deltaY, deltaX: 0, shiftKey: false, ctrlKey: false,
                   preventDefault: () => { prevented = true; }, ...extra });
        return prevented;
    };
    return { root, doc, slider, page, wheel };
}

test('the wheel over a slider moves it and sends input and change', () => {
    const p = fakePage();
    let t = 1000;
    attachSliderWheel(p.root, { now: () => t });
    const af = p.slider('afGainSliderA');
    assert.equal(p.wheel(af, -100), true);
    assert.equal(af.value, '51');
    assert.deepEqual(af.sent, ['input', 'change']);
});

test('scrolling the page past a slider leaves it alone', () => {
    const p = fakePage();
    let t = 1000;
    attachSliderWheel(p.root, { now: () => t });
    const af = p.slider('afGainSliderA');
    p.wheel(p.page, 100);                 // the page scrolls
    t += 50;
    assert.equal(p.wheel(af, 100), false); // the slider passes under the pointer
    assert.equal(af.value, '50');
    t += 50;
    p.wheel(af, 100);                      // the scroll is still going
    assert.equal(af.value, '50');
    t += SCROLL_GAP_MS + 1;                // a pause, then the wheel is meant for the slider
    p.wheel(af, 100);
    assert.equal(af.value, '49');
});

test('a transmit level only takes the wheel when focused', () => {
    const p = fakePage();
    attachSliderWheel(p.root, { focusOnly: ['powerSlider'], now: () => 1000 });
    const power = p.slider('powerSlider', 10);
    assert.equal(p.wheel(power, -100), false);
    assert.equal(power.value, '10');
    assert.deepEqual(power.sent, []);
    p.doc.activeElement = power;
    p.wheel(power, -100);
    assert.equal(power.value, '11');
});

test('data-wheel-focus marks a slider as focus-only too', () => {
    const p = fakePage();
    attachSliderWheel(p.root, { now: () => 1000 });
    const mic = p.slider('mic', 20, { 'data-wheel-focus': '' });
    p.wheel(mic, -100);
    assert.equal(mic.value, '20');
});

test('disabled sliders and Ctrl+wheel are left alone', () => {
    const p = fakePage();
    attachSliderWheel(p.root, { now: () => 1000 });
    const sq = p.slider('squelchSliderA');
    p.wheel(sq, -100, { ctrlKey: true });
    assert.equal(sq.value, '50');
    sq.disabled = true;
    p.wheel(sq, -100);
    assert.equal(sq.value, '50');
});

test('Shift+wheel arriving as deltaX moves ten steps', () => {
    const p = fakePage();
    attachSliderWheel(p.root, { now: () => 1000 });
    const rf = p.slider('rfGainSliderA');
    p.wheel(rf, 0, { deltaX: -100, shiftKey: true });
    assert.equal(rf.value, '60');
});
