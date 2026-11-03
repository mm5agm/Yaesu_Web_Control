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
.pfw-step .modal-body p:last-child { margin-bottom: 0; }
`;

// The browser's own pop-ups (MIDI permission, the port list) cannot be
// styled, so each is introduced by a panel in the app's style first: Bootstrap
// 5.3's modal classes, shown without Bootstrap's script. Resolves true for
// Continue, false for Cancel, Esc or a click outside.
function stepPanel({ title, icon, body, ok = 'Continue' }) {
    return new Promise(resolve => {
        const backdrop = document.createElement('div');
        backdrop.className = 'modal-backdrop fade show';
        const m = document.createElement('div');
        m.className = 'modal fade show d-block pfw-step';
        m.tabIndex = -1;
        m.setAttribute('role', 'dialog');
        m.setAttribute('aria-modal', 'true');
        m.setAttribute('data-bs-theme', 'dark');
        m.innerHTML = `
            <div class="modal-dialog modal-dialog-centered">
                <div class="modal-content">
                    <div class="modal-header">
                        <h5 class="modal-title"><i class="bi ${icon}"></i> ${title}</h5>
                        <button type="button" class="btn-close" data-pfw-step="cancel" aria-label="Cancel"></button>
                    </div>
                    <div class="modal-body">${body}</div>
                    <div class="modal-footer">
                        <button type="button" class="btn btn-secondary" data-pfw-step="cancel">Cancel</button>
                        <button type="button" class="btn btn-warning" data-pfw-step="ok">${ok}</button>
                    </div>
                </div>
            </div>`;
        const done = result => {
            document.removeEventListener('keydown', key);
            m.remove();
            backdrop.remove();
            resolve(result);
        };
        const key = e => { if (e.key === 'Escape') done(false); };
        m.addEventListener('click', e => {
            const b = e.target.closest('[data-pfw-step]');
            if (b) done(b.dataset.pfwStep === 'ok');
            else if (e.target === m) done(false);
        });
        document.addEventListener('keydown', key);
        document.body.append(backdrop, m);
        m.querySelector('[data-pfw-step="ok"]').focus();
    });
}

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

    // MIDI with SysEx, introduced by a panel the first time, when the browser
    // is about to ask. False when the user cancels.
    async function openMidi() {
        let state = 'granted';
        try { state = (await navigator.permissions.query({ name: 'midi', sysex: true })).state; } catch { /* no query: just ask */ }
        if (state === 'denied')
            throw new Error('the browser has been told not to let this page use MIDI devices. Click the icon left of the address, set MIDI devices to Allow, and reload the page.');
        if (state === 'prompt' && !await stepPanel({
            title: 'Allow MIDI devices', icon: 'bi-usb-symbol',
            body: `<p>The browser will now ask whether ${appName} may control and reprogram your MIDI devices.</p>
                   <p>Press <b>Allow</b>: it is how ${appName} asks the adapter which firmware it has, and restarts it for an update. The browser asks only once.</p>`,
        })) return false;
        await link.open();
        return true;
    }

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
        if (!await openMidi()) return say('Press Check adapter to see which firmware the adapter has.');
        await link.waitPresent(2500);
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
            // board, or one put in download mode with BOOT, goes straight to
            // the port list.
            let midi = false;
            try { midi = await openMidi(); } catch { /* no MIDI: the port list still works */ }
            if (midi && await link.waitPresent(1500)) {
                beforeUpdate();
                say('Restarting the adapter in download mode...');
                const ok = await link.restartForUpdate();
                logLine(ok ? 'Adapter restarting in download mode' : 'Adapter did not answer (firmware before 1.0?)');
                // Still running its firmware, so it would not be in the list.
                if (!ok) throw new FlashError('connect');
            }
            // Continue is also the click the browser needs before it shows the list.
            say('Choose the board in the list, then press Connect.');
            if (!await stepPanel({
                title: 'Choose the board', icon: 'bi-cpu',
                body: `<p>The browser will now list the ESP32 boards plugged in. Radio COM ports are never listed.</p>
                       <p>Choose <b>USB JTAG/serial debug unit</b> (it can take a second to appear), then press <b>Connect</b>.</p>
                       <p class="text-body-secondary small">If the list stays empty, press Cancel there, hold the board's BOOT button while you plug it in, let go, and press Install / Update again.</p>`,
            })) throw new FlashError('cancelled');
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
