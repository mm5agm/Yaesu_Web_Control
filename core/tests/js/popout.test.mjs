// Pop-out geometry and the host/child handshake. Node has BroadcastChannel
// built in, and two channels of one name in one process talk to each other,
// so the handshake runs for real; window and localStorage are stubs.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
    clampGeometry, featuresFor, PopoutHost, PopoutChild, MIN_WIDTH, MIN_HEIGHT,
} from '../../js/popout/popout.js';

const DEF = { width: 700, height: 420 };

// ── clampGeometry ────────────────────────────────────────────────────────────

test('nothing saved gives the default size and no position', () => {
    assert.deepEqual(clampGeometry(null, DEF), { width: 700, height: 420 });
    assert.deepEqual(clampGeometry(undefined, DEF), { width: 700, height: 420 });
    assert.deepEqual(clampGeometry('rubbish', DEF), { width: 700, height: 420 });
});

test('a saved geometry comes back as it was, rounded', () => {
    assert.deepEqual(
        clampGeometry({ width: 812.4, height: 515.6, left: 2600.2, top: 140.7 }, DEF),
        { width: 812, height: 516, left: 2600, top: 141 });
});

test('a size too small to use is raised to the minimum', () => {
    const g = clampGeometry({ width: 40, height: 10, left: 0, top: 0 }, DEF);
    assert.equal(g.width, MIN_WIDTH);
    assert.equal(g.height, MIN_HEIGHT);
});

test('a missing or broken size falls back to the default, not to zero', () => {
    const g = clampGeometry({ width: 'x', height: -5, left: 10, top: 10 }, DEF);
    assert.equal(g.width, 700);
    assert.equal(g.height, 420);
});

test('position on a second monitor is kept, including to the left of the main one', () => {
    // A monitor placed left of the primary has negative coordinates. Those
    // are real places, not errors, and clamping them to the current screen
    // would defeat the whole reason for a pop-out.
    const g = clampGeometry({ width: 700, height: 420, left: -1900, top: 60 }, DEF);
    assert.equal(g.left, -1900);
    assert.equal(g.top, 60);
});

test('half a position, or a nonsense one, is dropped entirely', () => {
    assert.deepEqual(clampGeometry({ width: 700, height: 420, left: 100 }, DEF),
        { width: 700, height: 420 });
    assert.deepEqual(clampGeometry({ width: 700, height: 420, left: 'a', top: 5 }, DEF),
        { width: 700, height: 420 });
    assert.deepEqual(clampGeometry({ width: 700, height: 420, left: 1e9, top: 5 }, DEF),
        { width: 700, height: 420 });
});

test('a position of zero is a real position', () => {
    assert.deepEqual(clampGeometry({ width: 700, height: 420, left: 0, top: 0 }, DEF),
        { width: 700, height: 420, left: 0, top: 0 });
});

test('featuresFor writes position only when there is one', () => {
    assert.equal(featuresFor({ width: 700, height: 420 }), 'popup=yes,width=700,height=420');
    assert.equal(featuresFor({ width: 700, height: 420, left: -5, top: 9 }),
        'popup=yes,width=700,height=420,left=-5,top=9');
});

// ── Handshake ────────────────────────────────────────────────────────────────

let seq = 0;
const freshName = () => `test-${process.pid}-${++seq}`;
const tick = () => new Promise(r => setTimeout(r, 30));

function stubBrowser() {
    const store = new Map();
    globalThis.localStorage = {
        getItem: k => (store.has(k) ? store.get(k) : null),
        setItem: (k, v) => store.set(k, String(v)),
    };
    const listeners = {};
    const opened = [];
    globalThis.window = {
        innerWidth: 640, innerHeight: 380, screenX: 2000, screenY: 100,
        closed: false,
        addEventListener: (t, f) => { (listeners[t] ??= []).push(f); },
        open: (url, name, features) => {
            const w = { url, name, features, closed: false, focus() { this.focused = true; } };
            opened.push(w);
            return w;
        },
        close() { this.closed = true; },
        location: { href: '' },
    };
    return { store, listeners, opened, fire: t => (listeners[t] ?? []).forEach(f => f()) };
}

test('the host hears the child open, close and reattach', async () => {
    const b = stubBrowser();
    const name = freshName();
    const seen = [];
    let reattached = 0;
    const host = new PopoutHost({
        name, url: '/X', defaultSize: DEF,
        onChange: open => seen.push(open),
        onReattach: () => reattached++,
    }).start();

    const child = new PopoutChild({ name }).start();
    await tick();
    assert.equal(host.isOpen, true);

    b.fire('pagehide');
    await tick();
    assert.equal(host.isOpen, false);

    child._post('opened');
    await tick();
    child.reattach();
    await tick();
    assert.equal(host.isOpen, false);
    assert.equal(reattached, 1);
    assert.deepEqual(seen, [true, false, true, false]);

    clearInterval(child._watch);
    host._channel.close(); child._channel.close();
});

test('a host that starts after the child finds it by pinging', async () => {
    stubBrowser();
    const name = freshName();
    const child = new PopoutChild({ name }).start();
    await tick();

    const host = new PopoutHost({ name, url: '/X', defaultSize: DEF }).start();
    await tick();
    assert.equal(host.isOpen, true, 'a main-page reload should still know the pop-out is there');

    clearInterval(child._watch);
    host._channel.close(); child._channel.close();
});

test('open uses a fixed window name and the saved geometry; a second click focuses', () => {
    const b = stubBrowser();
    const name = freshName();
    b.store.set('popoutGeom_' + name, JSON.stringify({ width: 900, height: 500, left: 2100, top: 50 }));
    const host = new PopoutHost({ name, url: '/X', defaultSize: DEF });

    assert.equal(host.open(), true);
    assert.equal(b.opened.length, 1);
    assert.equal(b.opened[0].name, 'rwc-popout-' + name);
    assert.equal(b.opened[0].features, 'popup=yes,width=900,height=500,left=2100,top=50');

    assert.equal(host.open(), true);
    assert.equal(b.opened.length, 1, 'the second click must not open another window');
    assert.equal(b.opened[0].focused, true);
});

test('a blocked pop-up reports false and leaves the host closed', () => {
    stubBrowser();
    window.open = () => null;
    const host = new PopoutHost({ name: freshName(), url: '/X', defaultSize: DEF });
    assert.equal(host.open(), false);
    assert.equal(host.isOpen, false);
});

test('the child saves its page-area size and screen position', () => {
    const b = stubBrowser();
    const name = freshName();
    const child = new PopoutChild({ name }).start();
    b.fire('pagehide');
    assert.deepEqual(JSON.parse(b.store.get('popoutGeom_' + name)),
        { width: 640, height: 380, left: 2000, top: 100 });
    clearInterval(child._watch);
    child._channel.close();
});
