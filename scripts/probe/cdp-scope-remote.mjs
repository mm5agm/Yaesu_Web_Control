// Verifies that scope changes made AT THE RADIO reach the panel.
//
// Two halves, checked separately because they fail for different reasons:
//
//   browser  applyRemote() patches the one sub-command it was told about and
//            repaints, without re-reading anything
//   server   an SS message off the wire becomes a ScopeSetting envelope and
//            arrives at applyRemote
//
// The server half needs the radio to actually send an SS, so the probe wraps
// applyRemote to record every call and then reports what turned up. Clicking a
// control in the panel is a CAT-originated change; whether the radio also
// announces those is the question this answers.
//
// Usage:  node scripts/probe/cdp-scope-remote.mjs
// Needs headless Chrome on --remote-debugging-port=9333. No npm dependencies.

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

// Conditional, never a blind toggle: the panel restores its open/closed state
// from localStorage, so a toggle closes it half the time and everything after
// silently measures the collapsed path instead.
await evaluate(`(() => {
    const b = document.getElementById('radioScopeBody');
    if (b.style.display === 'none') document.getElementById('radioScopeToggle').click();
    return 'ok';
})()`);
await sleep(2500);

// Record every ScopeSetting that reaches the panel from the server.
await evaluate(`(() => {
    const c = window.radioScopeControl;
    window.__remoteCalls = [];
    const original = c.applyRemote.bind(c);
    c.applyRemote = m => { window.__remoteCalls.push(m); return original(m); };
    return 'wrapped';
})()`);

const shown = () => evaluate(`(() => {
    const on = sel => [...document.querySelectorAll(sel)]
        .filter(b => b.classList.contains('active')).map(b => b.dataset.value).join(',');
    return {
        span:  on('.scope-span-btn'),
        type:  on('.scope-type-btn'),
        place: on('.scope-place-btn'),
        size:  on('.scope-size-btn'),
        badge: document.getElementById('radioScopeStatus').textContent
    };
})()`);

console.log('══ browser half: applyRemote patches and repaints ══');
console.log('  before        :', JSON.stringify(await shown()));

// Pretend the radio announced a span change, then a mode change. Values are
// chosen to be visibly different from whatever the radio is on.
const band = await evaluate('window.radioScopeControl.band');
await evaluate(`window.radioScopeControl.applyRemote({ band: '${band}', setting: '5', field: '30000' }), 'x'`);
await sleep(300);
console.log('  after span=3  :', JSON.stringify(await shown()));

await evaluate(`window.radioScopeControl.applyRemote({ band: '${band}', setting: '6', field: '70000' }), 'x'`);
await sleep(300);
console.log('  after mode=7  :', JSON.stringify(await shown()), ' (7 = W/F Cursor, size N)');

// The other band must be ignored outright — patching this panel from the scope
// it is not showing is the same class of bug as the original band mismatch.
const other = band === 'sub' ? 'main' : 'sub';
const beforeOther = await shown();
await evaluate(`window.radioScopeControl.applyRemote({ band: '${other}', setting: '5', field: '00000' }), 'x'`);
await sleep(300);
const afterOther = await shown();
console.log(`  ${other} ignored  :`,
    JSON.stringify(beforeOther) === JSON.stringify(afterOther) ? 'yes' : 'NO - it repainted!');

console.log('\n══ server half: does an SS off the wire arrive? ══');
console.log('  Clicking span 20k in the panel (a CAT-originated change).');
await evaluate(`document.querySelector('.scope-span-btn[data-value="4"]').click(), 'x'`);
await sleep(3000);
const calls = await evaluate('JSON.stringify(window.__remoteCalls)');
console.log('  ScopeSetting envelopes received:', calls);
console.log('  final                          :', JSON.stringify(await shown()));
console.log('\n  An empty list here does NOT mean the plumbing is broken - it may');
console.log('  only mean the radio does not announce changes it was asked to make');
console.log('  over CAT. Front-panel changes are the case that matters; turn the');
console.log('  knob on the rig while this page is open to confirm those.');

ws.close();
await sleep(200);
process.exit(0);
