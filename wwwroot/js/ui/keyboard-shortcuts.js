// Yaesu Web Control – keyboard shortcut dispatcher (Index page).
//
// Single registry drives both dispatch and the "?" help dialog so the table
// cannot drift from what the keys actually do.
//
// Do not import freq-keyboard.js / memories.js here: Index already loads those
// with a ?v= cache-buster, and a second unversioned import would create a
// separate module instance with its own closed-over DOM refs.
// band-plan.js is safe to import: modeForHz is a pure function with no page state.
import { modeForHz } from './band-plan.js';

/** @typedef {{ keys: string, action: string, group: string, when?: string }} ShortcutHelpRow */

const MIN_FREQ_HZ = 30_000;
const MAX_FREQ_HZ = 75_000_000;
const BANDS = ['160m', '80m', '60m', '40m', '30m', '20m', '17m', '15m', '12m', '10m', '6m', '4m'];

/**
 * Help-table rows. Keys are display labels; dispatch uses matchers below.
 * Keep this list in sync with handleKey() — review checklist for USER_MANUAL §13.
 * @type {ShortcutHelpRow[]}
 */
export const SHORTCUT_HELP = [
    // Frequency / VFO
    { group: 'Frequency & VFO', keys: 'g', action: 'Open frequency keypad for the active VFO' },
    { group: 'Frequency & VFO', keys: 'j / ←', action: 'Frequency step down' },
    { group: 'Frequency & VFO', keys: 'i / →', action: 'Frequency step up' },
    { group: 'Frequency & VFO', keys: 'Shift + j/i/←/→', action: 'Step ×10 (1 kHz when base is 100 Hz)' },
    { group: 'Frequency & VFO', keys: 'Alt + j/i/←/→', action: 'Step ×100 (10 kHz when base is 100 Hz)' },
    { group: 'Frequency & VFO', keys: 'Shift + Alt + j/i', action: 'Tune to previous / next DX spot' },
    { group: 'Frequency & VFO', keys: 'n', action: 'Toggle active VFO (A ↔ B)' },
    { group: 'Frequency & VFO', keys: 'N', action: 'Copy VFO A → B (A = B)' },
    { group: 'Frequency & VFO', keys: 'm', action: 'Open memories panel' },
    { group: 'Frequency & VFO', keys: 'b / B', action: 'Previous / next amateur band' },
    // Modes
    { group: 'Mode', keys: 'l', action: 'LSB' },
    { group: 'Mode', keys: 'u', action: 'USB' },
    { group: 'Mode', keys: 'c', action: 'CW-U' },
    { group: 'Mode', keys: 'C or Alt+c', action: 'CW-L' },
    { group: 'Mode', keys: 'a', action: 'AM' },
    { group: 'Mode', keys: 'A or Alt+a', action: 'AM-N' },
    { group: 'Mode', keys: 'q', action: 'FM' },
    { group: 'Mode', keys: 'Q or Alt+q', action: 'FM-N' },
    { group: 'Mode', keys: 'd', action: 'DATA-U' },
    { group: 'Mode', keys: 'Alt+d', action: 'DATA-L' },
    // Passband
    { group: 'Passband', keys: 'p', action: 'Narrow IF width (previous step)' },
    { group: 'Passband', keys: 'P', action: 'Widen IF width (next step)' },
    { group: 'Passband', keys: '/', action: 'Restore default IF width' },
    { group: 'Passband', keys: '↑ / ↓', action: 'IF Shift ±20 Hz (when frequency display is not focused)' },
    { group: 'Passband', keys: 'Shift + ↑/↓', action: 'IF Shift ±100 Hz' },
    // Spectrum (Windows / SDR configured)
    { group: 'Spectrum', keys: 'z / Z', action: 'Zoom in / out (narrower / wider span)', when: 'SDR configured' },
    { group: 'Spectrum', keys: 'Alt + z / Alt + Z', action: 'Max zoom in / out (narrowest / widest span)', when: 'SDR configured' },
    { group: 'Spectrum', keys: '< / >', action: 'Wider / narrower span (same as Z / z)', when: 'SDR configured' },
    { group: 'Spectrum', keys: 'w / W', action: 'Spectrum range −/+ 1 dB', when: 'SDR configured' },
    { group: 'Spectrum', keys: 'Alt + w / Alt + W', action: 'Spectrum range −/+ 10 dB', when: 'SDR configured' },
    { group: 'Spectrum', keys: 's', action: 'Toggle spectrum hold (freeze / live)', when: 'SDR configured' },
    { group: 'Spectrum', keys: 'S', action: 'Reset spectrum vertical range to 60 dB', when: 'SDR configured' },
    // Panels / UI
    { group: 'Panels', keys: 'D', action: 'Toggle DX Spots list' },
    { group: 'Panels', keys: '@', action: 'Open DX Watch dialog' },
    { group: 'Panels', keys: 'R', action: 'Show / focus Radio Display panel', when: 'Radio Display configured' },
    { group: 'Panels', keys: 'r', action: 'Start / stop Remote Audio', when: 'Remote Audio available' },
    { group: 'Panels', keys: 'x', action: 'Show / hide VFO B panel' },
    { group: 'Panels', keys: 'y', action: 'Show / hide S-meter history strip' },
    { group: 'Panels', keys: 'f / F', action: 'Enter full-screen mode' },
    // Audio
    { group: 'Remote Audio', keys: 'v / V', action: 'RX gain −/+ one step', when: 'Remote Audio streaming' },
    { group: 'Remote Audio', keys: 'Space', action: 'Mute / unmute RX audio (only if Space is not the TX shortcut)', when: 'Remote Audio streaming' },
    // Help / cancel
    { group: 'Help', keys: '?', action: 'Open this keyboard shortcuts dialog' },
    { group: 'Help', keys: 'h', action: 'Open this keyboard shortcuts dialog (alias)' },
    { group: 'Help', keys: 'Esc', action: 'Close help / dialogs; exit full-screen' },
];

/**
 * Resolve frequency step size in Hz from modifiers.
 * Base step is 100 Hz (audible on SSB/CW without racing the band).
 * @param {{ shiftKey?: boolean, altKey?: boolean }} mods
 * @returns {number}
 */
export function resolveTuneStepHz(mods = {}) {
    const base = 100;
    if (mods.shiftKey && mods.altKey) return 0; // reserved for DX-spot hop
    if (mods.altKey) return base * 100;   // 10 kHz
    if (mods.shiftKey) return base * 10;  // 1 kHz
    return base;
}

/**
 * @param {string} key
 * @param {{ altKey?: boolean, shiftKey?: boolean }} mods
 * @returns {string | null} Yaesu mode id, or null
 */
export function resolveModeShortcut(key, mods = {}) {
    const k = key.length === 1 ? key : '';
    if (!k) return null;
    const lower = k.toLowerCase();
    const alt = !!mods.altKey;

    if (lower === 'l') return 'LSB';
    if (lower === 'u') return 'USB';
    if (lower === 'c') return (alt || k === 'C') ? 'CW-L' : 'CW-U';
    if (lower === 'a') return (alt || k === 'A') ? 'AM-N' : 'AM';
    if (lower === 'q') return (alt || k === 'Q') ? 'FM-N' : 'FM';
    if (lower === 'd' && k === 'd') return alt ? 'DATA-L' : 'DATA-U';
    return null;
}

// ── Context guards ───────────────────────────────────────────────────────────

function isTypingIntoEditable() {
    const active = document.activeElement;
    if (active) {
        if (active.isContentEditable) return true;
        if (active.tagName === 'TEXTAREA' || active.tagName === 'SELECT') return true;
        if (active.tagName === 'INPUT') {
            const type = (active.getAttribute('type') || 'text').toLowerCase();
            if (['range', 'checkbox', 'radio', 'button', 'submit', 'reset', 'file', 'color', 'hidden'].includes(type))
                return false;
            return true;
        }
    }
    const freqKb = document.getElementById('freqKeyboardDialog');
    if (freqKb && freqKb.open) return true;
    return false;
}

function isFrequencyDisplayFocused() {
    const active = document.activeElement;
    if (!active) return false;
    return active.id === 'freqA' || active.id === 'freqB'
        || !!active.closest?.('#freqA, #freqB, #freqA-controls, #freqB-controls');
}

function isBrowserFindChord(e) {
    return (e.ctrlKey || e.metaKey) && (e.key === 'f' || e.key === 'F');
}

function spaceIsTxShortcut() {
    const configured = window.ywcTxToggleKey;
    return configured === 'Space' || configured === ' ';
}

// ── Active VFO ───────────────────────────────────────────────────────────────

function getActiveVfo() {
    if (typeof window.getActiveVfoLetter === 'function') {
        const v = window.getActiveVfoLetter();
        if (v === 'A' || v === 'B') return v;
    }
    const vfoRow = document.getElementById('vfoRow');
    const single = vfoRow?.dataset?.singleReceiver === 'true';
    if (single) {
        if (document.getElementById('rxVfoB')?.classList.contains('btn-success')) return 'B';
        return 'A';
    }
    if (document.getElementById('vfoBCol')?.classList.contains('vfo-active')) return 'B';
    return 'A';
}

function announce(message) {
    const el = document.getElementById('vfoStatusAnnounce');
    if (!el || !message) return;
    el.textContent = '';
    requestAnimationFrame(() => { el.textContent = message; });
}

function clampHz(hz) {
    return Math.max(MIN_FREQ_HZ, Math.min(MAX_FREQ_HZ, Math.round(hz)));
}

function currentFrequencyHz(vfo) {
    const state = window.radioControl?._state;
    const fromState = state?.lastBackendFreq?.[vfo];
    if (Number.isFinite(fromState) && fromState > 0) return fromState;
    const display = document.getElementById('freq' + vfo);
    if (!display) return 0;
    const digits = Array.from(display.querySelectorAll('.digit'))
        .filter(d => d.textContent !== '.')
        .map(d => d.textContent)
        .join('');
    const n = parseInt(digits, 10);
    return Number.isFinite(n) ? n : 0;
}

async function stepFrequency(deltaHz) {
    const vfo = getActiveVfo();
    const cur = currentFrequencyHz(vfo);
    if (!cur || !window.radioControl?.setFrequency) return;
    const next = clampHz(cur + deltaHz);
    if (next === cur) return;
    await window.radioControl.setFrequency(vfo, next);
}

// ── Band / mode / IF width ───────────────────────────────────────────────────

function currentBandId(vfo) {
    const checked = document.querySelector(`#bandRadioGroup${vfo} input[type="radio"]:checked`);
    return checked?.value || null;
}

function cycleBand(direction) {
    const vfo = getActiveVfo();
    const cur = (currentBandId(vfo) || '').toLowerCase();
    let idx = BANDS.findIndex(b => b.toLowerCase() === cur);
    if (idx < 0) idx = direction > 0 ? -1 : 0;
    const next = BANDS[(idx + direction + BANDS.length) % BANDS.length];
    const label = document.querySelector(`#bandRadioGroup${vfo} label[data-band="${next}"]`);
    if (label) {
        label.click();
    } else if (typeof window.setBand === 'function') {
        window.setBand(vfo, next);
    } else if (window.radioControl?.setBand) {
        window.radioControl.setBand(vfo, next);
    }
    announce(`Band ${next}`);
}

function setModeForActive(mode) {
    const vfo = getActiveVfo();
    const select = document.getElementById(`modeSelect${vfo}`);
    if (select) {
        const opt = Array.from(select.options).find(o => o.value === mode);
        if (!opt) {
            announce(`Mode ${mode} not available`);
            return;
        }
        select.value = mode;
    }
    if (typeof window.setMode === 'function') window.setMode(vfo, mode);
    announce(`Mode ${mode}`);
}

function cycleIfWidth(direction) {
    const vfo = getActiveVfo();
    const select = document.getElementById(`ifWidthSelect${vfo}`);
    if (!select || select.disabled || select.options.length === 0) {
        announce('IF Width not available in this mode');
        return;
    }
    const idx = select.selectedIndex;
    const next = Math.max(0, Math.min(select.options.length - 1, idx + direction));
    if (next === idx) return;
    select.selectedIndex = next;
    const code = select.value;
    window.radioControl?.setIfWidth?.(vfo, code);
    const label = select.options[next]?.textContent?.trim() || code;
    announce(`IF Width ${label}`);
}

function restoreDefaultIfWidth() {
    const vfo = getActiveVfo();
    if (typeof window.resetIfWidth === 'function') {
        window.resetIfWidth(vfo);
        announce('IF Width default');
        return;
    }
    const select = document.getElementById(`ifWidthSelect${vfo}`);
    if (!select || !select.options.length) return;
    // Prefer an option labelled Default, else first option (code 0 on most models).
    let opt = Array.from(select.options).find(o => /default/i.test(o.textContent || ''));
    if (!opt) opt = select.options[0];
    select.value = opt.value;
    window.radioControl?.setIfWidth?.(vfo, opt.value);
    announce('IF Width default');
}

function nudgeIfShift(deltaHz) {
    const vfo = getActiveVfo();
    const slider = document.getElementById(`ifShiftSlider${vfo}`);
    if (!slider) return;
    const min = Number(slider.min);
    const max = Number(slider.max);
    const cur = Number(slider.value) || 0;
    const next = Math.max(min, Math.min(max, cur + deltaHz));
    if (next === cur) return;
    slider.value = String(next);
    const label = document.getElementById(`ifShiftValue${vfo}`);
    if (label) label.textContent = String(next);
    slider.dispatchEvent(new Event('input', { bubbles: true }));
    slider.dispatchEvent(new Event('change', { bubbles: true }));
    // site.js wires change → setIfShift; also call directly for reliability.
    window.radioControl?.setIfShift?.(vfo, next);
    announce(`IF Shift ${next > 0 ? '+' : ''}${next} Hz`);
}

// ── VFO / panels ─────────────────────────────────────────────────────────────

function toggleActiveVfo() {
    const next = getActiveVfo() === 'A' ? 'B' : 'A';
    const vfoRow = document.getElementById('vfoRow');
    const single = vfoRow?.dataset?.singleReceiver === 'true';
    if (single) {
        document.getElementById(`rxVfo${next}`)?.click();
    } else if (typeof window.setActiveVfo === 'function') {
        window.setActiveVfo(next);
    } else {
        document.querySelector(`#vfo${next}Col .card-header`)?.click();
    }
    announce(`VFO ${next}`);
}

function copyAtoB() {
    if (typeof window.copyVfo === 'function') window.copyVfo('ab');
    else document.getElementById('copyAtoBBtn')?.click();
    announce('VFO A copied to B');
}

function toggleMemories() {
    const dialog = document.getElementById('memoriesDialog');
    if (dialog?.open) {
        if (typeof window.closeMemoriesPanel === 'function') window.closeMemoriesPanel();
        else dialog.close();
        return;
    }
    if (typeof window.openMemoriesPanel === 'function') window.openMemoriesPanel();
    else document.getElementById('memBtn')?.click();
}

function toggleDxSpots() {
    window.dxSpotsPanel?.toggle?.();
}

function openDxWatch() {
    const dlg = document.getElementById('dxWatchDialog');
    if (!dlg) return;
    dlg.show();
    window.refreshDxWatchList?.();
}

function toggleVfoB() {
    document.getElementById('vfoBToggleBtn')?.click();
}

function toggleSMeterHistory() {
    document.getElementById('sMeterHistoryToggleBtn')?.click();
}

function focusFrequencyEntry() {
    const vfo = getActiveVfo();
    // openFreqKeyboard / openKeyboard already focuses a keypad button inside the
    // dialog. Do not refocus #freqA/#freqB afterwards — that pulls focus out of
    // the dialog, so Enter/←/→/Backspace stop working (digits still worked
    // because they are handled regardless of focus).
    if (typeof window.openFreqKeyboard === 'function') {
        window.openFreqKeyboard(vfo);
        return;
    }
    document.querySelector(`[data-freq-keyboard="${vfo}"]`)?.click();
}

function enterFullscreen() {
    if (!document.fullscreenElement) {
        document.body.requestFullscreen?.();
    }
}

function showRadioDisplay() {
    const showBtn = document.getElementById('radioDisplayShowBtn');
    const container = document.getElementById('radioDisplayContainer');
    if (container && container.style.display === 'none') {
        showBtn?.click();
    }
    container?.scrollIntoView?.({ behavior: 'smooth', block: 'nearest' });
    announce('Radio Display');
}

function toggleRemoteAudio() {
    const startBtn = document.getElementById('remoteAudioStartBtn');
    const stopBtn = document.getElementById('remoteAudioStopBtn');
    if (!startBtn && !stopBtn) {
        announce('Remote Audio not available');
        return;
    }
    if (stopBtn && !stopBtn.disabled) {
        stopBtn.click();
        announce('Remote Audio stopped');
        return;
    }
    if (startBtn && !startBtn.disabled) {
        startBtn.click();
        announce('Remote Audio starting');
    }
}

function nudgeRxGain(delta) {
    const slider = document.getElementById('remoteAudioRxGain');
    if (!slider) return false;
    const min = Number(slider.min || 0.05);
    const max = Number(slider.max || 4);
    const step = Number(slider.step || 0.05);
    const cur = Number(slider.value) || 1;
    const next = Math.max(min, Math.min(max, +(cur + delta * step).toFixed(2)));
    if (next === cur) return true;
    slider.value = String(next);
    slider.dispatchEvent(new Event('input', { bubbles: true }));
    const val = document.getElementById('remoteAudioRxGainVal');
    if (val) val.textContent = next.toFixed(2);
    announce(`RX gain ${next.toFixed(2)}`);
    return true;
}

function toggleRxMute() {
    const btn = document.getElementById('remoteAudioRxMute');
    if (!btn) return false;
    btn.click();
    return true;
}

// ── Spectrum ─────────────────────────────────────────────────────────────────

function activeSpectrumPanel() {
    const vfo = getActiveVfo();
    const panel = vfo === 'B' ? window.spectrumPanelB : window.spectrumPanelA;
    if (panel) return { panel, vfo };
    if (window.spectrumPanelA) return { panel: window.spectrumPanelA, vfo: 'A' };
    return null;
}

function spanButtons(vfo) {
    return Array.from(document.querySelectorAll(`.span-btn[data-vfo="${vfo}"]`));
}

function cycleSpan(direction, { extreme } = {}) {
    const ctx = activeSpectrumPanel();
    if (!ctx) return;
    const buttons = spanButtons(ctx.vfo);
    if (!buttons.length) return;
    // Buttons are ordered narrow → wide in the markup.
    const idx = buttons.findIndex(b => b.classList.contains('active'));
    let nextIdx;
    if (extreme) {
        nextIdx = direction < 0 ? 0 : buttons.length - 1;
    } else {
        const cur = idx < 0 ? (direction < 0 ? buttons.length : -1) : idx;
        nextIdx = Math.max(0, Math.min(buttons.length - 1, cur + direction));
    }
    if (nextIdx === idx) return;
    buttons[nextIdx].click();
    announce(`Span ${buttons[nextIdx].textContent.trim()}`);
}

function nudgeSpectrumRange(deltaDb) {
    const ctx = activeSpectrumPanel();
    if (!ctx?.panel?.setSpectrumRange || !ctx.panel.getSpectrumRange) return;
    const cur = ctx.panel.getSpectrumRange();
    const next = Math.max(5, Math.min(160, Math.round(cur + deltaDb)));
    ctx.panel.setSpectrumRange(next);
    const slider = document.querySelector(`.spectrum-range-slider[data-vfo="${ctx.vfo}"]`);
    const label = document.querySelector(`.spectrum-range-label[data-vfo="${ctx.vfo}"]`);
    if (slider) slider.value = String(next);
    if (label) label.textContent = `${next} dB`;
    announce(`Spectrum range ${next} dB`);
}

function resetSpectrumRange() {
    const ctx = activeSpectrumPanel();
    if (!ctx?.panel?.setSpectrumRange) return;
    ctx.panel.setSpectrumRange(60);
    const slider = document.querySelector(`.spectrum-range-slider[data-vfo="${ctx.vfo}"]`);
    const label = document.querySelector(`.spectrum-range-label[data-vfo="${ctx.vfo}"]`);
    if (slider) slider.value = '60';
    if (label) label.textContent = '60 dB';
    announce('Spectrum range 60 dB');
}

function toggleSpectrumHold() {
    const ctx = activeSpectrumPanel();
    if (!ctx) return;
    const btn = document.querySelector(`.spectrum-hold-btn[data-vfo="${ctx.vfo}"]`);
    btn?.click();
}

function stepDxSpot(direction) {
    const vfo = getActiveVfo();
    const panel = (vfo === 'B' ? window.spectrumPanelB : window.spectrumPanelA) || window.spectrumPanelA;
    const spots = panel?._spots;
    if (!Array.isArray(spots) || spots.length === 0) {
        announce('No DX spots');
        return;
    }
    const cur = currentFrequencyHz(vfo);
    const sorted = spots
        .filter(s => Number.isFinite(s.frequencyHz))
        .slice()
        .sort((a, b) => a.frequencyHz - b.frequencyHz);
    if (!sorted.length) return;

    let target;
    if (direction > 0) {
        target = sorted.find(s => s.frequencyHz > cur + 50) || sorted[0];
    } else {
        target = [...sorted].reverse().find(s => s.frequencyHz < cur - 50) || sorted[sorted.length - 1];
    }
    if (!target || !window.radioControl?.setFrequency) return;
    window.radioControl.setFrequency(vfo, target.frequencyHz);
    const mode = modeForHz(target.frequencyHz);
    if (mode && typeof window.setMode === 'function') {
        try { window.setMode(vfo, mode); } catch { /* ignore */ }
    }
    announce(`DX ${target.callsign || ''} ${(target.frequencyHz / 1000).toFixed(1)}`.trim());
}

// ── Help dialog ──────────────────────────────────────────────────────────────

let _helpDialog = null;
let _helpPreviousFocus = null;

function ensureHelpDialog() {
    if (_helpDialog) return _helpDialog;
    const dlg = document.createElement('dialog');
    dlg.id = 'keyboardShortcutsDialog';
    dlg.setAttribute('aria-labelledby', 'keyboardShortcutsTitle');
    dlg.style.cssText = [
        'border:1px solid #555', 'border-radius:8px', 'background:#1e1e2e', 'color:#eee',
        'padding:0', 'max-width:min(720px, 95vw)', 'width:720px', 'max-height:90vh',
        'z-index:10050', 'box-shadow:0 12px 40px rgba(0,0,0,0.65)'
    ].join(';');

    const groups = [];
    for (const row of SHORTCUT_HELP) {
        let g = groups.find(x => x.name === row.group);
        if (!g) { g = { name: row.group, rows: [] }; groups.push(g); }
        g.rows.push(row);
    }

    let body = '';
    for (const g of groups) {
        body += `<h3 style="font-size:0.95rem;margin:1rem 0 0.4rem;color:#9cf;">${escapeHtml(g.name)}</h3>`;
        body += '<table style="width:100%;border-collapse:collapse;font-size:0.85rem;">';
        body += '<thead><tr>'
            + '<th scope="col" style="text-align:left;padding:4px 8px;border-bottom:1px solid #444;width:40%;">Key</th>'
            + '<th scope="col" style="text-align:left;padding:4px 8px;border-bottom:1px solid #444;">Action</th>'
            + '</tr></thead><tbody>';
        for (const row of g.rows) {
            const when = row.when
                ? ` <span style="color:#888;font-size:0.8em;">(${escapeHtml(row.when)})</span>`
                : '';
            body += `<tr>`
                + `<td style="padding:4px 8px;border-bottom:1px solid #333;font-family:ui-monospace,Consolas,monospace;white-space:nowrap;">${escapeHtml(row.keys)}</td>`
                + `<td style="padding:4px 8px;border-bottom:1px solid #333;">${escapeHtml(row.action)}${when}</td>`
                + `</tr>`;
        }
        body += '</tbody></table>';
    }

    dlg.innerHTML =
        `<div style="display:flex;align-items:center;justify-content:space-between;padding:10px 14px;border-bottom:1px solid #444;position:sticky;top:0;background:#1e1e2e;">`
        + `<strong id="keyboardShortcutsTitle">Keyboard shortcuts</strong>`
        + `<button type="button" class="btn btn-sm ywc-dialog-close" id="keyboardShortcutsClose" aria-label="Close keyboard shortcuts">&#10005;</button>`
        + `</div>`
        + `<div style="padding:4px 14px 16px;overflow:auto;max-height:calc(90vh - 52px);">`
        + `<p style="font-size:0.8rem;color:#aaa;margin:0.6rem 0 0;">`
        + `Shortcuts are ignored while typing in a text field, the CW Send box, or the frequency keypad. `
        + `Browser chords such as Ctrl/⌘+F are never captured. `
        + `Frequency digit editing (arrows on a focused VFO display) is unchanged.`
        + `</p>`
        + body
        + `</div>`;

    document.body.appendChild(dlg);
    dlg.querySelector('#keyboardShortcutsClose')?.addEventListener('click', () => closeHelpDialog());
    dlg.addEventListener('cancel', (e) => {
        // Native <dialog> cancel (Esc) — keep our close path for focus restore.
        e.preventDefault();
        closeHelpDialog();
    });
    _helpDialog = dlg;
    return dlg;
}

function escapeHtml(s) {
    return String(s ?? '').replace(/[&<>"']/g, c => (
        { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
    ));
}

export function openHelpDialog() {
    const dlg = ensureHelpDialog();
    _helpPreviousFocus = document.activeElement;
    if (!dlg.open) dlg.showModal();
    dlg.querySelector('#keyboardShortcutsClose')?.focus();
}

export function closeHelpDialog() {
    if (!_helpDialog?.open) return;
    _helpDialog.close();
    const prev = _helpPreviousFocus;
    _helpPreviousFocus = null;
    if (prev && typeof prev.focus === 'function') {
        try { prev.focus({ preventScroll: true }); } catch { /* ignore */ }
    }
}

function isHelpOpen() {
    return !!_helpDialog?.open;
}

// ── Dispatcher ───────────────────────────────────────────────────────────────

/**
 * @param {KeyboardEvent} e
 * @returns {boolean} true if handled
 */
export function handleKey(e) {
    if (e.ctrlKey || e.metaKey) return false;
    if (isBrowserFindChord(e)) return false;

    // Help: allow ? / h even when a non-text control has focus, but not in text fields.
    const key = e.key;

    if (key === 'Escape') {
        if (isHelpOpen()) {
            e.preventDefault();
            closeHelpDialog();
            return true;
        }
        // Let existing capture-phase handlers close freq keyboard / memories.
        // Still handle fullscreen if nothing else consumed it on bubble — site.js
        // already exits fullscreen on Esc; we only claim help here.
        return false;
    }

    if (isTypingIntoEditable()) return false;

    // ? and h open help (Shift+/ produces "?" on US layouts — key is "?")
    if (key === '?' || (key === 'h' && !e.altKey && !e.shiftKey)) {
        e.preventDefault();
        if (isHelpOpen()) closeHelpDialog();
        else openHelpDialog();
        return true;
    }

    if (isHelpOpen()) return false;

    // Fullscreen — preserve existing f/F behaviour (not Ctrl/Cmd+F).
    if ((key === 'f' || key === 'F') && !e.altKey) {
        e.preventDefault();
        enterFullscreen();
        return true;
    }

    // Space → RX mute only when not configured as TX PTT.
    if ((key === ' ' || key === 'Spacebar') && !e.altKey && !e.shiftKey) {
        if (spaceIsTxShortcut()) return false;
        if (toggleRxMute()) {
            e.preventDefault();
            return true;
        }
        return false;
    }

    // Mode letters (before generic letter handling).
    const mode = resolveModeShortcut(key, e);
    if (mode && !e.repeat) {
        // 'D' alone is DX spots (uppercase), not a mode — resolveModeShortcut
        // only returns DATA for lowercase 'd'.
        e.preventDefault();
        setModeForActive(mode);
        return true;
    }

    // DX spots / watch
    if (key === 'D' && !e.altKey) {
        e.preventDefault();
        toggleDxSpots();
        return true;
    }
    if (key === '@') {
        e.preventDefault();
        openDxWatch();
        return true;
    }

    // Frequency entry
    if (key === 'g' && !e.altKey && !e.shiftKey) {
        e.preventDefault();
        focusFrequencyEntry();
        return true;
    }

    // Memories / VFO
    if (key === 'm' && !e.altKey && !e.shiftKey) {
        e.preventDefault();
        toggleMemories();
        return true;
    }
    if (key === 'n' && !e.altKey && !e.shiftKey) {
        e.preventDefault();
        toggleActiveVfo();
        return true;
    }
    if (key === 'N' && !e.altKey) {
        e.preventDefault();
        copyAtoB();
        return true;
    }

    // Band
    if ((key === 'b' || key === 'B') && !e.altKey) {
        e.preventDefault();
        cycleBand(key === 'B' ? 1 : -1);
        return true;
    }

    // Tune: j/i and arrows (arrows skipped while frequency digit editor has focus)
    const isTuneDown = key === 'j' || key === 'ArrowLeft';
    const isTuneUp = key === 'i' || key === 'ArrowRight';
    if ((isTuneDown || isTuneUp) && !(isFrequencyDisplayFocused() && (key === 'ArrowLeft' || key === 'ArrowRight'))) {
        if (e.shiftKey && e.altKey && (key === 'j' || key === 'i')) {
            e.preventDefault();
            stepDxSpot(key === 'i' ? 1 : -1);
            return true;
        }
        // Don't steal Left/Right from band radiogroup / other widgets that use them.
        if ((key === 'ArrowLeft' || key === 'ArrowRight') && document.activeElement) {
            const role = document.activeElement.getAttribute('role');
            if (role === 'radio' || role === 'slider' || document.activeElement.closest?.('[role="radiogroup"]')) {
                return false;
            }
            if (document.activeElement.tagName === 'INPUT' || document.activeElement.tagName === 'SELECT') {
                return false;
            }
        }
        const step = resolveTuneStepHz(e);
        if (step > 0) {
            e.preventDefault();
            stepFrequency(isTuneUp ? step : -step);
            return true;
        }
    }

    // IF width / default
    if (key === 'p' && !e.altKey) {
        e.preventDefault();
        cycleIfWidth(-1);
        return true;
    }
    if (key === 'P' && !e.altKey) {
        e.preventDefault();
        cycleIfWidth(1);
        return true;
    }
    if (key === '/' && !e.altKey) {
        e.preventDefault();
        restoreDefaultIfWidth();
        return true;
    }

    // IF shift via Up/Down when freq display not focused
    if ((key === 'ArrowUp' || key === 'ArrowDown') && !isFrequencyDisplayFocused()) {
        if (document.activeElement?.tagName === 'INPUT' || document.activeElement?.tagName === 'SELECT') {
            return false;
        }
        if (document.activeElement?.closest?.('[role="radiogroup"]')) return false;
        e.preventDefault();
        const delta = (key === 'ArrowUp' ? 1 : -1) * (e.shiftKey ? 100 : 20);
        nudgeIfShift(delta);
        return true;
    }

    // Spectrum zoom / range
    if ((key === 'z' || key === 'Z') && window.ywcIsWindowsHost) {
        e.preventDefault();
        // z = zoom in (narrower), Z = zoom out (wider)
        const dir = key === 'z' ? -1 : 1;
        cycleSpan(dir, { extreme: e.altKey });
        return true;
    }
    if ((key === '<' || key === '>') && window.ywcIsWindowsHost) {
        e.preventDefault();
        cycleSpan(key === '<' ? 1 : -1);
        return true;
    }
    if ((key === 'w' || key === 'W') && window.ywcIsWindowsHost) {
        e.preventDefault();
        const mag = e.altKey ? 10 : 1;
        nudgeSpectrumRange(key === 'W' ? mag : -mag);
        return true;
    }
    if (key === 's' && !e.altKey && !e.shiftKey && window.ywcIsWindowsHost) {
        e.preventDefault();
        toggleSpectrumHold();
        return true;
    }
    if (key === 'S' && !e.altKey && window.ywcIsWindowsHost) {
        e.preventDefault();
        resetSpectrumRange();
        return true;
    }

    // Panels / audio
    if (key === 'x' && !e.altKey && !e.shiftKey) {
        e.preventDefault();
        toggleVfoB();
        return true;
    }
    if (key === 'y' && !e.altKey && !e.shiftKey) {
        e.preventDefault();
        toggleSMeterHistory();
        return true;
    }
    if (key === 'R' && !e.altKey) {
        e.preventDefault();
        showRadioDisplay();
        return true;
    }
    if (key === 'r' && !e.altKey && !e.shiftKey) {
        e.preventDefault();
        toggleRemoteAudio();
        return true;
    }
    if ((key === 'v' || key === 'V') && !e.altKey) {
        if (nudgeRxGain(key === 'V' ? 1 : -1)) {
            e.preventDefault();
            return true;
        }
        return false;
    }

    return false;
}

/**
 * Wire document-level shortcuts on the Index page.
 */
export function initKeyboardShortcuts() {
    document.addEventListener('keydown', (e) => {
        if (e.defaultPrevented) return;
        handleKey(e);
    });

    // Optional toolbar affordance: any [data-open-shortcuts] button.
    document.querySelectorAll('[data-open-shortcuts]').forEach(btn => {
        btn.addEventListener('click', () => openHelpDialog());
    });
}
