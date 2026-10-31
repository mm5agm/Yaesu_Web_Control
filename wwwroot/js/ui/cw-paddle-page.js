// CW Paddle test page (Pages/CwPaddle.cshtml): MIDI paddle -> iambic keyer
// -> sidetone, with what was sent read back as text and every MIDI message
// and element logged. With Send to the shack PC ticked, each element also
// goes to YWC over the hub (RadioHub.CwKey), which plays it after its jitter
// buffer and reports back. Dry run: YWC only logs; nothing keys the radio.

import { IambicKeyer, ElementDecoder } from '../cw/iambic-keyer.js';
import { Sidetone } from '../cw/sidetone.js';
import { MidiPaddle } from '../cw/paddle-midi.js';
import { KeyLink } from '../cw/key-link.js';

const STORE = 'ywc.cwPaddle';
const DEFAULTS = { device: '', ditNote: 20, dahNote: 21, channel: 0, swap: false,
                   wpm: 20, mode: 'B', stuckS: 3, tone: true, pitch: 600, volume: 30, link: false };
let settings = { ...DEFAULTS };
try { settings = { ...DEFAULTS, ...JSON.parse(localStorage.getItem(STORE) || '{}') }; } catch { /* keep defaults */ }
const save = () => localStorage.setItem(STORE, JSON.stringify(settings));

const $ = id => document.getElementById(id);
const t0 = performance.now();
const stamp = ms => (ms - t0).toFixed(1).padStart(10);

const LOG_LINES = 500;
function log(text, at = performance.now()) {
    const el = $('cwpLog');
    const atBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 4;
    el.append(`${stamp(at)}  ${text}\n`);
    while (el.childNodes.length > LOG_LINES) el.removeChild(el.firstChild);
    if (atBottom) el.scrollTop = el.scrollHeight;
}

// --- The parts -----------------------------------------------------------

// The link to the shack PC. Packets are fire-and-forget (connection.send):
// waiting for each to be acknowledged would hold the next one up.
const hub = window.cwpHub;   // built in CwPaddle.cshtml
const link = new KeyLink({
    send: packet => {
        if (hub.state !== signalR.HubConnectionState.Connected) return;
        hub.send('CwKey', packet).catch(e => log(`shack  send failed: ${e.message}`));
    },
});
// A summary of each burst of keying, a second after the shack goes quiet,
// so a long log need not be read line by line.
let burst = null;
let burstTimer = 0;
function burstSummary() {
    const b = burst;
    burst = null;
    const safety = b.safety ? `${b.safety} safety let-go${b.safety === 1 ? '' : 's'}` : 'no safety let-go';
    log(`shack  SUMMARY: ${b.elements} elements, lengths within ${b.maxErr.toFixed(1)} ms of sent, ` +
        `latest ${b.maxLate.toFixed(1)} ms, ${safety}`);
}
hub.on('CwKeyReport', r => {
    const late = r.kind === 'key' && r.lateMs > 1 ? `  (${r.lateMs.toFixed(1)} ms late)` : '';
    log(`shack  ${r.kind === 'safety' ? '!! ' : ''}${r.text}${late}`);
    if (r.kind === 'busy') { $('cwpLinkStatus').textContent = 'Another browser has the key: close its tab.'; return; }
    burst ??= { elements: 0, maxErr: 0, maxLate: 0, safety: 0 };
    if (r.kind === 'safety') burst.safety++;
    if (r.kind === 'key') burst.maxLate = Math.max(burst.maxLate, r.lateMs ?? 0);
    if (r.playedMs != null && r.sentMs != null) {
        burst.elements++;
        burst.maxErr = Math.max(burst.maxErr, Math.abs(r.playedMs - r.sentMs));
    }
    clearTimeout(burstTimer);
    burstTimer = setTimeout(burstSummary, 1000);
});
function linkStatus() {
    const s = $('cwpLinkStatus');
    if (!settings.link) s.textContent = 'Not sending.';
    else if (hub.state === signalR.HubConnectionState.Connected) s.textContent = 'Sending to YWC (dry run: it logs, keys nothing).';
    else s.textContent = `Hub ${hub.state.toLowerCase()}: waiting to send.`;
}
hub.onreconnecting(linkStatus);
hub.onreconnected(linkStatus);
hub.onclose(linkStatus);
hub.start().then(linkStatus, e => { log(`Hub did not connect: ${e.message}`); linkStatus(); });
function setLink(on) {
    settings.link = on;
    save();
    if (on) link.start(); else link.stop();
    linkStatus();
}

const sidetone = new Sidetone({ pitchHz: settings.pitch, volume: settings.volume / 100 });
const decoder = new ElementDecoder(s => { $('cwpCopy').textContent += s; });

function lightKey(start, end) {
    const now = performance.now();
    setTimeout(() => $('cwpKeyLamp').classList.add('on'), Math.max(0, start - now));
    setTimeout(() => $('cwpKeyLamp').classList.remove('on'), Math.max(0, end - now));
}

const keyer = new IambicKeyer({
    wpm: settings.wpm, mode: settings.mode, stuckMs: settings.stuckS * 1000,
    onElement: ({ paddle, start, end }) => {
        if (settings.tone) sidetone.element(start, end);
        link.element(start, end);
        lightKey(start, end);
        decoder.element(paddle, start, end, keyer.unitMs);
        log(`keyer  ${paddle === 'dit' ? 'dit' : 'DAH'}  ${(end - start).toFixed(0)} ms` +
            `${start > performance.now() + 0.5 ? `  (starts in ${(start - performance.now()).toFixed(1)} ms)` : ''}`, start);
    },
    onStop: () => { sidetone.cancel(); link.abort(); $('cwpKeyLamp').classList.remove('on'); },
    onStuck: p => log(`${p} paddle held over ${settings.stuckS} s: treated as let go until it is released`),
});

setInterval(() => { if (!keyer.busy) decoder.idle(performance.now()); }, 50);

const paddle = new MidiPaddle({
    ditNote: settings.ditNote, dahNote: settings.dahNote, channel: settings.channel, swap: settings.swap,
    onPaddle: (p, down) => {
        $(p === 'dit' ? 'cwpDitLamp' : 'cwpDahLamp').classList.toggle('on', down);
        keyer.paddle(p, down);
    },
    onMessage: ({ input, data, at, paddle: p, down }) => {
        const hex = data.map(b => b.toString(16).padStart(2, '0').toUpperCase()).join(' ');
        log(`midi   ${hex.padEnd(9)}  ${p ? `${p} ${down ? 'pressed' : 'let go'}` : '(not a paddle note)'}  [${input}]`, at);
    },
    onDevices: list => {
        const sel = $('cwpDevice');
        const keep = settings.device;
        sel.replaceChildren(new Option('All MIDI inputs', ''));
        for (const d of list) sel.add(new Option(`${d.name}${d.state === 'connected' ? '' : ' (not connected)'}`, d.id));
        sel.value = list.some(d => d.id === keep) ? keep : '';
        const names = list.filter(d => d.state === 'connected').map(d => d.name);
        $('cwpStatus').textContent = names.length ? `MIDI inputs: ${names.join(', ')}` : 'No MIDI inputs found. Plug the paddle in.';
    },
});

// --- Controls --------------------------------------------------------------

const ch = $('cwpChannel');
ch.add(new Option('Any', '0'));
for (let i = 1; i <= 16; i++) ch.add(new Option(String(i), String(i)));

$('cwpDitNote').value = settings.ditNote;
$('cwpDahNote').value = settings.dahNote;
ch.value = String(settings.channel);
$('cwpSwap').checked = settings.swap;
$('cwpWpm').value = settings.wpm;
$('cwpMode').value = settings.mode;
$('cwpStuck').value = settings.stuckS;
$('cwpTone').checked = settings.tone;
$('cwpPitch').value = settings.pitch;
$('cwpVolume').value = settings.volume;
$('cwpLink').checked = settings.link;
if (settings.link) setLink(true);

function numberSetting(id, key, min, max, apply) {
    $(id).addEventListener('change', () => {
        const v = Math.round(Number($(id).value));
        if (!Number.isFinite(v) || v < min || v > max) { $(id).value = settings[key]; return; }
        settings[key] = v;
        save();
        apply(v);
    });
}
numberSetting('cwpDitNote', 'ditNote', 0, 127, v => { paddle.releaseAll(); paddle.ditNote = v; });
numberSetting('cwpDahNote', 'dahNote', 0, 127, v => { paddle.releaseAll(); paddle.dahNote = v; });
numberSetting('cwpWpm', 'wpm', 5, 50, v => { keyer.wpm = v; });
numberSetting('cwpStuck', 'stuckS', 1, 30, v => { keyer.stuckMs = v * 1000; });
numberSetting('cwpPitch', 'pitch', 300, 1200, v => { sidetone.pitch = v; });

ch.addEventListener('change', () => { settings.channel = Number(ch.value); save(); paddle.releaseAll(); paddle.channel = settings.channel; });
$('cwpSwap').addEventListener('change', () => { settings.swap = $('cwpSwap').checked; save(); paddle.releaseAll(); paddle.swap = settings.swap; });
$('cwpMode').addEventListener('change', () => { settings.mode = $('cwpMode').value; save(); keyer.mode = settings.mode; });
$('cwpTone').addEventListener('change', () => { settings.tone = $('cwpTone').checked; save(); if (!settings.tone) sidetone.cancel(); });
$('cwpVolume').addEventListener('input', () => { settings.volume = Number($('cwpVolume').value); save(); sidetone.volume = settings.volume / 100; });
$('cwpDevice').addEventListener('change', () => { settings.device = $('cwpDevice').value; save(); paddle.select(settings.device); });
$('cwpLink').addEventListener('change', () => setLink($('cwpLink').checked));
$('cwpClearCopy').addEventListener('click', () => { $('cwpCopy').textContent = ''; });
$('cwpClearLog').addEventListener('click', () => { $('cwpLog').textContent = ''; });

$('cwpConnect').addEventListener('click', async () => {
    // Audio may only start from a click, so the sidetone starts here too.
    try { await sidetone.start(); } catch (e) { log(`Sidetone could not start: ${e.message}`); }
    if (!MidiPaddle.supported) {
        $('cwpStatus').textContent = 'This browser has no Web MIDI. Use Chrome or Edge, on https or localhost.';
        return;
    }
    try {
        await paddle.open();
        paddle.select(settings.device);
        $('cwpConnect').disabled = true;
        $('cwpConnect').textContent = 'Connected';
        log('MIDI open');
    } catch (e) {
        $('cwpStatus').textContent = `MIDI was refused or failed: ${e.message}`;
        log(`MIDI failed: ${e.message}`);
    }
});

// A hidden tab's timers are slowed by the browser, and the keyer's with them.
document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
        keyer.stop();
        log('Page hidden: keyer stopped (the browser slows timers in hidden tabs)');
    }
});
window.addEventListener('pagehide', () => { keyer.stop(); paddle.releaseAll(); link.stop(); });

log('Page open. Dry run: nothing is sent to the radio; the shack PC only logs.');
