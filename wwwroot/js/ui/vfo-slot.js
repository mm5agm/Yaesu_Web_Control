/**
 * Shared right-hand column: VFO B or Radio Display (Radio Scope video).
 *
 * When Radio Display is configured, VFO A stays half-width and the right
 * column shows exactly one of VFO B or the capture card. Popping the
 * display out frees the column for VFO B until reattach.
 *
 * When Radio Display is unconfigured, behaviour matches the old VFO B
 * show/hide toggle (VFO A widens when B is hidden).
 */

const SLOT_KEY = 'ywc.vfoSlot';
const LEGACY_KEY = 'vfoBVisible';

/** @type {'b'|'scope'} */
let mode = 'b';
/** Radio Display status is something other than unconfigured. */
let displayAvailable = false;
/** Capture is living in the pop-out window — do not pull it back via the VFO B button. */
let popoutOpen = false;
let bound = false;

function slotEl() {
  return document.getElementById('vfoBSlot');
}

function hasSlotLayout() {
  return !!slotEl() && !!document.getElementById('radioDisplayContainer');
}

/**
 * @returns {'b'|'scope'}
 */
function readStoredMode() {
  const stored = localStorage.getItem(SLOT_KEY);
  if (stored === 'b' || stored === 'scope') return stored;
  // Migrate legacy show/hide: hidden → prefer scope once display appears; shown → b.
  const legacy = localStorage.getItem(LEGACY_KEY);
  if (legacy === 'false') return 'scope';
  if (legacy === 'true') return 'b';
  // First visit with a configured display: scope visible (matches prior default).
  return 'scope';
}

function syncToggleButtons() {
  const vfoBtn = document.getElementById('vfoBToggleBtn');
  const vfoLed = vfoBtn?.querySelector('.toggle-dd__led');
  const showingB = !displayAvailable
    ? document.getElementById('vfoBCol')?.style.display !== 'none'
    : (popoutOpen || mode === 'b');

  if (vfoBtn) {
    vfoLed?.classList.toggle('is-on', !!showingB);
    vfoBtn.setAttribute('aria-pressed', showingB ? 'true' : 'false');
    const tip = popoutOpen
      ? 'VFO B panel — Radio Scope is in a pop-out window'
      : (displayAvailable
        ? 'Switch the right column between VFO B and Radio Scope'
        : 'VFO B panel visibility — click to hide or show the panel');
    vfoBtn.setAttribute('title', tip);
    vfoBtn.setAttribute('aria-label', tip);
  }

  // Radio Scope button LED is driven by RadioDisplayPanel._syncVisibilityToggle;
  // keep it opposite of VFO B when the shared slot is active.
  if (displayAvailable && !popoutOpen) {
    const scopeBtn = document.getElementById('radioDisplayShowBtn');
    const scopeLed = scopeBtn?.querySelector('.toggle-dd__led');
    const scopeOn = mode === 'scope';
    if (scopeBtn) {
      scopeLed?.classList.toggle('is-on', scopeOn);
      scopeBtn.setAttribute('aria-pressed', scopeOn ? 'true' : 'false');
      const label = scopeOn ? 'Hide Radio Scope' : 'Show Radio Scope';
      scopeBtn.setAttribute('aria-label', label);
      const tip = scopeBtn.closest('.radio-display-tip');
      if (tip) {
        tip.setAttribute('data-bs-title', label);
        tip.setAttribute('aria-label', label);
        if (typeof bootstrap !== 'undefined' && bootstrap.Tooltip) {
          const instance = bootstrap.Tooltip.getInstance(tip);
          if (instance) instance.setContent({ '.tooltip-inner': label });
        }
      }
    }
  }
}

function applyLegacyVisibility(visible) {
  const aCol = document.getElementById('vfoACol');
  const bCol = document.getElementById('vfoBCol');
  const slot = slotEl();
  const vfoRow = document.getElementById('vfoRow');
  const rd = document.getElementById('radioDisplayContainer');

  if (rd) rd.style.display = 'none';
  if (bCol) bCol.style.display = visible ? '' : 'none';
  if (slot) slot.style.display = visible ? '' : 'none';

  if (aCol) {
    if (visible) {
      aCol.classList.remove('col-12');
      aCol.classList.add('col-lg-6');
    } else {
      aCol.classList.remove('col-lg-6');
      aCol.classList.add('col-12');
    }
  }
  vfoRow?.classList.toggle('vfo-b-hidden', !visible);
  syncToggleButtons();
}

function applySlotMode() {
  const aCol = document.getElementById('vfoACol');
  const bCol = document.getElementById('vfoBCol');
  const slot = slotEl();
  const vfoRow = document.getElementById('vfoRow');
  const rd = document.getElementById('radioDisplayContainer');

  // Shared-slot layout: VFO A never expands; the right column always stays.
  if (aCol) {
    aCol.classList.remove('col-12');
    aCol.classList.add('col-lg-6');
  }
  if (slot) slot.style.display = '';
  vfoRow?.classList.remove('vfo-b-hidden');

  const showScope = !popoutOpen && mode === 'scope';
  if (bCol) bCol.style.display = showScope ? 'none' : '';
  if (rd) {
    rd.style.display = showScope ? '' : 'none';
  }
  // Keep the Hide preference aligned so status polls and stream start agree.
  if (!popoutOpen) {
    localStorage.setItem('ywc.radioDisplayVisible', showScope ? '1' : '0');
  }
  syncToggleButtons();
}

function apply() {
  if (!hasSlotLayout()) {
    // No shared slot markup — legacy path on older DOM.
    const visible = localStorage.getItem(LEGACY_KEY) !== 'false';
    applyLegacyVisibility(visible);
    return;
  }
  if (!displayAvailable) {
    // A stored "scope" choice only applies once a capture card can fill the
    // column — until then show VFO B (or honour a classic hide via legacy key).
    if (localStorage.getItem(SLOT_KEY) === 'scope') applyLegacyVisibility(true);
    else applyLegacyVisibility(localStorage.getItem(LEGACY_KEY) !== 'false');
    return;
  }
  applySlotMode();
}

/**
 * @param {'b'|'scope'} next
 * @param {{ persist?: boolean, skipPanel?: boolean }} [opts]
 */
function setMode(next, opts = {}) {
  if (next !== 'b' && next !== 'scope') return;
  if (popoutOpen && next === 'scope') {
    // Capture stays in the pop-out; ignore requests to dock scope here.
    return;
  }
  mode = next;
  if (opts.persist !== false) {
    localStorage.setItem(SLOT_KEY, mode);
    localStorage.setItem(LEGACY_KEY, mode === 'b' ? 'true' : 'false');
  }
  apply();

  if (!opts.skipPanel && displayAvailable && typeof window.__vfoSlotApplyPanel === 'function') {
    window.__vfoSlotApplyPanel(mode === 'scope' && !popoutOpen);
  }
}

function toggleFromVfoBButton() {
  if (!hasSlotLayout() || !displayAvailable) {
    const visible = document.getElementById('vfoBCol')?.style.display !== 'none'
      && slotEl()?.style.display !== 'none';
    const next = !visible;
    localStorage.setItem(LEGACY_KEY, String(next));
    localStorage.setItem(SLOT_KEY, next ? 'b' : 'scope');
    mode = next ? 'b' : 'scope';
    applyLegacyVisibility(next);
    return;
  }
  if (popoutOpen) {
    // Scope is away — button is informational; keep B visible.
    return;
  }
  setMode(mode === 'b' ? 'scope' : 'b');
}

/** Called from Radio Display show/hide button / panel. */
function onRadioDisplayShow() {
  if (popoutOpen) return;
  setMode('scope', { skipPanel: true });
}

/** Called from Radio Display hide / Close. */
function onRadioDisplayHide() {
  if (popoutOpen) {
    // Hide while popping out: free the column for VFO B without changing the
    // remembered preference away from scope.
    applySlotMode();
    return;
  }
  setMode('b', { skipPanel: true });
}

function onPopoutStarted() {
  popoutOpen = true;
  // Remember scope as the preferred docked mode so reattach restores it.
  if (mode !== 'scope') {
    mode = 'scope';
    localStorage.setItem(SLOT_KEY, 'scope');
  }
  applySlotMode();
}

function onPopoutClosed() {
  popoutOpen = false;
  // Leave VFO B in the column until the operator switches back.
  mode = 'b';
  localStorage.setItem(SLOT_KEY, 'b');
  localStorage.setItem(LEGACY_KEY, 'true');
  apply();
}

function onReattached() {
  popoutOpen = false;
  setMode('scope', { skipPanel: true });
}

/**
 * @param {boolean} available
 */
function setDisplayAvailable(available) {
  const was = displayAvailable;
  displayAvailable = !!available;
  if (was !== displayAvailable) apply();
  else syncToggleButtons();
}

function getMode() {
  return mode;
}

function isPopoutOpen() {
  return popoutOpen;
}

function isDisplayAvailable() {
  return displayAvailable;
}

function bind() {
  if (bound) return;
  bound = true;
  mode = readStoredMode();
  document.getElementById('vfoBToggleBtn')?.addEventListener('click', toggleFromVfoBButton);
  apply();
}

const api = {
  bind,
  setMode,
  getMode,
  setDisplayAvailable,
  isDisplayAvailable,
  isPopoutOpen,
  onRadioDisplayShow,
  onRadioDisplayHide,
  onPopoutStarted,
  onPopoutClosed,
  onReattached,
  apply
};

window.vfoSlot = api;
export default api;
