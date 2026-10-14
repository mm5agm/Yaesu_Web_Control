// The TX timeout warning's timer, on a fake clock.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { TxTimeout, clampTimeoutSeconds, timeoutText, stepTimeout, txTimeoutWarning, TX_TIMEOUT_KEY, TX_TIMEOUT_DEFAULT_S } from '../../js/radio-panel/tx-timeout.js';

function rig(limit) {
    let t = 0;
    let fn = null;
    const kv = new Map();
    if (limit != null) kv.set(TX_TIMEOUT_KEY, String(limit));
    const seen = [];
    const tt = new TxTimeout({
        now: () => t,
        timers: { setInterval: f => { fn = f; return 1; }, clearInterval: () => { fn = null; } },
        store: { get: k => kv.get(k) ?? null, set: (k, v) => kv.set(k, v) },
        onChange: s => seen.push(s),
    });
    const advance = s => { for (let i = 0; i < s; i++) { t += 1000; fn?.(); } };
    return { tt, advance, seen, kv, ticking: () => fn != null };
}

test('the limit is the shared key, clamped, defaulting to 120 s', () => {
    assert.equal(rig().tt.limit, TX_TIMEOUT_DEFAULT_S);
    assert.equal(rig(2).tt.limit, 5);
    assert.equal(rig(99999).tt.limit, 3600);
    assert.equal(rig('junk').tt.limit, 120);
    const r = rig();
    r.tt.limit = 45;
    assert.equal(r.kv.get(TX_TIMEOUT_KEY), '45');
});

test('trips at the limit of unbroken TX, not before', () => {
    const r = rig(10);
    r.tt.setTransmitting(true);
    r.advance(9);
    assert.equal(r.tt.tripped, false);
    assert.equal(r.tt.txSeconds, 9);
    r.advance(1);
    assert.equal(r.tt.tripped, true);
    assert.equal(r.seen.at(-1).tripped, true);
});

test('dropping to receive clears it and starts again from zero', () => {
    const r = rig(10);
    r.tt.setTransmitting(true);
    r.advance(12);
    r.tt.setTransmitting(false);
    assert.equal(r.tt.tripped, false);
    assert.equal(r.tt.txSeconds, 0);
    assert.equal(r.ticking(), false);
    r.tt.setTransmitting(true);
    r.advance(5);
    assert.equal(r.tt.tripped, false);
});

test('repeats of the same flag do not restart the clock', () => {
    const r = rig(10);
    r.tt.setTransmitting(true);
    r.advance(6);
    r.tt.setTransmitting(true);
    r.advance(4);
    assert.equal(r.tt.tripped, true);
});

test('dismiss lasts for this transmission only', () => {
    const r = rig(5);
    r.tt.setTransmitting(true);
    r.advance(6);
    r.tt.dismiss();
    assert.equal(r.tt.tripped, false);
    r.advance(30);
    assert.equal(r.tt.tripped, false);
    r.tt.setTransmitting(false);
    r.tt.setTransmitting(true);
    r.advance(5);
    assert.equal(r.tt.tripped, true);
});

test('lowering the limit below the time already on trips at once', () => {
    const r = rig(120);
    r.tt.setTransmitting(true);
    r.advance(40);
    r.tt.limit = 30;
    assert.equal(r.tt.tripped, true);
});

test('text and stepping', () => {
    assert.equal(timeoutText(90), '1:30');
    assert.equal(timeoutText(5), '0:05');
    assert.equal(clampTimeoutSeconds(0), 120);
    assert.equal(stepTimeout(120, 1), 180);
    assert.equal(stepTimeout(120, -1), 105);
    assert.equal(stepTimeout(30, -1), 25);
    assert.equal(stepTimeout(30, 1), 45);
    assert.equal(stepTimeout(5, -1), 5);
    assert.equal(stepTimeout(3600, 1), 3600);
});

test('the warning names the limit, as the main page words it', () => {
    assert.equal(txTimeoutWarning(90), '⚠ TX has been ON for more than 90 seconds — check your microphone, keyer or VOX!');
    assert.match(txTimeoutWarning('junk'), /more than 120 seconds/);
});
