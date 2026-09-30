// rtty-settings.js holds the Mark / Shift / Rev typed into the RTTY tuner,
// which click-to-tune also reads in AFSK modes. A wrong answer is silent: a
// click lands a few hundred hertz off and the operator's software copies
// nothing.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
    DEFAULT_RTTY_SETTINGS, normaliseRttySettings, loadRttySettings,
    saveRttySettings, afskMidpointAudioHz,
} from '../../js/rtty/rtty-settings.js';

function memoryStorage(initial = {}) {
    const m = new Map(Object.entries(initial));
    return { getItem: k => (m.has(k) ? m.get(k) : null), setItem: (k, v) => m.set(k, String(v)) };
}

test('midpoint of the 2125 / 170 default is 2210', () => {
    assert.equal(afskMidpointAudioHz(DEFAULT_RTTY_SETTINGS), 2210);
});

test('a 1415 Hz mark centres the pair on 1500 (VK2RT, discussion #169)', () => {
    assert.equal(afskMidpointAudioHz({ markHz: 1415, shiftHz: 170, reverse: false }), 1500);
});

test('Rev puts space below mark, so the midpoint moves down', () => {
    assert.equal(afskMidpointAudioHz({ markHz: 2125, shiftHz: 170, reverse: true }), 2040);
});

test('other shifts use half of themselves', () => {
    assert.equal(afskMidpointAudioHz({ markHz: 1275, shiftHz: 850, reverse: false }), 1700);
});

test('nonsense falls back to the defaults, field by field', () => {
    assert.deepEqual(normaliseRttySettings(null), { ...DEFAULT_RTTY_SETTINGS });
    assert.deepEqual(normaliseRttySettings({ markHz: 50, shiftHz: 171, reverse: 'yes' }), { ...DEFAULT_RTTY_SETTINGS });
    assert.deepEqual(normaliseRttySettings({ markHz: 1415 }), { markHz: 1415, shiftHz: 170, reverse: false });
});

test('what the tuner saves is what click-to-tune loads', () => {
    const store = memoryStorage();
    saveRttySettings({ markHz: 1415, shiftHz: 170, reverse: false }, store);
    assert.equal(afskMidpointAudioHz(loadRttySettings(store)), 1500);
});

test('reads the key the tuner has always used, so saved settings survive the upgrade', () => {
    const store = memoryStorage({ rttyTuner: JSON.stringify({ markHz: 1415, shiftHz: 170, reverse: false }) });
    assert.equal(loadRttySettings(store).markHz, 1415);
});

test('storage that throws gives the defaults', () => {
    const broken = { getItem() { throw new Error('blocked'); }, setItem() { throw new Error('blocked'); } };
    assert.deepEqual(loadRttySettings(broken), { ...DEFAULT_RTTY_SETTINGS });
    assert.doesNotThrow(() => saveRttySettings(DEFAULT_RTTY_SETTINGS, broken));
});

test('an allowed list narrows what a stored shift may be', () => {
    // The host page says which shifts its radio has; the IC-7300 has three.
    const icom = [170, 200, 425];
    assert.equal(normaliseRttySettings({ markHz: 2125, shiftHz: 850 }, icom).shiftHz, 170);
    assert.equal(normaliseRttySettings({ markHz: 2125, shiftHz: 425 }, icom).shiftHz, 425);
});

test('a default shift the radio lacks falls to its first rung', () => {
    assert.equal(normaliseRttySettings(null, [425, 850]).shiftHz, 425);
});

test('an empty or absent list keeps the standard set', () => {
    assert.equal(normaliseRttySettings({ shiftHz: 850 }, []).shiftHz, 850);
    assert.equal(normaliseRttySettings({ shiftHz: 850 }).shiftHz, 850);
});
