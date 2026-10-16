// Radio Web Control - browser test harness
// Shared by Icom Web Control and Yaesu Web Control. Each app keeps its own
// test script (page names, element ids, mode names); this file only drives
// a browser. Edit it here, in the core.
//
// Drives a real headless Chrome (or Edge) over the DevTools Protocol, with no
// npm packages: Node 22+ has a global WebSocket and fetch. The app under test
// must already be running - this never starts it.
//
// Nothing a test does can reach the radio. Before any page script runs:
//   - every fetch() that is not a GET, and every sendBeacon, is recorded in
//     __rwcTest.writes and answered with an empty 200. The one exception is
//     /radioHub (SignalR's own negotiate), so the hub still connects.
//   - every SignalR handler is wrapped. With __rwcTest.simOnly (the default)
//     messages from the real hub are dropped and only __rwcTest.emit() reaches
//     the page, so a test decides what the radio "is doing", not the radio.
// GETs go through to the app, so pages read real state, unless a test
// overrides a URL prefix in __rwcTest.getOverrides.
//
// Two lessons from verifying CSS this way (#163): set a class is not the
// real path, and programmatic scrolling/clicking skips the browser's input
// pipeline. click() here is a real mouse event through Input.dispatchMouseEvent.

import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const BROWSERS = [
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser',
];

export const sleep = ms => new Promise(r => setTimeout(r, ms));

// Runs in every page before its own scripts (Page.addScriptToEvaluateOnNewDocument).
const INSTRUMENT = String.raw`(() => {
    if (window.__rwcTest) return;
    const T = window.__rwcTest = {
        writes: [], getOverrides: {}, simOnly: true, inEmit: false,
        handlers: [], started: 0, startErrors: [], invokes: [],
    };
    const json = body => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    const realFetch = window.fetch.bind(window);
    window.fetch = async (input, init = {}) => {
        const url = typeof input === 'string' || input instanceof URL ? String(input) : input.url;
        const method = String(init.method || (typeof input === 'object' && input.method) || 'GET').toUpperCase();
        const u = new URL(url, location.href);
        const path = u.pathname + u.search;
        if (method === 'GET') {
            for (const [prefix, body] of Object.entries(T.getOverrides)) {
                if (path.startsWith(prefix)) return json(body);
            }
            return realFetch(input, init);
        }
        if (u.origin === location.origin && u.pathname.toLowerCase().startsWith('/radiohub')) {
            return realFetch(input, init);
        }
        let body = init.body;
        try { body = JSON.parse(body); } catch { /* leave as sent */ }
        T.writes.push({ method, path, body });
        return json({});
    };
    if (navigator.sendBeacon) {
        navigator.sendBeacon = (url, data) => { T.writes.push({ method: 'BEACON', path: String(url), body: data ?? null }); return true; };
    }
    const patch = sr => {
        const P = sr && sr.HubConnection && sr.HubConnection.prototype;
        if (!P || P.__rwcPatched) return;
        P.__rwcPatched = true;
        const on = P.on, start = P.start, invoke = P.invoke;
        P.on = function (name, fn) {
            const conn = this;
            const wrapped = function (...args) {
                if (T.simOnly && !T.inEmit) return;
                return fn.apply(this, args);
            };
            T.handlers.push({ conn, name: String(name).toLowerCase(), fn: wrapped });
            return on.call(this, name, wrapped);
        };
        P.start = function (...args) {
            const p = start.apply(this, args);
            p.then(() => { T.started++; }, e => { T.startErrors.push(String(e)); });
            return p;
        };
        P.invoke = function (name, ...args) {
            T.invokes.push(name);
            return invoke.call(this, name, ...args);
        };
    };
    let sr;
    Object.defineProperty(window, 'signalR', {
        configurable: true, enumerable: true,
        get() { return sr; },
        set(v) { sr = v; patch(v); },
    });
    T.emit = (name, ...args) => {
        const key = String(name).toLowerCase();
        let n = 0;
        T.inEmit = true;
        try {
            for (const h of T.handlers) {
                if (h.name !== key) continue;
                try { h.fn.apply(h.conn, args); } catch (e) { console.error('[rwcTest] handler threw', e); }
                n++;
            }
        } finally { T.inEmit = false; }
        return n;
    };
    T.state = (property, value) => T.emit('RadioStateUpdate', { property, value });
})();`;

class Cdp {
    constructor(wsUrl) {
        this._ws = new WebSocket(wsUrl);
        this._id = 0;
        this._pending = new Map();
        this._listeners = [];
        this.opened = new Promise((res, rej) => {
            this._ws.addEventListener('open', res, { once: true });
            this._ws.addEventListener('error', rej, { once: true });
        });
        this._ws.addEventListener('message', ev => {
            const msg = JSON.parse(ev.data);
            if (msg.id && this._pending.has(msg.id)) {
                const { res, rej } = this._pending.get(msg.id);
                this._pending.delete(msg.id);
                if (msg.error) rej(new Error(msg.error.message)); else res(msg.result);
            } else if (msg.method) {
                for (const l of this._listeners) l(msg.method, msg.params);
            }
        });
    }
    send(method, params = {}) {
        const id = ++this._id;
        this._ws.send(JSON.stringify({ id, method, params }));
        return new Promise((res, rej) => this._pending.set(id, { res, rej }));
    }
    on(fn) { this._listeners.push(fn); }
    close() { try { this._ws.close(); } catch { /* gone */ } }
}

export class Page {
    constructor(cdp, targetId, browser) {
        this.cdp = cdp;
        this.targetId = targetId;
        this._browser = browser;
        this.failed = [];       // same-origin requests that 404'd or failed
        this.exceptions = [];   // uncaught exceptions
        this.consoleErrors = [];
        this._loaded = null;
        this._requests = new Map();
    }

    async _init(baseUrl, before) {
        const origin = new URL(baseUrl).origin;
        this.cdp.on((method, p) => {
            if (method === 'Network.requestWillBeSent') this._requests.set(p.requestId, p.request.url);
            if (method === 'Network.responseReceived' && p.response.url.startsWith(origin) && p.response.status >= 400) {
                this.failed.push(`${p.response.status} ${p.response.url.slice(origin.length)}`);
            }
            if (method === 'Network.loadingFailed' && !p.canceled) {
                const url = this._requests.get(p.requestId) || '?';
                if (url.startsWith(origin) && !/\/radiohub/i.test(url)) this.failed.push(`${p.errorText} ${url.slice(origin.length)}`);
            }
            if (method === 'Runtime.exceptionThrown') {
                const d = p.exceptionDetails;
                this.exceptions.push((d.exception && d.exception.description || d.text || '').split('\n')[0]);
            }
            if (method === 'Runtime.consoleAPICalled' && p.type === 'error') {
                this.consoleErrors.push(p.args.map(a => a.value ?? a.description ?? '').join(' ').slice(0, 200));
            }
            if (method === 'Page.loadEventFired' && this._loaded) this._loaded();
        });
        await this.cdp.send('Page.enable');
        await this.cdp.send('Runtime.enable');
        await this.cdp.send('Network.enable');
        await this.cdp.send('Network.setCacheDisabled', { cacheDisabled: true });
        await this.cdp.send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
        await this.cdp.send('Page.addScriptToEvaluateOnNewDocument', { source: INSTRUMENT + '\n' + (before || '') });
    }

    async goto(url, { settleMs = 1500, timeoutMs = 20000 } = {}) {
        const loaded = new Promise(res => { this._loaded = res; });
        await this.cdp.send('Page.navigate', { url });
        await Promise.race([loaded, sleep(timeoutMs).then(() => { throw new Error(`load timed out: ${url}`); })]);
        await sleep(settleMs);
    }

    /** Evaluate an expression in the page; awaits promises; returns its value. */
    async eval(expression) {
        const r = await this.cdp.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, userGesture: true });
        if (r.exceptionDetails) {
            const d = r.exceptionDetails;
            throw new Error(`in page: ${(d.exception && d.exception.description) || d.text}`);
        }
        return r.result.value;
    }

    /** Poll until the expression is truthy; returns its value or throws. */
    async waitFor(expression, { timeoutMs = 10000, what = expression } = {}) {
        const end = Date.now() + timeoutMs;
        for (;;) {
            let v;
            try { v = await this.eval(expression); } catch { v = undefined; }
            if (v) return v;
            if (Date.now() > end) throw new Error(`timed out waiting for ${what}`);
            await sleep(100);
        }
    }

    emit(name, ...args) { return this.eval(`__rwcTest.emit(${JSON.stringify(name)}, ...${JSON.stringify(args)})`); }
    state(property, value) { return this.eval(`__rwcTest.state(${JSON.stringify(property)}, ${JSON.stringify(value)})`); }
    writes() { return this.eval('__rwcTest.writes'); }
    clearWrites() { return this.eval('__rwcTest.writes.length = 0'); }

    /** The centre of an element (or of rect = fn(rect)), in viewport pixels. */
    async pointOf(selector) {
        const p = await this.eval(`(() => {
            const el = document.querySelector(${JSON.stringify(selector)});
            if (!el) return null;
            el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
            const r = el.getBoundingClientRect();
            return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
        })()`);
        if (!p) throw new Error(`no element ${selector}`);
        return p;
    }

    async mouse(type, x, y) {
        await this.cdp.send('Input.dispatchMouseEvent', {
            type, x, y, button: type === 'mouseMoved' ? 'none' : 'left',
            buttons: type === 'mousePressed' ? 1 : 0, clickCount: type === 'mouseMoved' ? 0 : 1,
        });
    }

    /** A real click: move, press, release, through the browser's input pipeline. */
    async click(target) {
        const { x, y } = typeof target === 'string' ? await this.pointOf(target) : target;
        await this.mouse('mouseMoved', x, y);
        await this.mouse('mousePressed', x, y);
        await this.mouse('mouseReleased', x, y);
        await sleep(150);
    }

    /** A real drag from one point to another. */
    async drag(from, to, steps = 8) {
        await this.mouse('mouseMoved', from.x, from.y);
        await this.mouse('mousePressed', from.x, from.y);
        for (let i = 1; i <= steps; i++) {
            await this.mouse('mouseMoved', from.x + (to.x - from.x) * i / steps, from.y + (to.y - from.y) * i / steps);
        }
        await this.mouse('mouseReleased', to.x, to.y);
        await sleep(150);
    }

    async close() {
        this.cdp.close();
        try { await fetch(`${this._browser.devtools}/json/close/${this.targetId}`); } catch { /* gone */ }
    }
}

export class Browser {
    static async launch({ port = 9333, headless = true } = {}) {
        const exe = process.env.RWC_TEST_BROWSER || BROWSERS.find(p => existsSync(p));
        if (!exe) throw new Error('No Chrome or Edge found. Set RWC_TEST_BROWSER to its path.');
        const profile = mkdtempSync(join(tmpdir(), 'rwc-ui-test-'));
        const args = [
            `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
            '--no-first-run', '--no-default-browser-check', '--disable-extensions',
            '--autoplay-policy=no-user-gesture-required', 'about:blank',
        ];
        if (headless) args.unshift('--headless=new');
        const proc = spawn(exe, args, { stdio: 'ignore' });
        const b = new Browser(proc, `http://127.0.0.1:${port}`, profile);
        const end = Date.now() + 15000;
        for (;;) {
            try { const r = await fetch(`${b.devtools}/json/version`); if (r.ok) break; } catch { /* not up yet */ }
            if (Date.now() > end) { b.close(); throw new Error(`${exe} did not open its debugging port`); }
            await sleep(200);
        }
        return b;
    }

    constructor(proc, devtools, profile) { this.proc = proc; this.devtools = devtools; this.profile = profile; }

    /**
     * Open a page with the instrumentation in place, then navigate to url.
     * `before` is extra script run ahead of the page's own, after the
     * instrumentation - e.g. setting __rwcTest.getOverrides.
     */
    async open(url, { before = '', settleMs } = {}) {
        const r = await fetch(`${this.devtools}/json/new?about:blank`, { method: 'PUT' });
        const t = await r.json();
        const cdp = new Cdp(t.webSocketDebuggerUrl);
        await cdp.opened;
        const page = new Page(cdp, t.id, this);
        await page._init(url, before);
        await page.goto(url, { settleMs });
        return page;
    }

    close() {
        try { this.proc.kill(); } catch { /* gone */ }
        setTimeout(() => { try { rmSync(this.profile, { recursive: true, force: true }); } catch { /* locked */ } }, 1000).unref();
    }
}

/**
 * A minimal runner: test(name, fn) records, run() executes in order and
 * prints PASS / FAIL / NOTE lines. fn receives a `note(text)` for things
 * worth reading that are not failures. Exit code is the failure count.
 */
export class Suite {
    constructor(title) { this.title = title; this.tests = []; }
    test(name, fn) { this.tests.push({ name, fn }); }
    async run() {
        console.log(`\n${this.title}\n${'='.repeat(this.title.length)}`);
        let failed = 0;
        const failures = [];
        for (const t of this.tests) {
            const notes = [];
            try {
                await t.fn(text => notes.push(text));
                console.log(`PASS  ${t.name}`);
            } catch (e) {
                failed++;
                failures.push(t.name);
                console.log(`FAIL  ${t.name}\n      ${String(e.message || e).split('\n').join('\n      ')}`);
            }
            for (const n of notes) console.log(`      note: ${n}`);
        }
        console.log(`\n${this.tests.length - failed} passed, ${failed} failed`);
        for (const f of failures) console.log(`  failed: ${f}`);
        return failed;
    }
}

export function expect(cond, message) {
    if (!cond) throw new Error(message);
}
