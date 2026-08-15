// ?v=1 is a one-time cache-buster, not a number to bump — see gaugeFactory.js.
import {
  MSG_CONTROL, MSG_OPUS_RX, MSG_PCM_RX,
  frameMessage, parseBody, buildCodecOfferList
} from './audio-protocol.js?v=1';
import { AudioCapture } from './audio-capture.js?v=1';
import { AudioPlayback } from './audio-playback.js?v=1';

/**
 * Owns the /audio WebSocket session and wires capture ↔ playback.
 */
export class AudioSession {
  constructor({ onStatus, onLevels, onError } = {}) {
    this._onStatus = onStatus || (() => {});
    this._onLevels = onLevels || (() => {});
    this._onError = onError || (() => {});
    this._ws = null;
    this._capture = null;
    this._playback = null;
    this._running = false;
    this._codec = 'pcm16';
  }

  get running() { return this._running; }

  /**
   * @param {{ deviceId?: string, preferredCodec?: 'opus'|'pcm16' }} [opts]
   */
  async start(opts = {}) {
    if (this._running) return;
    if (!window.isSecureContext && location.hostname !== 'localhost' && location.hostname !== '127.0.0.1') {
      throw new Error('Microphone requires HTTPS (or localhost). Enable HTTPS in Settings → Web / HTTP, restart, and open the HTTPS URL.');
    }

    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = `${proto}//${location.host}/audio`;
    this._ws = new WebSocket(url);
    this._ws.binaryType = 'arraybuffer';

    await new Promise((resolve, reject) => {
      const t = setTimeout(() => reject(new Error('WebSocket connect timeout')), 10000);
      this._ws.onopen = () => { clearTimeout(t); resolve(); };
      this._ws.onerror = () => { clearTimeout(t); reject(new Error('WebSocket failed to connect')); };
    });

    const codecs = buildCodecOfferList(opts.preferredCodec || 'opus');

    this._ws.send(frameMessage(MSG_CONTROL, 0, new TextEncoder().encode(JSON.stringify({
      cmd: 'hello',
      codecs
    }))));

    const ready = await this._waitReady();
    this._codec = ready.codec || 'pcm16';

    this._playback = new AudioPlayback();
    await this._playback.start(this._codec);

    this._capture = new AudioCapture({
      codec: this._codec,
      onFrame: (buf) => {
        if (this._ws && this._ws.readyState === WebSocket.OPEN)
          this._ws.send(buf);
      }
    });
    await this._capture.start({ deviceId: opts.deviceId || '' });

    this._ws.onmessage = (ev) => this._onMessage(ev.data);
    this._ws.onclose = () => {
      this._running = false;
      this._onStatus('disconnected');
      this.stop();
    };

    this._running = true;
    this._onStatus('streaming', this._codec);
  }

  _waitReady() {
    return new Promise((resolve, reject) => {
      const t = setTimeout(() => reject(new Error('No ready from server')), 15000);
      this._ws.onmessage = (ev) => {
        const bytes = new Uint8Array(ev.data);
        if (bytes.byteLength < 4) return;
        const bodyLen = new DataView(bytes.buffer).getUint32(0, false);
        const body = bytes.subarray(4, 4 + bodyLen);
        const msg = parseBody(body);
        if (!msg || msg.type !== MSG_CONTROL) return;
        try {
          const obj = JSON.parse(new TextDecoder().decode(msg.payload));
          if (obj.cmd === 'busy') {
            clearTimeout(t);
            reject(new Error(obj.message || 'Audio session busy'));
            return;
          }
          if (obj.cmd === 'error') {
            clearTimeout(t);
            reject(new Error(obj.message || 'Audio error'));
            return;
          }
          if (obj.cmd === 'ready') {
            clearTimeout(t);
            // Switch to normal handler after ready
            this._ws.onmessage = (e) => this._onMessage(e.data);
            resolve(obj);
          }
        } catch (e) {
          clearTimeout(t);
          reject(e);
        }
      };
    });
  }

  _onMessage(data) {
    const bytes = new Uint8Array(data);
    if (bytes.byteLength < 4) return;
    const bodyLen = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(0, false);
    const body = bytes.subarray(4, 4 + bodyLen);
    const msg = parseBody(body);
    if (!msg) return;

    if (msg.type === MSG_CONTROL) {
      try {
        const obj = JSON.parse(new TextDecoder().decode(msg.payload));
        if (obj.cmd === 'levels') this._onLevels(obj.rx || 0, obj.tx || 0);
        if (obj.cmd === 'error') this._onError(obj.message || 'Audio error');
      } catch { /* ignore */ }
      return;
    }

    if (msg.type === MSG_OPUS_RX || msg.type === MSG_PCM_RX)
      this._playback?.handlePacket(msg.type, msg.payload);
  }

  setMicMuted(muted) {
    if (this._capture) this._capture.muted = muted;
  }

  setRxMuted(muted) {
    if (this._playback) this._playback.muted = muted;
  }

  /** Live software gain on the host bridge (0.05–4). */
  setGain({ rx, tx } = {}) {
    if (!this._ws || this._ws.readyState !== WebSocket.OPEN) return;
    const body = { cmd: 'setGain' };
    if (typeof rx === 'number') body.rx = rx;
    if (typeof tx === 'number') body.tx = tx;
    this._ws.send(frameMessage(MSG_CONTROL, 0, new TextEncoder().encode(JSON.stringify(body))));
  }

  /** Live RX FFT for filter-scope, or null when not streaming / muted. */
  getSpectrum() {
    return this._playback?.getSpectrum() ?? null;
  }

  async stop() {
    this._running = false;
    try { await this._capture?.stop(); } catch { /* ignore */ }
    try { await this._playback?.stop(); } catch { /* ignore */ }
    this._capture = null;
    this._playback = null;
    if (this._ws) {
      try { this._ws.close(); } catch { /* ignore */ }
      this._ws = null;
    }
    this._onStatus('stopped');
  }
}

export function createAudioSession(handlers) {
  return new AudioSession(handlers);
}
