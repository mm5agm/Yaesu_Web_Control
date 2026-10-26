// The RTTY Contest view's typed-frequency rule (contest-view.js). The rest
// of the view is DOM and is covered by the apps' UI suites.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseFrequencyEntry } from '../../js/rtty-contest/contest-view.js';

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
