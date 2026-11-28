// FT8 Tiles: Call CQ's count and the ALC/SWR line (Mike 2E0UMF, 2026-11-28).
// Run from the repo root:  node --test "Tests/js/*.test.mjs"

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cqTileText, txLevelsText } from '../../wwwroot/js/ft8-tiles/ft8-tiles-slots.js';

test("Call CQ counts up to the limit", () => {
  assert.deepEqual(cqTileText(true, false, { active: true, sent: 3, limit: 6 }),
    { big: "Calling CQ 3 of 6", small: "Stop transmit ends it" });
  assert.equal(cqTileText(true, false, { active: true, sent: 0, limit: 6 }).big, "Calling CQ 1 of 6");
  assert.equal(cqTileText(true, false, { active: true, sent: 9, limit: 0 }).big, "Calling CQ");
});
test("Call CQ says the limit at rest, and that it stopped", () => {
  assert.equal(cqTileText(false, false, { limit: 6 }).small, "stops after 6 if no answer");
  assert.equal(cqTileText(false, false, { limit: 0 }).small, "until someone answers");
  assert.equal(cqTileText(false, false, { limit: 6, stoppedSecondsAgo: 30 }).small, "stopped after 6, no answer");
  assert.equal(cqTileText(false, false, { limit: 6, stoppedSecondsAgo: 300 }).small, "stops after 6 if no answer");
  assert.equal(cqTileText(false, true, { limit: 6 }).big, "CQ next period");
  assert.equal(cqTileText(false, false, undefined).big, "Call CQ");
});
test("TX levels: now, last, and too high", () => {
  const lim = { alcLimit: 50, swrLimit: 2.5 };
  assert.equal(txLevelsText(null), null);
  assert.equal(txLevelsText({ ...lim, current: null, last: null }), null);
  assert.deepEqual(txLevelsText({ ...lim, current: { alc: 4, swr: 1.2, ageSeconds: 0 }, last: null }),
    { text: "ALC 4% · SWR 1.2:1", high: false });
  assert.deepEqual(txLevelsText({ ...lim, current: null, last: { alc: 0, swr: 1.0, ageSeconds: 20 } }),
    { text: "Last TX: ALC 0% good · SWR 1.0:1", high: false });
  assert.deepEqual(txLevelsText({ ...lim, current: null, last: { alc: 62, swr: 3.1, ageSeconds: 20 } }),
    { text: "Last TX: ALC 62% too high: turn Pwr down in WSJT-Z · SWR 3.1:1 high", high: true });
  assert.equal(txLevelsText({ ...lim, current: null, last: { alc: 0, swr: 1.0, ageSeconds: 700 } }), null);
});
