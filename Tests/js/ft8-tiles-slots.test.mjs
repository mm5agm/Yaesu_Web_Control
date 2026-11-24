// FT8 Tiles (issue #332): the Sort tile, the status line and the last QSO.
// Run from the repo root:  node --test "Tests/js/*.test.mjs"

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { TileSlots, sortCalls, txStatus, LastQso } from '../../wwwroot/js/ft8-tiles/ft8-tiles-slots.js';
const st = [
  { call: "K1ABC", kind: "cq", snr: -18, ageSeconds: 10 },
  { call: "DL2XY", kind: "cq", snr: -3, ageSeconds: 2 },
  { call: "G4AAA", kind: "you", snr: -20, ageSeconds: 14 },
  { call: "AA1B", kind: "finished", snr: -9, ageSeconds: 5 },
];
test("sorts keep callers-to-you first", () => {
  assert.deepEqual(sortCalls(st, "strength"), ["G4AAA", "DL2XY", "AA1B", "K1ABC"]);
  assert.deepEqual(sortCalls(st, "newest"), ["G4AAA", "DL2XY", "AA1B", "K1ABC"]);
  assert.deepEqual(sortCalls(st, "callsign"), ["G4AAA", "AA1B", "DL2XY", "K1ABC"]);
  assert.deepEqual(sortCalls(st, "heard"), ["G4AAA", "K1ABC", "DL2XY", "AA1B"]);
});
test("arrange once, then stations keep their tiles", () => {
  const t = new TileSlots();
  t.update(st.map(s => s.call));
  t.arrange(sortCalls(st, "callsign"));
  assert.deepEqual(t.slots, ["G4AAA", "AA1B", "DL2XY", "K1ABC"]);
  t.update(["K1ABC", "AA1B", "NEW1", "G4AAA"]);   // DL2XY gone, NEW1 takes its tile
  assert.deepEqual(t.slots, ["G4AAA", "AA1B", "NEW1", "K1ABC"]);
});
test("status lines", () => {
  assert.equal(txStatus({ connected: true, transmitting: true, txEnabled: true, dxCall: "K1ABC", txMessage: "K1ABC MM5AGM R-10" }).line,
    "Transmitting to K1ABC · sending K1ABC MM5AGM R-10");
  assert.equal(txStatus({ connected: true, transmitting: false, txEnabled: false, dxCall: "", txMessage: "CQ MM5AGM IO85" }).line,
    "Not transmitting · last sent CQ MM5AGM IO85");
  assert.equal(txStatus({ connected: true, transmitting: false, txEnabled: true, dxCall: "", txMessage: "CQ MM5AGM IO85" }).state, "Waiting to transmit");
  assert.equal(txStatus({ connected: false }).state, "WSJT-Z not connected");
});
test("last QSO: logged, then a later unlogged one isn't overwritten", () => {
  const q = new LastQso();
  const A = { call: "K1ABC", frequency: 14074000, mode: "FT8", sent: "-10", received: "-12" };
  q.update({ dxCall: "K1ABC" });
  assert.equal(q.text(), "");
  q.update({ dxCall: "K1ABC", logged: A });
  assert.equal(q.text(), "Last QSO: K1ABC · logged ✓");
  q.update({ dxCall: "DL2XY", logged: A });
  assert.equal(q.text(), "Last QSO: K1ABC · logged ✓");
  q.update({ dxCall: "", logged: A });
  assert.equal(q.text(), "Last QSO: DL2XY · not logged");
  q.update({ dxCall: "", logged: A });
  assert.equal(q.text(), "Last QSO: DL2XY · not logged");
});
test("working a logged station again is a new QSO", () => {
  const q = new LastQso();
  const A = { call: "K1ABC", frequency: 1, mode: "FT8", sent: "", received: "" };
  q.update({ dxCall: "K1ABC", logged: A });
  q.update({ dxCall: "" });
  q.update({ dxCall: "K1ABC" });
  q.update({ dxCall: "" });
  assert.equal(q.text(), "Last QSO: K1ABC · not logged");
});
