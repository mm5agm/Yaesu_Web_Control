// The RTTY Contest view's typed-frequency rule, what the main page keeps
// when parts are popped out, and how a saved layout is read back
// (contest-view.js). The rest
// of the view is DOM and is covered by the apps' UI suites.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseFrequencyEntry, partsLeft, tidyBoxSize, tidyLayout, BOX_SIZES } from '../../js/rtty-contest/contest-view.js';

test('under 100 is MHz', () => {
    assert.equal(parseFrequencyEntry('14.08'), 14_080_000);
    assert.equal(parseFrequencyEntry('7.0405'), 7_040_500);
    assert.equal(parseFrequencyEntry('3,59'), 3_590_000);
});

test('100 up to 100000 is kHz', () => {
    assert.equal(parseFrequencyEntry('14080'), 14_080_000);
    assert.equal(parseFrequencyEntry('7040.5'), 7_040_500);
    assert.equal(parseFrequencyEntry(' 21 080 '), 21_080_000);
});

test('100000 and up is Hz', () => {
    assert.equal(parseFrequencyEntry('14080000'), 14_080_000);
    assert.equal(parseFrequencyEntry('475000'), 475_000);
});

test('rubbish and out of range is null', () => {
    for (const t of ['', 'abc', '14.08.1', '-7', '0', null, undefined]) assert.equal(parseFrequencyEntry(t), null, String(t));
    assert.equal(parseFrequencyEntry('20'), 20_000_000);
    assert.equal(parseFrequencyEntry('10'), 10_000_000);
    assert.equal(parseFrequencyEntry('80', 30_000, 56_000_000), null);
    assert.equal(parseFrequencyEntry('0.02'), null);         // 20 kHz
});

test('the main page keeps what has not popped out', () => {
    assert.equal(partsLeft({}), 'both');
    assert.equal(partsLeft(undefined), 'both');
    assert.equal(partsLeft({ tuner: true }), 'both');
    assert.equal(partsLeft({ spectrum: true }), 'waterfall');
    assert.equal(partsLeft({ waterfall: true }), 'spectrum');
    assert.equal(partsLeft({ spectrum: true, waterfall: true }), 'none');
    assert.equal(partsLeft({ both: true }), 'none');
});

test('a box size is clamped, and empty is a box that fills its room', () => {
    assert.equal(tidyBoxSize('tunerWidth', ''), null);
    assert.equal(tidyBoxSize('tunerWidth', null), null);
    assert.equal(tidyBoxSize('tunerWidth', 'wide'), null);
    assert.equal(tidyBoxSize('tunerWidth', 420.4), 420);
    assert.equal(tidyBoxSize('tunerWidth', 10), BOX_SIZES.tunerWidth.min);
    assert.equal(tidyBoxSize('tunerWidth', 5000), BOX_SIZES.tunerWidth.max);
    // The others have no top: the room on screen is the limit.
    assert.equal(tidyBoxSize('stackWidth', 5000), 5000);
    assert.equal(tidyBoxSize('stackHeight', 50), BOX_SIZES.stackHeight.min);
    assert.equal(tidyBoxSize('tunerHeight', 50), BOX_SIZES.tunerHeight.min);
    assert.equal(tidyBoxSize('strip', 125), null);
});

test('a layout with nothing in it is the standard page', () => {
    for (const l of [undefined, null, {}, 'junk']) {
        assert.deepEqual(tidyLayout(l), {
            stackWidth: null, stackHeight: null, tunerWidth: null, tunerHeight: null, meters: true, spots: true, wfFirst: false, decode: false,
            split: 0.5, step: 10,
            out: { spectrum: false, waterfall: false, both: false, tuner: false },
            places: {},
        });
    }
});

test('a saved layout comes back as saved, less what is wrong in it', () => {
    const place = { left: 1920, top: 0, width: 1100, height: 760, zoomSafe: true };
    const l = tidyLayout({
        stackWidth: 900, stackHeight: 500, tunerWidth: 480, tunerHeight: 'x', strip: 125, meters: false, spots: false, wfFirst: true, split: 0.3, step: 100,
        out: { both: true, tuner: 'yes' },
        places: { both: place, tuner: { left: 5 }, spectrum: null },
    });
    assert.equal(l.tunerWidth, 480);
    assert.equal(l.stackWidth, 900);
    assert.equal(l.stackHeight, 500);
    assert.equal(l.tunerHeight, null);
    // STRIP has gone: an older layout's is left out.
    assert.equal('strip' in l, false);
    assert.equal(l.meters, false);
    assert.equal(l.spots, false);
    assert.equal(l.wfFirst, true);
    assert.equal(l.decode, false);
    assert.equal(tidyLayout({ decode: true }).decode, true);
    assert.equal(l.split, 0.3);
    assert.equal(l.step, 100);
    // Only true is out: a hand edit's "yes" is not.
    assert.deepEqual(l.out, { spectrum: false, waterfall: false, both: true, tuner: false });
    // A place with no size is no place.
    assert.deepEqual(l.places, { both: place });
    assert.equal(tidyLayout({ split: 1.5, step: 7 }).split, 0.5);
    assert.equal(tidyLayout({ split: 1.5, step: 7 }).step, 10);
});
