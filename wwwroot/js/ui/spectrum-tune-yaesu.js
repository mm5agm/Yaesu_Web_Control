// spectrum-tune-yaesu.js - where a click on the radio panel's SDR spectrum
// tunes, for the board (radio-panel-yaesu.js) and the pop-out spectrum
// (radio-spectrum-yaesu.js), so a click does the same in both.
//
// Classic's rules (spectrum-panel.js): the band plan may pick the mode
// (unless Settings turns that off), and in RTTY / DATA-L the dial goes the
// tone offset away from the clicked signal, which is the midpoint.

import { autoModeForHz } from '/js/ui/band-plan.js';
import { loadRttySettings, afskMidpointAudioHz } from '/js/rtty/rtty-settings.js';

export const MODE_CAT = { 'LSB': '1', 'USB': '2', 'CW-U': '3', 'FM': '4', 'AM': '5', 'RTTY-L': '6', 'CW-L': '7', 'DATA-U': 'C' };

/**
 * One per page: it remembers the radio's RTTY shift and polarity once asked.
 * askTones(mode) on every mode change; target(hz, mode) for a click at hz with
 * the VFO in mode gives { mode: the band plan's, or null to leave it; to: the dial Hz }.
 */
export function spectrumTuner() {
    const tones = { shiftHz: 170, polarityRev: false, asked: false };
    function askTones(mode) {
        if (tones.asked || !String(mode).startsWith('RTTY')) return;
        tones.asked = true;
        fetch('/api/cat/rtty').then(res => res.ok ? res.json() : null).then(t => {
            if (Number(t?.shiftHz) > 0) tones.shiftHz = Number(t.shiftHz);
            if (typeof t?.polarityRev === 'boolean') tones.polarityRev = t.polarityRev;
        }).catch(() => { tones.asked = false; });
    }
    function toneOffsetHz(mode) {
        if (mode === 'RTTY-L' || mode === 'RTTY-U') return (tones.polarityRev ? -1 : 1) * tones.shiftHz / 2;
        if (mode === 'DATA-L') return afskMidpointAudioHz(loadRttySettings());
        return 0;
    }
    function target(hz, curMode) {
        const auto = autoModeForHz(hz);
        const mode = auto && MODE_CAT[auto] ? auto : null;
        return { mode, to: Math.round(hz + toneOffsetHz(mode || curMode)) };
    }
    return { askTones, target };
}
