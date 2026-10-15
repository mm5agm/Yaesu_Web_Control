// The radio panel spectrum's drawing settings: the classic spectrum's Range,
// Speed, Bright, Smooth and Hold, cleaned the same way whatever was saved.

import test from 'node:test';
import assert from 'node:assert/strict';
import { cleanSpectrumSettings, smoothBins, SPECTRUM_DEFAULTS } from '../../js/radio-panel/spectrum-view.js';

test('nothing saved gives the defaults', () => {
    assert.deepEqual(cleanSpectrumSettings(), { ...SPECTRUM_DEFAULTS });
    assert.deepEqual(cleanSpectrumSettings({}), { range: 60, speed: 1, bright: 0, smooth: 2, hold: false });
});

test('values are clamped to the classic sliders', () => {
    const s = cleanSpectrumSettings({ range: 400, bright: -5, smooth: 99 });
    assert.equal(s.range, 160);
    assert.equal(s.bright, 0);
    assert.equal(s.smooth, 8);
    assert.equal(cleanSpectrumSettings({ range: 1 }).range, 5);
});

test('speed must be one of the divisors', () => {
    assert.equal(cleanSpectrumSettings({ speed: 16 }).speed, 16);
    assert.equal(cleanSpectrumSettings({ speed: 3 }).speed, 1);
    assert.equal(cleanSpectrumSettings({ speed: '128' }).speed, 128);
});

test('hold only when exactly true', () => {
    assert.equal(cleanSpectrumSettings({ hold: true }).hold, true);
    assert.equal(cleanSpectrumSettings({ hold: 'yes' }).hold, false);
});

test('rubbish falls back to the default', () => {
    assert.equal(cleanSpectrumSettings({ range: 'wide' }).range, 60);
});

test('smoothing 0 returns the bins untouched', () => {
    const b = [1, 2, 3];
    assert.equal(smoothBins(b, 0), b);
});

test('smoothing averages neighbours; the ends average what they have', () => {
    const out = Array.from(smoothBins([0, 0, 9, 0, 0], 1));
    assert.deepEqual(out, [0, 3, 3, 3, 0]);
    const ends = Array.from(smoothBins([3, 0, 0, 0, 6], 1));
    assert.deepEqual(ends, [1.5, 1, 0, 2, 3]);
});
