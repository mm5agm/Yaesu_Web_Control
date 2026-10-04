// Pop-out geometry and the host/child handshake. Node has BroadcastChannel
// built in, and two channels of one name in one process talk to each other,
// so the handshake runs for real; window and localStorage are stubs.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
    clampGeometry, featuresFor, screenShare, frameFrom, pageAreaSize, usableSave, fitSize,
    PopoutHost, PopoutChild, MIN_WIDTH, MIN_HEIGHT,
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
        removeItem: k => store.delete(k),
    };
    delete globalThis.sessionStorage;
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
    b.store.set('popoutGeom_' + name, JSON.stringify({ width: 900, height: 500, left: 2100, top: 50, zoomSafe: true }));
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

test('a small size the operator chose is kept', () => {
    // Measured 2026-10-04 at 50% zoom: a pop-out shrunk to 154 x 91.
    assert.deepEqual(clampGeometry({ width: 154, height: 91, left: 2288, top: 89 }, DEF),
        { width: 154, height: 91, left: 2288, top: 89 });
});

test('the child saves its page-area size and screen position', () => {
    const b = stubBrowser();
    const name = freshName();
    const child = new PopoutChild({ name }).start();
    b.fire('pagehide');
    // No request was recorded, so the frame is unknown and the size unmarked.
    assert.deepEqual(JSON.parse(b.store.get('popoutGeom_' + name)),
        { width: 640, height: 380, left: 2000, top: 100 });
    clearInterval(child._watch);
    child._channel.close();
});

// ── Browser zoom ─────────────────────────────────────────────────────────────

test('the frame is the outer size less the size that was asked for', () => {
    assert.deepEqual(frameFrom({ width: 716, height: 459 }, { width: 700, height: 420 }), { w: 16, h: 39 });
});

test('no request, or a window not given its size, gives no frame', () => {
    assert.equal(frameFrom({ width: 716, height: 459 }, null), null);
    assert.equal(frameFrom({ width: 600, height: 459 }, { width: 700, height: 420 }), null);
    assert.equal(frameFrom({ width: 1900, height: 459 }, { width: 700, height: 420 }), null);
    assert.equal(frameFrom({ width: 716, height: 459 }, { width: 'x', height: 420 }), null);
});

test('the saved page area ignores zoom when the frame is known', () => {
    // 50% zoom: inner is twice the screen size, outer is not.
    const win = { innerWidth: 1400, innerHeight: 840, outerWidth: 716, outerHeight: 459 };
    assert.deepEqual(pageAreaSize(win, { w: 16, h: 39 }), { width: 700, height: 420 });
    assert.deepEqual(pageAreaSize(win, null), { width: 1400, height: 840 });
});

test('at 50% zoom a pop-out reopens the size it was left, not twice it', () => {
    const b = stubBrowser();
    const name = freshName();
    b.store.set('popoutGeom_' + name, JSON.stringify({ width: 700, height: 420, left: 2000, top: 100, zoomSafe: true }));
    const host = new PopoutHost({ name, url: '/X', defaultSize: DEF });
    host.open();
    assert.ok(b.store.has('popoutReq_' + name));
    Object.assign(globalThis.window, { innerWidth: 1400, innerHeight: 840, outerWidth: 716, outerHeight: 459 });
    const child = new PopoutChild({ name }).start();
    assert.equal(b.store.has('popoutReq_' + name), false);
    b.fire('pagehide');
    assert.deepEqual(JSON.parse(b.store.get('popoutGeom_' + name)),
        { width: 700, height: 420, left: 2000, top: 100, zoomSafe: true });
    clearInterval(child._watch);
    child._channel.close(); host._channel?.close();
});

test('a size saved before the zoom fix goes back to the default, keeping its place', () => {
    assert.deepEqual(usableSave({ width: 3800, height: 2100, left: 5, top: 6 }, null), { left: 5, top: 6 });
    assert.deepEqual(clampGeometry(usableSave({ width: 3800, height: 2100, left: 5, top: 6 }, null), DEF),
        { width: 700, height: 420, left: 5, top: 6 });
});

test('a saved size bigger than the screen goes back to the default', () => {
    const scr = { availWidth: 1920, availHeight: 1040 };
    assert.deepEqual(usableSave({ width: 2000, height: 500, left: 0, top: 0, zoomSafe: true }, scr), { left: 0, top: 0 });
    assert.deepEqual(usableSave({ width: 900, height: 1100, zoomSafe: true }, scr), {});
    // Tall on a short screen is a choice, not a fault.
    const short = { availWidth: 1608, availHeight: 651 };
    assert.deepEqual(usableSave({ width: 380, height: 600, zoomSafe: true }, short), { width: 380, height: 600 });
    assert.deepEqual(usableSave({ width: 900, height: 500, zoomSafe: true }, scr), { width: 900, height: 500 });
    assert.equal(usableSave(null, scr), null);
});

// ── Reattach carries the size back ───────────────────────────────────────────

test('the panel takes the pop-out size, cut to the main window', () => {
    const view = { width: 1600, height: 900 };
    assert.deepEqual(fitSize({ width: 328, height: 206 }, view), { width: 328, height: 206 });
    assert.deepEqual(fitSize({ width: 3000, height: 2000 }, view), { width: 1584, height: 884 });
    assert.deepEqual(fitSize({ width: 40, height: 20 }, view), { width: MIN_WIDTH, height: MIN_HEIGHT });
    assert.equal(fitSize(null, view), null);
    assert.equal(fitSize({ width: 0, height: 300 }, view), null);
});

test('reattach hands the pop-out page size to the host', async () => {
    const b = stubBrowser();
    const name = freshName();
    const sizes = [];
    const host = new PopoutHost({ name, url: '/X', defaultSize: DEF, onReattach: s => sizes.push(s) }).start();
    const child = new PopoutChild({ name }).start();
    await tick();
    child.reattach();
    await tick();
    assert.deepEqual(sizes, [{ width: 640, height: 380 }]);
    clearInterval(child._watch);
    host._channel.close(); child._channel.close();
});

// ── screenShare ──────────────────────────────────────────────────────────────

test('a quarter of the screen is half its width by half its height', () => {
    assert.deepEqual(screenShare(0.25, { availWidth: 1920, availHeight: 1040 }), { width: 960, height: 520 });
});

test('a share of a small or missing screen never goes under the minimum', () => {
    assert.deepEqual(screenShare(0.25, { availWidth: 200, availHeight: 100 }), { width: MIN_WIDTH, height: MIN_HEIGHT });
    assert.deepEqual(screenShare(0.25, undefined), { width: MIN_WIDTH, height: MIN_HEIGHT });
});

test('a default size given as a function is asked at open, and a saved size still wins', () => {
    const b = stubBrowser();
    const name = freshName();
    const host = new PopoutHost({ name, url: '/X', defaultSize: () => ({ width: 960, height: 520 }) });
    host.open();
    assert.match(b.opened[0].features, /width=960,height=520/);

    b.store.set('popoutGeom_' + name, JSON.stringify({ width: 500, height: 300, left: 10, top: 20, zoomSafe: true }));
    host._open = false;
    host.open();
    assert.match(b.opened[1].features, /width=500,height=300,left=10,top=20/);
});
