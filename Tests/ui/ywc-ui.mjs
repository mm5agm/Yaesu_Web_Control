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
    '/CwReader', '/CwSend', '/Diagnostics', '/DxSpots', '/Labels', '/Memories',
    '/Ports', '/RadioDisplay', '/RemoteAudio', '/RttyTuner', '/Settings', '/UserManual',
    '/rtty-contest/window?p=both', '/rtty-contest/window?p=waterfall', '/RttyTuner?contest=true', '/RttyTuner?contest=true&decode=true',
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
    for (const p of ['/js/hub/hub-connection.js', '/js/dx/dx-spots-panel.js', '/js/popout/popout-mode-pause.js', '/js/modes/mode-panel-guard.js', '/js/dialogs/feature-setup.js']) {
        expect(await status(p) === 200, `${p} is not served - is the app built from this branch?`);
    }
    for (const p of ['/js/ui/hub-connection.js', '/js/ui/dx-spots-panel.js', '/js/ui/feature-setup.js']) {
        expect(await status(p) === 404, `${p} is still served - the app is running an older build`);
    }
});

// ------------------------------------------------------- feature set-up pop-outs
// core/js/dialogs/feature-setup.js, shared with IWC. The state is faked, so
// no test reads or writes the real settings.
const FS_STATE = {
    dxCluster: { enabled: false, host: '', port: 7300, callsign: '' },
    cwAudio: { rxDevice: 'Line (USB AUDIO  CODEC) [Windows WASAPI]', devices: [
        { key: 'Microphone (Webcam) [Windows WASAPI]', name: 'Microphone (Webcam) [Windows WASAPI]', likelyRadio: false },
        { key: 'Line (USB AUDIO  CODEC) [Windows WASAPI]', name: 'Line (USB AUDIO  CODEC) [Windows WASAPI]', likelyRadio: true },
    ] },
};
const fsOpen = path => open(path, { settleMs: 1500, before: `__rwcTest.getOverrides['/api/feature-setup/state'] = ${JSON.stringify(FS_STATE)};` });

for (const path of ['/', '/flexui'])
    test(`${path}: provides the CW audio and DX cluster set-up pop-outs`, async () => {
        const page = await fsOpen(path);
        try {
            await page.waitFor(`!!window.radioFeatureSetup`, { timeoutMs: 8000, what: 'window.radioFeatureSetup' });
            expect(await page.eval(`radioFeatureSetup.provides('cw-audio') && radioFeatureSetup.provides('dx-cluster')`), 'a pop-out is missing');
        } finally { await page.close(); }
    });

test('CW audio pop-out: lists the devices from /state, saves only the one picked', async () => {
    const page = await fsOpen('/');
    try {
        await page.waitFor(`!!window.radioFeatureSetup`, { timeoutMs: 8000, what: 'window.radioFeatureSetup' });
        await page.eval(`radioFeatureSetup.open('cw-audio')`);
        await page.waitFor(`document.getElementById('featureSetupDialog')?.open`, { what: 'the dialog' });
        const sel = await page.eval(`(() => { const s = document.getElementById('fsCwRx'); return { v: s.value, opts: [...s.options].map(o => o.textContent) }; })()`);
        expect(sel.v === 'Line (USB AUDIO  CODEC) [Windows WASAPI]', `selected "${sel.v}"`);
        expect(sel.opts.some(t => /CODEC\) \[Windows WASAPI\] \(looks like the radio\)/.test(t)), `options ${JSON.stringify(sel.opts)}`);
        await page.eval(`(() => { const s = document.getElementById('fsCwRx'); s.value = 'Microphone (Webcam) [Windows WASAPI]'; document.getElementById('featureSetupSave').click(); })()`);
        await page.waitFor(`__rwcTest.writes.length > 0`, { what: 'the save' });
        const w = await page.writes();
        expect(w.length === 1 && w[0].path === '/api/feature-setup/audio-rx' && w[0].body.device === 'Microphone (Webcam) [Windows WASAPI]', `wrote ${JSON.stringify(w)}`);
    } finally { await page.close(); }
});

test('DX cluster pop-out: saves the four cluster fields', async () => {
    const page = await fsOpen('/');
    try {
        await page.waitFor(`!!window.radioFeatureSetup`, { timeoutMs: 8000, what: 'window.radioFeatureSetup' });
        await page.eval(`radioFeatureSetup.open('dx-cluster')`);
        await page.waitFor(`document.getElementById('featureSetupDialog')?.open`, { what: 'the dialog' });
        await page.eval(`(() => {
            document.getElementById('fsDxHost').value = 'dx.example.org';
            document.getElementById('fsDxCall').value = 'n0call';
            document.getElementById('featureSetupSave').click();
        })()`);
        await page.waitFor(`__rwcTest.writes.length > 0`, { what: 'the save' });
        const w = await page.writes();
        expect(w.length === 1 && w[0].path === '/api/feature-setup/dx-cluster'
            && JSON.stringify(w[0].body) === JSON.stringify({ enabled: true, host: 'dx.example.org', port: 7300, callsign: 'n0call' }),
            `wrote ${JSON.stringify(w)}`);
    } finally { await page.close(); }
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

// Layout = null, so no site.js: radio-window-yaesu.js has to start the hub
// itself, and before 7097f86c it never did - the window loaded and stayed dead.
test('/RadioWindow?g=mem: the window draws and its hub connects', async () => {
    const page = await open('/RadioWindow?g=mem', { settleMs: 2500 });
    try {
        const r = await page.eval(`({ started: __rwcTest.started, panels: [...document.querySelectorAll('.rp-wpanel')].length, text: document.body.textContent })`);
        expect(!/No such window/.test(r.text), 'window group not found');
        expect(r.panels > 0, 'no panels drawn');
        expect(r.started, 'hub never started');
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

// Classic's Memory Keyer on /radio: the CW window's MEMORIES panel. Writes are
// stubbed, so M1 here never reaches the radio.
test('/RadioWindow?g=cw: MEMORIES shows M1-M5, saves, and a key sends its slot', async () => {
    const page = await open('/RadioWindow?g=cw', { settleMs: 2500 });
    try {
        await page.eval(`__rwcTest.getOverrides['/api/cat/cw/playing'] = { playing: false }`);
        const r = await page.eval(`(() => {
            const box = [...document.querySelectorAll('.rp-wpanel')].find(p => p.dataset.panel === 'cwMem');
            return box ? { keys: [...box.querySelectorAll('.rp-wkey')].map(k => k.textContent), boxes: box.querySelectorAll('input').length } : null;
        })()`);
        expect(r, 'no MEMORIES panel in the CW window');
        expect(r.boxes === 5, `expected 5 message boxes, got ${r.boxes}`);
        expect(['M1', 'M2', 'M3', 'M4', 'M5', 'SAVE'].every(k => r.keys.includes(k)), `keys: ${r.keys.join(', ')}`);

        await page.clearWrites();
        await page.eval(`(() => {
            const box = document.querySelector('.rp-wpanel[data-panel="cwMem"]');
            const inputs = box.querySelectorAll('input');
            inputs[0].value = 'CQ TEST';
            [...box.querySelectorAll('.rp-wkey')].find(k => k.textContent === 'SAVE').click();
        })()`);
        await sleep(300);
        let w = await page.writes();
        const save = w.find(x => x.path === '/api/cat/cw/messages');
        expect(save && Array.isArray(save.body) && save.body.length === 5 && save.body[0] === 'CQ TEST', `save write: ${JSON.stringify(w)}`);

        await page.clearWrites();
        await page.eval(`[...document.querySelectorAll('.rp-wpanel[data-panel="cwMem"] .rp-wkey')].find(k => k.textContent === 'M1').click()`);
        await sleep(300);
        w = await page.writes();
        const send = w.find(x => x.path === '/api/cat/cw/send');
        expect(send && send.body?.slot === 1 && send.body?.message === 'CQ TEST', `send write: ${JSON.stringify(w)}`);
        const keys = await page.eval(`[...document.querySelectorAll('.rp-wpanel[data-panel="cwMem"] .rp-wkey')].slice(0, 5).map(k => ({ t: k.textContent, d: k.disabled }))`);
        expect(keys[0].t.startsWith('▶') && keys.slice(1).every(k => k.d), `while playing: ${JSON.stringify(keys)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

test('/RadioWindow?g=cw: BREAK-IN click steps it, the wheel steps SPEED', async () => {
    const page = await open('/RadioWindow?g=cw', { settleMs: 2500 });
    try {
        const vals = `[...document.querySelectorAll('.rp-wpanel[data-panel="keyer"] .rp-wval')]`;
        await page.eval(`__rwcTest.state('CwSpeed', 22); __rwcTest.state('CwPitch', 30); __rwcTest.state('CwBreakIn', '1')`);
        await sleep(200);
        const shown = await page.eval(`(() => {
            const k = ${vals}.find(k => /^(OFF|SEMI|FULL|--)$/.test(k.textContent));
            return k ? k.textContent : null;
        })()`);
        expect(shown, 'no BREAK-IN value in the KEYER panel');
        const next = { OFF: '1', SEMI: '2', FULL: '0', '--': '0' }[shown];
        await page.clearWrites();
        await page.eval(`${vals}.find(k => k.textContent === ${JSON.stringify(shown)}).click()`);
        await sleep(300);
        let w = (await page.writes()).filter(x => x.path === '/api/cat/cw/breakin');
        expect(w.length === 1 && w[0].body?.mode === next, `shown ${shown}, writes: ${JSON.stringify(w)}`);

        // The wheel over SPEED steps it: up one notch is +1 wpm.
        const wpm = await page.eval(`parseInt(${vals}[0].textContent, 10)`);
        await page.clearWrites();
        await page.eval(`${vals}[0].parentElement.dispatchEvent(new WheelEvent('wheel', { deltaY: -100, bubbles: true, cancelable: true }))`);
        await sleep(300);
        w = (await page.writes()).filter(x => x.path === '/api/cat/cw/speed');
        expect(Number.isFinite(wpm) && w.length === 1 && w[0].body?.speed === wpm + 1, `speed ${wpm}, writes: ${JSON.stringify(w)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

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
        name: 'RTTY panel', good: 'RTTY-L', bad: 'CW-U', note });
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

// ------------------------------------------------------- front-panel state

// #228: the radio pushes MG over auto-information, the hub rebroadcasts it as
// "MicGain", and the slider used to sit still because no branch handled it.
test('Index: a MicGain broadcast moves the MIC Gain slider (#228)', async () => {
    const page = await open('/', { settleMs: 2500 });
    try {
        await page.state('MicGain', 33);
        await page.waitFor(`document.getElementById('micGainSlider')?.value === '33'`, { what: 'MIC Gain slider at 33' });
        expect(await text(page, '#micGainValue') === '33', `label shows ${await text(page, '#micGainValue')}`);
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

// The TX toggle key is site.js's. A shortcut on the same letter (u = USB)
// must not act too, held or not, and the ? list must say the key is taken.
const press = (page, key, code, extra = {}) => page.eval(`document.dispatchEvent(new KeyboardEvent('keydown',
    ${JSON.stringify({ key, code, bubbles: true, cancelable: true, ...extra })}))`);
for (const path of ['/', '/flexui']) test(`Keys: the TX key is not also a shortcut (${path})`, async () => {
    const page = await open(path, { settleMs: 2500 });
    try {
        for (const [p, v] of [['IsConnected', true], ['ActiveVfo', 0], ['FrequencyA', 14_074_000], ['ModeA', 'LSB']]) await page.state(p, v);
        // Control: with no TX key, u is USB, so the check below can fail.
        await page.eval(`window.ywcTxToggleKey = ''`);
        await page.clearWrites();
        await press(page, 'u', 'KeyU');
        expect(modeWrites(await page.writes()).length === 1, 'u did not set a mode with no TX key');

        await page.eval(`window.ywcTxToggleKey = 'u'`);
        await page.clearWrites();
        await press(page, 'u', 'KeyU');
        await press(page, 'u', 'KeyU', { repeat: true });
        const ws = await page.writes();
        expect(modeWrites(ws).length === 0, `the TX key also set the mode: ${JSON.stringify(modeWrites(ws))}`);
        const tx = ws.filter(w => w.path.startsWith('/api/cat/tx'));
        expect(tx.length === 1, `TX toggled ${tx.length} times for one press and one repeat`);

        // A held key: site.js ignores its repeats, so they reached the
        // shortcuts unmarked, and j (tune down) acted on every one.
        await page.eval(`window.ywcTxToggleKey = 'j'`);
        await page.clearWrites();
        await press(page, 'j', 'KeyJ', { repeat: true });
        await press(page, 'j', 'KeyJ', { repeat: true });
        await new Promise(r => setTimeout(r, 800));
        const held = freqWrites(await page.writes());
        expect(held.length === 0, `holding the TX key tuned: ${JSON.stringify(held)}`);

        await page.eval(`window.ywcTxToggleKey = 'u'`);
        await press(page, '?', 'Slash', { shiftKey: true });
        const help = await text(page, '#keyboardShortcutsDialog');
        expect(/no shortcut acts on this key/.test(help ?? ''), 'the ? list does not name the TX key');
    } finally { await page.close(); }
});

test('/RadioSpectrum: a click tunes there, and the readout names it first', async () => {
    const page = await open('/RadioSpectrum', { settleMs: 1500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the spectrum hub handler' });
        await page.state('FrequencyA', 14_074_000);
        await page.state('ModeA', 'USB');
        const bins = Array.from({ length: 475 }, () => -110);
        await page.state('SpectrumUpdate', { sdrId: 'A', bins, centreHz: 14_074_000, spanHz: 10_000, rfOrdered: true });
        // Three quarters of the way across a 10 kHz span on 14.074 is 14.0765.
        const read = await page.eval(`(() => {
            const el = document.querySelector('[data-pane="A"] .rp-spectrum');
            const rc = el.getBoundingClientRect(), x = rc.left + rc.width * 0.75, y = rc.top + rc.height / 2;
            el.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, clientX: x, clientY: y }));
            const t = el.querySelector('.rp-xread').textContent;
            el.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: x, clientY: y }));
            return t;
        })()`);
        expect(read === '14.076.500', `the readout said "${read}"`);
        await sleep(300);
        const w = (await page.writes()).filter(x => x.path === '/api/cat/frequency/a');
        expect(w.length === 1 && w[0].body?.frequencyHz === 14_076_500, `frequency wrote ${JSON.stringify(w)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

test('/RadioSpectrum: the mouse wheel steps the dial by the board STEP', async () => {
    const page = await open('/RadioSpectrum', { settleMs: 1500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the spectrum hub handler' });
        await page.state('FrequencyA', 12_000_550);
        const bins = Array.from({ length: 475 }, () => -110);
        await page.state('SpectrumUpdate', { sdrId: 'A', bins, centreHz: 12_000_550, spanHz: 10_000, rfOrdered: true });
        await page.eval(`localStorage.setItem('ywc.radioPanel.stepHz', '1000')`);
        // Off the bands, so no DX spot is near to snap to. The pointer stays
        // mid-pane: the readout follows the trace as it slides.
        const reads = await page.eval(`(() => {
            const el = document.querySelector('[data-pane="A"] .rp-spectrum');
            const rc = el.getBoundingClientRect(), x = rc.left + rc.width / 2, y = rc.top + rc.height / 2;
            el.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, clientX: x, clientY: y }));
            const before = el.querySelector('.rp-xread').textContent;
            el.dispatchEvent(new WheelEvent('wheel', { bubbles: true, cancelable: true, clientX: x, clientY: y, deltaY: -100 }));
            return [before, el.querySelector('.rp-xread').textContent];
        })()`);
        expect(reads[0] === '12.000.550' && reads[1] === '12.001.000', `the readout said ${JSON.stringify(reads)}`);
        await sleep(300);
        let w = (await page.writes()).filter(x => x.path === '/api/cat/frequency/a');
        expect(w.length === 1 && w[0].body?.frequencyHz === 12_001_000, `a notch up wrote ${JSON.stringify(w)}`);
        await page.eval(`(() => {
            const el = document.querySelector('[data-pane="A"] .rp-waterfall');
            el.dispatchEvent(new WheelEvent('wheel', { bubbles: true, cancelable: true, deltaY: 100 }));
        })()`);
        await sleep(300);
        w = (await page.writes()).filter(x => x.path === '/api/cat/frequency/a');
        expect(w.length === 2 && w[1].body?.frequencyHz === 12_000_000, `a notch down wrote ${JSON.stringify(w)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.eval(`localStorage.removeItem('ywc.radioPanel.stepHz')`).catch(() => {}); await page.close(); }
});

test('/rtty-contest: the tuner is docked, the strip follows the radio, and a click never changes the mode (#169)', async () => {
    const page = await open('/rtty-contest', { settleMs: 1500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the contest hub handler' });
        const docked = await page.eval(`!!document.querySelector('.rc-tuner > #rttyTunerDialog[open]')`);
        expect(docked, 'the RTTY tuner is not open in the side slot');
        // 14.030 in USB: the band plan says CW there, which this page must not write.
        await page.state('FrequencyA', 14_030_000);
        await page.state('ModeA', 'USB');
        const bins = Array.from({ length: 475 }, () => -110);
        await page.state('SpectrumUpdate', { sdrId: 'A', bins, centreHz: 14_030_000, spanHz: 10_000, rfOrdered: true });
        const strip = await page.eval(`document.querySelector('.rc-freq').textContent + ' ' + document.querySelector('.rc-top .rc-mode').selectedOptions[0]?.textContent`);
        expect(strip === '14.030.000 USB', `the strip said "${strip}"`);
        await page.eval(`(() => {
            const el = document.querySelector('.rc-holder:not([hidden]) .rp-spectrum');
            const rc = el.getBoundingClientRect(), x = rc.left + rc.width * 0.75, y = rc.top + rc.height / 2;
            el.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, clientX: x, clientY: y }));
            el.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: x, clientY: y }));
        })()`);
        await sleep(300);
        const all = await page.writes();
        const f = all.filter(x => x.path === '/api/cat/frequency/a');
        expect(f.length === 1 && f[0].body?.frequencyHz === 14_032_500, `frequency wrote ${JSON.stringify(f)}`);
        const m = all.filter(x => x.path.startsWith('/api/cat/mode'));
        expect(m.length === 0, `a mode was written: ${JSON.stringify(m)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

test('/rtty-contest: a typed frequency tunes, and DX SPOTS hides the spot tags', async () => {
    const page = await open('/rtty-contest', { settleMs: 1500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the contest hub handler' });
        await page.eval(`(() => {
            document.querySelector('.rc-freq').click();
            const i = document.querySelector('.rc-freq-in');
            i.value = '7.0405';
            i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
        })()`);
        await sleep(300);
        const f = (await page.writes()).filter(x => x.path === '/api/cat/frequency/a');
        expect(f.length === 1 && f[0].body?.frequencyHz === 7_040_500, `frequency wrote ${JSON.stringify(f)}`);

        await page.eval(`localStorage.setItem('ywc.rttyContest.spots', '1')`);
        await page.state('FrequencyA', 14_080_000);
        const bins = Array.from({ length: 475 }, () => -110);
        await page.state('SpectrumUpdate', { sdrId: 'A', bins, centreHz: 14_080_000, spanHz: 10_000, rfOrdered: true });
        await page.state('DxSpot', { callsign: 'T3ST', frequencyHz: 14_081_000, receivedUtc: new Date().toISOString() });
        // The live cluster may add its own spots near 14.080, so look for ours.
        const tags = () => page.eval(`[...document.querySelectorAll('.rc-holder:not([hidden]) .rp-spot')].map(t => t.textContent)`);
        const before = await tags();
        await page.eval(`[...document.querySelectorAll('.rc-top button')].find(b => b.textContent === 'DX SPOTS').click()`);
        const after = await tags();
        expect(before.includes('T3ST') && after.length === 0, `spot tags before/after the switch: ${before}/${after}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.eval(`localStorage.removeItem('ywc.rttyContest.spots')`).catch(() => {}); await page.close(); }
});

test('/rtty-contest: a band key goes to the RTTY spot in RTTY-L, 60 m to the band memory, and the wheel over a digit tunes', async () => {
    const page = await open('/rtty-contest', { settleMs: 1500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the contest hub handler' });
        const keys = await page.eval(`[...document.querySelectorAll('.rc-brow-A .rc-band')].map(b => b.dataset.band)`);
        expect(keys.includes('160m') && keys.includes('6m') && keys.includes('60m'), `MAIN band keys: ${keys}`);
        // The page starts in the RTTY mode saved in Settings, which may not be
        // RTTY-L on this PC. Pick it on the MODE list (the save is stubbed).
        await page.eval(`(() => { const s = document.querySelector('.rc-top .rc-mode'); s.value = 'RTTY-L'; s.dispatchEvent(new Event('change')); })()`);
        await sleep(400);
        await page.clearWrites();
        const band = b => page.eval(`document.querySelector('.rc-brow-A .rc-band[data-band="${b}"]').click()`);
        await band('20m');
        await sleep(400);
        let all = await page.writes();
        const f = all.filter(x => x.path === '/api/cat/frequency/a');
        const m = all.filter(x => x.path === '/api/cat/mode/a');
        expect(f.length === 1 && f[0].body?.frequencyHz === 14_080_000, `20m wrote ${JSON.stringify(f)}`);
        expect(m.length === 1 && m[0].body?.mode === '6', `20m mode wrote ${JSON.stringify(m)}`);
        await band('60m');
        await sleep(400);
        all = await page.writes();
        const b = all.filter(x => x.path === '/api/cat/band/a');
        expect(b.length === 1 && b[0].body?.band === '60m', `60m wrote ${JSON.stringify(b)}`);
        expect(all.filter(x => x.path === '/api/cat/mode/a').length === 2, 'no RTTY-L after the 60m band memory');

        // The key lit follows the frequency, however it got there.
        await page.state('FrequencyA', 7_040_000);
        const lit = await page.eval(`[...document.querySelectorAll('.rc-brow-A .rc-band.on')].map(b => b.dataset.band).join()`);
        expect(lit === '40m', `lit: "${lit}"`);

        // The wheel up over the kHz digit: one kHz up.
        await page.eval(`(() => {
            const d = document.querySelector('.rc-freq span[data-step="1000"]');
            d.dispatchEvent(new WheelEvent('wheel', { deltaY: -100, bubbles: true, cancelable: true }));
        })()`);
        await sleep(400);
        const w = (await page.writes()).filter(x => x.path === '/api/cat/frequency/a').slice(1);
        expect(w.length === 1 && w[0].body?.frequencyHz === 7_041_000, `the wheel wrote ${JSON.stringify(w)}`);
        const strip = await page.eval(`document.querySelector('.rc-freq').textContent`);
        expect(strip === '7.041.000', `the strip said "${strip}"`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

// Bruce VK2RT, 2026-10-26: he runs AFSK in DATA-L, so the band keys and
// the tuner must not keep putting the radio in RTTY-L. The MODE list shows
// the receiver's mode and keeps what is picked as the RTTY mode.
test('/rtty-contest: MODE shows the mode, a pick is kept before it is written, and the band keys use it', async () => {
    const page = await open('/rtty-contest', { settleMs: 1500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the contest hub handler' });
        const shown = () => page.eval(`(() => { const s = document.querySelector('.rc-top .rc-mode'); return s.value + '|' + s.selectedOptions[0]?.textContent; })()`);
        const opts = await page.eval(`[...document.querySelectorAll('.rc-top .rc-mode option:not([disabled])')].map(o => o.value).join()`);
        expect(opts === 'RTTY-L,RTTY-U,DATA-L,DATA-U', `MODE offers ${opts}`);
        await page.state('ModeA', 'RTTY-U');
        let s = await shown();
        expect(s === 'RTTY-U|RTTY-U', `in RTTY-U MODE shows ${s}`);
        await page.state('ModeA', 'CW-U');
        s = await shown();
        expect(s === '|CW-U', `in CW-U MODE shows ${s}`);

        await page.eval(`(() => { const s = document.querySelector('.rc-top .rc-mode'); s.value = 'DATA-L'; s.dispatchEvent(new Event('change')); })()`);
        await sleep(400);
        let all = (await page.writes()).filter(x => x.path === '/api/rtty/mode' || x.path.startsWith('/api/cat/mode'));
        expect(all.length === 2 && all[0].path === '/api/rtty/mode' && all[0].body?.mode === 'DATA-L'
            && all[1].path === '/api/cat/mode/a' && all[1].body?.mode === '8', `the pick wrote ${JSON.stringify(all)}`);

        await page.eval(`document.querySelector('.rc-brow-A .rc-band[data-band="20m"]').click()`);
        await sleep(400);
        const m = (await page.writes()).filter(x => x.path === '/api/cat/mode/a');
        expect(m.length === 2 && m[1].body?.mode === '8', `the 20m key wrote ${JSON.stringify(m)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

// Bruce VK2RT, 2026-10-26: power, bandwidth and shift were the controls he
// wanted most. They sit in a box under the tuner.
test('/rtty-contest: PWR, WIDTH and SHIFT show the radio and write what is picked', async () => {
    const page = await open('/rtty-contest', { settleMs: 1500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the contest hub handler' });
        for (const [p, v] of [['ModeA', 'RTTY-L'], ['IfWidthA', '11'], ['IfShiftA', 120], ['Power', 50]]) await page.state(p, v);
        await sleep(200);
        const shown = await page.eval(`['power', 'width', 'shift'].map(id => { const r = document.querySelector('.rc-ctl .rc-c-' + id); return r.querySelector('input,select').value + '|' + (r.querySelector('.rc-num')?.textContent ?? ''); }).join(' ')`);
        expect(shown === '50|50 W 11| 120|+120 Hz', `the box showed "${shown}"`);
        const widths = await page.eval(`document.querySelectorAll('.rc-ctl .rc-c-width option').length`);
        expect(widths > 5, `WIDTH offers ${widths} widths in RTTY-L`);
        await page.state('ModeA', 'FM');
        expect(await page.eval(`document.querySelector('.rc-ctl .rc-c-width select').disabled`), 'WIDTH is not greyed in FM');
        await page.state('ModeA', 'RTTY-L');

        await page.clearWrites();
        await page.eval(`(() => {
            const w = document.querySelector('.rc-ctl .rc-c-width select'); w.value = '9'; w.dispatchEvent(new Event('change'));
            const p = document.querySelector('.rc-ctl .rc-c-power input'); p.value = '30'; p.dispatchEvent(new Event('input')); p.dispatchEvent(new Event('change'));
            document.querySelector('.rc-ctl .rc-c-shift input').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
        })()`);
        await sleep(400);
        const w = (await page.writes()).map(x => `${x.path} ${JSON.stringify(x.body)}`);
        expect(w.includes('/api/cat/ifwidth/a {"code":"9"}') && w.includes('/api/cat/power/a {"Watts":30}') && w.includes('/api/cat/ifshift/a {"shiftHz":0}') && w.length === 3,
            `the box wrote ${JSON.stringify(w)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

// Bruce VK2RT, 2026-10-26: he never uses SUB, and wants only the bands he
// works. BANDS hides band keys per row, and SUB with its tab and its row.
test('/rtty-contest: BANDS hides band keys per row, and SUB with its tab and band row', async () => {
    const page = await open('/rtty-contest', { settleMs: 1500 });
    const clear = `['hiddenReceivers', 'hiddenBands.A', 'hiddenBands.B', 'receiver'].forEach(k => localStorage.removeItem('ywc.rttyContest.' + k))`;
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the contest hub handler' });
        expect(await page.eval(`!!document.querySelector('.rc-brow-B') && !!document.querySelector('.rp-spop-tab:nth-child(2)')`), 'this test wants a radio with SUB');
        const seen = sel => page.eval(`[...document.querySelectorAll('${sel}')].filter(e => e.offsetParent !== null).length`);
        await page.eval(`document.querySelector('.rc-bandset-key').click()`);
        expect(await page.eval(`document.querySelector('.rc-bandset').open`), 'BANDS did not open its dialog');
        // Untick 60 m on MAIN: gone from MAIN only.
        const untick = (row, band) => page.eval(`(() => { const l = [...document.querySelectorAll('.rc-bandrow-${row} label')].find(l => l.textContent === '${band}'); const c = l.querySelector('input'); c.checked = false; c.dispatchEvent(new Event('change')); })()`);
        await untick('A', '60m');
        expect(await seen('.rc-brow-A .rc-band[data-band="60m"]') === 0, '60m still shows on MAIN');
        expect(await seen('.rc-brow-B .rc-band[data-band="60m"]') === 1, '60m went from SUB too');

        // Put the page on SUB, then hide SUB: the page goes back to MAIN.
        await page.eval(`document.querySelector('.rp-spop-tab:nth-child(2)').click()`);
        await page.eval(`(() => { const c = document.querySelector('.rc-tick-rx-B input'); c.checked = false; c.dispatchEvent(new Event('change')); })()`);
        await page.eval(`document.querySelector('.rc-bandset').close()`);
        expect(await seen('.rc-brow-B') === 0 && await seen('.rp-spop-tabs') === 0, 'the SUB row or the tabs still show');
        const rx = await page.eval(`localStorage.getItem('ywc.rttyContest.receiver')`);
        expect(rx === 'A', `the page is on ${rx} with SUB hidden`);

        // The choices last.
        await page.eval(`location.reload()`);
        await sleep(1500);
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the contest hub handler after a reload' });
        expect(await seen('.rc-brow-B') === 0 && await seen('.rc-brow-A .rc-band[data-band="60m"]') === 0 && await seen('.rc-brow-A .rc-band[data-band="20m"]') === 1,
            'the choices did not last a reload');
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.eval(clear).catch(() => {}); await page.close(); }
});

// Bruce VK2RT, 2026-10-26: arrows on the spectrum that move one span, to
// look along the band. With the waterfall on top they go to the waterfall.
test('/rtty-contest: the arrows move the dial one span down or up, and go with the part on top', async () => {
    const page = await open('/rtty-contest', { settleMs: 2500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the contest hub handler' });
        await page.state('FrequencyA', 14_080_000);
        const bins = Array.from({ length: 400 }, () => 40);
        await page.state('SpectrumUpdate', { sdrId: 'A', bins, centreHz: 14_080_000, spanHz: 10_000, rfOrdered: true });
        await sleep(300);
        const where = () => page.eval(`[...document.querySelectorAll('.rc-holder:not([hidden]) .rp-jump')].map(b => b.parentElement.className.split(' ')[0] + ':' + b.className.split(' ')[1]).join()`);
        let w = await where();
        expect(w === 'rp-spectrum:rp-jump-dn,rp-spectrum:rp-jump-up', `the arrows are on ${w}`);
        await page.clearWrites();
        await page.eval(`document.querySelector('.rc-holder:not([hidden]) .rp-jump-up').click()`);
        await sleep(300);
        await page.state('FrequencyA', 14_080_000);
        await page.eval(`document.querySelector('.rc-holder:not([hidden]) .rp-jump-dn').click()`);
        await sleep(300);
        const all = await page.writes();
        const f = all.filter(x => x.path === '/api/cat/frequency/a').map(x => x.body?.frequencyHz);
        expect(f.length === 2 && f[0] === 14_090_000 && f[1] === 14_070_000, `the arrows wrote ${JSON.stringify(f)}`);
        const cat = all.filter(x => x.path.startsWith('/api/cat/'));
        expect(cat.length === 2, `the arrows wrote more than the frequency: ${JSON.stringify(cat)}`);

        await page.eval(`document.querySelector('.rc-swap').click()`);
        w = await where();
        expect(w === 'rp-waterfall:rp-jump-dn,rp-waterfall:rp-jump-up', `with the waterfall on top the arrows are on ${w}`);
        await page.eval(`document.querySelector('.rc-swap').click()`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.eval(`localStorage.removeItem('ywc.rttyContest.wfFirst')`).catch(() => {}); await page.close(); }
});

// Bruce VK2RT, 2026-10-26: the trace was too heavy. TRACE starts at 0.8 px
// here, and what is picked is kept.
test('/rtty-contest: TRACE starts thin and keeps what is picked', async () => {
    const page = await open('/rtty-contest', { settleMs: 1500 });
    try {
        await page.eval(`localStorage.removeItem('ywc.rttyContest.trace')`);
        await page.eval(`location.reload()`);
        await sleep(1500);
        const opts = await page.eval(`[...document.querySelectorAll('.rc-scope .rc-trace option')].map(o => o.value).join()`);
        expect(opts === '0.5,0.8,1.2,2', `TRACE offers ${opts}`);
        let v = await page.eval(`document.querySelector('.rc-scope .rc-trace').value`);
        expect(v === '0.8', `TRACE starts at ${v}`);
        await page.eval(`(() => { const s = document.querySelector('.rc-scope .rc-trace'); s.value = '2'; s.dispatchEvent(new Event('change')); })()`);
        await page.eval(`location.reload()`);
        await sleep(1500);
        v = await page.eval(`document.querySelector('.rc-scope .rc-trace').value`);
        expect(v === '2', `after a reload TRACE is ${v}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.eval(`localStorage.removeItem('ywc.rttyContest.trace')`).catch(() => {}); await page.close(); }
});

// Bruce VK2RT, 2026-10-26: the Radio panel's mode keys had no DATA-L.
test('/radio: the mode keys include RTTY-U and DATA-L, and write MD 9 and 8', async () => {
    const page = await open('/radio', { settleMs: 1500 });
    try {
        const keys = await page.eval(`[...document.querySelectorAll('.rp-modes .rp-soft')].map(b => b.textContent).join()`);
        expect(keys === 'LSB,USB,CW-U,CW-L,AM,FM,RTTY-L,RTTY-U,DATA-L,DATA-U', `the mode keys are ${keys}`);
        await page.clearWrites();
        for (const m of ['DATA-L', 'RTTY-U']) {
            await page.eval(`[...document.querySelectorAll('.rp-modes .rp-soft')].find(b => b.textContent === '${m}').click()`);
            await sleep(300);
        }
        const w = (await page.writes()).filter(x => x.path.startsWith('/api/cat/mode/')).map(x => x.body?.mode).join();
        expect(w === '8,9', `the keys wrote ${w}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

// Bruce's set-up (#169): AFSK in DATA-L with the software on Mark 1415 / 170.
// The pair's midpoint is 1500 Hz of audio, so a click on a signal puts the
// dial 1500 Hz above it, and the mark lands on 1415.
test('/rtty-contest: in DATA-L with Mark 1415 / 170 a click puts the dial 1500 Hz above the signal', async () => {
    const page = await open('/rtty-contest', {
        settleMs: 1500,
        // The tuner's status is faked too: a page left open on the real app
        // runs the shared tuner host, and this page would join it on its tones.
        before: `try { localStorage.setItem('rttyTuner', JSON.stringify({ markHz: 1415, shiftHz: 170, reverse: false })); } catch {}
            __rwcTest.getOverrides['/api/rtty/tuner?'] = { running: false, hostRunning: false, points: [] };`,
    });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the contest hub handler' });
        await page.state('FrequencyA', 14_080_000);
        await page.state('ModeA', 'DATA-L');
        const bins = Array.from({ length: 475 }, () => -110);
        await page.state('SpectrumUpdate', { sdrId: 'A', bins, centreHz: 14_080_000, spanHz: 10_000, rfOrdered: true });
        await sleep(700);
        const tones = await page.eval(`[...document.querySelectorAll('.rc-top .rc-chip')].map(c => c.textContent).join(' | ')`);
        expect(tones.includes('MARK 1415 / 170'), `the strip said "${tones}"`);
        await page.eval(`(() => {
            const el = document.querySelector('.rc-holder:not([hidden]) .rp-spectrum');
            const rc = el.getBoundingClientRect(), x = rc.left + rc.width * 0.75, y = rc.top + rc.height / 2;
            el.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, clientX: x, clientY: y }));
            el.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: x, clientY: y }));
        })()`);
        await sleep(300);
        const all = await page.writes();
        const f = all.filter(x => x.path === '/api/cat/frequency/a');
        expect(f.length === 1 && f[0].body?.frequencyHz === 14_082_500 + 1500, `frequency wrote ${JSON.stringify(f)}`);
        expect(!all.some(x => x.path.startsWith('/api/cat/mode')), 'a mode was written');
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.eval(`localStorage.removeItem('rttyTuner')`).catch(() => {}); await page.close(); }
});

test('/rtty-contest: the scope row swaps the waterfall on top, steps the wheel 1 Hz, and keeps REF and AUTO', async () => {
    const clean = `['wfFirst', 'step', 'levels.A'].forEach(k => localStorage.removeItem('ywc.rttyContest.' + k))`;
    const page = await open('/rtty-contest', { settleMs: 1500, before: `try { ${clean}; } catch {}` });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the contest hub handler' });
        await page.state('FrequencyA', 14_080_000);
        await page.state('ModeA', 'USB');
        const bins = Array.from({ length: 475 }, (_, i) => (i === 300 ? -60 : -110));
        for (let i = 0; i < 3; i++) await page.state('SpectrumUpdate', { sdrId: 'A', bins, centreHz: 14_080_000, spanHz: 10_000, rfOrdered: true });
        await sleep(700);

        const order = () => page.eval(`(() => {
            const p = document.querySelector('.rc-holder:not([hidden]) .rp-pane');
            const kids = [...p.children];
            return kids.indexOf(p.querySelector('.rp-waterfall')) < kids.indexOf(p.querySelector('.rp-spectrum')) ? 'wf' : 'spec';
        })()`);
        expect(await order() === 'spec', 'the spectrum is not on top by default');
        await page.eval(`document.querySelector('.rc-swap').click()`);
        expect(await order() === 'wf', 'WF ON TOP did not move the waterfall up');
        expect(await page.eval(`localStorage.getItem('ywc.rttyContest.wfFirst')`) === '1', 'the swap was not kept');

        const ref = () => page.eval(`document.querySelector('.rc-scope .rc-num').textContent`);
        const r0 = await ref();
        await page.eval(`document.querySelector('.rc-ref-up').click()`);
        const r1 = await ref();
        expect(r0 === '-6' && r1 === '-4', `REF went ${r0} -> ${r1}`);
        await page.eval(`document.querySelector('.rc-auto').click()`);
        const kept = JSON.parse(await page.eval(`localStorage.getItem('ywc.rttyContest.levels.A')`));
        expect(kept?.ref === -4 && kept?.auto === false, `levels kept as ${JSON.stringify(kept)}`);

        await page.eval(`document.querySelector('.rc-fit').click()`);
        const range = await page.eval(`document.querySelectorAll('.rc-scope .rc-num')[1].textContent`);
        expect(range !== '60', `FIT left the range at ${range}`);

        await page.eval(`(() => { const s = document.querySelector('.rc-step'); s.value = '1'; s.dispatchEvent(new Event('change')); })()`);
        await page.eval(`document.querySelector('.rc-holder:not([hidden]) .rp-spectrum').dispatchEvent(new WheelEvent('wheel', { deltaY: -100, bubbles: true, cancelable: true }))`);
        await sleep(400);
        const f = (await page.writes()).filter(x => x.path === '/api/cat/frequency/a');
        expect(f.length === 1 && f[0].body?.frequencyHz === 14_080_001, `a notch up wrote ${JSON.stringify(f)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.eval(clean).catch(() => {}); await page.close(); }
});

test('/rtty-contest: a part in its own window leaves the page, and comes back when the window goes', async () => {
    // The windows are opened as tabs here: they find the main page over the
    // same BroadcastChannel a POP OUT window does.
    const main = await open('/rtty-contest', { settleMs: 1500 });
    const wins = [];
    try {
        const shown = async () => (await main.eval(`[...document.querySelectorAll('.rp-pane')].map(p => p.hidden ? 'hidden' : p.classList.contains('no-wf') ? 'spectrum' : p.classList.contains('no-spec') ? 'waterfall' : 'both').join(',')`)).split(',');
        expect((await shown()).every(x => x === 'both'), `at the start the panes are ${await shown()}`);

        wins.push(await open('/rtty-contest/window?p=both', { settleMs: 1500 }));
        await main.waitFor(`!document.querySelector('.rc-out').hidden`, { what: 'the note for both parts out' });
        expect((await shown()).every(x => x === 'hidden'), `with BOTH out the panes are ${await shown()}`);
        expect(await wins[0].eval(`!!document.querySelector('.rc-dock') && !document.querySelector('.rc-pop-both')`), 'the window has no DOCK key, or has the pop-out keys');

        // A single part takes BOTH back (the recalled tab goes to /rtty-contest).
        wins.push(await open('/rtty-contest/window?p=waterfall', { settleMs: 1500 }));
        await main.waitFor(`document.querySelector('.rc-out').hidden`, { what: 'BOTH to be taken back', timeoutMs: 5000 });
        await sleep(1200);
        expect((await shown())[0] === 'spectrum', `with the waterfall out the page shows ${await shown()}`);
        expect(await wins[1].eval(`[...document.querySelectorAll('.rp-pane')].some(p => p.classList.contains('no-spec') && !p.hidden)`), 'the waterfall window does not show the waterfall alone');

        await wins.pop().close();
        await main.waitFor(`[...document.querySelectorAll('.rp-pane')].every(p => !p.classList.contains('no-wf'))`, { what: 'the waterfall to come back', timeoutMs: 5000 });

        // The tuner: hidden here, and stopped, while its window is out. The
        // window is the tuner pop-out every GUI uses, Tune section only.
        wins.push(await open('/RttyTuner?contest=true', { settleMs: 1500 }));
        await main.waitFor(`!document.querySelector('.rc-tnote').hidden`, { what: 'the tuner note' });
        expect(await main.eval(`document.getElementById('rttyTunerDialog').hidden`), 'the docked tuner is still showing');
        expect(await text(main, '.rc-pop-tuner') === 'DOCK', 'the tuner key does not say DOCK');
        expect(await wins.at(-1).eval(`document.getElementById('rttyTunerDialog').classList.contains('rtty-contest')`), 'the tuner window shows more than the Tune section');
        const w = await main.writes();
        expect(w.some(x => x.path.startsWith('/api/rtty/tuner/stop')), `the docked tuner was not stopped: ${JSON.stringify(w.map(x => x.path))}`);
        for (const p of [main, ...wins]) expect(p.exceptions.length === 0, `uncaught exceptions:\n${p.exceptions.join('\n')}`);
    } finally {
        for (const p of wins) await p.close().catch(() => {});
        await main.close();
    }
});

test('/rtty-contest: grabs size the spectrum and the tuner, the tuner scope stays square, LAYOUT saves and puts back', async () => {
    const API = '/api/rttycontestlayouts';
    const before = `
        __rwcTest.getOverrides['${API}/abc'] = { id: 'abc', name: 'Test', layout: { stackHeight: 400, tunerWidth: 500, tunerHeight: 450, wfFirst: true, step: 100 } };
        __rwcTest.getOverrides['${API}'] = { activeId: '__default__', layouts: [{ id: 'abc', name: 'Test' }] };
        try { for (const k of ['stackWidth', 'stackHeight', 'tunerWidth', 'tunerHeight']) localStorage.removeItem('ywc.rttyContest.' + k); } catch {}
        window.prompt = () => 'Mine';`;
    const page = await open('/rtty-contest', { before, settleMs: 1500 });
    try {
        await page.cdp.send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 900, deviceScaleFactor: 1, mobile: false });
        await sleep(400);
        expect(await page.eval(`!document.querySelector('.rc-strip')`), 'STRIP is still there');
        const box = sel => page.eval(`(r => ({ x: Math.round(r.left), w: Math.round(r.width), h: Math.round(r.height) }))(document.querySelector(${JSON.stringify(sel)}).getBoundingClientRect())`);
        const stack = () => box('.rc-stack');
        const tuner = () => box('#rttyTunerDialog');
        // A drag with the button held, as a hand does it.
        async function drag(sel, dx, dy) {
            const g = await page.pointOf(sel);
            const mouse = (type, x, y, buttons) => page.cdp.send('Input.dispatchMouseEvent', { type, x, y, button: 'left', buttons, clickCount: 1 });
            await mouse('mousePressed', g.x, g.y, 1);
            for (let i = 1; i <= 8; i++) await mouse('mouseMoved', g.x + dx * i / 8, g.y + dy * i / 8, 1);
            await mouse('mouseReleased', g.x + dx, g.y + dy, 0);
            await sleep(300);
        }

        const s0 = await stack(), t0 = await tuner();
        await drag('.rc-stack > .rc-grab-r', -300, 0);
        const s1 = await stack(), t1 = await tuner();
        expect(s1.w === s0.w - 300, `the spectrum went from ${s0.w} to ${s1.w} wide, not ${s0.w - 300}`);
        expect(t1.x === t0.x - 300 && t1.w === t0.w, `the tuner did not move along with the spectrum's edge: ${JSON.stringify([t0, t1])}`);
        expect(await page.eval(`localStorage.getItem('ywc.rttyContest.stackWidth')`) === String(s1.w), 'the spectrum width was not kept');

        await drag('.rc-stack > .rc-grab-b', 0, -200);
        const s2 = await stack();
        expect(s2.h === s1.h - 200 && s2.w === s1.w, `the spectrum went from ${s1.h} to ${s2.h} high, not ${s1.h - 200}`);

        // The tuner starts as small as its scope and readouts (Bruce VK2RT,
        // 2026-10-26), so the corner makes it bigger here.
        await drag('.rc-tuner > .rc-grab-rb', 150, 60);
        const t2 = await tuner();
        expect(t2.w === t1.w + 150, `the tuner went from ${t1.w} to ${t2.w} wide by its corner, not ${t1.w + 150}`);
        expect(t2.h === t1.h + 60, `the tuner went from ${t1.h} to ${t2.h} high by its corner, not ${t1.h + 60}`);
        const sc = await box('#rttyTunerCanvas');
        expect(sc.w === sc.h && sc.w > 300, `the tuner scope is ${sc.w} x ${sc.h}, not a square`);

        await page.eval(`document.querySelector('.rc-stack > .rc-grab-r').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))`);
        await sleep(200);
        expect(await page.eval(`document.querySelector('.rc-stack').style.width === ''`), 'a double-click did not put the spectrum back to filling its room');

        const pick = v => page.eval(`(s => { s.value = ${JSON.stringify(v)}; s.dispatchEvent(new Event('change')); })(document.querySelector('.rc-layouts'))`);
        await page.clearWrites();
        await pick('use:abc');
        await sleep(600);
        const s3 = await stack(), t3 = await tuner();
        expect(s3.h === 400 && t3.w === 500, `the layout's sizes did not come back: ${JSON.stringify([s3, t3])}`);
        expect(await page.eval(`document.querySelector('.rc-tuner').style.height === '450px' && document.querySelector('.rc-stack').style.width === ''`),
            "the layout's tuner height, or its unsized spectrum width, did not come back");
        expect(await page.eval(`localStorage.getItem('ywc.rttyContest.wfFirst') === '1' && document.querySelector('.rc-step').value === '100'`), "the layout's swap and step did not come back");
        expect((await page.writes()).some(x => x.method === 'PUT' && x.path === API + '/active' && x.body?.id === 'abc'), 'the layout was not made the active one');

        await page.clearWrites();
        await pick('save-new');
        await sleep(400);
        const post = (await page.writes()).find(x => x.method === 'POST' && x.path === API);
        const l = post?.body?.layout;
        expect(post?.body?.name === 'Mine' && l?.stackWidth === null && l?.stackHeight === 400 && l?.tunerWidth === 500 && l?.tunerHeight === 450,
            `the save did not send this page: ${JSON.stringify(post)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally {
        // The sizes, swap and step this left would carry into the next test.
        await page.eval(`['stackWidth', 'stackHeight', 'tunerWidth', 'tunerHeight', 'wfFirst', 'step'].forEach(k => localStorage.removeItem('ywc.rttyContest.' + k))`).catch(() => {});
        await page.close();
    }
});

test('/rtty-contest: DECODE shows the decoded text in its own box under the tuner and starts the decoder; it pops out by itself; off stops it and hides it', async () => {
    const before = `try { localStorage.removeItem('ywc.rttyContest.decode'); } catch {}`;
    const page = await open('/rtty-contest', { before, settleMs: 1500 });
    const wins = [];
    try {
        const shown = () => page.eval(`(e => !!e && e.offsetParent !== null)(document.getElementById('rttyReaderOut'))`);
        const posts = async what => (await page.writes()).filter(x => x.method === 'POST' && x.path === '/api/rtty/reader/' + what).length;
        expect(!(await shown()), 'the decoded text shows before DECODE');
        expect(await posts('stop') >= 1, 'a decoder left running was not stopped at the start');
        expect(await page.eval(`(e => !!e && e.offsetParent === null)(document.getElementById('rttySendInput'))`), 'Send shows');

        await page.clearWrites();
        await page.click('.rc-decode');
        await sleep(300);
        expect(await shown(), 'DECODE did not show the decoded text');
        expect(await posts('start') === 1, 'DECODE did not start the decoder');
        expect(await page.eval(`document.querySelector('.rc-decode').getAttribute('aria-pressed') === 'true' && localStorage.getItem('ywc.rttyContest.decode') === '1'`),
            'DECODE is not shown as on, or not kept');
        expect(await page.eval(`(e => !!e && e.offsetParent === null)(document.getElementById('rttySendInput'))`), 'Send shows with DECODE on');
        expect(await page.eval(`!!document.querySelector('.rc-side > .rc-dbox > .rtty-decode #rttyReaderOut') && !document.querySelector('#rttyTunerDialog #rttyReaderOut')`),
            'the decoded text is not in a box of its own');
        expect(await page.eval(`(t => { const d = document.querySelector('.rc-dbox').getBoundingClientRect(); return d.top >= t.bottom && d.height > 100; })(document.getElementById('rttyTunerDialog').getBoundingClientRect())`),
            'the decoded text is not under the tuner');
        const sc = await page.eval(`(r => [Math.round(r.width), Math.round(r.height)])(document.getElementById('rttyTunerCanvas').getBoundingClientRect())`);
        expect(sc[0] === sc[1] && sc[0] > 200, `the tuner scope is ${sc} with the text under it`);

        // The text pops out by itself: the tuner stays, the window has the
        // text and no tuner.
        wins.push(await open('/RttyTuner?contest=true&decode=true', { settleMs: 1500 }));
        await page.waitFor(`document.querySelector('.rc-pop-decode').textContent === 'DOCK'`, { what: 'the decode key to say DOCK' });
        expect(await page.eval(`document.querySelector('.rtty-decode').hidden && !document.querySelector('.rc-dbox .rc-tnote').hidden && !document.getElementById('rttyTunerDialog').hidden`),
            'with the text out the page still shows it, has no note, or lost the tuner');
        expect(await page.eval(`document.querySelector('.rc-pop-tuner').textContent`) === 'POP OUT', 'the tuner went out with the text');
        expect(await wins[0].eval(`(e => !!e && e.offsetParent !== null)(document.getElementById('rttyReaderOut')) && document.getElementById('rttyTunerCanvas').offsetParent === null`),
            'the decode window does not show the text alone');
        expect(!(await wins[0].writes()).some(x => x.path.startsWith('/api/rtty/tuner/start')), 'the decode window started a tuner');
        await wins.pop().close();
        await page.waitFor(`document.querySelector('.rc-pop-decode').textContent === 'POP OUT'`, { what: 'the text to come back', timeoutMs: 5000 });
        expect(await shown(), 'the text did not come back when its window went');

        await page.clearWrites();
        await page.click('.rc-decode');
        await sleep(300);
        expect(!(await shown()), 'DECODE off did not hide the decoded text');
        expect(await page.eval(`document.querySelector('.rc-dbox').hidden`), 'DECODE off left the decode box');
        expect(await posts('stop') === 1, 'DECODE off did not stop the decoder');
        for (const p of [page, ...wins]) expect(p.exceptions.length === 0, `uncaught exceptions:\n${p.exceptions.join('\n')}`);
    } finally {
        for (const p of wins) await p.close().catch(() => {});
        await page.close();
    }
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
