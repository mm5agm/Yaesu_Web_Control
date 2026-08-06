// if-width-tables.js — per-mode bandwidth tables for IF Width (SH command).
//
// The SH command takes the same code for all modes on each radio, but the
// resulting bandwidth differs by mode. In SSB code 8 = 1650 Hz; in CW the
// same code 8 = 400 Hz. This module provides the mode-aware lookup used by
// both the dropdown rebuild logic in site.js and the Filter Function Display
// in filter-scope-panel.js.
//
// Data is sourced from Table 3 of each radio's official CAT manual.

// Mode group: classifies each operating mode into a bandwidth table column.
//   'ssb' — wide SSB / DATA-SSB widths
//   'cw'  — narrow CW / RTTY / PSK widths
//   null  — AM, FM, DATA-FM and variants: IF Width dropdown does not apply
//
// DATA-L / DATA-U use the SSB column because the typical use case is FT8/FT4
// which wants the full SSB passband. (The FT-710 manual lists them under the
// CW column but the practical operator preference is SSB.)
const MODE_GROUP = {
    'LSB': 'ssb', 'USB': 'ssb',
    'DATA-L': 'ssb', 'DATA-U': 'ssb',
    'CW-U': 'cw', 'CW-L': 'cw',
    'RTTY-L': 'cw', 'RTTY-U': 'cw',
    'PSK': 'cw',
    'AM': null, 'AM-N': null,
    'FM': null, 'FM-N': null,
    'DATA-FM': null, 'DATA-FM-N': null,
};

function modeGroup(mode) {
    if (mode == null) return 'ssb';
    return MODE_GROUP.hasOwnProperty(mode) ? MODE_GROUP[mode] : 'ssb';
}

// Per-radio bandwidth tables. Each value is the bandwidth in Hz at that SH code.
// Code 0 is the radio's mode-dependent default; rendered as "Default".
const TABLES = {
    'FTdx101MP': {
        ssb: {
            // Codes 0-21 per the 2308-L CAT manual; codes 22 and 23 verified
            // empirically on Colin's FTdx101MP (issue #50) — current firmware
            // extends the SSB filter set to 3.5 kHz and 4 kHz.
            0: 'default',
            1: 300, 2: 400, 3: 600, 4: 850, 5: 1100, 6: 1200, 7: 1500, 8: 1650,
            9: 1800, 10: 1950, 11: 2100, 12: 2200, 13: 2300, 14: 2400, 15: 2500,
            16: 2600, 17: 2700, 18: 2800, 19: 2900, 20: 3000, 21: 3200,
            22: 3500, 23: 4000
        },
        cw: {
            // Codes 0-18 per the 2308-L CAT manual; codes 19/20/21 verified
            // empirically on Colin's FTdx101MP (issue #50) — current firmware
            // extends the CW filter set to 3.2 / 3.5 / 4.0 kHz. Same kHz
            // values as SSB codes 21/22/23 but at different code numbers
            // (the SH command is mode-aware, not a flat code→Hz lookup).
            0: 'default',
            1: 50, 2: 100, 3: 150, 4: 200, 5: 250, 6: 300, 7: 350, 8: 400,
            9: 450, 10: 500, 11: 600, 12: 800, 13: 1200, 14: 1400, 15: 1700,
            16: 2000, 17: 2400, 18: 3000,
            19: 3200, 20: 3500, 21: 4000
        }
    },
    'FTdx10': {
        ssb: {
            0: 'default',
            1: 300, 2: 400, 3: 600, 4: 850, 5: 1100, 6: 1200, 7: 1500, 8: 1650,
            9: 1800, 10: 1950, 11: 2100, 12: 2250, 13: 2400, 14: 2450, 15: 2500,
            16: 2600, 17: 2700, 18: 2800, 19: 2900, 20: 3000, 21: 3200, 22: 3500, 23: 4000
        },
        cw: {
            0: 'default',
            1: 50, 2: 100, 3: 150, 4: 200, 5: 250, 6: 300, 7: 350, 8: 400,
            9: 450, 10: 500, 11: 600, 12: 800, 13: 1200, 14: 1400, 15: 1700,
            16: 2000, 17: 2400, 18: 3000, 19: 3200, 20: 3500, 21: 4000
        }
    },
    'FT-710': {
        // Codes are non-contiguous on the FT-710 — only specific codes are
        // exposed in the dropdown rather than the full 0-23 range. The numeric
        // codes match the radio's SH command codes per the CAT manual.
        ssb: {
            0: 'default',
            1: 300, 3: 850, 5: 1100, 7: 1500, 9: 1800,
            12: 2250, 16: 2600, 19: 2900, 20: 3200, 21: 3500, 22: 4000
        },
        cw: {
            0: 'default',
            1: 50, 3: 150, 5: 250, 7: 350, 9: 450,
            12: 800, 16: 2000, 19: 3200, 20: 3500, 21: 4000
        }
    },
    'FTDX3000': {
        // FTDX3000 uses Wide bandwidths only (Narrow has fewer steps). Codes
        // are non-contiguous.
        ssb: {
            1: 200, 2: 400, 3: 600, 4: 850, 6: 1350, 7: 1500, 9: 1800,
            12: 2200, 14: 2400, 16: 2600, 18: 2800, 20: 3000, 22: 3400, 25: 4000
        },
        cw: {
            // FTDX3000 CW Wide: codes 10-16
            10: 500, 11: 800, 12: 1200, 13: 1400, 14: 1700, 15: 2000, 16: 2400
        }
    },
};
// FTdx101D shares the FTdx101MP tables.
TABLES['FTdx101D'] = TABLES['FTdx101MP'];

function hzLabel(hz) {
    if (hz === 'default') return 'Default';
    if (hz >= 1000) {
        const k = hz / 1000;
        return Number.isInteger(k) ? `${k}.0 kHz` : `${k} kHz`;
    }
    return `${hz} Hz`;
}

// Returns the bandwidth in Hz for (model, mode, code), or null if the
// dropdown does not apply (AM/FM modes).
function ifWidthHzFor(model, mode, code) {
    const group = modeGroup(mode);
    if (!group) return null;
    const table = TABLES[model]?.[group];
    if (!table) return null;
    const hz = table[code];
    return (hz == null || hz === 'default') ? null : hz;
}

// Returns an array of { code, label, hz } for the given model+mode, or null
// if the dropdown should be hidden (AM/FM modes).
function ifWidthOptionsFor(model, mode) {
    const group = modeGroup(mode);
    if (!group) return null;
    const table = TABLES[model]?.[group];
    if (!table) return null;
    return Object.entries(table)
        .map(([code, hz]) => ({ code: String(code), label: hzLabel(hz), hz }))
        .sort((a, b) => parseInt(a.code) - parseInt(b.code));
}

// Rebuild a <select> element with the options for the current mode.
// Preserves the currently selected code if it still exists in the new options.
function rebuildIfWidthSelect(selectEl, model, mode) {
    if (!selectEl) return;
    const options = ifWidthOptionsFor(model, mode);
    // Hide the entire row when the dropdown does not apply (AM/FM modes).
    // The row contains both the label and the select — walk up to find it.
    const row = selectEl.closest('.d-flex, .u2-slider-row');
    if (!options) {
        if (row) row.style.display = 'none';
        return;
    }
    if (row) row.style.display = '';

    const previousCode = selectEl.value;
    selectEl.innerHTML = '';
    for (const opt of options) {
        const optEl = document.createElement('option');
        optEl.value = opt.code;
        optEl.textContent = opt.label;
        selectEl.appendChild(optEl);
    }
    // Preserve the selected code if still valid.
    if (Array.from(selectEl.options).some(o => o.value === previousCode)) {
        selectEl.value = previousCode;
    }
}

// Expose to non-module code (site.js loads as a regular script).
window.IfWidth = {
    modeGroup,
    ifWidthHzFor,
    ifWidthOptionsFor,
    rebuildIfWidthSelect,
};

export { modeGroup, ifWidthHzFor, ifWidthOptionsFor, rebuildIfWidthSelect };
