// startModePausedPopout pauses a pop-out once the mode has settled outside
// its modes and resumes it the moment the mode is back - where the main
// page's ModePanelGuard would close a dialog. Timers are Node's own, mocked.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test, mock } from 'node:test';
import assert from 'node:assert/strict';
import { startModePausedPopout } from '../../js/popout/popout-mode-pause.js';

globalThis.document ??= { activeElement: null };
globalThis.requestAnimationFrame ??= fn => fn();

function setup(initialMode = '') {
    const state = { started: 0, calls: [], announceEl: { textContent: '' } };
    const panel = {
        isPaused: false,
        setPaused(p, text) { this.isPaused = p; state.calls.push([p, text]); },
    };
    const pause = startModePausedPopout({
        name: 'RTTY tuner',
        panel,
        belongs: m => m.startsWith('RTTY'),
        pausedText: m => `Paused - the radio is in ${m}`,
        startNow: () => state.started++,
        initialMode,
        announceEl: state.announceEl,
    });
    return { pause, panel, state };
}

test('opened in one of its modes, it starts at once', () => {
    const { panel, state } = setup('RTTY-L');
    assert.equal(state.started, 1);
    assert.equal(panel.isPaused, false);
});

test('opened in the wrong mode, it starts paused with no settle wait', () => {
    const { panel, state } = setup('CW-U');
    assert.equal(state.started, 0);
    assert.equal(panel.isPaused, true);
    assert.deepEqual(state.calls, [[true, 'Paused - the radio is in CW-U']]);
});

test('pauses only after the mode has settled outside its modes', () => {
    mock.timers.enable({ apis: ['setTimeout'] });
    try {
        const { pause, panel, state } = setup('RTTY-L');
        pause.setMode('RTTY-L');
        pause.setMode('USB');
        assert.equal(panel.isPaused, false);
        mock.timers.tick(10000);
        assert.equal(panel.isPaused, true);
        assert.deepEqual(state.calls.at(-1), [true, 'Paused - the radio is in USB']);
        assert.equal(state.announceEl.textContent, 'RTTY tuner paused, mode is now USB');
    } finally { mock.timers.reset(); }
});

test('a blip out and straight back never pauses', () => {
    mock.timers.enable({ apis: ['setTimeout'] });
    try {
        const { pause, panel, state } = setup('RTTY-L');
        pause.setMode('USB');
        pause.setMode('RTTY-L');
        mock.timers.tick(10000);
        assert.equal(panel.isPaused, false);
        assert.equal(state.calls.length, 0);
    } finally { mock.timers.reset(); }
});

test('resumes the moment the mode is back, and says so', () => {
    const { pause, panel, state } = setup('CW-U');
    pause.setMode('RTTY-U');
    assert.equal(panel.isPaused, false);
    assert.deepEqual(state.calls.at(-1), [false, undefined]);
    assert.equal(state.announceEl.textContent, 'RTTY tuner running again');
});

test('ignores empty and non-string mode reports', () => {
    const { pause, panel } = setup('CW-U');
    pause.setMode('');
    pause.setMode(null);
    pause.setMode(3);
    assert.equal(panel.isPaused, true);
});
