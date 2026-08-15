// Verifies the CAT scope panel's band selector in BOTH directions:
//
//   inbound   VFO header clicked -> radio changes band -> ActiveVfo broadcast ->
//             scope controls re-aim themselves
//   outbound  panel's own MAIN/SUB button clicked -> VS sent -> radio actually
//             changes band (checked against the radio, not just the UI)
//
// Drives the real page in headless Chrome over CDP, clicking exactly what an
// operator would click. Restores the radio to MAIN before exiting.
//
// Usage:  node scripts/probe/cdp-scope-follow.mjs
// Needs a headless Chrome already listening on --remote-debugging-port=9333.
// Node 24's global WebSocket means no npm dependencies.

const PORT = 9333;
const URL_ = 'http://localhost:8080/';

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function pageTarget() {
    for (let i = 0; i < 40; i++) {
        try {
            const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
            const p = list.find(t => t.type === 'page' && t.webSocketDebuggerUrl);
            if (p) return p;
        } catch { /* chrome not up yet */ }
        await sleep(250);
    }
    throw new Error('no page target');
}

const target = await pageTarget();
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise(r => ws.addEventListener('open', r, { once: true }));

let id = 0;
const pending = new Map();
ws.addEventListener('message', ev => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
});
const send = (method, params = {}) => new Promise(res => {
    const n = ++id; pending.set(n, res);
    ws.send(JSON.stringify({ id: n, method, params }));
});
const evaluate = async expr => {
    const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true });
    if (r.result?.exceptionDetails) return `ERROR: ${r.result.exceptionDetails.text}`;
    return r.result?.result?.value;
};

await send('Runtime.enable');
await send('Page.enable');
await send('Page.navigate', { url: URL_ });
await sleep(6000);

const band       = () => evaluate('window.radioScopeControl?.band');
const activeBtn  = () => evaluate(
    '[...document.querySelectorAll(".scope-band-btn")].filter(b=>b.classList.contains("active")).map(b=>b.dataset.band).join(",")');
const badge      = () => evaluate('document.getElementById("radioScopeStatus")?.textContent');
const clickVfo   = col => evaluate(`document.querySelector("#${col} .card-header").click(), "clicked"`);

const report = async label => console.log(
    `  ${label.padEnd(28)} band=${await band()}  activeBtn=${await activeBtn()}  badge="${await badge()}"`);

console.log('══ initial (radio on MAIN) ══');
await report('on load');

console.log('\n══ ensure the panel is expanded ══');
// NOT an unconditional click. The panel restores its expand state from
// localStorage, so a blind toggle closes it about half the time and every
// subsequent reading is the collapsed behaviour instead of the one under test.
// That cost a debugging cycle once already.
console.log('  ', await evaluate(`(() => {
    const body = document.getElementById('radioScopeBody');
    if (body.style.display === 'none') {
        document.getElementById('radioScopeToggle').click();
        return 'was collapsed - expanded it';
    }
    return 'already expanded';
})()`));
await sleep(2500);
await report('after expand');

console.log('\n══ click VFO B header -> radio to SUB ══');
console.log('  ', await clickVfo('vfoBCol'));
await sleep(3000);
await report('after VFO B');

console.log('\n══ click VFO A header -> radio back to MAIN ══');
console.log('  ', await clickVfo('vfoACol'));
await sleep(3000);
await report('after VFO A');

// ── outbound: the panel's own buttons must move the radio ────────────────────
// This is the half that was missing at first. The buttons re-aimed the controls
// and left the radio where it was, so "SUB scope" adjusted a scope the operator
// could not see. Read VS off the radio after each click — the button highlight
// agreeing with itself proves nothing.

const readVs = async () => (await (await fetch(
    'http://localhost:8080/api/vctune/diag?cmd=VS%3B')).json()).response;
const clickBand = b => evaluate(
    `document.querySelector('.scope-band-btn[data-band="${b}"]').click(), "clicked"`);

console.log('\n══ click the panel\'s SUB button -> radio must go to SUB ══');
console.log('  ', await clickBand('sub'));
await sleep(3000);
await report('after SUB button');
const vsSub = await readVs();
console.log('  VS on radio:', vsSub, vsSub === 'VS1' ? '✓ SUB' : '✗ radio did NOT move');

console.log('\n══ click the panel\'s MAIN button -> radio must go back to MAIN ══');
console.log('  ', await clickBand('main'));
await sleep(3000);
await report('after MAIN button');
const vsMain = await readVs();
console.log('  VS on radio:', vsMain, vsMain === 'VS0' ? '✓ MAIN' : '✗ radio did NOT move');

console.log('\n══ radio state ══');
const vs = { response: vsMain };
console.log('  VS now:', vs.response, vs.response === 'VS0' ? '(MAIN — restored)' : '(NOT restored!)');

ws.close();
// Let the socket finish closing before exiting: process.exit() racing an
// in-flight WebSocket teardown trips a libuv assertion on Windows.
await sleep(200);
process.exit(0);
