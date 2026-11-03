import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import {
    sysexRequest, parseReply, parseVersion, compareVersions, isAdapterName,
    versionAdvice, failureAdvice, md5Hex, flashImage, choosePort, CMD_VERSION, CMD_DOWNLOAD,
} from '../../js/cw/paddle-firmware.js';

const fw = new URL('../../firmware/paddle-adapter/', import.meta.url);

test('requests are the firmware\'s SysEx: F0 7D 59 50 cmd F7', () => {
    assert.deepEqual(sysexRequest(CMD_VERSION), [0xF0, 0x7D, 0x59, 0x50, 0x01, 0xF7]);
    assert.deepEqual(sysexRequest(CMD_DOWNLOAD), [0xF0, 0x7D, 0x59, 0x50, 0x02, 0xF7]);
});

test('a version reply is read, and anything else is not ours', () => {
    const text = [...'1.0 user'].map(c => c.charCodeAt(0));
    assert.deepEqual(parseReply(new Uint8Array([0xF0, 0x7D, 0x59, 0x50, 0x01, ...text, 0xF7])), { cmd: 1, text: '1.0 user' });
    assert.deepEqual(parseReply([0xF0, 0x7D, 0x59, 0x50, 0x02, 0xF7]), { cmd: 2, text: '' });
    assert.equal(parseReply([0x90, 20, 127]), null);
    assert.equal(parseReply([0xF0, 0x7E, 0x59, 0x50, 0x01, 0xF7]), null);
    assert.equal(parseReply([0xF0, 0x7D, 0x59, 0x50, 0x01]), null);
});

test('versions compare part by part, as numbers', () => {
    assert.deepEqual(parseVersion('1.2 user'), { version: '1.2', build: 'user' });
    assert.equal(compareVersions('1.0', '1.0'), 0);
    assert.equal(compareVersions('1.0', '1.0.0'), 0);
    assert.equal(compareVersions('1.9', '1.10'), -1);
    assert.equal(compareVersions('2.0', '1.10'), 1);
});

test('the adapter and the emulator are recognised by name', () => {
    assert.ok(isAdapterName('YWC Paddle Adapter'));
    assert.ok(isAdapterName('MIDIIN2 (YWC Paddle Emulator)'));
    assert.ok(!isAdapterName('HaliKey MIDI'));
    assert.ok(!isAdapterName(undefined));
});

test('the advice follows what the adapter said', () => {
    const name = 'YWC Paddle Adapter';
    assert.equal(versionAdvice(null, '1.0').state, 'none');
    assert.equal(versionAdvice({ name, version: '', build: '' }, '1.0').state, 'old');
    assert.match(versionAdvice({ name, version: '', build: '' }, '1.0').text, /BOOT/);
    assert.equal(versionAdvice({ name, version: '1.0', build: 'emulator' }, '1.0').state, 'dev');
    assert.equal(versionAdvice({ name, version: '0.9', build: 'user' }, '1.0').state, 'update');
    assert.equal(versionAdvice({ name, version: '1.0', build: 'user' }, '1.0').state, 'current');
    assert.equal(versionAdvice({ name, version: '1.1', build: 'user' }, '1.0').state, 'newer');
});

test('a board that will not connect, or is not listed, is told to hold BOOT', () => {
    assert.match(failureAdvice('connect'), /BOOT/);
    assert.match(failureAdvice('port'), /BOOT/);
    assert.match(failureAdvice('write', 'MD5 of file does not match data in flash!'), /did not read back/);
    assert.match(failureAdvice('chip', 'ESP32-C3'), /ESP32-C3.*ESP32-S3 boards only/);
    assert.match(failureAdvice('gesture'), /again/);
});

test('md5Hex matches node\'s MD5 at every padding boundary', () => {
    for (const n of [0, 1, 3, 55, 56, 57, 63, 64, 65, 119, 120, 1000, 70000]) {
        const b = new Uint8Array(n).map((_, i) => (i * 31 + 7) & 0xFF);
        assert.equal(md5Hex(b), createHash('md5').update(b).digest('hex'), `length ${n}`);
    }
});

test('the port list asks only for Espressif\'s USB ID, never a radio\'s', async () => {
    let asked;
    await choosePort({ requestPort: async o => { asked = o; return {}; } });
    assert.deepEqual(asked, { filters: [{ usbVendorId: 0x303A }] });
    await assert.rejects(choosePort({ requestPort: async () => { throw Object.assign(new Error('x'), { name: 'NotFoundError' }); } }),
        e => e.stage === 'port');
    await assert.rejects(choosePort({ requestPort: async () => { throw Object.assign(new Error('x'), { name: 'SecurityError' }); } }),
        e => e.stage === 'gesture');
});

function fakeEsptool({ chip = 'ESP32-S3 (QFN56)', failAt = null } = {}) {
    const calls = [];
    class Transport {
        constructor(port) { this.port = port; }
        async setDTR(v) { calls.push(`dtr ${v}`); }
        async setRTS(v) { calls.push(`rts ${v}`); }
        async disconnect() { calls.push('disconnect'); }
    }
    class ESPLoader {
        constructor(o) { this.o = o; }
        async main(mode) { calls.push(`main ${mode}`); if (failAt === 'connect') throw new Error('Failed to connect with the device'); return chip; }
        async writeFlash(o) {
            calls.push('write');
            this.o.terminal.writeLine('writing');
            o.reportProgress(0, 50, 100);
            assert.equal(o.fileArray[0].address, 0);
            assert.equal(o.flashSize, 'keep');
            assert.equal(o.calculateMD5Hash(new Uint8Array([1, 2, 3])), createHash('md5').update(new Uint8Array([1, 2, 3])).digest('hex'));
            if (failAt === 'write') throw new Error('MD5 of file does not match data in flash!');
        }
        async writeReg(addr, value, mask) { calls.push(`reg ${addr.toString(16)} ${value} ${mask}`); }
    }
    return { esptool: { Transport, ESPLoader }, calls };
}

test('flashing writes at 0, checks by MD5, resets into the firmware and lets go of the port', async () => {
    const { esptool, calls } = fakeEsptool();
    const progress = [], logs = [];
    await flashImage({ port: {}, image: new Uint8Array(8), esptool, onProgress: f => progress.push(f), onLog: s => logs.push(s) });
    // The force-download flag is cleared first, or the reset lands in download mode again.
    assert.deepEqual(calls, ['main default_reset', 'write', 'reg 6000812c 0 1', 'dtr false', 'rts true', 'rts false', 'disconnect']);
    assert.deepEqual(progress, [0.5]);
    assert.deepEqual(logs, ['writing']);
});

test('the port vanishing during the reset is the reset working, not a failure', async () => {
    const { esptool, calls } = fakeEsptool();
    esptool.Transport.prototype.setRTS = async function (v) {
        calls.push(`rts ${v}`);
        if (!v) throw new Error('The device has been lost.');
    };
    await flashImage({ port: {}, image: new Uint8Array(8), esptool });
    assert.equal(calls.at(-1), 'disconnect');
});

test('a failed flash says what to do, and still lets go of the port', async () => {
    for (const [failAt, stage] of [['connect', 'connect'], ['write', 'write']]) {
        const { esptool, calls } = fakeEsptool({ failAt });
        await assert.rejects(flashImage({ port: {}, image: new Uint8Array(8), esptool }), e => e.stage === stage);
        assert.equal(calls.at(-1), 'disconnect');
    }
    const { esptool, calls } = fakeEsptool({ chip: 'ESP32-C3' });
    await assert.rejects(flashImage({ port: {}, image: new Uint8Array(8), esptool }), e => e.stage === 'chip');
    assert.ok(!calls.includes('write'));
});

test('dist/ holds the firmware the source says, so no stale file ships', () => {
    const src = readFileSync(new URL('src/main.cpp', fw), 'utf8');
    const version = src.match(/^#define FW_VERSION "([^"]+)"/m)[1];
    const json = JSON.parse(readFileSync(new URL('dist/firmware.json', fw), 'utf8'));
    assert.equal(json.version, version, 'firmware.json: run make-dist.ps1');
    const bin = readFileSync(new URL(`dist/${json.file}`, fw));
    assert.ok(bin.includes(Buffer.from(`YWC Paddle Adapter ${version}:`)), 'the .bin is not the user build of this version: run make-dist.ps1');
    assert.equal(bin[0], 0xE9, 'the .bin starts with an ESP image header');
});
