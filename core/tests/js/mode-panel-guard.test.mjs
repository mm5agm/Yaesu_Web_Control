// ModePanelGuard closes a panel only once the mode has settled outside the
// panel's modes, and never on a blip. Timers are Node's own, mocked.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test, mock } from 'node:test';
import assert from 'node:assert/strict';
import { ModePanelGuard } from '../../js/modes/mode-panel-guard.js';

globalThis.document ??= { activeElement: null };

function setup({ open = true } = {}) {
    const state = { open, closed: 0, said: [] };
    const guard = new ModePanelGuard({
        name: 'RTTY tuner',
        belongs: m => m.startsWith('RTTY'),
        isOpen: () => state.open,
        close: () => { state.closed++; state.open = false; },
        announce: msg => state.said.push(msg),
        settleMs: 2000,
    });
    return { guard, state };
}

test('closes after the mode has held outside the group for settleMs', () => {
    mock.timers.enable({ apis: ['setTimeout'] });
    try {
        const { guard, state } = setup();
        guard.setMode('RTTY-L');
        guard.setMode('CW-U');
        mock.timers.tick(1999);
        assert.equal(state.closed, 0);
        mock.timers.tick(1);
        assert.equal(state.closed, 1);
        assert.deepEqual(state.said, ['RTTY tuner closed, mode is now CW-U']);
    } finally { mock.timers.reset(); }
});

test('a blip back into the group cancels the close', () => {
    mock.timers.enable({ apis: ['setTimeout'] });
    try {
        const { guard, state } = setup();
        guard.setMode('USB');
        mock.timers.tick(1000);
        guard.setMode('RTTY-U');
        mock.timers.tick(5000);
        assert.equal(state.closed, 0);
    } finally { mock.timers.reset(); }
});

test('moving between foreign modes counts from the first one', () => {
    mock.timers.enable({ apis: ['setTimeout'] });
    try {
        const { guard, state } = setup();
        guard.setMode('CW-U');
        mock.timers.tick(1500);
        guard.setMode('AM');
        mock.timers.tick(500);
        assert.equal(state.closed, 1);
        assert.deepEqual(state.said, ['RTTY tuner closed, mode is now AM']);
    } finally { mock.timers.reset(); }
});

test('a closed panel is left alone and nothing is said', () => {
    mock.timers.enable({ apis: ['setTimeout'] });
    try {
        const { guard, state } = setup({ open: false });
        guard.setMode('CW-U');
        mock.timers.tick(5000);
        assert.equal(state.closed, 0);
        assert.deepEqual(state.said, []);
    } finally { mock.timers.reset(); }
});

test('empty or missing modes are ignored', () => {
    mock.timers.enable({ apis: ['setTimeout'] });
    try {
        const { guard, state } = setup();
        guard.setMode('');
        guard.setMode(null);
        mock.timers.tick(5000);
        assert.equal(state.closed, 0);
    } finally { mock.timers.reset(); }
});

test('focus inside the panel goes back to the opening button', () => {
    mock.timers.enable({ apis: ['setTimeout'] });
    try {
        const { guard } = setup();
        const inside = {}, button = { focused: 0, focus() { this.focused++; } };
        globalThis.document.activeElement = inside;
        guard.setFocusReturn(el => el === inside, button);
        guard.setMode('CW-L');
        mock.timers.tick(2000);
        assert.equal(button.focused, 1);
    } finally { mock.timers.reset(); globalThis.document.activeElement = null; }
});
