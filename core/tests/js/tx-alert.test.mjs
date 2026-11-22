// The high SWR / high ALC pop-up's text and dismiss list (YWC Discussion #257).
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { alertCards, dismissKey, rememberDismissed, DISMISSED_MAX } from '../../js/txalert/tx-alert.js';

const clock = () => '14:05:12';
const swr = (over = {}) => ({ id: 3, active: true, peak: 3.24, limit: 2.5, at: '2026-11-22T14:05:12Z', mode: 'USB', ...over });
const alc = (over = {}) => ({ id: 4, active: true, peak: 71.6, limit: 50, at: '2026-11-22T14:05:12Z', mode: 'DATA-U', ...over });
const snap = (over = {}) => ({ enabled: true, run: 'r1', swr: null, alc: null, ...over });

test('nothing to show when off, empty or missing', () => {
    assert.deepEqual(alertCards(null), []);
    assert.deepEqual(alertCards(snap()), []);
    assert.deepEqual(alertCards(snap({ enabled: false, swr: swr() })), []);
});

test('a live SWR alert says how high and what to do', () => {
    const [c] = alertCards(snap({ swr: swr() }), undefined, clock);
    assert.equal(c.kind, 'swr');
    assert.equal(c.active, true);
    assert.equal(c.title, 'HIGH SWR');
    assert.equal(c.value, '3.2 : 1');
    assert.match(c.detail, /limit of 2\.5 : 1/);
    assert.match(c.detail, /Stop transmitting/);
    assert.equal(c.key, 'r1:swr:3');
});

test('an ended SWR alert gives the peak and its time', () => {
    const [c] = alertCards(snap({ swr: swr({ active: false }) }), undefined, clock);
    assert.equal(c.active, false);
    assert.equal(c.title, 'High SWR on the last transmission');
    assert.match(c.detail, /Peak at 14:05:12/);
});

test('an ALC alert names the mode', () => {
    const [c] = alertCards(snap({ alc: alc() }), undefined, clock);
    assert.equal(c.title, 'HIGH ALC');
    assert.equal(c.value, '72%');
    assert.match(c.detail, /limit of 50% in DATA-U/);
});

test('SWR comes first when both are up', () => {
    const cards = alertCards(snap({ swr: swr(), alc: alc() }), undefined, clock);
    assert.deepEqual(cards.map(c => c.kind), ['swr', 'alc']);
});

test('a dismissed alert stays hidden, the next one shows', () => {
    const gone = new Set([dismissKey('r1', 'swr', 3)]);
    assert.deepEqual(alertCards(snap({ swr: swr() }), k => gone.has(k)), []);
    assert.equal(alertCards(snap({ swr: swr({ id: 5 }) }), k => gone.has(k)).length, 1);
});

test('a restarted app does not reuse an old dismissal', () => {
    const gone = new Set([dismissKey('r1', 'swr', 3)]);
    assert.equal(alertCards(snap({ run: 'r2', swr: swr() }), k => gone.has(k)).length, 1);
});

test('the dismiss list keeps the newest, once each', () => {
    let list = [];
    for (let i = 0; i < DISMISSED_MAX + 5; i++) list = rememberDismissed(list, `k${i}`);
    assert.equal(list.length, DISMISSED_MAX);
    assert.equal(list.at(-1), `k${DISMISSED_MAX + 4}`);
    list = rememberDismissed(list, 'k10');
    assert.equal(list.filter(k => k === 'k10').length, 1);
    assert.deepEqual(rememberDismissed('junk', 'a'), ['a']);
});
