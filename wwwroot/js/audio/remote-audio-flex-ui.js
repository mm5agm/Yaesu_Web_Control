/**
 * Flex workspace Remote Audio UI (/Flex only).
 * Forked from remote-audio-ui.js — do not import this on Index or /RemoteAudio.
 */
import { createAudioSession } from './audio-session.js';
import { supportsWebCodecsOpus } from './audio-protocol.js';

const CHANNEL_NAME = 'ywc-remote-audio-flex';
const POPOUT_STORAGE_KEY = 'ywc.remoteAudio.flex.popout';
const MIC_STORAGE_KEY = 'ywc.remoteAudio.browserMic';
const CODEC_STORAGE_KEY = 'ywc.remoteAudio.codec';
const SPECTRUM_HZ = 15;
const HEARTBEAT_MS = 2000;
const HEARTBEAT_STALE_MS = 6000;

const TIP_MIC_ON = 'Mute microphone';
const TIP_MIC_OFF = 'Unmute microphone';
const TIP_RX_ON = 'Deafen — mute received audio';
const TIP_RX_OFF = 'Undeafen — hear received audio';
const TIP_TX_OFF = 'Toggle transmit (PTT)';
const TIP_TX_ON = 'Click to stop transmitting';
const TX_POLL_MS = 1500;

function isTogglePressed(el) {
  return el?.getAttribute('aria-pressed') === 'true';
}

const BTN_VARIANT_CLASSES = [
  'ywc-btn-success', 'ywc-btn-warning', 'ywc-btn-danger', 'ywc-btn-secondary', 'ywc-btn-outline',
];

function clearBtnVariants(btn) {
  if (!btn) return;
  btn.classList.remove(...BTN_VARIANT_CLASSES);
}

function setMicMuteUi(btn, icon, tipWrap, muted) {
  if (!btn) return;
  btn.setAttribute('aria-pressed', muted ? 'true' : 'false');
  clearBtnVariants(btn);
  btn.classList.add(muted ? 'ywc-btn-warning' : 'ywc-btn-success');
  if (icon) icon.textContent = muted ? '🔇' : '🎤';
  const tip = muted ? TIP_MIC_OFF : TIP_MIC_ON;
  btn.setAttribute('aria-label', tip);
  setTipText(tipWrap, tip);
}

function setRxMuteUi(btn, icon, tipWrap, muted) {
  if (!btn) return;
  btn.setAttribute('aria-pressed', muted ? 'true' : 'false');
  clearBtnVariants(btn);
  btn.classList.add(muted ? 'ywc-btn-warning' : 'ywc-btn-success');
  if (icon) icon.textContent = muted ? '🔇' : '🎧';
  const tip = muted ? TIP_RX_OFF : TIP_RX_ON;
  btn.setAttribute('aria-label', tip);
  setTipText(tipWrap, tip);
}

function setTxButtonUi(btn, tipWrap, transmitting) {
  if (!btn) return;
  const on = !!transmitting;
  btn.setAttribute('aria-pressed', on ? 'true' : 'false');
  clearBtnVariants(btn);
  btn.classList.add(on ? 'ywc-btn-danger' : 'ywc-btn-warning');
  btn.innerHTML = on
    ? '<span class="remote-audio-btn-icon" aria-hidden="true">📻</span> TX ON'
    : '<span class="remote-audio-btn-icon" aria-hidden="true">📻</span> TX';
  const tip = on ? TIP_TX_ON : TIP_TX_OFF;
  btn.setAttribute('aria-label', tip);
  setTipText(tipWrap, tip);
}

function setTipText(el, text) {
  if (!el) return;
  el.setAttribute('aria-label', text);
  el.setAttribute('title', text);
}

function initRemoteAudioTooltips() {
  const bar = document.getElementById('remoteAudioBar');
  if (!bar || bar.dataset.ywcAudioUi !== 'flex') return false;
  bar.querySelectorAll('.ywc-remote-audio-tip').forEach(el => {
    const tip = el.getAttribute('title') || el.getAttribute('aria-label');
    if (tip) el.setAttribute('title', tip);
  });
  return true;
}

function ensureRemoteAudioTooltips() {
  if (initRemoteAudioTooltips()) return;
  window.addEventListener('load', () => initRemoteAudioTooltips(), { once: true });
}

// Live FFT is MAIN/USB mono only today — attach to VFO A alone. VFO B keeps
// the decorative random bars until stereo L→A / R→B lands (see
// .cursor/plans/remote-audio-stereo-filter-scopes.plan.md).
function attachFilterScopeSpectrum(provider) {
  window.filterScopePanelA?.setSpectrumProvider?.(provider);
  window.filterScopePanelB?.setSpectrumProvider?.(null);
}

function clearFilterScopeSpectrum() {
  window.filterScopePanelA?.setSpectrumProvider?.(null);
  window.filterScopePanelB?.setSpectrumProvider?.(null);
}

function openChannel() {
  try {
    return typeof BroadcastChannel !== 'undefined' ? new BroadcastChannel(CHANNEL_NAME) : null;
  } catch {
    return null;
  }
}

function setPopoutOwned(owned) {
  try {
    if (owned) localStorage.setItem(POPOUT_STORAGE_KEY, '1');
    else localStorage.removeItem(POPOUT_STORAGE_KEY);
  } catch { /* ignore */ }
}

function isPopoutOwned() {
  try {
    return localStorage.getItem(POPOUT_STORAGE_KEY) === '1';
  } catch {
    return false;
  }
}

function getSavedMicId() {
  try { return localStorage.getItem(MIC_STORAGE_KEY) || ''; }
  catch { return ''; }
}

function saveMicId(id) {
  try {
    if (id) localStorage.setItem(MIC_STORAGE_KEY, id);
    else localStorage.removeItem(MIC_STORAGE_KEY);
  } catch { /* ignore */ }
}

function getSavedCodec() {
  try {
    const v = localStorage.getItem(CODEC_STORAGE_KEY);
    if (v === 'pcm16' || v === 'opus') return v;
  } catch { /* ignore */ }
  return 'opus';
}

function saveCodec(codec) {
  try {
    localStorage.setItem(CODEC_STORAGE_KEY, codec === 'pcm16' ? 'pcm16' : 'opus');
  } catch { /* ignore */ }
}

function preferredCodecForSession() {
  const saved = getSavedCodec();
  if (saved === 'opus' && !supportsWebCodecsOpus()) return 'pcm16';
  return saved;
}

/** Resolve a control from the panel root (works after FlexLayout popout remount). */
function audioEl(root, id) {
  if (!root) return null;
  const doc = root.ownerDocument || document;
  return doc.getElementById(id) || root.querySelector?.('#' + CSS.escape(id)) || null;
}

/** Wire Start/Stop/mute/meters for the Flex remote-audio panel. */
function bindRemoteAudioFlexControls(bar) {
  const root = bar || document.getElementById('remoteAudioBar');
  if (!root) return;
  const startBtn = audioEl(root, 'remoteAudioStartBtn');
  const stopBtn = audioEl(root, 'remoteAudioStopBtn');
  const micMute = audioEl(root, 'remoteAudioMicMute');
  const rxMute = audioEl(root, 'remoteAudioRxMute');
  const micMuteIcon = micMute?.querySelector('.remote-audio-btn-icon, i');
  const rxMuteIcon = rxMute?.querySelector('.remote-audio-btn-icon, i');
  const micMuteTip = audioEl(root, 'remoteAudioMicMuteTip');
  const rxMuteTip = audioEl(root, 'remoteAudioRxMuteTip');
  const txBtn = audioEl(root, 'remoteAudioTxBtn');
  const txTip = audioEl(root, 'remoteAudioTxTip');
  const txVfoBadge = audioEl(root, 'remoteAudioTxVfo');
  const statusEl = audioEl(root, 'remoteAudioStatus');
  const rxMeter = audioEl(root, 'remoteAudioRxMeter');
  const txMeter = audioEl(root, 'remoteAudioTxMeter');
  const micSelect = audioEl(root, 'remoteAudioMicSelect');
  const codecSelect = audioEl(root, 'remoteAudioCodecSelect');
  const codecHint = audioEl(root, 'remoteAudioCodecHint');
  const rxGainSlider = audioEl(root, 'remoteAudioRxGain');
  const txGainSlider = audioEl(root, 'remoteAudioTxGain');
  const rxGainVal = audioEl(root, 'remoteAudioRxGainVal');
  const txGainVal = audioEl(root, 'remoteAudioTxGainVal');

  const channel = openChannel();
  let session = null;
  let remoteOwned = isPopoutOwned();
  let latestSpectrum = null;
  let spectrumTimer = null;
  let heartbeatTimer = null;
  let staleTimer = null;
  let txPollTimer = null;
  let lastHeartbeat = remoteOwned ? Date.now() : 0;
  let streamStatus = 'idle';
  let streamCodec = null;
  let gainSaveTimer = null;
  let applyingRemoteGain = false;
  let applyingRemoteCodec = false;
  let transmitting = false;
  let txVfo = 0; // 0 = A, 1 = B (effective TX VFO)
  let txBusy = false;

  function selectedMicId() {
    return (micSelect?.value || getSavedMicId() || '').trim();
  }

  function initCodecSelect() {
    if (!codecSelect) return;
    const opusOk = supportsWebCodecsOpus();
    const opusOpt = [...codecSelect.options].find(o => o.value === 'opus');
    if (opusOpt) {
      opusOpt.disabled = !opusOk;
      opusOpt.textContent = opusOk
        ? 'Opus (~32 kb/s) — recommended'
        : 'Opus — not supported in this browser';
    }
    const saved = getSavedCodec();
    codecSelect.value = (saved === 'opus' && !opusOk) ? 'pcm16' : saved;
    if (codecHint) {
      codecHint.textContent = opusOk
        ? 'Opus uses far less bandwidth than PCM16. Stop and reconnect remote audio for a codec change to apply.'
        : 'This browser cannot encode Opus (WebCodecs); PCM16 will be used.';
    }
  }

  function selectedCodec() {
    if (codecSelect?.value === 'pcm16') return 'pcm16';
    return preferredCodecForSession();
  }

  function applyTxState(next, { broadcast, txVfo: nextVfo } = {}) {
    transmitting = !!next;
    if (typeof nextVfo === 'number' && (nextVfo === 0 || nextVfo === 1))
      txVfo = nextVfo;
    setTxButtonUi(txBtn, txTip, transmitting);
    setTxVfoBadge(txVfoBadge, txVfo, transmitting);
    if (broadcast) post({ type: 'txState', transmitting, txVfo });
  }

  function setTxVfoBadge(el, vfo, on) {
    if (!el) return;
    const letter = vfo === 1 ? 'B' : 'A';
    el.textContent = `VFO ${letter}`;
    el.classList.remove('ywc-badge-secondary', 'ywc-badge-warning', 'ywc-badge-danger');
    el.classList.add(on ? 'ywc-badge-danger' : 'ywc-badge-secondary');
    el.title = on
      ? `Transmitting on VFO ${letter}`
      : `Transmit VFO is ${letter}`;
    el.setAttribute('aria-label', el.title);
  }

  async function fetchTxState() {
    try {
      const res = await fetch('/api/cat/tx');
      if (!res.ok) return;
      const data = await res.json();
      const nextOn = typeof data.transmitting === 'boolean' ? data.transmitting : transmitting;
      const nextVfo = (data.txVfo === 0 || data.txVfo === 1) ? data.txVfo : txVfo;
      if (nextOn !== transmitting || nextVfo !== txVfo)
        applyTxState(nextOn, { txVfo: nextVfo });
    } catch { /* ignore */ }
  }

  async function toggleRemoteTx() {
    if (txBusy) return;
    txBusy = true;
    if (txBtn) txBtn.disabled = true;
    try {
      const res = await fetch('/api/cat/tx', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ transmit: !transmitting })
      });
      if (!res.ok) throw new Error('TX toggle failed');
      const data = await res.json();
      const nextVfo = (data.txVfo === 0 || data.txVfo === 1) ? data.txVfo : txVfo;
      applyTxState(!!data.transmitting, { broadcast: true, txVfo: nextVfo });
    } catch (e) {
      console.warn('Remote audio TX toggle failed', e);
      setStatusText(e.message || 'TX toggle failed');
    } finally {
      txBusy = false;
      if (txBtn) txBtn.disabled = false;
    }
  }

  function isTxShortcutBlocked() {
    const active = document.activeElement;
    if (!active || active === document.body || active === document.documentElement)
      return false;
    if (active.isContentEditable) return true;
    const tag = active.tagName;
    if (tag === 'TEXTAREA' || tag === 'SELECT') return true;
    if (tag === 'INPUT') {
      const type = (active.getAttribute('type') || 'text').toLowerCase();
      // Gain sliders are inputs — do not treat them as text entry.
      if (['range', 'checkbox', 'radio', 'button', 'submit', 'reset', 'file', 'color', 'hidden'].includes(type))
        return false;
      return true;
    }
    return false;
  }

  function txShortcutMatches(e, configuredKey) {
    if (configuredKey == null || configuredKey === '') return false;
    const isSpaceShortcut = configuredKey === 'Space' || configuredKey === ' ';
    if (isSpaceShortcut)
      return e.key === ' ' || e.code === 'Space';
    if (configuredKey.length === 1 && e.key.length === 1)
      return e.key.toLowerCase() === configuredKey.toLowerCase();
    return e.key === configuredKey || e.code === configuredKey;
  }

  function clampGain(v) {
    const n = Number(v);
    if (!Number.isFinite(n)) return 1;
    return Math.min(4, Math.max(0.05, n));
  }

  function formatGain(v) {
    return clampGain(v).toFixed(2);
  }

  function readGainUi() {
    return {
      rx: clampGain(rxGainSlider?.value ?? 1),
      tx: clampGain(txGainSlider?.value ?? 1)
    };
  }

  function setGainUi(rx, tx, { silent } = {}) {
    applyingRemoteGain = true;
    try {
      if (typeof rx === 'number' && rxGainSlider) {
        rxGainSlider.value = String(clampGain(rx));
        if (rxGainVal) rxGainVal.textContent = formatGain(rx);
      }
      if (typeof tx === 'number' && txGainSlider) {
        txGainSlider.value = String(clampGain(tx));
        if (txGainVal) txGainVal.textContent = formatGain(tx);
      }
    } finally {
      applyingRemoteGain = false;
    }
    if (!silent) {
      // no-op — callers decide whether to persist/broadcast
    }
  }

  function applyGainLive(rx, tx) {
    if (remoteOwned && streamStatus === 'streaming') {
      post({ type: 'setGain', rx, tx });
      return;
    }
    session?.setGain({ rx, tx });
  }

  function persistGain(rx, tx) {
    clearTimeout(gainSaveTimer);
    gainSaveTimer = setTimeout(async () => {
      try {
        await fetch('/api/audio/gain', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ rx, tx })
        });
      } catch (e) {
        console.warn('Failed to save audio gain', e);
      }
    }, 400);
  }

  function onGainInput() {
    if (applyingRemoteGain) return;
    const { rx, tx } = readGainUi();
    if (rxGainVal) rxGainVal.textContent = formatGain(rx);
    if (txGainVal) txGainVal.textContent = formatGain(tx);
    applyGainLive(rx, tx);
    persistGain(rx, tx);
    post({ type: 'gain', rx, tx });
  }

  async function refreshMicList() {
    if (!micSelect || !navigator.mediaDevices?.enumerateDevices) return;
    const saved = selectedMicId();
    let devices = [];
    try {
      devices = await navigator.mediaDevices.enumerateDevices();
    } catch {
      return;
    }
    const mics = devices.filter(d => d.kind === 'audioinput');
    micSelect.innerHTML = '';
    const def = document.createElement('option');
    def.value = '';
    def.textContent = 'Default microphone';
    micSelect.appendChild(def);
    for (const d of mics) {
      const opt = document.createElement('option');
      opt.value = d.deviceId;
      opt.textContent = d.label || `Microphone ${micSelect.options.length}`;
      micSelect.appendChild(opt);
    }
    if (saved && [...micSelect.options].some(o => o.value === saved))
      micSelect.value = saved;
    else
      micSelect.value = '';
  }

  function setStatusText(text) {
    if (statusEl) statusEl.textContent = text;
  }

  function setMeters(rx, tx) {
    if (rxMeter) rxMeter.style.width = `${Math.min(100, Math.round(rx * 100))}%`;
    if (txMeter) txMeter.style.width = `${Math.min(100, Math.round(tx * 100))}%`;
  }

  function updateLocalButtons() {
    const streaming = streamStatus === 'streaming';
    if (micMute) micMute.disabled = !streaming;
    if (rxMute) rxMute.disabled = !streaming;
    if (micSelect) micSelect.disabled = remoteOwned && streaming;
    if (codecSelect) codecSelect.disabled = streaming;
    if (!streaming) {
      setMicMuteUi(micMute, micMuteIcon, micMuteTip, false);
      setRxMuteUi(rxMute, rxMuteIcon, rxMuteTip, false);
    }
    if (remoteOwned && streaming) {
      if (startBtn) startBtn.disabled = true;
      if (stopBtn) stopBtn.disabled = false;
    } else {
      if (startBtn) startBtn.disabled = streaming;
      if (stopBtn) stopBtn.disabled = !streaming;
    }
  }

  function post(msg) {
    try { channel?.postMessage(msg); } catch { /* ignore */ }
  }

  function attachLocalSpectrum() {
    if (!session) return;
    attachFilterScopeSpectrum(() => session.getSpectrum());
  }

  function attachRemoteSpectrum() {
    attachFilterScopeSpectrum(() => latestSpectrum);
  }

  function stopSpectrumRelay() {
    if (spectrumTimer) {
      clearInterval(spectrumTimer);
      spectrumTimer = null;
    }
  }

  function startSpectrumRelay() {
    stopSpectrumRelay();
    spectrumTimer = setInterval(() => {
      if (!session || streamStatus !== 'streaming') return;
      const spec = session.getSpectrum();
      if (!spec || !spec.data) {
        post({ type: 'spectrum', data: null });
        return;
      }
      post({
        type: 'spectrum',
        data: Array.from(spec.data),
        sampleRate: spec.sampleRate,
        fftSize: spec.fftSize
      });
    }, Math.round(1000 / SPECTRUM_HZ));
  }

  function clearRemoteOwnershipUi() {
    remoteOwned = false;
    setPopoutOwned(false);
    latestSpectrum = null;
    clearFilterScopeSpectrum();
    setMeters(0, 0);
    streamStatus = 'idle';
    streamCodec = null;
    setStatusText('Idle');
    updateLocalButtons();
  }

  function ensureSession() {
    if (session) return session;
    session = createAudioSession({
      onStatus: (s, codec) => {
        streamStatus = s;
        streamCodec = codec || null;
        if (!remoteOwned) {
          setStatusText(s === 'streaming'
            ? `Streaming (${codec})`
            : s.charAt(0).toUpperCase() + s.slice(1));
          if (s === 'streaming') attachLocalSpectrum();
          else clearFilterScopeSpectrum();
        }
        updateLocalButtons();
      },
      onLevels: (rx, tx) => {
        setMeters(rx, tx);
      },
      onError: (msg) => {
        setStatusText(msg);
        if (!remoteOwned) alert(msg);
      }
    });
    return session;
  }

  async function startLocal() {
    try {
      setStatusText('Connecting…');
      await ensureSession().start({
        deviceId: selectedMicId(),
        preferredCodec: selectedCodec()
      });
      await refreshMicList(); // labels appear after permission grant
    } catch (e) {
      const msg = e.message || String(e);
      setStatusText(msg);
      alert(msg);
      updateLocalButtons();
    }
  }

  async function stopLocal() {
    if (session) await session.stop();
    streamStatus = 'stopped';
    streamCodec = null;
    stopSpectrumRelay();
    if (!remoteOwned) {
      clearFilterScopeSpectrum();
      setStatusText('Stopped');
    }
    updateLocalButtons();
  }

  if (channel) {
    channel.onmessage = (ev) => {
      const msg = ev.data;
      if (!msg || typeof msg !== 'object') return;

      if (msg.type === 'heartbeat') {
          lastHeartbeat = Date.now();
          const streaming = msg.status === 'streaming';
          if (streaming) {
            remoteOwned = true;
            setPopoutOwned(true);
            streamStatus = 'streaming';
            streamCodec = msg.codec || null;
            setStatusText(`In pop-out window (streaming${msg.codec ? ` · ${msg.codec}` : ''})`);
            attachRemoteSpectrum();
          } else if (remoteOwned) {
            // Popout still open but no longer streaming — free the main bar.
            clearRemoteOwnershipUi();
          }
          updateLocalButtons();
          return;
        }
        if (msg.type === 'hello' && msg.role === 'popout') {
          lastHeartbeat = Date.now();
          setStatusText('In pop-out window');
          post({ type: 'requestStatus' });
          window.publishTxState?.();
          updateLocalButtons();
          return;
        }
        if (msg.type === 'ownership') {
          lastHeartbeat = Date.now();
          if (!msg.owned) {
            clearRemoteOwnershipUi();
            return;
          }
          setStatusText('In pop-out window');
          updateLocalButtons();
          return;
        }
        if (msg.type === 'popoutClosed') {
          clearRemoteOwnershipUi();
          return;
        }
        if (msg.type === 'status') {
          lastHeartbeat = Date.now();
          if (msg.status === 'streaming') {
            remoteOwned = true;
            setPopoutOwned(true);
            streamStatus = 'streaming';
            streamCodec = msg.codec || null;
            setStatusText(`In pop-out window (streaming${msg.codec ? ` · ${msg.codec}` : ''})`);
            attachRemoteSpectrum();
            updateLocalButtons();
            return;
          }
          // stopped / idle / disconnected from popout
          clearRemoteOwnershipUi();
          if (msg.status && msg.status !== 'idle')
            setStatusText(msg.status.charAt(0).toUpperCase() + msg.status.slice(1));
          return;
        }
        if (msg.type === 'levels') {
          setMeters(msg.rx || 0, msg.tx || 0);
          return;
        }
        if (msg.type === 'error') {
          setStatusText(msg.message || 'Audio error');
          return;
        }
        if (msg.type === 'muteState') {
          if (typeof msg.mic === 'boolean') setMicMuteUi(micMute, micMuteIcon, micMuteTip, msg.mic);
          if (typeof msg.rx === 'boolean') setRxMuteUi(rxMute, rxMuteIcon, rxMuteTip, msg.rx);
          return;
        }
        if (msg.type === 'gain') {
          setGainUi(msg.rx, msg.tx);
          return;
        }
        if (msg.type === 'spectrum') {
          if (!msg.data) {
            latestSpectrum = null;
            return;
          }
          latestSpectrum = {
            data: msg.data instanceof Uint8Array ? msg.data : new Uint8Array(msg.data),
            sampleRate: msg.sampleRate,
            fftSize: msg.fftSize
          };
          return;
        }
        if (msg.type === 'txState') {
          if (typeof msg.transmitting === 'boolean') {
            window.applySharedTxState?.(msg.transmitting);
            applyTxState(msg.transmitting, { txVfo: msg.txVfo });
          }
          return;
        }
        if (msg.type === 'requestTxState') {
          window.publishTxState?.();
          return;
        }
    };
  }

  startBtn?.addEventListener('click', async () => {
    if (remoteOwned && streamStatus === 'streaming') return;
    await startLocal();
  });

  stopBtn?.addEventListener('click', async () => {
    if (remoteOwned && streamStatus === 'streaming') {
      post({ type: 'stop' });
      return;
    }
    await stopLocal();
  });

  micMute?.addEventListener('click', () => {
    const next = !isTogglePressed(micMute);
    setMicMuteUi(micMute, micMuteIcon, micMuteTip, next);
    if (remoteOwned && streamStatus === 'streaming') {
      post({ type: 'setMute', mic: next });
      return;
    }
    session?.setMicMuted(next);
  });

  rxMute?.addEventListener('click', () => {
    const next = !isTogglePressed(rxMute);
    setRxMuteUi(rxMute, rxMuteIcon, rxMuteTip, next);
    if (remoteOwned && streamStatus === 'streaming') {
      post({ type: 'setMute', rx: next });
      return;
    }
    session?.setRxMuted(next);
  });

  txBtn?.addEventListener('click', () => { toggleRemoteTx(); });

  micSelect?.addEventListener('change', () => {
    saveMicId(micSelect.value || '');
  });

  codecSelect?.addEventListener('change', () => {
    if (applyingRemoteCodec) return;
    const next = codecSelect.value === 'pcm16' ? 'pcm16' : 'opus';
    if (next === 'opus' && !supportsWebCodecsOpus()) {
      codecSelect.value = 'pcm16';
      return;
    }
    saveCodec(next);
    post({ type: 'codecPref', codec: next });
  });

  rxGainSlider?.addEventListener('input', onGainInput);
  txGainSlider?.addEventListener('input', onGainInput);

  if (remoteOwned) {
    setStatusText('In pop-out window');
    post({ type: 'requestStatus' });
    window.publishTxState?.();
  }
  staleTimer = setInterval(() => {
    if (!remoteOwned) return;
    if (Date.now() - lastHeartbeat > HEARTBEAT_STALE_MS) {
      clearRemoteOwnershipUi();
    }
  }, HEARTBEAT_MS);

  if (txBtn) {
    fetchTxState();
    txPollTimer = setInterval(fetchTxState, TX_POLL_MS);
    document.addEventListener('keydown', (e) => {
      const configuredKey = window.ywcTxToggleKey;
      if (configuredKey == null || configuredKey === '' || e.ctrlKey || e.metaKey || e.altKey || e.repeat)
        return;
      if (isTxShortcutBlocked()) return;
      if (!txShortcutMatches(e, configuredKey)) return;
      e.preventDefault();
      e.stopPropagation();
      const active = document.activeElement;
      if (active && typeof active.blur === 'function' && active.tagName === 'BUTTON')
        active.blur();
      toggleRemoteTx();
    }, true);
  }

  if (stopBtn) stopBtn.disabled = true;
  setMicMuteUi(micMute, micMuteIcon, micMuteTip, false);
  setRxMuteUi(rxMute, rxMuteIcon, rxMuteTip, false);
  setTxButtonUi(txBtn, txTip, false);
  setTxVfoBadge(txVfoBadge, 0, false);
  initCodecSelect();
  updateLocalButtons();
  ensureRemoteAudioTooltips();
  refreshMicList();
  try {
    navigator.mediaDevices?.addEventListener?.('devicechange', () => refreshMicList());
  } catch { /* ignore */ }
}

/**
 * Flex workspace Remote Audio panel (/Flex) — full inline controls, FlexLayout tab styling.
 */
export async function initRemoteAudioFlex() {
  // Templates mount asynchronously after FlexLayout JSON load — wait briefly.
  let bar = document.getElementById('remoteAudioBar');
  for (let i = 0; (!bar || bar.dataset.ywcAudioUi !== 'flex') && i < 60; i++) {
    await new Promise((r) => setTimeout(r, 50));
    bar = document.getElementById('remoteAudioBar');
  }
  if (!bar || bar.dataset.ywcAudioUi !== 'flex') return;

  let enabled = false;
  let rxGain = 1;
  let txGain = 1;
  try {
    const res = await fetch('/api/audio/status');
    const data = await res.json();
    enabled = !!data.enabled;
    if (typeof data.rxGain === 'number') rxGain = data.rxGain;
    if (typeof data.txGain === 'number') txGain = data.txGain;
  } catch {
    enabled = false;
  }

  if (!enabled) {
    try { window.ywcFlexFlags && (window.ywcFlexFlags.remoteAudio = false); } catch { /* ignore */ }
    try { window.ywcFlex?.hidePanel?.('remoteAudio'); } catch { /* ignore */ }
    const statusEl = audioEl(bar, 'remoteAudioStatus');
    if (statusEl) statusEl.textContent = 'Remote audio is disabled in Settings';
    const startBtn = audioEl(bar, 'remoteAudioStartBtn');
    if (startBtn) startBtn.disabled = true;
    return;
  }

  bar.style.display = '';
  bindRemoteAudioFlexControls(bar);
  const rx = audioEl(bar, 'remoteAudioRxGain');
  const tx = audioEl(bar, 'remoteAudioTxGain');
  const rxV = audioEl(bar, 'remoteAudioRxGainVal');
  const txV = audioEl(bar, 'remoteAudioTxGainVal');
  if (rx) rx.value = String(rxGain);
  if (tx) tx.value = String(txGain);
  if (rxV) rxV.textContent = Number(rxGain).toFixed(2);
  if (txV) txV.textContent = Number(txGain).toFixed(2);
}
