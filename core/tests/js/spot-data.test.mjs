// node --test tests/js/spot-data.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
    gridToLatLon, distanceKm, bandOf, bandRank, topicCall, topicsFor, isCallsign,
    KINDS, classifyMqtt, fromReceptionReport, SpotStore, listRows, utcHhmm, formatDistance,
    bestDx, utcHhmmss, subsolarPoint, nightPolygon, countryFor, spotLatLon,
} from '../../js/spotmap/spot-data.js';

const near = (a, b, tol) => assert.ok(Math.abs(a - b) <= tol, `${a} not within ${tol} of ${b}`);

test('grid squares: 4 and 6 characters to the square centre', () => {
    const [lat4, lon4] = gridToLatLon('IO85');
    near(lat4, 55.5, 1e-9);
    near(lon4, -3, 1e-9);
    const [lat6, lon6] = gridToLatLon('io85cx');   // lower case is fine
    near(lat6, 55.979, 0.01);
    near(lon6, -3.792, 0.01);
    const [latF, lonF] = gridToLatLon('FN42');
    near(latF, 42.5, 1e-9);
    near(lonF, -71, 1e-9);
});

test('grid squares: rubbish gives null', () => {
    for (const g of ['', null, undefined, 'IO8', 'ZZ99', '1234', 'IOAB']) assert.equal(gridToLatLon(g), null, String(g));
    // A bad 3rd pair falls back to the 4-character centre.
    assert.deepEqual(gridToLatLon('IO85zz'), gridToLatLon('IO85'));
});

test('distance: Edinburgh area to Boston is about 5,000 km', () => {
    const km = distanceKm(gridToLatLon('IO85'), gridToLatLon('FN42'));
    near(km, 5070, 150);
    assert.equal(distanceKm(null, [0, 0]), null);
    assert.equal(distanceKm([10, 10], [10, 10]), 0);
});

test('bands from Hz', () => {
    assert.equal(bandOf(14_074_000), '20m');
    assert.equal(bandOf(7_074_000), '40m');
    assert.equal(bandOf(50_313_000), '6m');
    assert.equal(bandOf(5_357_000), '60m');
    assert.equal(bandOf(144_174_000), '2m');
    assert.equal(bandOf(0), '');
    assert.equal(bandOf(undefined), '');
    assert.equal(bandOf(13_000_000), '13.000 MHz');
    assert.ok(bandRank('160m') < bandRank('20m'));
    assert.ok(bandRank('20m') < bandRank('2m'));
    assert.ok(bandRank('13.000 MHz') > bandRank('70cm'));
});

test('topics: slash becomes a dot, both ends subscribed', () => {
    assert.equal(topicCall('mm5agm/p'), 'MM5AGM.P');
    assert.deepEqual(topicsFor('MM5AGM'), ['pskr/filter/v2/+/+/MM5AGM/#', 'pskr/filter/v2/+/+/+/MM5AGM/#']);
});

test('callsign check', () => {
    assert.ok(isCallsign('MM5AGM'));
    assert.ok(isCallsign('ea8/mm5agm'));
    assert.ok(!isCallsign('MM'));
    assert.ok(!isCallsign('MM5 AGM'));
    assert.ok(!isCallsign(''));
});

test('classify MQTT messages by which end the operator is', () => {
    const m = { sc: 'mm5agm', rc: 'K1ABC', t: 100, t_tx: 95 };
    assert.deepEqual(classifyMqtt(m, 'MM5AGM'), { kind: 'heardMe', raw: { ...m, t: 95 } });
    assert.equal(classifyMqtt({ sc: 'K1ABC', rc: 'MM5AGM', t: 5 }, 'MM5AGM').kind, 'iHeard');
    assert.equal(classifyMqtt({ sc: 'K1ABC', rc: 'G4XYZ' }, 'MM5AGM'), null);
    assert.equal(classifyMqtt(null, 'MM5AGM'), null);
});

test('query API reports map to MQTT field names', () => {
    const r = fromReceptionReport({
        senderCallsign: 'MM5AGM', senderLocator: 'IO85cx', receiverCallsign: 'K1ABC', receiverLocator: 'FN42',
        flowStartSeconds: '1700000000', frequency: '14075123', mode: 'FT8', sNR: '-12',
    });
    assert.deepEqual(r, { sc: 'MM5AGM', sl: 'IO85cx', rc: 'K1ABC', rl: 'FN42', t: '1700000000', f: '14075123', md: 'FT8', rp: '-12' });
});

test('store keeps the latest spot per station and drops repeats', () => {
    const st = new SpotStore();
    const a = st.add('heardMe', { sq: 1, sc: 'MM5AGM', sl: 'IO85cx', rc: 'K1ABC', rl: 'FN42', t: 100, f: 14074000, md: 'FT8', rp: -10 });
    assert.equal(a.call, 'K1ABC');
    assert.equal(a.band, '20m');
    assert.equal(a.snr, -10);
    assert.equal(st.gridFromSpots, 'IO85CX');
    assert.equal(st.add('heardMe', { sq: 1, sc: 'MM5AGM', rc: 'K1ABC', rl: 'FN42', t: 100 }), null, 'same sq');
    assert.equal(st.add('heardMe', { sc: 'MM5AGM', rc: 'K1ABC', rl: 'FN42', t: 50, rp: 3 }), null, 'older');
    st.add('heardMe', { sc: 'MM5AGM', rc: 'K1ABC', rl: 'FN42', t: 200, f: 7074000, rp: '' });
    const held = st.byKind.heardMe.get('K1ABC');
    assert.equal(held.t, 200);
    assert.equal(held.band, '40m');
    assert.equal(held.snr, null, 'empty SNR is null, not 0');
    assert.equal(st.add('iHeard', { sc: 'W1AW', sl: '', rc: 'MM5AGM', t: 300 }), null, 'no grid');
    assert.equal(st.add('nonsense', { sc: 'W1AW' }), null);
});

test('store: within() honours the window and the kinds', () => {
    const st = new SpotStore();
    st.add('heardMe', { sc: 'MM5AGM', rc: 'K1ABC', rl: 'FN42', t: 1000 });
    st.add('iHeard', { sc: 'W1AW', sl: 'FN31', rc: 'MM5AGM', t: 1500 });
    assert.equal(st.within(1600, 600).length, 2);
    assert.equal(st.within(1600, 300).length, 1);
    assert.equal(st.within(1600, 600, ['iHeard'])[0].call, 'W1AW');
    st.clear();
    assert.equal(st.within(1600, 9999).length, 0);
});

test('band counts: stations per band both ways, a station on two bands counts on both', () => {
    const st = new SpotStore();
    const me = 'MM5AGM';
    st.add('heardMe', { sc: me, rc: 'K1ABC', rl: 'FN42', t: 100, f: 14074000 });
    st.add('heardMe', { sc: me, rc: 'K1ABC', rl: 'FN42', t: 200, f: 7074000 });   // moved to 40m
    st.add('heardMe', { sc: me, rc: 'K1ABC', rl: 'FN42', t: 150, f: 14075000 });  // older 20m spot still updates 20m
    st.add('heardMe', { sc: me, rc: 'W1AW', rl: 'FN31', t: 210, f: 14074000 });
    st.add('iHeard', { sc: 'JA1XYZ', sl: 'PM95', rc: me, t: 220, f: 14074000 });
    st.add('iHeard', { sc: 'VK2RT', sl: 'QF56', rc: me, t: 230, f: 3573000 });
    st.add('iHeard', { sc: 'G4OLD', sl: 'IO91', rc: me, t: 10, f: 28074000 });     // outside the window
    st.add('iHeard', { sc: 'NOFREQ', sl: 'IO91', rc: me, t: 230 });                // no band: not counted
    const rows = st.bandCounts(250, 200);
    assert.deepEqual(rows, [
        { band: '80m', heardMe: 0, iHeard: 1 },
        { band: '40m', heardMe: 1, iHeard: 0 },
        { band: '20m', heardMe: 2, iHeard: 1 },
    ]);
    assert.equal(st.byBand.heardMe.get('K1ABC|20m').t, 150);
    assert.equal(st.byKind.heardMe.get('K1ABC').band, '40m', 'the map still shows the latest spot');
    // Picking a band lists everyone heard there, even if they have moved on.
    assert.deepEqual(st.within(250, 200, ['heardMe'], '20m').map(s => s.call).sort(), ['K1ABC', 'W1AW']);
    assert.deepEqual(st.within(250, 200, ['heardMe']).map(s => s.call).sort(), ['K1ABC', 'W1AW']);
    assert.deepEqual(st.within(250, 1000, KINDS, '10m').map(s => s.call), ['G4OLD']);
    st.clear();
    assert.deepEqual(st.bandCounts(250, 200), []);
});

test('band counts: out-of-band frequencies sort after the bands, by frequency', () => {
    const st = new SpotStore();
    st.add('iHeard', { sc: 'A1A', sl: 'IO91', rc: 'MM5AGM', t: 1, f: 4625000 });
    st.add('iHeard', { sc: 'B1B', sl: 'IO91', rc: 'MM5AGM', t: 1, f: 474200 });
    st.add('iHeard', { sc: 'C1C', sl: 'IO91', rc: 'MM5AGM', t: 1, f: 14074000 });
    assert.deepEqual(st.bandCounts(2, 10).map(r => r.band), ['20m', '0.474 MHz', '4.625 MHz']);
});

test('list rows: distance filled in and every sort order', () => {
    const home = gridToLatLon('IO85');
    const spots = [
        { kind: 'heardMe', call: 'K1ABC', grid: 'FN42', t: 100, band: '20m', snr: -10 },
        { kind: 'iHeard', call: 'DL1XX', grid: 'JO62', t: 300, band: '40m', snr: 5 },
        { kind: 'heardMe', call: 'VK2RT', grid: 'QF56', t: 200, band: '20m', snr: null },
    ];
    assert.deepEqual(listRows(spots, home).map(r => r.call), ['DL1XX', 'VK2RT', 'K1ABC']);
    assert.deepEqual(listRows(spots, home, 'time', false).map(r => r.call), ['K1ABC', 'VK2RT', 'DL1XX']);
    assert.deepEqual(listRows(spots, home, 'distance').map(r => r.call), ['VK2RT', 'K1ABC', 'DL1XX']);
    assert.deepEqual(listRows(spots, home, 'snr').map(r => r.call), ['DL1XX', 'K1ABC', 'VK2RT']);
    assert.deepEqual(listRows(spots, home, 'call', false).map(r => r.call), ['DL1XX', 'K1ABC', 'VK2RT']);
    assert.deepEqual(listRows(spots, home, 'band', false).map(r => r.call), ['DL1XX', 'K1ABC', 'VK2RT']);
    assert.ok(listRows(spots, home)[0].km > 1000);
    assert.equal(listRows(spots, null)[0].km, null, 'no home, no distance');
});

test('time and distance formatting', () => {
    assert.equal(utcHhmm(0), '00:00');
    assert.equal(utcHhmm(1700000000), '22:13');
    assert.equal(formatDistance(5070.4), '5,070 km');
    assert.equal(formatDistance(1609.344, true), '1,000 mi');
    assert.equal(formatDistance(null), '');
});

test('best DX: the furthest station each way, in the spots given', () => {
    const home = gridToLatLon('IO85');
    const spots = [
        { kind: 'heardMe', call: 'W1AW', grid: 'FN31', t: 100, band: '20m' },
        { kind: 'heardMe', call: 'VK2XX', grid: 'QF56', t: 90, band: '20m' },
        { kind: 'iHeard', call: 'DL1AA', grid: 'JO62', t: 80, band: '40m' },
        { kind: 'iHeard', call: 'JA1AA', grid: 'PM95', t: 70, band: '20m' },
    ];
    const best = bestDx(spots, home);
    assert.equal(best.heardMe.call, 'VK2XX');
    assert.ok(best.heardMe.km > 16000);
    assert.equal(best.iHeard.call, 'JA1AA');
    assert.deepEqual(bestDx([], home), { heardMe: null, iHeard: null });
    // No home grid yet: no distances, so no best DX.
    assert.deepEqual(bestDx(spots, null), { heardMe: null, iHeard: null });
});

test('clock: seconds to HH:MM:SS UTC', () => {
    assert.equal(utcHhmmss(Date.UTC(2026, 10, 28, 9, 5, 7) / 1000), '09:05:07');
});

test('sun: overhead point at a solstice, an equinox and noon at Greenwich', () => {
    // June solstice 2026-06-21 08:24 UTC: sun over the Tropic of Cancer.
    near(subsolarPoint(Date.UTC(2026, 5, 21, 8, 24))[0], 23.44, 0.05);
    // March equinox 2026-03-20 14:46 UTC: sun over the equator.
    near(subsolarPoint(Date.UTC(2026, 2, 20, 14, 46))[0], 0, 0.05);
    // December solstice: Tropic of Capricorn.
    near(subsolarPoint(Date.UTC(2026, 11, 21, 20, 50))[0], -23.44, 0.05);
    // 12:00 UTC: the sun is near Greenwich, off by the equation of time (under 17 minutes, 4.3 degrees).
    for (const month of [0, 3, 6, 9, 10]) {
        const lon = subsolarPoint(Date.UTC(2026, month, 15, 12, 0))[1];
        assert.ok(Math.abs(lon) < 4.5, `month ${month}: ${lon}`);
    }
    // 2026-11-03 12:00 UTC: the equation of time is +16.4 min, so the sun has passed Greenwich and is 4.1 degrees west.
    near(subsolarPoint(Date.UTC(2026, 10, 3, 12, 0))[1], -4.1, 0.15);
});

test('grey line: every point is 90 degrees from the sun, closed round the dark pole', () => {
    for (const when of [Date.UTC(2026, 10, 28, 15, 0), Date.UTC(2026, 5, 21, 3, 0), Date.UTC(2026, 2, 20, 14, 46)]) {
        const sun = subsolarPoint(when);
        const poly = nightPolygon(when, 5);
        const line = poly.slice(0, -2);
        assert.equal(line.length, 73);
        assert.equal(line[0][1], -180);
        near(line.at(-1)[1], 180, 1e-9);
        for (const p of line) near(distanceKm(sun, p) / (6371 * Math.PI / 180), 90, 0.01);
        // The dark pole is the one away from the sun.
        assert.equal(poly.at(-1)[0], sun[0] > 0 ? -90 : 90);
    }
});

test('countryFor: callsign to country centre', () => {
    assert.equal(countryFor('JY4CH').name, 'Jordan');
    assert.equal(countryFor('MM5AGM').name, 'Scotland');
    assert.equal(countryFor('PC9K').name, 'Netherlands');
    assert.equal(countryFor('KH6XX').name, 'Hawaii');
    assert.equal(countryFor('UA0ABC').name, 'Asiatic Russia');
    const jy = countryFor('jy4ch');
    near(jy.lat, 31.18, 0.01);
    near(jy.lon, 36.42, 0.01);   // east positive, unlike cty.dat
});

test('countryFor: portable calls and rubbish', () => {
    assert.equal(countryFor('EA8/G4ABC').name, 'Canary Islands');
    assert.equal(countryFor('G4ABC/P').name, 'England');
    assert.equal(countryFor('<JY4CH>').name, 'Jordan');
    assert.equal(countryFor('G4ABC/MM'), null);
    assert.equal(countryFor(''), null);
    assert.equal(countryFor(null), null);
});

test('store: an approximate spot goes on, and a real grid replaces it', () => {
    const store = new SpotStore();
    const approx = store.add('iHeard', { sc: 'JY4CH', sl: '', rc: 'MM5AGM', t: 1000, f: 14075000, md: 'FT8', rp: -5, sq: 'a', ll: [31.18, 36.42], approx: 'Jordan' });
    assert.ok(approx);
    assert.equal(approx.grid, '');
    assert.equal(approx.approx, 'Jordan');
    assert.deepEqual(spotLatLon(approx), [31.18, 36.42]);
    assert.equal(store.gridFor('JY4CH'), '');   // an approximate spot is no grid
    // A real grid wins even when a little older.
    const real = store.add('iHeard', { sc: 'JY4CH', sl: 'KM71', rc: 'MM5AGM', t: 900, f: 14075000, md: 'FT8', rp: -5, sq: 'b' });
    assert.ok(real);
    assert.equal(store.byKind.iHeard.get('JY4CH').grid, 'KM71');
    assert.equal(store.gridFor('JY4CH'), 'KM71');
    // And a newer approximate spot doesn't push the real one off.
    assert.equal(store.add('iHeard', { sc: 'JY4CH', sl: '', rc: 'MM5AGM', t: 2000, f: 14075000, sq: 'c', ll: [31, 36], approx: 'Jordan' }), null);
    assert.equal(store.byKind.iHeard.get('JY4CH').grid, 'KM71');
});

test('store: no grid and no position is still turned away', () => {
    const store = new SpotStore();
    assert.equal(store.add('iHeard', { sc: 'JY4CH', sl: '', rc: 'MM5AGM', t: 1000 }), null);
});

test('best DX leaves out approximate spots', () => {
    const home = gridToLatLon('IO85');
    const spots = [
        { kind: 'iHeard', call: 'VK2X', grid: '', ll: [-23.7, 132.33], approx: 'Australia', t: 1, band: '20m', mode: 'FT8', snr: 0 },
        { kind: 'iHeard', call: 'JY4CH', grid: 'KM71', t: 1, band: '20m', mode: 'FT8', snr: 0 },
    ];
    assert.equal(bestDx(spots, home).iHeard.call, 'JY4CH');
    assert.ok(listRows(spots, home).find(r => r.call === 'VK2X').km > 10000);   // the list still shows a distance
});
