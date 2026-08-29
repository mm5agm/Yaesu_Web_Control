// ?v=1 is a one-time cache-buster, not a number to bump — see gaugeFactory.js.
import {
  SAMPLE_RATE, FRAME_SAMPLES, MSG_OPUS_TX, MSG_PCM_TX, OPUS_FRAME_DURATION_US,
  frameMessage, floatToPcm16, supportsWebCodecsOpus
} from './audio-protocol.js?v=1';

/**
 * Captures microphone PCM, encodes Opus (WebCodecs) or PCM16, sends via callback.
 */
export class AudioCapture {
  constructor({ onFrame, codec = 'pcm16' }) {
    this._onFrame = onFrame;
    this._codec = codec;
    this._seq = 1;
    this._stream = null;
    this._ctx = null;
    this._worklet = null;
    this._encoder = null;
    this._muted = false;
    this._accum = new Float32Array(0);
  }

  get muted() { return this._muted; }
  set muted(v) { this._muted = !!v; }

  /**
   * @param {{ deviceId?: string }} [opts] — browser audioinput deviceId; empty = default.
   */
  async start(opts = {}) {
    const deviceId = (opts.deviceId || '').trim();
    const audio = {
      echoCancellation: false,
      noiseSuppression: false,
      autoGainControl: false,
      channelCount: 1,
      sampleRate: SAMPLE_RATE
    };
    if (deviceId) audio.deviceId = { exact: deviceId };

    try {
      this._stream = await navigator.mediaDevices.getUserMedia({ audio });
    } catch (e) {
      // Stale saved deviceId — fall back to the default mic.
      if (deviceId && (e.name === 'OverconstrainedError' || e.name === 'NotFoundError' || e.name === 'NotReadableError')) {
        delete audio.deviceId;
        this._stream = await navigator.mediaDevices.getUserMedia({ audio });
      } else {
        throw e;
      }
    }

    this._ctx = new AudioContext({
      sampleRate: SAMPLE_RATE,
      latencyHint: 'interactive'
    });
    const source = this._ctx.createMediaStreamSource(this._stream);

    if (this._codec === 'opus' && supportsWebCodecsOpus()) {
      try {
        await this._startOpusEncoder();
      } catch (e) {
        console.warn('Opus encode unavailable, using PCM16', e);
        this._codec = 'pcm16';
      }
    }

    const processorCode = `
      class YwcCaptureProcessor extends AudioWorkletProcessor {
        process(inputs) {
          const input = inputs[0];
          if (input && input[0] && input[0].length) {
            const copy = new Float32Array(input[0]);
            this.port.postMessage(copy, [copy.buffer]);
          }
          return true;
        }
      }
      registerProcessor('ywc-capture', YwcCaptureProcessor);
    `;
    const blob = new Blob([processorCode], { type: 'application/javascript' });
    const url = URL.createObjectURL(blob);
    await this._ctx.audioWorklet.addModule(url);
    URL.revokeObjectURL(url);

    this._worklet = new AudioWorkletNode(this._ctx, 'ywc-capture');
    this._worklet.port.onmessage = (ev) => this._onSamples(ev.data);
    source.connect(this._worklet);
    // Keep graph alive without monitoring locally
    this._worklet.connect(this._ctx.createGain()).connect(this._ctx.destination);
    this._worklet.context.createGain; // silence via gain 0
    const silent = this._ctx.createGain();
    silent.gain.value = 0;
    this._worklet.disconnect();
    source.connect(this._worklet);
    this._worklet.connect(silent);
    silent.connect(this._ctx.destination);
  }

  async _startOpusEncoder() {
    this._pendingChunks = [];
    this._encoder = new AudioEncoder({
      output: (chunk) => {
        // Skip empty / config-only chunks — Concentus only wants Opus packets.
        if (!chunk || chunk.byteLength < 1) return;
        const buf = new ArrayBuffer(chunk.byteLength);
        chunk.copyTo(buf);
        const framed = frameMessage(MSG_OPUS_TX, this._seq++, buf);
        this._onFrame(framed);
      },
      error: (e) => console.warn('AudioEncoder error', e)
    });
    this._encoder.configure({
      codec: 'opus',
      sampleRate: SAMPLE_RATE,
      numberOfChannels: 1,
      bitrate: 32000,
      opus: {
        application: 'voip',
        frameDuration: OPUS_FRAME_DURATION_US
      }
    });
  }

  _onSamples(float32) {
    if (this._muted) return;

    // Accumulate to 10 ms frames
    const merged = new Float32Array(this._accum.length + float32.length);
    merged.set(this._accum);
    merged.set(float32, this._accum.length);
    this._accum = merged;

    while (this._accum.length >= FRAME_SAMPLES) {
      // Own a contiguous copy — AudioEncoder may hold the buffer asynchronously.
      const frame = this._accum.slice(0, FRAME_SAMPLES);
      this._accum = this._accum.slice(FRAME_SAMPLES);

      if (this._codec === 'opus' && this._encoder) {
        try {
          const audioData = new AudioData({
            format: 'f32-planar',
            sampleRate: SAMPLE_RATE,
            numberOfFrames: FRAME_SAMPLES,
            numberOfChannels: 1,
            timestamp: performance.now() * 1000,
            data: frame
          });
          this._encoder.encode(audioData);
          audioData.close();
        } catch (e) {
          console.warn('Opus encode failed', e);
        }
      } else {
        const pcm = floatToPcm16(frame);
        const framed = frameMessage(MSG_PCM_TX, this._seq++, pcm);
        this._onFrame(framed);
      }
    }
  }

  async stop() {
    try { this._encoder?.close(); } catch { /* ignore */ }
    this._encoder = null;
    try { this._worklet?.disconnect(); } catch { /* ignore */ }
    try { await this._ctx?.close(); } catch { /* ignore */ }
    this._ctx = null;
    this._stream?.getTracks().forEach(t => t.stop());
    this._stream = null;
    this._accum = new Float32Array(0);
  }
}
