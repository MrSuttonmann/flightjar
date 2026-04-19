import { test } from 'node:test';
import assert from 'node:assert/strict';

import { HIST_LEN, TREND_THRESHOLDS, pushHistory, trendInfo } from '../../app/static/trend.js';

function makeEntry() {
  return { hist: { alt: [], spd: [], dst: [] } };
}

test('pushHistory caps each buffer at HIST_LEN', () => {
  const e = makeEntry();
  for (let i = 0; i < HIST_LEN + 5; i++) {
    pushHistory(e, { altitude: i, speed: i, distance_km: i });
  }
  assert.equal(e.hist.alt.length, HIST_LEN);
  assert.equal(e.hist.spd.length, HIST_LEN);
  assert.equal(e.hist.dst.length, HIST_LEN);
  // Oldest sample should have been shifted out.
  assert.equal(e.hist.alt[0], 5);
  assert.equal(e.hist.alt[HIST_LEN - 1], HIST_LEN + 4);
});

test('trendInfo returns empty until the buffer has >= 3 samples', () => {
  const e = makeEntry();
  pushHistory(e, { altitude: 1000, speed: 100, distance_km: 10 });
  pushHistory(e, { altitude: 1100, speed: 110, distance_km: 11 });
  const t = trendInfo(e, 'alt');
  assert.equal(t.dir, '');
  assert.equal(t.cls, '');
  assert.equal(t.arrow, '');
});

test('trendInfo classifies rises with delta > threshold as up', () => {
  const e = makeEntry();
  const delta = TREND_THRESHOLDS.alt + 100;
  for (let i = 0; i < 5; i++) {
    pushHistory(e, { altitude: 30000 + i * delta, speed: null, distance_km: null });
  }
  const t = trendInfo(e, 'alt');
  assert.equal(t.dir, 'up');
  assert.equal(t.cls, 'trend-up');
  assert.match(t.arrow, /↑/);
});

test('trendInfo classifies falls with delta < -threshold as down', () => {
  const e = makeEntry();
  const delta = TREND_THRESHOLDS.spd + 5;
  for (let i = 0; i < 5; i++) {
    pushHistory(e, { altitude: null, speed: 500 - i * delta, distance_km: null });
  }
  const t = trendInfo(e, 'spd');
  assert.equal(t.dir, 'down');
  assert.equal(t.cls, 'trend-down');
  assert.match(t.arrow, /↓/);
});

test('trendInfo classifies tiny wobbles within the dead-zone as flat', () => {
  const e = makeEntry();
  for (let i = 0; i < 5; i++) {
    pushHistory(e, { altitude: null, speed: null, distance_km: 25 + (i % 2) * 0.1 });
  }
  const t = trendInfo(e, 'dst');
  assert.equal(t.dir, 'flat');
  assert.equal(t.cls, '');  // flat carries no class so default colour applies
  assert.match(t.arrow, /—/);
});

test('trendInfo tolerates gaps (null samples) at the ends', () => {
  const e = makeEntry();
  // Sequence: null, null, 1000, 1100, 1200 — should read "up" using 1000→1200.
  const delta = TREND_THRESHOLDS.alt + 1;
  e.hist.alt = [null, null, 10000, 10000 + delta, 10000 + 2 * delta];
  const t = trendInfo(e, 'alt');
  assert.equal(t.dir, 'up');
});
