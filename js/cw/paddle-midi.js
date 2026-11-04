// A CW paddle that reaches the browser as MIDI notes, the way a HaliKey MIDI
// sends it: note on when a paddle is pressed, note off (or note on with
// velocity 0) when it is let go. The HaliKey uses note 20 for dit and 21 for
// dah; both are settable, as is the channel (0 = any).
//
// Web MIDI needs Chrome or Edge and a secure page (https, or localhost), and
// the browser asks the user's permission the first time.
//
// If the device goes away while a paddle is down, the paddle is let go:
// a paddle that is never released would key for ever.

import { DIT, DAH } from './iambic-keyer.js';

export class MidiPaddle {
    // onPaddle(paddle, down)         a paddle pressed or let go
    // onMessage({ input, data, at, paddle, down })  every message, for a log
    // onDevices([{ id, name, state }])  the inputs, whenever they change
    constructor({ ditNote = 20, dahNote = 21, channel = 0, swap = false,
                  onPaddle = () => {}, onMessage = () => {}, onDevices = () => {} } = {}) {
        this.ditNote = ditNote;
        this.dahNote = dahNote;
        this.channel = channel;
        this.swap = swap;
        this._onPaddle = onPaddle;
        this._onMessage = onMessage;
        this._onDevices = onDevices;
        this._access = null;
        this._selected = '';     // input id; '' = all inputs
        this._down = { dit: false, dah: false };
    }

    static get supported() {
        return typeof navigator !== 'undefined' && typeof navigator.requestMIDIAccess === 'function';
    }

    async open() {
        this._access = await navigator.requestMIDIAccess({ sysex: false });
        this._access.onstatechange = e => {
            if (e.port?.type === 'input' && e.port.state === 'disconnected') this.releaseAll();
            this._attach();
        };
        this._attach();
    }

    get inputs() {
        if (!this._access) return [];
        return [...this._access.inputs.values()].map(i => ({ id: i.id, name: i.name, state: i.state }));
    }

    select(id) {
        this._selected = id || '';
        this.releaseAll();
        this._attach();
    }

    releaseAll() {
        for (const p of [DIT, DAH]) {
            if (this._down[p]) {
                this._down[p] = false;
                this._onPaddle(p, false);
            }
        }
    }

    _attach() {
        for (const input of this._access.inputs.values()) {
            const listen = input.state === 'connected' && (!this._selected || input.id === this._selected);
            input.onmidimessage = listen ? e => this._message(input, e) : null;
        }
        this._onDevices(this.inputs);
    }

    _message(input, e) {
        const data = [...e.data];
        const type = data[0] & 0xF0, ch = (data[0] & 0x0F) + 1;
        let paddle = null, down = null;
        if ((type === 0x90 || type === 0x80) && data.length >= 3 && (!this.channel || ch === this.channel)) {
            down = type === 0x90 && data[2] > 0;
            if (data[1] === this.ditNote) paddle = DIT;
            else if (data[1] === this.dahNote) paddle = DAH;
            if (paddle && this.swap) paddle = paddle === DIT ? DAH : DIT;
        }
        this._onMessage({ input: input.name, data, at: e.timeStamp, paddle, down });
        if (paddle && this._down[paddle] !== down) {
            this._down[paddle] = down;
            this._onPaddle(paddle, down);
        }
    }
}
