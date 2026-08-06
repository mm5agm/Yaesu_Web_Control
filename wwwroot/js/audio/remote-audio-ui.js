import { createAudioSession } from './audio-session.js';

function attachFilterScopeSpectrum(session) {
  const provider = () => session.getSpectrum();
  window.filterScopePanelA?.setSpectrumProvider?.(provider);
  window.filterScopePanelB?.setSpectrumProvider?.(provider);
}

function clearFilterScopeSpectrum() {
  window.filterScopePanelA?.setSpectrumProvider?.(null);
  window.filterScopePanelB?.setSpectrumProvider?.(null);
}

/**
 * Index-page Remote Audio controls. Shown only when Settings has audio enabled.
 */
export async function initRemoteAudioUi() {
  const bar = document.getElementById('remoteAudioBar');
  if (!bar) return;

  let enabled = false;
  try {
    const res = await fetch('/api/audio/status');
    const data = await res.json();
    enabled = !!data.enabled;
  } catch {
    enabled = false;
  }

  if (!enabled) {
    bar.style.display = 'none';
    return;
  }

  bar.style.display = '';
  const startBtn = document.getElementById('remoteAudioStartBtn');
  const stopBtn = document.getElementById('remoteAudioStopBtn');
  const micMute = document.getElementById('remoteAudioMicMute');
  const rxMute = document.getElementById('remoteAudioRxMute');
  const statusEl = document.getElementById('remoteAudioStatus');
  const rxMeter = document.getElementById('remoteAudioRxMeter');
  const txMeter = document.getElementById('remoteAudioTxMeter');

  const session = createAudioSession({
    onStatus: (s, codec) => {
      if (statusEl) {
        statusEl.textContent = s === 'streaming'
          ? `Streaming (${codec})`
          : s.charAt(0).toUpperCase() + s.slice(1);
      }
      if (startBtn) startBtn.disabled = s === 'streaming';
      if (stopBtn) stopBtn.disabled = s !== 'streaming';
      if (s === 'streaming') attachFilterScopeSpectrum(session);
      else clearFilterScopeSpectrum();
    },
    onLevels: (rx, tx) => {
      if (rxMeter) rxMeter.style.width = `${Math.min(100, Math.round(rx * 100))}%`;
      if (txMeter) txMeter.style.width = `${Math.min(100, Math.round(tx * 100))}%`;
    },
    onError: (msg) => {
      if (statusEl) statusEl.textContent = msg;
      alert(msg);
    }
  });

  startBtn?.addEventListener('click', async () => {
    try {
      statusEl.textContent = 'Connecting…';
      await session.start();
    } catch (e) {
      statusEl.textContent = e.message || String(e);
      alert(e.message || String(e));
    }
  });

  stopBtn?.addEventListener('click', async () => {
    await session.stop();
  });

  micMute?.addEventListener('change', () => {
    session.setMicMuted(micMute.checked);
  });
  rxMute?.addEventListener('change', () => {
    session.setRxMuted(rxMute.checked);
  });

  if (stopBtn) stopBtn.disabled = true;
}
