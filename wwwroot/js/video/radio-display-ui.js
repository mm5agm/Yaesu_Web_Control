/**
 * Radio Display UI wiring: status poll + MJPEG img stream + controls.
 */
import { RadioDisplayPanel } from './radio-display-panel.js?v=8';

const STATUS_POLL_MS = 4000;
const RECONNECT_MS = 2500;
const RECONNECT_MAX_MS = 15000;
const ALLOWED_FPS = [15, 30, 60];
const ALLOWED_QUALITY = [40, 65, 85];
const CHANNEL_NAME = 'ywc-radio-display';
const AUTO_START_KEY = 'ywc.radioDisplayAutoStart';

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
/** Operator asked for a live stream (Start, Auto, or pop-out handoff). */
let wantStream = false;
/** Host reported disconnect; do not reopen index:N until Start / device change. */
let holdDisconnected = false;
let holdDisconnectedDetail = '';
/** Start is in flight; ignore a leftover disconnected status from the previous halt. */
let awaitingCapture = false;
let currentDeviceKey = '';
let currentTargetFps = 15;
let currentJpegQuality = 85;
let deviceRates = [];
let currentCaptureSize = '';
let deviceSizes = [];
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

function isAutoStart() {
  return localStorage.getItem(AUTO_START_KEY) === '1';
}

function setAutoStart(on) {
  localStorage.setItem(AUTO_START_KEY, on ? '1' : '0');
}

function parseRates(raw) {
  if (Array.isArray(raw)) {
    return raw.map(Number).filter((n) => Number.isFinite(n) && n > 0);
  }
  if (typeof raw === 'string' && raw.trim()) {
    return raw.split(',').map(Number).filter((n) => Number.isFinite(n) && n > 0);
  }
  return [];
}

function fpsChoices(rates) {
  const parsed = parseRates(rates);
  const cap = parsed.length ? Math.max.apply(null, parsed) : 0;
  const list = cap > 0 ? ALLOWED_FPS.filter((f) => f <= cap) : ALLOWED_FPS.slice();
  return list.length ? list : [ALLOWED_FPS[0]];
}

function normalizeFps(fps, rates = deviceRates) {
  const n = Number(fps);
  const allowed = fpsChoices(rates);
  if (allowed.includes(n)) return n;
  return allowed.reduce((best, a) =>
    Math.abs(a - n) < Math.abs(best - n) ? a : best);
}

function applyDeviceFpsCap(rates, selected) {
  const sel = document.getElementById('radioDisplayFpsSelect');
  const parsed = parseRates(rates);
  if (parsed.length) deviceRates = parsed;
  const allowed = fpsChoices(deviceRates);
  const want = normalizeFps(selected ?? currentTargetFps, deviceRates);
  if (sel && document.activeElement !== sel) {
    const same =
      sel.options.length === allowed.length &&
      allowed.every((f, i) => sel.options[i] && sel.options[i].value === String(f));
    if (!same) {
      sel.innerHTML = '';
      allowed.forEach((f) => {
        const opt = document.createElement('option');
        opt.value = String(f);
        opt.textContent = f + ' fps';
        sel.appendChild(opt);
      });
    }
    if (sel.value !== String(want)) sel.value = String(want);
  }
  currentTargetFps = want;
  return want;
}

function syncFpsSelect(fps) {
  applyDeviceFpsCap(deviceRates, fps);
}

/**
 * Rebuild the capture-size list. Hidden entirely when the host reports no
 * sizes — macOS and the OpenCV fallback cannot enumerate pins, and an
 * Auto-only dropdown is just a control that does nothing.
 */
function applyDeviceSizes(sizes, selected) {
  const wrap = document.getElementById('radioDisplaySizeWrap');
  const sel = document.getElementById('radioDisplaySizeSelect');
  if (Array.isArray(sizes)) deviceSizes = sizes.slice();
  const want = deviceSizes.includes(selected ?? currentCaptureSize)
    ? (selected ?? currentCaptureSize)
    : '';
  currentCaptureSize = want;
  if (wrap) wrap.hidden = deviceSizes.length === 0;
  if (!sel) return;

  const signature = deviceSizes.join(',');
  if (sel.dataset.sizes !== signature) {
    sel.dataset.sizes = signature;
    sel.replaceChildren();
    const auto = document.createElement('option');
    auto.value = '';
    auto.textContent = 'Auto size';
    sel.appendChild(auto);
    for (const s of deviceSizes) {
      const opt = document.createElement('option');
      opt.value = s;
      opt.textContent = s.replace('x', '×');
      sel.appendChild(opt);
    }
  }
  if (sel.value !== want && document.activeElement !== sel) sel.value = want;
}

function syncSizeSelect(size) {
  applyDeviceSizes(deviceSizes, size);
}

function normalizeQuality(q) {
  const n = Number(q);
  if (!Number.isFinite(n)) return 85;
  if (ALLOWED_QUALITY.includes(n)) return n;
  return ALLOWED_QUALITY.reduce((best, a) =>
    Math.abs(a - n) < Math.abs(best - n) ? a : best);
}

function syncQualitySelect(quality) {
  const sel = document.getElementById('radioDisplayQualitySelect');
  if (!sel) return;
  const want = String(normalizeQuality(quality));
  if (sel.value !== want && document.activeElement !== sel) {
    sel.value = want;
  }
}

function syncAutoStartCheckbox() {
  const el = document.getElementById('radioDisplayAutoStart');
  if (el) el.checked = isAutoStart();
}

function syncStreamButton() {
  const btn = document.getElementById('radioDisplayStreamBtn');
  if (!btn) return;
  let icon = btn.querySelector('i');
  if (!icon) {
    icon = document.createElement('i');
    icon.setAttribute('aria-hidden', 'true');
    btn.replaceChildren(icon);
  }
  if (wantStream) {
    btn.className = 'btn btn-sm btn-outline-danger';
    icon.className = 'bi bi-stop-fill';
    btn.setAttribute('aria-label', 'Stop stream');
    btn.disabled = false;
  } else {
    btn.className = 'btn btn-sm btn-outline-success';
    icon.className = 'bi bi-play-fill';
    btn.setAttribute('aria-label', 'Start stream');
    btn.disabled = !enabled || !currentDeviceKey;
  }
}

function syncDisconnectedControls() {
  const held = holdDisconnected;
  const fpsSel = document.getElementById('radioDisplayFpsSelect');
  const qualSel = document.getElementById('radioDisplayQualitySelect');
  const sizeSel = document.getElementById('radioDisplaySizeSelect');
  const autoEl = document.getElementById('radioDisplayAutoStart');
  const devSel = document.getElementById('radioDisplayDeviceSelect');
  if (sizeSel) sizeSel.disabled = held;
  if (fpsSel) fpsSel.disabled = held;
  if (qualSel) qualSel.disabled = held;
  if (autoEl) autoEl.disabled = held;
  if (devSel) {
    devSel.title = held
      ? 'Device disconnected — refresh the list, confirm the intended camera, then Start. Select a different device to switch intentionally.'
      : '';
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

function clearDisconnectedHold() {
  holdDisconnected = false;
  holdDisconnectedDetail = '';
}

function onDeviceLost(detail) {
  awaitingCapture = false;
  holdDisconnected = true;
  holdDisconnectedDetail = detail || '';
  wantStream = false;
  stopStream();
  panel?.setStatus('disconnected', detail || undefined);
  syncStreamButton();
  syncDisconnectedControls();
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  const delay = reconnectDelayMs();
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    if (holdDisconnected) return;
    if (!enabled || !panel || panel.isHiddenByUser()) return;
    if (!wantStream || !currentDeviceKey) return;
    startStream();
  }, delay);
}

function onStreamInterrupted() {
  if (!streamActive) return;
  streamActive = false;
  panel?.hideFrame();
  if (holdDisconnected) return;
  scheduleReconnect();
  pollStatus({ attachStream: false });
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
    panel?.markFrameLoaded();
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
  if (!panel || !enabled || !wantStream) return;
  if (panel.isHiddenByUser()) return;
  if (!currentDeviceKey) {
    stopStream();
    panel.setStatus('idle', 'select a device');
    syncStreamButton();
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
  syncStreamButton();
}

function stopStream() {
  streamActive = false;
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  panel?.clearStream();
  syncStreamButton();
}

async function requestStart() {
  if (!enabled || !currentDeviceKey) return;
  try {
    const res = await fetch('/api/video/start', { method: 'POST' });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data.error || ('HTTP ' + res.status));
    clearDisconnectedHold();
    wantStream = true;
    awaitingCapture = true;
    startStream();
    syncStreamButton();
    syncDisconnectedControls();
  } catch (e) {
    console.warn('Radio Display start failed', e);
    alert('Could not start capture: ' + (e.message || e));
  }
}

function requestStop() {
  wantStream = false;
  stopStream();
  if (panel && enabled) {
    panel.setStatus('idle', currentDeviceKey ? 'stopped' : 'select a device');
  }
  syncStreamButton();
}

function reattachToIndex() {
  // Ask Home to attach first so the host never drops the last viewer
  // (USB HDMI dongles native-crash if the capture graph is torn down
  // and immediately reopened).
  postChannel({ type: 'reattach', stream: wantStream });
  try {
    if (window.opener && !window.opener.closed) {
      window.opener.postMessage(
        { type: 'ywc-radio-display-reattach', stream: wantStream },
        window.location.origin);
    }
  } catch { /* ignore cross-origin */ }
  setTimeout(() => {
    stopStream();
    window.close();
  }, 250);
}

function onReattachFromPopout(stream) {
  if (uiMode !== 'index' || !panel) return;
  panel.show();
  if (holdDisconnected) {
    wantStream = false;
    pollStatus();
  } else {
    wantStream = !!stream || isAutoStart();
    if (wantStream && enabled && currentDeviceKey) {
      requestStart();
    } else {
      pollStatus();
    }
  }
  syncStreamButton();
}

function deviceOptionMatches(d, want) {
  if (!want) return false;
  const key = d.key || '';
  if (key && key === want) return true;
  if (holdDisconnected) return false;
  if (d.index == null) return false;
  return want === ('index:' + d.index) || want === String(d.index);
}

function selectMatchesSavedKey(sel, want) {
  if (!sel) return true;
  if ((sel.value || '') === (want || '')) return true;
  if (holdDisconnected) return false;
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
      if (d.rates && d.rates.length) opt.dataset.rates = d.rates.join(',');
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
      const suffix = holdDisconnected ? ' (unavailable)' : ' (not present)';
      orphan.textContent = truncateLabel(want + suffix, maxLabel);
      orphan.title = want + suffix;
      orphan.selected = true;
      sel.appendChild(orphan);
    }
    if (!want) none.selected = true;
    const selected = sel.selectedOptions && sel.selectedOptions[0];
    const listedRates = selected && selected.dataset ? selected.dataset.rates : '';
    if (listedRates) applyDeviceFpsCap(listedRates, currentTargetFps);
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
    clearDisconnectedHold();
    currentDeviceKey = data.deviceKey || '';
    if (data.rates) applyDeviceFpsCap(data.rates, currentTargetFps);
    // Sizes belong to the old device. Blank the list; the next status poll
    // repopulates it from whatever the new one advertises.
    applyDeviceSizes([], '');
    syncStreamButton();
    syncDisconnectedControls();
    if (wantStream && currentDeviceKey && !panel.isHiddenByUser()) {
      setTimeout(() => requestStart(), 600);
    } else if (!currentDeviceKey) {
      panel.setStatus('idle', 'select a device');
    } else {
      panel.setStatus('idle');
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
    currentTargetFps = normalizeFps(data.targetFps ?? want, data.rates ?? deviceRates);
    applyDeviceFpsCap(data.rates ?? deviceRates, currentTargetFps);
  } catch (e) {
    console.warn('Radio Display FPS change failed', e);
    alert('Could not change frame rate: ' + (e.message || e));
    syncFpsSelect(currentTargetFps);
  }
}

/**
 * Persist the capture size. The pin is chosen when the device is opened, so
 * the host restarts the capture; a running stream drops for a second or two.
 */
async function setCaptureSize(size) {
  const want = deviceSizes.includes(size) ? size : '';
  try {
    const res = await fetch('/api/video/size', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ size: want })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data.error || ('HTTP ' + res.status));
    applyDeviceSizes(data.sizes ?? deviceSizes, data.captureSize ?? want);
  } catch (e) {
    console.warn('Radio Display capture size change failed', e);
    alert('Could not change capture size: ' + (e.message || e));
    syncSizeSelect(currentCaptureSize);
  }
}

async function setJpegQuality(quality) {
  const want = normalizeQuality(quality);
  try {
    const res = await fetch('/api/video/jpeg-quality', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ quality: want })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data.error || ('HTTP ' + res.status));
    currentJpegQuality = normalizeQuality(data.jpegQuality ?? want);
    syncQualitySelect(currentJpegQuality);
  } catch (e) {
    console.warn('Radio Display image quality change failed', e);
    alert('Could not change image quality: ' + (e.message || e));
    syncQualitySelect(currentJpegQuality);
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
    currentJpegQuality = normalizeQuality(data.jpegQuality);
    applyDeviceFpsCap(data.rates, data.targetFps);
    applyDeviceSizes(data.sizes, data.captureSize ?? '');

    if (!panel) return;

    if (!data.enabled) {
      clearDisconnectedHold();
      wantStream = false;
      panel.setStatus('unconfigured');
      lastServerStatus = 'unconfigured';
      stopStream();
      syncStreamButton();
      return;
    }

    const sel = document.getElementById('radioDisplayDeviceSelect');
    if (sel && document.activeElement !== sel && !selectMatchesSavedKey(sel, currentDeviceKey)) {
      await loadDeviceSelect(currentDeviceKey);
    }
    syncFpsSelect(currentTargetFps);
    syncQualitySelect(currentJpegQuality);
    syncSizeSelect(currentCaptureSize);
    syncStreamButton();

    if (!currentDeviceKey) {
      clearDisconnectedHold();
      panel.setStatus('idle', 'select a device');
      lastServerStatus = 'idle';
      stopStream();
      return;
    }

    const detail = data.width && data.height
      ? `${data.width}×${data.height}` + (data.fps ? ` @ ${data.fps}fps` : '')
      : (data.error || undefined);

    let status = data.status || 'idle';
    if (status === 'connecting' || status === 'streaming') {
      awaitingCapture = false;
      if (holdDisconnected && !data.halted && status !== 'disconnected') {
        clearDisconnectedHold();
        syncDisconnectedControls();
      }
    }

    const serverHalted = !!data.halted || status === 'disconnected';
    if (serverHalted) {
      holdDisconnected = true;
      if (data.error) holdDisconnectedDetail = data.error;
      if (awaitingCapture && wantStream) {
        panel.setStatus('connecting');
        lastServerStatus = 'connecting';
        return;
      }
      wantStream = false;
      stopStream();
      panel.setStatus('disconnected', holdDisconnectedDetail || data.error || undefined);
      lastServerStatus = 'disconnected';
      syncStreamButton();
      syncDisconnectedControls();
      if (attachStream) return;
    }

    if (holdDisconnected && !wantStream) {
      panel.setStatus('disconnected', holdDisconnectedDetail || data.error || undefined);
      lastServerStatus = 'disconnected';
      syncStreamButton();
      syncDisconnectedControls();
      if (attachStream) return;
    } else if (!wantStream) {
      status = 'idle';
      panel.setStatus('idle');
      lastServerStatus = 'idle';
      if (attachStream) return;
    } else if (status === 'idle' && streamActive) {
      status = 'connecting';
    }
    if (wantStream) panel.setStatus(status, detail);

    const seq = Number(data.frameSeq) || 0;
    const recovered = status === 'streaming' && lastServerStatus !== 'streaming';
    const seqReset = status === 'streaming' && seq > 0 && lastFrameSeq > 0 && seq < lastFrameSeq;
    lastFrameSeq = seq;

    const img = document.getElementById('radioDisplayImg');
    const looksBlank = streamActive && status === 'streaming' && img && !img.naturalWidth;
    if (looksBlank) blankPolls += 1;
    else blankPolls = 0;

    lastServerStatus = status;

    if (!attachStream || panel.isHiddenByUser() || !wantStream || holdDisconnected) return;

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
      if (msg.type === 'reattach') onReattachFromPopout(!!msg.stream);
      if (msg.type === 'popout-ready') onPopoutReady();
    };
  }

  window.addEventListener('message', (ev) => {
    if (ev.origin !== window.location.origin) return;
    if (ev.data?.type === 'ywc-radio-display-reattach') {
      onReattachFromPopout(!!ev.data.stream);
    }
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
    requestStop();
    if (uiMode === 'popout') {
      window.close();
      return;
    }
    panel.hide();
  });

  document.getElementById('radioDisplayShowBtn')?.addEventListener('click', () => {
    panel.show();
    if (isAutoStart()) requestStart();
    else syncStreamButton();
  });

  document.getElementById('radioDisplayPopoutBtn')?.addEventListener('click', () => {
    const qs = wantStream ? '?stream=1' : '';
    const w = window.open('/RadioDisplay' + qs, 'ywc-radio-display', 'width=900,height=600');
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
    document.getElementById('radioDisplayQualitySelect')?.addEventListener('change', (ev) => {
      setJpegQuality(ev.target.value);
    });
    document.getElementById('radioDisplaySizeSelect')?.addEventListener('change', (ev) => {
      setCaptureSize(ev.target.value || '');
    });
    document.getElementById('radioDisplayStreamBtn')?.addEventListener('click', () => {
      if (wantStream) requestStop();
      else requestStart();
    });
    document.getElementById('radioDisplayAutoStart')?.addEventListener('change', (ev) => {
      const on = !!ev.target.checked;
      setAutoStart(on);
      if (on && enabled && currentDeviceKey && !panel.isHiddenByUser()) {
        requestStart();
      }
    });
  }

  const fitBtn = document.getElementById('radioDisplayFitBtn');
  if (fitBtn && panel) {
    fitBtn.textContent = panel.getFitMode() === 'contain' ? 'Fit' : 'Fill';
  }

  syncAutoStartCheckbox();
  syncStreamButton();
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

  const streamHint = uiMode === 'popout'
    && new URLSearchParams(window.location.search).get('stream') === '1';
  const autoWanted = isAutoStart() || streamHint;

  // Probe status before attaching MJPEG so server halt blocks Auto/reload reopen.
  await pollStatus({ attachStream: false });
  syncDisconnectedControls();
  wantStream = !holdDisconnected && autoWanted;

  // Probe the device list before attaching MJPEG so enumeration never
  // Open()s the dongle while the capture loop already holds it.
  await loadDeviceSelect(currentDeviceKey);
  if (wantStream && enabled && currentDeviceKey && !panel.isHiddenByUser()) {
    await requestStart();
  }
  syncStreamButton();
  if (statusTimer) clearInterval(statusTimer);
  statusTimer = setInterval(pollStatus, STATUS_POLL_MS);

  window.addEventListener('beforeunload', () => stopStream());
}
