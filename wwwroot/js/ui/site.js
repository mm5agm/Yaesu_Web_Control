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
    return !!(active && (
        active.tagName === 'INPUT' ||
        active.tagName === 'TEXTAREA' ||
        active.isContentEditable
    ));
}

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
        ? (e.key === ' ')
        : (configuredKey.length === 1 && e.key.length === 1
            ? e.key.toLowerCase() === configuredKey.toLowerCase()
            : e.key === configuredKey);
    if (!keyMatches) return;

    e.preventDefault();
    toggleTx();
});

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
        window.signalRConnection = new signalR.HubConnectionBuilder().withUrl("/radioHub").withAutomaticReconnect().build();
        window.signalRConnection.start().catch(function (err) { });
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

// Frequency display renderer (outer version, used by outer updateFrequencyDisplay)
function updateFrequencyDisplay(receiver, freqHz) {
    const display = document.getElementById('freq' + receiver);
    if (!display) {
        return;
    }
    let selIdx = window.radioControl && window.radioControl._state ? window.radioControl._state.selectedIdx[receiver] : null;
    let editing = window.radioControl && window.radioControl._state ? window.radioControl._state.editing[receiver] : false;
    let localFreq = window.radioControl && window.radioControl._state ? window.radioControl._state.localFreq[receiver] : null;
    let lastBackendFreq = window.radioControl && window.radioControl._state ? window.radioControl._state.lastBackendFreq[receiver] : null;
    let freqToShow = (!editing || localFreq === null)
        ? lastBackendFreq
        : localFreq;
    display.innerHTML = renderFrequencyDigits(freqToShow, selIdx);
    if (freqToShow && freqToShow > 0) {
        const mhz = String(parseFloat((freqToShow / 1e6).toFixed(6)));
        clearTimeout(_ariaDebounceTimers[receiver]);
        _ariaDebounceTimers[receiver] = setTimeout(() => {
            display.setAttribute('aria-valuenow', mhz);
            display.setAttribute('aria-label', `VFO ${receiver}: ${mhz} MHz`);
            display.setAttribute('title', `VFO ${receiver}: ${mhz} MHz`);
        }, 500);
    }
}

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
    if (window.pausePolling) pausePolling();
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

// Outer power slider max updater
function updatePowerSliderMax(maxPower) {
    const slider = document.getElementById('powerSlider');
    const labelMax = document.getElementById('powerMaxLabel');
    const model = (window.state && window.state.radioModel) || null;
    const actualMax = model
        ? modelMaxPower(model)
        : (typeof maxPower === "number" ? maxPower : 200);

    if (slider) {
        slider.max = actualMax;
        slider.min = 5;
        if (parseInt(slider.value, 10) > actualMax) {
            slider.value = actualMax;
            const display = document.getElementById('powerValue');
            if (display && window.MeterFormatters) {
                display.textContent = window.MeterFormatters.powerLabel(actualMax);
            }
        }
    }
    if (labelMax) labelMax.textContent = window.MeterFormatters.powerLabel(actualMax);
}

// TX state updater - updates TX button and meters
function updateTxIndicators(isTransmitting) {
    if (window.radioControl && window.radioControl._state) {
        window.radioControl._state.isTransmitting = isTransmitting;
    }
    if (window.ftdx101Meters) {
        window.ftdx101Meters.setTransmitting(isTransmitting);
    }
    if (!isTransmitting) {
        // Force gauges to zero immediately when TX stops
        if (window.meterPanel) {
            window.meterPanel.update('power', 0);
            window.meterPanel.update('swr', 0);
        }
        updateMeterDomLabel('PowerMeter', { skip: false, displayValue: { watts: 0, rawAvg: 0 } });
        updateMeterDomLabel('SWRMeter',   { skip: false, displayValue: { swr: 1.0 } });
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
            const rawEl = document.getElementById('raw-powerout-label');
            if (rawEl) rawEl.textContent = 'Raw Power Out: ' + Math.round(dv.rawAvg);
            const canvas = document.getElementById('powerMeterCanvas');
            if (canvas) canvas.dataset.reading = formatted;
            break;
        }
        case 'SWRMeter': {
            const formatted = window.MeterFormatters.swr(dv.swr);
            const el = document.getElementById('swrMeterValue');
            if (el) el.textContent = formatted;
            const canvas = document.getElementById('swrMeterCanvas');
            if (canvas) canvas.dataset.reading = formatted;
            break;
        }
        case 'CompressionMeter': {
            const formatted = window.MeterFormatters.compressionOverlay(dv.db);
            const el = document.getElementById('compressionMeterValue');
            if (el) el.textContent = formatted;
            const canvas = document.getElementById('compressionMeterCanvas');
            if (canvas) canvas.dataset.reading = formatted;
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
            const alcCanvas = document.getElementById('alcMeterCanvas');
            if (alcCanvas) alcCanvas.dataset.reading = alcFormatted;
            break;
        }
        case 'IDDMeter': {
            const formatted = window.MeterFormatters.iddOverlay(dv.amps);
            const el = document.getElementById('iddMeterValue');
            if (el) el.textContent = formatted;
            const canvas = document.getElementById('iddMeterCanvas');
            if (canvas) canvas.dataset.reading = formatted;
            break;
        }
        case 'VDDMeter': {
            const formatted = window.MeterFormatters.vddOverlay(dv.volts);
            const el = document.getElementById('vddMeterValue');
            if (el) el.textContent = formatted;
            const canvas = document.getElementById('vddMeterCanvas');
            if (canvas) canvas.dataset.reading = formatted;
            break;
        }
        case 'Temperature': {
            const formatted = window.MeterFormatters.tempOverlay(dv.tempC);
            const el = document.getElementById('paTemperatureValue');
            if (el) el.textContent = formatted;
            const canvas = document.getElementById('tempMeterCanvas');
            if (canvas) canvas.dataset.reading = formatted;
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
    // Find the power value display element
    const powerValue = document.getElementById('powerValue');
    if (powerValue) {
        powerValue.innerText = window.MeterFormatters.powerLabel(watts);
    }
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
    // Fetch radio status and update slider max / model-dependent UI
    fetch('/api/cat/status')
        .then(response => response.json())
        .then(data => {
            if (data && data.radioModel && window.state) {
                window.state.radioModel = data.radioModel;
                updatePowerSliderMax();
            }
        });

    // Update powerValue label live as slider moves (outer/global version).
    // NOTE: deliberately no longer initialises the label from slider.value
    // on page load. The slider has step=5 so the browser snaps the
    // server-rendered exact value (e.g. 91 W from the radio) to the
    // nearest step (90 W). Reading that back into the label gave the
    // operator a label that disagreed with what the radio is actually
    // doing — Jacek SP3L reported this on #35 follow-up. The Razor
    // template now renders the actual Power into the label directly;
    // SignalR Power pushes update the label using the exact value, not
    // the rounded slider position. The 'input' listener below only fires
    // on user interaction (programmatic .value sets don't trigger it),
    // so user-moves-slider still updates the label to the chosen step.
    const slider = document.getElementById('powerSlider');
    const display = document.getElementById('powerValue');
    if (slider && display) {
        slider.addEventListener('input', function () {
            display.textContent = window.MeterFormatters.powerLabel(slider.value);
        });
    }
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

// Apply the .vfo-inactive class to whichever VFO panel is NOT the active
// (RX) one — but only on single-receiver radios (FTdx10, FT-710, FTDX3000).
// CSS greys only that panel's .card-body (header stays normal so TX looks
// enabled). Dual-receiver radios leave both panels active because each
// VFO is its own physical receiver chain. The data-single-receiver
// attribute on #vfoRow is rendered server-side from RadioCapabilities.cs.
// See docs/decisions/0003-single-vs-dual-receiver-ui.md.
function applyVfoActiveStyling() {
    const vfoRow = document.getElementById('vfoRow');
    if (!vfoRow) return;
    const aCol = document.getElementById('vfoACol');
    const bCol = document.getElementById('vfoBCol');
    if (!aCol || !bCol) return;

    // Spectrum panels live OUTSIDE the VFO columns in their own
    // #spectrumContainer section — so they need the class applied
    // separately to be greyed when their corresponding VFO is inactive.
    // Note these can be absent (only one SDR configured, or none).
    const aSpec = document.getElementById('spectrumContainerA');
    const bSpec = document.getElementById('spectrumContainerB');

    const singleReceiver = vfoRow.dataset.singleReceiver === 'true';
    if (!singleReceiver) {
        // Dual-receiver: both panels are real receivers, both stay active.
        aCol.classList.remove('vfo-inactive');
        bCol.classList.remove('vfo-inactive');
        aSpec?.classList.remove('vfo-inactive');
        bSpec?.classList.remove('vfo-inactive');
        return;
    }

    // Single-receiver: white = active VFO (the one currently RECEIVING),
    // grey = the other one. This is true in BOTH normal and split mode:
    //
    //   Normal mode (R2): white = active VFO (RX), grey = the other.
    //                     Pressing A/B on the radio swaps which is RX.
    //
    //   Split mode  (R7): white = active VFO (RX), grey = TX VFO.
    //                     Radio receives on active VFO, transmits on the
    //                     opposite VFO.
    //
    // In both cases, "inactive" (= grey) = whichever VFO is NOT the active
    // RX one. We previously drove split-mode greying from txVfo (FT
    // command) because the spec implied FT tracks the TX VFO — but the
    // FTdx10 doesn't reliably move FT when split engages while VFO-B is
    // the active VFO (Jacek SP3L 2026-06-21 #34 R7 fail). Using activeVfo
    // (VS command) for both cases is deterministic and matches what the
    // radio is actually doing.
    //
    // The TX button and SPLIT badge land on the inactive panel in split
    // mode (R8) because updateTxButton / updateSplitButton derive the TX
    // position as "opposite of active" on single-receiver radios. The
    // card header is not greyed, so TX stays full-colour and clickable.
    //
    // The spectrum panel is NOT greyed — on single-receiver radios the
    // single spectrum always shows the live receive signal. The second
    // spectrum panel is hidden permanently by updateContainerVisibility().
    const splitOn = splitMode > 0;
    const inactiveCol = (activeVfo === 0) ? bCol : aCol;
    const activeCol   = (activeVfo === 0) ? aCol : bCol;

    activeCol.classList.remove('vfo-inactive', 'vfo-tx-editable');
    inactiveCol.classList.add('vfo-inactive');
    // R10/R11: in split mode the inactive panel IS the TX VFO — operators
    // must still be able to set the TX frequency from YWC without
    // un-splitting. .vfo-tx-editable re-enables the frequency field while
    // leaving every other card-body control read-only.
    inactiveCol.classList.toggle('vfo-tx-editable', splitOn);

    // Make sure neither spectrum carries a stale inactive class from a
    // previous render — in case the user switched RadioModel from
    // dual-receiver to single-receiver mid-session.
    aSpec?.classList.remove('vfo-inactive');
    bSpec?.classList.remove('vfo-inactive');
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
        } else {

        }
    } catch (error) {

    }
}

function updateTxButton() {
    const btnA = document.getElementById('txButtonA');
    const btnB = document.getElementById('txButtonB');

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

    // Show only on TX VFO
    if (btnA) btnA.style.display = (positionVfo === 0) ? 'inline-block' : 'none';
    if (btnB) btnB.style.display = (positionVfo === 1) ? 'inline-block' : 'none';

    // Update button state
    const activeBtn = (positionVfo === 0) ? btnA : btnB;
    if (activeBtn) {
        if (isTransmitting) {
            activeBtn.className = 'btn btn-danger btn-sm';
            activeBtn.innerHTML = '<i class="bi bi-broadcast" aria-hidden="true"></i> TX ON';
            activeBtn.title = 'Click to stop transmitting';
        } else {
            activeBtn.className = 'btn btn-warning btn-sm';
            activeBtn.innerHTML = '<i class="bi bi-broadcast" aria-hidden="true"></i> TX';
            activeBtn.title = 'Click to transmit';
        }
    }
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
        btn.className = active ? 'btn btn-sm btn-danger' : 'btn btn-sm btn-outline-secondary';
        btn.style.paddingTop    = '1px';
        btn.style.paddingBottom = '1px';
        btn.textContent = active ? 'Split ON' : 'Split';
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

async function copyVfo(direction) {
    try {
        await fetch(`/api/cat/copy-vfo/${direction}`, { method: 'POST' });
    } catch {}
}
window.copyVfo = copyVfo;

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
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/radioHub")
    .withAutomaticReconnect()
    .build();

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
    const select = document.getElementById(`modeSelect${receiver}`);
    if (select) {
        select.value = mode;
    } else {

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
    const label = document.getElementById('micGainLabel');
    if (!label) return;

    // Data modes where "MIC Gain" actually controls Data Out level
    const dataModes = ['DATA-U', 'DATA-L', 'PSK', 'DATA-FM', 'DATA-FM-N', 'RTTY-U', 'RTTY-L'];

    if (dataModes.includes(mode)) {
        label.textContent = 'Data Out Gain';
    } else {
        label.textContent = 'MIC Gain';
    }
}

// First SignalR RadioStateUpdate handler (outer scope).
// Handles ModeA/B, FrequencyA/B, PowerA/B updates pushed from the backend.
connection.on("RadioStateUpdate", function (update) {

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
        if (window.IfWidth && window._radioModel) {
            window.IfWidth.rebuildIfWidthSelect(
                document.getElementById('ifWidthSelectA'), window._radioModel, update.value);
        }
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('modeA', update.value);
        if (window.voiceAnnounce) window.voiceAnnounce.sayMode('A', update.value);
        if (window.audioFilter && window.audioFilter.onModeChanged) window.audioFilter.onModeChanged('A', update.value);
    }
    if (update.property === "ModeB") {
        updateModeSelect('B', update.value);
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ mode: update.value });
        updateContourSliderBounds('B');
        if (typeof window._updateSquelchVisibility === 'function') window._updateSquelchVisibility('B', update.value);
        if (window.IfWidth && window._radioModel) {
            window.IfWidth.rebuildIfWidthSelect(
                document.getElementById('ifWidthSelectB'), window._radioModel, update.value);
        }
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('modeB', update.value);
        if (window.voiceAnnounce) window.voiceAnnounce.sayMode('B', update.value);
        if (window.audioFilter && window.audioFilter.onModeChanged) window.audioFilter.onModeChanged('B', update.value);
    }

    // --- ANTENNA CHANGE ---
    // The radio does not auto-broadcast AN changes from the front panel,
    // so MeterPollingService polls AN0/AN1 every couple of seconds and
    // routes the response through the dispatcher → RadioStateService →
    // SignalR. The antenna control is a <select>, not a radio-button group,
    // so we update its .value directly.
    if (update.property === "AntennaA") {
        const sel = document.getElementById('antennaSelectA');
        if (sel) sel.value = update.value;
    }
    if (update.property === "AntennaB") {
        const sel = document.getElementById('antennaSelectB');
        if (sel) sel.value = update.value;
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
        try { state.lastBackendFreq.A = update.value; } catch (_) { /* state lives in IIFE scope only */ }
        try { updateFrequencyDisplay('A', update.value); } catch (e) { console.error('updateFrequencyDisplay A error:', e); }
        try { window.dispatchEvent(new CustomEvent('radioFrequencyUpdate', { detail: { receiver: 'A', hz: update.value } })); }
        catch (e) { console.error('radioFrequencyUpdate dispatch error:', e); }
        try { if (window.syncSegmentSelectToFrequency) window.syncSegmentSelectToFrequency('A', update.value); }
        catch (e) { console.error('syncSegmentSelectToFrequency A error:', e); }
    }
    if (update.property === "FrequencyB") {
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('freqHzB', update.value);
        try { state.lastBackendFreq.B = update.value; } catch (_) { /* state lives in IIFE scope only */ }
        try { updateFrequencyDisplay('B', update.value); } catch (e) { console.error('updateFrequencyDisplay B error:', e); }
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
    }
    if (update.property === "BandB") {
        // ...removed debug logging...
        updateBandButton('B', update.value);
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('bandB', update.value);
        if (window.voiceAnnounce) window.voiceAnnounce.sayBand('B', update.value);
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
        // Update both the per-VFO slider (dual-receiver layout) AND the
        // unified `powerSlider` used on single-receiver radio layouts.
        // The old code only updated powerSliderA, so on FTdx10 a front-
        // panel power change moved the displayed value but left the slider
        // visually frozen at the previous position — reported by SP3L-Jacek
        // as #36 on v2.3.6.
        const sliderA = document.getElementById('powerSliderA');
        if (sliderA) sliderA.value = update.value;
        const slider = document.getElementById('powerSlider');
        if (slider) {
            slider.value = update.value;
            // Repaint the fill-percentage CSS custom property so the
            // visual progress track matches the new position.
            if (typeof updateSliderFill === 'function') updateSliderFill(slider);
        }
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
        if (typeof window.handleTxStateForTimeout === 'function') {
            window.handleTxStateForTimeout(!!update.value);
        }
        if (window.voiceAnnounce) window.voiceAnnounce.sayTxState(!!update.value);
    }
    if (update.property === "TxVfo") {
        txVfo = update.value;
        updateTxButton();
        applyVfoActiveStyling();
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('txVfo', update.value);
    }
    if (update.property === "ActiveVfo") {
        activeVfo = update.value;
        applyVfoActiveStyling();
        // In normal mode on a single-receiver radio, the TX button position
        // follows activeVfo (the TX VFO IS the active VFO; FT doesn't move).
        updateTxButton();
        // R8 (Jacek SP3L #34, 2026-06-21): in split mode the TX VFO is the
        // opposite of active, so the SPLIT TX badge and the red border have
        // to switch panels whenever the active VFO changes.
        updateSplitButton();
    }

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
        if (typeof window.updateToolbarStatus === 'function') window.updateToolbarStatus('split', update.value);
    }

    // --- METER UPDATES ---
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
        const selectEl = document.getElementById('roofingFilterSelectA');
        if (selectEl) selectEl.value = update.value;
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ roofingCode: update.value });
        updateContourSliderBounds('A');
    }
    if (update.property === "RoofingFilterB") {
        const selectEl = document.getElementById('roofingFilterSelectB');
        if (selectEl) selectEl.value = update.value;
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ roofingCode: update.value });
        updateContourSliderBounds('B');
    }

    // --- AGC ---
    if (update.property === "AgcA") {
        const selectEl = document.getElementById('agcSelectA');
        // Values 5/6 (AUTO-FAST/MID/SLOW) are normalised to 4 (AUTO) by the dispatcher,
        // but guard here too in case of a race.
        if (selectEl) selectEl.value = (update.value === "5" || update.value === "6") ? "4" : update.value;
    }
    if (update.property === "AgcB") {
        const selectEl = document.getElementById('agcSelectB');
        if (selectEl) selectEl.value = (update.value === "5" || update.value === "6") ? "4" : update.value;
    }

    // --- IPO/AMP ---
    if (update.property === "IpoA") {
        const el = document.getElementById('ipoSelectA');
        if (el) el.value = update.value;
    }
    if (update.property === "IpoB") {
        const el = document.getElementById('ipoSelectB');
        if (el) el.value = update.value;
    }

    // --- ATTENUATOR ---
    if (update.property === "AttA") {
        const el = document.getElementById('attSelectA');
        if (el) el.value = update.value;
    }
    if (update.property === "AttB") {
        const el = document.getElementById('attSelectB');
        if (el) el.value = update.value;
    }

    // --- NOISE REDUCTION ---
    if (update.property === "NrA") {
        const el = document.getElementById('nrSelectA');
        if (el) el.value = update.value;
    }
    if (update.property === "NrB") {
        const el = document.getElementById('nrSelectB');
        if (el) el.value = update.value;
    }

    // --- MANUAL NOTCH FREQUENCY ---
    if (update.property === "ManualNotchFreqA") {
        const el = document.getElementById('manualNotchFreqA');
        if (el) { el.value = update.value; document.getElementById('manualNotchFreqValueA').textContent = update.value + ' Hz'; }
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ manualNotchFreqHz: parseInt(update.value) || 800 });
    }
    if (update.property === "ManualNotchFreqB") {
        const el = document.getElementById('manualNotchFreqB');
        if (el) { el.value = update.value; document.getElementById('manualNotchFreqValueB').textContent = update.value + ' Hz'; }
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ manualNotchFreqHz: parseInt(update.value) || 800 });
    }

    // --- NOISE BLANKER ---
    if (update.property === "NbA") {
        const el = document.getElementById('nbSelectA');
        if (el) el.value = update.value;
    }
    if (update.property === "NbB") {
        const el = document.getElementById('nbSelectB');
        if (el) el.value = update.value;
    }

    // --- AUTO NOTCH ---
    if (update.property === "AutoNotchA") {
        const el = document.getElementById('autoNotchSelectA');
        if (el) el.value = update.value;
    }
    if (update.property === "AutoNotchB") {
        const el = document.getElementById('autoNotchSelectB');
        if (el) el.value = update.value;
    }

    // --- IF WIDTH ---
    if (update.property === "IfWidthA") {
        const el = document.getElementById('ifWidthSelectA');
        if (el) {
            const exists = Array.from(el.options).some(o => o.value === String(update.value));
            if (exists) el.value = update.value;
        }
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ ifWidthCode: update.value });
        updateContourSliderBounds('A');
    }
    if (update.property === "IfWidthB") {
        const el = document.getElementById('ifWidthSelectB');
        if (el) {
            const exists = Array.from(el.options).some(o => o.value === String(update.value));
            if (exists) el.value = update.value;
        }
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ ifWidthCode: update.value });
        updateContourSliderBounds('B');
    }

    // --- IF SHIFT ---
    if (update.property === "IfShiftA" && !ifShiftDragging.A) {
        const slider = document.getElementById('ifShiftSliderA');
        const label = document.getElementById('ifShiftValueA');
        if (slider) slider.value = update.value;
        if (label) label.textContent = update.value;
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ ifShiftHz: parseInt(update.value) || 0 });
    }
    if (update.property === "IfShiftB" && !ifShiftDragging.B) {
        const slider = document.getElementById('ifShiftSliderB');
        const label = document.getElementById('ifShiftValueB');
        if (slider) slider.value = update.value;
        if (label) label.textContent = update.value;
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ ifShiftHz: parseInt(update.value) || 0 });
    }

    // --- CLARIFIER ---
    if (update.property === "RxClarOn") {
        rxClarOn = update.value === true || update.value === 'true' || update.value === 1;
        const sel = document.getElementById('clarModeSelect');
        if (sel) sel.value = rxClarOn && txClarOn ? 'rxtx' : rxClarOn ? 'rx' : txClarOn ? 'tx' : 'off';
    }
    if (update.property === "TxClarOn") {
        txClarOn = update.value === true || update.value === 'true' || update.value === 1;
        const sel = document.getElementById('clarModeSelect');
        if (sel) sel.value = rxClarOn && txClarOn ? 'rxtx' : rxClarOn ? 'rx' : txClarOn ? 'tx' : 'off';
    }
    if (update.property === "ClarifierOffsetA") {
        clarOffsets.A = parseInt(update.value) || 0;
        if (clarVfo === 'A') {
            const slider = document.getElementById('clarOffsetSlider');
            const label  = document.getElementById('clarOffsetValue');
            if (slider) slider.value = clarOffsets.A;
            if (label)  label.textContent = clarOffsets.A;
        }
    }
    if (update.property === "ClarifierOffsetB") {
        clarOffsets.B = parseInt(update.value) || 0;
        if (clarVfo === 'B') {
            const slider = document.getElementById('clarOffsetSlider');
            const label  = document.getElementById('clarOffsetValue');
            if (slider) slider.value = clarOffsets.B;
            if (label)  label.textContent = clarOffsets.B;
        }
    }

    // --- CONTOUR ---
    if (update.property === "ContourOnA") {
        contourState.A.on = update.value === true || update.value === 'true' || update.value === 1;
        _updateContourBtn('A');
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ contourOn: contourState.A.on });
    }
    if (update.property === "ContourOnB") {
        contourState.B.on = update.value === true || update.value === 'true' || update.value === 1;
        _updateContourBtn('B');
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ contourOn: contourState.B.on });
    }
    if (update.property === "ContourFreqA") {
        contourState.A.freqHz = parseInt(update.value) || 800;
        const slider = document.getElementById('contourFreqSliderA');
        const label  = document.getElementById('contourFreqValueA');
        if (slider) slider.value = contourState.A.freqHz;
        if (label)  label.textContent = contourState.A.freqHz + ' Hz';
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ contourFreqHz: contourState.A.freqHz });
    }
    if (update.property === "ContourFreqB") {
        contourState.B.freqHz = parseInt(update.value) || 800;
        const slider = document.getElementById('contourFreqSliderB');
        const label  = document.getElementById('contourFreqValueB');
        if (slider) slider.value = contourState.B.freqHz;
        if (label)  label.textContent = contourState.B.freqHz + ' Hz';
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ contourFreqHz: contourState.B.freqHz });
    }

    // --- APF ---
    if (update.property === "ApfOnA") {
        apfState.A.on = update.value === true || update.value === 'true' || update.value === 1;
        _updateApfBtn('A');
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ apfOn: apfState.A.on });
    }
    if (update.property === "ApfOnB") {
        apfState.B.on = update.value === true || update.value === 'true' || update.value === 1;
        _updateApfBtn('B');
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ apfOn: apfState.B.on });
    }
    if (update.property === "ApfFreqA") {
        apfState.A.freqHz = parseInt(update.value) || 0;
        const slider = document.getElementById('apfFreqSliderA');
        const label  = document.getElementById('apfFreqValueA');
        if (slider) slider.value = apfState.A.freqHz;
        if (label)  label.textContent = apfState.A.freqHz + ' Hz';
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ apfFreqHz: apfState.A.freqHz });
    }
    if (update.property === "ApfFreqB") {
        apfState.B.freqHz = parseInt(update.value) || 0;
        const slider = document.getElementById('apfFreqSliderB');
        const label  = document.getElementById('apfFreqValueB');
        if (slider) slider.value = apfState.B.freqHz;
        if (label)  label.textContent = apfState.B.freqHz + ' Hz';
        if (window.filterScopePanelB) window.filterScopePanelB.setState({ apfFreqHz: apfState.B.freqHz });
    }

    // --- MANUAL NOTCH ---
    if (update.property === "ManualNotchA") {
        const el = document.getElementById('manualNotchSelectA');
        if (el) el.value = update.value;
        if (window.filterScopePanelA) window.filterScopePanelA.setState({ manualNotchOn: update.value === '1' });
    }
    if (update.property === "ManualNotchB") {
        const el = document.getElementById('manualNotchSelectB');
        if (el) el.value = update.value;
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
        const el = document.getElementById('nbLevelSelectA');
        if (el) el.value = update.value;
    }
    if (update.property === "NbLevelB") {
        const el = document.getElementById('nbLevelSelectB');
        if (el) el.value = update.value;
    }

    // --- NR LEVEL (DNR algorithm on FTdx10) ---
    if (update.property === "NrLevelA") {
        const el = document.getElementById('nrLevelSelectA');
        if (el) el.value = update.value;
    }
    if (update.property === "NrLevelB") {
        const el = document.getElementById('nrLevelSelectB');
        if (el) el.value = update.value;
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
        if (l) l.textContent = (300 + parseInt(update.value) * 10) + ' Hz';
    }
    if (update.property === "CwSpeed") {
        const s = document.getElementById('cwSpeedSlider'); const l = document.getElementById('cwSpeedValue');
        if (s) s.value = update.value; if (l) l.textContent = update.value;
    }
    if (update.property === "CwBreakIn") {
        const el = document.getElementById('cwBreakInSelect'); if (el) el.value = update.value;
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

// Fetch and apply band button state from the backend on page load
async function updateBandButtonsFromBackend() {
    try {
        const response = await fetch('/api/cat/status');
        if (!response.ok) return;
        const data = await response.json();
        // Update global radioModel if present
        if (data.radioModel) {
            state.radioModel = data.radioModel;
            // Always call updatePowerSliderMax to use latest radioModel
            updatePowerSliderMax();
        }
        if (data.vfoA && data.vfoA.band) {
            document.querySelectorAll('input[name="band-A"]').forEach(radio => {
                radio.checked = (radio.value.toLowerCase() === data.vfoA.band.toLowerCase());
            });
            syncBandAriaChecked('A');
        }
        if (data.vfoB && data.vfoB.band) {
            document.querySelectorAll('input[name="band-B"]').forEach(radio => {
                radio.checked = (radio.value.toLowerCase() === data.vfoB.band.toLowerCase());
            });
            syncBandAriaChecked('B');
        }
    } catch (error) {
        // ...removed debug logging...
    }
}


// Update band button selection for a specific receiver (called via SignalR)
function updateBandButton(receiver, band) {
    // ...removed debug logging...
    if (!band) {
        // ...removed debug logging...
        return;
    }
    const bandLower = band.toLowerCase();
    const inputs = document.querySelectorAll(`input[name="band-${receiver}"]`);
    // ...removed debug logging...

    let foundMatch = false;
    inputs.forEach(radio => {
        const matches = (radio.value.toLowerCase() === bandLower);
        if (matches) {
            foundMatch = true;
            // ...removed debug logging...
        }
        radio.checked = matches;
    });

    if (typeof syncBandAriaChecked === 'function') syncBandAriaChecked(receiver);

    if (!foundMatch) {
        // ...removed debug logging...
    }
    // ...removed debug logging...
}

// Sync aria-checked and tabindex on band-radio-label[role="radio"] elements
// after the underlying radio input's checked state is changed programmatically.
function syncBandAriaChecked(receiver) {
    document.querySelectorAll(`input[name="band-${receiver}"]`).forEach(input => {
        const label = input.closest('label[role="radio"]');
        if (!label) return;
        label.setAttribute('aria-checked', input.checked ? 'true' : 'false');
        label.tabIndex = input.checked ? 0 : -1;
    });
}

// Outer DOMContentLoaded - initial UI wiring
window.addEventListener('DOMContentLoaded', () => {
    pollInitStatus();
        updateBandButtonsFromBackend();

    // VFO-B show/hide toggle — click handler is in Index.cshtml (applyVisibility).
    // Only set the aria-label here; do not add a second click listener.
    document.getElementById('vfoBToggleBtn')
        ?.setAttribute('aria-label', 'Show or hide VFO B panel');

    // Split / Swap VFO button handlers
    document.getElementById('splitBtn')?.addEventListener('click', () => setSplit(splitMode > 0 ? 0 : 1));
    document.getElementById('quickSplitBtn')?.addEventListener('click', () => setSplit(2));
    document.getElementById('swapVfoBtn')?.addEventListener('click', swapVfo);
    document.getElementById('copyBtoABtn')?.addEventListener('click', () => copyVfo('ba'));
    document.getElementById('copyAtoBBtn')?.addEventListener('click', () => copyVfo('ab'));

    // Clarifier: seed JS state from server-rendered HTML values
    const clarSlider = document.getElementById('clarOffsetSlider');
    if (clarSlider) clarOffsets.A = parseInt(clarSlider.value) || 0;
    const clarSel = document.getElementById('clarModeSelect');
    if (clarSel) {
        const initMode = clarSel.value || 'off';
        rxClarOn = initMode === 'rx' || initMode === 'rxtx';
        txClarOn = initMode === 'tx' || initMode === 'rxtx';
    }

    // Contour/APF: seed JS state from server-rendered HTML values
    for (const vfo of ['A', 'B']) {
        const cBtn = document.getElementById(`contourBtn${vfo}`);
        if (cBtn) contourState[vfo].on = cBtn.classList.contains('btn-success');
        const cSlider = document.getElementById(`contourFreqSlider${vfo}`);
        if (cSlider) contourState[vfo].freqHz = parseInt(cSlider.value) || 800;
        const aBtn = document.getElementById(`apfBtn${vfo}`);
        if (aBtn) apfState[vfo].on = aBtn.classList.contains('btn-success');
        const aSlider = document.getElementById(`apfFreqSlider${vfo}`);
        if (aSlider) apfState[vfo].freqHz = parseInt(aSlider.value) || 0;
    }

    // Event delegation for band button changes
    document.addEventListener('change', function(e) {
        if (e.target.type === 'radio' && e.target.name && e.target.name.startsWith('band-')) {
            const receiver = e.target.getAttribute('data-receiver');
            const band = e.target.value;
            syncBandAriaChecked(receiver);
            if (receiver && band && window.radioControl && window.radioControl.setBand) {
                window.radioControl.setBand(receiver, band);
            }
        }
    });

    // Keyboard navigation for band radiogroups (arrow keys move between bands)
    document.querySelectorAll('.band-radio-grid[role="radiogroup"]').forEach(grid => {
        grid.addEventListener('keydown', function(e) {
            const radios = Array.from(grid.querySelectorAll('label[role="radio"]'));
            const idx = radios.indexOf(document.activeElement);
            if (idx === -1) return;
            let next = -1;
            if (e.key === 'ArrowRight' || e.key === 'ArrowDown')      next = (idx + 1) % radios.length;
            else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp')    next = (idx - 1 + radios.length) % radios.length;
            else return;
            e.preventDefault();
            radios[next].focus();
            radios[next].click();
        });
    });
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

// IF Shift slider: send only on release, block SignalR updates while dragging
const ifShiftDragging = { A: false, B: false };

function setupIfShiftSlider(receiver) {
    const slider = document.getElementById(`ifShiftSlider${receiver}`);
    if (!slider) return;
    const sendShift = () => {
        if (window.radioControl) window.radioControl.setIfShift(receiver, parseInt(slider.value));
    };
    slider.addEventListener('mousedown',  () => { ifShiftDragging[receiver] = true; });
    slider.addEventListener('touchstart', () => { ifShiftDragging[receiver] = true; }, { passive: true });
    // Document-level mouseup catches releases anywhere, not just over the slider element
    document.addEventListener('mouseup', () => {
        if (ifShiftDragging[receiver]) { ifShiftDragging[receiver] = false; sendShift(); }
    });
    slider.addEventListener('touchend',   () => { ifShiftDragging[receiver] = false; sendShift(); });
    // Keyboard arrow keys fire 'change' after the value settles
    slider.addEventListener('change', sendShift);
}

function resetIfShift(receiver) {
    const slider = document.getElementById(`ifShiftSlider${receiver}`);
    const label  = document.getElementById(`ifShiftValue${receiver}`);
    if (slider) slider.value = 0;
    if (label)  label.textContent = '0';
    if (window.radioControl) window.radioControl.setIfShift(receiver, 0);
}
window.resetIfShift = resetIfShift;

function selectClarVfo(vfo) {
    clarVfo = vfo;
    document.getElementById('clarVfoABtn')?.classList.toggle('active', vfo === 'A');
    document.getElementById('clarVfoBBtn')?.classList.toggle('active', vfo === 'B');
    const offset = clarOffsets[vfo];
    const slider = document.getElementById('clarOffsetSlider');
    const label  = document.getElementById('clarOffsetValue');
    if (slider) slider.value = offset;
    if (label)  label.textContent = offset;
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
    const slider = document.getElementById('clarOffsetSlider');
    const label  = document.getElementById('clarOffsetValue');
    if (slider) slider.value = 0;
    if (label)  label.textContent = '0';
    await _setClarifier(clarVfo, rxClarOn, txClarOn, 0);
}

async function nudgeClarifier(deltaHz) {
    const vfo = clarVfo;
    let newOffset = Math.round(((clarOffsets[vfo] || 0) + deltaHz) / 10) * 10;
    newOffset = Math.max(-9990, Math.min(9990, newOffset));
    clarOffsets[vfo] = newOffset;
    const slider = document.getElementById('clarOffsetSlider');
    const label  = document.getElementById('clarOffsetValue');
    if (slider) slider.value = newOffset;
    if (label)  label.textContent = newOffset;
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
    try {
        if (btn) { btn.disabled = true; btn.textContent = '…'; }
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
            if (btn) {
                btn.textContent = '✓ Saved';
                btn.classList.replace('btn-outline-secondary', 'btn-success');
                setTimeout(() => {
                    btn.textContent = 'Save to Mem';
                    btn.classList.replace('btn-success', 'btn-outline-secondary');
                    btn.disabled = false;
                }, 1500);
            }
        } else {
            if (status) status.textContent = `Failed to save VFO ${vfo}`;
            if (btn) { btn.textContent = '✗ Failed'; btn.disabled = false; }
        }
    } catch (e) {
        if (status) status.textContent = `Error saving VFO ${vfo}`;
        if (btn) { btn.textContent = '✗ Error'; btn.disabled = false; }
        console.error('Save to memory failed:', e);
    }
}
window.saveVfoToMemory = saveVfoToMemory;

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
    const select = document.getElementById(`ifWidthSelect${receiver}`);
    if (!select) return;
    // Default is the last option (widest bandwidth — 3.0 kHz for FTdx101, 3.4 kHz for FTdx10)
    const defaultOpt = select.options[select.options.length - 1];
    if (!defaultOpt) return;
    select.value = defaultOpt.value;
    if (window.radioControl) window.radioControl.setIfWidth(receiver, defaultOpt.value);
}
window.resetIfWidth = resetIfWidth;

function _updateContourBtn(vfo) {
    const btn = document.getElementById(`contourBtn${vfo}`);
    if (!btn) return;
    const on = contourState[vfo].on;
    btn.textContent = on ? 'Contour On' : 'Contour Off';
    btn.className = btn.className.replace(/btn-success|btn-outline-secondary/g, '').trim();
    btn.classList.add(on ? 'btn-success' : 'btn-outline-secondary');
}

function _updateApfBtn(vfo) {
    const btn = document.getElementById(`apfBtn${vfo}`);
    if (!btn) return;
    const on = apfState[vfo].on;
    btn.textContent = on ? 'APF On' : 'APF Off';
    btn.className = btn.className.replace(/btn-success|btn-outline-secondary/g, '').trim();
    btn.classList.add(on ? 'btn-success' : 'btn-outline-secondary');
}

async function toggleContour(vfo) {
    const newOn = !contourState[vfo].on;
    contourState[vfo].on = newOn;
    _updateContourBtn(vfo);
    const panel = vfo === 'B' ? window.filterScopePanelB : window.filterScopePanelA;
    if (panel) panel.setState({ contourOn: newOn });
    try {
        await fetch(`/api/cat/contour/${vfo.toLowerCase()}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ on: newOn, freqHz: contourState[vfo].freqHz })
        });
    } catch (e) { console.error('Contour toggle failed:', e); }
}
window.toggleContour = toggleContour;

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
// preserved as an outer clamp via the slider's initial min/max values,
// so we never let the user set a value the radio can't accept. If the
// existing contour value falls outside the new (narrower) range, clamp
// it in place and send the clamped value to the radio.
//
// Called from the SignalR handlers for ModeA/ModeB, IfWidthA/IfWidthB,
// and the per-VFO roofing-filter changes; also once at startup after the
// FilterScopePanel instances are constructed.
function updateContourSliderBounds(vfo) {
    const panel = window['filterScopePanel' + vfo];
    if (!panel || typeof panel.getPassband !== 'function') return;
    const slider = document.getElementById('contourFreqSlider' + vfo);
    if (!slider) return;

    // Cache the radio's hard limits on first run (the values rendered
    // server-side from the radio model: 100..3200 for FTdx101, 100..4000
    // for FTDX3000). After that, future updates only narrow within those.
    if (slider._hardMin == null) slider._hardMin = parseInt(slider.min);
    if (slider._hardMax == null) slider._hardMax = parseInt(slider.max);

    const { lo, hi } = panel.getPassband();
    const newMin = Math.max(slider._hardMin, Math.round(lo));
    const newMax = Math.min(slider._hardMax, Math.round(hi));
    if (newMin >= newMax) return;

    // Capture the OLD value before changing min/max — once we set the new
    // max, the browser auto-clamps slider.value to fit, so reading it
    // afterwards would always give the clamped (= new max) value and we'd
    // never realise the value had actually moved.
    const oldVal  = parseInt(slider.value);
    const clamped = Math.max(newMin, Math.min(newMax, oldVal));

    slider.min = newMin;
    slider.max = newMax;

    if (clamped !== oldVal) {
        slider.value = clamped;
        const label = document.getElementById('contourFreqValue' + vfo);
        if (label) label.textContent = clamped + ' Hz';
        setContourFreq(vfo, clamped);  // updates panel state + sends CAT
    }
}
window.updateContourSliderBounds = updateContourSliderBounds;

async function toggleApf(vfo) {
    const newOn = !apfState[vfo].on;
    apfState[vfo].on = newOn;
    _updateApfBtn(vfo);
    const panel = vfo === 'B' ? window.filterScopePanelB : window.filterScopePanelA;
    if (panel) panel.setState({ apfOn: newOn });
    try {
        await fetch(`/api/cat/apf/${vfo.toLowerCase()}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ on: newOn, freqHz: apfState[vfo].freqHz })
        });
    } catch (e) { console.error('APF toggle failed:', e); }
}
window.toggleApf = toggleApf;

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

document.addEventListener('DOMContentLoaded', function() {
    setupIfShiftSlider('A');
    setupIfShiftSlider('B');
});


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
        lastBackendFreq: { A: null, B: null },
        lastBand: { A: null, B: null },
        lastMode: { A: null, B: null },
        lastAntenna: { A: null, B: null },
        lastPower: { A: 100, B: 100 },
        maxPower: 200,
        radioModel: 'FTdx101MP',
        pollingInterval: null,
        operationInProgress: false,
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

    // Update band, mode, and antenna radio/toggle buttons to reflect current state.
    // NOTE: The Razor page renders mode buttons as <input type="radio" name="modeA" value="USB">
    // and band/antenna buttons similarly.  We update .checked directly.
    function highlightButtons(receiver, band, mode, antenna) {
        // Band buttons (rendered by _BandButtonsPartial as input[name="band-A/B"])
        document.querySelectorAll(`input[name="band-${receiver}"]`).forEach(btn => {
            btn.checked = (btn.value === band);
        });
        if (typeof syncBandAriaChecked === 'function') syncBandAriaChecked(receiver);

        // Mode dropdown - update the selected value
        const modeSelect = document.getElementById(`modeSelect${receiver}`);
        if (modeSelect && mode) {
            modeSelect.value = mode;
        }

        // Antenna buttons
        document.querySelectorAll(`input[name="antenna${receiver}"]`).forEach(btn => {
            btn.checked = (btn.value === antenna);
        });
    }

    // Update ONLY mode and antenna selectors (not bands) - used by polling to avoid overwriting user's band selection
    function updateModeAndAntennaButtons(receiver, mode, antenna) {
        // Mode dropdown
        const modeSelect = document.getElementById(`modeSelect${receiver}`);
        if (modeSelect && mode) {
            modeSelect.value = mode;
        }

        // Antenna is a <select> (#antennaSelectA / #antennaSelectB), not a
        // radio-button group — earlier code queried input[name="antennaA"]
        // which never matched anything, so polling-based antenna updates
        // were silently broken.
        const antennaSelect = document.getElementById(`antennaSelect${receiver}`);
        if (antennaSelect && antenna) {
            antennaSelect.value = antenna;
        }
    }

    // Update roofing filter dropdown
    function updateRoofingFilterSelect(receiver, filterCode) {
        const selectEl = document.getElementById(`roofingFilterSelect${receiver}`);
        if (selectEl && filterCode) {
            selectEl.value = filterCode;
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
            clearTimeout(display._debounceTimer);
            display._debounceTimer = setTimeout(() => {
                setFrequency(receiver, newFreq);
                state.lastSentFreq[receiver] = newFreq;
                state.localFreq[receiver] = null;
                // IMPORTANT: keep state.editing=true here. The polling tick
                // at ~500 ms will reset it to false once it sees the radio
                // confirm data.vfoA.frequency === state.lastSentFreq.A (see
                // the reset block in fetchRadioStatus). If we clear editing
                // now, the very next polling tick re-renders the display
                // with whatever frequency the radio is still reporting --
                // typically the OLD value, because we just sent the new one
                // ~tens of ms ago and the radio hasn't echoed back yet.
                // That race shows up as the digit "flipping back then
                // settling on the new value" the user reported.
            }, 600);
        }

        // Auto-select the kHz position when the user hits an arrow / ▲ / ▼
        // without first picking a digit. Defaults to "4th from the right"
        // so the first action moves something audible rather than a 1 Hz
        // tick the user can't hear.
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
                state.editing[receiver] = true;
                state.localFreq[receiver] = parseInt(digits.map(d => d.textContent).join(''));
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
        const didPause = pausePolling();
        try {
            highlightButtons(receiver, band, state.lastMode[receiver], state.lastAntenna[receiver]);
            state.lastBand[receiver] = band;
            const response = await fetch(`/api/cat/band/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ band })
            });
        } catch (error) {
        } finally {
            if (didPause) {
                resumePolling();
            }
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
        const didPause = pausePolling();
        try {
            highlightButtons(receiver, state.lastBand[receiver], state.lastMode[receiver], antenna);
            state.lastAntenna[receiver] = antenna;
            const response = await fetch(`/api/cat/antenna/${receiver.toLowerCase()}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ antenna })
            });
        } catch (error) {
        } finally {
            if (didPause) {
                resumePolling();
            }
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
        const didPause = pausePolling();

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
                // Update dropdown to show actual filter
                const selectEl = document.getElementById(`roofingFilterSelect${receiver}`);
                if (selectEl && data.filter) {
                    selectEl.value = data.filter;
                }
            }
        } catch (error) {
            showMessageBox('Error setting roofing filter. Check console for details.', 'Error');
        } finally {
            if (didPause) {
                resumePolling();
            }
        }
    }

    function pausePolling() {
        if (state.pollingInterval && !state.operationInProgress) {
            state.operationInProgress = true;
            return true;
        }
        return false;
    }

    function resumePolling() {
        if (state.operationInProgress) {
            state.operationInProgress = false;
            setTimeout(fetchRadioStatus, 500);
        }
    }

    // Full status poll - updates frequencies, S-meter, band/mode/antenna buttons, and power
    async function fetchRadioStatus() {
        if (state.operationInProgress) {
            return;
        }
        try {
            const response = await fetch('/api/cat/status');
            if (!response.ok) {
                return;
            }
            const data = await response.json();

            if (data.radioModel !== undefined) {
                state.radioModel = data.radioModel;
            }
            // Always call updatePowerSliderMax to use latest radioModel
            if (state.radioModel) {
                const model = state.radioModel.toLowerCase();
                if (model === "ftdx101d") {
                    state.maxPower = 100;
                } else if (model === "ftdx101mp") {
                    state.maxPower = 200;
                } else {
                    const maxPower = (data.maxPower !== undefined) ? data.maxPower : 200;
                    state.maxPower = maxPower;
                }
            } else {
                const maxPower = (data.maxPower !== undefined) ? data.maxPower : 200;
                state.maxPower = maxPower;
            }
            updatePowerSliderMax();

            state.lastMode.A = data.vfoA.mode;
            state.lastMode.B = data.vfoB.mode;
            state.lastAntenna.A = data.vfoA.antenna;
            state.lastAntenna.B = data.vfoB.antenna;

            // Show set power value (not meter reading) when not transmitting
            let powerValue = 100;
            if (data.vfoA && data.vfoA.power !== undefined) {
                powerValue = data.vfoA.power;
                state.lastPower.A = data.vfoA.power;
            } else if (state.lastPower && typeof state.lastPower === 'object' && state.lastPower.A !== undefined) {
                powerValue = state.lastPower.A;
            }
            updatePowerSlider(null, powerValue);
            // TX meter (updatePowerMeter) will use RM5 during transmit only

            // Stop showing local frequency once backend confirms our sent value.
            // IMPORTANT: do NOT clear state.selectedIdx here. The user's
            // digit selection should survive a successful step so the next
            // ArrowUp / ▲ press acts on the same digit -- accessibility
            // users press these in rapid sequence and re-selecting every
            // time would be unusable. Selection is cleared explicitly when
            // the user clicks outside the display (see the document.click
            // handler inside initializeDigitInteraction).
            if (state.editing.A && state.lastSentFreq.A !== null && state.localFreq.A === null && data.vfoA.frequency === state.lastSentFreq.A) {
                state.editing.A = false;
            }
            if (state.editing.B && state.lastSentFreq.B !== null && state.localFreq.B === null && data.vfoB.frequency === state.lastSentFreq.B) {
                state.editing.B = false;
            }

            if (!state.editing.A) {
                updateFrequencyDisplay('A', data.vfoA.frequency);
                if (data.vfoA.frequency !== state.lastBackendFreq.A) {
                    state.lastBackendFreq.A = data.vfoA.frequency;
                    window.dispatchEvent(new CustomEvent('radioFrequencyUpdate', { detail: { receiver: 'A', hz: data.vfoA.frequency } }));
                }
            } else updateFrequencyDisplay('A', state.localFreq.A);

            if (!state.editing.B) {
                updateFrequencyDisplay('B', data.vfoB.frequency);
                if (data.vfoB.frequency !== state.lastBackendFreq.B) {
                    state.lastBackendFreq.B = data.vfoB.frequency;
                    window.dispatchEvent(new CustomEvent('radioFrequencyUpdate', { detail: { receiver: 'B', hz: data.vfoB.frequency } }));
                }
            } else updateFrequencyDisplay('B', state.localFreq.B);

            updateSMeter('A', data.vfoA.sMeter);
            updateSMeter('B', data.vfoB.sMeter);

            if (window.ftdx101Meters) {
                const metersFromState = {
                    PowerMeter:       data.powerMeter,
                    SWRMeter:         data.swrMeter,
                    CompressionMeter: data.compressionMeter,
                    ALCMeter:         data.alcMeter,
                    IDDMeter:         data.iddMeter,
                    VDDMeter:         data.vddMeter,
                    // Temperature intentionally omitted — the persisted value in radio_state.json
                    // can be stale (e.g. from a hot previous session). Live SignalR updates from
                    // MeterPollingService arrive within ~100ms and provide the first real reading.
                };
                for (const [prop, value] of Object.entries(metersFromState)) {
                    if (value !== undefined) {
                        const result = window.ftdx101Meters.handleMeterUpdate(prop, value);
                        updateMeterDomLabel(prop, result);
                    }
                }
            }

            // Update band buttons from polling (fixes WSJT-X and radio band changes)
            if (data.vfoA.band) {
                updateBandButton('A', data.vfoA.band);
                state.lastBand.A = data.vfoA.band;
            }
            if (data.vfoB.band) {
                updateBandButton('B', data.vfoB.band);
                state.lastBand.B = data.vfoB.band;
            }

            // Update mode and antenna buttons from polling
            updateModeAndAntennaButtons('A', data.vfoA.mode, data.vfoA.antenna);
            updateModeAndAntennaButtons('B', data.vfoB.mode, data.vfoB.antenna);

            // Update roofing filter dropdowns
            if (data.vfoA.roofingFilter) {
                updateRoofingFilterSelect('A', data.vfoA.roofingFilter);
            }
            if (data.vfoB.roofingFilter) {
                updateRoofingFilterSelect('B', data.vfoB.roofingFilter);
            }

            // Update MIC Gain / Data Out Gain label based on current mode (VFO A is main)
            updateMicGainLabel(data.vfoA.mode);


        } catch (error) {
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
        // Only one power control supported
        // Only update the label from the slider value, never from backend
        const display = document.getElementById('powerValue');
        const slider = document.getElementById('powerSlider');
        if (display && slider) {
            display.textContent = window.MeterFormatters.powerLabel(slider.value);
        }
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
        const slider = document.getElementById('powerSlider');
        const labelMax = document.getElementById('powerMaxLabel');
        const actualMax = state.radioModel
            ? window.modelMaxPower(state.radioModel)
            : (typeof maxPower === "number" ? maxPower : 200);

        if (slider) {
            slider.max = actualMax;
            slider.min = 5;
            if (parseInt(slider.value, 10) > actualMax) {
                slider.value = actualMax;
                const display = document.getElementById('powerValue');
                if (display) display.textContent = window.MeterFormatters.powerLabel(actualMax);
            }
            updateSliderFill(slider);
        }
        if (labelMax) labelMax.textContent = window.MeterFormatters.powerLabel(actualMax);
    }

    function updateSMeter(receiver, value) {
        // v2.4.0: restored as a real per-VFO pair (dual-receiver radios have
        // an independently calibrated S-meter per receiver, SM0;/SM1;).
        // On single-receiver radios receiver 'B' is a no-op below since
        // window.meterPanel has no 'smeterB' gauge (canvas doesn't exist —
        // MeterPanel._createGauges skipped it) and sMeterHistoryB.push() is
        // a no-op (canvas doesn't exist).
        const gaugeKey   = receiver === 'B' ? 'smeterB' : 'smeter';
        const history     = receiver === 'B' ? window.sMeterHistoryB : window.sMeterHistory;
        const canvasId    = receiver === 'B' ? 'sMeterCanvasB' : 'sMeterCanvas';
        const labelId     = receiver === 'B' ? 'sMeterValueB' : 'sMeterValue';

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
    }

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
    const s = document.getElementById('powerSlider'); if (s) updateSliderFill(s);
    fetchRadioStatus();
    state.pollingInterval = setInterval(fetchRadioStatus, 500);

    // Robustly track editing state for the power slider to prevent backend/UI jumps
    const powerSlider = document.getElementById('powerSlider');
    const powerDisplay = document.getElementById('powerValue');
    if (powerSlider && powerDisplay) {
        window.editingPower = false;
        // Set editingPower true on any user interaction
        powerSlider.addEventListener('input', function () {
            window.editingPower = true;
            powerDisplay.textContent = window.MeterFormatters.powerLabel(powerSlider.value);
        });
        powerSlider.addEventListener('mousedown', function () {
            window.editingPower = true;
        });
        powerSlider.addEventListener('touchstart', function () {
            window.editingPower = true;
        });
        powerSlider.addEventListener('focus', function () {
            window.editingPower = true;
        });
        // Reset editingPower on all possible end events
        powerSlider.addEventListener('change', function () {
            window.editingPower = false;
        });
        powerSlider.addEventListener('mouseup', function () {
            window.editingPower = false;
        });
        powerSlider.addEventListener('touchend', function () {
            window.editingPower = false;
        });
        powerSlider.addEventListener('mouseleave', function () {
            window.editingPower = false;
        });
        powerSlider.addEventListener('blur', function () {
            window.editingPower = false;
        });
        // Defensive: clear editingPower if window loses focus
        window.addEventListener('blur', function () {
            window.editingPower = false;
        });
    }

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
    // Band Segment Dropdown
    // -------------------------------------------------------------------------
    // Populates the segment select for a VFO based on the current band and
    // band plan, restores the last-used segment from localStorage, and tunes
    // the radio when the user picks a segment.

    function segmentStorageKey(vfo, band) {
        return `bandSeg_${vfo}_${band}`;
    }

    // Set the Segment dropdown to reflect whichever segment of the band
    // contains the current frequency. Called from the FrequencyA/B SignalR
    // handlers so the dropdown stays in sync when the operator tunes via
    // the radio's knob, the spectrum click, or the on-screen freq keyboard.
    // No-op if the band's dropdown hasn't been populated yet (e.g. on
    // initial connect before BandA arrives).
    function syncSegmentSelectToFrequency(vfo, hz) {
        const select = document.getElementById(`segmentSelect${vfo}`);
        if (!select || select.disabled) return;
        const band = state.lastBand && state.lastBand[vfo];
        if (!band) return;
        const plan = window.bandPlan || 'UK';
        if (!window.bandPlanData || !window.getBandSegmentForHz) {
            // Fallback if helper not loaded — use inline lookup against the plan.
            // Mirror the band-plan.js segmentForHz logic exactly, including the
            // "below-lowest → first segment" fallback, so 14.010 etc don't
            // produce a blank dropdown when the helper isn't loaded.
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
            if (select.value !== match) select.value = match;
            return;
        }
        const key = window.getBandSegmentForHz(plan, band, hz) || '';
        if (select.value !== key) select.value = key;
    }
    // Expose to the outer SignalR handler (FrequencyA/B), which lives outside
    // this IIFE and would otherwise get a ReferenceError trying to call it.
    window.syncSegmentSelectToFrequency = syncSegmentSelectToFrequency;

    function populateSegmentSelect(vfo, band) {
        const select = document.getElementById(`segmentSelect${vfo}`);
        if (!select) return;

        // Wait until band-plan.js has been imported by the module script.
        const bandPlanData = window.bandPlanData;
        const plan = window.bandPlan || 'UK';
        if (!bandPlanData) return;

        const segments = (bandPlanData[plan] || {})[band] || null;
        select.innerHTML = '';

        if (!segments) {
            const opt = document.createElement('option');
            opt.value = '';
            opt.textContent = '--';
            select.appendChild(opt);
            select.disabled = true;
            return;
        }

        select.disabled = false;
        const placeholder = document.createElement('option');
        placeholder.value = '';
        placeholder.textContent = '--';
        select.appendChild(placeholder);

        for (const [key, seg] of Object.entries(segments)) {
            const opt = document.createElement('option');
            opt.value = key;
            opt.textContent = seg.label;
            select.appendChild(opt);
        }

        // Restore last used segment for this band
        const saved = localStorage.getItem(segmentStorageKey(vfo, band));
        if (saved && select.querySelector(`option[value="${saved}"]`)) {
            select.value = saved;
        }
    }

    // Called when the user picks a segment from the dropdown.
    window.onSegmentChange = async function(vfo, segKey) {
        if (!segKey) return;
        const plan = window.bandPlan || 'UK';
        const bandPlanData = window.bandPlanData;
        if (!bandPlanData) return;

        // Determine the current band for this VFO
        const band = state.lastBand[vfo];
        if (!band) return;

        const segments = (bandPlanData[plan] || {})[band];
        if (!segments || !segments[segKey]) return;

        const { freq, mode } = segments[segKey];

        // Save preference
        localStorage.setItem(segmentStorageKey(vfo, band), segKey);

        // Set mode first so the radio doesn't shift frequency when mode changes,
        // then tune to the target frequency.
        if (window.radioControl) {
            const modeSelect = document.getElementById(`modeSelect${vfo}`);
            if (modeSelect) modeSelect.value = mode;
            await window.setMode(vfo, mode);
            await window.radioControl.setFrequency(vfo, freq);
        }
    };

    // Hook into the band state change: when lastBand is updated, repopulate
    // the segment select. We patch setBand and updateBandButton so both
    // UI-driven and SignalR-driven band changes trigger the update.
    const _origUpdateBandButton = window.updateBandButton;

    // Re-populate segments whenever band state changes. Skip if the band is
    // unchanged — the BandA SignalR event fires on every frequency change,
    // not only on real band transitions, so repopulating here would reset the
    // dropdown to its localStorage value and stomp on the auto-sync we did
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

    // Also update segment immediately when a band button is clicked (before poll)
    document.addEventListener('change', function(e) {
        if (e.target.type === 'radio' && e.target.name && e.target.name.startsWith('band-')) {
            const receiver = e.target.getAttribute('data-receiver');
            const band = e.target.value;
            if (receiver && band && window.bandPlanData) {
                populateSegmentSelect(receiver, band);
            }
        }
    });

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
    // default value". The dropdown UI value is restored by populateSegmentSelect
    // on DOMContentLoaded; the radio is NOT auto-tuned. If the user wants to
    // jump to a saved segment, they click the dropdown manually.

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


connection.start().catch(function (err) {
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
        ['vddMeterCanvas', 'iddMeterCanvas', 'tempMeterCanvas', 'compressionMeterCanvas'].forEach(id => {
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

// ── GitHub update check ───────────────────────────────────────────────────
(function () {
    const DISMISS_KEY_PREFIX = 'updateCheckDismissed_';

    function _dismissKey(version) { return DISMISS_KEY_PREFIX + version; }

    function _escHtml(s) {
        return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function _isNewer(latest, current) {
        const parse = v => v.split('.').map(n => parseInt(n, 10) || 0);
        const a = parse(latest);
        const b = parse(current);
        for (let i = 0; i < Math.max(a.length, b.length); i++) {
            const diff = (a[i] || 0) - (b[i] || 0);
            if (diff > 0) return true;
            if (diff < 0) return false;
        }
        return false;
    }

    function _dismiss(version) {
        try { localStorage.setItem(_dismissKey(version), '1'); } catch { /* private browsing */ }
        const el = document.getElementById('updateBanner');
        if (el) el.remove();
    }

    function _showUpdateBanner(version, releaseUrl) {
        if (document.getElementById('updateBanner')) return;
        try { if (localStorage.getItem(_dismissKey(version))) return; } catch { /* private browsing */ }
        const banner = document.createElement('div');
        banner.id = 'updateBanner';
        banner.style.cssText = [
            'position:fixed', 'top:50%', 'left:50%', 'transform:translate(-50%,-50%)', 'z-index:9999',
            'background:#1e2a38', 'border:1px solid #4a8abf', 'border-radius:8px',
            'padding:10px 14px', 'color:#cde', 'font-size:0.84rem',
            'box-shadow:0 4px 16px rgba(0,0,0,0.6)', 'max-width:340px', 'width:320px'
        ].join(';');
        banner.innerHTML =
            `<div style="display:flex;align-items:flex-start;gap:8px">` +
            `<div style="flex:1"><strong>Update available — v${_escHtml(version)}</strong><br>` +
            `<span style="color:#aab;font-size:0.78rem">A newer version of Yaesu Web Control is available.</span></div>` +
            `<button id="updateBannerDismissX" ` +
            `style="background:none;border:none;color:#aaa;cursor:pointer;font-size:1rem;line-height:1;padding:0" aria-label="Dismiss">✕</button>` +
            `</div>` +
            `<div style="margin-top:8px;display:flex;gap:8px">` +
            `<a href="${_escHtml(releaseUrl)}" target="_blank" rel="noopener" ` +
            `style="background:#1a4a7a;border:1px solid #4a8abf;color:#cde;padding:3px 10px;border-radius:4px;font-size:0.78rem;text-decoration:none">Download</a>` +
            `<button id="updateBannerDismissBtn" ` +
            `style="background:#2d2d44;border:1px solid #555;color:#aaa;padding:3px 10px;border-radius:4px;font-size:0.78rem;cursor:pointer">Dismiss</button>` +
            `</div>`;
        document.body.appendChild(banner);
        document.getElementById('updateBannerDismissX').addEventListener('click', () => _dismiss(version));
        document.getElementById('updateBannerDismissBtn').addEventListener('click', () => _dismiss(version));
    }

    async function _checkForUpdate() {
        const meta = document.querySelector('meta[name="x-app-version"]');
        if (!meta) return;
        const current = meta.content.trim();
        try {
            const resp = await fetch('https://api.github.com/repos/mm5agm/Yaesu_Web_Control/releases/latest', {
                headers: { Accept: 'application/vnd.github+json' }
            });
            if (!resp.ok) return;
            const data = await resp.json();
            const latest = (data.tag_name || '').replace(/^v/i, '');
            if (latest && _isNewer(latest, current)) {
                _showUpdateBanner(latest, data.html_url || 'https://github.com/mm5agm/Yaesu_Web_Control/releases');
            }
        } catch { /* network unavailable or rate limited — silently skip */ }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => setTimeout(_checkForUpdate, 3000));
    } else {
        setTimeout(_checkForUpdate, 3000);
    }
})();

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
