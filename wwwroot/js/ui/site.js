// Full-page "server has stopped" overlay. Shown when the SystemTrayService
// broadcasts ServerShutdown right before stopping the host, so the browser
// tab doesn't sit on stale data with a frozen meter needle. The page can't
// reliably close its own tab (browsers only allow window.close() for tabs
// the page itself opened) — we try it as a courtesy and otherwise leave a
// clear visual cue.
function showServerStoppedOverlay() {
    if (document.getElementById('ywcServerStoppedOverlay')) return;
    const overlay = document.createElement('div');
    overlay.id = 'ywcServerStoppedOverlay';
    overlay.setAttribute('role', 'alertdialog');
    overlay.setAttribute('aria-modal', 'true');
    overlay.setAttribute('aria-label', 'Yaesu Web Control has been closed');
    overlay.style.cssText = [
        'position:fixed', 'inset:0', 'z-index:99999',
        // Fully opaque, not rgba(...,0.96) — at 4% transparency, any canvas
        // panel that's still self-animating (notably the filter scope, which
        // runs a 20 fps requestAnimationFrame loop) was ghosting through and
        // making the page look "frozen except for the filter graphic" instead
        // of clearly stopped.
        'background:#141820', 'color:#e0e0e0',
        'display:flex', 'flex-direction:column',
        'justify-content:center', 'align-items:center', 'text-align:center',
        'padding:24px', 'font-family:system-ui,sans-serif'
    ].join(';');
    overlay.innerHTML =
        '<div style="font-size:2rem;margin-bottom:0.5rem">Yaesu Web Control has stopped</div>' +
        '<div style="font-size:1rem;max-width:520px;line-height:1.5;margin-bottom:1.5rem;color:#aab">' +
            'The app has been closed from the system-tray icon. The radio is no longer being controlled from this browser tab.' +
            '<br><br>Once you restart Yaesu Web Control, click <strong>Reload page</strong> below to continue. Or just close this browser tab using its X button.' +
        '</div>' +
        '<button type="button" id="ywcServerStoppedReloadBtn" ' +
            'style="padding:8px 22px;border-radius:6px;border:1px solid #4a8abf;background:#2a4860;color:#e0e0ff;cursor:pointer;font-size:0.95rem">' +
            '↻ Reload page' +
        '</button>';
    document.body.appendChild(overlay);
    document.getElementById('ywcServerStoppedReloadBtn')?.addEventListener('click', () => {
        // location.reload() works for any tab regardless of how it was opened.
        // If YWC isn't back up yet, the reload will fail and the browser shows
        // its own "can't connect" page — which is still a clearer outcome than
        // a tab stuck on the overlay.
        location.reload();
    });

    // Cleanly stop any panels that drive their own animation timers. The
    // overlay is opaque so they'd be hidden anyway, but cancelling the RAF
    // loops avoids burning CPU on a tab the user has clearly walked away
    // from. Each call is wrapped because some panels may not exist on this
    // page (e.g. spectrum is only present when an SDR is configured).
    try { window.filterScopePanelA?.stop?.(); } catch { /* ignore */ }
    try { window.filterScopePanelB?.stop?.(); } catch { /* ignore */ }
    try { window.sMeterHistory?.stop?.();    } catch { /* ignore */ }
    try { window.sMeterHistoryB?.stop?.();   } catch { /* ignore */ }
}

function isTypingIntoEditable() {
    const active = document.activeElement;
    if (active) {
        if (active.isContentEditable) return true;
        if (active.tagName === 'TEXTAREA' || active.tagName === 'SELECT') return true;
        if (active.tagName === 'INPUT') {
            const type = (active.getAttribute('type') || 'text').toLowerCase();
            // Range/checkbox/etc. are not text entry — allow TX shortcut.
            if (['range', 'checkbox', 'radio', 'button', 'submit', 'reset', 'file', 'color', 'hidden'].includes(type))
                return false;
            return true;
        }
    }
    // The on-screen frequency keypad is a text-entry surface even though focus
    // sits on its buttons (a <dialog>, not an <input>). Treat it as "typing" so
    // global shortcuts (TX toggle, fullscreen) don't fire while a frequency is
    // being entered — otherwise a non-digit shortcut key sails past the keypad's
    // own keydown handler (which only swallows digits/Escape/nav) to the rig.
    const freqKb = document.getElementById('freqKeyboardDialog');
    if (freqKb && freqKb.open) return true;
    return false;
}

// Make every range input usable without dragging. Yaesu context sliders have
// their own wheel handler; this delegated handler covers standalone sliders
// and reuses their normal input/change wiring.
document.addEventListener('wheel', function (event) {
    const slider = event.target.closest?.('input[type="range"]');
    if (!slider || slider.disabled || event.ctrlKey || !event.deltaY) return;

    const min = Number(slider.min || 0);
    const max = Number(slider.max || 100);
    const step = Number(slider.step || 1);
    const current = Number(slider.value);
    if (![min, max, step, current].every(Number.isFinite) || step <= 0 || max <= min) return;

    const next = Math.min(max, Math.max(min, current + (event.deltaY < 0 ? step : -step)));
    if (next === current) return;

    event.preventDefault();
    slider.value = String(next);
    slider.dispatchEvent(new Event('input', { bubbles: true }));
    slider.dispatchEvent(new Event('change', { bubbles: true }));
}, { passive: false });

// --- Fullscreen Toggle: 'f' or 'F' to enter, 'Esc' to exit ---
document.addEventListener('keydown', function (e) {
    // Ignore if typing in an input, textarea, or contenteditable
    if (isTypingIntoEditable()) return;
    if ((e.key === 'f' || e.key === 'F') && !e.ctrlKey && !e.metaKey && !e.altKey) {
        // Bare F only — guarding against modifiers stops YWC from stealing
        // Ctrl+F (browser find-in-page) and Cmd+F on Mac.
        const body = document.body;
        if (body && !document.fullscreenElement) {
            body.requestFullscreen && body.requestFullscreen();
            e.preventDefault();
        }
    } else if (e.key === 'Escape') {
        // Exit fullscreen if in fullscreen
        if (document.fullscreenElement) {
            document.exitFullscreen && document.exitFullscreen();
            e.preventDefault();
        }
    }
});

// Optional browser TX shortcut. Disabled by default; when configured, it
// toggles transmit using the same /api/cat/tx flow as the on-screen button.
document.addEventListener('keydown', function (e) {
    const configuredKey = window.ywcTxToggleKey;
    // Empty string only — do not use falsy check; a legacy " " must still match.
    if (configuredKey == null || configuredKey === '' || isTypingIntoEditable()) return;
    if (e.ctrlKey || e.metaKey || e.altKey || e.repeat) return;

    // Settings stores Space as the token "Space" (HTML cannot round-trip " ").
    // Accept both the token and a legacy lone-space value.
    const isSpaceShortcut = configuredKey === 'Space' || configuredKey === ' ';
    const keyMatches = isSpaceShortcut
        ? (e.key === ' ' || e.code === 'Space')
        : (configuredKey.length === 1 && e.key.length === 1
            ? e.key.toLowerCase() === configuredKey.toLowerCase()
            : e.key === configuredKey || e.code === configuredKey);
    if (!keyMatches) return;

    e.preventDefault();
    const active = document.activeElement;
    if (active && typeof active.blur === 'function' && active.tagName === 'BUTTON')
        active.blur();
    toggleTx();
}, true);

// Add/remove fullscreen-mode class on body when entering/exiting fullscreen
document.addEventListener('fullscreenchange', function () {
    if (document.fullscreenElement) {
        document.body.classList.add('fullscreen-mode');
    } else {
        document.body.classList.remove('fullscreen-mode');
    }
});

// Debugging: Log Save Button Presses and Page Content for Language Issues
// ========================================================================
// This block helps diagnose why the browser might think the page is in French.
// It logs all clicks on elements with "save" in their id, name, or class,
// and logs the text content of the page and any form data being submitted.
document.addEventListener('click', function (e) {
    let el = e.target;
    if (!el) return;
    // Check if the element is a button or input with "save" in id, name, or class
    let isSave = false;
    if (el.tagName === 'BUTTON' || el.tagName === 'INPUT') {
        let id = (el.id || '').toLowerCase();
        let name = (el.name || '').toLowerCase();
        let cls = (el.className || '').toLowerCase();
        if (id.includes('save') || name.includes('save') || cls.includes('save')) {
            isSave = true;
        }
    }
    // Also check parent elements (for icon buttons etc.)
    if (!isSave && el.closest) {
        let btn = el.closest('button, input');
        if (btn) {
            let id = (btn.id || '').toLowerCase();
            let name = (btn.name || '').toLowerCase();
            let cls = (btn.className || '').toLowerCase();
            if (id.includes('save') || name.includes('save') || cls.includes('save')) {
                isSave = true;
                el = btn; // Use the button/input as the element
            }
        }
    }
    // Removed debug logging and diagnostic alert for production cleanup
    // (No action needed on save button press)
});
// Style fix for Raw Power Out label
document.addEventListener('DOMContentLoaded', function () {
    var rawPowerLabel = document.getElementById('raw-powerout-label');
    if (rawPowerLabel) {
        rawPowerLabel.style.removeProperty('max-width');
        rawPowerLabel.style.minWidth = '120px';
        rawPowerLabel.style.removeProperty('width');
        rawPowerLabel.style.whiteSpace = 'nowrap';
        rawPowerLabel.style.textAlign = 'right';
        rawPowerLabel.style.fontFamily = 'monospace';
        rawPowerLabel.style.display = 'inline-block';
        rawPowerLabel.style.marginLeft = '12px';
    }

    // --- SignalR connection setup and disconnect on page unload ---
    if (window.signalRConnection === undefined) {
        window.signalRConnection = window.ywcHubConnection("/radioHub");
        window.signalRConnection.start().then(function () {
            window.signalRConnection.invoke("Heartbeat").catch(function () { });
        }).catch(function (err) { });
        // Heartbeat: send every 5 seconds
        window.signalRHeartbeatInterval = setInterval(function () {
            if (window.signalRConnection && window.signalRConnection.invoke) {
                window.signalRConnection.invoke("Heartbeat").catch(function (err) {
                    // Ignore errors if connection is closed
                });
            }
        }, 5000);
    }
    // Stop heartbeat connection only when the tab is actually closing/navigating away.
    // visibilitychange (tab switch, minimise) must NOT stop it — that fired the 30-second
    // shutdown timer whenever the user alt-tabbed, causing ERR_CONNECTION_REFUSED.
    function _stopHeartbeat() {
        if (window.signalRConnection && window.signalRConnection.stop) {
            window.signalRConnection.stop();
        }
        if (window.signalRHeartbeatInterval) {
            clearInterval(window.signalRHeartbeatInterval);
        }
    }
    window.addEventListener('unload', _stopHeartbeat);
    window.addEventListener('beforeunload', _stopHeartbeat);
});
// FTdx101 Web App - site.js
// =============================================================================
// This file has two main sections:
//
//  1. A small block of globals (lines ~1-400) that were written early in the
//     project: the outer `state`, outer `fetchRadioStatus`, outer SignalR
//     handler, and the outer pollInitStatus / DOMContentLoaded wiring.
//
//  2. An IIFE (Immediately Invoked Function Expression) block that contains the
//     full, authoritative implementation: its own inner `state`, all the real
//     polling logic, highlightButtons, gauge init, etc.
//
// The outer globals are kept because the Razor pages call window.setBand,
// window.setMode, window.setAntenna, and window.radioControl directly via
// inline onchange="..." attributes, and the IIFE overwrites window.radioControl
// at the end with the real implementations.
//
// THE BUG THAT WAS FIXED:
// When the radio itself changed mode (e.g. the user turned the MODE knob on
// the front panel), the backend sent a SignalR "RadioStateUpdate" with
// property="ModeA" / "ModeB".  The handler only updated the modeDisplayA/B
// <span> element (the text label under the buttons), but never set .checked
// on the corresponding <input type="radio"> button.  So the text changed but
// the selected button did not move.
//
// Fix is in the first SignalR handler (~line 300) and the second one
// (~line 1017): both now call updateModeRadioButton() which sets .checked
// on the matching input[name="modeA/B"] element.
// =============================================================================

// ---------------------------------------------------------------------------
// OUTER GLOBALS
// These exist because the Razor page's inline onchange handlers fire before
// the IIFE runs, so window.setBand / setMode / setAntenna must be defined
// at global scope.  The IIFE later replaces window.radioControl with its
// own (better) versions.
// ---------------------------------------------------------------------------



// Debounce timers for aria attribute updates — one per VFO (A/B).
// Visual updates (innerHTML) happen immediately; screen-reader attributes
// are only written after 500 ms of no further changes so the reader
// announces the final frequency rather than every scroll-wheel step.
// Bumped from 300 ms (2026-06-14) — OZ1JTE on #20 reported still hearing
// intermediate frequencies during rapid wheel scrolling. The visible
// digit spans are aria-hidden so the spinbutton's accessible value comes
// only from aria-valuenow, which this debounce gates.
const _ariaDebounceTimers = {};


function renderFrequencyDigits(freq, selIdx) {
    // Show dashes if no valid frequency yet
    if (!freq || isNaN(freq) || freq < 100) {
        return '<span class="digit" aria-hidden="true">-</span><span class="digit" aria-hidden="true">-</span>.<span class="digit" aria-hidden="true">-</span><span class="digit" aria-hidden="true">-</span><span class="digit" aria-hidden="true">-</span>.<span class="digit" aria-hidden="true">-</span><span class="digit" aria-hidden="true">-</span><span class="digit" aria-hidden="true">-</span>';
    }
    let s = freq.toString().padStart(8, "0");
    let html = "";
    let digitIdx = 0;
    for (let i = 0; i < 8; i++) {
        if (i === 2 || i === 5) {
            html += '<span class="digit" aria-hidden="true">.</span>';
        }
        let selected = (selIdx === digitIdx) ? " selected" : "";
        html += `<span class="digit${selected}" aria-hidden="true" tabindex="-1">${s[i]}</span>`;
        digitIdx++;
    }
    return html;
}


// Outer band setter - called from Razor inline onchange on band buttons
window.setBand = async function (receiver, band) {
    try {
        if (window.highlightButtons) highlightButtons(receiver, band, state.lastMode ? state.lastMode[receiver] : undefined, state.lastAntenna ? state.lastAntenna[receiver] : undefined);
        if (state.lastBand) state.lastBand[receiver] = band;
        const response = await fetch(`/api/cat/band/${receiver.toLowerCase()}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ band })
        });
        // No debug logging
    } catch (error) {
        // No debug logging
    }
};

// Quick Memory Bank Store / Recall (Thomas OZ1JTE request). Global radio
// function — the backend sends QI; (store) or QR; (recall). A recall changes
// the radio's frequency/mode, which flows back to the UI via auto-info or the
// FA/FB poll, so there's nothing to update here beyond an accessible status
// announcement (Thomas is a screen-reader user).
// Announce into the aria-live status span. Clearing first, then setting on the
// next frame, forces a screen reader to re-read even when the message is
// identical to last time — Recall sends the same text on every press as the user
// steps through QMB slots, and without this they'd hear nothing after the first.
function qmbAnnounce(status, message) {
    if (!status) return;
    status.textContent = '';
    requestAnimationFrame(() => { status.textContent = message; });
}
async function qmbSend(action, announce) {
    const status = document.getElementById('qmbStatus');
    try {
        const response = await fetch(`/api/cat/qmb/${action}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' }
        });
        qmbAnnounce(status, response.ok ? announce : 'QMB command failed');
    } catch (error) {
        qmbAnnounce(status, 'QMB command failed');
    }
}
window.qmbStore  = function () { return qmbSend('store',  'Stored to Quick Memory Bank'); };
window.qmbRecall = function () { return qmbSend('recall', 'Recalled from Quick Memory Bank'); };
window.qmbVfo    = function () { return qmbSend('vfo',    'Returned to VFO mode'); };

// Outer mode setter - called from Razor inline onchange on mode select
window.setMode = async function (receiver, mode) {
    const modeToCatCode = {
        "LSB": "1", "USB": "2", "CW-U": "3", "FM": "4", "AM": "5", "RTTY-L": "6", "CW-L": "7", "DATA-L": "8", "RTTY-U": "9", "DATA-FM": "A", "FM-N": "B", "DATA-U": "C", "AM-N": "D", "PSK": "E", "DATA-FM-N": "F"
    };
    const catCode = modeToCatCode[mode];
    if (!catCode) {
        return;
    }
    const response = await fetch(`/api/cat/mode/${receiver.toLowerCase()}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ mode: catCode })
    });
    // No debug logging
};

// Outer antenna setter - called from Razor inline onchange on antenna buttons
window.setAntenna = async function (receiver, antenna) {
    try {
        if (window.highlightButtons) highlightButtons(receiver, state.lastBand ? state.lastBand[receiver] : undefined, state.lastMode ? state.lastMode[receiver] : undefined, antenna);
        if (state.lastAntenna) state.lastAntenna[receiver] = antenna;
        const response = await fetch(`/api/cat/antenna/${receiver.toLowerCase()}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ antenna })
        });
        // No debug logging
    } catch (error) {
        // No debug logging
    }
};

// Centralised radio -> max-power mapping. Source of truth used by both
// updatePowerSliderMax implementations. Without this, only the two FTdx101
// variants were named explicitly and other 100 W radios (FTdx10, FT-710,
// FTDX3000, FT-991A) fell through to a 200 W cap (#37, SP3L-Jacek 2026-06-16).
function modelMaxPower(model) {
    if (!model) return 200;
    switch (model.toLowerCase()) {
        case "ftdx101mp": return 200;
        case "ftdx101d":
        case "ftdx10":
        case "ft-710":
        case "ftdx3000":
        case "ft-991a":
            return 100;
        default: return 200;
    }
}
window.modelMaxPower = modelMaxPower;

// Server-rendered radio model. window._radioModel is assigned later inside
// Index.cshtml's module block, so DOMContentLoaded in this file runs first
// and must not rely on it alone (be48971 regression — FTdx10 slider stuck at 200 W).
function getConfiguredRadioModel() {
    return window._radioModel
        || (window.state && window.state.radioModel)
        || document.getElementById('vfoRow')?.dataset?.radioModel
        || null;
}
window.getConfiguredRadioModel = getConfiguredRadioModel;

// Rated TX output for the configured radio. Prefer the figure the server
// rendered into #vfoRow: it comes from RadioCapabilities.MaxPowerWatts, which
// is the same value CatController.SetPower validates against, so the slider
// cannot offer a wattage the API will reject. modelMaxPower is the fallback for
// pages that don't render the VFO row.
function configuredMaxPower(fallback) {
    const rendered = Number(document.getElementById('vfoRow')?.dataset?.maxPower);
    if (Number.isFinite(rendered) && rendered > 0) return rendered;
    const model = getConfiguredRadioModel();
    if (model) return modelMaxPower(model);
    return typeof fallback === "number" ? fallback : 200;
}
window.configuredMaxPower = configuredMaxPower;

function syncRadioModel(model) {
    if (!model) return;
    window.state = window.state || {};
    window.state.radioModel = model;
    if (window.radioControl && window.radioControl._state) {
        window.radioControl._state.radioModel = model;
    }
}

// Outer power control max updater
function updatePowerSliderMax(maxPower) {
    const actualMax = configuredMaxPower(maxPower);
    window.powerCycleButton?.setState({ min: 5, max: actualMax }, { silent: true });
}

// True while the radio is transmitting and therefore no longer measuring
// received signal. Read by updateSMeter() — see the comment there.
let sMetersFrozenByTx = false;

// TX state updater - updates TX button and meters
function updateTxIndicators(isTransmitting) {
    if (window.radioControl && window.radioControl._state) {
        window.radioControl._state.isTransmitting = isTransmitting;
    }
    // Grey the S-meters for the duration of the over. The radio latches SM0
    // and SM1 at key-down and holds them until release (measured — see the
    // .meters-tx-dim comment in Index.cshtml), so they are not readings.
    sMetersFrozenByTx = !!isTransmitting;
    document.getElementById('meterGaugesRow')
        ?.classList.toggle('meters-tx-dim', sMetersFrozenByTx);
    // VFO linear S-meters live outside #meterGaugesRow — dim them the same way.
    document.querySelectorAll('[data-linear-smeter]')
        .forEach(el => el.classList.toggle('meters-tx-dim', sMetersFrozenByTx));
    if (window.ftdx101Meters) {
        window.ftdx101Meters.setTransmitting(isTransmitting);
    }
    if (!isTransmitting) {
        // Force gauges to zero immediately when TX stops, without waiting for
        // the next backend broadcast (~200 ms away at the default poll interval).
        if (window.meterPanel) {
            window.meterPanel.update('power', 0);
            window.meterPanel.update('swr', 0);
            window.meterPanel.update('compression', 0);
            window.meterPanel.update('alc', 0);
            window.meterPanel.update('idd', 0);
        }
        updateMeterDomLabel('PowerMeter',       { skip: false, displayValue: { watts: 0, rawAvg: 0 } });
        updateMeterDomLabel('SWRMeter',         { skip: false, displayValue: { swr: 1.0 } });
        updateMeterDomLabel('CompressionMeter', { skip: false, displayValue: { db: 0 } });
        updateMeterDomLabel('ALCMeter',         { skip: false, displayValue: { percent: 0, alcVolts: 0, rawValue: 0 } });
        updateMeterDomLabel('IDDMeter',         { skip: false, displayValue: { amps: 0 } });
    }
}

// Update DOM labels for a single meter using the result from ftdx101Meters.handleMeterUpdate().
// Formatting is done here (UI layer) — the orchestrator returns plain numeric values.
function updateMeterDomLabel(property, result) {
    if (!result || result.skip) return;
    const dv = result.displayValue;
    switch (property) {
        case 'PowerMeter': {
            const formatted = window.MeterFormatters.powerOverlay(dv.watts);
            const el = document.getElementById('powerMeterValue');
            if (el) el.textContent = formatted;
            // Compact linear sibling readout (freestanding — includes unit).
            const linEl = document.getElementById('powerLinearValue');
            if (linEl) linEl.textContent = window.MeterFormatters.powerLabel(dv.watts);
            const rawEl = document.getElementById('raw-powerout-label');
            if (rawEl) rawEl.textContent = 'Raw Power Out: ' + Math.round(dv.rawAvg);
            const canvas = document.getElementById('powerMeterCanvas');
            if (canvas) canvas.dataset.reading = formatted;
            const linCanvas = document.getElementById('powerLinearCanvas');
            if (linCanvas) linCanvas.dataset.reading = window.MeterFormatters.powerLabel(dv.watts);
            break;
        }
        case 'SWRMeter': {
            const offScale = window.MeterFormatters.swrIsOffScale(dv.swr);
            const formatted = window.MeterFormatters.swr(dv.swr);
            const el = document.getElementById('swrMeterValue');
            if (el) {
                el.textContent = formatted;
                // The badge is the <div> the gauge builds around this span
                // (gauge.js, gaugeTitle block). Its background is set inline
                // there, so it has to be overridden inline here — a CSS class
                // would lose to the inline style.
                const badge = el.parentElement;
                if (badge) {
                    badge.style.background = offScale ? '#ffc107' : '#dc3545';
                    badge.style.color      = offScale ? '#000000' : '#ffffff';
                }
            }
            // Compact linear sibling readout — same formatted text + off-scale colour.
            const linEl = document.getElementById('swrLinearValue');
            if (linEl) {
                linEl.textContent = formatted;
                linEl.style.background = offScale ? '#ffc107' : '#dc3545';
                linEl.style.color      = offScale ? '#000000' : '#ffffff';
            }
            const canvas = document.getElementById('swrMeterCanvas');
            if (canvas) {
                canvas.dataset.reading = window.MeterFormatters.swrAnnouncement(dv.swr);
                canvas.dataset.offScale = offScale ? 'true' : 'false';
            }
            const linCanvas = document.getElementById('swrLinearCanvas');
            if (linCanvas) {
                linCanvas.dataset.reading = window.MeterFormatters.swrAnnouncement(dv.swr);
                linCanvas.dataset.offScale = offScale ? 'true' : 'false';
            }
            break;
        }
        case 'CompressionMeter': {
            const formatted = window.MeterFormatters.compressionOverlay(dv.db);
            const el = document.getElementById('compressionMeterValue');
            if (el) el.textContent = formatted;
            const linEl = document.getElementById('compressionLinearValue');
            if (linEl) linEl.textContent = `${formatted} dB`;
            const canvas = document.getElementById('compressionMeterCanvas');
            if (canvas) canvas.dataset.reading = formatted;
            const linCanvas = document.getElementById('compressionLinearCanvas');
            if (linCanvas) linCanvas.dataset.reading = `${formatted} dB`;
            break;
        }
        case 'ALCMeter': {
            const el  = document.getElementById('alcValue');
            const bar = document.getElementById('alcBar');
            const meterEl = document.getElementById('alcMeterValue');
            const alcFormatted = window.MeterFormatters.alcVolts(dv.alcVolts);
            if (el) el.textContent = window.MeterFormatters.percent(dv.percent);
            if (bar) {
                bar.style.width = `${dv.percent}%`;
                bar.setAttribute('aria-valuenow', dv.percent);
                bar.className = 'progress-bar';
                if (dv.percent < 70)      bar.classList.add('bg-success');
                else if (dv.percent < 90) bar.classList.add('bg-warning');
                else                      bar.classList.add('bg-danger');
            }
            if (meterEl) meterEl.textContent = alcFormatted;
            const linEl = document.getElementById('alcLinearValue');
            if (linEl) linEl.textContent = alcFormatted;
            const alcCanvas = document.getElementById('alcMeterCanvas');
            if (alcCanvas) alcCanvas.dataset.reading = alcFormatted;
            const linCanvas = document.getElementById('alcLinearCanvas');
            if (linCanvas) linCanvas.dataset.reading = alcFormatted;
            break;
        }
        case 'IDDMeter': {
            const formatted = window.MeterFormatters.iddOverlay(dv.amps);
            const el = document.getElementById('iddMeterValue');
            if (el) el.textContent = formatted;
            const linEl = document.getElementById('iddLinearValue');
            if (linEl) linEl.textContent = formatted;
            const canvas = document.getElementById('iddMeterCanvas');
            if (canvas) canvas.dataset.reading = formatted;
            const linCanvas = document.getElementById('iddLinearCanvas');
            if (linCanvas) linCanvas.dataset.reading = formatted;
            break;
        }
        case 'VDDMeter': {
            const formatted = window.MeterFormatters.vddOverlay(dv.volts);
            const el = document.getElementById('vddMeterValue');
            if (el) el.textContent = formatted;
            const linEl = document.getElementById('vddLinearValue');
            if (linEl) linEl.textContent = formatted;
            const canvas = document.getElementById('vddMeterCanvas');
            if (canvas) canvas.dataset.reading = formatted;
            const linCanvas = document.getElementById('vddLinearCanvas');
            if (linCanvas) linCanvas.dataset.reading = formatted;
            break;
        }
        case 'Temperature': {
            const formatted = window.MeterFormatters.tempOverlay(dv.tempC);
            const el = document.getElementById('paTemperatureValue');
            if (el) el.textContent = formatted;
            const linEl = document.getElementById('tempLinearValue');
            if (linEl) linEl.textContent = formatted;
            const canvas = document.getElementById('tempMeterCanvas');
            if (canvas) canvas.dataset.reading = formatted;
            const linCanvas = document.getElementById('tempLinearCanvas');
            if (linCanvas) linCanvas.dataset.reading = formatted;
            break;
        }
    }
}

// Outer power setter (stub - real version is inside the IIFE)
async function setPower(receiver, watts) {
    const power = parseInt(watts);
    try {

        const response = await fetch(`/api/cat/power/${receiver.toLowerCase()}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ Watts: power })
        });
        if (!response.ok) {

        } else {

        }
        updatePowerDisplay(receiver, power);
    } catch (error) {

    }
}

// Placeholder - replaced by the IIFE's real implementation once it runs
window.updatePowerDisplay = function(receiver, watts) {
    window.powerCycleButton?.setState({ value: Number(watts) }, { silent: true });
};

// ---------------------------------------------------------------------------
// Radio Power On/Off Toggle
// ---------------------------------------------------------------------------
let radioPowerOn = true; // Track radio power state

async function toggleRadioPower() {
    const btn = document.getElementById('radioPowerBtn');
    if (!btn) return;

    // Disable button during operation
    btn.disabled = true;
    btn.innerHTML = '<span class="spinner-border spinner-border-sm"></span> POWER';

    try {
        const newPowerState = !radioPowerOn;


        const response = await fetch('/api/cat/radiopower', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ powerOn: newPowerState })
        });

        if (response.ok) {
            const data = await response.json();
            radioPowerOn = data.powerOn;
            updateRadioPowerButton();

        } else {

        }
    } catch (error) {

    } finally {
        btn.disabled = false;
        updateRadioPowerButton();
    }
}

function updateRadioPowerButton() {
    const btn = document.getElementById('radioPowerBtn');
    if (!btn) return;

    if (radioPowerOn) {
        btn.className = 'btn btn-success btn-sm';
        btn.innerHTML = '<i class="bi bi-power" aria-hidden="true"></i> POWER';
        btn.title = 'Radio is ON - Click to turn OFF';
    } else {
        btn.className = 'btn btn-danger btn-sm';
        btn.innerHTML = '<i class="bi bi-power" aria-hidden="true"></i> POWER';
        btn.title = 'Radio is OFF - Click to turn ON';
    }
}

// Check radio power status on page load
async function checkRadioPowerStatus() {
    try {
        const response = await fetch('/api/cat/radiopower');
        if (response.ok) {
            const data = await response.json();
            radioPowerOn = data.powerOn;
            updateRadioPowerButton();
        }
    } catch (error) {

    }
}

// Initialize radio power button state on page load
document.addEventListener('DOMContentLoaded', function() {
    checkRadioPowerStatus();
    checkTxStatus();
    // Seed the power control max from the server-rendered radio model.
    syncRadioModel(getConfiguredRadioModel());
    updatePowerSliderMax();
});

// ---------------------------------------------------------------------------
// TX Button Toggle
// ---------------------------------------------------------------------------
let isTransmitting = false;
let txVfo = 0; // 0 = VFO A, 1 = VFO B (the TX VFO — only flips with split)
// activeVfo tracks which VFO is the operating (RX) VFO -- changes when the
// user presses A/B on the radio front panel in normal mode. Distinct from
// txVfo. SP3L Jacek #34 R2 root cause was that the normal-mode greying
// logic was watching txVfo (which doesn't change on A/B normal-mode swap)
// instead of activeVfo.
let activeVfo = 0;

// Sync TX PTT with the Remote Audio pop-out (same BroadcastChannel).
const TX_SYNC_CHANNEL = 'ywc-remote-audio';
let _txSyncChannel = null;
function getTxSyncChannel() {
    if (_txSyncChannel) return _txSyncChannel;
    try {
        if (typeof BroadcastChannel === 'undefined') return null;
        _txSyncChannel = new BroadcastChannel(TX_SYNC_CHANNEL);
        _txSyncChannel.onmessage = (ev) => {
            const msg = ev.data;
            if (!msg || typeof msg !== 'object') return;
            if (msg.type === 'txState' && typeof msg.transmitting === 'boolean') {
                if (!!msg.transmitting === isTransmitting) return;
                applySharedTxState(msg.transmitting);
                return;
            }
            if (msg.type === 'requestTxState')
                publishTxState();
        };
    } catch {
        _txSyncChannel = null;
    }
    return _txSyncChannel;
}
function effectiveTxVfo() {
    const vfoRow = document.getElementById('vfoRow');
    const isSingleReceiver = vfoRow?.dataset.singleReceiver === 'true';
    if (isSingleReceiver) {
        return (splitMode > 0)
            ? (activeVfo === 0 ? 1 : 0)
            : activeVfo;
    }
    return txVfo;
}
function publishTxState() {
    try {
        getTxSyncChannel()?.postMessage({
            type: 'txState',
            transmitting: isTransmitting,
            txVfo: effectiveTxVfo()
        });
    } catch { /* ignore */ }
}
function applySharedTxState(transmitting, _sharedTxVfo) {
    // Index owns VFO selection via SignalR; only PTT on/off syncs from pop-out.
    isTransmitting = !!transmitting;
    updateTxButton();
    updateTxIndicators(isTransmitting);
    if (typeof window.handleTxStateForTimeout === 'function')
        window.handleTxStateForTimeout(isTransmitting);
}
window.publishTxState = publishTxState;
window.applySharedTxState = applySharedTxState;
document.addEventListener('DOMContentLoaded', () => { getTxSyncChannel(); });

// Dual-receiver (FTdx101): highlight which band is active — the MAIN/SUB
// band the main tuning knob controls — with .vfo-active, driven by
// activeVfo (VS: 0 = MAIN/A, 1 = SUB/B). The radio auto-broadcasts VS when
// you press MAIN⇄SUB-select on the front panel, so this follows live.
// Single-receiver radios do not grey or lock either panel; both stay fully
// editable — except the per-VFO linear S-meter, which greys on the inactive
// VFO (only one receiver is actually measuring). Clears any leftover
// .vfo-inactive / .vfo-tx-editable from earlier builds.
// See docs/decisions/0003-single-vs-dual-receiver-ui.md.
function applyVfoActiveStyling() {
    const vfoRow = document.getElementById('vfoRow');
    if (!vfoRow) return;
    const aCol = document.getElementById('vfoACol');
    const bCol = document.getElementById('vfoBCol');
    if (!aCol || !bCol) return;

    aCol.classList.remove('vfo-inactive', 'vfo-tx-editable');
    bCol.classList.remove('vfo-inactive', 'vfo-tx-editable');
    document.getElementById('spectrumContainerA')?.classList.remove('vfo-inactive');
    document.getElementById('spectrumContainerB')?.classList.remove('vfo-inactive');

    const smA = document.getElementById('sMeterLinearRowA');
    const smB = document.getElementById('sMeterLinearRowB');
    const singleReceiver = vfoRow.dataset.singleReceiver === 'true';
    if (singleReceiver) {
        // No active-band amber ring on single-receiver — RX/TX selectors
        // already show which VFO is receiving / transmitting.
        aCol.classList.remove('vfo-active');
        bCol.classList.remove('vfo-active');
        // Only one physical S-meter: grey the inactive VFO's linear face.
        smA?.classList.toggle('linear-smeter-inactive', activeVfo !== 0);
        smB?.classList.toggle('linear-smeter-inactive', activeVfo !== 1);
        return;
    }

    aCol.classList.toggle('vfo-active', activeVfo === 0);
    bCol.classList.toggle('vfo-active', activeVfo === 1);
    // Dual-receiver: both S-meters are live (SM0/SM1).
    smA?.classList.remove('linear-smeter-inactive');
    smB?.classList.remove('linear-smeter-inactive');
}

// Apply the styling at page-load time too, before any SignalR update has
// arrived. This handles the case where the radio is already on a stable
// VFO and YWC's TxVfo state is correct by the time the DOM is ready.
document.addEventListener('DOMContentLoaded', () => {
    // Defer to next tick so other DOMContentLoaded handlers run first
    // (the VFO panels need to be in the DOM, which they always are at
    // this point — but the txVfo global may not have been set from
    // server state yet, in which case the default 0 applies and gets
    // corrected by the first SignalR update).
    setTimeout(applyVfoActiveStyling, 0);
});
let splitMode = 0; // 0 = OFF, 1 = ON (VFO A=RX / VFO B=TX), 2 = ON+5kHz Quick Split

let clarVfo = 'A';
let clarOffsets = { A: 0, B: 0 };
let rxClarOn = false;
let txClarOn = false;

let contourState = { A: { on: false, freqHz: 800 }, B: { on: false, freqHz: 800 } };
let apfState     = { A: { on: false, freqHz: 0   }, B: { on: false, freqHz: 0   } };

async function toggleTx() {
    const newTxState = !isTransmitting;


    try {
        const response = await fetch('/api/cat/tx', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ transmit: newTxState })
        });

        if (response.ok) {
            const data = await response.json();
            isTransmitting = data.transmitting;
            updateTxButton();
            updateTxIndicators(isTransmitting);
            publishTxState();
        } else {

        }
    } catch (error) {

    }
}

function updateTxButton() {
    const btn = document.getElementById('txButton');
    if (!btn) return;

    // TX button position rules:
    //
    //  Single-receiver normal mode: TX VFO IS the active (RX) VFO.
    //    Pressing A/B on the front panel changes which VFO will key, but the
    //    FT command stays at 0. Position by activeVfo. (Pre3 fix.)
    //
    //  Single-receiver split mode: the TX VFO is the OPPOSITE of the active
    //    (RX) VFO -- the radio receives on activeVfo and transmits on the
    //    other one. FT often doesn't move on FTdx10 when split engages, so
    //    don't rely on txVfo here. Pre7 fix (Jacek SP3L #34, pre6 follow-up
    //    "TX button stays on white panel after split enabled").
    //
    //  Dual-receiver (any mode): txVfo (FT command) is reliable. Use it.
    //
    const vfoRow = document.getElementById('vfoRow');
    const isSingleReceiver = vfoRow?.dataset.singleReceiver === 'true';
    let positionVfo;
    if (isSingleReceiver) {
        positionVfo = (splitMode > 0)
            ? (activeVfo === 0 ? 1 : 0)   // split: TX = opposite of active
            : activeVfo;                   // normal: TX = active
    } else {
        positionVfo = txVfo;
    }

    const txVfoLabel = positionVfo === 0 ? 'A' : 'B';
    document.getElementById('receiverAHeading').innerHTML =
        positionVfo === 0
            ? 'VFO A - <i class="bi bi-broadcast ms-1" title="Transmit VFO" aria-label="Transmit VFO"></i>'
            : 'VFO A';
    document.getElementById('receiverBHeading').innerHTML =
        positionVfo === 1
            ? 'VFO B - <i class="bi bi-broadcast ms-1" title="Transmit VFO" aria-label="Transmit VFO"></i>'
            : 'VFO B';
    btn.querySelector('.toggle-dd__led')
        ?.classList.toggle('is-on', isTransmitting);
    btn.querySelector('.toggle-dd__label').innerHTML =
        `<i class="bi bi-broadcast me-1" aria-hidden="true"></i>TX ${txVfoLabel}`;
    btn.title = isTransmitting
        ? `Click to stop transmitting on VFO ${txVfoLabel}`
        : `Click to transmit on VFO ${txVfoLabel}`;
    btn.setAttribute(
        'aria-label',
        isTransmitting
            ? `Stop transmitting on VFO ${txVfoLabel}`
            : `Start transmitting on VFO ${txVfoLabel}`
    );
}

function updateSplitButton() {
    const btn        = document.getElementById('splitBtn');
    const badgeA     = document.getElementById('splitTxBadgeA');
    const badgeB     = document.getElementById('splitTxBadgeB');
    const vfoACard   = document.querySelector('#vfoACol .card');
    const vfoBCard   = document.querySelector('#vfoBCol .card');
    const vfoRow     = document.getElementById('vfoRow');
    const isSingleReceiver = vfoRow?.dataset.singleReceiver === 'true';
    const active     = splitMode > 0;

    if (btn) {
        btn.classList.toggle('btn-danger', active);
        btn.querySelector('.toggle-dd__label').textContent = active ? 'Split ON' : 'Split';
    }

    // R8: the SPLIT TX badge belongs on the TX VFO's header. On
    // single-receiver radios the TX VFO is the OPPOSITE of the active VFO;
    // on dual-receiver radios it's whichever VFO the FT command points to.
    // Show one badge, hide the other.
    let txVfoIdx;
    if (isSingleReceiver) {
        txVfoIdx = (activeVfo === 0) ? 1 : 0;   // opposite of RX
    } else {
        txVfoIdx = txVfo;
    }
    if (badgeA) badgeA.style.display = (active && txVfoIdx === 0) ? 'inline-block' : 'none';
    if (badgeB) badgeB.style.display = (active && txVfoIdx === 1) ? 'inline-block' : 'none';

    // Card border colour: red on the TX VFO when split is on, green on the
    // other one. Pre-fix this only ever touched #vfoBCol, so when VFO-B
    // was the active RX the colours ended up on the wrong card
    // (Jacek SP3L #34 R7 fail 2026-06-21).
    const txCard    = (txVfoIdx === 0) ? vfoACard : vfoBCard;
    const otherCard = (txVfoIdx === 0) ? vfoBCard : vfoACard;
    if (txCard) {
        txCard.classList.toggle('border-danger', active);
        txCard.classList.toggle('border-success', !active);
    }
    if (otherCard) {
        // The non-TX card is never red; clear any leftover from a previous
        // split state where this card WAS the TX one.
        otherCard.classList.remove('border-danger');
        otherCard.classList.toggle('border-success', !active);
    }
}

// Independent RX / TX VFO selectors (single-receiver radios, #78). RX follows
// activeVfo (VS / FR). TX must use effectiveTxVfo() — the same rule as the
// Index TX button — because on FTdx10 / FT-710 the FT register often stays
// at 0 when the operating VFO moves (front-panel A/B or RX selector), so
// raw txVfo would leave TX stuck on A and falsely light split-red.
// Colours match Yaesu front-panel convention: green = receiving, red = transmitting.
function updateRxTxSelectors() {
    const rxA = document.getElementById('rxVfoA');
    if (!rxA) return; // group only rendered on single-receiver radios
    const pick = (el, on, onClass) => {
        if (!el) return;
        el.classList.remove('btn-secondary', 'btn-success', 'btn-danger', 'btn-outline-secondary');
        el.classList.add(on ? onClass : 'btn-outline-secondary');
    };
    pick(rxA, activeVfo === 0, 'btn-success');
    pick(document.getElementById('rxVfoB'), activeVfo === 1, 'btn-success');
    const effTx = effectiveTxVfo();
    pick(document.getElementById('txVfoA'), effTx === 0, 'btn-danger');
    pick(document.getElementById('txVfoB'), effTx === 1, 'btn-danger');
}

async function setRxVfo(vfo) {
    try {
        const r = await fetch(`/api/cat/rx-vfo/${vfo}`, { method: 'POST' });
        if (r.ok) {
            const d = await r.json();
            activeVfo = d.rxVfo;
            if (typeof d.txVfo === 'number') txVfo = d.txVfo;
            if (typeof d.splitMode === 'number') splitMode = d.splitMode;
            updateRxTxSelectors();
            updateSplitButton();
            applyVfoActiveStyling();
            updateTxButton();
        }
    } catch {}
}

async function setTxVfo(vfo) {
    try {
        const r = await fetch(`/api/cat/tx-vfo/${vfo}`, { method: 'POST' });
        if (r.ok) {
            const d = await r.json();
            txVfo = d.txVfo;
            if (typeof d.splitMode === 'number') splitMode = d.splitMode;
            updateRxTxSelectors();
            updateSplitButton();
            applyVfoActiveStyling();
            updateTxButton();
        }
    } catch {}
}

async function setSplit(mode) {
    try {
        const r = await fetch(`/api/cat/split/${mode}`, { method: 'POST' });
        if (r.ok) {
            const data = await r.json();
            splitMode = data.splitMode;
            updateSplitButton();
            // R7: greying flips between normal and split — refresh after the
            // local toggle even though the radio will also auto-info-broadcast
            // SplitMode and trigger applyVfoActiveStyling that way (covers the
            // window before the broadcast arrives).
            applyVfoActiveStyling();
        }
    } catch {}
}

async function swapVfo() {
    try {
        await fetch('/api/cat/swap-vfo', { method: 'POST' });
        // FrequencyA/B updates arrive via SignalR; the endpoint also broadcasts immediately
    } catch {}
}

// Dual-receiver only: make a VFO the active (MAIN/SUB) band by sending VS.
// The ActiveVfo update arrives via SignalR and moves the highlight; setting
// it server-side too avoids flicker.
async function setActiveVfo(vfo) {
    try {
        await fetch(`/api/cat/active-vfo/${vfo}`, { method: 'POST' });
    } catch {}
}

async function copyVfo(direction) {
    try {
        await fetch(`/api/cat/copy-vfo/${direction}`, { method: 'POST' });
    } catch {}
}
window.copyVfo = copyVfo;
window.swapVfo = swapVfo;
window.setActiveVfo = setActiveVfo;
window.setRxVfo = setRxVfo;
window.setTxVfo = setTxVfo;
window.getActiveVfoLetter = function () {
    return activeVfo === 1 ? 'B' : 'A';
};

async function checkTxStatus() {
    try {
        const response = await fetch('/api/cat/tx');
        if (response.ok) {
            const data = await response.json();
            isTransmitting = data.transmitting;
            txVfo = data.txVfo;
            updateTxButton();
        }
    } catch (error) {

    }
}

// ---------------------------------------------------------------------------
// SignalR connection - shared by both the outer handler below and the
// second handler at the bottom of the file (after the IIFE).
// ---------------------------------------------------------------------------
const connection = window.ywcHubConnection("/radioHub");

// Redirect to Settings page if the backend signals an init failure
connection.on("ShowSettingsPage", function () {
    window.location.href = "/Settings";
});

// "Reading radio settings…" overlay shown during single-receiver ping-pong
// (see RadioInitializationService.cs comments around the VS swap block).
// Backend broadcasts a status string; empty string means clear/hide.
connection.on("RadioInfoStatus", function (message) {
    let overlay = document.getElementById('radioInfoStatusOverlay');
    if (!message) {
        if (overlay) overlay.style.display = 'none';
        return;
    }
    if (!overlay) {
        overlay = document.createElement('div');
        overlay.id = 'radioInfoStatusOverlay';
        overlay.setAttribute('role', 'status');
        overlay.setAttribute('aria-live', 'polite');
        overlay.style.cssText = [
            'position: fixed',
            'top: 1rem',
            'left: 50%',
            'transform: translateX(-50%)',
            'z-index: 9999',
            'background: rgba(20, 24, 32, 0.92)',
            'color: #fff',
            'padding: 0.75rem 1.25rem',
            'border-radius: 0.5rem',
            'font-size: 0.95rem',
            'box-shadow: 0 4px 16px rgba(0, 0, 0, 0.35)',
            'pointer-events: none'
        ].join(';');
        document.body.appendChild(overlay);
    }
    overlay.textContent = message;
    overlay.style.display = '';
});


// An external program (WSJT-X, a logger, anything on rigctld) asked for a
// frequency this radio cannot tune, so the request was refused and the radio
// stayed where it was.
//
// This explains the refusal; it does not prevent the other program from
// complaining. WSJT-X in particular decides it failed from the rigctld reply
// and never sees this page, so its own "Rig control error" still appears —
// the whole point of this banner is that the operator now knows why.
//
// Deliberately a banner, not a modal: WSJT-X re-sends while it sits on the
// offending band, and a dialog needing dismissal each time would be worse than
// silence — especially with a screen reader. role="alert" (not "status" like
// the overlay above) because this reports a request that did not happen.
connection.on("FrequencyRejected", function (info) {
    if (!info) return;
    const mhz = typeof info.frequencyMhz === 'number'
        ? info.frequencyMhz.toFixed(3)
        : String(info.frequencyHz || '');
    const who = info.source ? ` (requested by ${info.source})` : '';

    let el = document.getElementById('frequencyRejectedBanner');
    if (!el) {
        el = document.createElement('div');
        el.id = 'frequencyRejectedBanner';
        el.setAttribute('role', 'alert');
        el.style.cssText = [
            'position: fixed',
            'top: 1rem',
            'left: 50%',
            'transform: translateX(-50%)',
            'z-index: 9999',
            'background: rgba(120, 78, 8, 0.96)',
            'color: #fff',
            'padding: 0.75rem 1.25rem',
            'border-radius: 0.5rem',
            'border: 1px solid #ffc107',
            'font-size: 0.95rem',
            'max-width: 90vw',
            'text-align: center',
            'box-shadow: 0 4px 16px rgba(0, 0, 0, 0.35)',
            'pointer-events: none'
        ].join(';');
        document.body.appendChild(el);
    }
    el.textContent =
        `Requested frequency not available: ${mhz} MHz is outside this radio's range${who}. ` +
        `The radio has stayed on its current frequency.`;
    el.style.display = '';

    clearTimeout(el._hideTimer);
    el._hideTimer = setTimeout(() => { el.style.display = 'none'; }, 8000);
});




function sMeterLabel(val) {
    return window.calibrationEngine.calibrateSMeterLabel(val);
}


// ---------------------------------------------------------------------------
// BUG FIX: updateModeSelect
// ---------------------------------------------------------------------------
// Updates the mode dropdown select when the mode changes from the radio
// (e.g., via SignalR update or front panel knob change).
// ---------------------------------------------------------------------------
function updateModeSelect(receiver, mode) {
    const widget = window[`modeButton${receiver}`];
    if (widget && mode) {
        widget.setState({ selectedId: String(mode) }, { silent: true });
    }
}

// ---------------------------------------------------------------------------
// updateMicGainLabel
// ---------------------------------------------------------------------------
// Updates the MIC Gain label based on the current mode.
// In DATA modes (DATA-U, DATA-L, PSK, DATA-FM, etc.), this controls Data Out level.
// In voice modes (SSB, AM, FM, etc.), this controls MIC Gain.
// ---------------------------------------------------------------------------
function updateMicGainLabel(mode) {
    // Data modes where "MIC Gain" actually controls Data Out level
    const dataModes = ['DATA-U', 'DATA-L', 'PSK', 'DATA-FM', 'DATA-FM-N', 'RTTY-U', 'RTTY-L'];
    const label = dataModes.includes(mode) ? 'Data Out Gain' : 'MIC Gain';
    window.micGainCycleButton?.setLabel(label);
}

// ---------------------------------------------------------------------------
// CW-only controls — ZIN and APF are meaningful only in CW modes on Yaesu
// hardware. Per-VFO header buttons follow that VFO's mode; the CW Keyer
// popout ZIN follows whichever VFO is currently active (VS).
// ---------------------------------------------------------------------------
const _vfoModeCache = { A: null, B: null };

const _CW_ONLY_DISABLED_TITLE = 'Available in CW mode only';
const _ZIN_TITLES = {
    A: 'CW Auto Zero In on VFO A — nudges the VFO so the received CW signal sits at your configured CW pitch',
    B: 'CW Auto Zero In on VFO B — nudges the VFO so the received CW signal sits at your configured CW pitch',
    active: 'CW Auto Zero In — nudges the active VFO so the received CW signal sits at your preferred pitch',
};

function isCwMode(mode) {
    return mode === 'CW-U' || mode === 'CW-L';
}

function updateCwKeyerZinButton() {
    const btn = document.getElementById('zinBtnActive');
    if (!btn) return;

    const vfoLetter = typeof window.getActiveVfoLetter === 'function'
        ? window.getActiveVfoLetter()
        : 'A';
    const mode = _vfoModeCache[vfoLetter]
        ?? window[`modeButton${vfoLetter}`]?.getState?.()?.selectedId
        ?? null;
    const enabled = isCwMode(mode);

    btn.disabled = !enabled;
    btn.title = enabled ? _ZIN_TITLES.active : _CW_ONLY_DISABLED_TITLE;
}

function updateCwOnlyControls(vfo, mode) {
    const letter = vfo === 'B' ? 'B' : 'A';
    if (mode != null) _vfoModeCache[letter] = mode;

    const enabled = isCwMode(mode);

    const zinBtn = document.getElementById(`zinBtn${letter}`);
    if (zinBtn) {
        zinBtn.disabled = !enabled;
        zinBtn.title = enabled ? _ZIN_TITLES[letter] : _CW_ONLY_DISABLED_TITLE;
    }

    const apfWidget = window[`apfButton${letter}`];
    if (apfWidget?.setDisabled) {
        apfWidget.setDisabled(!enabled);
        if (apfWidget.button) {
            apfWidget.button.title = enabled
                ? 'Right-click for frequency'
                : _CW_ONLY_DISABLED_TITLE;
        }
    }

    updateCwKeyerZinButton();
}
window.updateCwOnlyControls = updateCwOnlyControls;
window.isCwMode = isCwMode;

// ---------------------------------------------------------------------------
// Filter Function Display: host-side spectrum of the radio's RX audio.
//
// The host runs an FFT over the radio's USB RX audio and pushes ~12 frames a
// second to pages that asked for them (SubscribeFilterSpectrum). The frame is
// shaped like the browser AnalyserNode output Remote Audio hands the panel,
// so the panel draws either through the same code; this one is the fallback
// for when Remote Audio is not playing, which on most pages is always. Bins
// arrive base64-encoded because the JSON hub protocol sends byte[] that way.
// ---------------------------------------------------------------------------
const filterSpectrumFeed = {
    latest: null,          // { data: Uint8Array, sampleRate, fftSize, at }
    reasonLogged: false,
    provider() {
        const f = filterSpectrumFeed.latest;
        // A stale frame means the capture stopped (device unplugged, host
        // busy); better an empty passband than a frozen one.
        if (!f || Date.now() - f.at > 1000) return null;
        return f;
    },
    subscribe(attempt) {
        // The panel is built by Index.cshtml's module script on
        // DOMContentLoaded; the hub can be up before that. Give it a moment
        // rather than assume an order. Pages without the panel give up.
        if (!window.filterScopePanelA) {
            attempt = attempt || 0;
            if (attempt < 20) setTimeout(function () { filterSpectrumFeed.subscribe(attempt + 1); }, 250);
            return;
        }
        connection.invoke("SubscribeFilterSpectrum").then(function (reason) {
            if (reason) {
                filterSpectrumFeed.latest = null;
                if (!filterSpectrumFeed.reasonLogged) {
                    console.info("Filter display: no host audio spectrum - " + reason);
                    filterSpectrumFeed.reasonLogged = true;
                }
                return;
            }
            window.filterScopePanelA.setHostSpectrumProvider(filterSpectrumFeed.provider);
        }).catch(function () { /* older host without the hub method */ });
    },
    receive(value) {
        if (!value || typeof value.bins !== 'string') return;
        const raw = atob(value.bins);
        const data = new Uint8Array(raw.length);
        for (let i = 0; i < raw.length; i++) data[i] = raw.charCodeAt(i);
        filterSpectrumFeed.latest = {
            data, sampleRate: value.sampleRate, fftSize: value.fftSize, at: Date.now()
        };
    }
};
// Group membership dies with the connection, so ask again after a reconnect.
connection.onreconnected(function () { filterSpectrumFeed.subscribe(); });

// While a VFO is "editing", the frequency display shows the operator's
// in-progress value instead of what the radio reports, so a poll arriving
// mid-edit can't yank the digits back. Something has to end that, and there
// are two ways it ends, not one:
//
//   1. The radio echoes back the frequency this display sent. The edit landed.
//   2. The radio goes somewhere this display never sent it — the spectrum
//      mouse wheel, the front-panel knob, or another CAT program.
//
// Only (1) existed, so (2) left the display frozen while the rig tuned away
// under it, until the operator happened to click somewhere else on the page.
// Colin hit this on 2026-09-20 bench-testing #168: the radio's own display
// followed the wheel and YWC's did not.
//
// Called before the display update so a change takes effect on the very
// broadcast that revealed it, not the next one.
function reconcileFrequencyEditing(receiver, valueHz) {
    const s = window.radioControl && window.radioControl._state;
    if (!s || !s.editing[receiver]) return;

    // Mid-edit: localFreq is only non-null between a digit step and the
    // settling send, and the operator's value wins for that moment.
    if (s.localFreq[receiver] !== null && s.localFreq[receiver] !== undefined) return;

    if (valueHz === s.lastSentFreq[receiver]) { s.editing[receiver] = false; return; }

    // Somebody else moved the radio. Wait out our own write first: a broadcast
    // already in flight when we sent carries the OLD frequency, and acting on
    // it would show the pre-edit value for a moment before the echo settles —
    // the flip-back this editing flag exists to prevent.
    const SETTLE_MS = 1500;
    if (Date.now() - (s._lastFreqSend[receiver] || 0) > SETTLE_MS) {
        s.editing[receiver]      = false;
        s.lastSentFreq[receiver] = null;
    }
}

// First SignalR RadioStateUpdate handler (outer scope).
// Handles ModeA/B, FrequencyA/B, PowerA/B updates pushed from the backend.
connection.on("RadioStateUpdate", function (update) {

    if (update.property === "FilterSpectrum") {
        filterSpectrumFeed.receive(update.value);
        return;
    }

    // --- SERVER SHUTDOWN ---
    // Sent by SystemTrayService just before the host stops, so the browser
    // tab can replace the stale UI with a clear "server has stopped" notice
    // instead of sitting with a frozen meter. Attempt window.close() as a
    // courtesy — only works for tabs the page itself opened, but harmless
    // when it doesn't.
    if (update.property === "ServerShutdown") {
        try { showServerStoppedOverlay(); } catch (e) { /* best-effort */ }
        // Explicitly tear down the SignalR connection so Kestrel doesn't sit
        // for 5 s waiting for our long-lived hub connection to drain on
        // server-side shutdown. Without this, `_lifetime.StopApplication()`
        // on the server was blocking the WinForms STA thread for ~5 s before
        // returning — a Kestrel "polite drain" issue. Closing the connection
        // here means Kestrel has nothing to drain.
        try { connection.stop(); } catch (e) { /* may already be closed */ }
        try { window.signalRConnection?.stop(); } catch (e) { /* same — different connection */ }
        return;
    }

    // --- CALIBRATION UPDATED ---
    // Server broadcasts this when CalibrationService.Save runs (i.e. someone
    // hit Save Calibration on the Meter Calibration page). All open browser
    // tabs reload their in-memory calibration tables so the meters reflect
    // the new values immediately without a full page reload.
    // Fixes Jacek's #29 follow-up where saved calibration was being ignored
    // until the user pressed F5.
    if (update.property === "CalibrationUpdated") {
        try { window.calibrationEngine?.reload?.(); } catch (e) { /* best-effort */ }
        return;
    }

    // --- CONNECTION STATE ---
    if (update.property === "IsConnected") {
        const connected = update.value === true || update.value === 'true';
        if (typeof window._applyConnectBtnState === 'function') window._applyConnectBtnState(connected);
    }

    // --- MODE CHANGE (THE BUG FIX) ---
    if (update.property === "ModeA") {
        updateModeSelect('A', update.value);
        updateMicGainLabel(update.value);
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ mode: update.value });
        updateContourSliderBounds('A');
        if (typeof window._updateSquelchVisibility === 'function') window._updateSquelchVisibility('A', update.value);
        updateCwOnlyControls('A', update.value);
        if (window.IfWidth && window._radioModel) {
            window.IfWidth.rebuildIfWidthSelect(
                window.ifWidthButtonA, window._radioModel, update.value);
        }
        updateIfShiftForMode('A', update.value);
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('modeA', update.value);
        if (window.voiceAnnounce) window.voiceAnnounce.sayMode('A', update.value);
        if (window.audioFilter && window.audioFilter.onModeChanged) window.audioFilter.onModeChanged('A', update.value);
        if (window.radioControl && window.radioControl._state) window.radioControl._state.lastMode.A = update.value;
    }
    if (update.property === "ModeB") {
        updateModeSelect('B', update.value);
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ mode: update.value });
        updateContourSliderBounds('B');
        if (typeof window._updateSquelchVisibility === 'function') window._updateSquelchVisibility('B', update.value);
        updateCwOnlyControls('B', update.value);
        if (window.IfWidth && window._radioModel) {
            window.IfWidth.rebuildIfWidthSelect(
                window.ifWidthButtonB, window._radioModel, update.value);
        }
        updateIfShiftForMode('B', update.value);
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('modeB', update.value);
        if (window.voiceAnnounce) window.voiceAnnounce.sayMode('B', update.value);
        if (window.audioFilter && window.audioFilter.onModeChanged) window.audioFilter.onModeChanged('B', update.value);
        if (window.radioControl && window.radioControl._state) window.radioControl._state.lastMode.B = update.value;
    }

    // --- ANTENNA CHANGE ---
    // The radio does not auto-broadcast AN changes from the front panel,
    // so MeterPollingService polls AN0/AN1 every couple of seconds and
    // routes the response through the dispatcher → RadioStateService →
    // SignalR. Update the Yaesu-style antenna button directly.
    if (update.property === "AntennaA") {
        const button = window.antennaButtonA;
        if (button) button.setState({ selectedId: String(update.value) }, { silent: true });
        if (window.radioControl && window.radioControl._state) window.radioControl._state.lastAntenna.A = update.value;
    }
    if (update.property === "AntennaB") {
        const button = window.antennaButtonB;
        if (button) button.setState({ selectedId: String(update.value) }, { silent: true });
        if (window.radioControl && window.radioControl._state) window.radioControl._state.lastAntenna.B = update.value;
    }

    // --- PROC ---
    if (update.property === "ProcEnabled") {
        if (typeof window.updateProcButton === 'function') window.updateProcButton(update.value);
    }
    if (update.property === "ProcLevel") {
        const slider = document.getElementById('procLevelSlider');
        const label  = document.getElementById('procLevelValue');
        if (slider) slider.value = update.value;
        if (label)  label.textContent = update.value;
    }

    // --- FREQUENCY CHANGE ---
    //
    // Important: `state` in this scope refers to a variable defined inside an
    // IIFE further down the file (line ~1829) and is NOT visible here.
    // Touching it bare throws ReferenceError and silently aborts the rest of
    // the handler — which is what was breaking the segment-dropdown auto-sync
    // for ages. Wrap every `state.*` access in try/catch so a single failure
    // can't kill the handler. The IIFE's own polling loop keeps the
    // frequency display fresh independently, so losing this write isn't fatal.
    if (update.property === "FrequencyA") {
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('freqHzA', update.value);
        if (window.radioControl && window.radioControl._state) {
            window.radioControl._state.lastBackendFreq.A = update.value;
        }
        // Keep the Band key aligned with Hz. BandA only broadcasts when the
        // band *name* changes, and a SignalR BandA that arrived before the
        // Yaesu key existed is easy to miss — derive from frequency whenever
        // it moves (also heals OOB / stale-persisted band mismatches).
        lastVfoHz.A = update.value;
        try {
            if (typeof window.bandForHz === 'function') {
                const derived = window.bandForHz(update.value);
                if (derived) updateBandButton('A', derived);
                else applyBandOutOfBand('A');
            } else {
                applyBandOutOfBand('A');
            }
        } catch (e) { console.error('band sync A error:', e); }
        try { reconcileFrequencyEditing('A', update.value); } catch (e) { console.error('reconcileFrequencyEditing A error:', e); }
        try { window.updateFrequencyDisplay('A', update.value); } catch (e) { console.error('updateFrequencyDisplay A error:', e); }
        try { window.dispatchEvent(new CustomEvent('radioFrequencyUpdate', { detail: { receiver: 'A', hz: update.value } })); }
        catch (e) { console.error('radioFrequencyUpdate dispatch error:', e); }
        try { if (window.syncSegmentSelectToFrequency) window.syncSegmentSelectToFrequency('A', update.value); }
        catch (e) { console.error('syncSegmentSelectToFrequency A error:', e); }
    }
    if (update.property === "FrequencyB") {
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('freqHzB', update.value);
        if (window.radioControl && window.radioControl._state) {
            window.radioControl._state.lastBackendFreq.B = update.value;
        }
        lastVfoHz.B = update.value;
        try {
            if (typeof window.bandForHz === 'function') {
                const derived = window.bandForHz(update.value);
                if (derived) updateBandButton('B', derived);
                else applyBandOutOfBand('B');
            } else {
                applyBandOutOfBand('B');
            }
        } catch (e) { console.error('band sync B error:', e); }
        try { reconcileFrequencyEditing('B', update.value); } catch (e) { console.error('reconcileFrequencyEditing B error:', e); }
        try { window.updateFrequencyDisplay('B', update.value); } catch (e) { console.error('updateFrequencyDisplay B error:', e); }
        try { window.dispatchEvent(new CustomEvent('radioFrequencyUpdate', { detail: { receiver: 'B', hz: update.value } })); }
        catch (e) { console.error('radioFrequencyUpdate dispatch error:', e); }
        try { if (window.syncSegmentSelectToFrequency) window.syncSegmentSelectToFrequency('B', update.value); }
        catch (e) { console.error('syncSegmentSelectToFrequency B error:', e); }
    }

    // --- BAND CHANGE ---
    if (update.property === "BandA") {
        // ...removed debug logging...
        updateBandButton('A', update.value);
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('bandA', update.value);
        if (window.voiceAnnounce) window.voiceAnnounce.sayBand('A', update.value);
        if (window.radioControl && window.radioControl._state) window.radioControl._state.lastBand.A = update.value;
    }
    if (update.property === "BandB") {
        // ...removed debug logging...
        updateBandButton('B', update.value);
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('bandB', update.value);
        if (window.voiceAnnounce) window.voiceAnnounce.sayBand('B', update.value);
        if (window.radioControl && window.radioControl._state) window.radioControl._state.lastBand.B = update.value;
    }

    // --- POWER CHANGE ---
    // Only handle generic Power (no A/B distinction)
    if (update.property === "PowerA") {
        if (typeof window.updatePowerDisplay === 'function') window.updatePowerDisplay("A", update.value);
        const sliderA = document.getElementById('powerSliderA');
        if (sliderA) sliderA.value = update.value;
    }
    if (update.property === "PowerB") {
        if (typeof window.updatePowerDisplay === 'function') window.updatePowerDisplay("B", update.value);
        const sliderB = document.getElementById('powerSliderB');
        if (sliderB) sliderB.value = update.value;
    }
    if (update.property === "Power") {
        if (typeof window.updatePowerDisplay === 'function') window.updatePowerDisplay("A", update.value);
        window.powerCycleButton?.setState({ value: Number(update.value) }, { silent: true });
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('power', update.value);
    }

    // --- RADIO POWER STATE ---
    if (update.property === "RadioPowerOn") {
        radioPowerOn = update.value;
        updateRadioPowerButton();
    }

    // --- TX STATE ---
    if (update.property === "IsTransmitting") {
        isTransmitting = update.value;
        // Always update the IIFE's state for correct gauge behavior
        if (window.radioControl && window.radioControl._state) {
            window.radioControl._state.isTransmitting = update.value;
            // ...removed debug logging...
        } else {
            // ...removed debug logging...
        }
        updateTxButton();
        updateTxIndicators(update.value);
        publishTxState();
        if (typeof window.handleTxStateForTimeout === 'function') {
            window.handleTxStateForTimeout(!!update.value);
        }
        if (window.voiceAnnounce) window.voiceAnnounce.sayTxState(!!update.value);
    }
    if (update.property === "TxVfo") {
        txVfo = update.value;
        updateTxButton();
        applyVfoActiveStyling();
        updateRxTxSelectors();
        publishTxState();
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('txVfo', update.value);
    }
    if (update.property === "ActiveVfo") {
        activeVfo = update.value;
        updateCwKeyerZinButton();
        applyVfoActiveStyling();
        updateRxTxSelectors();
        // In normal mode on a single-receiver radio, the TX button position
        // follows activeVfo (the TX VFO IS the active VFO; FT doesn't move).
        updateTxButton();
        publishTxState();
        // R8 (Jacek SP3L #34, 2026-06-21): in split mode the TX VFO is the
        // opposite of active, so the SPLIT TX badge and the red border have
        // to switch panels whenever the active VFO changes.
        updateSplitButton();
        // The radio's own scope display follows the operating band, so point
        // the CAT scope controls at the same band. Absent on models without
        // the SS command, and a no-op on single-receiver ones.
        window.notifyRadioScopeControls
            ? window.notifyRadioScopeControls(c => c.setActiveBand(update.value))
            : window.radioScopeControl?.setActiveBand(update.value);
    }

    // --- RADIO SCOPE CHANGED AT THE FRONT PANEL ---
    // The radio announces SS changes the operator makes on the rig itself, so
    // the scope panel can follow a hand on the front panel the same way it
    // already follows its own writes. Transient: nothing is stored server-side
    // (see RadioStateService.BroadcastTransient).
    if (update.property === "ScopeSetting") {
        window.notifyRadioScopeControls
            ? window.notifyRadioScopeControls(c => c.applyRemote(update.value))
            : window.radioScopeControl?.applyRemote(update.value);
    }

    // The Radio Display hotspot overlay keeps its own small copy of the VFO
    // and front-end state so it can label and cycle what the TFT is showing.
    window.radioDisplayHotspots?.onRadioState(update);

    // --- SPLIT MODE ---
    if (update.property === "SplitMode") {
        splitMode = update.value;
        updateSplitButton();
        // R7 (Jacek SP3L #34): greying flips when split toggles — the inactive
        // panel becomes the TX VFO (grey) and the RX VFO becomes white.
        applyVfoActiveStyling();
        // Pre7 fix: also re-evaluate the TX button position. On single-receiver
        // radios, enabling split changes which panel the TX button should sit
        // on (becomes "opposite of activeVfo") but FT often doesn't move on
        // FTdx10 to trigger the TxVfo handler -- so do it here too.
        updateTxButton();
        updateRxTxSelectors();
        publishTxState();
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('split', update.value);
    }

    // --- METER UPDATES ---
    if (update.property === "SMeterA") {
        try { window.updateSMeter?.('A', update.value); }
        catch (e) { console.error('updateSMeter A error:', e); }
    }
    if (update.property === "SMeterB") {
        try { window.updateSMeter?.('B', update.value); }
        catch (e) { console.error('updateSMeter B error:', e); }
    }
    if (window.ftdx101Meters) {
        // PowerMeter is sent as { value, isTransmitting } — unpack it and sync TX state.
        let meterValue = update.value;
        if (update.property === "PowerMeter" &&
            typeof update.value === 'object' && update.value !== null &&
            'value' in update.value && 'isTransmitting' in update.value) {
            meterValue = update.value.value;
            window.ftdx101Meters.setTransmitting(update.value.isTransmitting);
        }
        const result = window.ftdx101Meters.handleMeterUpdate(update.property, meterValue);
        if (result) updateMeterDomLabel(update.property, result);
    }

    // --- ROOFING FILTER ---
    if (update.property === "RoofingFilterA") {
        if (window.roofingButtonA) {
            window.roofingButtonA.setState({ selectedId: String(update.value) }, { silent: true });
        }
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ roofingCode: update.value });
        updateContourSliderBounds('A');
    }
    if (update.property === "RoofingFilterB") {
        if (window.roofingButtonB) {
            window.roofingButtonB.setState({ selectedId: String(update.value) }, { silent: true });
        }
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ roofingCode: update.value });
        updateContourSliderBounds('B');
    }

    // --- AGC ---
    if (update.property === "AgcA") {
        const code = (update.value === "5" || update.value === "6") ? "4" : update.value;
        if (window.agcButtonA) window.agcButtonA.setState({ selectedId: String(code) }, { silent: true });
    }
    if (update.property === "AgcB") {
        const code = (update.value === "5" || update.value === "6") ? "4" : update.value;
        if (window.agcButtonB) window.agcButtonB.setState({ selectedId: String(code) }, { silent: true });
    }

    // --- IPO/AMP ---
    if (update.property === "IpoA") {
        if (window.ipoButtonA) window.ipoButtonA.setState({ selectedId: String(update.value) }, { silent: true });
    }
    if (update.property === "IpoB") {
        if (window.ipoButtonB) window.ipoButtonB.setState({ selectedId: String(update.value) }, { silent: true });
    }

    // --- ATTENUATOR ---
    if (update.property === "AttA") {
        if (window.attButtonA) window.attButtonA.setState({ selectedId: String(update.value) }, { silent: true });
    }
    if (update.property === "AttB") {
        if (window.attButtonB) window.attButtonB.setState({ selectedId: String(update.value) }, { silent: true });
    }

    // --- NOISE REDUCTION ---
    if (update.property === "NrA") {
        if (window.nrCycleButtonA) {
            window.nrCycleButtonA.setState({ selectedId: String(update.value) }, { silent: true });
        }
    }
    if (update.property === "NrB") {
        if (window.nrCycleButtonB) {
            window.nrCycleButtonB.setState({ selectedId: String(update.value) }, { silent: true });
        }
    }

    // --- MANUAL NOTCH FREQUENCY ---
    if (update.property === "ManualNotchFreqA") {
        if (window.manNotchButtonA) {
            window.manNotchButtonA.setState({ value: Number(update.value) }, { silent: true });
        }
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ manualNotchFreqHz: parseInt(update.value) || 800 });
    }
    if (update.property === "ManualNotchFreqB") {
        if (window.manNotchButtonB) {
            window.manNotchButtonB.setState({ value: Number(update.value) }, { silent: true });
        }
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ manualNotchFreqHz: parseInt(update.value) || 800 });
    }

    // --- NOISE BLANKER ---
    if (update.property === "NbA") {
        if (window.nbButtonA) {
            window.nbButtonA.setState({ enabled: update.value === "1" || update.value === 1 }, { silent: true });
        }
    }
    if (update.property === "NbB") {
        if (window.nbButtonB) {
            window.nbButtonB.setState({ enabled: update.value === "1" || update.value === 1 }, { silent: true });
        }
    }

    // --- AUTO NOTCH ---
    if (update.property === "AutoNotchA") {
        if (window.autoNotchButtonA) {
            window.autoNotchButtonA.setState({ enabled: update.value === "1" || update.value === 1 }, { silent: true });
        }
    }
    if (update.property === "AutoNotchB") {
        if (window.autoNotchButtonB) {
            window.autoNotchButtonB.setState({ enabled: update.value === "1" || update.value === 1 }, { silent: true });
        }
    }

    // --- IF WIDTH ---
    if (update.property === "IfWidthA") {
        const widget = window.ifWidthButtonA;
        if (widget) {
            widget.setState({ selectedId: String(update.value) }, { silent: true });
        }
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ ifWidthCode: update.value });
        updateContourSliderBounds('A');
    }
    if (update.property === "IfWidthB") {
        const widget = window.ifWidthButtonB;
        if (widget) {
            widget.setState({ selectedId: String(update.value) }, { silent: true });
        }
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ ifWidthCode: update.value });
        updateContourSliderBounds('B');
    }

    // --- IF SHIFT ---
    if (update.property === "IfShiftA") {
        const widget = window.ifShiftButtonA;
        if (widget && !widget.menuOpen) {
            const hz = typeof window.snapIfShiftHz === "function"
                ? window.snapIfShiftHz(update.value)
                : (parseInt(update.value, 10) || 0);
            widget.setState({ selectedId: String(hz) }, { silent: true });
        }
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ ifShiftHz: parseInt(update.value) || 0 });
    }
    if (update.property === "IfShiftB") {
        const widget = window.ifShiftButtonB;
        if (widget && !widget.menuOpen) {
            const hz = typeof window.snapIfShiftHz === "function"
                ? window.snapIfShiftHz(update.value)
                : (parseInt(update.value, 10) || 0);
            widget.setState({ selectedId: String(hz) }, { silent: true });
        }
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ ifShiftHz: parseInt(update.value) || 0 });
    }

    // --- CLARIFIER ---
    if (update.property === "RxClarOn") {
        rxClarOn = update.value === true || update.value === 'true' || update.value === 1;
        const mode = rxClarOn && txClarOn ? 'rxtx' : rxClarOn ? 'rx' : txClarOn ? 'tx' : 'off';
        window.clarModeButton?.setState({ selectedId: mode }, { silent: true });
    }
    if (update.property === "TxClarOn") {
        txClarOn = update.value === true || update.value === 'true' || update.value === 1;
        const mode = rxClarOn && txClarOn ? 'rxtx' : rxClarOn ? 'rx' : txClarOn ? 'tx' : 'off';
        window.clarModeButton?.setState({ selectedId: mode }, { silent: true });
    }
    if (update.property === "ClarifierOffsetA") {
        clarOffsets.A = parseInt(update.value) || 0;
        if (clarVfo === 'A') {
            window.clarOffsetButton?.setState({ value: clarOffsets.A }, { silent: true });
        }
    }
    if (update.property === "ClarifierOffsetB") {
        clarOffsets.B = parseInt(update.value) || 0;
        if (clarVfo === 'B') {
            window.clarOffsetButton?.setState({ value: clarOffsets.B }, { silent: true });
        }
    }

    // --- CONTOUR ---
    if (update.property === "ContourOnA") {
        contourState.A.on = update.value === true || update.value === 'true' || update.value === 1;
        if (window.contourButtonA) {
            window.contourButtonA.setState({ enabled: contourState.A.on }, { silent: true });
        }
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ contourOn: contourState.A.on });
    }
    if (update.property === "ContourOnB") {
        contourState.B.on = update.value === true || update.value === 'true' || update.value === 1;
        if (window.contourButtonB) {
            window.contourButtonB.setState({ enabled: contourState.B.on }, { silent: true });
        }
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ contourOn: contourState.B.on });
    }
    if (update.property === "ContourFreqA") {
        contourState.A.freqHz = parseInt(update.value) || 800;
        if (window.contourButtonA) {
            window.contourButtonA.setState({ value: contourState.A.freqHz }, { silent: true });
        }
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ contourFreqHz: contourState.A.freqHz });
    }
    if (update.property === "ContourFreqB") {
        contourState.B.freqHz = parseInt(update.value) || 800;
        if (window.contourButtonB) {
            window.contourButtonB.setState({ value: contourState.B.freqHz }, { silent: true });
        }
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ contourFreqHz: contourState.B.freqHz });
    }

    // --- APF ---
    if (update.property === "ApfOnA") {
        apfState.A.on = update.value === true || update.value === 'true' || update.value === 1;
        if (window.apfButtonA) {
            window.apfButtonA.setState({ enabled: apfState.A.on }, { silent: true });
        }
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ apfOn: apfState.A.on });
    }
    if (update.property === "ApfOnB") {
        apfState.B.on = update.value === true || update.value === 'true' || update.value === 1;
        if (window.apfButtonB) {
            window.apfButtonB.setState({ enabled: apfState.B.on }, { silent: true });
        }
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ apfOn: apfState.B.on });
    }
    if (update.property === "ApfFreqA") {
        apfState.A.freqHz = parseInt(update.value) || 0;
        if (window.apfButtonA) {
            window.apfButtonA.setState({ value: apfState.A.freqHz }, { silent: true });
        }
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ apfFreqHz: apfState.A.freqHz });
    }
    if (update.property === "ApfFreqB") {
        apfState.B.freqHz = parseInt(update.value) || 0;
        if (window.apfButtonB) {
            window.apfButtonB.setState({ value: apfState.B.freqHz }, { silent: true });
        }
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ apfFreqHz: apfState.B.freqHz });
    }

    // --- MANUAL NOTCH ---
    if (update.property === "ManualNotchA") {
        if (window.manNotchButtonA) {
            window.manNotchButtonA.setState({ enabled: update.value === "1" || update.value === 1 }, { silent: true });
        }
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ manualNotchOn: update.value === '1' });
    }
    if (update.property === "ManualNotchB") {
        if (window.manNotchButtonB) {
            window.manNotchButtonB.setState({ enabled: update.value === "1" || update.value === 1 }, { silent: true });
        }
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ manualNotchOn: update.value === '1' });
    }

    // --- AF GAIN ---
    if (update.property === "AfGainA" || update.property === "AfGainB") {
        const receiver = update.property === "AfGainA" ? 'A' : 'B';
        const slider = document.getElementById(`afGainSlider${receiver}`);
        const label  = document.getElementById(`afGainValue${receiver}`);
        if (slider && !afGainDragging[receiver]) {
            slider.value = update.value;
            if (label) label.innerText = update.value;
        }
    }

    // --- ATU ---
    if (update.property === "AtuEnabled") {
        const enabled = update.value === true || update.value === 'true';
        // Track latest known state in a data attribute so that when an
        // auto-tune cycle finishes we can restore the correct on/off look.
        const btn = document.getElementById('atuBtn');
        if (btn) btn.dataset.atuEnabled = enabled ? 'true' : 'false';
        if (window.updateAtuButton) window.updateAtuButton(enabled);
    }
    if (update.property === "AtuTuning") {
        const tuning = update.value === true || update.value === 'true';
        if (window.updateAtuTuningState) window.updateAtuTuningState(tuning);
    }

    // --- NB LEVEL ---
    if (update.property === "NbLevelA") {
        if (window.nbButtonA) {
            window.nbButtonA.setState({ value: Number(update.value) }, { silent: true });
        }
    }
    if (update.property === "NbLevelB") {
        if (window.nbButtonB) {
            window.nbButtonB.setState({ value: Number(update.value) }, { silent: true });
        }
    }

    // --- NR LEVEL (DNR algorithm on FTdx10) ---
    if (update.property === "NrLevelA") {
        if (window.nrCycleButtonA) {
            window.nrCycleButtonA.setState({ value: Number(update.value) }, { silent: true });
        }
    }
    if (update.property === "NrLevelB") {
        if (window.nrCycleButtonB) {
            window.nrCycleButtonB.setState({ value: Number(update.value) }, { silent: true });
        }
    }

    // --- RF GAIN ---
    if (update.property === "RfGainA") {
        const s = document.getElementById('rfGainSliderA'); const l = document.getElementById('rfGainValueA');
        if (s) s.value = update.value; if (l) l.textContent = update.value;
    }
    if (update.property === "RfGainB") {
        const s = document.getElementById('rfGainSliderB'); const l = document.getElementById('rfGainValueB');
        if (s) s.value = update.value; if (l) l.textContent = update.value;
    }

    // --- SQUELCH ---
    if (update.property === "SquelchA") {
        const s = document.getElementById('squelchSliderA'); const l = document.getElementById('squelchValueA');
        if (s) s.value = update.value; if (l) l.textContent = update.value;
    }
    if (update.property === "SquelchB") {
        const s = document.getElementById('squelchSliderB'); const l = document.getElementById('squelchValueB');
        if (s) s.value = update.value; if (l) l.textContent = update.value;
    }

    // --- MONITOR ON/OFF + LEVEL ---
    if (update.property === "MonitorOn") {
        const on = update.value === true || update.value === 'true';
        if (typeof window._updateMonitorBtn === 'function') window._updateMonitorBtn(on);
    }
    if (update.property === "MonitorLevelA") {
        if (window.monitorCycleButton) {
            window.monitorCycleButton.setState(
                { value: Number(update.value) },
                { silent: true }
            );
        }
        const slider = document.getElementById('monLevelSlider');
        const label  = document.getElementById('monLevelValue');
        if (slider) slider.value = update.value;
        if (label) label.textContent = update.value;
    }

    // --- VOX ---
    if (update.property === "VoxOn") {
        if (window.updateVoxButton) window.updateVoxButton(update.value === true || update.value === 'true');
    }
    if (update.property === "VoxGain") {
        const s = document.getElementById('voxGainSlider'); const l = document.getElementById('voxGainValue');
        if (s) s.value = update.value; if (l) l.textContent = update.value;
    }
    if (update.property === "VoxDelay") {
        const s = document.getElementById('voxDelaySlider'); const l = document.getElementById('voxDelayValue');
        if (s) s.value = update.value; if (l) l.textContent = update.value;
    }

    // --- CW ---
    if (update.property === "CwPitch") {
        const s = document.getElementById('cwPitchSlider'); const l = document.getElementById('cwPitchHz');
        if (s) s.value = update.value;
        // The label already supplies the unit, so write the number alone -
        // appending ' Hz' here rendered as "Pitch: 700 Hz Hz" the moment the
        // radio's own CwPitch arrived over SignalR.
        if (l) l.textContent = 300 + parseInt(update.value) * 10;
    }
    if (update.property === "CwSpeed") {
        const s = document.getElementById('cwSpeedSlider'); const l = document.getElementById('cwSpeedValue');
        if (s) s.value = update.value; if (l) l.textContent = update.value;
        // The CW Send panel carries the same speed control; keep it honest.
        window.cwSendPanel?.setSpeed?.(update.value);
    }
    if (update.property === "CwBreakIn") {
        const el = document.getElementById('cwBreakInSelect'); if (el) el.value = update.value;
        window.cwSendPanel?.setBreakIn?.(update.value);
    }
    if (update.property === "CwBreakInDelay") {
        const s = document.getElementById('cwDelaySlider'); const l = document.getElementById('cwDelayValue');
        if (s) s.value = update.value; if (l) l.textContent = update.value;
    }

    // --- FM REPEATER ---
    if (update.property === "FmShiftDir") {
        const el = document.getElementById('fmShiftSelect'); if (el) el.value = update.value;
    }
    if (update.property === "FmOffsetHz") {
        const el = document.getElementById('fmOffsetInput'); if (el) el.value = Math.round(update.value / 1000);
    }
    if (update.property === "CtcssMode") {
        const el = document.getElementById('ctcssModeSelect'); if (el) el.value = update.value;
    }
    if (update.property === "CtcssTone") {
        const el = document.getElementById('ctcssToneSelect'); if (el) el.value = update.value;
    }

});

// SignalR connection is started once below (after the IIFE) with a .catch() error handler.

// ---------------------------------------------------------------------------
// Initialization overlay polling
// Polls /api/status/init every second until status is "complete", "radio_off", or "error".
// On error, redirects to /Settings ONLY if user hasn't dismissed the overlay.
// On radio_off, stays on Index page so user can turn radio on via power button.
// ---------------------------------------------------------------------------
let initPollingStopped = false; // Allow user to dismiss and continue

async function pollInitStatus() {
    if (initPollingStopped) return; // User dismissed, stop polling

    try {
        const response = await fetch('/api/status/init');
        if (!response.ok) {
            if (!initPollingStopped) {
                setTimeout(pollInitStatus, 2000);
            }
            return;
        }
        const data = await response.json();
        const overlay = document.getElementById('initOverlay');
        const statusText = document.getElementById('initStatusText');
        if (!overlay || !statusText) return;

        statusText.innerText = data.status;

        if (data.status === "complete") {
            overlay.style.display = "none";
            initPollingStopped = true; // Stop polling
            radioPowerOn = true;
            updateRadioPowerButton();
            // NB: previous behaviour called window.applySegmentsOnInit() here,
            // which auto-tuned the radio to the last-clicked band segment on
            // every Index-page load. That overwrote whatever frequency the
            // operator had set manually on the rig and was the root cause of
            // Jacek SP3L's bug #33 (radio jumps on YWC startup, also fires
            // on Home->About->Home tab navigation because pollInitStatus runs
            // on every page mount). The dropdown still restores its saved
            // value visually via populateSegmentSelect; we just don't push
            // the saved frequency back to the radio. The rig's current state
            // is the source of truth.
        } else if (data.status === "radio_off") {
            // Radio is off - hide overlay and let user turn it on via power button
            overlay.style.display = "none";
            initPollingStopped = true;
            radioPowerOn = false;
            updateRadioPowerButton();
            // ...removed debug logging...
        } else if (data.status === "error") {
            statusText.innerHTML = "COM port error. <a href='/Settings' class='text-white'>Go to Settings</a> to configure the serial port.";
            overlay.style.display = "block";
            // Don't auto-redirect - let user choose
        } else {
            overlay.style.display = "block";
        }

        if (data.status !== "complete" && data.status !== "radio_off" && !initPollingStopped) {
            setTimeout(pollInitStatus, 1000);
        }
    } catch (error) {
        // ...removed debug logging...
        if (!initPollingStopped) {
            setTimeout(pollInitStatus, 2000);
        }
    }
}

function dismissInitOverlay() {
    initPollingStopped = true;
    const overlay = document.getElementById('initOverlay');
    if (overlay) overlay.style.display = "none";
}

// Touch device detection helper
function isTouchDevice() {
    return 'ontouchstart' in window || navigator.maxTouchPoints > 0;
}

// Interim radioControl - overwritten by the IIFE below once it executes
window.radioControl = {
    setBand: window.setBand,
    setMode: window.setMode,
    setAntenna: window.setAntenna,
    setPower: window.setPower,
    updatePowerDisplay: window.updatePowerDisplay,
    setAgc: async function (receiver, code) {
        await fetch(`/api/cat/agc/${receiver.toLowerCase()}`,
            { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code }) });
    },
    setIpo: async function (receiver, code) {
        await fetch(`/api/cat/ipo/${receiver.toLowerCase()}`,
            { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code }) });
    },
    setAutoNotch: async function (receiver, code) {
        await fetch(`/api/cat/autonotch/${receiver.toLowerCase()}`,
            { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code }) });
    },
    setNr: async function (receiver, code) {
        await fetch(`/api/cat/nr/${receiver.toLowerCase()}`,
            { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code }) });
    },
    setAttenuator: async function (receiver, code) {
        await fetch(`/api/cat/attenuator/${receiver.toLowerCase()}`,
            { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code }) });
    },
    setManualNotch: async function (receiver, enabled) {
        await fetch(`/api/cat/manualnotch/${receiver.toLowerCase()}`,
            { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ enabled }) });
    },
    setNoiseBlanker: async function (receiver, enabled) {
        await fetch(`/api/cat/noiseblanker/${receiver.toLowerCase()}`,
            { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ enabled }) });
    },
    setManualNotchFreq: async function (receiver, frequencyHz) {
        await fetch(`/api/cat/manualnotchfreq/${receiver.toLowerCase()}`,
            { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ frequencyHz }) });
    },
    setIfWidth: async function (receiver, code) {
        await fetch(`/api/cat/ifwidth/${receiver.toLowerCase()}`,
            { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code }) });
    },
    setIfShift: async function (receiver, shiftHz) {
        await fetch(`/api/cat/ifshift/${receiver.toLowerCase()}`,
            { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ shiftHz: parseInt(shiftHz) }) });
    }
};



// Out-of-band marker state.
//
// The `state` object further down the file is trapped inside an IIFE and is
// not visible here — see the note above the FrequencyA handler. The band
// buttons need both values together: the band name says *whether* we are out
// of band, and the frequency says *which* band button to mark.
const lastVfoHz   = { A: 0, B: 0 };
const lastVfoBand = { A: null, B: null };

// Paint the out-of-band marker on the band grid.
//
// The server reports "Unknown" for a frequency outside every allocation in the
// operator's own IARU region, so no band button is selected. On its own that
// just looks like nothing is happening. Here we mark the nearest band in the
// operator's region instead: a UK operator on 3.9 MHz gets a red 80m button,
// Paint the out-of-band marker on the Band Yaesu key.
//
// The server reports "Unknown" for a frequency outside every allocation in the
// operator's own IARU region, so no band is selected. Mark the Band key red
// and show the nearest band in the operator's region instead: a UK operator
// on 3.9 MHz gets a red 80m key — "you are at 80m, but not where you are
// allowed to be".
function applyBandOutOfBand(receiver) {
    const widget = window[`bandButton${receiver}`];
    if (!widget || !widget.root) return;

    const band = lastVfoBand[receiver];
    const selected = widget.getState().selectedId;
    const isOutOfBand = !!band && band.toLowerCase() === 'unknown';
    const oobBand = (isOutOfBand && typeof window.nearestBandForHz === 'function')
        ? window.nearestBandForHz(lastVfoHz[receiver])
        : null;

    const marked = !!oobBand;
    widget.root.classList.toggle('toggle-dd--oob', marked);

    if (widget.button) {
        if (marked) {
            if (!('titleOriginal' in widget.button.dataset)) {
                widget.button.dataset.titleOriginal = widget.button.getAttribute('title') || '';
            }
            const nearest = oobBand;
            widget.button.setAttribute(
                'title',
                `${nearest} — out of band for your region`
            );
            // Keep the displayed selection on the nearest band so the key still
            // names something meaningful while the frequency is OOB.
            if (selected?.toLowerCase() !== nearest.toLowerCase()) {
                widget.setState({ selectedId: nearest }, { silent: true });
            }
        } else if ('titleOriginal' in widget.button.dataset) {
            widget.button.setAttribute('title', widget.button.dataset.titleOriginal);
            delete widget.button.dataset.titleOriginal;
        }
    }
}

// a11y-labels.js reapplies titles from labels.json on every window focus,
// wiping the "out of band" suffix. Index.cshtml calls this once that has run.
window.refreshBandOutOfBand = function () {
    applyBandOutOfBand('A');
    applyBandOutOfBand('B');
};

// Update band button selection for a specific receiver (called via SignalR)
function updateBandButton(receiver, band) {
    if (!band) return;
    lastVfoBand[receiver] = band;
    const bandLower = band.toLowerCase();
    const widget = window[`bandButton${receiver}`];
    if (widget && bandLower !== 'unknown') {
        // Normalise "20m" / "20M" / "20" style values to option ids.
        const id = bandLower.endsWith('m') ? bandLower : `${bandLower}m`;
        const match = widget.options?.find(
            (o) => o.id.toLowerCase() === id || o.id.toLowerCase() === bandLower
        );
        if (match) {
            widget.setState({ selectedId: match.id }, { silent: true });
        }
    }
    applyBandOutOfBand(receiver);
}
window.updateBandButton = updateBandButton;

// Called after the Band Yaesu keys are created so a SignalR BandA/FrequencyA
// that arrived earlier (while window.bandButtonA was still null) is applied.
window.syncBandButtonsFromFrequency = function () {
    for (const receiver of ['A', 'B']) {
        const hz = lastVfoHz[receiver];
        if (hz > 0 && typeof window.bandForHz === 'function') {
            const derived = window.bandForHz(hz);
            if (derived) {
                updateBandButton(receiver, derived);
                continue;
            }
        }
        if (lastVfoBand[receiver]) {
            updateBandButton(receiver, lastVfoBand[receiver]);
        }
    }
};

// Outer DOMContentLoaded - initial UI wiring
window.addEventListener('DOMContentLoaded', () => {
    ensureSaveMemTooltips();
    pollInitStatus();

    // VFO-B show/hide toggle — click handler is in Index.cshtml (applyVisibility).
    // Only set the aria-label here; do not add a second click listener.
    document.getElementById('vfoBToggleBtn')
        ?.setAttribute('aria-label', 'Show or hide VFO B panel');

    // Split / Swap VFO button handlers
    document.getElementById('splitBtn')?.addEventListener('click', () => setSplit(splitMode > 0 ? 0 : 1));
    // Quick Split (+5k) is a one-shot action, not a persistent mode — the
    // Split button (red) shows the resulting split state. Give a brief "fired"
    // flash so the press registers visually, then return to the resting style.
    const quickSplitBtn = document.getElementById('quickSplitBtn');
    quickSplitBtn?.addEventListener('click', () => {
        quickSplitBtn.classList.add('btn-danger');
        clearTimeout(quickSplitBtn._flashTimer);
        quickSplitBtn._flashTimer = setTimeout(() => {
            quickSplitBtn.classList.remove('btn-danger');
        }, 250);
        setSplit(2);
    });
    // Independent RX / TX VFO selectors (single-receiver radios only; the group
    // is not rendered otherwise, so these no-op on dual-receiver radios).
    document.getElementById('rxVfoA')?.addEventListener('click', () => setRxVfo('A'));
    document.getElementById('rxVfoB')?.addEventListener('click', () => setRxVfo('B'));
    document.getElementById('txVfoA')?.addEventListener('click', () => setTxVfo('A'));
    document.getElementById('txVfoB')?.addEventListener('click', () => setTxVfo('B'));
    updateRxTxSelectors();
    document.getElementById('swapVfoBtn')?.addEventListener('click', swapVfo);
    document.getElementById('copyBtoABtn')?.addEventListener('click', () => copyVfo('ba'));
    document.getElementById('copyAtoBBtn')?.addEventListener('click', () => copyVfo('ab'));

    // Dual-receiver only: click a VFO panel's header to make it the active
    // (MAIN/SUB) band. Ignored on single-receiver (greying already shows the
    // active VFO) and when the click lands on a control inside the header.
    (function wireVfoActiveSelect() {
        const vfoRow = document.getElementById('vfoRow');
        if (!vfoRow || vfoRow.dataset.singleReceiver === 'true') return;
        const wire = (colId, vfo) => {
            const header = document.querySelector(`#${colId} .card-header`);
            if (!header) return;
            header.style.cursor = 'pointer';
            header.title = `Click to make VFO ${vfo} the active (MAIN/SUB) band`;
            header.addEventListener('click', (e) => {
                if (e.target.closest('button, input, select, a, [role="button"], .badge')) return;
                setActiveVfo(vfo);
            });
        };
        wire('vfoACol', 'A');
        wire('vfoBCol', 'B');
    })();

    // Clarifier: seed JS state from server-rendered HTML values
    const clarOffsetRoot = document.getElementById('clarOffsetButton');
    if (clarOffsetRoot) clarOffsets.A = parseInt(clarOffsetRoot.dataset.value) || 0;
    const clarModeRoot = document.getElementById('clarModeButton');
    const initMode = window.clarModeButton?.getState()?.selectedId
        || clarModeRoot?.dataset.selected
        || 'off';
    rxClarOn = initMode === 'rx' || initMode === 'rxtx';
    txClarOn = initMode === 'tx' || initMode === 'rxtx';

    // Contour/APF: seed JS state from Yaesu-key widgets (or data attrs before init).
    for (const vfo of ['A', 'B']) {
        const contour = window[`contourButton${vfo}`];
        if (contour) {
            const s = contour.getState();
            contourState[vfo].on = s.enabled;
            contourState[vfo].freqHz = s.value;
        } else {
            const root = document.getElementById(`contourButton${vfo}`);
            if (root) {
                contourState[vfo].on = root.dataset.enabled === '1' || root.dataset.enabled === 'true';
                const hz = parseInt(root.dataset.value, 10);
                if (!Number.isNaN(hz)) contourState[vfo].freqHz = hz;
            }
        }
        const apf = window[`apfButton${vfo}`];
        if (apf) {
            const s = apf.getState();
            apfState[vfo].on = s.enabled;
            apfState[vfo].freqHz = s.value;
        } else {
            const root = document.getElementById(`apfButton${vfo}`);
            if (root) {
                apfState[vfo].on = root.dataset.enabled === '1' || root.dataset.enabled === 'true';
                const hz = parseInt(root.dataset.value, 10);
                if (!Number.isNaN(hz)) apfState[vfo].freqHz = hz;
            }
        }
    }
});

// Touch up/down button handler for mobile frequency editing
function changeSelectedDigit(receiver, delta) {
    const display = document.getElementById('freq' + receiver);
    let digits = Array.from(display.querySelectorAll('.digit')).filter(d => d.textContent !== '.');
    let idx = state.selectedIdx[receiver];
    if (idx === null || !digits[idx]) return;
    let freqArr = digits.map(d => parseInt(d.textContent));
    let newVal = freqArr[idx] + delta;
    if (newVal > 9) newVal = 0;
    if (newVal < 0) newVal = 9;
    freqArr[idx] = newVal;
    let newFreq = parseInt(freqArr.join(''));
    newFreq = Math.max(30000, Math.min(75000000, newFreq));
    state.localFreq[receiver] = newFreq;
    updateFrequencyDisplay(receiver, newFreq);
    const displayElem = document.getElementById('freq' + receiver);
    clearTimeout(displayElem._debounceTimer);
    displayElem._debounceTimer = setTimeout(() => {
        setFrequency(receiver, newFreq);
    }, 200);
}

// ===========================================================================
// IIFE - Full authoritative implementation
// ===========================================================================
// Everything inside this block is the "real" app logic. It defines its own
// inner state, all the polling/display/gauge functions, and at the end
// overwrites window.radioControl so Razor inline handlers call these
// better-implemented versions.
// ===========================================================================
// --- AF Gain slider change handler ---
// Professional AF Gain handler: sets pending state, updates only on backend confirmation
// --- AF Gain slider change handler with smooth UX ---
// Track user interaction state
const afGainDragging = { A: false, B: false };

function setupAfGainSlider(receiver) {
    const slider = document.getElementById(`afGainSlider${receiver}`);
    if (!slider) return;
    const send = () => {
        fetch(`/api/cat/afgain/${receiver.toLowerCase()}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(parseInt(slider.value))
        });
    };
    slider.addEventListener('mousedown', () => { afGainDragging[receiver] = true; });
    slider.addEventListener('touchstart', () => { afGainDragging[receiver] = true; }, { passive: true });
    document.addEventListener('mouseup', () => {
        if (afGainDragging[receiver]) { afGainDragging[receiver] = false; send(); }
    });
    slider.addEventListener('touchend', () => { afGainDragging[receiver] = false; send(); });
    slider.addEventListener('change', send);
}

document.addEventListener('DOMContentLoaded', function() {
    setupAfGainSlider('A');
    setupAfGainSlider('B');
});

// IF SHIFT has no effect in AM or FM: measured on an FTdx101MP on
// 2026-09-19 (#166) -- parked 5 kHz off a broadcast carrier, +1000 and
// -1000 sound identical, and the radio's own filter display shows no
// shift. The knob still turns and CAT still reports a value, so grey the
// control out rather than let it look as if it does something.
const IF_SHIFT_FIXED_TITLE =
    'IF Shift has no effect in AM or FM on this radio - the filter is fixed about the carrier.';

function updateIfShiftForMode(receiver, mode) {
    const m = (mode || '').toUpperCase();
    const fixed = m === 'AM' || m === 'AM-N' || m.includes('FM');
    const widget = window[`ifShiftButton${receiver}`];
    if (!widget) return;
    widget.setDisabled(fixed);
    if (fixed) {
        widget.root.title = IF_SHIFT_FIXED_TITLE;
    } else {
        widget.root.removeAttribute('title');
    }
}
window.updateIfShiftForMode = updateIfShiftForMode;

function resetIfShift(receiver) {
    const widget = window[`ifShiftButton${receiver}`];
    if (widget) widget.setState({ selectedId: "0" }, { silent: true });
    const panel = receiver === "B" ? window.filterScopePanelB : window.filterScopePanelA;
    if (panel) panel.setState({ ifShiftHz: 0 });
    if (window.radioControl) window.radioControl.setIfShift(receiver, 0);
}
window.resetIfShift = resetIfShift;

function selectClarVfo(vfo) {
    clarVfo = vfo;
    window.clarVfoButton?.setState({ selectedId: vfo }, { silent: true });
    const offset = clarOffsets[vfo];
    window.clarOffsetButton?.setState({ value: offset }, { silent: true });
}
window.selectClarVfo = selectClarVfo;

async function setClarifierMode(mode) {
    rxClarOn = mode === 'rx' || mode === 'rxtx';
    txClarOn = mode === 'tx' || mode === 'rxtx';
    await _setClarifier(clarVfo, rxClarOn, txClarOn, clarOffsets[clarVfo]);
}
window.setClarifierMode = setClarifierMode;

async function setClarifierOffset(offsetHz) {
    clarOffsets[clarVfo] = offsetHz;
    await _setClarifier(clarVfo, rxClarOn, txClarOn, offsetHz);
}
window.setClarifierOffset = setClarifierOffset;

async function resetClarifier() {
    clarOffsets[clarVfo] = 0;
    window.clarOffsetButton?.setState({ value: 0 }, { silent: true });
    await _setClarifier(clarVfo, rxClarOn, txClarOn, 0);
}

async function nudgeClarifier(deltaHz) {
    const vfo = clarVfo;
    let newOffset = Math.round(((clarOffsets[vfo] || 0) + deltaHz) / 10) * 10;
    newOffset = Math.max(-9990, Math.min(9990, newOffset));
    clarOffsets[vfo] = newOffset;
    window.clarOffsetButton?.setState({ value: newOffset }, { silent: true });
    try {
        await fetch('/api/cat/clarifier/nudge', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ vfo, deltaHz })
        });
    } catch (e) {
        console.error('Clarifier nudge failed:', e);
    }
}
window.nudgeClarifier = nudgeClarifier;
window.resetClarifier = resetClarifier;

async function saveVfoToMemory(vfo) {
    const btn    = document.getElementById('saveMemBtn' + vfo);
    const status = document.getElementById('saveMemStatus' + vfo);
    const icon   = btn?.querySelector('.save-mem-key__icon');
    const resetBtn = () => {
        if (icon) icon.className = 'bi bi-floppy-fill save-mem-key__icon';
        btn?.classList.remove('is-saved', 'is-error', 'is-busy');
    };
    try {
        if (btn) { btn.disabled = true; btn.classList.add('is-busy'); }
        if (status) status.textContent = '';
        // Use the dedicated save-vfo endpoint: the backend reads the full
        // live radio state from RadioStateService and captures every advanced
        // field (antenna, IF width/shift, roofing, NB/NR/AGC, power) in one
        // shot. The browser only needs to send a label.
        const resp = await fetch(`/api/memory/save-vfo/${vfo}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ label: '' })
        });
        if (resp.ok) {
            if (status) status.textContent = `VFO ${vfo} saved to memories`;
            window.refreshMemoriesPanel?.();
            if (btn && icon) {
                btn.classList.remove('is-busy');
                btn.classList.add('is-saved');
                icon.className = 'bi bi-check-lg save-mem-key__icon';
                setTimeout(() => {
                    resetBtn();
                    btn.disabled = false;
                }, 1500);
            } else if (btn) {
                btn.disabled = false;
            }
        } else {
            if (status) status.textContent = `Failed to save VFO ${vfo}`;
            if (btn && icon) {
                btn.classList.remove('is-busy');
                btn.classList.add('is-error');
                icon.className = 'bi bi-x-lg save-mem-key__icon';
                btn.disabled = false;
                setTimeout(resetBtn, 1500);
            } else if (btn) {
                btn.disabled = false;
            }
        }
    } catch (e) {
        if (status) status.textContent = `Error saving VFO ${vfo}`;
        if (btn && icon) {
            btn.classList.remove('is-busy');
            btn.classList.add('is-error');
            icon.className = 'bi bi-x-lg save-mem-key__icon';
            btn.disabled = false;
            setTimeout(resetBtn, 1500);
        } else if (btn) {
            btn.disabled = false;
        }
        console.error('Save to memory failed:', e);
    }
}
window.saveVfoToMemory = saveVfoToMemory;

function initSaveMemTooltips() {
    if (typeof bootstrap === 'undefined') return false;
    document.querySelectorAll('.save-mem-tip[data-bs-toggle="tooltip"]').forEach(el => {
        bootstrap.Tooltip.getOrCreateInstance(el, {
            delay: { show: 200, hide: 50 },
            trigger: 'hover focus',
            placement: el.getAttribute('data-bs-placement') || 'top',
            fallbackPlacements: ['bottom', 'right', 'left']
        });
    });
    return true;
}

function ensureSaveMemTooltips() {
    if (initSaveMemTooltips()) return;
    window.addEventListener('load', () => initSaveMemTooltips(), { once: true });
}

async function _setClarifier(vfo, rxOn, txOn, offsetHz) {
    try {
        await fetch('/api/cat/clarifier', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ vfo, rxOn, txOn, offsetHz })
        });
    } catch (e) {
        console.error('Clarifier update failed:', e);
    }
}

function resetIfWidth(receiver) {
    const widget = window[`ifWidthButton${receiver}`];
    if (!widget) return;
    const defaultId = widget.clickSelectId ?? "0";
    widget.setState({ selectedId: String(defaultId) }, { silent: true });
    if (window.radioControl) window.radioControl.setIfWidth(receiver, defaultId);
}
window.resetIfWidth = resetIfWidth;

async function setContourOn(vfo, on) {
    contourState[vfo].on = on;
    const panel = vfo === 'B' ? window.filterScopePanelB : window.filterScopePanelA;
    if (panel) panel.setState({ contourOn: on });
    try {
        await fetch(`/api/cat/contour/${vfo.toLowerCase()}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ on, freqHz: contourState[vfo].freqHz })
        });
    } catch (e) { console.error('Contour toggle failed:', e); }
}
window.setContourOn = setContourOn;

async function setContourFreq(vfo, hz) {
    contourState[vfo].freqHz = hz;
    const panel = vfo === 'B' ? window.filterScopePanelB : window.filterScopePanelA;
    if (panel) panel.setState({ contourFreqHz: hz });
    try {
        await fetch(`/api/cat/contour/${vfo.toLowerCase()}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ on: contourState[vfo].on, freqHz: hz })
        });
    } catch (e) { console.error('Contour freq failed:', e); }
}
window.setContourFreq = setContourFreq;

// Recompute the contour slider's min/max for a VFO based on the current
// passband (mode + IF Width + roofing). The radio's hard CAT range is
// preserved as an outer clamp via the widget's initial min/max values,
// so we never let the user set a value the radio can't accept. If the
// existing contour value falls outside the new (narrower) range, clamp
// it in place and send the clamped value to the radio.
//
// Called from the SignalR handlers for ModeA/ModeB, IfWidthA/IfWidthB,
// and the per-VFO roofing-filter changes; also once at startup after the
// FilterScopePanel instances are constructed.
function updateContourSliderBounds(vfo) {
    const panel = window['filterScopePanel' + vfo];
    const widget = window[`contourButton${vfo}`];
    if (!panel || typeof panel.getPassband !== 'function' || !widget) return;

    // Cache the radio's hard limits on first run (the values rendered
    // server-side from the radio model: 100..3200 for FTdx101, 100..4000
    // for FTDX3000). After that, future updates only narrow within those.
    if (widget._hardMin == null) widget._hardMin = widget.min;
    if (widget._hardMax == null) widget._hardMax = widget.max;

    const { lo, hi } = panel.getPassband();
    const newMin = Math.max(widget._hardMin, Math.round(lo));
    const newMax = Math.min(widget._hardMax, Math.round(hi));
    if (newMin >= newMax) return;

    const oldVal = widget.getState().value;
    const clamped = Math.max(newMin, Math.min(newMax, oldVal));

    widget.setState({ min: newMin, max: newMax, value: clamped }, { silent: true });
    contourState[vfo].freqHz = clamped;

    if (clamped !== oldVal) {
        setContourFreq(vfo, clamped);  // updates panel state + sends CAT
    }
}
window.updateContourSliderBounds = updateContourSliderBounds;

async function setApfOn(vfo, on) {
    apfState[vfo].on = on;
    const panel = vfo === 'B' ? window.filterScopePanelB : window.filterScopePanelA;
    if (panel) panel.setState({ apfOn: on });
    try {
        await fetch(`/api/cat/apf/${vfo.toLowerCase()}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ on, freqHz: apfState[vfo].freqHz })
        });
    } catch (e) { console.error('APF toggle failed:', e); }
}
window.setApfOn = setApfOn;

async function setApfFreq(vfo, hz) {
    apfState[vfo].freqHz = hz;
    const panel = vfo === 'B' ? window.filterScopePanelB : window.filterScopePanelA;
    if (panel) panel.setState({ apfFreqHz: hz });
    try {
        await fetch(`/api/cat/apf/${vfo.toLowerCase()}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ on: apfState[vfo].on, freqHz: hz })
        });
    } catch (e) { console.error('APF freq failed:', e); }
}
window.setApfFreq = setApfFreq;


(function () {
    'use strict';

    // ...removed debug logging...


    // Full inner state object - this is the authoritative state for the app
    const state = {
        editing: { A: false, B: false },
        editingPower: { A: false, B: false },
        localFreq: { A: null, B: null },
        selectedIdx: { A: null, B: null },
        lastSentFreq: { A: null, B: null },
        _lastFreqSend: { A: 0, B: 0 },   // ms timestamp of the last throttled send-to-radio, per receiver
        lastBackendFreq: { A: null, B: null },
        lastBand: { A: null, B: null },
        lastMode: { A: null, B: null },
        lastAntenna: { A: null, B: null },
        lastPower: { A: 100, B: 100 },
        maxPower: 200,
        radioModel: 'FTdx101MP',
        isTransmitting: false  // Track TX state for meter display
    };

    function renderFrequencyDigits(freq, selIdx) {
        if (!freq || freq < 1000) {
            return '<span class="digit" aria-hidden="true">-</span><span class="digit" aria-hidden="true">-</span>.<span class="digit" aria-hidden="true">-</span><span class="digit" aria-hidden="true">-</span><span class="digit" aria-hidden="true">-</span>.<span class="digit" aria-hidden="true">-</span><span class="digit" aria-hidden="true">-</span><span class="digit" aria-hidden="true">-</span>';
        }
        let s = freq.toString().padStart(8, "0");
        let html = "";
        let digitIdx = 0;
        for (let i = 0; i < 8; i++) {
            if (i === 2 || i === 5) {
                html += '<span class="digit" aria-hidden="true">.</span>';
            }
            let selected = (selIdx === digitIdx) ? " selected" : "";
            html += `<span class="digit${selected}" aria-hidden="true" tabindex="-1">${s[i]}</span>`;
            digitIdx++;
        }
        return html;
    }

    function updateFrequencyDisplay(receiver, freqHz) {
        const display = document.getElementById('freq' + receiver);
        if (!display) {
            // ...removed debug logging...
            return;
        }
        let selIdx = state.selectedIdx[receiver];
        let freqToShow = state.editing[receiver]
            ? (state.localFreq[receiver] ?? state.lastSentFreq[receiver] ?? freqHz)
            : freqHz;
        display.innerHTML = renderFrequencyDigits(freqToShow, selIdx);
        if (freqToShow && freqToShow > 0) {
            const mhz = String(parseFloat((freqToShow / 1e6).toFixed(6)));
            clearTimeout(_ariaDebounceTimers[receiver]);
            _ariaDebounceTimers[receiver] = setTimeout(() => {
                display.setAttribute('aria-valuenow', mhz);
                display.setAttribute('aria-label', `VFO ${receiver}: ${mhz} MHz`);
                display.setAttribute('title', `VFO ${receiver}: ${mhz} MHz`);
            }, 300);
        }
    }
    window.updateFrequencyDisplay = updateFrequencyDisplay;

    // Update band, mode, and antenna to reflect current state.
    function highlightButtons(receiver, band, mode, antenna) {
        if (band && window[`bandButton${receiver}`]) {
            window[`bandButton${receiver}`].setState({ selectedId: String(band) }, { silent: true });
        }

        if (mode && window[`modeButton${receiver}`]) {
            window[`modeButton${receiver}`].setState({ selectedId: String(mode) }, { silent: true });
        }

        if (antenna && window[`antennaButton${receiver}`]) {
            window[`antennaButton${receiver}`].setState({ selectedId: String(antenna) }, { silent: true });
        }
    }

    // Update roofing filter Yaesu-key
    function updateRoofingFilterSelect(receiver, filterCode) {
        const widget = window[`roofingButton${receiver}`];
        if (widget && filterCode) {
            widget.setState({ selectedId: String(filterCode) }, { silent: true });
        }
    }

    function initializeDigitInteraction(receiver) {
        const display  = document.getElementById('freq' + receiver);
        const controls = document.getElementById('freq' + receiver + '-controls');
        const upBtn    = document.getElementById('freq' + receiver + '-up');
        const downBtn  = document.getElementById('freq' + receiver + '-down');
        if (!display) return;
        if (display._initialized) return;
        display._initialized = true;

        // Shared digit-step routine used by wheel, keyboard, and ▲/▼ buttons.
        // `step` is the signed amount to add to the digit at the selected
        // position; carries propagate left through more-significant digits.
        function stepSelectedDigit(step) {
            const digits = Array.from(display.querySelectorAll('.digit')).filter(d => d.textContent !== '.');
            const idx = state.selectedIdx[receiver];
            if (idx === null || idx === undefined || !digits[idx]) return;
            const freqArr = digits.map(d => parseInt(d.textContent));
            let carry = step;
            let i = idx;
            while (carry !== 0 && i >= 0 && i < freqArr.length) {
                const newVal = freqArr[i] + carry;
                if (newVal > 9) {
                    freqArr[i] = newVal % 10;
                    carry = Math.floor(newVal / 10);
                    i--;
                } else if (newVal < 0) {
                    freqArr[i] = ((newVal % 10) + 10) % 10;
                    carry = Math.floor(newVal / 10);
                    i--;
                } else {
                    freqArr[i] = newVal;
                    carry = 0;
                }
            }
            let newFreq = parseInt(freqArr.join(''));
            newFreq = Math.max(30000, Math.min(75000000, newFreq));
            state.localFreq[receiver] = newFreq;
            state.editing[receiver] = true;
            updateFrequencyDisplay(receiver, newFreq);
            // Re-find digits after re-render and keep selection on the same index.
            const newDigits = Array.from(display.querySelectorAll('.digit')).filter(d => d.textContent !== '.');
            newDigits.forEach(d => d.classList.remove('selected'));
            if (newDigits[idx]) newDigits[idx].classList.add('selected');
            // Send to the radio AS WE STEP, throttled — so a press-and-hold (or a
            // wheel spin) tunes the radio live instead of only when you let go.
            // Previously this was a 600 ms trailing debounce, but the ▲/▼
            // hold-repeat fires every 500 ms, so each step reset the timer and the
            // radio never moved until release (reported by Colin on the FtdX101).
            // Leading-edge throttle: move the radio at most every SEND_THROTTLE_MS;
            // a trailing timer always sends the final position and clears localFreq
            // so the ~500 ms poll can settle the display once the radio confirms.
            const SEND_THROTTLE_MS = 250;
            const sendToRadio = (settle) => {
                const f = state.localFreq[receiver] ?? newFreq;
                // Skip the actual CAT write if this frequency was already the last
                // one sent — stops the trailing timer re-sending a value the
                // leading edge already pushed during a continuous hold/spin.
                if (f !== state.lastSentFreq[receiver]) {
                    setFrequency(receiver, f);
                    state.lastSentFreq[receiver] = f;
                    state._lastFreqSend[receiver] = Date.now();
                }
                // Only the trailing (settle) send releases localFreq, so the poll
                // can take over once the radio confirms lastSentFreq. Intermediate
                // sends keep localFreq set so the display keeps showing the live
                // in-progress value. state.editing stays true either way —
                // reconcileFrequencyEditing (by the SignalR handler) clears it
                // once the radio echoes lastSentFreq back, avoiding the "flip
                // back then settle" race, or once the radio moves somewhere we
                // did not send it.
                if (settle) state.localFreq[receiver] = null;
            };
            if (Date.now() - (state._lastFreqSend[receiver] || 0) >= SEND_THROTTLE_MS) {
                sendToRadio(false);   // leading edge: move the radio now
            }
            clearTimeout(display._debounceTimer);
            display._debounceTimer = setTimeout(() => sendToRadio(true), SEND_THROTTLE_MS);
        }

        // Auto-select the kHz position when the user hits an arrow / ▲ / ▼
        // without first picking a digit. Defaults to "4th from the right"
        // so the first action moves something audible rather than a 1 Hz
        // tick the user can't hear.
        // Picking a digit also sets that VFO's tuning step, so one mouse-wheel
        // notch on the spectrum moves the dial by the digit the operator just
        // pointed at (Bruce VK2RT, discussion #168). `idx` is the position among
        // the eight rendered digits, most significant first, so the last digit is
        // 1 Hz. The reverse — moving the selected digit when the step is changed
        // elsewhere — is deliberately NOT done: the selection is cleared whenever
        // the user clicks anywhere else on the page, so it cannot be a reliable
        // display of a value that persists.
        function latchTuningStepFromDigit(idx, digitCount) {
            if (idx === null || idx === undefined || idx < 0) return;
            const store = window.ywcTuningStep;   // set by the tuning-step module
            if (!store) return;
            store.set(receiver, Math.pow(10, digitCount - 1 - idx));
        }

        function ensureSelection() {
            const digits = Array.from(display.querySelectorAll('.digit')).filter(d => d.textContent !== '.');
            if (digits.length === 0) return;
            const cur = state.selectedIdx[receiver];
            if (cur === null || cur === undefined || !digits[cur]) {
                const defaultIdx = Math.max(0, digits.length - 4);
                digits.forEach(d => d.classList.remove('selected'));
                digits[defaultIdx].classList.add('selected');
                state.selectedIdx[receiver] = defaultIdx;
            }
        }

        display.addEventListener('click', function (e) {
            if (!e.target.classList.contains('digit') || e.target.textContent === '.') return;
            const digits = Array.from(display.querySelectorAll('.digit')).filter(d => d.textContent !== '.');
            digits.forEach(d => d.classList.remove('selected'));
            state.selectedIdx[receiver] = digits.indexOf(e.target);
            if (state.selectedIdx[receiver] !== -1) {
                digits[state.selectedIdx[receiver]].classList.add('selected');
                // Selecting a digit is NOT an edit. It used to set editing +
                // localFreq here, which froze the display on the value as it
                // was at the moment of the click: `updateFrequencyDisplay`
                // shows localFreq while editing, and editing only cleared when
                // the radio echoed a frequency THIS display had sent. So after
                // clicking a digit to set the wheel step (#168), wheeling the
                // spectrum moved the radio while YWC sat still -- reported by
                // Colin on 2026-09-20. `stepSelectedDigit` sets both the moment
                // the operator actually changes the value, which is the point
                // at which the display must stop following the poll.
                latchTuningStepFromDigit(state.selectedIdx[receiver], digits.length);
            }
            // Explicitly focus the display so the very next ArrowUp/Down
            // press is delivered here instead of bubbling to body. The
            // digit spans are tabindex=-1 so a span click does NOT
            // automatically focus the display in every browser.
            display.focus({ preventScroll: true });
        });

        display.addEventListener('wheel', function (e) {
            // Wheel is position-sensitive: cursor over a digit picks that digit.
            if (e.target.classList.contains('digit') && e.target.textContent !== '.') {
                const digits = Array.from(display.querySelectorAll('.digit')).filter(d => d.textContent !== '.');
                const hovered = digits.indexOf(e.target);
                if (hovered !== -1) {
                    digits.forEach(d => d.classList.remove('selected'));
                    digits[hovered].classList.add('selected');
                    state.selectedIdx[receiver] = hovered;
                    latchTuningStepFromDigit(hovered, digits.length);
                }
            }
            ensureSelection();
            stepSelectedDigit(e.deltaY < 0 ? 1 : -1);
            e.preventDefault();
        }, { passive: false });

        // Keyboard navigation — primary accessibility path for users who
        // can't use a mouse wheel (head-tracking input, on-screen-keyboard
        // users, reduced-dexterity operators). The freq display is
        // role="spinbutton" tabindex="0" so it accepts focus from Tab.
        //
        //   ArrowUp / ArrowDown          step the selected digit by 1
        //   PageUp   / PageDown          step the selected digit by 10
        //   ArrowLeft / ArrowRight       move the selected-digit cursor
        //   Home / End                   jump to the most / least significant digit
        //
        // First-press semantics: if no digit is currently selected (e.g. the
        // polling reset cleared it after a previous edit completed, or the
        // user's click missed and landed on a "." separator), the very
        // first arrow press just SHOWS the selection at the kHz digit and
        // does NOT step. A second press then actually steps. This avoids
        // the surprise where ArrowUp silently changes a digit the user
        // can't see is selected.
        display.addEventListener('keydown', function (e) {
            const ourKeys = ['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown',
                             'ArrowLeft', 'ArrowRight', 'Home', 'End'];
            if (!ourKeys.includes(e.key)) return;

            const allDigits = Array.from(display.querySelectorAll('.digit')).filter(d => d.textContent !== '.');
            if (allDigits.length === 0) return;

            // "Is anything currently visibly selected?" -- the truth is in
            // the DOM, not in state. state.selectedIdx can hold a stale
            // index left over from a previous interaction; the visible
            // highlight is what the user can actually see. Bootstrap only
            // when no digit is highlighted on screen.
            const visiblySelected = display.querySelector('.digit.selected');
            if (!visiblySelected) {
                ensureSelection();
                e.preventDefault();
                return;
            }
            // Re-sync state from DOM if they disagree (defensive). The DOM
            // class is the source of truth for "which digit is selected";
            // state mirrors it so stepSelectedDigit / move-cursor logic can
            // work in terms of an integer index.
            const cur = allDigits.indexOf(visiblySelected);
            state.selectedIdx[receiver] = cur;

            switch (e.key) {
                case 'ArrowUp':   stepSelectedDigit(1);   e.preventDefault(); break;
                case 'ArrowDown': stepSelectedDigit(-1);  e.preventDefault(); break;
                case 'PageUp':    stepSelectedDigit(10);  e.preventDefault(); break;
                case 'PageDown':  stepSelectedDigit(-10); e.preventDefault(); break;
                case 'ArrowLeft':
                    if (cur > 0) {
                        const newIdx = cur - 1;
                        allDigits.forEach(d => d.classList.remove('selected'));
                        allDigits[newIdx].classList.add('selected');
                        state.selectedIdx[receiver] = newIdx;
                    }
                    e.preventDefault();
                    break;
                case 'ArrowRight':
                    if (cur < allDigits.length - 1) {
                        const newIdx = cur + 1;
                        allDigits.forEach(d => d.classList.remove('selected'));
                        allDigits[newIdx].classList.add('selected');
                        state.selectedIdx[receiver] = newIdx;
                    }
                    e.preventDefault();
                    break;
                case 'Home':
                    allDigits.forEach(d => d.classList.remove('selected'));
                    allDigits[0].classList.add('selected');
                    state.selectedIdx[receiver] = 0;
                    e.preventDefault();
                    break;
                case 'End':
                    allDigits.forEach(d => d.classList.remove('selected'));
                    allDigits[allDigits.length - 1].classList.add('selected');
                    state.selectedIdx[receiver] = allDigits.length - 1;
                    e.preventDefault();
                    break;
            }
        });

        // ▲ / ▼ buttons — visible when Settings > Accessibility >
        // Show frequency arrow buttons is on (Yuri W4YSW request). A single
        // click/tap steps the currently-selected digit by 1 — same action as
        // ArrowUp / ArrowDown and the mouse wheel. Press-and-hold repeats
        // that same step every 500 ms until released, so reaching a distant
        // frequency doesn't need dozens of individual clicks.
        function bindHoldToRepeat(btn, direction) {
            if (!btn) return;
            let repeatTimer = null;
            let firedByHold = false;

            function doStep() {
                ensureSelection();
                stepSelectedDigit(direction);
            }
            function start(e) {
                e.preventDefault();
                firedByHold = true;
                doStep();
                clearInterval(repeatTimer);
                repeatTimer = setInterval(doStep, 500);
            }
            function stop() {
                clearInterval(repeatTimer);
                repeatTimer = null;
            }

            btn.addEventListener('mousedown', start);
            btn.addEventListener('touchstart', start, { passive: false });
            btn.addEventListener('mouseup', stop);
            btn.addEventListener('mouseleave', stop);
            btn.addEventListener('touchend', stop);
            btn.addEventListener('touchcancel', stop);
            window.addEventListener('blur', stop);

            // Keyboard activation (Enter/Space) fires 'click' directly with
            // no preceding mousedown/touchstart, so it still gets a single
            // step. A pointer click/tap already stepped via start() above —
            // firedByHold suppresses the duplicate step from the click that
            // follows mouseup/touchend.
            btn.addEventListener('click', function (e) {
                e.preventDefault();
                if (firedByHold) { firedByHold = false; return; }
                doStep();
            });
        }
        bindHoldToRepeat(upBtn, 1);
        bindHoldToRepeat(downBtn, -1);

        document.addEventListener('click', function (e) {
            // Don't clear selection on clicks inside the display OR inside
            // our own ▲/▼ controls — those should keep the digit selected
            // so a button click can act on it.
            if (display.contains(e.target)) return;
            if (controls && controls.contains(e.target)) return;
            // Selection is persistent across polling cycles (so an
            // accessibility user can press ArrowUp / ▲ in rapid sequence
            // without having to re-select each time). The user explicitly
            // ends a selection by clicking somewhere else on the page --
            // that's the cue handled here.
            const hadSelection = state.selectedIdx[receiver] !== null && state.selectedIdx[receiver] !== undefined;
            const wasEditing = state.editing[receiver];
            if (!hadSelection && !wasEditing) return;
            state.selectedIdx[receiver] = null;
            state.editing[receiver] = false;
            state.localFreq[receiver] = null;
            updateFrequencyDisplay(receiver, state.lastBackendFreq[receiver] ?? 0);
        });
    }

    async function setFrequency(receiver, freqHz) {
        try {
            const response = await fetch(`/api/cat/frequency/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ frequencyHz: freqHz })
            });
            updateFrequencyDisplay(receiver, freqHz);
        } catch (error) {
        }
    }

    async function setBand(receiver, band) {
        try {
            highlightButtons(receiver, band, state.lastMode[receiver], state.lastAntenna[receiver]);
            state.lastBand[receiver] = band;
            const response = await fetch(`/api/cat/band/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ band })
            });
        } catch (error) {
        }
    }

    async function setMode(receiver, mode) {
        const catCode = modeToCatCode[mode];
        if (!catCode) {
            return;
        }
        const response = await fetch(`/api/cat/mode/${receiver.toLowerCase()}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ mode: catCode })
        });
    }

    async function setAntenna(receiver, antenna) {
        try {
            highlightButtons(receiver, state.lastBand[receiver], state.lastMode[receiver], antenna);
            state.lastAntenna[receiver] = antenna;
            const response = await fetch(`/api/cat/antenna/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ antenna })
            });
        } catch (error) {
        }
    }

    // Show Windows-style message box (auto-dismisses after 3 seconds)
    function showMessageBox(message, title = 'Warning') {
        const modalEl = document.getElementById('messageBoxModal');
        const titleEl = document.getElementById('messageBoxTitle');
        const textEl = document.getElementById('messageBoxText');

        if (modalEl && titleEl && textEl) {
            titleEl.innerHTML = `<i class="bi bi-exclamation-triangle-fill me-2" aria-hidden="true"></i>${title}`;
            textEl.textContent = message;
            const modal = new bootstrap.Modal(modalEl);
            modal.show();

            // Auto-dismiss after 3 seconds
            setTimeout(() => {
                modal.hide();
            }, 3000);
        } else {
            // Fallback to alert if modal not found
            alert(message);
        }
    }

    async function setAgc(receiver, code) {
        try {
            await fetch(`/api/cat/agc/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ code })
            });
        } catch (e) {
            console.error('setAgc error:', e);
        }
    }

    async function setIpo(receiver, code) {
        try {
            await fetch(`/api/cat/ipo/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ code })
            });
        } catch (e) { console.error('setIpo error:', e); }
    }

    async function setAutoNotch(receiver, code) {
        try {
            await fetch(`/api/cat/autonotch/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ code })
            });
        } catch (e) { console.error('setAutoNotch error:', e); }
    }

    async function setNr(receiver, code) {
        try {
            await fetch(`/api/cat/nr/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ code })
            });
        } catch (e) { console.error('setNr error:', e); }
    }

    async function setAttenuator(receiver, code) {
        try {
            await fetch(`/api/cat/attenuator/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ code })
            });
        } catch (e) { console.error('setAttenuator error:', e); }
    }

    async function setManualNotch(receiver, enabled) {
        try {
            await fetch(`/api/cat/manualnotch/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enabled })
            });
        } catch (e) { console.error('setManualNotch error:', e); }
    }

    async function setNoiseBlanker(receiver, enabled) {
        try {
            await fetch(`/api/cat/noiseblanker/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enabled })
            });
        } catch (e) { console.error('setNoiseBlanker error:', e); }
    }

    async function setManualNotchFreq(receiver, frequencyHz) {
        try {
            await fetch(`/api/cat/manualnotchfreq/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ frequencyHz: parseInt(frequencyHz) })
            });
        } catch (e) { console.error('setManualNotchFreq error:', e); }
    }

    async function setIfWidth(receiver, code) {
        try {
            await fetch(`/api/cat/ifwidth/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ code })
            });
        } catch (e) { console.error('setIfWidth error:', e); }
    }

    async function setIfShift(receiver, shiftHz) {
        try {
            await fetch(`/api/cat/ifshift/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ shiftHz: parseInt(shiftHz) })
            });
        } catch (e) { console.error('setIfShift error:', e); }
    }

    async function setRoofingFilter(receiver, filter) {
        try {
            const response = await fetch(`/api/cat/roofingfilter/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ filter })
            });

            const data = await response.json();

            if (!response.ok) {
                showMessageBox(`Failed to set roofing filter: ${data.error}`, 'Error');
                return;
            }

            // Check if there's a warning (filter not installed)
            if (data.warning) {
                showMessageBox(data.message, 'Roofing Filter');
                // Update key to show actual filter
                if (data.filter) updateRoofingFilterSelect(receiver, data.filter);
            }
        } catch (error) {
            showMessageBox('Error setting roofing filter. Check console for details.', 'Error');
        }
    }

    // Power display helpers
    function updateSliderFill(slider) {
        const min = parseFloat(slider.min) || 0;
        const max = parseFloat(slider.max) || 100;
        const val = parseFloat(slider.value) || 0;
        const pct = ((val - min) / (max - min)) * 100;
        slider.style.setProperty('--fill-pct', pct + '%');
    }

    function updatePowerDisplay(receiver, watts) {
        window.powerCycleButton?.setState({ value: Number(watts) }, { silent: true });
    }

    async function setPower(receiver, watts) {
        try {
            // Ensure state.lastPower is an object
            if (typeof state.lastPower !== 'object' || state.lastPower === null) {
                state.lastPower = {};
            }
            state.lastPower[receiver] = parseInt(watts);
            const response = await fetch(`/api/cat/power/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ Watts: parseInt(watts) })
            });
            updatePowerDisplay(receiver, watts);
        } catch (error) {
        }
    }

    function updatePowerSlider(receiver, watts) {
        // No-op: backend never updates the slider. User only.
    }

    function updatePowerSliderMax(maxPower) {
        const actualMax = window.configuredMaxPower
            ? window.configuredMaxPower(maxPower)
            : (typeof maxPower === "number" ? maxPower : 200);
        window.powerCycleButton?.setState({ min: 5, max: actualMax }, { silent: true });
    }

    function updateSMeter(receiver, value) {
        // v2.4.0: restored as a real per-VFO pair (dual-receiver radios have
        // an independently calibrated S-meter per receiver, SM0;/SM1;).
        // On single-receiver radios receiver 'B' is a no-op below since
        // window.meterPanel has no 'smeterB' gauge (canvas doesn't exist —
        // MeterPanel._createGauges skipped it) and sMeterHistoryB.push() is
        // a no-op (canvas doesn't exist).
        // The radio stops measuring received signal at key-down: it latches
        // SM0/SM1 and holds them for the whole over, then resumes within
        // ~200 ms of release (measured — see the .meters-tx-dim comment in
        // Index.cshtml). So the value arriving here during transmit is a
        // snapshot of the instant you keyed, not a reading, and until now it
        // was drawn as though it were live.
        //
        // Show zero instead. Safe only because updateTxIndicators() greys the
        // gauges at the same time: a zeroed needle on its own would claim "no
        // signal", but greyed-and-zero reads as "not measuring".
        //
        // Deliberately placed before everything below so the gauge, the S-unit
        // label, the raw readout and the 30-second history strip all agree.
        // In particular the strip now records a floor across each over rather
        // than a flat line at the frozen value, which was the same untruth
        // drawn sideways.
        if (sMetersFrozenByTx) value = 0;

        const gaugeKey   = receiver === 'B' ? 'smeterB' : 'smeter';
        const history     = receiver === 'B' ? window.sMeterHistoryB : window.sMeterHistory;
        const canvasId    = receiver === 'B' ? 'sMeterCanvasB' : 'sMeterCanvas';
        const labelId     = receiver === 'B' ? 'sMeterValueB' : 'sMeterValue';
        const linearLabelId = receiver === 'B' ? 'sMeterLinearValueB' : 'sMeterLinearValueA';
        const linearCanvasId = receiver === 'B' ? 'sMeterLinearCanvasB' : 'sMeterLinearCanvasA';

        // The S-meter gauge has hardcoded tick positions on a 0-255 scale and
        // ignores calibration tables for needle placement. To make the user's
        // calibration actually affect where the needle sits, translate the raw
        // ADC value into the gauge position where the calibrated S-unit lives
        // on the static dial. calibrateSMeterForGauge does the two-step:
        // raw → user-calibrated S-unit → static gauge position. The history,
        // raw display, and snap-label keep using the un-translated raw value.
        // Reported by Jacek SP3L on #29; confirmed broken by Colin on bench
        // 2026-06-12 and traced to gauge.js:137 hardcoded majorTicks.
        // Both VFOs share the single calibration table -- there's no
        // per-receiver calibration in this codebase, and SM0;/SM1; both
        // report on the same raw 0-255 scale.
        const gaugePos = window.calibrationEngine?.calibrateSMeterForGauge
            ? window.calibrationEngine.calibrateSMeterForGauge(value)
            : value;
        if (window.meterPanel) window.meterPanel.update(gaugeKey, gaugePos);
        if (history) history.push(value);
        if (receiver === 'A' && typeof updateRawSMeterValueA === 'function') updateRawSMeterValueA(value);
        const canvas = document.getElementById(canvasId);
        const sUnit = sMeterLabel(value);
        if (canvas) canvas.dataset.reading = sUnit;
        // Update the "S-Meter S5" title label under the gauge (matches SWR /
        // Power / Temp / etc. format). Element is rendered by SMeterGauge
        // when gaugeTitleShow is true (set in gauge.js).
        const sLabel = document.getElementById(labelId);
        if (sLabel) sLabel.textContent = sUnit;
        // Compact linear sibling in the VFO panel.
        const linLabel = document.getElementById(linearLabelId);
        if (linLabel) linLabel.textContent = sUnit;
        const linCanvas = document.getElementById(linearCanvasId);
        if (linCanvas) linCanvas.dataset.reading = sUnit;
    }
    window.updateSMeter = updateSMeter;

    // Update MIC bar meter (0-255 raw value)
    function updateMICMeter(value) {
        const percentage = Math.round((value / 255) * 100);
        const valueSpan = document.getElementById('micValue');
        const progressBar = document.getElementById('micBar');

        if (valueSpan) valueSpan.textContent = window.MeterFormatters.percent(percentage);
        if (progressBar) {
            progressBar.style.width = `${percentage}%`;
            progressBar.setAttribute('aria-valuenow', percentage);

            // Color coding: green < 80%, warning >= 80%
            progressBar.className = 'progress-bar';
            if (percentage < 80) {
                progressBar.classList.add('bg-success');
            } else {
                progressBar.classList.add('bg-warning');
            }
        }
    }

    // Kick everything off
    initializeDigitInteraction('A');
    initializeDigitInteraction('B');
    // Overwrite the interim window.radioControl with the real implementations
    window.radioControl = {
        setFrequency,
        setBand,
        setMode,
        setAntenna,
        setRoofingFilter,
        setAgc,
        setIpo,
        setAutoNotch,
        setNr,
        setAttenuator,
        setManualNotch,
        setNoiseBlanker,
        setManualNotchFreq,
        setIfWidth,
        setIfShift,
        _state: state,  // Expose state for TX indicator updates
        updatePowerDisplay: updatePowerDisplay,
        setPower: setPower
    };

    window.updateMICMeter = updateMICMeter;

    // Blur VFO control selects immediately after change so they don't stay highlighted
    document.querySelectorAll('.vfo-control-item select').forEach(function (sel) {
        sel.addEventListener('change', function () { this.blur(); });
    });

    // -------------------------------------------------------------------------
    // Band Segment Yaesu key
    // -------------------------------------------------------------------------
    // Populates the segment menu for a VFO based on the current band and
    // band plan, restores the last-used segment from localStorage, and tunes
    // the radio when the user picks a segment.

    function segmentStorageKey(vfo, band) {
        return `bandSeg_${vfo}_${band}`;
    }

    // The server (BandPlanService) reports this when the frequency falls
    // outside every allocation in the operator's own IARU region.
    const OOB_BAND = 'unknown';
    function isOutOfBand(band) {
        return typeof band === 'string' && band.toLowerCase() === OOB_BAND;
    }

    // Paint (or clear) the out-of-band state on a Segment key. The key
    // carries the warning as well as the colour, because a partially-sighted
    // operator gets the accessible name, not the red.
    function setSegmentOutOfBand(widget, vfo, on) {
        if (!widget) return;
        widget.root.classList.toggle('toggle-dd--oob', on);
        const label = on
            ? `VFO ${vfo} out of band — frequency is outside every allocation in your region`
            : null;
        widget._ariaOverride = label;
        widget._sync();
    }

    // Set the Segment key to reflect whichever segment of the band
    // contains the current frequency. Called from the FrequencyA/B SignalR
    // handlers so the key stays in sync when the operator tunes via
    // the radio's knob, the spectrum click, or the on-screen freq keyboard.
    // No-op if the band's menu hasn't been populated yet (e.g. on
    // initial connect before BandA arrives).
    function syncSegmentSelectToFrequency(vfo, hz) {
        const widget = window[`segmentButton${vfo}`];
        // Disabled means the key holds a single OOB or "--" placeholder,
        // so there is no segment to select. populateSegmentSelect re-runs on
        // the next band change and picks the sync back up.
        if (!widget || widget.disabled) return;
        const band = state.lastBand && state.lastBand[vfo];
        if (!band || isOutOfBand(band)) return;
        const plan = window.bandPlan || 'UK';
        if (!window.bandPlanData || !window.getBandSegmentForHz) {
            // Fallback if helper not loaded — use inline lookup against the plan.
            // Mirror the band-plan.js segmentForHz logic exactly, including the
            // "below-lowest → first segment" fallback, so 14.010 etc don't
            // produce a blank key when the helper isn't loaded.
            const segments = (window.bandPlanData && window.bandPlanData[plan] && window.bandPlanData[plan][band]) || null;
            if (!segments) return;
            const ordered = Object.entries(segments).sort((a, b) => a[1].freq - b[1].freq);
            let match = '';
            for (const [key, seg] of ordered) {
                if (typeof seg.freq !== 'number') continue;
                if (hz >= seg.freq) match = key;
                else break;
            }
            if (!match && ordered.length > 0) match = ordered[0][0];
            if (widget.getState().selectedId !== match) {
                widget.setState({ selectedId: match }, { silent: true });
            }
            return;
        }
        const key = window.getBandSegmentForHz(plan, band, hz) || '';
        if (widget.getState().selectedId !== key) {
            widget.setState({ selectedId: key }, { silent: true });
        }
    }
    // Expose to the outer SignalR handler (FrequencyA/B), which lives outside
    // this IIFE and would otherwise get a ReferenceError trying to call it.
    window.syncSegmentSelectToFrequency = syncSegmentSelectToFrequency;
    window.populateSegmentSelect = populateSegmentSelect;

    function populateSegmentSelect(vfo, band) {
        const widget = window[`segmentButton${vfo}`];
        if (!widget) return;

        // Wait until band-plan.js has been imported by the module script.
        const bandPlanData = window.bandPlanData;
        const plan = window.bandPlan || 'UK';
        if (!bandPlanData) return;

        const segments = (bandPlanData[plan] || {})[band] || null;
        const oob = isOutOfBand(band);

        if (!segments) {
            // Two different "no segments" cases, and they mean different
            // things to the operator: OOB is a warning (you are outside your
            // region's allocations), whereas "--" just means this band has no
            // activity plan in the JSON — 4m outside Region 1, say.
            widget.setOptions([{ id: '', label: oob ? 'OOB' : '--' }], {
                silent: true,
                selectedId: '',
            });
            widget.setDisabled(true);
            setSegmentOutOfBand(widget, vfo, oob);
            return;
        }

        widget.setDisabled(false);
        setSegmentOutOfBand(widget, vfo, false);

        const options = [{ id: '', label: '--' }];
        for (const [key, seg] of Object.entries(segments)) {
            options.push({ id: key, label: seg.label });
        }

        // Restore last used segment for this band. This is only a fallback
        // for the moment before we know the frequency — the radio's actual
        // frequency wins immediately below, because the key's job is to
        // say where the operator *is*, not where they last went.
        const saved = localStorage.getItem(segmentStorageKey(vfo, band));
        const savedOk = saved && options.some((o) => o.id === saved);
        widget.setOptions(options, {
            silent: true,
            selectedId: savedOk ? saved : '',
        });

        // Use lastVfoHz (top-level, written directly by the FrequencyA/B
        // SignalR handlers) rather than state.lastBackendFreq — that one is
        // written inside a try/catch from a scope where `state` isn't
        // visible, so it throws and is swallowed on every update and holds a
        // stale frequency. Getting this wrong showed up as the key
        // dropping to "--" when tuning back in from out of band: FrequencyA
        // arrives before BandA, so the good sync early-returns against the
        // still-disabled OOB placeholder and this call is the last word.
        const hz = (lastVfoHz && lastVfoHz[vfo]) || (state.lastBackendFreq && state.lastBackendFreq[vfo]);
        if (typeof hz !== 'number' || hz <= 0) return;

        // Only override the saved value when the frequency really lands in
        // the band we just populated. On a band-button click we are called
        // before the radio has retuned, so hz is still the *old* band's —
        // syncing blindly would flash "--" until the new frequency arrived.
        const live = window.getBandSegmentForHz
            ? window.getBandSegmentForHz(plan, band, hz)
            : null;
        if (live) syncSegmentSelectToFrequency(vfo, hz);
    }

    // Called when the user picks a segment from the menu.
    window.onSegmentChange = async function(vfo, segKey) {
        if (!segKey) return;
        const plan = window.bandPlan || 'UK';
        const bandPlanData = window.bandPlanData;
        if (!bandPlanData) return;

        // Determine the current band for this VFO. Out of band there is no
        // segment to tune to and nothing worth remembering, so bail before
        // we touch the radio or localStorage.
        const band = state.lastBand[vfo];
        if (!band || isOutOfBand(band)) return;

        const segments = (bandPlanData[plan] || {})[band];
        if (!segments || !segments[segKey]) return;

        const { freq, mode } = segments[segKey];

        // Save preference
        localStorage.setItem(segmentStorageKey(vfo, band), segKey);

        // Set mode first so the radio doesn't shift frequency when mode changes,
        // then tune to the target frequency.
        if (window.radioControl) {
            if (window[`modeButton${vfo}`]) {
                window[`modeButton${vfo}`].setState({ selectedId: String(mode) }, { silent: true });
            }
            await window.setMode(vfo, mode);
            await window.radioControl.setFrequency(vfo, freq);
        }
    };

    // Hook into the band state change: when lastBand is updated, repopulate
    // the segment menu. We patch setBand and updateBandButton so both
    // UI-driven and SignalR-driven band changes trigger the update.
    const _origUpdateBandButton = window.updateBandButton;

    // Re-populate segments whenever band state changes. Skip if the band is
    // unchanged — the BandA SignalR event fires on every frequency change,
    // not only on real band transitions, so repopulating here would reset the
    // key to its localStorage value and stomp on the auto-sync we did
    // from the matching FrequencyA event.
    function onBandChanged(vfo, band) {
        if (state.lastBand[vfo] === band) return;
        state.lastBand[vfo] = band;
        populateSegmentSelect(vfo, band);
    }

    // Wrap the outer updateBandButton so SignalR-driven band changes also update segments
    window.updateBandButton = function(receiver, band) {
        if (_origUpdateBandButton) _origUpdateBandButton(receiver, band);
        onBandChanged(receiver, band);
    };

    // Populate segments on first load once bandPlanData is ready
    function tryPopulateSegmentsOnLoad() {
        if (!window.bandPlanData) {
            setTimeout(tryPopulateSegmentsOnLoad, 100);
            return;
        }
        if (state.lastBand.A) populateSegmentSelect('A', state.lastBand.A);
        if (state.lastBand.B) populateSegmentSelect('B', state.lastBand.B);
    }
    document.addEventListener('DOMContentLoaded', function () {
        setTimeout(tryPopulateSegmentsOnLoad, 200);
    });

    // Removed (#33 fix, 2026-06-12): window.applySegmentsOnInit used to be
    // called from pollInitStatus when init completed, and it auto-tuned the
    // radio to the last-clicked band segment for each VFO. That behaviour
    // overwrote whatever frequency the operator had set manually on the rig,
    // which Jacek SP3L reported as #33: "YWC changes radio frequency to some
    // default value". The key UI value is restored by populateSegmentSelect
    // on DOMContentLoaded; the radio is NOT auto-tuned. If the user wants to
    // jump to a saved segment, they click the menu manually.

    // --- Raw Meter Label Visibility State (S-Meter and Power Out) ---
    // Use localStorage to sync across tabs/pages
    function getShowRawMeterLabels() {
        return localStorage.getItem('showRawMeterLabels') === 'true';
    }
    function setShowRawMeterLabels(val) {
        localStorage.setItem('showRawMeterLabels', val ? 'true' : 'false');
        window.showRawMeterLabels = val;
        updateRawMeterLabelVisibility();
    }
    function updateRawMeterLabelVisibility() {
        var show = window.showRawMeterLabels;
        var elS = document.getElementById('raw-s-meter-label-a');
        if (elS) elS.style.display = show ? '' : 'none';
        var elP = document.getElementById('raw-powerout-label');
        if (elP) elP.style.display = show ? '' : 'none';
    }
    // Listen for localStorage changes (cross-tab)
    window.addEventListener('storage', function (e) {
        if (e.key === 'showRawMeterLabels') {
            window.showRawMeterLabels = getShowRawMeterLabels();
            updateRawMeterLabelVisibility();
        }
    });
    // Expose for other scripts
    window.getShowRawMeterLabels = getShowRawMeterLabels;
    window.setShowRawMeterLabels = setShowRawMeterLabels;
    window.updateRawMeterLabelVisibility = updateRawMeterLabelVisibility;
    // Init on page load
    window.showRawMeterLabels = getShowRawMeterLabels();
    document.addEventListener('DOMContentLoaded', updateRawMeterLabelVisibility);

    // --- Raw S-Meter Value Update ---
    // Store last raw S-Meter value for VFO A
    window.lastRawSMeterA = 0;
    function updateRawSMeterValueA(val) {
        window.lastRawSMeterA = val;
        var el = document.getElementById('rawSMeterValueA');
        if (el) el.textContent = val;
    }

    // Calibration page: Toggle button logic for raw meter labels
    // (runs on both pages, harmless if button not present)
    document.addEventListener('DOMContentLoaded', function () {
        var btn = document.getElementById('toggleRawMeterLabelsBtn');
        if (btn) {
            function updateBtnText() {
                btn.textContent = window.getShowRawMeterLabels() ? 'Hide Raw Meter Readings' : 'Show Raw Meter Readings';
            }
            btn.addEventListener('click', function () {
                var newVal = !window.getShowRawMeterLabels();
                window.setShowRawMeterLabels(newVal);
                updateBtnText();
            });
            updateBtnText();
        }
    });
})();


connection.start().then(function () {
    filterSpectrumFeed.subscribe();
}).catch(function (err) {
    return;
});

// Show touch frequency controls on mobile
document.addEventListener('DOMContentLoaded', function () {
    if ('ontouchstart' in window || navigator.maxTouchPoints > 0) {
        var aControls = document.getElementById('freqA-controls');
        var bControls = document.getElementById('freqB-controls');
        if (aControls) aControls.style.display = '';
        if (bControls) bControls.style.display = '';
    }
});

// Screen-reader hover announcements via ARIA live region.
// NVDA may have mouse tracking or tooltip reporting disabled; live regions
// are always announced regardless of those settings.
(function () {
    const liveRegion = document.createElement('div');
    liveRegion.id = '_sr_live';
    // ASSERTIVE (not polite): each new announcement interrupts the
    // previous one rather than queueing behind it. This addresses
    // OZ1JTE's feedback on #20 — when sweeping the mouse across many
    // interactive elements (memory channels, settings inputs) the
    // screen reader was reading every passed-over button in turn
    // because polite-mode queued them all. Assertive plus the longer
    // debounce below means only the element the mouse rests on
    // actually gets announced.
    liveRegion.setAttribute('aria-live', 'assertive');
    liveRegion.setAttribute('aria-atomic', 'true');
    liveRegion.style.cssText = 'position:absolute;left:-9999px;width:1px;height:1px;overflow:hidden;white-space:nowrap;';
    document.body.appendChild(liveRegion);

    // TX-only meter canvases have no reading until the radio transmits.
    // Pre-fill with '—' so hover always announces something (name + dash rather than name only).
    document.addEventListener('DOMContentLoaded', function () {
        ['vddMeterCanvas', 'vddLinearCanvas', 'iddMeterCanvas', 'iddLinearCanvas',
         'tempMeterCanvas', 'tempLinearCanvas', 'compressionMeterCanvas'].forEach(id => {
            const c = document.getElementById(id);
            if (c && !c.dataset.reading) c.dataset.reading = '—';
        });
    });

    // Elements we want announced on hover (covers all interactive controls on the page).
    const INTERACTIVE = [
        'button',
        'a[href]',
        'select',
        'input:not([aria-hidden="true"])',  // exclude the aria-hidden radio inputs inside band buttons
        'label[role="radio"]',              // band buttons
        '[role="spinbutton"]',              // frequency display
        'canvas[role="img"]'                // spectrum canvas
    ].join(',');

    let lastLabel = '';
    let timer = null;

    document.addEventListener('mouseover', function (e) {
        const el = e.target.closest(INTERACTIVE);
        if (!el) {
            // Mouse moved off all interactive elements — reset so re-hover re-announces
            clearTimeout(timer);
            lastLabel = '';
            return;
        }
        let label;
        if (el.tagName === 'CANVAS') {
            // All canvases are aria-hidden="true" so NVDA mouse tracking ignores them.
            // The live region owns all announcements: name (from aria-label, user-customisable)
            // followed by the current reading stored in dataset.reading.
            const name    = el.getAttribute('aria-label') || el.getAttribute('title') || '';
            const reading = el.dataset.reading || '';
            label = reading ? (name ? name + ': ' + reading : reading) : (name || null);
        } else {
            label = el.getAttribute('aria-label') || el.getAttribute('title');
            // Fall back to text content for buttons/links that have no aria-label/title
            if (!label && (el.tagName === 'BUTTON' || el.tagName === 'A')) {
                label = (el.textContent || '').trim().replace(/\s+/g, ' ');
            }
            // Do NOT append selected option for SELECTs — NVDA announces the selected value
            // itself; appending here causes a double announcement for every dropdown.
            // Append current value for sliders
            if (el.tagName === 'INPUT' && el.type === 'range') {
                label = label + ', ' + el.value;
            }
        }
        if (!label) return;
        if (label === lastLabel) return;
        clearTimeout(timer);
        // 400 ms (was 200 ms) so the screen reader doesn't announce every
        // interactive element the mouse sweeps over on its way to the
        // intended target. OZ1JTE reported this on the Memories page in
        // particular, where dense rows of inputs/buttons make a quick
        // sweep noisy. 400 ms requires a genuine pause-and-hover.
        timer = setTimeout(function () {
            lastLabel = label;
            liveRegion.textContent = '';
            requestAnimationFrame(function () { liveRegion.textContent = label; });
        }, 400);
    });
    // No mouseout handler — resetting lastLabel in the mouseover null-el branch is sufficient
    // and avoids the aggressive clearing that mouseout on every child element causes.
})();

// ── Viewport width warning ────────────────────────────────────────────────
(function () {
    const STORAGE_KEY = 'viewportWarningDismissed2'; // bumped to re-show after threshold change
    const THRESHOLD   = 1280; // CSS px — below this gauges are likely to wrap

    function check() {
        const banner = document.getElementById('viewportWarning');
        if (!banner) return;
        if (localStorage.getItem(STORAGE_KEY) === '1') return;
        banner.style.display = window.innerWidth < THRESHOLD ? '' : 'none';
    }

    window.dismissViewportWarning = function () {
        const banner = document.getElementById('viewportWarning');
        if (banner) banner.style.display = 'none';
        localStorage.setItem(STORAGE_KEY, '1');
    };

    let _resizeTimer;
    window.addEventListener('resize', function () {
        clearTimeout(_resizeTimer);
        _resizeTimer = setTimeout(check, 200);
    });

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', check);
    } else {
        check();
    }
})();

// ── GitHub update check ─────────────────────────────────
// Moved to core/js/update/update-banner.js, shared with Icom Web Control,
// when the banner learned to show what is in the release. It is loaded by
// _Layout.cshtml and configured by the x-app-* meta tags there.
//
// The shared copy also fixes a fault this one had: it compared versions
// with a plain parseInt, so "2.5.2-pre3" read as 2.5.2 and a tester on a
// pre-release was never told the full release had shipped.

// ─────────────────────────────────────────────────────────────────────────────
// VC Tune preselector controls
// Sends commands to /api/vctune/{band}/{command} and updates button states
// from the JSON response.  Band is 'a' (MAIN) or 'b' (SUB).
// ─────────────────────────────────────────────────────────────────────────────
(function () {
    var _state         = { a: 'Unknown', b: 'Unknown' };
    var _catBlocked    = { a: false, b: false };  // true when hardware rejects VT CAT

    function _updateUi(band, data) {
        var vfo       = band.toUpperCase();
        var toggleBtn = document.getElementById('vcTuneToggleBtn' + vfo);
        var defBtn    = document.getElementById('vcTuneDefaultBtn' + vfo);
        var ctrBtn    = document.getElementById('vcTuneCenterBtn' + vfo);
        var meter     = document.getElementById('vcTuneMeter' + vfo);
        var warn      = document.getElementById('vcTuneWarn' + vfo);
        var row       = document.getElementById('vcTuneRow' + vfo);

        var catNotSupported = (data.errorCategory === 'CatNotSupported');
        if (catNotSupported) _catBlocked[band] = true;

        // Hardware does not support VC Tune over CAT — hide everything immediately.
        if (catNotSupported) {
            if (toggleBtn) toggleBtn.style.display = 'none';
            if (row)       row.style.display       = 'none';
            return;
        }

        var state = data.state || 'Unknown';
        var avail = (data.availability != null) ? data.availability : 0;
        _state[band] = state;

        var notInstalled = (avail === 0);

        if (toggleBtn) {
            var isActive = (state === 'On' || state === 'Stepping' || state === 'Centering');
            toggleBtn.classList.remove('btn-outline-light', 'btn-warning');
            toggleBtn.classList.add(isActive ? 'btn-warning' : 'btn-outline-light');
            toggleBtn.disabled = notInstalled;
            if (band === 'b') toggleBtn.style.display = avail > 0 ? '' : 'none';
        }

        if (defBtn) defBtn.disabled = notInstalled;
        if (ctrBtn) ctrBtn.disabled = notInstalled;

        if (meter) {
            var m = (data.meter != null) ? data.meter : -1;
            meter.textContent = m >= 0 ? 'P5: ' + m : 'P5: -';
        }

        if (warn) {
            var txt = '';
            if (avail === 0)                        txt = 'Not installed';
            else if (avail === 2)                   txt = 'Temporarily unavailable';
            else if (!data.success && data.message) txt = data.message;
            warn.textContent   = txt;
            warn.style.display = txt ? '' : 'none';
        }

        if (band === 'b' && row) row.style.display = avail > 0 ? '' : 'none';
    }

    async function vcTuneCommand(band, cmd) {
        try {
            var resp = await fetch('/api/vctune/' + band + '/' + cmd, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({})
            });
            if (!resp.ok) return;
            _updateUi(band, await resp.json());
        } catch(e) { console.error('VC Tune ' + cmd + ' failed:', e); }
    }

    async function vcTuneToggle(band) {
        if (_catBlocked[band]) return;
        var isOn = (_state[band] === 'On' || _state[band] === 'Stepping' || _state[band] === 'Centering');
        await vcTuneCommand(band, isOn ? 'off' : 'on');
    }

    async function vcTuneStep(band, direction) {
        if (_catBlocked[band]) return;
        var sel    = document.getElementById('vcTuneStep' + band.toUpperCase());
        var amount = sel ? parseInt(sel.value, 10) : 5;
        try {
            var resp = await fetch('/api/vctune/' + band + '/step', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ direction: direction, amount: amount })
            });
            if (!resp.ok) return;
            _updateUi(band, await resp.json());
        } catch(e) { console.error('VC Tune step failed:', e); }
    }

    async function _refreshStatus(band) {
        try {
            var resp = await fetch('/api/vctune/' + band + '/status');
            if (!resp.ok) return;
            _updateUi(band, await resp.json());
        } catch(e) { /* non-fatal */ }
    }

    function _vcTuneInit() {
        if (document.getElementById('vcTuneToggleBtnA')) _refreshStatus('a');
        if (document.getElementById('vcTuneToggleBtnB')) _refreshStatus('b');
    }

    window.vcTuneCommand = vcTuneCommand;
    window.vcTuneToggle  = vcTuneToggle;
    window.vcTuneStep    = vcTuneStep;

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', _vcTuneInit);
    } else {
        _vcTuneInit();
    }
})()
