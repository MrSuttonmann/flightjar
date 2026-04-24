import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  bboxContains,
  bboxOfGeometry,
  flightProgress,
  haversineKm,
  pointInGeometry,
  pointInPolygon,
  pointInRing,
  trailDistanceKm,
} from '../../app/static/geo.js';

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

test('trailDistanceKm returns 0 for empty / single-point trails', () => {
  assert.equal(trailDistanceKm([]), 0);
  assert.equal(trailDistanceKm([[51.47, -0.45]]), 0);
  assert.equal(trailDistanceKm(null), 0);
  assert.equal(trailDistanceKm(undefined), 0);
});

test('trailDistanceKm sums consecutive segments', () => {
  // Two 1°-latitude legs from EGLL going due north. ~111 km each ×
  // two = ~222 km.
  const trail = [
    [51.47, -0.45, 0, 0, false],
    [52.47, -0.45, 0, 0, false],
    [53.47, -0.45, 0, 0, false],
  ];
  const d = trailDistanceKm(trail);
  assert.ok(d > 220 && d < 224, `expected ~222 km, got ${d}`);
});

test('trailDistanceKm includes gap-flagged segments', () => {
  // Gap flag signals a signal-lost segment — endpoints are real
  // fixes, so the straight-line distance is still our best
  // estimate of what the plane actually covered.
  const trail = [
    [51.0, -1.0, 0, 0, false],
    [52.0, -1.0, 0, 0, true],  // gap flagged here
  ];
  assert.ok(trailDistanceKm(trail) > 110);
});

test('trailDistanceKm skips entries with missing coords', () => {
  const trail = [
    [51.0, -1.0],
    [null, null],
    [52.0, -1.0],
  ];
  // The null row breaks adjacency — first->null and null->third are
  // both skipped. Result should be 0.
  assert.equal(trailDistanceKm(trail), 0);
});

test('trailDistanceKm ignores trailing metadata in entries', () => {
  // Real snapshot trails carry 5 elements [lat, lon, alt, spd, gap].
  // The helper must not care.
  const trail = [
    [51.47, -0.45, 35000, 450, false],
    [52.47, -0.45, 36000, 460, false],
  ];
  const d = trailDistanceKm(trail);
  assert.ok(d > 110 && d < 112);
});

// ---- point-in-polygon ----

const SQUARE = [[0, 0], [10, 0], [10, 10], [0, 10], [0, 0]];

test('pointInRing: point inside a square is inside', () => {
  assert.equal(pointInRing(5, 5, SQUARE), true);
});

test('pointInRing: points outside a square are outside', () => {
  assert.equal(pointInRing(11, 5, SQUARE), false);
  assert.equal(pointInRing(-1, 5, SQUARE), false);
  assert.equal(pointInRing(5, -1, SQUARE), false);
  assert.equal(pointInRing(5, 11, SQUARE), false);
});

test('pointInRing: degenerate input returns false', () => {
  assert.equal(pointInRing(0, 0, null), false);
  assert.equal(pointInRing(0, 0, []), false);
  assert.equal(pointInRing(0, 0, [[0, 0], [1, 1]]), false);
});

test('pointInRing: concave L-shape — point in the notch is outside', () => {
  // L-shape with the notch in the upper-right quadrant (5..10, 5..10).
  const lShape = [
    [0, 0], [10, 0], [10, 5], [5, 5], [5, 10], [0, 10], [0, 0],
  ];
  // In the bottom arm
  assert.equal(pointInRing(2, 2, lShape), true);
  // In the right arm
  assert.equal(pointInRing(7, 2, lShape), true);
  // In the notch — outside the L
  assert.equal(pointInRing(7, 7, lShape), false);
});

test('pointInPolygon: hole-interior is outside', () => {
  const outer = SQUARE;
  const hole = [[4, 4], [6, 4], [6, 6], [4, 6], [4, 4]];
  // Inside outer, outside hole
  assert.equal(pointInPolygon(2, 2, [outer, hole]), true);
  assert.equal(pointInPolygon(7, 7, [outer, hole]), true);
  // Inside the hole — should be classified as outside the polygon
  assert.equal(pointInPolygon(5, 5, [outer, hole]), false);
  // Outside the outer ring entirely
  assert.equal(pointInPolygon(20, 20, [outer, hole]), false);
});

test('pointInPolygon: empty / null rings returns false', () => {
  assert.equal(pointInPolygon(0, 0, null), false);
  assert.equal(pointInPolygon(0, 0, []), false);
});

test('pointInGeometry: Polygon dispatch', () => {
  const geom = { type: 'Polygon', coordinates: [SQUARE] };
  assert.equal(pointInGeometry(5, 5, geom), true);
  assert.equal(pointInGeometry(20, 20, geom), false);
});

test('pointInGeometry: MultiPolygon — inside either component', () => {
  const left = [[[0, 0], [4, 0], [4, 4], [0, 4], [0, 0]]];
  const right = [[[10, 0], [14, 0], [14, 4], [10, 4], [10, 0]]];
  const geom = { type: 'MultiPolygon', coordinates: [left, right] };
  assert.equal(pointInGeometry(2, 2, geom), true);
  assert.equal(pointInGeometry(12, 2, geom), true);
  // In the gap between the two squares
  assert.equal(pointInGeometry(7, 2, geom), false);
});

test('pointInGeometry: returns false for unsupported / null geometries', () => {
  assert.equal(pointInGeometry(0, 0, null), false);
  assert.equal(pointInGeometry(0, 0, undefined), false);
  assert.equal(pointInGeometry(0, 0, {}), false);
  assert.equal(pointInGeometry(0, 0, { type: 'LineString', coordinates: [[0, 0], [1, 1]] }), false);
  assert.equal(pointInGeometry(0, 0, { type: 'Point', coordinates: [0, 0] }), false);
});

test('bboxOfGeometry: Polygon returns [minLon, minLat, maxLon, maxLat]', () => {
  const geom = { type: 'Polygon', coordinates: [SQUARE] };
  assert.deepEqual(bboxOfGeometry(geom), [0, 0, 10, 10]);
});

test('bboxOfGeometry: MultiPolygon spans every component', () => {
  const left = [[[0, 0], [4, 0], [4, 4], [0, 4], [0, 0]]];
  const right = [[[10, 1], [14, 1], [14, 5], [10, 5], [10, 1]]];
  const geom = { type: 'MultiPolygon', coordinates: [left, right] };
  assert.deepEqual(bboxOfGeometry(geom), [0, 0, 14, 5]);
});

test('bboxOfGeometry: ignores hole rings (they cannot extend the outer bbox)', () => {
  const outer = [[-2, -2], [12, -2], [12, 12], [-2, 12], [-2, -2]];
  const hole = [[0, 0], [4, 0], [4, 4], [0, 4], [0, 0]];
  const geom = { type: 'Polygon', coordinates: [outer, hole] };
  assert.deepEqual(bboxOfGeometry(geom), [-2, -2, 12, 12]);
});

test('bboxOfGeometry: unsupported / null geometries return null', () => {
  assert.equal(bboxOfGeometry(null), null);
  assert.equal(bboxOfGeometry({}), null);
  assert.equal(bboxOfGeometry({ type: 'LineString', coordinates: [] }), null);
  assert.equal(bboxOfGeometry({ type: 'Polygon', coordinates: [] }), null);
});

test('bboxContains: boundary is inclusive', () => {
  const bb = [0, 0, 10, 10];
  assert.equal(bboxContains(bb, 0, 0), true);
  assert.equal(bboxContains(bb, 10, 10), true);
  assert.equal(bboxContains(bb, 5, 5), true);
  assert.equal(bboxContains(bb, -0.001, 5), false);
  assert.equal(bboxContains(bb, 5, 10.001), false);
  assert.equal(bboxContains(null, 0, 0), false);
});
