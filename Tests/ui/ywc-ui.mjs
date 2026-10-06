// Yaesu Web Control - browser checks for the pages and pop-outs
//
//   node Tests/ui/ywc-ui.mjs [--base http://localhost:8080] [--headed] [--only text]
//
// The app must already be running. Nothing here reaches the radio: every
// write is stubbed and recorded, and the mode / frequency the pages see comes
// from the test, not from the radio (see core/tests/ui/browser-harness.mjs).
// So it is safe to run with the radio connected, but it does not prove
// anything about CAT itself - only that the pages do the right thing with
// what the radio reports.

import { Browser, Suite, expect, sleep } from '../../core/tests/ui/browser-harness.mjs';

const args = process.argv.slice(2);
const arg = (name, fallback) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : fallback; };
const BASE = arg('--base', 'http://localhost:8080').replace(/\/$/, '');
const ONLY = arg('--only', '');
const SETTLE = 2000 + 700; // core's MODE_SETTLE_MS, plus margin

const PAGES = [
    '/', '/About', '/ApplicationSetup', '/AudioFilter', '/Calibrations',
    '/Calibration/MeterCalibration', '/MeterCalibration',
    '/CwReader', '/CwSend', '/Diagnostics', '/DxSpots', '/flexui', '/Labels', '/Memories',
    '/Ports', '/RadioDisplay', '/RemoteAudio', '/RttyTuner', '/Settings', '/UserManual',
];

const suite = new Suite(`Yaesu Web Control UI checks against ${BASE}`);
const test = (name, fn) => { if (!ONLY || name.toLowerCase().includes(ONLY.toLowerCase())) suite.test(name, fn); };

let browser;
const open = (path, opts) => browser.open(BASE + path, opts);
const modeA = (page, mode) => page.state('ModeA', mode);
const visible = (page, sel) => page.eval(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); return !!e && !e.hidden && e.getClientRects().length > 0; })()`);
const text = (page, sel) => page.eval(`document.querySelector(${JSON.stringify(sel)})?.textContent ?? null`);

// Same-origin requests a page made that failed, minus ones that are about the
// radio's state rather than the page (a disconnected radio 5xx's some reads).
// KNOWN are faults that predate the pop-out work, reported as notes so a new
// one still fails. Remove a line when its fault is fixed.
const KNOWN = [
    // e.g. /^404 \/some\/old\.js/,   // why, and where it is tracked
];
const pageFaults = page => page.failed.filter(f => !/^5\d\d \/api\//.test(f) && !KNOWN.some(k => k.test(f)));
const knownFaults = page => [...new Set(page.failed.filter(f => KNOWN.some(k => k.test(f))))];

// ---------------------------------------------------------------- preflight

test('build serves the core modules and not the deleted local copies', async () => {
    const status = async p => (await fetch(BASE + p)).status;
    for (const p of ['/js/hub/hub-connection.js', '/js/dx/dx-spots-panel.js', '/js/popout/popout-mode-pause.js', '/js/modes/mode-panel-guard.js']) {
        expect(await status(p) === 200, `${p} is not served - is the app built from this branch?`);
    }
    for (const p of ['/js/ui/hub-connection.js', '/js/ui/dx-spots-panel.js']) {
        expect(await status(p) === 404, `${p} is still served - the app is running an older build`);
    }
});

// ---------------------------------------------------------------- every page

for (const path of PAGES) {
    test(`page loads cleanly: ${path}`, async note => {
        const page = await open(path, { settleMs: 2500 });
        try {
            const faults = pageFaults(page);
            for (const k of knownFaults(page)) note(`known, older than this branch: ${k}`);
            expect(faults.length === 0, `failed requests:\n${faults.join('\n')}`);
            expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
            const hub = await page.eval('({ started: __rwcTest.started, errors: __rwcTest.startErrors, handlers: __rwcTest.handlers.length })');
            expect(hub.errors.length === 0, `hub failed to start: ${hub.errors.join('; ')}`);
            if (hub.handlers && !hub.started) note('registered hub handlers but the hub had not connected after 2.5 s');
            const writes = await page.writes();
            if (writes.length) note(`writes on load (stubbed): ${writes.map(w => `${w.method} ${w.path}`).join(', ')}`);
            if (page.consoleErrors.length) note(`console errors: ${page.consoleErrors.slice(0, 3).join(' | ')}`);
        } finally { await page.close(); }
    });
}

// ---------------------------------------------------------------- flex UI

// The Experimental UI is a separate re-implementation of Index, so a module
// path that drifts there (/js/ui/hub-connection.js for /js/hub/…) would show
// up as a failed request and nothing else. This proves the page's module graph
// resolves and the shared panel dialogs the pop-out wiring depends on are
// present. It does not drive CAT.
test('Experimental UI (/flexui) loads its modules and shared dialogs', async () => {
    const page = await open('/flexui', { settleMs: 3000 });
    try {
        const faults = pageFaults(page);
        expect(faults.length === 0, `failed requests:\n${faults.join('\n')}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
        // onFlexReady() swallows per-callback errors to console.error, so a
        // broken import or a bad attachPopout wiring would not surface as a
        // page exception - only here.
        expect(page.consoleErrors.length === 0, `console errors:\n${page.consoleErrors.join('\n')}`);
        const hubType = await page.eval('typeof window.rwcHubConnection');
        expect(hubType === 'function', `window.rwcHubConnection is ${hubType}, not a function`);
        // The pop-out wiring runs inside onFlexReady; if it threw, these would
        // be undefined (its error is swallowed to console.error, checked above).
        for (const g of ['dxSpotsOpen', 'cwReaderOpen', 'rttyTunerOpen', 'cwSendOpen']) {
            const t = await page.eval(`typeof window.${g}`);
            expect(t === 'function', `window.${g} is ${t}, not a function`);
        }
        for (const id of ['ywcFlexHost', 'cwReaderDialog', 'cwSendDialog', 'rttyTunerDialog', 'dxSpotsDialog', 'audioFilterDialogA', 'audioFilterDialogB']) {
            const present = await page.eval(`!!document.getElementById(${JSON.stringify(id)})`);
            expect(present, `#${id} is missing on /flexui`);
        }
    } finally { await page.close(); }
});

test('Calibrations page shows the tables, not its loading text', async () => {
    const page = await open('/Calibrations');
    try {
        await page.waitFor(`!document.getElementById('calibration-app').textContent.includes('Loading')`,
            { timeoutMs: 5000, what: 'calibration-editor.js to replace the loading text' });
        const t = await text(page, '#calibration-app');
        expect(!t.includes('Failed'), `the page says: ${t.slice(0, 120)}`);
    } finally { await page.close(); }
});

// ---------------------------------------------------------------- pop-outs

async function modePopoutChecks({ path, pausedSel, announceSel, name, good, bad, note }) {
    const page = await open(path);
    try {
        await modeA(page, good);
        await page.waitFor(`document.querySelector(${JSON.stringify(pausedSel)}).hidden`, { what: `${name} running in ${good}` });

        // A blip out and straight back never pauses it.
        await modeA(page, bad);
        await sleep(400);
        await modeA(page, good);
        await sleep(SETTLE);
        expect(!(await visible(page, pausedSel)), `${name} paused after a 400 ms blip to ${bad}`);

        // Settled in the wrong mode: pauses, says why, and says so aloud.
        await page.clearWrites();
        await modeA(page, bad);
        await sleep(1000);
        expect(!(await visible(page, pausedSel)), `${name} paused before the settle time`);
        await sleep(SETTLE - 1000);
        expect(await visible(page, pausedSel), `${name} did not pause after ${bad} settled`);
        const why = await text(page, pausedSel);
        expect(why.includes(bad), `paused text does not name ${bad}: "${why}"`);
        await page.waitFor(`document.querySelector(${JSON.stringify(announceSel)}).textContent === ${JSON.stringify(`${name} paused, mode is now ${bad}`)}`,
            { timeoutMs: 2000, what: 'pause announcement' });
        const onPause = await page.writes();

        // Back in its mode: running again at once, no settle wait.
        await page.clearWrites();
        await modeA(page, good);
        await sleep(300);
        expect(!(await visible(page, pausedSel)), `${name} did not resume at once on ${good}`);
        await page.waitFor(`document.querySelector(${JSON.stringify(announceSel)}).textContent === ${JSON.stringify(`${name} running again`)}`,
            { timeoutMs: 2000, what: 'resume announcement' });
        const onResume = await page.writes();

        expect(page.exceptions.length === 0, `exceptions: ${page.exceptions.join('; ')}`);
        return { onPause, onResume };
    } finally { await page.close(); }
}

test('CW Reader pop-out pauses outside CW and resumes in CW', async note => {
    await modePopoutChecks({ path: '/CwReader', pausedSel: '#cwReaderPaused', announceSel: '#cwReaderAnnounce',
        name: 'CW reader', good: 'CW-U', bad: 'USB', note });
});

test('RTTY Tuner pop-out pauses outside RTTY/DATA/SSB and resumes', async note => {
    const { onPause, onResume } = await modePopoutChecks({ path: '/RttyTuner', pausedSel: '#rttyTunerPaused', announceSel: '#rttyTunerAnnounce',
        name: 'RTTY tuner', good: 'RTTY-L', bad: 'CW-U', note });
    expect(onPause.some(w => /\/api\/rtty\/tuner\/stop/.test(w.path)), `no tuner stop sent on pause (sent: ${onPause.map(w => w.path).join(', ') || 'nothing'})`);
    expect(onResume.some(w => /\/api\/rtty\/tuner\/start/.test(w.path)), `no tuner start sent on resume (sent: ${onResume.map(w => w.path).join(', ') || 'nothing'})`);
});

test('RTTY Tuner pop-out stays running in every mode it belongs to', async () => {
    const page = await open('/RttyTuner');
    try {
        for (const m of ['RTTY-L', 'RTTY-U', 'DATA-L', 'DATA-U', 'PSK', 'LSB', 'USB']) {
            await modeA(page, m);
        }
        await sleep(SETTLE);
        expect(!(await visible(page, '#rttyTunerPaused')), 'paused in one of its own modes');
    } finally { await page.close(); }
});

// ---------------------------------------------------------------- main page dialogs

test('Index: CW reader dialog closes when the mode settles outside CW, not on a blip', async () => {
    const page = await open('/', { settleMs: 2500 });
    try {
        await modeA(page, 'CW-U');
        await sleep(SETTLE);
        await page.eval(`document.getElementById('cwReaderDialog').open || window.cwReaderPanel.toggle()`);
        await page.waitFor(`document.getElementById('cwReaderDialog').open`, { what: 'CW reader dialog open' });

        await modeA(page, 'USB'); await sleep(400); await modeA(page, 'CW-U');
        await sleep(SETTLE);
        expect(await page.eval(`document.getElementById('cwReaderDialog').open`), 'closed on a 400 ms blip');

        await modeA(page, 'USB');
        await sleep(SETTLE);
        expect(!(await page.eval(`document.getElementById('cwReaderDialog').open`)), 'still open after USB settled');
    } finally { await page.close(); }
});

test('Index: RTTY tuner dialog closes when the mode settles in CW, stays in USB', async () => {
    const page = await open('/', { settleMs: 2500 });
    try {
        await modeA(page, 'RTTY-L');
        await sleep(SETTLE);
        await page.eval(`document.getElementById('rttyTunerDialog').open || window.rttyTuner.toggle()`);
        await page.waitFor(`document.getElementById('rttyTunerDialog').open`, { what: 'RTTY tuner dialog open' });

        await modeA(page, 'USB');
        await sleep(SETTLE);
        expect(await page.eval(`document.getElementById('rttyTunerDialog').open`), 'closed in USB, where it belongs');

        await modeA(page, 'CW-U');
        await sleep(SETTLE);
        expect(!(await page.eval(`document.getElementById('rttyTunerDialog').open`)), 'still open after CW settled');
    } finally { await page.close(); }
});

// ---------------------------------------------------------------- DX spots

const SPOT_HZ = 14_025_000;    // CW in every region's band plan
const SPOT = { callsign: 'T3ST', frequencyHz: SPOT_HZ, spotter: 'MM5AGM', comment: 'ui test', receivedUtc: new Date().toISOString(), isWatched: false };
const freqWrites = ws => ws.filter(w => w.path.startsWith('/api/cat/frequency/a'));
const modeWrites = ws => ws.filter(w => w.path.startsWith('/api/cat/mode/a'));

async function indexWithSpot() {
    const page = await open('/', { settleMs: 2500, before: `try { localStorage.setItem('dxOnlyWatched', '0'); } catch {}` });
    await page.eval(`(() => {
        window.dxOnlyWatched = false;
        window.dxSpotsPanel.setVfoFrequency(14_200_000);
        window.dxSpotsPanel.addSpot(${JSON.stringify(SPOT)});
        window.dxSpotsPanel.show();
    })()`);
    await page.waitFor(`document.getElementById('dxSpotsDialog').open && document.querySelector('#dxSpotsTbody tr[data-hz="${SPOT_HZ}"]')`, { what: 'test spot row' });
    return page;
}

for (const auto of [true, false]) {
    test(`Index: clicking a DX spot ${auto ? 'changes' : 'does not change'} mode with auto-mode ${auto ? 'ON' : 'OFF'}`, async () => {
        const page = await indexWithSpot();
        try {
            await page.eval(`window.ywcAutoModeChangeOnTune = ${auto}`);
            await page.clearWrites();
            await page.click(`#dxSpotsTbody tr[data-hz="${SPOT_HZ}"]`);
            await sleep(500);
            const ws = await page.writes();
            const f = freqWrites(ws);
            expect(f.length === 1 && f[0].body.frequencyHz === SPOT_HZ, `expected one frequency write of ${SPOT_HZ}, got ${JSON.stringify(f)}`);
            const m = modeWrites(ws);
            if (auto) expect(m.length === 1 && m[0].body.mode === '3', `expected one mode write of CW-U ("3"), got ${JSON.stringify(m)}`);
            else expect(m.length === 0, `mode written with auto-mode off: ${JSON.stringify(m)}`);
        } finally { await page.close(); }
    });
}

for (const auto of [true, false]) {
    test(`DxSpots pop-out: clicking a spot ${auto ? 'changes' : 'does not change'} mode with auto-mode ${auto ? 'ON' : 'OFF'}`, async () => {
        const before = `
            try { localStorage.setItem('dxOnlyWatched', '0'); } catch {}
            __rwcTest.getOverrides['/api/dxcluster/spots'] = [${JSON.stringify(SPOT)}];
            __rwcTest.getOverrides['/DxSpots?handler=AutoMode'] = { autoModeChangeOnTune: ${auto} };`;
        const page = await open('/DxSpots', { before });
        try {
            await page.state('FrequencyA', 14_200_000);
            await page.waitFor(`document.querySelector('#dxSpotsTbody tr[data-hz="${SPOT_HZ}"]')`, { what: 'test spot row' });
            await page.clearWrites();
            await page.click(`#dxSpotsTbody tr[data-hz="${SPOT_HZ}"]`);
            await sleep(800);
            const ws = await page.writes();
            const f = freqWrites(ws);
            expect(f.length === 1 && f[0].body.frequencyHz === SPOT_HZ, `expected one frequency write of ${SPOT_HZ}, got ${JSON.stringify(f)}`);
            const m = modeWrites(ws);
            if (auto) expect(m.length === 1 && m[0].body.mode === '3', `expected one mode write of CW-U ("3"), got ${JSON.stringify(m)}`);
            else expect(m.length === 0, `mode written with auto-mode off: ${JSON.stringify(m)}`);
        } finally { await page.close(); }
    });
}

test('DxSpots pop-out shows a spot pushed over the hub', async () => {
    const page = await open('/DxSpots', { before: `__rwcTest.getOverrides['/api/dxcluster/spots'] = [];` });
    try {
        await page.state('FrequencyA', 14_200_000);
        await page.state('DxSpot', { ...SPOT, callsign: 'T3ST2', frequencyHz: 14_030_000 });
        await page.waitFor(`document.querySelector('#dxSpotsTbody tr[data-hz="14030000"]')`, { timeoutMs: 3000, what: 'pushed spot row' });
    } finally { await page.close(); }
});

const dialogPos = page => page.eval(`(() => { const r = document.getElementById('dxSpotsDialog').getBoundingClientRect(); return { x: r.left, y: r.top }; })()`);
const allBands = page => page.eval(`document.getElementById('dxSpotsAllBandsChk').checked`);

test('Index: All bands switch toggles from its input and its label', async () => {
    const page = await indexWithSpot();
    try {
        const was = await allBands(page);
        await page.click('#dxSpotsAllBandsChk');
        expect(await allBands(page) === !was, 'clicking the switch itself did not toggle it');
        await page.click('label[for="dxSpotsAllBandsChk"]');
        expect(await allBands(page) === was, 'clicking the label did not toggle it');
    } finally { await page.close(); }
});

test('Index: a click on the All bands switch padding toggles it and does not drag', async note => {
    const page = await indexWithSpot();
    try {
        // A point inside the switch's div that is the div itself, not the
        // input or the label - the gap the README "Fixed since" row is about.
        const pt = await page.eval(`(() => {
            const div = document.getElementById('dxSpotsAllBandsChk').closest('.form-switch');
            const r = div.getBoundingClientRect();
            for (let y = r.top + 1; y < r.bottom; y += 2)
                for (let x = r.left + 1; x < r.right; x += 2)
                    if (document.elementFromPoint(x, y) === div) return { x, y };
            return null;
        })()`);
        if (!pt) { note('the switch div has no bare padding to click - every point is the input or label'); return; }
        const pos0 = await dialogPos(page);
        const was = await allBands(page);
        await page.drag(pt, { x: pt.x + 3, y: pt.y + 2 }, 2);   // a click with the slight wobble a real hand has
        const pos1 = await dialogPos(page);
        expect(Math.abs(pos1.x - pos0.x) < 1 && Math.abs(pos1.y - pos0.y) < 1, `the dialog moved by ${pos1.x - pos0.x}, ${pos1.y - pos0.y}`);
        expect(await allBands(page) === !was, `a click at (${pt.x}, ${pt.y}) on the switch padding did not toggle it`);
    } finally { await page.close(); }
});

test('Index: dragging the DX Spots title still moves the dialog', async () => {
    const page = await indexWithSpot();
    try {
        const from = await page.pointOf('#dxSpotsTitle');
        const pos0 = await dialogPos(page);
        await page.drag(from, { x: from.x + 120, y: from.y + 60 });
        const pos1 = await dialogPos(page);
        expect(Math.abs(pos1.x - pos0.x - 120) < 4 && Math.abs(pos1.y - pos0.y - 60) < 4,
            `expected a move of 120, 60; got ${pos1.x - pos0.x}, ${pos1.y - pos0.y}`);
    } finally { await page.close(); }
});

// ----------------------------------------------------------------

try {
    const up = await fetch(BASE + '/About').then(r => r.ok, () => false);
    if (!up) { console.error(`Nothing answering at ${BASE}. Start the app first.`); process.exit(2); }
    browser = await Browser.launch({ headless: !args.includes('--headed') });
    process.exitCode = await suite.run();
} finally {
    browser?.close();
}
