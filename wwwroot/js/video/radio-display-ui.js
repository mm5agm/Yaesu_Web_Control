/**
 * Radio Display UI wiring: status poll + MJPEG img stream + controls.
 */
import { RadioDisplayPanel } from './radio-display-panel.js?v=4';

const STATUS_POLL_MS = 4000;
const RECONNECT_MS = 2500;
const RECONNECT_MAX_MS = 15000;
const ALLOWED_FPS = [15, 30, 40, 60];
const CHANNEL_NAME = 'ywc-radio-display';

let panel = null;
let uiMode = 'index';
let statusTimer = null;
let reconnectTimer = null;
let reconnectAttempt = 0;
let lastServerStatus = '';
let lastFrameSeq = 0;
let blankPolls = 0;
let enabled = false;
let streamActive = false;
let currentDeviceKey = '';
let currentTargetFps = 15;
let controlsBound = false;
let channel = null;

function getChannel() {
  if (channel) return channel;
  if (typeof BroadcastChannel === 'undefined') return null;
  channel = new BroadcastChannel(CHANNEL_NAME);
  return channel;
}

function postChannel(msg) {
  try { getChannel()?.postMessage(msg); } catch { /* ignore */ }
}

function normalizeFps(fps) {
  const n = Number(fps);
  return ALLOWED_FPS.includes(n) ? n : 15;
}

function syncFpsSelect(fps) {
  const sel = document.getElementById('radioDisplayFpsSelect');
  if (!sel) return;
  const want = String(normalizeFps(fps));
  if (sel.value !== want && document.activeElement !== sel) {
    sel.value = want;
  }
}

function streamUrl() {
  return '/api/video/stream?t=' + Date.now();
}

function reconnectDelayMs() {
  const ms = Math.min(RECONNECT_MAX_MS, RECONNECT_MS * Math.pow(1.5, reconnectAttempt));
  reconnectAttempt += 1;
  return ms;
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  const delay = reconnectDelayMs();
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    if (!enabled || !panel || panel.isHiddenByUser()) return;
    if (!currentDeviceKey) return;
    startStream();
  }, delay);
}

function onStreamInterrupted() {
  if (!streamActive) return;
  streamActive = false;
  scheduleReconnect();
}

function bindStreamImage() {
  const img = document.getElementById('radioDisplayImg');
  if (!img) return;
  img.onerror = onStreamInterrupted;
  img.onstalled = onStreamInterrupted;
  img.onabort = onStreamInterrupted;
  img.onload = () => {
    reconnectAttempt = 0;
    blankPolls = 0;
  };
}

function notifyPopoutReady() {
  postChannel({ type: 'popout-ready' });
  try {
    if (window.opener && !window.opener.closed) {
      window.opener.postMessage({ type: 'ywc-radio-display-popout-ready' }, window.location.origin);
    }
  } catch { /* ignore cross-origin */ }
}

function startStream() {
  if (!panel || !enabled) return;
  if (panel.isHiddenByUser()) return;
  if (!currentDeviceKey) {
    stopStream();
    panel.setStatus('idle', 'select a device');
    return;
  }
  streamActive = true;
  const img = document.getElementById('radioDisplayImg');
  if (img) {
    img.onerror = null;
    img.onstalled = null;
    img.onabort = null;
    img.onload = null;
  }
  panel.setStreamUrl(streamUrl());
  if (uiMode === 'popout') notifyPopoutReady();
  bindStreamImage();
}

function stopStream() {
  streamActive = false;
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  panel?.clearStream();
}

function reattachToIndex() {
  // Ask Home to attach first so the host never drops the last viewer
  // (USB HDMI dongles native-crash if the capture graph is torn down
  // and immediately reopened).
  postChannel({ type: 'reattach' });
  try {
    if (window.opener && !window.opener.closed) {
      window.opener.postMessage({ type: 'ywc-radio-display-reattach' }, window.location.origin);
    }
  } catch { /* ignore cross-origin */ }
  setTimeout(() => {
    stopStream();
    window.close();
  }, 250);
}

function onReattachFromPopout() {
  if (uiMode !== 'index' || !panel) return;
  panel.show();
  if (enabled && currentDeviceKey) {
    startStream();
  } else {
    pollStatus();
  }
}

function deviceOptionMatches(d, want) {
  if (!want) return false;
  const key = d.key || '';
  if (key && key === want) return true;
  if (d.index == null) return false;
  return want === ('index:' + d.index) || want === String(d.index);
}

function selectMatchesSavedKey(sel, want) {
  if (!sel) return true;
  if ((sel.value || '') === (want || '')) return true;
  if (!want) return false;
  const opt = sel.selectedOptions && sel.selectedOptions[0];
  if (!opt) return false;
  const idx = opt.dataset && opt.dataset.index;
  return idx != null && (want === ('index:' + idx) || want === String(idx));
}

function truncateLabel(text, maxLen) {
  const s = String(text || '');
  if (s.length <= maxLen) return s;
  return s.slice(0, Math.max(0, maxLen - 1)) + '…';
}

async function loadDeviceSelect(selectedKey) {
  const sel = document.getElementById('radioDisplayDeviceSelect');
  if (!sel) return;
  try {
    const res = await fetch('/api/video/devices');
    if (!res.ok) throw new Error('HTTP ' + res.status);
    const data = await res.json();
    const want = selectedKey || currentDeviceKey || '';
    const maxLabel = uiMode === 'popout' ? 28 : 42;
    sel.innerHTML = '';
    const none = document.createElement('option');
    none.value = '';
    none.textContent = '(None)';
    sel.appendChild(none);
    let matched = false;
    (data.devices || []).forEach(function (d) {
      const key = d.key || '';
      const full = d.label || key;
      const opt = document.createElement('option');
      opt.value = key;
      if (d.index != null) opt.dataset.index = String(d.index);
      opt.textContent = truncateLabel(full, maxLabel);
      opt.title = full;
      if (deviceOptionMatches(d, want)) {
        opt.selected = true;
        matched = true;
      }
      sel.appendChild(opt);
    });
    if (want && !matched) {
      const orphan = document.createElement('option');
      orphan.value = want;
      orphan.textContent = truncateLabel(want + ' (not present)', maxLabel);
      orphan.title = want + ' (not present)';
      orphan.selected = true;
      sel.appendChild(orphan);
    }
    if (!want) none.selected = true;
    if ((data.devices || []).length === 0 && data.notes && panel) {
      panel.setStatus('idle', String(data.notes));
    }
  } catch (e) {
    console.warn('Radio Display device list failed', e);
  }
}

async function setDeviceKey(key) {
  try {
    stopStream();
    const res = await fetch('/api/video/device', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ key: key || '' })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data.error || ('HTTP ' + res.status));
    currentDeviceKey = data.deviceKey || '';
    if (currentDeviceKey && !panel.isHiddenByUser()) {
      setTimeout(() => startStream(), 600);
    } else {
      panel.setStatus(currentDeviceKey ? 'connecting' : 'idle',
        currentDeviceKey ? undefined : 'select a device');
    }
  } catch (e) {
    console.warn('Radio Display device change failed', e);
    alert('Could not change capture device: ' + (e.message || e));
    await loadDeviceSelect(currentDeviceKey);
  }
}

async function setTargetFps(fps) {
  const want = normalizeFps(fps);
  try {
    const res = await fetch('/api/video/fps', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ fps: want })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data.error || ('HTTP ' + res.status));
    currentTargetFps = normalizeFps(data.targetFps ?? want);
    syncFpsSelect(currentTargetFps);
  } catch (e) {
    console.warn('Radio Display FPS change failed', e);
    alert('Could not change frame rate: ' + (e.message || e));
    syncFpsSelect(currentTargetFps);
  }
}

/**
 * @param {{ attachStream?: boolean }} [options]
 */
async function pollStatus(options = {}) {
  const attachStream = options.attachStream !== false;
  try {
    const res = await fetch('/api/video/status');
    if (!res.ok) return;
    const data = await res.json();
    enabled = !!data.enabled;
    currentDeviceKey = data.deviceKey || '';
    currentTargetFps = normalizeFps(data.targetFps);

    if (!panel) return;

    if (!data.enabled) {
      panel.setStatus('unconfigured');
      lastServerStatus = 'unconfigured';
      stopStream();
      return;
    }

    const sel = document.getElementById('radioDisplayDeviceSelect');
    if (sel && document.activeElement !== sel && !selectMatchesSavedKey(sel, currentDeviceKey)) {
      await loadDeviceSelect(currentDeviceKey);
    }
    syncFpsSelect(currentTargetFps);

    if (!currentDeviceKey) {
      panel.setStatus('idle', 'select a device');
      lastServerStatus = 'idle';
      stopStream();
      return;
    }

    const detail = data.width && data.height
      ? `${data.width}×${data.height}` + (data.fps ? ` @ ${data.fps}fps` : '')
      : (data.error || undefined);

    let status = data.status || 'idle';
    if (status === 'idle' && streamActive) status = 'connecting';
    panel.setStatus(status, detail);

    const seq = Number(data.frameSeq) || 0;
    const recovered = status === 'streaming' && lastServerStatus !== 'streaming';
    const seqReset = status === 'streaming' && seq > 0 && lastFrameSeq > 0 && seq < lastFrameSeq;
    lastFrameSeq = seq;

    const img = document.getElementById('radioDisplayImg');
    const looksBlank = streamActive && status === 'streaming' && img && !img.naturalWidth;
    if (looksBlank) blankPolls += 1;
    else blankPolls = 0;

    lastServerStatus = status;

    if (!attachStream || panel.isHiddenByUser()) return;

    if (recovered || seqReset || blankPolls >= 2) {
      blankPolls = 0;
      startStream();
      return;
    }

    if (!streamActive) {
      startStream();
    }
  } catch (e) {
    console.warn('Radio Display status poll failed', e);
  }
}

function initRadioDisplayTooltips() {
  if (typeof bootstrap === 'undefined') return false;
  document.querySelectorAll('.radio-display-tip[data-bs-toggle="tooltip"]').forEach(el => {
    bootstrap.Tooltip.getOrCreateInstance(el, {
      delay: { show: 200, hide: 50 },
      trigger: 'hover focus',
      placement: 'top'
    });
  });
  return true;
}

function ensureRadioDisplayTooltips() {
  if (initRadioDisplayTooltips()) return;
  window.addEventListener('load', () => initRadioDisplayTooltips(), { once: true });
}

function onPopoutReady() {
  if (uiMode !== 'index') return;
  stopStream();
  panel?.hide();
}

function bindChannel() {
  const ch = getChannel();
  if (ch) {
    ch.onmessage = (ev) => {
      const msg = ev?.data;
      if (!msg || typeof msg !== 'object') return;
      if (msg.type === 'reattach') onReattachFromPopout();
      if (msg.type === 'popout-ready') onPopoutReady();
    };
  }

  window.addEventListener('message', (ev) => {
    if (ev.origin !== window.location.origin) return;
    if (ev.data?.type === 'ywc-radio-display-reattach') onReattachFromPopout();
    if (ev.data?.type === 'ywc-radio-display-popout-ready') onPopoutReady();
  });
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

  document.getElementById('radioDisplayCloseBtn')?.addEventListener('click', () => {
    stopStream();
    if (uiMode === 'popout') {
      window.close();
      return;
    }
    panel.hide();
  });

  document.getElementById('radioDisplayShowBtn')?.addEventListener('click', () => {
    panel.show();
    if (enabled) startStream();
  });

  document.getElementById('radioDisplayPopoutBtn')?.addEventListener('click', () => {
    const w = window.open('/RadioDisplay', 'ywc-radio-display', 'width=900,height=600');
    if (w) w.focus();
    // Hide the Index card immediately but keep the MJPEG viewer attached
    // until the pop-out acquires, so the USB device is never released.
    panel.hide();
    setTimeout(() => {
      if (uiMode === 'index' && panel?.isHiddenByUser()) stopStream();
    }, 4000);
  });

  document.getElementById('radioDisplayReattachBtn')?.addEventListener('click', () => {
    reattachToIndex();
  });

  if (!controlsBound) {
    controlsBound = true;
    document.getElementById('radioDisplayDeviceSelect')?.addEventListener('change', (ev) => {
      setDeviceKey(ev.target.value || '');
    });
    document.getElementById('radioDisplayRefreshDevicesBtn')?.addEventListener('click', () => {
      loadDeviceSelect(currentDeviceKey);
    });
    document.getElementById('radioDisplayFpsSelect')?.addEventListener('change', (ev) => {
      setTargetFps(ev.target.value);
    });
  }

  const fitBtn = document.getElementById('radioDisplayFitBtn');
  if (fitBtn && panel) {
    fitBtn.textContent = panel.getFitMode() === 'contain' ? 'Fit' : 'Fill';
  }

  ensureRadioDisplayTooltips();
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
  bindChannel();
  bindControls();

  // Probe the device list before attaching MJPEG so enumeration never
  // Open()s the dongle while the capture loop already holds it.
  await pollStatus({ attachStream: false });
  await loadDeviceSelect(currentDeviceKey);
  if (enabled && currentDeviceKey && !panel.isHiddenByUser()) startStream();
  if (statusTimer) clearInterval(statusTimer);
  statusTimer = setInterval(pollStatus, STATUS_POLL_MS);

  window.addEventListener('beforeunload', () => stopStream());
}
