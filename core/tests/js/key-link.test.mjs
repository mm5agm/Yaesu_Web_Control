import { test } from 'node:test';
import assert from 'node:assert/strict';
import { KeyLink } from '../../js/cw/key-link.js';

function link(opts = {}) {
    const sent = [];
    let now = 1000;
    const l = new KeyLink({ send: p => sent.push(p), clock: () => now, ...opts });
    return { l, sent, at: t => { now = t; } };
}

test('nothing is sent before start', () => {
    const { l, sent } = link();
    l.element(1000, 1060);
    assert.equal(sent.length, 0);
});

test('an element goes as a down and an up with running sequence numbers', () => {
    const { l, sent, at } = link();
    l.start();
    at(1010);
    l.element(1012, 1072);
    l.element(1132, 1312);
    const last = sent.at(-1);
    assert.equal(last.abort, false);
    assert.equal(last.now, 1010);
    assert.deepEqual(last.events, [
        { seq: 0, down: true, t: 1012 }, { seq: 1, down: false, t: 1072 },
        { seq: 2, down: true, t: 1132 }, { seq: 3, down: false, t: 1312 },
    ]);
    l.stop();
});

test('packets carry only the latest events, and pings repeat them', () => {
    const { l, sent } = link({ repeat: 4 });
    l.start();
    for (let k = 0; k < 5; k++) l.element(1000 + k * 120, 1060 + k * 120);
    l.ping();
    assert.deepEqual(sent.at(-1).events.map(e => e.seq), [6, 7, 8, 9]);
    l.stop();
});

test('events are repeated for keepMs, then dropped', () => {
    // A YWC restarted later must not be handed old events to play.
    const { l, sent, at } = link();
    l.start();
    l.element(1000, 1060);
    at(1900);
    l.ping();
    assert.deepEqual(sent.at(-1).events.map(e => e.seq), [0, 1]);
    at(2030);
    l.ping();
    assert.deepEqual(sent.at(-1).events.map(e => e.seq), [1]);
    at(2100);
    l.ping();
    assert.deepEqual(sent.at(-1).events, []);
    l.stop();
});

test('abort sends no events and later pings do not repeat old ones', () => {
    const { l, sent } = link();
    l.start();
    l.element(1000, 1060);
    l.abort();
    assert.deepEqual(sent.at(-1), { now: 1000, events: [], abort: true });
    l.ping();
    assert.deepEqual(sent.at(-1).events, []);
    l.element(1200, 1260);
    assert.deepEqual(sent.at(-1).events.map(e => e.seq), [2, 3]);   // numbering runs on
    l.stop();
});

test('stop tells the shack to let go', () => {
    const { l, sent } = link();
    l.start();
    l.stop();
    assert.equal(sent.at(-1).abort, true);
    assert.equal(l.running, false);
});
