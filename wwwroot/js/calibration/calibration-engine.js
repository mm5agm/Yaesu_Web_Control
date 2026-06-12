// Yaesu Web Control – Calibration Engine
// Pure functions only. No DOM, no UI, no gauge logic, no side effects.
//
// This is the single source of truth for all meter calibration.
// Usage:
//   import { calibrateNumeric, calibrateSMeterLabel, loadFromBackend } from './calibration-engine.js';
//
// At startup call loadFromBackend() once to replace default tables with
// user-saved calibration data from the server.  All subsequent calls to
// calibrateNumeric / calibrateSMeterLabel will use the loaded data.

import { defaultTables } from './calibration-tables.js?v=10';

// Live tables — initialised from defaults, replaced by loadFromBackend().
// Copied so the imported defaults are never mutated.
const tables = {};
for (const [key, rows] of Object.entries(defaultTables)) {
    tables[key] = rows.map(r => ({ ...r }));
}

// ------------------------------------------------------------
// Internal helpers
// ------------------------------------------------------------

// Linear interpolation between adjacent calibration points.
function interpolate(table, raw) {
    if (raw <= table[0].raw) return table[0].value;
    if (raw >= table[table.length - 1].raw) return table[table.length - 1].value;
    for (let i = 1; i < table.length; i++) {
        if (raw <= table[i].raw) {
            const prev = table[i - 1];
            const next = table[i];
            const t = (raw - prev.raw) / (next.raw - prev.raw);
            return prev.value + t * (next.value - prev.value);
        }
    }
    return table[table.length - 1].value;
}

// Snap to the nearest lower-or-equal raw entry (used for S-meter labels).
function snapLabel(table, raw) {
    let last = table[0];
    for (const pt of table) {
        if (raw < pt.raw) break;
        last = pt;
    }
    return last.label ?? String(last.value ?? last.raw);
}

// ------------------------------------------------------------
// Public API
// ------------------------------------------------------------

/**
 * Calibrate a raw ADC meter reading to a display value.
 * Falls back to the raw value (identity) when no table exists for meterName.
 *
 * @param {string} meterName  Key matching an entry in calibration-tables.js
 *                            e.g. 'PWR', 'SWR', 'ALC', 'IDD', 'VPA', 'TPA'
 * @param {number} raw        Raw 0–255 ADC value from the radio
 * @returns {number}          Calibrated display value
 */
export function calibrateNumeric(meterName, raw) {
    const table = tables[meterName];
    if (!table || table.length === 0) return raw;
    return interpolate(table, raw);
}

/**
 * Return the S-meter label string for a raw S-meter reading.
 *
 * @param {number} raw  Raw 0–255 ADC value
 * @returns {string}    Label such as 'S7', '+10', '+40'
 */
export function calibrateSMeterLabel(raw) {
    return snapLabel(tables.SMETER_LABELS, raw);
}

/**
 * Load backend calibration data and replace the live tables.
 * Safe to call at startup; silently falls back to defaults on any error.
 *
 * The backend returns a dictionary keyed by meter name (e.g. 'S-Meter').
 * backendNameMap translates those names to the table keys used here.
 * A backend entry can target MULTIPLE local tables — needed for S-meter
 * specifically, where one backend table ('S-Meter') drives both the
 * numeric gauge scale ('SMETER') and the snap-to-nearest label set
 * ('SMETER_LABELS'). Without that, calibrating S-meter only updated the
 * snap labels and left the gauge needle position on the hardcoded
 * defaults — Jacek (SP3L) reported the symptom on #29.
 *
 * @param {Object} backendNameMap  e.g. { 'S-Meter': ['SMETER', 'SMETER_LABELS'] }.
 *                                 Value can be a string for single-target or
 *                                 an array for multi-target.
 * @returns {Promise<boolean>}     true on successful load, false on any failure
 */
// The S-meter gauge (SMeterGauge in gauge.js) renders on a fixed 0-255 scale
// with HARDCODED tick positions and label text. It bypasses the calibration
// system for needle placement entirely. So when a user calibrates the S-meter,
// the snap-label display updates but the needle stays put — that's Jacek SP3L's
// #29 symptom and what Colin verified on his bench 2026-06-12.
//
// Fix: translate the radio's raw ADC reading through the user's calibration
// into an S-unit number, then into the gauge's static position for that S-unit.
// Site.js's updateSMeter() calls calibrateSMeterForGauge() instead of passing
// raw directly. The gauge's static labels stay where they are; the needle
// moves to where the user's calibration says it should sit on the dial.
//
// STATIC_SMETER_TICKS maps S-unit values to the gauge's needle-position scale.
// The gauge label overlay (gauge.js:93) places labels at EVENLY-SPACED angles
// (180° / (labelCount-1) per step), NOT at the numerical majorTicks values
// configured on the gauge. So even though majorTicks has [0,4,30,65,...], the
// "S5" label is actually painted at angle index 3 of 8 (= 37.5% of the arc),
// which on the canvas-gauges value scale (0-255 linear) corresponds to
// 255 * 3/8 = 95.625, not 65.
//
// Without this correction, a calibration mapping raw→S5 would put the needle
// at value=65 — which lands the needle at label index 2 = "S3" instead of S5.
// That's the "2 S-units low" symptom Jacek SP3L reported on #29 and Colin
// reproduced on his bench 2026-06-12.
//
// Formula: position = 255 * (labelIndex / (labelCount - 1)) for 9 labels.
const STATIC_SMETER_TICKS = [
    { value: 0,  position:   0       },   // S0  — label idx 0/8
    { value: 1,  position:  31.875   },   // S1  — label idx 1/8
    { value: 3,  position:  63.75    },   // S3  — label idx 2/8
    { value: 5,  position:  95.625   },   // S5  — label idx 3/8
    { value: 7,  position: 127.5     },   // S7  — label idx 4/8
    { value: 9,  position: 159.375   },   // S9  — label idx 5/8
    { value: 20, position: 191.25    },   // +20 — label idx 6/8
    { value: 40, position: 223.125   },   // +40 — label idx 7/8
    { value: 60, position: 255       }    // +60 — label idx 8/8
];

// Linear interpolation from S-unit value to gauge needle position.
function interpolateToGaugePosition(sUnit) {
    if (sUnit <= STATIC_SMETER_TICKS[0].value) return STATIC_SMETER_TICKS[0].position;
    if (sUnit >= STATIC_SMETER_TICKS[STATIC_SMETER_TICKS.length - 1].value)
        return STATIC_SMETER_TICKS[STATIC_SMETER_TICKS.length - 1].position;
    for (let i = 1; i < STATIC_SMETER_TICKS.length; i++) {
        if (sUnit <= STATIC_SMETER_TICKS[i].value) {
            const prev = STATIC_SMETER_TICKS[i - 1];
            const next = STATIC_SMETER_TICKS[i];
            const t = (sUnit - prev.value) / (next.value - prev.value);
            return prev.position + t * (next.position - prev.position);
        }
    }
    return STATIC_SMETER_TICKS[STATIC_SMETER_TICKS.length - 1].position;
}

/**
 * Compute the needle position (0-255) the static S-meter gauge should
 * display for the given raw ADC reading, honoring the user's calibration.
 * @param {number} raw  0-255 ADC value from the radio
 * @returns {number}    gauge needle position (0-255 for the static dial)
 */
export function calibrateSMeterForGauge(raw) {
    const sUnit = calibrateNumeric('SMETER', raw);
    return interpolateToGaugePosition(sUnit);
}

// Parse an S-meter label string into the SMETER numeric scale value.
// The hardcoded defaults in calibration-tables.js use:
//   S0..S9    → 0..9   (one per S-unit)
//   +10..+60  → 10..60 (dB above S9, value matches the dB number directly)
// User calibration files store labels in the same convention. Without this
// translation, Number('S1') is NaN and the numeric SMETER table falls back
// to the raw ADC value as the calibrated output — gauge needle reads raw
// 0-255 instead of S-unit 0-60. Reported by Jacek SP3L on #29 against v2.3.5.
function parseSUnitValue(str) {
    if (str == null) return null;
    const s = String(str).trim();
    const sMatch = s.match(/^S(\d+)$/i);
    if (sMatch) return parseInt(sMatch[1], 10);
    const plusMatch = s.match(/^\+(\d+)$/);
    if (plusMatch) return parseInt(plusMatch[1], 10);
    return null;
}

export async function loadFromBackend(backendNameMap = {}) {
    try {
        const response = await fetch('/api/calibration/all');
        if (!response.ok) return false;
        const data = await response.json();
        for (const [backendName, points] of Object.entries(data)) {
            const mapped = backendNameMap[backendName] ?? backendName;
            const targetKeys = Array.isArray(mapped) ? mapped : [mapped];
            for (const key of targetKeys) {
                if (!(key in tables)) continue;
                tables[key] = points.map(p => {
                    const rawVal = p.Raw ?? p.raw ?? 0;
                    // CalibrationPoint serialises the display value as "Radio" (a numeric string).
                    // Fall back through legacy field names before using raw as identity.
                    const radioStr = p.Radio ?? p.Value ?? p.value ?? p.Label ?? p.label;

                    // For the SMETER numeric scale specifically, translate S-unit
                    // labels ('S0'..'S9', '+10'..'+60') into the numeric values
                    // the gauge expects. The SMETER_LABELS table doesn't need
                    // this — it uses the label string directly via snapLabel().
                    if (key === 'SMETER') {
                        const sUnit = parseSUnitValue(radioStr);
                        if (sUnit !== null) {
                            return { raw: rawVal, value: sUnit, label: radioStr };
                        }
                        // Fall through to default handling if not S-unit-shaped
                        // (e.g. a user typed a raw number like '5' instead of 'S5').
                    }

                    const value = (radioStr !== undefined && radioStr !== null && radioStr !== '')
                        ? Number(radioStr)
                        : rawVal;
                    // Preserve the original string as a label when it can't be parsed as a number
                    // (e.g. S-meter points store 'S1', 'S7', '+20' as their Radio value).
                    const label = (radioStr != null && isNaN(Number(radioStr))) ? radioStr : undefined;
                    return { raw: rawVal, value: isNaN(value) ? rawVal : value, ...(label !== undefined && { label }) };
                });
            }
        }
        return true;
    } catch (e) {
        console.warn('[CalibrationEngine] Backend load failed, using defaults:', e.message);
        return false;
    }
}
