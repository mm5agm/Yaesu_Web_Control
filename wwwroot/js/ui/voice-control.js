// Voice control frontend.
//
// Wires each VFO's mic button (Index.cshtml: voiceMicBtnA / voiceMicBtnB) to
// /api/voice/start?vfo=A|B and /api/voice/stop on mousedown / mouseup (touch
// equivalents too). Listens for VoiceStatusUpdate over SignalR and reflects
// the recogniser state in that VFO's button colour + last-heard text.
//
// Only one SAPI recognition session can be live at a time (single
// SpeechRecognitionEngine on the backend), so the two buttons are
// independent controls over one shared engine, not two engines. Each
// VoiceStatusUpdate carries a `vfo` field identifying which button's press
// is being reported; a button ignores updates that aren't its own so the
// other button doesn't flicker.
//
// window.ywcVoiceEnabled (server-rendered in _Layout.cshtml) gates whether
// any mic button is shown at all. VFO B's button is additionally hidden on
// single-receiver radios via #vfoRow's data-single-receiver attribute.

(function () {
    'use strict';

    if (!window.ywcVoiceEnabled) return;

    // ---- Right-click voice-command help ----------------------------------
    //
    // Generated from the live grammar config via GET /api/voice/help, so the
    // on-screen list can never drift from what the radio actually responds to
    // (including phrases the user has customised). Fetched fresh each open so
    // config edits show up without a page reload; cheap and rarely triggered.

    function esc(s) {
        return String(s).replace(/[&<>"]/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
        });
    }

    function renderVoiceHelp(doc) {
        const parts = [];
        if (doc.intro) parts.push('<div class="vh-intro">' + esc(doc.intro) + '</div>');
        (doc.groups || []).forEach(function (g) {
            parts.push('<div class="vh-group">');
            parts.push('<h6>' + esc(g.name) + '</h6>');
            (g.items || []).forEach(function (item) {
                const examples = (item.examples || [])
                    .map(function (ex) { return ex === '…' ? '…' : '<code>' + esc(ex) + '</code>'; })
                    .join(' ');
                parts.push(
                    '<div class="vh-item">' +
                        '<span class="vh-what">' + esc(item.what) + '</span>' +
                        '<span class="vh-ex">' + examples + '</span>' +
                    '</div>');
            });
            parts.push('</div>');
        });
        return parts.join('');
    }

    let voiceHelpLoaded = false;
    function openVoiceHelp() {
        const dlg  = document.getElementById('voiceHelpDialog');
        const body = document.getElementById('voiceHelpBody');
        if (!dlg) return;
        if (typeof dlg.show === 'function') dlg.show(); else dlg.setAttribute('open', '');

        // Refetch each open so customised phrases stay current.
        if (body) body.innerHTML = '<div class="text-muted small">Loading…</div>';
        fetch('/api/voice/help')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (doc) {
                if (!body) return;
                body.innerHTML = doc
                    ? renderVoiceHelp(doc)
                    : '<div class="text-danger small">Couldn’t load the command list.</div>';
                voiceHelpLoaded = true;
            })
            .catch(function () {
                if (body && !voiceHelpLoaded)
                    body.innerHTML = '<div class="text-danger small">Couldn’t load the command list.</div>';
            });
    }

    function createVfoVoiceControl(vfo, defaultStepHz) {
        const suffix    = vfo; // 'A' | 'B'
        const container = document.getElementById('voiceMicContainer' + suffix);
        const btn        = document.getElementById('voiceMicBtn' + suffix);
        const stepSelect = document.getElementById('voiceNudgeStepSelect' + suffix);
        const lastHeardEl = document.getElementById('voiceLastHeard' + suffix);
        if (!btn || !container) return null;

        container.style.display = '';

        if (stepSelect) {
            stepSelect.value = String(defaultStepHz || 10000);
            stepSelect.addEventListener('change', async function () {
                const stepHz = parseInt(this.value, 10);
                try {
                    await fetch('/api/voice/nudge-step', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ stepHz, vfo }),
                    });
                } catch (e) {
                    console.error('[voice] nudge-step update failed (VFO ' + vfo + ')', e);
                }
            });
        }

        // ---- Visual state --------------------------------------------

        // Only the colour variant is swapped; the button's layout classes
        // (voice-mic-btn / ywc sizing) and its fixed size stay put.
        // Rebuilding className wholesale used to drop those and reflow the icon.
        const isYwc = btn.classList.contains('ywc-btn');
        const COLOUR_CLASSES = isYwc
            ? ['ywc-btn-outline', 'ywc-btn-danger', 'ywc-btn-success', 'ywc-btn-warning',
               'btn-outline-danger', 'btn-danger', 'btn-success', 'btn-warning']
            : ['btn-outline-danger', 'btn-danger', 'btn-success', 'btn-warning'];
        function setButtonClass(styleSuffix) {
            btn.classList.remove(...COLOUR_CLASSES);
            if (isYwc) {
                if (styleSuffix === 'outline-danger') {
                    btn.classList.add('ywc-btn-outline', 'ywc-btn-danger');
                } else {
                    btn.classList.add('ywc-btn-' + styleSuffix);
                }
                return;
            }
            btn.classList.add('btn-' + styleSuffix);
        }
        // Only setIdleVisual clears the listening class: the pulsing red is
        // held until release (Idle), which is the deliberate proof-of-release
        // cue. Heard/Executing broadcasts arriving after release then show
        // through as green; the .voice-mic-listening !important rule keeps them
        // red while the button is still held.
        function setIdleVisual()        { btn.classList.remove('voice-mic-listening'); setButtonClass('outline-danger'); }
        function setListeningVisual()   { setButtonClass('danger'); btn.classList.add('voice-mic-listening'); }
        function setHeardVisual()       { setButtonClass('success'); }
        function setExecutingVisual()   { setButtonClass('success'); }
        function setErrorVisual()       { setButtonClass('danger'); }
        function setUnrecognisedVisual(){ setButtonClass('warning'); }

        function setLastHeard(text, intent) {
            if (!lastHeardEl) return;
            if (!text) { lastHeardEl.textContent = ''; return; }
            lastHeardEl.textContent = intent
                ? '“' + text + '” → ' + intent
                : '“' + text + '”';
        }

        // ---- PTT plumbing ----------------------------------------------

        let pressed = false;

        async function startListening() {
            if (pressed) return;
            pressed = true;
            setListeningVisual();
            try {
                await fetch('/api/voice/start?vfo=' + encodeURIComponent(vfo), { method: 'POST' });
            } catch (e) {
                console.error('[voice] start failed (VFO ' + vfo + ')', e);
                setErrorVisual();
                pressed = false;
            }
        }

        async function stopListening() {
            if (!pressed) return;
            pressed = false;
            // Reset the visual locally as the user releases the button rather
            // than waiting for SignalR's Idle broadcast, which may not reach
            // this client (connection blip, race with the .on() handler
            // attaching, etc.) and would otherwise leave the button lit.
            setIdleVisual();
            try {
                await fetch('/api/voice/stop', { method: 'POST' });
            } catch (e) {
                console.error('[voice] stop failed (VFO ' + vfo + ')', e);
            }
        }

        // Only the primary (left) button starts PTT. mousedown fires for the
        // right button too, so without this guard a right-click would key the
        // recogniser as well as open the help popup below.
        btn.addEventListener('mousedown', function (e) { if (e.button === 0) startListening(); });
        btn.addEventListener('touchstart', function (e) { e.preventDefault(); startListening(); });

        // Right-click (desktop only — long-press is already PTT on touch) pops
        // up the generated-from-config voice command list.
        btn.addEventListener('contextmenu', function (e) {
            e.preventDefault();
            openVoiceHelp();
        });

        // mouseup/touchend is listened on document (not just the button) so
        // that dragging the pointer off the button while held still stops
        // listening on release rather than sticking in the Listening state.
        document.addEventListener('mouseup', function () { if (pressed) stopListening(); });
        document.addEventListener('touchend', function () { if (pressed) stopListening(); });
        document.addEventListener('touchcancel', function () { if (pressed) stopListening(); });

        function handleStatusUpdate(status) {
            // status: { state, lastHeard, lastIntent, errorMessage, vfo }
            // Ignore broadcasts for the other VFO's mic button.
            if (status.vfo && status.vfo !== vfo) return;

            switch (status.state) {
                case 'Idle':         setIdleVisual();        break;
                case 'Listening':    setListeningVisual();   break;
                case 'Heard':        setHeardVisual();       break;
                case 'Executing':    setExecutingVisual();   break;
                case 'Unrecognised': setUnrecognisedVisual();break;
                case 'Error':        setErrorVisual();       break;
                default:             /* leave as-is */       break;
            }
            if (status.lastHeard) {
                setLastHeard(status.lastHeard, status.lastIntent);
            }
            if (status.state === 'Error' && status.errorMessage) {
                btn.title = 'Voice error: ' + status.errorMessage;
            } else if (status.state === 'Listening') {
                btn.title = 'Listening… release to stop';
            } else {
                btn.title = 'Hold to give a VFO ' + vfo + ' voice command';
            }
        }

        return { vfo, stepSelect, handleStatusUpdate };
    }

    const controls = [
        createVfoVoiceControl('A', window.ywcVoiceNudgeStepHzA),
        createVfoVoiceControl('B', window.ywcVoiceNudgeStepHzB),
    ].filter(Boolean);

    // VFO B's mic button exists in the DOM even on single-receiver radios
    // (it's rendered unconditionally, like the rest of the VFO B controls)
    // but #vfoRow hides the whole VFO B panel via CSS in that case, so no
    // extra gating is needed here.

    if (controls.length === 0) return;

    // ---- SignalR status + nudge-step sync ---------------------------------

    // The site.js module owns the SignalR connection lifecycle. It exposes
    // window.signalRConnection (or similar) once connected. To avoid
    // coupling, we just listen for events on whichever connection appears on
    // window.
    function tryAttachSignalR() {
        const conn = window.signalRConnection || window.connection;
        if (!conn || typeof conn.on !== 'function') {
            setTimeout(tryAttachSignalR, 250);
            return;
        }
        conn.on('VoiceStatusUpdate', function (status) {
            controls.forEach(function (c) { c.handleStatusUpdate(status); });
        });
        conn.on('RadioStateUpdate', function (update) {
            // IntentDispatcher broadcasts VoiceNudgeStepHzA/B when a "set
            // step" voice command changes it, so the dropdown stays in sync
            // even though the change didn't come from this client.
            controls.forEach(function (c) {
                if (update.property === 'VoiceNudgeStepHz' + c.vfo && c.stepSelect) {
                    c.stepSelect.value = String(update.value);
                }
            });
        });
    }
    tryAttachSignalR();

    // Pull initial status once so each button reflects server state on page
    // load (e.g. Error if the configured SAPI locale is missing).
    fetch('/api/voice/status').then(r => r.ok ? r.json() : null).then(s => {
        if (s) controls.forEach(function (c) { c.handleStatusUpdate(s); });
    }).catch(() => {});
})();
