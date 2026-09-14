// Drives the real Index page in headless Chrome over CDP and reports:
//   - every uncaught exception / console error during load
//   - whether window.radioScopeControl exists
//   - whether the span buttons have click handlers that actually fire
// Node 24 has a global WebSocket, so this needs no dependencies.

const PORT = 9333;
const URL_ = 'http://localhost:8080/';

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function pageTarget() {
    for (let i = 0; i < 40; i++) {
        try {
            const r = await fetch(`http://127.0.0.1:${PORT}/json/list`);
            const list = await r.json();
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
const events = [];

ws.addEventListener('message', ev => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) {
        pending.get(msg.id)(msg);
        pending.delete(msg.id);
    } else if (msg.method) {
        events.push(msg);
    }
});

const send = (method, params = {}) => new Promise(res => {
    const n = ++id;
    pending.set(n, res);
    ws.send(JSON.stringify({ id: n, method, params }));
});

const evaluate = async expr => {
    const r = await send('Runtime.evaluate', {
        expression: expr, awaitPromise: true, returnByValue: true
    });
    if (r.result?.exceptionDetails) return { error: r.result.exceptionDetails.text };
    return r.result?.result?.value;
};

await send('Runtime.enable');
await send('Log.enable');
await send('Page.enable');
await send('Network.enable');

await send('Page.navigate', { url: URL_ });
await sleep(6000);

console.log('══ uncaught exceptions / console errors during load ══');
const bad = events.filter(e =>
    e.method === 'Runtime.exceptionThrown' ||
    (e.method === 'Runtime.consoleAPICalled' && ['error', 'warning'].includes(e.params.type)) ||
    (e.method === 'Log.entryAdded' && ['error'].includes(e.params.entry.level)));
if (!bad.length) console.log('  (none)');
for (const e of bad) {
    if (e.method === 'Runtime.exceptionThrown') {
        const d = e.params.exceptionDetails;
        console.log('  EXCEPTION:', d.text, d.exception?.description?.split('\n')[0] ?? '');
    } else if (e.method === 'Log.entryAdded') {
        console.log(`  LOG[${e.params.entry.level}]:`, e.params.entry.text, e.params.entry.url ?? '');
    } else {
        console.log(`  CONSOLE[${e.params.type}]:`,
            e.params.args.map(a => a.value ?? a.description ?? a.type).join(' '));
    }
}

console.log('\n══ module / instance state ══');
console.log('  window.radioScopeControl exists :', await evaluate('typeof window.radioScopeControl'));
console.log('  card in DOM                     :', await evaluate('!!document.getElementById("radioScopeCard")'));
console.log('  body display                    :', await evaluate('document.getElementById("radioScopeBody")?.style.display'));
console.log('  span buttons found              :', await evaluate('document.querySelectorAll(".scope-span-btn").length'));
console.log('  control .band                   :', await evaluate('window.radioScopeControl?.band'));
console.log('  control .busy                   :', await evaluate('window.radioScopeControl?.busy'));
console.log('  control .loaded                 :', await evaluate('window.radioScopeControl?.loaded'));

console.log('\n══ simulating a real click on the header, then a span button ══');
console.log('  header click ->', await evaluate('document.getElementById("radioScopeToggle").click(), "clicked"'));
await sleep(2500);
console.log('  body display now :', await evaluate('document.getElementById("radioScopeBody")?.style.display'));
console.log('  status badge     :', await evaluate('document.getElementById("radioScopeStatus")?.textContent'));
console.log('  loaded           :', await evaluate('window.radioScopeControl?.loaded'));

console.log('  span "20k" click ->',
    await evaluate('[...document.querySelectorAll(".scope-span-btn")].find(b=>b.textContent.trim()==="20k").click(), "clicked"'));
await sleep(2500);
console.log('  status badge after span click :', await evaluate('document.getElementById("radioScopeStatus")?.textContent'));

console.log('\n══ network requests to /api/scope ══');
const reqs = events.filter(e => e.method === 'Network.requestWillBeSent' && e.params.request.url.includes('/api/scope'));
if (!reqs.length) console.log('  (none — the browser never issued one)');
for (const r of reqs) console.log(' ', r.params.request.method, r.params.request.url);

ws.close();
// Let the socket finish closing before exiting: process.exit() racing an
// in-flight WebSocket teardown trips a libuv assertion on Windows.
await sleep(200);
process.exit(0);
