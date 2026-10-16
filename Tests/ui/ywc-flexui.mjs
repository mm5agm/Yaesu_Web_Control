// Yaesu Web Control - browser checks for the Experimental (Flex) UI at /flexui
//
//   node Tests/ui/ywc-flexui.mjs [--base http://localhost:8080] [--headed]
//
// The app must already be running. Nothing here reaches the radio.
//
// These checks exist because the Experimental UI is a fork of the Classic
// page: it loads its own copies of the modules the Classic page also uses
// (wwwroot/js/flexui/app/**, wwwroot/css/flexui/site.css). A build cannot see
// a stale or missing fork import - only a browser the page actually ran in
// can - and that is exactly the class of bug recorded in catchup.md (the
// stale /js/ui/hub-connection.js path that shipped unnoticed). Keep this in
// step with the import list in Pages/FlexPartials/_FlexScripts.cshtml.

import { Browser, Suite, expect, sleep } from '../../core/tests/ui/browser-harness.mjs';

const args = process.argv.slice(2);
const arg = (name, fallback) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : fallback; };
const BASE = arg('--base', 'http://localhost:8080').replace(/\/$/, '');

const suite = new Suite(`Yaesu Web Control Experimental UI checks against ${BASE}`);
const test = (name, fn) => suite.test(name, fn);

let browser;
const open = (path, opts) => browser.open(BASE + path, opts);
// Radio reads 5xx when no radio is attached; those are not page faults.
const pageFaults = page => page.failed.filter(f => !/^5\d\d \/api\//.test(f));
const status = async p => (await fetch(BASE + p)).status;

// ---------------------------------------------------------------- preflight

test('fork serves the flex modules and assets', async () => {
    for (const p of [
        '/js/flexui/site.js',
        '/js/flexui/flex-shims.js',
        '/js/flexui/flex-workspace.js',
        '/js/flexui/flex-layouts-admin.js',
        '/js/flexui/app/guages/meter-panel.js',
        '/js/flexui/app/guages/gaugeFactory.js',
        '/js/flexui/app/guages/gauge.js',
        '/js/flexui/app/guages/linear-gauge.js',
        '/js/flexui/app/ui/band-plan.js',
        '/js/flexui/app/ui/filter-scope-panel.js',
        '/js/flexui/app/ui/if-width-tables.js',
        '/js/flexui/app/ui/freq-keyboard.js',
        '/js/flexui/app/ui/keyboard-shortcuts.js',
        '/js/flexui/app/ui/memories.js',
        '/js/flexui/app/ui/vfo-key-buttons.js',
        '/js/flexui/app/ui/toggle-dropdown-button.js',
        '/js/flexui/app/audio/remote-audio-ui.js',
        '/js/flexui/app/video/radio-display-ui.js',
        '/js/flexui/app/sdr/spectrum-panel.js',
        '/css/flexui/site.css',
        '/css/theme-yaesu.css',
    ]) {
        expect(await status(p) === 200, `${p} is not served - is the app built from this branch?`);
    }
});

// ---------------------------------------------------------------- the page

test('/flexui loads cleanly and mounts the dock', async () => {
    const page = await open('/flexui', { settleMs: 3000 });
    try {
        const faults = pageFaults(page);
        expect(faults.length === 0, `failed requests:\n${faults.join('\n')}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
        expect(await page.eval('typeof window.rwcHubConnection') === 'function',
            'the hub connection factory did not load on /flexui');
        expect(await page.eval('!!window.ywcFlexFlags'), 'window.ywcFlexFlags was not set');
        const failedToMount = await page.eval(
            `document.getElementById('ywcFlexHost')?.textContent?.startsWith('Dockable layout library failed') ? true : false`);
        expect(!failedToMount, 'the FlexLayout bundle did not expose React / ReactDOM / FlexLayout');
        await page.waitFor(
            `!!document.querySelector('#ywcFlexHost .flexlayout__layout')`,
            { timeoutMs: 8000, what: 'the FlexLayout dock to mount' });
    } finally { await page.close(); }
});

test('/flexui loads the Classic page without the fork', async () => {
    // The other half of the fork: Classic must not have picked up any flex
    // asset. If this ever fails, the isolation has been broken.
    const page = await open('/', { settleMs: 2500 });
    try {
        const html = await page.eval('document.documentElement.outerHTML');
        expect(!html.includes('/js/flexui/'), 'the Classic page referenced a flex script');
        expect(!html.includes('/css/flexui/'), 'the Classic page referenced the flex stylesheet');
        expect(!html.includes('theme-yaesu'), 'the Classic page loaded the Yaesu palette');
    } finally { await page.close(); }
});

// ----------------------------------------------------------------

try {
    const up = await fetch(BASE + '/flexui').then(r => r.ok, () => false);
    if (!up) { console.error(`Nothing answering at ${BASE}/flexui. Start the app first.`); process.exit(2); }
    browser = await Browser.launch({ headless: !args.includes('--headed') });
    process.exitCode = await suite.run();
} finally {
    browser?.close();
}
