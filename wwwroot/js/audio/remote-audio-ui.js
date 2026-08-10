import { createAudioSession } from './audio-session.js';
import { supportsWebCodecsOpus } from './audio-protocol.js';

const CHANNEL_NAME = 'ywc-remote-audio';
const POPOUT_STORAGE_KEY = 'ywc.remoteAudio.popout';
const MIC_STORAGE_KEY = 'ywc.remoteAudio.browserMic';
const CODEC_STORAGE_KEY = 'ywc.remoteAudio.codec';
const POPOUT_WINDOW_NAME = 'ywc-remote-audio';
const POPOUT_FEATURES = 'width=780,height=380,resizable=yes,scrollbars=no';
const SPECTRUM_HZ = 15;
const HEARTBEAT_MS = 2000;
const HEARTBEAT_STALE_MS = 6000;

const TIP_CONNECT = 'Connect to transceiver audio';
const TIP_DISCONNECT = 'Disconnect transceiver audio';
const TIP_POPOUT =
  'Popout audio, useful to navigate YWC without interrupting the audio stream';
const TIP_POPOUT_REOPEN =
  'Re-open the audio pop-out window';

const TIP_MIC_ON = 'Mute microphone';
const TIP_MIC_OFF = 'Unmute microphone';
const TIP_RX_ON = 'Deafen — mute received audio';
const TIP_RX_OFF = 'Undeafen — hear received audio';

function isTogglePressed(el) {
  return el?.getAttribute('aria-pressed') === 'true';
}

function setMicMuteUi(btn, icon, tipWrap, muted) {
  if (!btn) return;
  btn.setAttribute('aria-pressed', muted ? 'true' : 'false');
  btn.classList.remove('btn-success', 'btn-warning', 'btn-outline-success', 'btn-outline-warning');
  btn.classList.add(muted ? 'btn-warning' : 'btn-success');
  if (icon) icon.className = `bi ${muted ? 'bi-mic-mute-fill' : 'bi-mic-fill'} remote-audio-btn-icon`;
  const tip = muted ? TIP_MIC_OFF : TIP_MIC_ON;
  btn.setAttribute('aria-label', tip);
  setTipText(tipWrap, tip);
}

function setRxMuteUi(btn, icon, tipWrap, muted) {
  if (!btn) return;
  btn.setAttribute('aria-pressed', muted ? 'true' : 'false');
  btn.classList.remove('btn-success', 'btn-warning', 'btn-outline-success', 'btn-outline-warning');
  btn.classList.add(muted ? 'btn-warning' : 'btn-success');
  if (icon) icon.className = `bi ${muted ? 'bi-volume-mute-fill' : 'bi-headphones'} remote-audio-btn-icon`;
  const tip = muted ? TIP_RX_OFF : TIP_RX_ON;
  btn.setAttribute('aria-label', tip);
  setTipText(tipWrap, tip);
}

function setTipText(el, text) {
  if (!el) return;
  el.setAttribute('data-bs-title', text);
  el.setAttribute('aria-label', text);
  if (typeof bootstrap === 'undefined') return;
  const existing = bootstrap.Tooltip.getInstance(el);
  if (existing) existing.dispose();
  bootstrap.Tooltip.getOrCreateInstance(el, {
    delay: { show: 200, hide: 50 },
    trigger: 'hover focus',
    placement: 'top'
  });
}

function initRemoteAudioTooltips() {
  if (typeof bootstrap === 'undefined') return false;
  document.querySelectorAll('#remoteAudioBar .remote-audio-tip[data-bs-toggle="tooltip"]').forEach(el => {
    bootstrap.Tooltip.getOrCreateInstance(el, {
      delay: { show: 200, hide: 50 },
      trigger: 'hover focus',
      placement: 'top'
    });
  });
  return true;
}

function ensureRemoteAudioTooltips() {
  if (initRemoteAudioTooltips()) return;
  window.addEventListener('load', () => initRemoteAudioTooltips(), { once: true });
}

function attachFilterScopeSpectrum(provider) {
  window.filterScopePanelA?.setSpectrumProvider?.(provider);
  window.filterScopePanelB?.setSpectrumProvider?.(provider);
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

function openPopoutWindow(autostart) {
  const q = autostart ? '?autostart=1' : '';
  return window.open(`/RemoteAudio${q}`, POPOUT_WINDOW_NAME, POPOUT_FEATURES);
}

/**
 * Wire Start/Stop/mute/meters for a remote-audio bar.
 * @param {'index'|'popout'} role
 */
function bindRemoteAudioControls(role) {
  const startBtn = document.getElementById('remoteAudioStartBtn');
  const stopBtn = document.getElementById('remoteAudioStopBtn');
  const popoutBtn = document.getElementById('remoteAudioPopoutBtn');
  const popoutTip = document.getElementById('remoteAudioPopoutTip');
  const closeBtn = document.getElementById('remoteAudioCloseBtn');
  const micMute = document.getElementById('remoteAudioMicMute');
  const rxMute = document.getElementById('remoteAudioRxMute');
  const micMuteIcon = micMute?.querySelector('i');
  const rxMuteIcon = rxMute?.querySelector('i');
  const micMuteTip = document.getElementById('remoteAudioMicMuteTip');
  const rxMuteTip = document.getElementById('remoteAudioRxMuteTip');
  const statusEl = document.getElementById('remoteAudioStatus');
  const rxMeter = document.getElementById('remoteAudioRxMeter');
  const txMeter = document.getElementById('remoteAudioTxMeter');
  const micSelect = document.getElementById('remoteAudioMicSelect');
  const codecSelect = document.getElementById('remoteAudioCodecSelect');
  const codecHint = document.getElementById('remoteAudioCodecHint');
  const rxGainSlider = document.getElementById('remoteAudioRxGain');
  const txGainSlider = document.getElementById('remoteAudioTxGain');
  const rxGainVal = document.getElementById('remoteAudioRxGainVal');
  const txGainVal = document.getElementById('remoteAudioTxGainVal');
  const levelsBtn = document.getElementById('remoteAudioLevelsBtn');
  const levelsDialog = document.getElementById('remoteAudioLevelsDialog');

  const channel = openChannel();
  let session = null;
  let remoteOwned = role === 'index' && isPopoutOwned();
  let latestSpectrum = null;
  let spectrumTimer = null;
  let heartbeatTimer = null;
  let staleTimer = null;
  let lastHeartbeat = remoteOwned ? Date.now() : 0;
  let streamStatus = 'idle';
  let streamCodec = null;
  let gainSaveTimer = null;
  let applyingRemoteGain = false;
  let applyingRemoteCodec = false;

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
    if (role === 'index' && remoteOwned && streamStatus === 'streaming') {
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

  function setPopoutHints(reopen) {
    setTipText(popoutTip, reopen ? TIP_POPOUT_REOPEN : TIP_POPOUT);
    if (popoutBtn) {
      popoutBtn.setAttribute('aria-label', reopen ? TIP_POPOUT_REOPEN : TIP_POPOUT);
    }
  }

  function updateLocalButtons() {
    const streaming = streamStatus === 'streaming';
    if (micMute) micMute.disabled = !streaming;
    if (rxMute) rxMute.disabled = !streaming;
    // Index locks the mic picker only while the popout is actively streaming.
    if (micSelect) micSelect.disabled = role === 'index' && remoteOwned && streaming;
    if (codecSelect) codecSelect.disabled = streaming;
    if (!streaming) {
      setMicMuteUi(micMute, micMuteIcon, micMuteTip, false);
      setRxMuteUi(rxMute, rxMuteIcon, rxMuteTip, false);
    }
    if (role === 'popout') {
      if (startBtn) startBtn.disabled = streaming;
      if (stopBtn) stopBtn.disabled = !streaming;
      return;
    }
    if (remoteOwned && streaming) {
      if (startBtn) startBtn.disabled = true;
      if (stopBtn) stopBtn.disabled = false;
      setPopoutHints(true);
    } else {
      // Popout stopped (or never started): unlock Start on the main bar.
      if (startBtn) startBtn.disabled = streaming;
      if (stopBtn) stopBtn.disabled = !streaming;
      setPopoutHints(!!(remoteOwned || isPopoutOwned()));
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
        if (role === 'popout') {
          setStatusText(s === 'streaming'
            ? `Streaming (${codec})`
            : s.charAt(0).toUpperCase() + s.slice(1));
          post({ type: 'status', status: s, codec: codec || null });
          if (s === 'streaming') startSpectrumRelay();
          else {
            stopSpectrumRelay();
            post({ type: 'spectrum', data: null });
          }
        } else if (!remoteOwned) {
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
        if (role === 'popout') post({ type: 'levels', rx, tx });
      },
      onError: (msg) => {
        setStatusText(msg);
        if (role === 'popout') post({ type: 'error', message: msg });
        else if (!remoteOwned) alert(msg);
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
    if (role === 'popout') {
      // Unlock the Index bar — ownership only applies while streaming.
      post({ type: 'status', status: 'stopped' });
      post({ type: 'spectrum', data: null });
      setStatusText('Stopped');
    } else if (!remoteOwned) {
      clearFilterScopeSpectrum();
      setStatusText('Stopped');
    }
    updateLocalButtons();
  }

  function claimPopoutOwnership() {
    setPopoutOwned(true);
    post({ type: 'ownership', owned: true });
    post({ type: 'hello', role: 'popout' });
  }

  function releasePopoutOwnership() {
    setPopoutOwned(false);
    stopSpectrumRelay();
    if (heartbeatTimer) {
      clearInterval(heartbeatTimer);
      heartbeatTimer = null;
    }
    post({ type: 'ownership', owned: false });
    post({ type: 'popoutClosed' });
    post({ type: 'spectrum', data: null });
  }

  if (channel) {
    channel.onmessage = (ev) => {
      const msg = ev.data;
      if (!msg || typeof msg !== 'object') return;

      if (role === 'index') {
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
          // Don't lock Start until the popout is actually streaming.
          setStatusText('In pop-out window');
          setPopoutHints(true);
          post({ type: 'requestStatus' });
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
          setPopoutHints(true);
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
        return;
      }

      // popout role
      if (msg.type === 'setMute') {
        if (typeof msg.mic === 'boolean') {
          setMicMuteUi(micMute, micMuteIcon, micMuteTip, msg.mic);
          session?.setMicMuted(msg.mic);
        }
        if (typeof msg.rx === 'boolean') {
          setRxMuteUi(rxMute, rxMuteIcon, rxMuteTip, msg.rx);
          session?.setRxMuted(msg.rx);
        }
        return;
      }
      if (msg.type === 'setGain') {
        setGainUi(msg.rx, msg.tx);
        session?.setGain({ rx: msg.rx, tx: msg.tx });
        return;
      }
      if (msg.type === 'gain') {
        setGainUi(msg.rx, msg.tx);
        return;
      }
      if (msg.type === 'codecPref') {
        if (!codecSelect || streamStatus === 'streaming') return;
        applyingRemoteCodec = true;
        try {
          const next = msg.codec === 'pcm16' ? 'pcm16' : 'opus';
          if (next === 'opus' && !supportsWebCodecsOpus()) return;
          codecSelect.value = next;
          saveCodec(next);
        } finally {
          applyingRemoteCodec = false;
        }
        return;
      }
      if (msg.type === 'stop') {
        stopLocal();
        return;
      }
      if (msg.type === 'requestStatus') {
        post({ type: 'status', status: streamStatus, codec: streamCodec });
        post({
          type: 'muteState',
          mic: isTogglePressed(micMute),
          rx: isTogglePressed(rxMute)
        });
        const g = readGainUi();
        post({ type: 'gain', rx: g.rx, tx: g.tx });
        post({ type: 'codecPref', codec: selectedCodec() });
      }
    };
  }

  startBtn?.addEventListener('click', async () => {
    if (role === 'index' && remoteOwned && streamStatus === 'streaming') return;
    await startLocal();
  });

  stopBtn?.addEventListener('click', async () => {
    if (role === 'index' && remoteOwned && streamStatus === 'streaming') {
      post({ type: 'stop' });
      return;
    }
    await stopLocal();
  });

  micMute?.addEventListener('click', () => {
    const next = !isTogglePressed(micMute);
    setMicMuteUi(micMute, micMuteIcon, micMuteTip, next);
    if (role === 'index' && remoteOwned && streamStatus === 'streaming') {
      post({ type: 'setMute', mic: next });
      return;
    }
    session?.setMicMuted(next);
  });

  rxMute?.addEventListener('click', () => {
    const next = !isTogglePressed(rxMute);
    setRxMuteUi(rxMute, rxMuteIcon, rxMuteTip, next);
    if (role === 'index' && remoteOwned && streamStatus === 'streaming') {
      post({ type: 'setMute', rx: next });
      return;
    }
    session?.setRxMuted(next);
  });

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

  levelsBtn?.addEventListener('click', () => {
    if (!levelsDialog) return;
    if (typeof levelsDialog.show === 'function') levelsDialog.show();
    else levelsDialog.setAttribute('open', '');
  });

  popoutBtn?.addEventListener('click', async () => {
    const wasStreaming = !!session?.running;
    if (wasStreaming) {
      await stopLocal();
      await new Promise((r) => setTimeout(r, 150));
    }
    const win = openPopoutWindow(!!wasStreaming);
    if (!win) {
      setStatusText('Pop-out blocked — allow pop-ups for this site');
      return;
    }
    lastHeartbeat = Date.now();
    setStatusText(wasStreaming ? 'Handing off to pop-out…' : 'In pop-out window');
    setPopoutHints(true);
    // Lock only once the popout reports streaming (or after autostart handoff).
    if (wasStreaming) {
      remoteOwned = true;
      setPopoutOwned(true);
      streamStatus = 'streaming';
      attachRemoteSpectrum();
    }
    updateLocalButtons();
  });

  closeBtn?.addEventListener('click', async () => {
    await stopLocal();
    releasePopoutOwnership();
    window.close();
  });

  if (role === 'popout') {
    claimPopoutOwnership();
    heartbeatTimer = setInterval(() => post({
      type: 'heartbeat',
      status: streamStatus,
      codec: streamCodec
    }), HEARTBEAT_MS);
    window.addEventListener('beforeunload', () => {
      try { session?.stop(); } catch { /* ignore */ }
      releasePopoutOwnership();
    });

    const params = new URLSearchParams(location.search);
    if (params.get('autostart') === '1') {
      // Start in the same turn as page script when possible (inherits
      // transient activation from the Index click that opened us).
      startLocal();
    }
  } else {
    if (remoteOwned) {
      setStatusText('In pop-out window');
      setPopoutHints(true);
      post({ type: 'requestStatus' });
    }
    staleTimer = setInterval(() => {
      if (!remoteOwned) return;
      if (Date.now() - lastHeartbeat > HEARTBEAT_STALE_MS) {
        clearRemoteOwnershipUi();
      }
    }, HEARTBEAT_MS);
  }

  if (stopBtn) stopBtn.disabled = true;
  setMicMuteUi(micMute, micMuteIcon, micMuteTip, false);
  setRxMuteUi(rxMute, rxMuteIcon, rxMuteTip, false);
  initCodecSelect();
  updateLocalButtons();
  ensureRemoteAudioTooltips();
  refreshMicList();
  try {
    navigator.mediaDevices?.addEventListener?.('devicechange', () => refreshMicList());
  } catch { /* ignore */ }
}

/**
 * Index-page Remote Audio controls. Shown only when Settings has audio enabled.
 */
export async function initRemoteAudioUi() {
  const bar = document.getElementById('remoteAudioBar');
  if (!bar) return;

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
    bar.style.display = 'none';
    return;
  }

  bar.style.display = '';
  bindRemoteAudioControls('index');
  const rx = document.getElementById('remoteAudioRxGain');
  const tx = document.getElementById('remoteAudioTxGain');
  const rxV = document.getElementById('remoteAudioRxGainVal');
  const txV = document.getElementById('remoteAudioTxGainVal');
  if (rx) rx.value = String(rxGain);
  if (tx) tx.value = String(txGain);
  if (rxV) rxV.textContent = Number(rxGain).toFixed(2);
  if (txV) txV.textContent = Number(txGain).toFixed(2);
}

/**
 * Pop-out window entry point (/RemoteAudio).
 */
export async function initRemoteAudioPopout() {
  const bar = document.getElementById('remoteAudioBar');
  if (!bar) return;

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
    const statusEl = document.getElementById('remoteAudioStatus');
    if (statusEl) statusEl.textContent = 'Remote audio is disabled in Settings';
    const startBtn = document.getElementById('remoteAudioStartBtn');
    if (startBtn) startBtn.disabled = true;
    return;
  }

  bindRemoteAudioControls('popout');
  const rx = document.getElementById('remoteAudioRxGain');
  const tx = document.getElementById('remoteAudioTxGain');
  const rxV = document.getElementById('remoteAudioRxGainVal');
  const txV = document.getElementById('remoteAudioTxGainVal');
  if (rx) rx.value = String(rxGain);
  if (tx) tx.value = String(txGain);
  if (rxV) rxV.textContent = Number(rxGain).toFixed(2);
  if (txV) txV.textContent = Number(txGain).toFixed(2);
}
