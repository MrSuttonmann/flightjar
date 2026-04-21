import { test } from 'node:test';
import assert from 'node:assert/strict';

import { haversineKm, flightProgress } from '../../app/static/geo.js';

test('haversineKm returns zero for identical points', () => {
  assert.equal(haversineKm(51.47, -0.45, 51.47, -0.45), 0);
});

test('haversineKm matches known EGLL -> KJFK distance (~5540 km)', () => {
  // EGLL ~ (51.47, -0.45), KJFK ~ (40.64, -73.78).
  const d = haversineKm(51.47, -0.45, 40.64, -73.78);
  assert.ok(d > 5500 && d < 5600, `expected ~5540km, got ${d}`);
});

test('haversineKm is symmetric', () => {
  const ab = haversineKm(52.0, -1.0, 40.0, -70.0);
  const ba = haversineKm(40.0, -70.0, 52.0, -1.0);
  assert.equal(ab, ba);
});

test('flightProgress returns null when any coord is missing', () => {
  assert.equal(flightProgress(null, 0, 0, 0, 0, 0, 450), null);
  assert.equal(flightProgress(0, null, 0, 0, 0, 0, 450), null);
  assert.equal(flightProgress(0, 0, null, 0, 0, 0, 450), null);
  assert.equal(flightProgress(0, 0, 0, null, 0, 0, 450), null);
  assert.equal(flightProgress(0, 0, 0, 0, null, 0, 450), null);
  assert.equal(flightProgress(0, 0, 0, 0, 0, null, 450), null);
});

test('flightProgress returns null below the speed threshold', () => {
  // Taxiing / stopped aircraft shouldn't claim an ETA.
  assert.equal(flightProgress(51.47, -0.45, 40.64, -73.78, 50.0, -30.0, null), null);
  assert.equal(flightProgress(51.47, -0.45, 40.64, -73.78, 50.0, -30.0, 0), null);
  assert.equal(flightProgress(51.47, -0.45, 40.64, -73.78, 50.0, -30.0, 40), null);
  // Right at the default threshold (50 kt) still excluded — must be > 50.
  assert.equal(flightProgress(51.47, -0.45, 40.64, -73.78, 50.0, -30.0, 50), null);
});

test('flightProgress at origin yields ~0% progress', () => {
  const p = flightProgress(51.47, -0.45, 40.64, -73.78, 51.47, -0.45, 480);
  assert.ok(p);
  assert.ok(p.pct < 0.01);
  // Remaining time: 5540 / (480 * 1.852) * 60 ~= 374 min.
  assert.ok(p.etaMinutes > 360 && p.etaMinutes < 400);
});

test('flightProgress at destination yields ~100% progress and ~0 ETA', () => {
  const p = flightProgress(51.47, -0.45, 40.64, -73.78, 40.64, -73.78, 480);
  assert.ok(p);
  assert.ok(p.pct > 0.99);
  assert.equal(p.etaMinutes, 0);
});

test('flightProgress clamps pct into [0, 1] when off the great circle', () => {
  // Current position well past the destination.
  const p = flightProgress(51.47, -0.45, 40.64, -73.78, 30.0, -90.0, 480);
  assert.ok(p);
  assert.ok(p.pct <= 1 && p.pct >= 0);
});

test('flightProgress scales ETA with groundspeed', () => {
  const fast = flightProgress(51.47, -0.45, 40.64, -73.78, 51.47, -0.45, 480);
  const slow = flightProgress(51.47, -0.45, 40.64, -73.78, 51.47, -0.45, 240);
  // Half the speed -> roughly double the remaining minutes.
  assert.ok(slow.etaMinutes > fast.etaMinutes * 1.8);
});
