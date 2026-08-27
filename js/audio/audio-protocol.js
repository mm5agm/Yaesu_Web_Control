/** Shared wire constants — keep in sync with Services/Audio/AudioConstants.cs */
export const SAMPLE_RATE = 48000;
/** 10 ms frames — lower packetization delay than 20 ms. */
export const FRAME_SAMPLES = 480;
/** Match host Opus encode; WebCodecs defaults to 20000 if omitted. */
export const OPUS_FRAME_DURATION_US = 10000;
export const MSG_OPUS_RX = 0x01;
export const MSG_OPUS_TX = 0x02;
export const MSG_PCM_RX = 0x03;
export const MSG_PCM_TX = 0x04;
export const MSG_CONTROL = 0x10;

export function frameMessage(type, seq, payload) {
  const bodyLen = 1 + 4 + payload.byteLength;
  const buf = new ArrayBuffer(4 + bodyLen);
  const view = new DataView(buf);
  view.setUint32(0, bodyLen, false);
  view.setUint8(4, type);
  view.setUint32(5, seq, false);
  new Uint8Array(buf, 9).set(new Uint8Array(payload));
  return buf;
}

export function parseBody(body) {
  if (body.byteLength < 5) return null;
  const view = new DataView(body.buffer, body.byteOffset, body.byteLength);
  const type = view.getUint8(0);
  const seq = view.getUint32(1, false);
  const payload = body.subarray(5);
  return { type, seq, payload };
}

export function floatToPcm16(float32) {
  const out = new ArrayBuffer(float32.length * 2);
  const view = new DataView(out);
  for (let i = 0; i < float32.length; i++) {
    const s = Math.max(-1, Math.min(1, float32[i]));
    view.setInt16(i * 2, s < 0 ? s * 0x8000 : s * 0x7fff, true);
  }
  return out;
}

export function pcm16ToFloat(bytes) {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const n = (bytes.byteLength / 2) | 0;
  const out = new Float32Array(n);
  for (let i = 0; i < n; i++) out[i] = view.getInt16(i * 2, true) / 32768;
  return out;
}

export function supportsWebCodecsOpus() {
  return typeof AudioEncoder !== 'undefined' && typeof AudioDecoder !== 'undefined';
}

/** Codecs this browser can offer, preferred first (Opus when WebCodecs is available). */
export function buildCodecOfferList(preferred = 'opus') {
  const available = [];
  if (supportsWebCodecsOpus()) available.push('opus');
  available.push('pcm16');
  const want = preferred === 'pcm16' ? 'pcm16' : 'opus';
  const ordered = [];
  if (available.includes(want)) ordered.push(want);
  for (const c of available) {
    if (!ordered.includes(c)) ordered.push(c);
  }
  return ordered;
}
