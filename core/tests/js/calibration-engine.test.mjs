// First JS tests in the shared core. calibration-engine.js is a pure-function
// ES module — no DOM, no radio, no I/O in its core path — which is why it and
// AdifParser were chosen as Phase 2's first tenants (see the shared-core plan).
//
// The engine imports its per-radio numbers from a sibling `calibration-tables.js`
// that the core deliberately does NOT ship (each app owns its own). So each test
// stages a fresh copy of the engine next to a fixture tables module in a temp
// dir and imports that — which is exactly how each app composes the shared
// engine with its own tables at runtime. Staging fresh per test also isolates
// the engine's module-level `tables` state, so loadFromBackend mutations in one
// test can't leak into another.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"
// (the bare directory form fails on Node 24 under Windows - it tries to load
//  the directory itself as a module.)

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, copyFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const ENGINE_SRC = new URL('../../js/calibration/calibration-engine.js', import.meta.url);

// A small, predictable set of tables. Values are chosen so interpolation and
// clamping give round numbers the assertions can state exactly.
const FIXTURE_TABLES = `export const defaultTables = {
    PWR:    [ { raw: 0, value: 0 }, { raw: 100, value: 50 }, { raw: 200, value: 100 } ],
    SMETER: [ { raw: 0, value: 0 }, { raw: 120, value: 9  }, { raw: 255, value: 60  } ],
    SMETER_LABELS: [
        { raw: 0,   label: 'S0'  },
        { raw: 120, label: 'S9'  },
        { raw: 255, label: '+60' }
    ]
};
`;

// Stage a fresh, isolated engine instance next to the fixture tables.
function freshEngine() {
    const dir = mkdtempSync(join(tmpdir(), 'rwc-caleng-'));
    writeFileSync(join(dir, 'package.json'), '{"type":"module"}');
    writeFileSync(join(dir, 'calibration-tables.js'), FIXTURE_TABLES);
    copyFileSync(ENGINE_SRC, join(dir, 'calibration-engine.js'));
    return import(pathToFileURL(join(dir, 'calibration-engine.js')).href);
}

// ---- calibrateNumeric ----

test('calibrateNumeric returns exact table points', async () => {
    const { calibrateNumeric } = await freshEngine();
    assert.equal(calibrateNumeric('PWR', 0), 0);
    assert.equal(calibrateNumeric('PWR', 100), 50);
    assert.equal(calibrateNumeric('PWR', 200), 100);
});

test('calibrateNumeric interpolates between points', async () => {
    const { calibrateNumeric } = await freshEngine();
    assert.equal(calibrateNumeric('PWR', 50), 25);   // halfway 0..100 -> 0..50
    assert.equal(calibrateNumeric('PWR', 150), 75);  // halfway 100..200 -> 50..100
});

test('calibrateNumeric clamps outside the table range', async () => {
    const { calibrateNumeric } = await freshEngine();
    assert.equal(calibrateNumeric('PWR', -10), 0);
    assert.equal(calibrateNumeric('PWR', 999), 100);
});

test('calibrateNumeric falls back to identity for an unknown meter', async () => {
    const { calibrateNumeric } = await freshEngine();
    assert.equal(calibrateNumeric('NOT_A_METER', 42), 42);
});

// ---- calibrateSMeterLabel (snap to nearest lower-or-equal) ----

test('calibrateSMeterLabel snaps down to the nearest label', async () => {
    const { calibrateSMeterLabel } = await freshEngine();
    assert.equal(calibrateSMeterLabel(0), 'S0');
    assert.equal(calibrateSMeterLabel(119), 'S0');  // just below the S9 point
    assert.equal(calibrateSMeterLabel(120), 'S9');
    assert.equal(calibrateSMeterLabel(255), '+60');
});

// ---- calibrateSMeterForGauge (S-unit -> static gauge needle position) ----

test('calibrateSMeterForGauge maps calibrated S-units onto the static dial', async () => {
    const { calibrateSMeterForGauge } = await freshEngine();
    // raw 0 -> SMETER 0 -> gauge position 0 (bottom of dial).
    assert.equal(calibrateSMeterForGauge(0), 0);
    // raw 120 -> SMETER 9 -> the S9 static tick at 159.375.
    assert.equal(calibrateSMeterForGauge(120), 159.375);
    // raw 255 -> SMETER 60 -> top of dial.
    assert.equal(calibrateSMeterForGauge(255), 255);
});

// ---- loadFromBackend (the only I/O path; fetch is stubbed) ----

test('loadFromBackend returns false and keeps defaults when fetch fails', async () => {
    const { loadFromBackend, calibrateNumeric } = await freshEngine();
    const original = globalThis.fetch;
    globalThis.fetch = async () => { throw new Error('network down'); };
    try {
        assert.equal(await loadFromBackend(), false);
        assert.equal(calibrateNumeric('PWR', 100), 50); // untouched
    } finally {
        globalThis.fetch = original;
    }
});

test('loadFromBackend replaces a table and parses S-unit labels', async () => {
    const { loadFromBackend, calibrateNumeric } = await freshEngine();
    const original = globalThis.fetch;
    // Backend returns S-meter points as S-unit label strings in the "Radio"
    // field; the engine must translate 'S9' -> numeric 9 for the SMETER scale
    // (the parseSUnitValue branch that fixed Jacek SP3L's #29).
    globalThis.fetch = async () => ({
        ok: true,
        json: async () => ({
            'S-Meter': [
                { Raw: 0,   Radio: 'S0' },
                { Raw: 200, Radio: 'S9' }
            ]
        })
    });
    try {
        const ok = await loadFromBackend({ 'S-Meter': ['SMETER'] });
        assert.equal(ok, true);
        // New table: raw 200 -> S-unit 9, and raw 100 interpolates 0..9 -> 4.5.
        assert.equal(calibrateNumeric('SMETER', 200), 9);
        assert.equal(calibrateNumeric('SMETER', 100), 4.5);
    } finally {
        globalThis.fetch = original;
    }
});
