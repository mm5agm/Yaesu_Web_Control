// On a panel showing the FTdx101's ACC scope, the pane label says when the
// radio's scope LEVEL is cutting the trace off ("MAIN · ACC SCOPE · LEVEL
// TOO LOW"), and goes back when it isn't. fetch and the timers are stubs.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test, mock } from 'node:test';
import assert from 'node:assert/strict';
import { renameForSource } from '../../js/radio-panel/spectrum-refine.js';

const LOW = { text: 'LEVEL too low', detail: 'Raise Level.' };
let answer;
globalThis.fetch = async () => ({ ok: true, json: async () => answer });
const settle = async () => { for (let i = 0; i < 5; i++) await new Promise(r => setImmediate(r)); };

test('an ACC pane label warns while LEVEL is too low, and clears', async () => {
    mock.timers.enable({ apis: ['setTimeout'] });
    try {
        answer = { acc: true, source: 'ACC scope', level: 3, max: 5, levelWarning: null };
        const names = [];
        renameForSource('A', 'MAIN · SDR A', n => names.push(n));
        await settle();
        assert.deepEqual(names, ['MAIN · ACC SCOPE']);

        answer = { ...answer, levelWarning: LOW };
        mock.timers.tick(5000); await settle();
        assert.equal(names.at(-1), 'MAIN · ACC SCOPE · LEVEL TOO LOW');

        mock.timers.tick(5000); await settle();
        assert.equal(names.length, 2, 'nothing new while the warning stands');

        answer = { ...answer, levelWarning: null };
        mock.timers.tick(5000); await settle();
        assert.equal(names.at(-1), 'MAIN · ACC SCOPE');
    } finally {
        mock.timers.reset();
    }
});

test('an SDR pane is never renamed or asked again', async () => {
    mock.timers.enable({ apis: ['setTimeout'] });
    try {
        let asked = 0;
        globalThis.fetch = async () => { asked++; return { ok: true, json: async () => ({ acc: false, source: 'SDR' }) }; };
        const names = [];
        renameForSource('B', 'SUB · SDR B', n => names.push(n));
        await settle();
        mock.timers.tick(20000); await settle();
        assert.deepEqual(names, []);
        assert.equal(asked, 1);
    } finally {
        mock.timers.reset();
    }
});
