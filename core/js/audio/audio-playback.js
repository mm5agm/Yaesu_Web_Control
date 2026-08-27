// ?v=1 is a one-time cache-buster, not a number to bump — see gaugeFactory.js.
import {
  SAMPLE_RATE, FRAME_SAMPLES, MSG_OPUS_RX, MSG_PCM_RX,
  pcm16ToFloat, supportsWebCodecsOpus
} from './audio-protocol.js?v=1';

/**
 * Low-latency playback of RX Opus or PCM16 frames.
 * Worklet queue is hard-capped (~30 ms) so bursts cannot accumulate delay.
 */
export class AudioPlayback {
  constructor() {
    this._ctx = null;
    this._worklet = null;
    this._analyser = null;
    this._freqBuf = null;
    this._decoder = null;
    this._codec = 'pcm16';
    this._muted = false;
    /** Max queued frames in the worklet (~3 × 10 ms). */
    this._maxQueueFrames = 3;
  }

  get muted() { return this._muted; }
  set muted(v) { this._muted = !!v; }

  /**
   * Live FFT magnitude bins for the filter-scope display, or null when
   * muted / not started (caller should fall back to decorative bars).
   * @returns {{ data: Uint8Array, sampleRate: number, fftSize: number } | null}
   */
  getSpectrum() {
    if (!this._analyser || !this._freqBuf || this._muted) return null;
    this._analyser.getByteFrequencyData(this._freqBuf);
    return {
      data: this._freqBuf,
      sampleRate: this._ctx.sampleRate,
      fftSize: this._analyser.fftSize
    };
  }

  async start(codec) {
    this._codec = codec || 'pcm16';
    // Prefer low output latency when the browser supports it.
    this._ctx = new AudioContext({
      sampleRate: SAMPLE_RATE,
      latencyHint: 'interactive'
    });

    if (this._codec === 'opus' && supportsWebCodecsOpus()) {
      try {
        await this._startOpusDecoder();
      } catch (e) {
        console.warn('Opus decode unavailable', e);
        this._codec = 'pcm16';
      }
    }

    const maxFrames = this._maxQueueFrames;
    const processorCode = `
      class YwcPlaybackProcessor extends AudioWorkletProcessor {
        constructor() {
          super();
          this.queue = [];
          this.offset = 0;
          this.maxFrames = ${maxFrames};
          this.port.onmessage = (ev) => {
            if (!ev.data || !ev.data.length) return;
            this.queue.push(ev.data);
            while (this.queue.length > this.maxFrames) {
              this.queue.shift();
              this.offset = 0;
            }
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
    // Analyser sits on the playback path so filter-scope can show real RX spectrum.
    this._analyser = this._ctx.createAnalyser();
    this._analyser.fftSize = 512;
    this._analyser.smoothingTimeConstant = 0.7;
    this._analyser.minDecibels = -90;
    this._analyser.maxDecibels = -20;
    this._freqBuf = new Uint8Array(this._analyser.frequencyBinCount);
    this._worklet.connect(this._analyser);
    this._analyser.connect(this._ctx.destination);
    if (this._ctx.state === 'suspended') await this._ctx.resume();
  }

  async _startOpusDecoder() {
    this._decoder = new AudioDecoder({
      output: (audioData) => {
        const frames = audioData.numberOfFrames;
        const buf = new Float32Array(frames);
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

    if (type === MSG_OPUS_RX && this._decoder) {
      const chunk = new EncodedAudioChunk({
        type: 'key',
        timestamp: performance.now() * 1000,
        data: payload
      });
      try { this._decoder.decode(chunk); }
      catch (e) { console.warn('decode failed', e); }
    }
  }

  _enqueue(float32) {
    if (!this._worklet) return;
    // Transfer ownership to the worklet (zero-copy).
    this._worklet.port.postMessage(float32, [float32.buffer]);
  }

  async stop() {
    try { this._decoder?.close(); } catch { /* ignore */ }
    this._decoder = null;
    try { this._worklet?.disconnect(); } catch { /* ignore */ }
    try { this._analyser?.disconnect(); } catch { /* ignore */ }
    try { await this._ctx?.close(); } catch { /* ignore */ }
    this._ctx = null;
    this._worklet = null;
    this._analyser = null;
    this._freqBuf = null;
  }
}
