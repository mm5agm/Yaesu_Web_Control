// panel-math.js drives where the radio panel's needle sits and how the VFO
// digits split. A wrong answer there draws something plausible, so it is pinned
// here rather than left to the eye.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
    formatFrequency, sMeterFraction, sMeterText, passbandPercent, passbandHz,
    axisLabels, spanText, widthText, knobAngle, binsToColumns, percentile,
    meterGeometry, waterfallColour,
} from '../../js/radio-panel/panel-math.js';

test('frequency splits into MHz.kHz and .Hz', () => {
    assert.deepEqual(formatFrequency(7020949), { big: '7.020', small: '.949' });
    assert.deepEqual(formatFrequency(14074000), { big: '14.074', small: '.000' });
    assert.deepEqual(formatFrequency(475000), { big: '0.475', small: '.000' });
    assert.deepEqual(formatFrequency(50313005), { big: '50.313', small: '.005' });
    assert.deepEqual(formatFrequency(undefined), { big: '0.000', small: '.000' });
});

test('S9 sits where the red arc starts, S9+60 at full scale', () => {
    assert.equal(sMeterFraction(0), 0);
    assert.equal(sMeterFraction(9), 0.6);
    assert.equal(sMeterFraction(60), 1);
    assert.equal(sMeterFraction(20), (9 + 2) / 15);
    assert.equal(sMeterFraction(-3), 0);
    assert.equal(sMeterFraction(200), 1);
});

test('the S-meter is continuous across S9 to S9+10', () => {
    const below = sMeterFraction(9.999), at = sMeterFraction(10);
    assert.ok(Math.abs(at - below) < 0.001, `${below} vs ${at}`);
    assert.ok(sMeterFraction(9.5) > sMeterFraction(9));
});

test('S-meter text', () => {
    assert.equal(sMeterText(0), 'S0');
    assert.equal(sMeterText(5.4), 'S5');
    assert.equal(sMeterText(9), 'S9');
    assert.equal(sMeterText(9.5), 'S9+5 dB');
    assert.equal(sMeterText(20), 'S9+20 dB');
});

test('passband: USB above the dial, LSB below, CW centred', () => {
    assert.deepEqual(passbandHz('USB', 2400), { lo: 100, hi: 2500 });
    assert.deepEqual(passbandHz('LSB', 2400), { lo: -2500, hi: -100 });
    assert.deepEqual(passbandHz('CW-U', 500), { lo: -250, hi: 250 });
    assert.equal(passbandHz('USB', 0), null);
    const pct = passbandPercent('CW-U', 500, 10000);
    assert.equal(pct.left, 47.5);
    assert.equal(pct.width, 5);
});

test('passband is clipped to the screen', () => {
    const pct = passbandPercent('USB', 3000, 2000);
    assert.equal(pct.left, 55);
    assert.equal(pct.left + pct.width, 100);
    assert.equal(passbandPercent('USB', 3000, 0), null);
});

test('axis labels centre on the dial', () => {
    assert.deepEqual(axisLabels(7020000, 50000), ['6995.0', '7007.5', '7020.0', '7032.5', '7045.0']);
    assert.deepEqual(axisLabels(7020000, 10000, 3), ['7015.00', '7020.00', '7025.00']);
});

test('span and width text', () => {
    assert.equal(spanText(50000), '50 kHz');
    assert.equal(spanText(2000000), '2 MHz');
    assert.equal(spanText(500), '500 Hz');
    assert.equal(spanText(999), '1 kHz');
    assert.equal(spanText(19989), '20 kHz');
    assert.equal(spanText(124987), '125 kHz');
    assert.equal(widthText(2400), '2.4 kHz');
    assert.equal(widthText(3000), '3.0 kHz');
    assert.equal(widthText(250), '250 Hz');
    assert.equal(widthText(null), '');
});

test('knob sweeps 270 degrees', () => {
    assert.equal(knobAngle(0), -135);
    assert.equal(knobAngle(0.5), 0);
    assert.equal(knobAngle(1), 135);
    assert.equal(knobAngle(7), 135);
});

test('columns keep the strongest bin, so a one-bin carrier survives', () => {
    const bins = new Array(1000).fill(-100);
    bins[503] = -20;
    const cols = binsToColumns(bins, 100);
    assert.equal(cols.length, 100);
    assert.equal(cols[50], -20);
    assert.equal(cols[49], -100);
    assert.equal(binsToColumns([], 10).length, 10);
});

test('more columns than bins draws lines between bins, not steps', () => {
    const cols = binsToColumns([1, 2, 3], 6);
    assert.deepEqual(Array.from(cols), [1, 1.25, 1.75, 2.25, 2.75, 3]);
    assert.deepEqual(Array.from(binsToColumns([5], 3)), [5, 5, 5]);
});

test('percentile', () => {
    assert.equal(percentile([5, 1, 3, 2, 4], 0), 1);
    assert.equal(percentile([5, 1, 3, 2, 4], 1), 5);
    assert.equal(percentile([5, 1, 3, 2, 4], 0.5), 3);
    assert.equal(percentile([], 0.5), 0);
});

test('meter geometry: needle at rest points left, at full scale right', () => {
    const rest = meterGeometry(0), full = meterGeometry(1);
    assert.ok(rest.needle.x2 < 125 && full.needle.x2 > 125);
    assert.equal(rest.upper.length, 8);
    assert.equal(rest.lower.length, 5);
    assert.equal(rest.lower[4].text, '200');
});

test('waterfall colour runs dark to bright', () => {
    assert.deepEqual(waterfallColour(0), [4, 16, 28]);
    assert.deepEqual(waterfallColour(1), [255, 224, 102]);
    assert.deepEqual(waterfallColour(-1), [4, 16, 28]);
});

test('waterfall colour follows a theme\'s own stops', () => {
    const stops = [[0, 0, 0], [200, 0, 0]];
    assert.deepEqual(waterfallColour(0.5, stops), [100, 0, 0]);
    assert.deepEqual(waterfallColour(0.5, []), waterfallColour(0.5));
});

test('alignBins moves the picture by the offset', async () => {
    const { alignBins } = await import('../../js/radio-panel/panel-math.js');
    const bins = [0, 0, 0, 9, 0, 0, 0, 0, 0, 0];      // 10 bins over 1000 Hz: 100 Hz each
    const right = Array.from(alignBins(bins, 200, 1000));
    assert.equal(right.indexOf(9), 5);
    const left = Array.from(alignBins(bins, -300, 1000));
    assert.equal(left.indexOf(9), 0);
    assert.equal(alignBins(bins, 0, 1000), bins);
    assert.equal(alignBins(bins, 40, 1000), bins);    // under half a bin
});

test('TX meter fractions land on their legends', async () => {
    const { txMeterFraction } = await import('../../js/radio-panel/panel-math.js');
    assert.equal(txMeterFraction('PO', 100), 0.5);
    assert.equal(txMeterFraction('PO', 500), 1);
    assert.equal(txMeterFraction('SWR', 1), 0);
    assert.equal(txMeterFraction('SWR', 1.5), 0.25);
    assert.equal(txMeterFraction('SWR', 2), 0.5);
    assert.equal(txMeterFraction('SWR', 3), 0.75);
    assert.ok(txMeterFraction('SWR', 10) > 0.95 && txMeterFraction('SWR', 10) <= 1);
    assert.equal(txMeterFraction('COMP', 10), 0.5);
    assert.equal(txMeterFraction('IDD', 40), 1);
    assert.equal(txMeterFraction('NOPE', 5), 0);
    assert.equal(txMeterFraction('TEMP', 50), 0.5);
    assert.equal(txMeterFraction('VDD', 47.5, { min: 40, max: 55 }), 0.5);
    assert.equal(txMeterFraction('VDD', 13, { min: 10, max: 16 }), 0.5);
    assert.equal(txMeterFraction('VDD', 30, { min: 40, max: 55 }), 0);
    assert.equal(txMeterFraction('VDD', 13), 0);
});

test('a range dial is labelled at its ends and middle', async () => {
    const { rangeLegends } = await import('../../js/radio-panel/panel-math.js');
    assert.deepEqual(rangeLegends(40, 55), ['40', '', '47.5', '', '55']);
    assert.deepEqual(rangeLegends(10, 16), ['10', '', '13', '', '16']);
});

test('every theme defines every colour, so none falls back half-drawn', async () => {
    const { THEMES, themeIds, resolveTheme } = await import('../../js/radio-panel/radio-panel-themes.js');
    const keys = Object.keys(THEMES.console.vars);
    for (const [id, t] of Object.entries(THEMES)) {
        const missing = keys.filter(k => !(k in t.vars));
        assert.deepEqual(missing, [], `${id} is missing ${missing.join(', ')}`);
        assert.ok(t.waterfall.length >= 2, `${id} waterfall`);
    }
    assert.equal(themeIds().at(-1), 'system');
    assert.equal(resolveTheme('system', true).applied, 'console');
    assert.equal(resolveTheme('system', false).applied, 'daylight');
    assert.equal(resolveTheme('system', false).id, 'system');
    assert.equal(resolveTheme('nope').id, 'console');
});
