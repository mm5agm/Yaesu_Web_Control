// Radio Web Control - pause a mode-specific pop-out outside its modes
// Shared by Icom Web Control and Yaesu Web Control. This file is copied into
// each app's wwwroot at build time - see js/README.md. Edit it here, in the
// core, never in a wwwroot copy.
//
// The main page closes its CW reader or RTTY tuner once the mode has settled
// somewhere the panel is no use (ModePanelGuard). A window on a second monitor
// closing itself would be worse than a dialog doing it, so a pop-out pauses
// instead and picks up again the moment the radio is back in one of its
// modes. The mode names are the radio's own, so the app says which belong.
//
// The panel needs only setPaused(paused, text) and isPaused; startNow() is
// whatever begins it (startPolling() for the reader, start() for the tuner).

import { ModePanelGuard } from '../modes/mode-panel-guard.js';

/**
 * @param {object} opts
 * @param {string} opts.name        spoken name, e.g. "CW reader"
 * @param {{ setPaused(paused: boolean, text?: string): void, isPaused: boolean }} opts.panel
 * @param {(mode: string) => boolean} opts.belongs
 * @param {(mode: string) => string} opts.pausedText  what the paused panel says
 * @param {() => void} opts.startNow
 * @param {string} [opts.initialMode]  the mode the server rendered the page in
 * @param {HTMLElement|null} [opts.announceEl]  an aria-live region
 * @param {string} [opts.resumedText]  spoken on resume; default "<name> running again"
 * @returns {{ setMode(mode: string): void }}  feed it every mode report
 */
export function startModePausedPopout({
    name, panel, belongs, pausedText, startNow,
    initialMode = '', announceEl = null, resumedText,
}) {
    let mode = initialMode || '';
    const announce = msg => {
        if (!announceEl) return;
        announceEl.textContent = '';
        requestAnimationFrame(() => { announceEl.textContent = msg; });
    };
    const textFor = () => pausedText(mode || 'another mode');

    const guard = new ModePanelGuard({
        name,
        belongs,
        isOpen: () => !panel.isPaused,
        close: () => {
            panel.setPaused(true, textFor());
            announce(`${name} paused, mode is now ${mode}`);
        },
        announce: () => { /* close() has said it already */ },
    });

    // What the radio was in when the window opened is known for certain, so
    // there is nothing to wait out: a window opened in the wrong mode starts
    // paused rather than running for the settle time first.
    if (mode && !belongs(mode)) panel.setPaused(true, textFor());
    else startNow();

    return {
        setMode(m) {
            if (typeof m !== 'string' || !m) return;
            mode = m;
            guard.setMode(m);
            if (belongs(m) && panel.isPaused) {
                panel.setPaused(false);
                announce(resumedText ?? `${name} running again`);
            }
        },
    };
}
