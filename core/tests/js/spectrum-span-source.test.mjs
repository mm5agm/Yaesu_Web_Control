// A span list on a panel showing the radio's own scope (the FTdx101's ACC
// scope) drops the SDR-only spans, which the server refuses (Colin,
// 2026-11-14: 15 kHz on the ACC scope stayed at 10 kHz). fetch and the
// <select> are stubs.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { limitSpansToSource, RADIO_SCOPE_SPANS_HZ } from '../../js/radio-panel/spectrum-refine.js';

const ALL = [1e3, 2e3, 5e3, 10e3, 15e3, 20e3, 30e3, 40e3, 50e3, 100e3, 200e3, 500e3, 1e6, 2e6];

function fakeSelect(values) {
    const sel = { options: [] };
    for (const v of values) {
        const o = { value: String(v), disabled: v === '', remove() { sel.options.splice(sel.options.indexOf(o), 1); } };
        sel.options.push(o);
    }
    return sel;
}
const served = info => { globalThis.fetch = async () => ({ ok: true, json: async () => info }); };
const settle = () => new Promise(r => setTimeout(r, 0));

test('an ACC scope panel keeps only the radio spans', async () => {
    served({ available: true, source: 'ACC scope', level: 1, max: 8 });
    const sel = fakeSelect(['', ...ALL]);
    limitSpansToSource(sel, 'A');
    await settle();
    assert.deepEqual(sel.options.map(o => o.value), ['', ...RADIO_SCOPE_SPANS_HZ.map(String)]);
});

test('an SDR panel keeps every span', async () => {
    served({ available: false, source: 'SDR' });
    const sel = fakeSelect(ALL);
    limitSpansToSource(sel, 'B');
    await settle();
    assert.equal(sel.options.length, ALL.length);
});

test('no answer from the server changes nothing', async () => {
    globalThis.fetch = async () => { throw new Error('offline'); };
    const sel = fakeSelect(ALL);
    limitSpansToSource(sel, 'A');
    await settle();
    assert.equal(sel.options.length, ALL.length);
    limitSpansToSource(null, 'A');
});
