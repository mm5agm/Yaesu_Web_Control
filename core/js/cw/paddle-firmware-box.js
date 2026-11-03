// The Install / Update box for the paddle adapter's firmware, for a CW Paddle
// page: mountFirmwareBox(element, options) fills the element. The work is in
// paddle-firmware.js; this is buttons, words and a progress bar.
//
// options:
//   firmwareUrl   where the app serves core/firmware/paddle-adapter/dist,
//                 ending in '/'
//   appName       for the words ("Yaesu Web Control")
//   beforeUpdate  called before the adapter is restarted, so the page can
//                 stop keying (the adapter goes away for a minute)

import { AdapterLink, choosePort, flashImage, versionAdvice, FlashError } from './paddle-firmware.js';

const STYLE = `
.pfw-status { margin-top: 0.4rem; }
.pfw-status.ok { color: #8d8; }
.pfw-status.warn { color: #fd6; }
.pfw-status.err { color: #f99; }
.pfw-progress { width: 100%; max-width: 28em; height: 0.8rem; margin-top: 0.4rem; }
.pfw-log { font-family: Consolas, monospace; font-size: 0.75rem; max-height: 12em; overflow-y: auto; background: #111; padding: 0.3rem 0.5rem; border-radius: 4px; white-space: pre-wrap; }
`;

export function mountFirmwareBox(el, { firmwareUrl, appName = 'this app', beforeUpdate = () => {} }) {
    if (!document.getElementById('pfw-style')) {
        const s = document.createElement('style');
        s.id = 'pfw-style';
        s.textContent = STYLE;
        document.head.appendChild(s);
    }
    el.innerHTML = `
        <div class="cwp-row">
            <button type="button" class="btn btn-sm btn-outline-light" data-pfw="check">Check adapter</button>
            <button type="button" class="btn btn-sm btn-warning" data-pfw="install">Install / Update</button>
            <span data-pfw="carried"></span>
        </div>
        <div class="pfw-status" data-pfw="status">Press Check adapter to see which firmware the adapter has.</div>
        <progress class="pfw-progress" data-pfw="progress" max="1" value="0" hidden></progress>
        <div class="cwp-note">
            Turns an ESP32-S3 board into a paddle adapter (wiring: dit to GPIO4, dah to GPIO5, common to GND).
            The board needs its native USB port on a socket: on boards with two, the one marked USB, not COM or UART.
            Original ESP32 boards (ESP-WROOM-32, such as ELEGOO's) and C3 boards cannot be adapters.
            Plug the board in by that port and press Install / Update: the browser lists only ESP32 boards,
            never the radio's COM ports, and the board restarts with the new firmware when it is done (about a minute).
        </div>
        <details class="mt-1"><summary class="cwp-note">Programming log</summary><div class="pfw-log" data-pfw="log"></div></details>`;
    const $ = name => el.querySelector(`[data-pfw="${name}"]`);
    const status = $('status'), progress = $('progress'), log = $('log');
    const checkBtn = $('check'), installBtn = $('install');
    let carried = '';
    let busy = false;
    const link = new AdapterLink();

    const say = (text, kind = '') => { status.textContent = text; status.className = `pfw-status ${kind}`; };
    const logLine = text => { log.textContent += text.endsWith('\n') ? text : text + '\n'; log.scrollTop = log.scrollHeight; };

    if (!('serial' in navigator) || !AdapterLink.supported) {
        say('Programming the adapter needs Chrome or Edge, on https.', 'warn');
        checkBtn.disabled = installBtn.disabled = true;
        return;
    }

    fetch(firmwareUrl + 'firmware.json', { cache: 'no-cache' })
        .then(r => r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`)))
        .then(j => { carried = j.version; $('carried').textContent = `${appName} carries firmware ${carried}.`; })
        .catch(e => { say(`The firmware file is missing from ${appName} (${e.message}).`, 'err'); installBtn.disabled = true; });

    async function check() {
        await link.open();
        const found = await link.askVersion();
        const a = versionAdvice(found, carried || '?');
        say(a.text, a.state === 'current' || a.state === 'newer' ? 'ok' : a.state === 'none' ? '' : 'warn');
        return found;
    }

    async function waitForAdapter(ms) {
        const end = performance.now() + ms;
        while (performance.now() < end) {
            await new Promise(r => setTimeout(r, 1000));
            const found = await link.askVersion().catch(() => null);
            if (found?.version) return found;
        }
        return null;
    }

    checkBtn.addEventListener('click', async () => {
        if (busy) return;
        try { await check(); } catch (e) { say(`MIDI: ${e.message}. The browser has to be allowed to use MIDI devices, with SysEx.`, 'err'); }
    });

    installBtn.addEventListener('click', async () => {
        if (busy) return;
        busy = true;
        checkBtn.disabled = installBtn.disabled = true;
        log.textContent = '';
        try {
            // An adapter on MIDI is asked to restart in download mode; a new
            // board, or firmware before 1.0, goes straight to the port list.
            // The list must open soon after the click (the browser allows
            // it only then), so nothing here waits long.
            try { await link.open(); } catch { /* no MIDI: the port list still works */ }
            if (link.present) {
                beforeUpdate();
                say('Restarting the adapter in download mode...');
                const ok = await link.restartForUpdate();
                logLine(ok ? 'Adapter restarting in download mode' : 'Adapter did not answer (firmware before 1.0?)');
                if (ok) await new Promise(r => setTimeout(r, 600));
            }
            say('Choose the board in the list (it may take a second to appear), then press Connect.');
            const port = await choosePort();
            const r = await fetch(firmwareUrl + 'ywc-paddle-adapter.bin', { cache: 'no-cache' });
            if (!r.ok) throw new FlashError('', `The firmware file is missing from ${appName} (HTTP ${r.status}).`);
            const image = new Uint8Array(await r.arrayBuffer());
            say('Programming: do not unplug the board.');
            progress.hidden = false;
            progress.value = 0;
            await flashImage({ port, image, onLog: logLine, onProgress: f => { progress.value = f; } });
            say('Written and checked. Waiting for the adapter to come back...', 'ok');
            const back = await waitForAdapter(10000);
            if (back) say(`Done: ${back.name} is running firmware ${back.version}.`, 'ok');
            else say('Written and checked. The adapter has not come back over MIDI yet: unplug it and plug it back in by its USB port.', 'ok');
        } catch (e) {
            say(e.message, 'err');
            if (e.detail) logLine(`Error: ${e.detail}`);
        } finally {
            progress.hidden = true;
            busy = false;
            checkBtn.disabled = installBtn.disabled = false;
        }
    });
}
