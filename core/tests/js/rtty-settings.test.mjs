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
    assert.deepEqual(normaliseRttySettings({ markHz: 50, shiftHz: 2, baud: 0, reverse: 'yes' }),
                     { ...DEFAULT_RTTY_SETTINGS });
    assert.deepEqual(normaliseRttySettings({ markHz: 1415 }),
                     { ...DEFAULT_RTTY_SETTINGS, markHz: 1415 });
});

test('a shift no radio menu offers is kept, because the decoder can still use it', () => {
    // The point of the 2026-10-08 change. 450 Hz is the German weather stations
    // and 452 is one of them measured by the Auto button; both have to survive,
    // because what the tuner and the decoder run on is what is on the air, not
    // what the radio's own decoder can be told about.
    assert.equal(normaliseRttySettings({ shiftHz: 450 }).shiftHz, 450);
    assert.equal(normaliseRttySettings({ shiftHz: 452 }).shiftHz, 452);
    assert.equal(normaliseRttySettings({ shiftHz: 850 }).shiftHz, 850);

    // Still bounded, so a corrupt stored value cannot ask for a filter in the
    // wrong kilohertz.
    assert.equal(normaliseRttySettings({ shiftHz: 5000 }).shiftHz, DEFAULT_RTTY_SETTINGS.shiftHz);
    assert.equal(normaliseRttySettings({ shiftHz: -170 }).shiftHz, DEFAULT_RTTY_SETTINGS.shiftHz);
});

test('a speed is kept to the hundredth, named or not', () => {
    assert.equal(normaliseRttySettings({ baud: 45.45 }).baud, 45.45);
    assert.equal(normaliseRttySettings({ baud: 56.9 }).baud, 56.9);
    assert.equal(normaliseRttySettings({ baud: 68.327 }).baud, 68.33);
    assert.equal(normaliseRttySettings({ baud: 1 }).baud, DEFAULT_RTTY_SETTINGS.baud);
    assert.equal(normaliseRttySettings({ baud: 9600 }).baud, DEFAULT_RTTY_SETTINGS.baud);
});

test('settings saved before there was a speed read back at 45.45', () => {
    // The stored object gains a field. An upgrade must not leave the dialog
    // showing a blank speed or NaN.
    const store = memoryStorage({ rttyTuner: JSON.stringify({ markHz: 2125, shiftHz: 170, reverse: false }) });
    assert.equal(loadRttySettings(store).baud, 45.45);
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

test('a host list no longer narrows anything, and passing one is harmless', () => {
    // These three took a list of the radio's rungs and snapped a stored shift
    // onto it. They now assert the opposite, which is the behaviour Colin asked
    // for: a stored 850 stays 850 on a radio whose menu stops at 425, because
    // the tuner and the decoder do not go through that menu.
    //
    // A second argument is still accepted without complaint - JavaScript ignores
    // extra arguments - so a caller in the other app that has not been updated
    // keeps working rather than throwing.
    assert.equal(normaliseRttySettings({ markHz: 2125, shiftHz: 850 }, [170, 200, 425]).shiftHz, 850);
    assert.equal(normaliseRttySettings(null, [425, 850]).shiftHz, 170);
    assert.equal(normaliseRttySettings({ shiftHz: 850 }, []).shiftHz, 850);
});
