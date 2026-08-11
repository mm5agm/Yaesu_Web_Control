/**
 * Radio Display UI wiring: status poll + MJPEG img stream + controls.
 */
import { RadioDisplayPanel } from './radio-display-panel.js?v=3';

const STATUS_POLL_MS = 4000;
const RECONNECT_MS = 2500;

let panel = null;
let uiMode = 'index';
let statusTimer = null;
let reconnectTimer = null;
let enabled = false;
let streamActive = false;

function streamUrl() {
  return '/api/video/stream?t=' + Date.now();
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    if (!enabled || !panel || panel.isHiddenByUser()) return;
    startStream();
  }, RECONNECT_MS);
}

function startStream() {
  if (!panel || !enabled) return;
  if (panel.isHiddenByUser()) return;
  streamActive = true;
  panel.setStreamUrl(streamUrl());
  const img = document.getElementById('radioDisplayImg');
  if (img) {
    img.onerror = () => {
      streamActive = false;
      panel.setStatus('disconnected', 'stream error');
      scheduleReconnect();
    };
    img.onload = () => {
      // First JPEG arrived — treat as streaming until status poll says otherwise.
      if (streamActive) panel.setStatus('streaming');
    };
  }
}

function stopStream() {
  streamActive = false;
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  panel?.clearStream();
}

async function pollStatus() {
  try {
    const res = await fetch('/api/video/status');
    if (!res.ok) return;
    const data = await res.json();
    enabled = !!data.enabled && !!(data.deviceKey);

    if (!panel) return;

    if (!data.enabled) {
      panel.setStatus('unconfigured');
      stopStream();
      return;
    }

    if (!data.deviceKey) {
      panel.setStatus('unconfigured');
      stopStream();
      return;
    }

    const detail = data.width && data.height
      ? `${data.width}×${data.height}` + (data.fps ? ` @ ${data.fps}fps` : '')
      : (data.error || undefined);

    let status = data.status || 'idle';
    if (status === 'idle' && streamActive) status = 'connecting';
    panel.setStatus(status, detail);

    if (!panel.isHiddenByUser() && !streamActive) {
      startStream();
    }
  } catch (e) {
    console.warn('Radio Display status poll failed', e);
  }
}

function bindControls() {
  document.getElementById('radioDisplayFitBtn')?.addEventListener('click', () => {
    const mode = panel.toggleFitMode();
    const btn = document.getElementById('radioDisplayFitBtn');
    if (btn) btn.textContent = mode === 'contain' ? 'Fit' : 'Fill';
  });

  document.getElementById('radioDisplayFullscreenBtn')?.addEventListener('click', () => {
    panel.requestFullscreen().catch(() => {});
  });

  document.getElementById('radioDisplayHideBtn')?.addEventListener('click', () => {
    // Index-only: drop this page's MJPEG viewer. Pop-out keeps its own connection.
    stopStream();
    panel.hide();
  });

  document.getElementById('radioDisplayShowBtn')?.addEventListener('click', () => {
    panel.show();
    if (enabled) startStream();
  });

  document.getElementById('radioDisplayPopoutBtn')?.addEventListener('click', () => {
    const w = window.open('/RadioDisplay', 'ywc-radio-display', 'width=900,height=600');
    if (w) w.focus();
    // Hand off to the pop-out: drop the Index MJPEG viewer so only one
    // browser connection stays open (better for Pi-class hosts).
    stopStream();
    panel.hide();
  });

  const fitBtn = document.getElementById('radioDisplayFitBtn');
  if (fitBtn && panel) {
    fitBtn.textContent = panel.getFitMode() === 'contain' ? 'Fit' : 'Fill';
  }
}

/**
 * Index / pop-out entry point.
 * @param {'index'|'popout'} [mode]
 */
export async function initRadioDisplayUi(mode = 'index') {
  const container = document.getElementById('radioDisplayContainer');
  if (!container) return;

  uiMode = mode === 'popout' ? 'popout' : 'index';
  panel = new RadioDisplayPanel(
    'radioDisplayImg',
    'radioDisplayContainer',
    'radioDisplayBadge',
    {
      ignoreHidePreference: uiMode === 'popout',
      naturalSize: uiMode === 'index'
    }
  );
  bindControls();

  await pollStatus();
  if (statusTimer) clearInterval(statusTimer);
  statusTimer = setInterval(pollStatus, STATUS_POLL_MS);

  // When the page unloads, drop the MJPEG connection so capture can stop
  // once the last viewer (Index or pop-out) is gone.
  window.addEventListener('beforeunload', () => stopStream());
}
