import {
  SAMPLE_RATE, FRAME_SAMPLES, MSG_OPUS_RX, MSG_PCM_RX,
  pcm16ToFloat, supportsWebCodecsOpus
} from './audio-protocol.js';

/**
 * Jitter-buffered playback of RX Opus or PCM16 frames.
 */
export class AudioPlayback {
  constructor() {
    this._ctx = null;
    this._worklet = null;
    this._decoder = null;
    this._codec = 'pcm16';
    this._muted = false;
    this._queue = []; // Float32Array frames
    this._maxQueue = 8; // ~160 ms
  }

  get muted() { return this._muted; }
  set muted(v) { this._muted = !!v; }

  async start(codec) {
    this._codec = codec || 'pcm16';
    this._ctx = new AudioContext({ sampleRate: SAMPLE_RATE });

    if (this._codec === 'opus' && supportsWebCodecsOpus()) {
      try {
        await this._startOpusDecoder();
      } catch (e) {
        console.warn('Opus decode unavailable', e);
        this._codec = 'pcm16';
      }
    }

    const processorCode = `
      class YwcPlaybackProcessor extends AudioWorkletProcessor {
        constructor() {
          super();
          this.queue = [];
          this.offset = 0;
          this.port.onmessage = (ev) => {
            if (ev.data && ev.data.length) this.queue.push(ev.data);
          };
        }
        process(inputs, outputs) {
          const out = outputs[0][0];
          if (!out) return true;
          let i = 0;
          while (i < out.length) {
            if (!this.queue.length) {
              out.fill(0, i);
              break;
            }
            const cur = this.queue[0];
            const avail = cur.length - this.offset;
            const need = out.length - i;
            const take = Math.min(avail, need);
            out.set(cur.subarray(this.offset, this.offset + take), i);
            i += take;
            this.offset += take;
            if (this.offset >= cur.length) {
              this.queue.shift();
              this.offset = 0;
            }
          }
          return true;
        }
      }
      registerProcessor('ywc-playback', YwcPlaybackProcessor);
    `;
    const blob = new Blob([processorCode], { type: 'application/javascript' });
    const url = URL.createObjectURL(blob);
    await this._ctx.audioWorklet.addModule(url);
    URL.revokeObjectURL(url);

    this._worklet = new AudioWorkletNode(this._ctx, 'ywc-playback');
    this._worklet.connect(this._ctx.destination);
    if (this._ctx.state === 'suspended') await this._ctx.resume();
  }

  async _startOpusDecoder() {
    this._decoder = new AudioDecoder({
      output: (audioData) => {
        const planes = audioData.numberOfChannels;
        const frames = audioData.numberOfFrames;
        const buf = new Float32Array(frames);
        // copyTo planar channel 0
        audioData.copyTo(buf, { planeIndex: 0 });
        audioData.close();
        this._enqueue(buf);
      },
      error: (e) => console.warn('AudioDecoder error', e)
    });
    this._decoder.configure({
      codec: 'opus',
      sampleRate: SAMPLE_RATE,
      numberOfChannels: 1
    });
  }

  handlePacket(type, payload) {
    if (this._muted) return;

    if (type === MSG_PCM_RX) {
      this._enqueue(pcm16ToFloat(payload));
      return;
    }

    if (type === MSG_OPUS_RX) {
      if (this._decoder) {
        const chunk = new EncodedAudioChunk({
          type: 'key',
          timestamp: performance.now() * 1000,
          data: payload
        });
        try { this._decoder.decode(chunk); }
        catch (e) { console.warn('decode failed', e); }
      }
    }
  }

  _enqueue(float32) {
    if (!this._worklet) return;
    // Cap queue to limit latency
    this._queue.push(float32);
    while (this._queue.length > this._maxQueue) this._queue.shift();
    const frame = this._queue.shift();
    if (frame) this._worklet.port.postMessage(frame, [frame.buffer]);
  }

  async stop() {
    try { this._decoder?.close(); } catch { /* ignore */ }
    this._decoder = null;
    try { this._worklet?.disconnect(); } catch { /* ignore */ }
    try { await this._ctx?.close(); } catch { /* ignore */ }
    this._ctx = null;
    this._worklet = null;
    this._queue = [];
  }
}
