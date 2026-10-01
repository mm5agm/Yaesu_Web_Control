// Folding a section away: the stored-value rule and the wiring. The DOM is a
// stub - just enough of a button and its targets to watch the attributes.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { isFoldedValue, attachFold } from '../../js/layout/fold-panel.js';

test('only an explicit 1 folds', () => {
    assert.equal(isFoldedValue('1'), true);
    for (const v of ['0', '', null, undefined, 'true', 'yes', '01']) {
        assert.equal(isFoldedValue(v), false, String(v));
    }
});

// ── stubs ────────────────────────────────────────────────────────────────────

class ClassList {
    constructor() { this.set = new Set(); }
    toggle(c, on) { if (on ?? !this.set.has(c)) this.set.add(c); else this.set.delete(c); }
    contains(c) { return this.set.has(c); }
}
function el(id = '') {
    const attrs = {};
    const handlers = {};
    return {
        id, hidden: false, title: '', textContent: '', classList: new ClassList(),
        setAttribute(k, v) { attrs[k] = String(v); },
        getAttribute(k) { return attrs[k] ?? null; },
        addEventListener(t, f) { handlers[t] = f; },
        click() { handlers.click?.(); },
        querySelector() { return null; },
    };
}
let store;
beforeEach(() => {
    store = {};
    globalThis.localStorage = {
        getItem: k => (k in store ? store[k] : null),
        setItem: (k, v) => { store[k] = String(v); },
    };
});

const LABELS = {
    shownLabel:  { text: 'Hide', title: 'Hide it', aria: 'Hide the meters' },
    foldedLabel: { text: 'Meters', title: 'Show it', aria: 'Show the meters' },
};

// ── attachFold ───────────────────────────────────────────────────────────────

test('starts showing when nothing is saved, and says so', () => {
    const b = el(), t = el('meterGaugesRow'), root = el();
    const f = attachFold({ name: 'meters', button: b, targets: [t], root, ...LABELS });
    assert.equal(f.folded, false);
    assert.equal(t.hidden, false);
    assert.equal(b.getAttribute('aria-expanded'), 'true');
    assert.equal(b.getAttribute('aria-controls'), 'meterGaugesRow');
    assert.equal(b.getAttribute('aria-label'), 'Hide the meters');
    assert.equal(root.classList.contains('is-folded'), false);
});

test('a click folds, hides every target, and is remembered', () => {
    const b = el(), t1 = el('a'), t2 = el('b'), root = el();
    attachFold({ name: 'm', button: b, targets: [t1, t2], root, ...LABELS });
    b.click();
    assert.equal(t1.hidden, true);
    assert.equal(t2.hidden, true);
    assert.equal(b.getAttribute('aria-expanded'), 'false');
    assert.equal(b.getAttribute('aria-label'), 'Show the meters');
    assert.equal(b.textContent, 'Meters');
    assert.equal(root.classList.contains('is-folded'), true);
    assert.equal(store.foldPanel_m, '1');
    b.click();
    assert.equal(t1.hidden, false);
    assert.equal(store.foldPanel_m, '0');
});

test('a saved fold is applied at start, and onChange hears it', () => {
    store.foldPanel_m = '1';
    const seen = [];
    const b = el(), t = el('x');
    attachFold({ name: 'm', button: b, targets: [t], ...LABELS, onChange: f => seen.push(f) });
    assert.equal(t.hidden, true);
    assert.deepEqual(seen, [true]);
});

test('storage that throws leaves the section showing', () => {
    globalThis.localStorage = { getItem() { throw new Error('off'); }, setItem() { throw new Error('off'); } };
    const b = el(), t = el('x');
    const f = attachFold({ name: 'm', button: b, targets: [t], ...LABELS });
    assert.equal(t.hidden, false);
    b.click();                      // must not throw
    assert.equal(f.folded, true);
});

test('nothing to wire gives null rather than throwing', () => {
    assert.equal(attachFold({ name: 'm', button: null, targets: [el()], ...LABELS }), null);
    assert.equal(attachFold({ name: 'm', button: el(), targets: [], ...LABELS }), null);
});
