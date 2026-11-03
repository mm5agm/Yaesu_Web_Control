// Installs and updates the paddle adapter's firmware from the browser: the
// ESP32-S3 board of core/firmware/paddle-adapter, which reaches the CW Paddle
// page as a MIDI device. The app serves the built file and its firmware.json
// (core/firmware/paddle-adapter/dist) at firmwareUrl.
//
// How an update goes:
// 1. Over MIDI, with SysEx (the browser asks permission): the adapter says
//    its firmware version, and is told to restart in the chip's own download
//    mode. Firmware before 1.0 does not answer.
// 2. Over Web Serial: the user picks the board in the browser's list, and
//    esptool-js writes the file, reads it back by MD5 and resets the board,
//    which starts the new firmware.
// 3. Over MIDI again: the adapter comes back and says its new version.
//
// The port list is limited to Espressif's own USB ID (303A), the native USB
// port of an ESP32-S3. A radio's COM port (Silicon Labs, FTDI, Icom ...) can
// never be chosen, so programming can never toggle DTR/RTS on a key line.
// A board that is not listed, or does not answer, needs BOOT held while it
// is plugged in, which puts any ESP32-S3 in download mode.
//
// Chrome or Edge, on a secure page (https or localhost), as for Web MIDI.

export const ESPRESSIF_VID = 0x303A;
export const SYSEX_HEAD = [0xF0, 0x7D, 0x59, 0x50];   // 7D: non-commercial; 59 50: "YP"
export const CMD_VERSION = 0x01;
export const CMD_DOWNLOAD = 0x02;

export function sysexRequest(cmd) {
    return [...SYSEX_HEAD, cmd, 0xF7];
}

// { cmd, text } for one of the adapter's replies, else null.
export function parseReply(data) {
    const d = [...data];
    if (d.length < SYSEX_HEAD.length + 2 || d.at(-1) !== 0xF7) return null;
    if (SYSEX_HEAD.some((b, i) => d[i] !== b)) return null;
    const text = String.fromCharCode(...d.slice(SYSEX_HEAD.length + 1, -1));
    return { cmd: d[SYSEX_HEAD.length], text };
}

// "1.0 user" -> { version: '1.0', build: 'user' }
export function parseVersion(text) {
    const [version = '', build = ''] = String(text).trim().split(/\s+/);
    return { version, build };
}

// Dotted numbers compared part by part: -1, 0 or 1.
export function compareVersions(a, b) {
    const pa = String(a).split('.').map(Number), pb = String(b).split('.').map(Number);
    for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
        const x = pa[i] || 0, y = pb[i] || 0;
        if (x !== y) return x < y ? -1 : 1;
    }
    return 0;
}

export function isAdapterName(name) {
    return /YWC Paddle (Adapter|Emulator)/i.test(name || '');
}

// What the page says about the adapter, from askVersion's answer and the
// version this app carries.
export function versionAdvice(found, carried) {
    if (!found) return { state: 'none', text: 'No paddle adapter found over MIDI. A new board has no adapter firmware yet: go straight to Install.' };
    if (!found.version) return { state: 'old', text: `${found.name} found, with firmware older than 1.0 (it does not say its version). Install ${carried}: hold BOOT while you plug the board in, this once.` };
    if (found.build && found.build !== 'user') return { state: 'dev', text: `${found.name} found, firmware ${found.version} (${found.build} build). Install replaces it with the user firmware ${carried}.` };
    const c = compareVersions(found.version, carried);
    if (c < 0) return { state: 'update', text: `${found.name} found, firmware ${found.version}. This app carries ${carried}: press Update.` };
    if (c > 0) return { state: 'newer', text: `${found.name} found, firmware ${found.version}, newer than the ${carried} this app carries. Nothing to do.` };
    return { state: 'current', text: `${found.name} found, firmware ${found.version}: up to date.` };
}

// What to tell the user when programming fails. stage: 'port' (no port
// chosen), 'gesture' (the list was asked for too long after the click),
// 'connect', 'chip', 'write' or 'reset'.
export function failureAdvice(stage, message = '') {
    const boot = 'Hold the BOOT button, plug the board\'s USB cable in (or press and let go of RST), let go of BOOT, then press Install again.';
    switch (stage) {
        case 'port': return `No board chosen. If the board was not in the list: ${boot}`;
        case 'gesture': return 'The browser shows the list of boards only straight after a click: press Install / Update again.';
        case 'connect': return `The board did not answer. ${boot}`;
        case 'chip': return `This board is ${message || 'not an ESP32-S3'}. The adapter firmware is for ESP32-S3 boards only.`;
        case 'write':
            if (/md5/i.test(message)) return 'The firmware was written but did not read back the same. Press Install again; if it happens again, try another USB cable.';
            return `Programming stopped part way (${message}). Check the cable, then press Install again. ${boot}`;
        case 'reset': return 'Written and checked, but the board did not restart. Unplug it and plug it back in.';
        default: return message;
    }
}

// MD5 of bytes, as lowercase hex: esptool-js compares it with what the chip
// reads back from flash, which is the check that the write took.
const MD5_S = [7, 12, 17, 22, 5, 9, 14, 20, 4, 11, 16, 23, 6, 10, 15, 21];
const MD5_K = Array.from({ length: 64 }, (_, i) => Math.floor(Math.abs(Math.sin(i + 1)) * 2 ** 32) >>> 0);

export function md5Hex(bytes) {
    const n = bytes.length;
    const words = (((n + 8) >>> 6) + 1) * 16;
    const x = new Uint32Array(words);
    for (let i = 0; i < n; i++) x[i >> 2] |= bytes[i] << ((i % 4) * 8);
    x[n >> 2] |= 0x80 << ((n % 4) * 8);
    x[words - 2] = (n * 8) >>> 0;
    x[words - 1] = Math.floor(n / 0x20000000);
    let a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;
    for (let o = 0; o < words; o += 16) {
        let a = a0, b = b0, c = c0, d = d0;
        for (let i = 0; i < 64; i++) {
            let f, g;
            if (i < 16) { f = (b & c) | (~b & d); g = i; }
            else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) % 16; }
            else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) % 16; }
            else { f = c ^ (b | ~d); g = (7 * i) % 16; }
            const s = MD5_S[(i >> 4) * 4 + (i % 4)];
            const t = (a + f + MD5_K[i] + x[o + g]) | 0;
            a = d; d = c; c = b;
            b = (b + ((t << s) | (t >>> (32 - s)))) | 0;
        }
        a0 = (a0 + a) | 0; b0 = (b0 + b) | 0; c0 = (c0 + c) | 0; d0 = (d0 + d) | 0;
    }
    let hex = '';
    for (const v of [a0, b0, c0, d0])
        for (let i = 0; i < 4; i++) hex += ((v >>> (i * 8)) & 0xFF).toString(16).padStart(2, '0');
    return hex;
}

// The adapter over Web MIDI, with SysEx. Listens with addEventListener, so
// it never takes the onmidimessage the page's MidiPaddle uses.
export class AdapterLink {
    constructor({ onChange = () => {} } = {}) {
        this._onChange = onChange;
        this._access = null;
    }

    static get supported() {
        return typeof navigator !== 'undefined' && typeof navigator.requestMIDIAccess === 'function';
    }

    async open() {
        if (this._access) return;
        this._access = await navigator.requestMIDIAccess({ sysex: true });
        this._access.addEventListener('statechange', () => this._onChange());
    }

    _ports(kind) {
        if (!this._access) return [];
        return [...this._access[kind].values()].filter(p => p.state === 'connected' && isAdapterName(p.name));
    }

    get present() { return this._ports('outputs').length > 0; }

    // Sends cmd to the first adapter and waits for its reply: the reply's
    // text, '' for none expected, or null if it did not answer in time.
    _ask(cmd, timeoutMs) {
        const out = this._ports('outputs')[0];
        if (!out) return Promise.resolve(undefined);
        const ins = this._ports('inputs');
        return new Promise(resolve => {
            const done = text => {
                clearTimeout(timer);
                ins.forEach(i => i.removeEventListener('midimessage', listen));
                resolve(text);
            };
            const listen = e => {
                const r = parseReply(e.data);
                if (r && r.cmd === cmd) done(r.text);
            };
            const timer = setTimeout(() => done(null), timeoutMs);
            ins.forEach(i => i.addEventListener('midimessage', listen));
            out.send(sysexRequest(cmd));
        });
    }

    // { name, version, build } (version '' when it did not answer: firmware
    // before 1.0), or null when no adapter is plugged in by its USB port.
    async askVersion(timeoutMs = 800) {
        const out = this._ports('outputs')[0];
        if (!out) return null;
        const text = await this._ask(CMD_VERSION, timeoutMs);
        return { name: out.name, ...(text ? parseVersion(text) : { version: '', build: '' }) };
    }

    // True when the adapter said it is restarting in download mode.
    async restartForUpdate(timeoutMs = 800) {
        return typeof (await this._ask(CMD_DOWNLOAD, timeoutMs)) === 'string';
    }
}

export class FlashError extends Error {
    constructor(stage, message) {
        super(failureAdvice(stage, message));
        this.stage = stage;
        this.detail = message;
    }
}

// Lets the user pick the board: only ports with Espressif's USB ID are listed.
export async function choosePort(serial = navigator.serial) {
    try {
        return await serial.requestPort({ filters: [{ usbVendorId: ESPRESSIF_VID }] });
    } catch (e) {
        throw new FlashError(e?.name === 'SecurityError' ? 'gesture' : 'port', e?.message);
    }
}

// Writes image (Uint8Array, a merged file for offset 0) to the board on port,
// checks it by MD5 and resets the board. onProgress(fraction), onLog(text).
// esptool is the esptool-js module; loaded from core/js/vendor when omitted.
export async function flashImage({ port, image, onProgress = () => {}, onLog = () => {}, esptool = null }) {
    const { ESPLoader, Transport } = esptool ?? await import('../vendor/esptool-js.js');
    const transport = new Transport(port, false);
    const terminal = { clean() {}, writeLine: s => onLog(s), write: s => onLog(s) };
    const loader = new ESPLoader({ transport, baudrate: 921600, terminal, debugLogging: false });
    let stage = 'connect';
    try {
        const chip = await loader.main('default_reset');
        if (!/ESP32-S3/i.test(chip)) throw new FlashError('chip', chip);
        stage = 'write';
        await loader.writeFlash({
            fileArray: [{ data: image, address: 0 }],
            flashMode: 'keep', flashFreq: 'keep', flashSize: 'keep',
            eraseAll: false, compress: true,
            reportProgress: (_, written, total) => onProgress(total ? written / total : 0),
            calculateMD5Hash: md5Hex,
        });
        stage = 'reset';
        await loader.after('hard_reset');
    } catch (e) {
        throw e instanceof FlashError ? e : new FlashError(stage, e?.message || String(e));
    } finally {
        try { await transport.disconnect(); } catch { /* the board has gone: it restarted */ }
    }
}
