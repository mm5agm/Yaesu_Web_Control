// iambic-keyer.js turns paddle presses into Morse elements. These tests pin
// the behaviour an operator's hands depend on - strings from a held paddle,
// alternation on a squeeze, the remembered opposite paddle, the mode A/B
// difference - and the timing: every element and every space the right
// number of units, on a clock the tests control.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { IambicKeyer, ElementDecoder, DIT, DAH } from '../../js/cw/iambic-keyer.js';
import { MORSE } from '../../js/cw/morse-timing.js';

// A clock that only moves when told to, firing timers in order on the way.
// late() gives how many ms late each timer fires, as a busy browser's do.
function fakeClock(late = () => 0) {
    let t = 0, id = 0, timers = [];
    return {
        now: () => t,
        setTimeout(fn, ms) { const i = ++id; timers.push({ i, at: t + ms + late(), fn }); return i; },
        clearTimeout(i) { timers = timers.filter(x => x.i !== i); },
        advance(to) {
            for (;;) {
                timers.sort((a, b) => a.at - b.at || a.i - b.i);
                const n = timers[0];
                if (!n || n.at > to) break;
                timers.shift();
                t = Math.max(t, n.at);
                n.fn();
            }
            t = Math.max(t, to);
        },
    };
}

// A keyer at 20 WPM (60 ms unit) and a script of [time, paddle, down] steps.
function run(script, opts = {}) {
    const clock = fakeClock(opts.late);
    const elements = [], stuck = [];
    const keyer = new IambicKeyer({
        wpm: 20, clock, ...opts,
        onElement: e => elements.push([e.paddle, e.start, e.end]),
        onStuck: p => stuck.push([p, clock.now()]),
    });
    for (const [at, p, down] of script) {
        clock.advance(at);
        keyer.paddle(p, down);
    }
    clock.advance(opts.until ?? 20000);
    return { elements, stuck, keyer, clock };
}

test('a tap sends one element of the right length', () => {
    assert.deepEqual(run([[0, DIT, true], [20, DIT, false]]).elements, [[DIT, 0, 60]]);
    assert.deepEqual(run([[0, DAH, true], [20, DAH, false]]).elements, [[DAH, 0, 180]]);
});

test('a held paddle sends a string, one unit apart', () => {
    const { elements } = run([[0, DIT, true], [300, DIT, false]]);
    assert.deepEqual(elements, [[DIT, 0, 60], [DIT, 120, 180], [DIT, 240, 300]]);
});

test('a squeeze alternates, starting with the paddle pressed first', () => {
    const { elements } = run([[0, DIT, true], [5, DAH, true], [500, DIT, false], [500, DAH, false]]);
    assert.deepEqual(elements, [[DIT, 0, 60], [DAH, 120, 300], [DIT, 360, 420], [DAH, 480, 660]]);
});

test('mode B sends one more element after a squeeze is let go; mode A does not', () => {
    const script = [[0, DAH, true], [50, DIT, true], [100, DAH, false], [100, DIT, false]];
    assert.deepEqual(run(script, { mode: 'B' }).elements, [[DAH, 0, 180], [DIT, 240, 300]]);
    assert.deepEqual(run(script, { mode: 'A' }).elements, [[DAH, 0, 180]]);
});

test('the other paddle tapped on its own during an element is sent next, in both modes', () => {
    // N: dah, then a dit tapped and let go while the dah is still sounding.
    const script = [[0, DAH, true], [40, DAH, false], [100, DIT, true], [120, DIT, false]];
    for (const mode of ['A', 'B']) {
        assert.deepEqual(run(script, { mode }).elements, [[DAH, 0, 180], [DIT, 240, 300]], `mode ${mode}`);
    }
});

test('a timer firing up to 25 ms late does not stretch the spacing', () => {
    // The browser wakes the keyer 8 ms before each space ends; here every
    // wake-up is 15 ms late, so 7 ms after the space has ended.
    const { elements } = run([[0, DIT, true], [300, DIT, false]], { late: () => 15 });
    assert.deepEqual(elements, [[DIT, 0, 60], [DIT, 120, 180], [DIT, 240, 300]]);
});

test('a squeeze with timers late by random amounts up to 25 ms comes out as with exact timers', () => {
    const script = [[0, DIT, true], [5, DAH, true], [2000, DIT, false], [2000, DAH, false]];
    let seed = 7;
    const late = () => ((seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648) * (8 + 25);
    assert.deepEqual(run(script, { late }).elements, run(script).elements);
});

test('a timer more than 25 ms late starts the element when it fires', () => {
    // The wake-up for the second dit is due at 112 and fires at 152: 32 ms
    // after the space ended, too late to keep, so the dit starts at 152.
    const { elements } = run([[0, DIT, true], [200, DIT, false]], { late: () => 40 });
    assert.deepEqual(elements.slice(0, 2), [[DIT, 0, 60], [DIT, 152, 212]]);
});

test('a press during the space waits for the space to finish', () => {
    const { elements } = run([[0, DIT, true], [10, DIT, false], [70, DIT, true], [80, DIT, false]]);
    assert.deepEqual(elements, [[DIT, 0, 60], [DIT, 120, 180]]);
});

test('a paddle held past stuckMs is let go, and works again once released', () => {
    const { elements, stuck } = run([[0, DAH, true], [10000, DAH, false], [11000, DIT, true], [11010, DIT, false]],
                                    { stuckMs: 3000 });
    assert.deepEqual(stuck, [[DAH, 3000]]);
    const dahs = elements.filter(e => e[0] === DAH);
    assert.ok(dahs.length > 1, 'dahs were sent while held');
    assert.ok(dahs.every(e => e[1] < 3000), 'no dah starts after the paddle is called stuck');
    assert.deepEqual(elements.at(-1), [DIT, 11000, 11060]);
});

test('stop() cuts off the element being sent', () => {
    const clock = fakeClock();
    const stops = [];
    const keyer = new IambicKeyer({ wpm: 20, clock, onStop: t => stops.push(t) });
    keyer.paddle(DAH, true);
    clock.advance(100);
    keyer.stop();
    assert.deepEqual(stops, [100]);
    assert.equal(keyer.busy, false);
    clock.advance(1000);
    assert.equal(keyer.busy, false, 'a stopped keyer does not carry on with the held paddle');
});

test('a slow "0" is not called stuck, though it takes longer than stuckMs', () => {
    // Five dahs at 5 WPM: the dah paddle held 4.6 s, with stuckMs 3000.
    const { elements, stuck } = run([[0, DAH, true], [4300, DAH, false]], { wpm: 5, stuckMs: 3000 });
    assert.deepEqual(stuck, []);
    assert.equal(elements.length, 5);
    assert.deepEqual(elements.at(-1), [DAH, 3840, 4560]);
});

test('at 20 WPM a paddle is still called stuck after stuckMs', () => {
    const { stuck } = run([[0, DAH, true], [5000, DAH, false]], { stuckMs: 3000 });
    assert.deepEqual(stuck, [[DAH, 3000]]);
});

test('speed sets the unit', () => {
    const { elements } = run([[0, DIT, true], [5, DIT, false]], { wpm: 40 });
    assert.deepEqual(elements, [[DIT, 0, 30]]);
});

// The paddle emulator's tap mode (YWC_IWC_PADDLE firmware): each element's
// paddle is pressed at the start of its slot and let go half a dit later.
function emulatorTaps(text, wpm, t0 = 50) {
    const u = 1200 / wpm, steps = [];
    let t = t0;
    for (const ch of text) {
        if (ch === ' ') { t += 4 * u; continue; }
        for (const e of MORSE[ch]) {
            const p = e === '.' ? DIT : DAH;
            steps.push([t, p, true], [t + u / 2, p, false]);
            t += (p === DIT ? 1 : 3) * u + u;
        }
        t += 2 * u;
    }
    return steps;
}

for (const [text, wpm] of [['TEST', 20], ['CQ CQ DE MM5AGM K', 25], ['PARIS PARIS', 35], ['599 TU', 12]]) {
    test(`the emulator's taps for "${text}" at ${wpm} WPM come out as "${text}"`, () => {
        const clock = fakeClock();
        let copy = '';
        const decoder = new ElementDecoder(s => { copy += s; });
        const keyer = new IambicKeyer({
            wpm, clock, onElement: e => decoder.element(e.paddle, e.start, e.end, keyer.unitMs),
        });
        for (const [at, p, down] of emulatorTaps(text, wpm)) {
            clock.advance(at);
            keyer.paddle(p, down);
        }
        clock.advance(clock.now() + 2000);
        decoder.idle(clock.now());
        assert.equal(copy.trim(), text);
    });
}

for (const wpm of [5, 12, 25, 35, 50]) {
    test(`at ${wpm} WPM a squeeze with timers late by up to 25 ms is exact to the unit`, () => {
        const u = 1200 / wpm;
        const script = [[0, DIT, true], [1, DAH, true], [22 * u, DIT, false], [22 * u, DAH, false]];
        let seed = wpm;
        const late = () => ((seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648) * (8 + 25);
        const { elements, stuck } = run(script, { wpm, late, until: 60 * u });
        assert.deepEqual(stuck, []);
        assert.ok(elements.length >= 7);
        let t = 0;
        elements.forEach(([p, start, end], k) => {
            assert.equal(p, k % 2 === 0 ? DIT : DAH, `element ${k}`);
            assert.ok(Math.abs(start - t) < 1e-9, `element ${k} starts at ${start}, not ${t}`);
            const len = (p === DIT ? 1 : 3) * u;
            assert.ok(Math.abs(end - start - len) < 1e-9, `element ${k} is ${end - start} ms, not ${len}`);
            t = end + u;
        });
    });

    test(`at ${wpm} WPM the emulator's taps with late timers still come out right`, () => {
        let seed = wpm + 100;
        const late = () => ((seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648) * (8 + 25);
        const clock = fakeClock(late);
        let copy = '';
        const decoder = new ElementDecoder(s => { copy += s; });
        const keyer = new IambicKeyer({
            wpm, clock, onElement: e => decoder.element(e.paddle, e.start, e.end, keyer.unitMs),
        });
        for (const [at, p, down] of emulatorTaps('CQ DE MM5AGM', wpm)) {
            clock.advance(at);
            keyer.paddle(p, down);
        }
        clock.advance(clock.now() + 20 * 1200 / wpm);
        decoder.idle(clock.now());
        assert.equal(copy.trim(), 'CQ DE MM5AGM');
    });
}

test('the decoder brackets a pattern it does not know', () => {
    let copy = '';
    const d = new ElementDecoder(s => { copy += s; });
    for (let i = 0; i < 8; i++) d.element(DIT, i * 120, i * 120 + 60, 60);
    d.idle(5000);
    assert.equal(copy, '[........] ');
});
