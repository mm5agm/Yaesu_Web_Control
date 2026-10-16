// Keeping a dialog on the window. Only the pure helpers are tested here; the
// DOM half is checked in the browser.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { pullIntoView, firstPlace, GRIP, EDGE } from '../../js/dialogs/place-in-view.js';

const VIEW = { width: 1000, height: 800 };
const box = (left, top, width = 300, height = 200) =>
    ({ left, top, right: left + width, width, height });

test('a dialog on the window is left alone', () => {
    assert.equal(pullIntoView(box(100, 100), VIEW), null);
    // Partly off the left, with enough on to grab, is still fine.
    assert.equal(pullIntoView(box(-300 + GRIP + 1, 100), VIEW), null);
});

test('a dialog running off the right or the bottom is moved fully on', () => {
    // Measured 2026-10-04: a panel at left 330 given the window's width.
    assert.deepEqual(pullIntoView(box(330, 40, 1900, 600), { width: 2000, height: 700 }), { left: 100 - EDGE });
    assert.deepEqual(pullIntoView(box(100, 300, 300, 600), VIEW), { top: 200 - EDGE });
    // Bigger than the window: its top-left corner stays on, so its title bar can be grabbed.
    assert.deepEqual(pullIntoView(box(100, 300, 1200, 900), VIEW), { left: 0, top: 0 });
});

test('a dialog past the right or left edge is centred', () => {
    assert.deepEqual(pullIntoView(box(1900, 100), VIEW), { left: 350 });
    assert.deepEqual(pullIntoView(box(-500, 100), VIEW), { left: 350 });
});

test('a dialog below the bottom or above the top comes to the top', () => {
    assert.deepEqual(pullIntoView(box(100, 1200), VIEW), { top: 80 });
    assert.deepEqual(pullIntoView(box(100, -20), VIEW), { top: 80 });
    // A dialog nearly as tall as the window goes as high as it must.
    assert.deepEqual(pullIntoView(box(100, 1200, 300, 760), VIEW), { top: 40 });
    assert.deepEqual(pullIntoView(box(100, 1200, 300, 900), VIEW), { top: 0 });
});

test('a dialog off in both directions is moved in both', () => {
    assert.deepEqual(pullIntoView(box(1900, 1200), VIEW), { left: 350, top: 80 });
});

test('a first place is centred across the top', () => {
    assert.deepEqual(firstPlace(300, VIEW), { left: 350, top: 80 });
    assert.deepEqual(firstPlace(300, { width: 1000, height: 400 }), { left: 350, top: 40 });
    assert.deepEqual(firstPlace(1200, VIEW), { left: 0, top: 80 });
});
