// dial-stepper.js tunes the pop-out spectrum from the mouse wheel. It must land
// on the step grid as the board's dial does, and a fast spin must end where
// the operator stopped, with no write left behind.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { dialStepper } from '../../js/radio-panel/dial-stepper.js';

function rig(startHz, stepHz = 100) {
    const at = { A: startHz, B: 0 }, writes = [], pending = [];
    const s = dialStepper({
        stepHz: () => stepHz,
        freq: vfo => at[vfo],
        show: (vfo, hz) => { at[vfo] = hz; },
        write: (vfo, hz) => { writes.push(hz); return new Promise(res => pending.push(res)); },
    });
    const settle = async () => { while (pending.length) { pending.shift()(); await new Promise(r => setTimeout(r, 0)); } };
    return { s, at, writes, settle };
}

test('a notch lands on the step grid, up or down', async () => {
    const a = rig(14_074_050);
    a.s.turn('A', 1); await a.settle();
    assert.deepEqual(a.writes, [14_074_100]);
    const b = rig(14_074_050);
    b.s.turn('A', -1); await b.settle();
    assert.deepEqual(b.writes, [14_074_000]);
    const c = rig(14_074_000, 1000);
    c.s.turn('A', 1); await c.settle();
    assert.deepEqual(c.writes, [14_075_000]);
});

test('notches during a write fold into one more, and the echo is held off', async () => {
    const r = rig(14_074_000);
    r.s.turn('A', 1); r.s.turn('A', 1); r.s.turn('A', 1);
    assert.equal(r.at.A, 14_074_300);
    assert.equal(r.s.holds('A'), true);
    await r.settle();
    assert.deepEqual(r.writes, [14_074_100, 14_074_300]);
    assert.equal(r.s.holds('B'), false);
});

test('nothing is sent with no frequency yet, or past the edges', async () => {
    const r = rig(0);
    r.s.turn('A', 1); await r.settle();
    const e = rig(30_000);
    e.s.turn('A', -1); await e.settle();
    assert.deepEqual([...r.writes, ...e.writes], []);
});

test('a nudge moves by what the digit is worth, off the grid, and folds as a turn does', async () => {
    const r = rig(14_074_050);
    r.s.nudge('A', 1000); r.s.nudge('A', 1000); r.s.nudge('A', -10);
    assert.equal(r.at.A, 14_076_040);
    await r.settle();
    assert.deepEqual(r.writes, [14_075_050, 14_076_040]);
    const e = rig(30_500);
    e.s.nudge('A', -1000); await e.settle();
    assert.deepEqual(e.writes, []);
});
