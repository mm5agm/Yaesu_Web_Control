// node --test tests/js/spot-data.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
    gridToLatLon, distanceKm, bandOf, bandRank, topicCall, topicsFor, isCallsign,
    classifyMqtt, fromReceptionReport, SpotStore, listRows, utcHhmm, formatDistance,
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
